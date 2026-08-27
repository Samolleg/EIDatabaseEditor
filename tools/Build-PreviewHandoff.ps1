param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$handoffRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'handoff'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $handoffRoot 'EvilIslands.DatabaseEditor-0.2.0-preview.1'
}

$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$handoffPrefix = $handoffRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($handoffPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory должен находиться внутри $handoffRoot"
}

if (Test-Path -LiteralPath $outputPath) {
    throw "Папка handoff уже существует: $outputPath"
}

$buildOutput = Join-Path $projectRoot 'src\EvilIslands.DatabaseEditor.App\bin\Release\net9.0-windows'
$runtimeFiles = @(
    'EvilIslands.DatabaseEditor.exe',
    'EvilIslands.DatabaseEditor.dll',
    'EvilIslands.DatabaseEditor.Core.dll',
    'EvilIslands.DatabaseEditor.deps.json',
    'EvilIslands.DatabaseEditor.runtimeconfig.json'
)

foreach ($name in $runtimeFiles) {
    $source = Join-Path $buildOutput $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Не найден Release runtime-файл: $source"
    }
}

$readmeSource = Join-Path $projectRoot 'packaging\README_RU.txt'
if (-not (Test-Path -LiteralPath $readmeSource -PathType Leaf)) {
    throw "Не найден README handoff: $readmeSource"
}

$gitHead = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $gitHead -notmatch '^[0-9a-f]{40}$') {
    throw 'Не удалось определить точный Git commit исходников.'
}

New-Item -ItemType Directory -Path $outputPath | Out-Null
try {
    foreach ($name in $runtimeFiles) {
        Copy-Item -LiteralPath (Join-Path $buildOutput $name) -Destination (Join-Path $outputPath $name)
    }
    Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $outputPath 'README_RU.txt')

    $manifestFiles = foreach ($file in (Get-ChildItem -LiteralPath $outputPath -File | Sort-Object Name)) {
        [ordered]@{
            name = $file.Name
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }

    $manifest = [ordered]@{
        product = 'EI Database Studio'
        version = '0.2.0-preview.1'
        sourceCommit = $gitHead
        createdAt = (Get-Date).ToString('o')
        framework = 'Microsoft.WindowsDesktop.App 9.0'
        files = @($manifestFiles)
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'manifest.json') -Encoding utf8
}
catch {
    $resolvedOutput = [System.IO.Path]::GetFullPath($outputPath)
    if ($resolvedOutput.StartsWith($handoffPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedOutput -PathType Container)) {
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }
    throw
}

Write-Output $outputPath
