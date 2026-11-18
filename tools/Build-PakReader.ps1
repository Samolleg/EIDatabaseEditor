#requires -Version 7.6.10
#requires -PSEdition Core
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = $projectRoot
while (!(Test-Path -LiteralPath (Join-Path $workspaceRoot 'RULES.md'))) {
    $parent = Split-Path -Parent $workspaceRoot
    if (!$parent -or $parent -eq $workspaceRoot) { throw 'Evil Islands workspace not found.' }
    $workspaceRoot = $parent
}
# The native reader deliberately reuses the reviewed engine implementation.
# Source drift must be reviewed, never silently compiled into a new reader.
foreach ($line in Get-Content -LiteralPath (Join-Path $projectRoot 'native/pak-reader.sources.sha256')) {
    $expected, $relative = $line -split '  ', 2
    if ((Get-FileHash -LiteralPath (Join-Path $workspaceRoot $relative) -Algorithm SHA256).Hash -ne $expected) {
        throw "PAK reader source changed: $relative. Review the shared reader before updating its pin."
    }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'MSVC x64 build tools are required for the PAK reader.' }
$msbuild = Join-Path $installation 'MSBuild/Current/Bin/MSBuild.exe'
$headers = Join-Path $projectRoot 'artifacts/pak-reader/include/nlohmann'
$jsonRoot = Join-Path $workspaceRoot 'rebuild64/SDKs/JSON'
foreach ($file in Get-ChildItem -LiteralPath $jsonRoot -Recurse -File) {
    $target = Join-Path $headers $file.FullName.Substring($jsonRoot.Length + 1)
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}
$previousPdbEndpoint = $env:_MSPDBSRV_ENDPOINT_
try {
    $env:_MSPDBSRV_ENDPOINT_ = 'DbPak-' + [guid]::NewGuid().ToString('N')
    & (Join-Path $PSScriptRoot 'invoke-build.ps1') $msbuild (Join-Path $projectRoot 'native/PakReader.vcxproj') /nologo /m:1 /v:minimal /p:Configuration=Release /p:Platform=x64 /p:PreferredToolArchitecture=x64 "/p:EiWorkspace=$workspaceRoot"
    if ($LASTEXITCODE -ne 0) { throw "PAK reader build failed: $LASTEXITCODE" }
} finally { $env:_MSPDBSRV_ENDPOINT_ = $previousPdbEndpoint }
