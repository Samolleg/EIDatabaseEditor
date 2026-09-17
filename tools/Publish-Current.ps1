param(
    [string]$PackagePath,
    [string]$ModulePath
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = Join-Path $projectRoot 'handoff\current'
$manifestName = 'host-manifest.sha256.json'

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
    foreach ($process in @(Get-Process -Name EvilIslands.DatabaseEditor -ErrorAction SilentlyContinue)) {
        if (-not $process.Path -or $process.Path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Закройте редактор из handoff\current перед обновлением.'
        }
    }
}
Assert-Closed
$temporary = $null
$handoffRoot = Split-Path -Parent $destination
New-Item -ItemType Directory -Path $handoffRoot -Force | Out-Null
$lockPath = Resolve-ContainedFile $handoffRoot '.current-update.lock'
$updateLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    if (-not $PackagePath) {
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('database-editor-current-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporary | Out-Null
        & dotnet publish (Join-Path $projectRoot 'src\EvilIslands.DatabaseEditor.App') -c Release --no-restore -p:DebugType=none -p:DebugSymbols=false -o $temporary
        if ($LASTEXITCODE -ne 0) { throw "Сборка не завершена: $LASTEXITCODE" }
        if ($ModulePath) {
            $moduleTarget = Join-Path $temporary 'Modules\malachite\0.4.0'
            New-Item -ItemType Directory -Path $moduleTarget -Force | Out-Null
            Copy-Item -Path (Join-Path $ModulePath '*') -Destination $moduleTarget -Recurse
        } elseif (Test-Path -LiteralPath (Join-Path $destination 'Modules')) {
            Copy-Item -LiteralPath (Join-Path $destination 'Modules') -Destination $temporary -Recurse
        } else { throw 'Для первой сборки укажите -ModulePath с проверенным комплектом Малахита.' }
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
    foreach ($entry in $entries) {
        $file = Resolve-ContainedFile $source $entry.Path
        $target = Resolve-ContainedFile $destination $entry.Path
        if ((Get-FileHash -LiteralPath $file).Hash -ne $entry.SHA256) { throw "Хэш исходника не совпал: $file" }
        if (Test-Path -LiteralPath $target) {
            $previous = @($old | Where-Object Path -eq $entry.Path)
            $hash = (Get-FileHash -LiteralPath $target).Hash
            if ($hash -ne $entry.SHA256 -and ($previous.Count -ne 1 -or $hash -ne $previous[0].SHA256)) {
                throw "Файл изменён вне сборки, сохранён без перезаписи: $target"
            }
        }
    }
    $obsolete = @($old | Where-Object { $_.Path -notin $entries.Path })
    foreach ($entry in $obsolete) {
        $file = Resolve-ContainedFile $destination $entry.Path
        if ((Test-Path -LiteralPath $file) -and (Get-FileHash -LiteralPath $file).Hash -ne $entry.SHA256) { throw "Старый файл изменён: $file" }
    }
    Assert-Closed
    foreach ($entry in $entries) {
        $target = Resolve-ContainedFile $destination $entry.Path
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Resolve-ContainedFile $source $entry.Path) -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target).Hash -ne $entry.SHA256) { throw "Ошибка проверки копии: $target" }
    }
    foreach ($entry in $obsolete) {
        $file = Resolve-ContainedFile $destination $entry.Path
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    Copy-Item -LiteralPath (Join-Path $source $manifestName) -Destination (Join-Path $destination $manifestName) -Force
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
