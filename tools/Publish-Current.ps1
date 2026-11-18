#requires -Version 7.6.10
#requires -PSEdition Core
param(
    [string]$PackagePath,
    [string]$ModulePackagePath
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = $PSScriptRoot
while (!(Test-Path -LiteralPath (Join-Path $workspaceRoot 'RULES.md') -PathType Leaf)) {
    $parent = Split-Path -Parent $workspaceRoot
    if (!$parent -or $parent -eq $workspaceRoot) { throw 'Project RULES.md not found above worktree.' }
    $workspaceRoot = $parent
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRoot = Split-Path -Parent $projectRoot
if ((Split-Path -Parent $taskRoot) -ne (Join-Path $workspaceRoot 'tasks') -or
    -not (Test-Path -LiteralPath (Join-Path $taskRoot 'BRIEF.md') -PathType Leaf)) {
    throw 'Запускайте скрипт из рабочей копии текущего задания tasks\<ID>\wt.'
}
$destination = Join-Path $taskRoot 'kit'
$taskTemporary = Join-Path $taskRoot 'tmp'
New-Item -ItemType Directory -Path $taskTemporary -Force | Out-Null
$manifestName = 'host-manifest.sha256.json'
$moduleSource = if ($ModulePackagePath) { (Resolve-Path -LiteralPath $ModulePackagePath).Path } else { Join-Path $workspaceRoot 'builds/malachite/current' }
if ($PackagePath -and $ModulePackagePath) { throw 'ModulePackagePath applies only when building a new host package.' }
if (-not $PackagePath -and -not (Test-Path -LiteralPath $moduleSource -PathType Container)) {
    throw "Нет комплекта Малахита: $moduleSource. Сначала заполните builds\malachite\current приёмкой задания Малахита."
}

function Resolve-ContainedFile([string]$Root, [string]$Relative) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $full = [IO.Path]::GetFullPath((Join-Path $rootFull $Relative))
    if ([IO.Path]::IsPathRooted($Relative) -or -not $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Путь вне папки комплекта: $Relative"
    }
    $node = $full
    while ($node -and $node.Length -ge $rootFull.Length) {
        if ((Test-Path -LiteralPath $node) -and ((Get-Item -LiteralPath $node).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Ссылки в пути комплекта не поддерживаются: $node"
        }
        $node = Split-Path -Parent $node
    }
    return $full
}
function Assert-Closed {
    foreach ($process in @(Get-Process -Name EvilIslands.DatabaseEditor,kmd-generate -ErrorAction SilentlyContinue)) {
        if (-not $process.Path -or $process.Path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Закройте редактор из builds\dbeditor\current перед обновлением.'
        }
    }
}
Assert-Closed
$temporary = $null
$handoffRoot = Split-Path -Parent $destination
New-Item -ItemType Directory -Path $handoffRoot -Force | Out-Null
$lockPath = Resolve-ContainedFile $taskTemporary 'dbeditor-current-update.lock'
$updateLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    if (-not $PackagePath) {
        # A coordinated task may package its reviewed module candidate without
        # installing either product into builds/current before owner acceptance.
        $moduleManifest = Get-Content -LiteralPath (Resolve-ContainedFile $moduleSource 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($file in $moduleManifest.files) {
            $sourceFile = Resolve-ContainedFile $moduleSource $file.name
            if ((Get-FileHash -LiteralPath $sourceFile).Hash -ne $file.sha256 -or (Get-Item -LiteralPath $sourceFile).Length -ne $file.length) { throw "Module file mismatch: $($file.name)" }
        }
        $temporary = Join-Path $taskTemporary ('database-editor-current-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporary | Out-Null
        & (Join-Path $PSScriptRoot 'invoke-build.ps1') dotnet.exe publish (Join-Path $projectRoot 'src\EvilIslands.DatabaseEditor.App') -c Release --no-restore -o $temporary
        if ($LASTEXITCODE -ne 0) { throw "Сборка не завершена: $LASTEXITCODE" }
        if (@($moduleManifest.sdk).Count -ne 2 -or @($moduleManifest.sdk.name | Sort-Object -Unique).Count -ne 2) { throw 'Invalid module SDK manifest.' }
        foreach ($sdk in $moduleManifest.sdk) {
            if ($sdk.name -notin @('ModuleContracts.V1.dll', 'ModuleContracts.Wpf.V1.dll') -or
                (Get-FileHash -LiteralPath (Join-Path $temporary $sdk.name)).Hash -ne $sdk.sha256) { throw 'Module SDK differs from the published host SDK.' }
        }
        $moduleTarget = Join-Path $temporary 'Modules\malachite\current'
        New-Item -ItemType Directory -Path $moduleTarget -Force | Out-Null
        Get-ChildItem -LiteralPath $moduleSource -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($moduleSource.Length + 1)
            $target = Join-Path $moduleTarget $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\CURRENT_README.txt') -Destination (Join-Path $temporary 'README.txt')
        $packSource = Join-Path $workspaceRoot 'builds/rebuild64/current'
        $packSums = @(Get-Content -LiteralPath (Join-Path $packSource 'SHA256SUMS') -Encoding UTF8)
        foreach ($name in 'EIRebuild64Pack.exe', 'EIRebuild64Pack.pdb') {
            $sums = @($packSums | Where-Object { $_ -match ('^[0-9A-Fa-f]{64}  ' + [regex]::Escape($name) + '$') })
            if ($sums.Count -ne 1) { throw "Нет единственного SHA-256 упаковщика: $name" }
            $expected = $sums[0].Substring(0, 64)
            $file = Resolve-ContainedFile $packSource $name
            if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) { throw "Хэш упаковщика не совпал: $file" }
            $target = Join-Path $temporary $name
            # Keep the EXE copied by MSBuild, whose hash was embedded in Core.
            # A concurrent rebuild64 update must fail packaging, never replace it.
            if ($name -eq 'EIRebuild64Pack.pdb') { Copy-Item -LiteralPath $file -Destination $target }
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw "Ошибка проверки копии упаковщика: $target" }
        }
        $entries = @(Get-ChildItem -LiteralPath $temporary -Recurse -File | ForEach-Object {
            [pscustomobject]@{ Path = $_.FullName.Substring($temporary.Length + 1); Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
        })
        ConvertTo-Json -InputObject $entries -Depth 4 | Set-Content -LiteralPath (Join-Path $temporary $manifestName) -Encoding utf8
        $PackagePath = $temporary
    }
    $source = (Resolve-Path -LiteralPath $PackagePath).Path
    if ($source.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) { throw 'Источник и назначение совпадают.' }
    if (Test-Path -LiteralPath (Join-Path $source 'Kimodo')) { throw 'В исходном комплекте присутствует удалённый генератор.' }
    $entries = Get-Content -LiteralPath (Join-Path $source $manifestName) -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not ($entries.Path -contains 'EvilIslands.DatabaseEditor.exe')) { throw 'В комплекте нет редактора.' }
    $old = @()
    if (Test-Path -LiteralPath (Join-Path $destination $manifestName)) {
        $old = Get-Content -LiteralPath (Join-Path $destination $manifestName) -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    # Canonical separators prevent unchanged Windows paths being deleted as obsolete.
    foreach ($entry in @($entries) + @($old)) { $entry.Path = $entry.Path.Replace('/', '\') }
    # Migrate the old separately managed generator. Preserve unlisted user files.
    $legacyManifest = 'Kimodo\install-manifest.json'
    $legacyPath = Resolve-ContainedFile $destination $legacyManifest
    if (Test-Path -LiteralPath $legacyPath) {
        $legacy = Get-Content -Raw -Encoding utf8 -LiteralPath $legacyPath | ConvertFrom-Json
        if ($legacy.format -ne 'kimodo-editor-install-v1') { throw 'Неизвестный формат старого манифеста генератора.' }
        foreach ($item in $legacy.files) {
            $null = Resolve-ContainedFile (Join-Path $destination 'Kimodo') $item.path
            $old += [pscustomobject]@{ Path = 'Kimodo\' + $item.path.Replace('/', '\'); SHA256 = $item.sha256 }
        }
        $old += [pscustomobject]@{ Path = $legacyManifest; SHA256 = (Get-FileHash -LiteralPath $legacyPath).Hash }
    }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $snapshots = @{}
    $changes = @()
    foreach ($entry in $entries) {
        if (-not $paths.Add($entry.Path.Replace('/', '\')) -or $entry.Path -eq $manifestName) { throw "Повторный путь манифеста: $($entry.Path)" }
        $file = Resolve-ContainedFile $source $entry.Path
        $target = Resolve-ContainedFile $destination $entry.Path
        if ($entry.SHA256 -notmatch '^[0-9A-Fa-f]{64}$' -or (Get-Item -LiteralPath $file).Length -ne $entry.Length -or
            (Get-FileHash -LiteralPath $file).Hash -ne $entry.SHA256) { throw "Хэш или размер исходника не совпал: $file" }
        $hash = $null
        if (Test-Path -LiteralPath $target) {
            $previous = @($old | Where-Object Path -eq $entry.Path)
            $hash = (Get-FileHash -LiteralPath $target).Hash
            if ($hash -ne $entry.SHA256 -and ($previous.Count -ne 1 -or $hash -ne $previous[0].SHA256)) {
                throw "Файл изменён вне сборки, сохранён без перезаписи: $target"
            }
        }
        $snapshots[$entry.Path] = $hash
        if ($hash -ne $entry.SHA256) { $changes += $entry }
    }
    $obsolete = @($old | Where-Object { $_.Path -notin $entries.Path })
    foreach ($entry in $obsolete) {
        $file = Resolve-ContainedFile $destination $entry.Path
        if ((Test-Path -LiteralPath $file) -and (Get-FileHash -LiteralPath $file).Hash -ne $entry.SHA256) { throw "Старый файл изменён: $file" }
        $snapshots[$entry.Path] = if (Test-Path -LiteralPath $file) { $entry.SHA256 } else { $null }
    }
    $hostManifestSource = Resolve-ContainedFile $source $manifestName
    $hostManifestTarget = Resolve-ContainedFile $destination $manifestName
    $snapshots[$manifestName] = if (Test-Path -LiteralPath $hostManifestTarget) { (Get-FileHash -LiteralPath $hostManifestTarget).Hash } else { $null }
    $changes += [pscustomobject]@{ Path = $manifestName; SHA256 = (Get-FileHash -LiteralPath $hostManifestSource).Hash }
    # Only replaced files need rollback copies; unchanged large model weights
    # are verified above but never recopied.
    $rollback = Join-Path $taskTemporary ('database-editor-rollback-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $rollback | Out-Null
    foreach ($entry in @($changes) + @($obsolete)) {
        if ($snapshots[$entry.Path]) {
            $backup = Resolve-ContainedFile $rollback $entry.Path
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            Copy-Item -LiteralPath (Resolve-ContainedFile $destination $entry.Path) -Destination $backup
            if ((Get-FileHash -LiteralPath $backup).Hash -ne $snapshots[$entry.Path]) { throw 'Файл изменился при подготовке отката.' }
        }
    }
    Assert-Closed
    foreach ($path in $snapshots.Keys) {
        $target = Resolve-ContainedFile $destination $path
        $now = if (Test-Path -LiteralPath $target) { (Get-FileHash -LiteralPath $target).Hash } else { $null }
        if ($now -ne $snapshots[$path]) { throw "Файл изменился перед обновлением: $path" }
    }
    $written = [Collections.Generic.List[string]]::new()
    try {
    foreach ($entry in $changes) {
        $target = Resolve-ContainedFile $destination $entry.Path
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        $written.Add($entry.Path)
        Copy-Item -LiteralPath (Resolve-ContainedFile $source $entry.Path) -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target).Hash -ne $entry.SHA256) { throw "Ошибка проверки копии: $target" }
    }
    foreach ($entry in $obsolete) {
        $file = Resolve-ContainedFile $destination $entry.Path
        $written.Add($entry.Path)
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    } catch {
        $failure = $_
        $rollbackErrors = @()
        for ($index = $written.Count - 1; $index -ge 0; $index--) {
            $path = $written[$index]
            try {
                $target = Resolve-ContainedFile $destination $path
                if ($snapshots[$path]) {
                    Copy-Item -LiteralPath (Resolve-ContainedFile $rollback $path) -Destination $target -Force
                    if ((Get-FileHash -LiteralPath $target).Hash -ne $snapshots[$path]) { throw "Откат не совпал: $path" }
                } elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
            } catch { $rollbackErrors += $_.Exception.Message }
        }
        throw "Обновление не завершено: $failure. Копия для отката: $rollback. Ошибки отката: $($rollbackErrors -join '; ')"
    }
    $checksums = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $destination 'SHA256SUMS') } | Sort-Object FullName | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($destination.Length + 1).Replace('\', '/')
    })
    $checksums | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS') -Encoding ASCII
    Write-Output "Копия заменённых файлов: $rollback"
    Write-Output "Готовый редактор: $destination\EvilIslands.DatabaseEditor.exe"
} finally {
    $updateLock.Dispose()
    if ($temporary) {
        $resolved = [IO.Path]::GetFullPath($temporary)
        $tempRoot = [IO.Path]::GetFullPath($taskTemporary).TrimEnd('\') + '\'
        if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved).StartsWith('database-editor-current-')) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
