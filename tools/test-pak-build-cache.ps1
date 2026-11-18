#requires -Version 7.6.10
#requires -PSEdition Core
param([Parameter(Mandatory=$true)][string]$FixtureRoot)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'pak-build-cache.ps1')
if([IO.Directory]::Exists($FixtureRoot)){throw 'Use a fresh fixture directory.'}
$null=[IO.Directory]::CreateDirectory($FixtureRoot)
$source=Join-Path $FixtureRoot 'source';$output=Join-Path $FixtureRoot 'dll';$receipt=Join-Path $FixtureRoot 'receipt'
[IO.File]::WriteAllText($source,'one');[IO.File]::WriteAllText($output,'dll')
$identity=Get-PakBuildIdentity @($source)
$script:checks=0
function Verify($Value){$script:checks++;if(!$Value){throw "Failed check $script:checks"}}
Verify (!(Test-PakBuildReceipt $receipt $identity @($output)))
Save-PakBuildReceipt $receipt $identity @($output)
Verify (Test-PakBuildReceipt $receipt $identity @($output))
$stamp=[IO.File]::GetLastWriteTimeUtc($source);[IO.File]::WriteAllText($source,'two');[IO.File]::SetLastWriteTimeUtc($source,$stamp)
Verify (!(Test-PakBuildReceipt $receipt (Get-PakBuildIdentity @($source)) @($output)))
Verify (!(Test-PakBuildReceipt $receipt ($identity+'compiler changed') @($output)))
[IO.File]::WriteAllText($output,'bad')
Verify (!(Test-PakBuildReceipt $receipt $identity @($output)))
[IO.File]::Delete($output)
Verify (!(Test-PakBuildReceipt $receipt $identity @($output)))
[IO.File]::WriteAllText($receipt,'corrupt json')
Verify (!(Test-PakBuildReceipt $receipt $identity @($output)))
Write-Host "PASS: $script:checks native PAK receipt checks."
