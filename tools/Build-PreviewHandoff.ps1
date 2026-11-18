#requires -Version 7.6.10
#requires -PSEdition Core
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = $PSScriptRoot
while (!(Test-Path -LiteralPath (Join-Path $workspaceRoot 'RULES.md') -PathType Leaf)) {
    $parent = Split-Path -Parent $workspaceRoot
    if (!$parent -or $parent -eq $workspaceRoot) { throw 'Project RULES.md not found above worktree.' }
    $workspaceRoot = $parent
}


# Local builds always update the permanent editor folder. Versioned exports are explicit only.
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    & (Join-Path $PSScriptRoot 'Publish-Current.ps1')
    return
}

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$propsPath = Join-Path $projectRoot 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$revision = [string]$props.Project.PropertyGroup.EditorRevision
$versionPrefix = if ($revision) { "$revision.0.0" } else { [string]$props.Project.PropertyGroup.VersionPrefix }
$versionSuffix = [string]$props.Project.PropertyGroup.VersionSuffix
if ([string]::IsNullOrWhiteSpace($versionPrefix)) {
    throw "В $propsPath не задан VersionPrefix."
}
$version = if ([string]::IsNullOrWhiteSpace($versionSuffix)) {
    $versionPrefix
} else {
    "$versionPrefix-$versionSuffix"
}
$handoffRoot = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot 'builds/dbeditor'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $handoffRoot 'current'
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

$runtimeIdentifier = 'win-x64'
$runtimeFile = 'EvilIslands.DatabaseEditor.exe'
$appProject = Join-Path $projectRoot 'src\EvilIslands.DatabaseEditor.App\EvilIslands.DatabaseEditor.App.csproj'
$nugetSource = 'https://api.nuget.org/v3/index.json'

$readmeSource = Join-Path $projectRoot 'packaging\README_RU.txt'
if (-not (Test-Path -LiteralPath $readmeSource -PathType Leaf)) {
    throw "Не найден README handoff: $readmeSource"
}

$gitHead = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $gitHead -notmatch '^[0-9a-f]{40}$') {
    throw 'Не удалось определить точный Git commit исходников.'
}
$gitStatus = @(& git -C $projectRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or $gitStatus.Count -ne 0) {
    throw 'Handoff создаётся только из чистого tracked Git-состояния.'
}

$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'EvilIslands.DatabaseEditor.Publish-' + [guid]::NewGuid().ToString('N'))
$publishPath = Join-Path $publishRoot 'publish'
New-Item -ItemType Directory -Path $publishPath | Out-Null
try {
    & (Join-Path $PSScriptRoot 'invoke-build.ps1') dotnet.exe publish $appProject `
        -c Release `
        -r $runtimeIdentifier `
        --self-contained true `
        --source $nugetSource `
        -p:RestoreIgnoreFailedSources=true `
        -p:NuGetAudit=false `
        --output $publishPath `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=none `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish завершился с кодом $LASTEXITCODE."
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $publishPath -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne $runtimeFile) {
        $actualNames = ($publishedFiles.Name | Sort-Object) -join ', '
        throw "Single-file публикация содержит неожиданные файлы: $actualNames"
    }

    $publishedExe = Join-Path $publishPath $runtimeFile
    $expectedFileVersion = [string]$props.Project.PropertyGroup.FileVersion
    $exeInfo = (Get-Item -LiteralPath $publishedExe).VersionInfo
    if ($exeInfo.FileVersion -ne $expectedFileVersion -or
        $exeInfo.ProductVersion -ne "$version+$gitHead") {
        throw "Release-бинарник не соответствует версии $version и Git commit $gitHead."
    }

    New-Item -ItemType Directory -Path $outputPath | Out-Null
    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $outputPath $runtimeFile)
    Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $outputPath 'README_RU.txt')

    $manifestFiles = foreach ($file in (Get-ChildItem -LiteralPath $outputPath -File | Sort-Object Name)) {
        [ordered]@{
            name = $file.Name
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }

    $manifest = [ordered]@{
        product = 'Редактор БД от Атамана'
        version = $version
        sourceCommit = $gitHead
        createdAt = (Get-Date).ToString('o')
        framework = '.NET 10 self-contained'
        runtimeIdentifier = $runtimeIdentifier
        singleFile = $true
        distributableFile = $runtimeFile
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
finally {
    $resolvedPublishRoot = [System.IO.Path]::GetFullPath($publishRoot)
    $tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedPublishRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedPublishRoot).StartsWith(
            'EvilIslands.DatabaseEditor.Publish-', [System.StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolvedPublishRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedPublishRoot -Recurse -Force
    }
}

Write-Output $outputPath
