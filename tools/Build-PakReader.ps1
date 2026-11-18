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
. (Join-Path $PSScriptRoot 'pak-build-cache.ps1')
$null=[IO.Directory]::CreateDirectory((Join-Path $projectRoot 'artifacts/pak-reader'))
$buildLock=Enter-PakBuildLock (Join-Path $projectRoot 'artifacts/pak-reader/.build.lock')
try {
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
# Include project-local headers, reviewed engine sources, codec, JSON and tools.
. (Join-Path $PSScriptRoot 'pak-build-cache.ps1')
$vcVersion=(Get-Content (Join-Path $installation 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$vc=Join-Path $installation "VC/Tools/MSVC/$vcVersion"
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdkVersion=(Get-ChildItem "$sdk/Include" -Directory | Where-Object {Test-Path "$($_.FullName)/um/Windows.h"} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1).Name
$inputs=@($PSCommandPath,(Join-Path $PSScriptRoot 'pak-build-cache.ps1'),$msbuild)
foreach($name in 'Directory.Build.props','Directory.Build.targets'){
    $path=Join-Path $projectRoot $name;if([IO.File]::Exists($path)){$inputs+=$path}
}
foreach($dir in @((Join-Path $projectRoot 'native'),(Join-Path $workspaceRoot 'rebuild64/Pak'),$jsonRoot,(Join-Path $workspaceRoot 'deps/zstd/v1.5.7/lib'))){
    $inputs+=@([IO.Directory]::EnumerateFiles($dir,'*',[IO.SearchOption]::AllDirectories))
}
foreach($name in 'cl.exe','c1xx.dll','c2.dll','link.exe'){$inputs+=Join-Path $vc ('bin/Hostx64/x64/'+$name)}
$baseInputs=@($inputs)
# MSBuild's tracked read dependencies cover the actual transitive SDK inputs too.
$tlogs=Join-Path $projectRoot 'artifacts/pak-reader/obj'
if([IO.Directory]::Exists($tlogs)){
    foreach($log in [IO.Directory]::EnumerateFiles($tlogs,'*.read.*.tlog',[IO.SearchOption]::AllDirectories)){
        $inputs+=@([IO.File]::ReadAllLines($log) | Where-Object {$_ -notlike '^*' -and [IO.File]::Exists($_) -and $_ -notlike "$projectRoot/artifacts/*" -and $_ -notlike "$projectRoot\artifacts\*"})
    }
}
$receipt=Join-Path $projectRoot 'artifacts/pak-reader/build.inputs.json'
$outputs=@('dll','pdb') | ForEach-Object {Join-Path $projectRoot "artifacts/pak-reader/EvilIslands.PakReader.$_"}
$identity=(Get-PakBuildIdentity $inputs)+"`nSDK=$sdkVersion|VC=$vcVersion|Release|x64"
if(Test-PakBuildReceipt $receipt $identity $outputs){Write-Host 'PAK reader unchanged; native MSBuild skipped.';return}
$target=if([IO.File]::Exists($receipt)){'Rebuild'}else{'Build'}
$previousPdbEndpoint = $env:_MSPDBSRV_ENDPOINT_
try {
    $env:_MSPDBSRV_ENDPOINT_ = 'DbPak-' + [guid]::NewGuid().ToString('N')
    & (Join-Path $PSScriptRoot 'invoke-build.ps1') $msbuild (Join-Path $projectRoot 'native/PakReader.vcxproj') /nologo /m:1 /v:minimal /p:Configuration=Release /p:Platform=x64 /nr:false /p:PreferredToolArchitecture=x64 "/t:$target" "/p:VCToolsVersion=$vcVersion" "/p:WindowsTargetPlatformVersion=$sdkVersion" "/p:EiWorkspace=$workspaceRoot"
    if ($LASTEXITCODE -ne 0) { throw "PAK reader build failed: $LASTEXITCODE" }
} finally { $env:_MSPDBSRV_ENDPOINT_ = $previousPdbEndpoint }

# Re-read the current dependency set; removed includes must not survive in the receipt.
$inputs=@($baseInputs)
foreach($log in [IO.Directory]::EnumerateFiles($tlogs,'*.read.*.tlog',[IO.SearchOption]::AllDirectories)){
    $inputs+=@([IO.File]::ReadAllLines($log) | Where-Object {$_ -notlike '^*' -and [IO.File]::Exists($_) -and $_ -notlike "$projectRoot/artifacts/*" -and $_ -notlike "$projectRoot\artifacts\*"})
}
$identity=(Get-PakBuildIdentity $inputs)+"`nSDK=$sdkVersion|VC=$vcVersion|Release|x64"
Save-PakBuildReceipt $receipt $identity $outputs

} finally { $buildLock.Dispose() }
