param(
    [string]$PackagePath
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = $PSScriptRoot
while (!(Test-Path -LiteralPath (Join-Path $workspaceRoot 'RULES.md') -PathType Leaf)) {
    $parent = Split-Path -Parent $workspaceRoot
    if (!$parent -or $parent -eq $workspaceRoot) { throw 'Project RULES.md not found above worktree.' }
    $workspaceRoot = $parent
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = Join-Path $workspaceRoot 'builds/dbeditor/current'
$manifestName = 'host-manifest.sha256.json'
$moduleSource = Join-Path $workspaceRoot 'builds/malachite/current'
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
            throw 'Закройте редактор и генератор Kimodo из builds\dbeditor\current перед обновлением.'
        }
    }
}
Assert-Closed
$temporary = $null
$handoffRoot = Split-Path -Parent $destination
New-Item -ItemType Directory -Path $handoffRoot -Force | Out-Null
$lockPath = Resolve-ContainedFile (Join-Path $workspaceRoot 'docs') 'dbeditor-current-update.lock'
$updateLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    if (-not $PackagePath) {
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('database-editor-current-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporary | Out-Null
        & dotnet publish (Join-Path $projectRoot 'src\EvilIslands.DatabaseEditor.App') -c Release --no-restore -p:DebugType=none -p:DebugSymbols=false -o $temporary
        if ($LASTEXITCODE -ne 0) { throw "Сборка не завершена: $LASTEXITCODE" }
        $moduleTarget = Join-Path $temporary 'Modules\malachite\current'
        New-Item -ItemType Directory -Path $moduleTarget -Force | Out-Null
        Get-ChildItem -LiteralPath $moduleSource -Recurse -File | Where-Object Extension -ne '.pdb' | ForEach-Object {
            $relative = $_.FullName.Substring($moduleSource.Length + 1)
            $target = Join-Path $moduleTarget $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\CURRENT_README.txt') -Destination (Join-Path $temporary 'README.txt')
        Get-ChildItem -LiteralPath $temporary -Recurse -File -Filter *.pdb | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
        $entries = @(Get-ChildItem -LiteralPath $temporary -Recurse -File | ForEach-Object {
            [pscustomobject]@{ Path = $_.FullName.Substring($temporary.Length + 1); Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
        })
        ConvertTo-Json -InputObject $entries -Depth 4 | Set-Content -LiteralPath (Join-Path $temporary $manifestName) -Encoding utf8
        $PackagePath = $temporary
    }
    $source = (Resolve-Path -LiteralPath $PackagePath).Path
    if ($source.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) { throw 'Источник и назначение совпадают.' }
    $entries = Get-Content -LiteralPath (Join-Path $source $manifestName) -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not ($entries.Path -contains 'EvilIslands.DatabaseEditor.exe')) { throw 'В комплекте нет редактора.' }
    $old = @()
    if (Test-Path -LiteralPath (Join-Path $destination $manifestName)) {
        $old = Get-Content -LiteralPath (Join-Path $destination $manifestName) -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    # Canonical separators prevent unchanged Windows paths being deleted as obsolete.
    foreach ($entry in @($entries) + @($old)) { $entry.Path = $entry.Path.Replace('/', '\') }
    # Kimodo has its own manifest. Keep it separate so host-only updates never
    # remove the installed generator, weights or user files.
    $hostEntries = @($entries)
    $nativeManifest = 'Kimodo\install-manifest.json'
    $nativeSource = Resolve-ContainedFile $source $nativeManifest
    $nativeTarget = Resolve-ContainedFile $destination $nativeManifest
    $nativeManifestHash = $null
    if (Test-Path -LiteralPath $nativeSource) {
        $native = Get-Content -LiteralPath $nativeSource -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($native.format -ne 'kimodo-editor-install-v1' -or -not $native.files -or
            -not ($native.files.path -contains 'bin/kmd-generate.exe')) { throw 'Неполный манифест Kimodo.' }
        $nativeOld = @()
        if (Test-Path -LiteralPath $nativeTarget) {
            $previousNative = Get-Content -LiteralPath $nativeTarget -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($previousNative.format -ne 'kimodo-editor-install-v1') { throw 'Неизвестный установленный манифест Kimodo.' }
            $nativeOld = @($previousNative.files)
            $nativeManifestHash = (Get-FileHash -LiteralPath $nativeTarget).Hash
        }
        foreach ($file in $native.files) {
            # Validate containment inside Kimodo itself, not just the package.
            $null = Resolve-ContainedFile (Join-Path $source 'Kimodo') $file.path
            $entries += [pscustomobject]@{ Path = 'Kimodo\' + $file.path.Replace('/', '\'); Length = $file.bytes; SHA256 = $file.sha256 }
        }
        foreach ($file in $nativeOld) {
            $null = Resolve-ContainedFile (Join-Path $destination 'Kimodo') $file.path
            $old += [pscustomobject]@{ Path = 'Kimodo\' + $file.path.Replace('/', '\'); Length = $file.bytes; SHA256 = $file.sha256 }
        }
        $entries += [pscustomobject]@{ Path = $nativeManifest; Length = (Get-Item -LiteralPath $nativeSource).Length; SHA256 = (Get-FileHash -LiteralPath $nativeSource).Hash }
        if ($nativeManifestHash) { $old += [pscustomobject]@{ Path = $nativeManifest; SHA256 = $nativeManifestHash } }
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
    $obsolete = @($old | Where-Object { $_.Path -notlike 'Kimodo\*' -and $_.Path -notin $hostEntries.Path })
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
    $rollback = Join-Path ([IO.Path]::GetTempPath()) ('database-editor-rollback-' + [guid]::NewGuid().ToString('N'))
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
    Write-Output "Копия заменённых файлов: $rollback"
    Write-Output "Готовый редактор: $destination\EvilIslands.DatabaseEditor.exe"
} finally {
    $updateLock.Dispose()
    if ($temporary) {
        $resolved = [IO.Path]::GetFullPath($temporary)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved).StartsWith('database-editor-current-')) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
