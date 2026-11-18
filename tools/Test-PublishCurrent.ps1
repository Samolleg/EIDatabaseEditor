#requires -Version 7.6.10
#requires -PSEdition Core
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('dbe-publish-test-' + [guid]::NewGuid().ToString('N'))
$testRoot = Join-Path $fixture 'tasks/DBTEST/wt'
New-Item -ItemType Directory -Path (Join-Path $testRoot 'tools') -Force | Out-Null
New-Item -ItemType File -Path (Join-Path $fixture 'RULES.md'),(Join-Path $testRoot '../BRIEF.md') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Publish-Current.ps1') -Destination (Join-Path $testRoot 'tools/Publish-Current.ps1')
$current = Join-Path $testRoot '../kit'
$package = Join-Path $testRoot '../package'
function Put([string]$root, [string]$path, [string]$value) {
    $target = Join-Path $root $path
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $value)
}
function Hash([string]$path) { (Get-FileHash -LiteralPath $path).Hash }
function Manifests([string]$root) {
    $hostEntries = @(foreach ($path in @('EvilIslands.DatabaseEditor.exe', 'app.dll', 'Modules/malachite/current/module.dll')) {
        $file = Join-Path $root $path
        [pscustomobject]@{ Path = $path; Length = (Get-Item $file).Length; SHA256 = Hash $file }
    })
    Put $root 'host-manifest.sha256.json' (ConvertTo-Json -InputObject $hostEntries)
    if ($root -ne $current) { return }
    $files = @(foreach ($path in @('bin/kmd-generate.exe', 'bin/ggml.dll', 'motion.gguf')) {
        $file = Join-Path $root ('Kimodo/' + $path)
        [pscustomobject]@{ path = $path; bytes = (Get-Item $file).Length; sha256 = Hash $file }
    })
    Put $root 'Kimodo/install-manifest.json' (ConvertTo-Json -Depth 5 -InputObject @{ format = 'kimodo-editor-install-v1'; files = $files })
}
function Snapshot {
    $map = @{}
    Get-ChildItem $current -Recurse -File | ForEach-Object { $map[$_.FullName] = Hash $_.FullName }
    return $map
}
function Unchanged($before) {
    $after = Snapshot
    if ($before.Count -ne $after.Count) { throw 'Changed file inventory.' }
    foreach ($key in $before.Keys) { if ($after[$key] -ne $before[$key]) { throw "Unexpected change: $key" } }
}
function Run-Publish { & (Join-Path $testRoot 'tools/Publish-Current.ps1') -PackagePath $package }
function Reject([string]$message) {
    $before = Snapshot
    $rejected = $false
    try { Run-Publish | Out-Null } catch { $rejected = $true; Write-Output "Expected rejection ($message): $($_.Exception.Message)" }
    if (-not $rejected) { throw "Not rejected: $message" }
    Unchanged $before
}
foreach ($root in @($current, $package)) {
    Put $root 'EvilIslands.DatabaseEditor.exe' 'old-host'
    Put $root 'app.dll' 'old-app'
    Put $root 'Modules/malachite/current/module.dll' 'preserved-module'
    if ($root -eq $current) {
    Put $root 'Kimodo/bin/kmd-generate.exe' 'old-native'
    Put $root 'Kimodo/bin/ggml.dll' 'old-ggml'
    Put $root 'Kimodo/motion.gguf' 'preserved-model'
    }
    Manifests $root
}
Put $current 'user-settings.txt' 'preserved-user'
Put $current 'Kimodo/user.json' 'preserved-native-user'
Put $package 'EvilIslands.DatabaseEditor.exe' 'new-host'
Put $package 'app.dll' 'new-app'
Manifests $package
Put $package 'app.dll' 'corrupt'
Reject 'bad package hash'
Put $package 'app.dll' 'new-app'
Put $current 'Kimodo/bin/ggml.dll' 'changed-outside-editor'
Reject 'preserve modified old generator'
Put $current 'Kimodo/bin/ggml.dll' 'old-ggml'
$global:dbePublishTestInjectFailure = $true
$global:dbePublishTestFailureTarget = [IO.Path]::GetFullPath((Join-Path $current 'app.dll'))
function Copy-Item {
    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
    if ($global:dbePublishTestInjectFailure -and [IO.Path]::GetFullPath($Destination) -eq $global:dbePublishTestFailureTarget) {
        $global:dbePublishTestInjectFailure = $false
        throw 'Injected native write failure'
    }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
Reject 'mid-copy rollback of host and native'
Run-Publish | Out-Null
foreach ($entry in (Get-Content (Join-Path $package 'host-manifest.sha256.json') -Raw | ConvertFrom-Json)) {
    if ((Hash (Join-Path $current $entry.Path)) -ne $entry.SHA256) { throw 'Host install mismatch.' }
}
if (Test-Path -LiteralPath (Join-Path $current 'Kimodo/install-manifest.json')) { throw 'Obsolete generator manifest survived.' }
foreach ($file in 'bin/kmd-generate.exe','bin/ggml.dll','motion.gguf') {
    if (Test-Path -LiteralPath (Join-Path $current "Kimodo/$file")) { throw "Obsolete generator file survived: $file" }
}
if ([IO.File]::ReadAllText((Join-Path $current 'user-settings.txt')) -ne 'preserved-user' -or
    [IO.File]::ReadAllText((Join-Path $current 'Kimodo/user.json')) -ne 'preserved-native-user') { throw 'Unmanaged data changed.' }
$hostOnly = Join-Path $testRoot 'host-only'
foreach ($entry in (Get-Content (Join-Path $package 'host-manifest.sha256.json') -Raw | ConvertFrom-Json)) {
    Put $hostOnly $entry.Path ([IO.File]::ReadAllText((Join-Path $package $entry.Path)))
}
Put $hostOnly 'host-manifest.sha256.json' ([IO.File]::ReadAllText((Join-Path $package 'host-manifest.sha256.json')))
$mixedEntries = Get-Content (Join-Path $hostOnly 'host-manifest.sha256.json') -Raw | ConvertFrom-Json
foreach ($entry in $mixedEntries) { $entry.Path = $entry.Path.Replace('\', '/') }
ConvertTo-Json -InputObject @($mixedEntries) | Set-Content (Join-Path $hostOnly 'host-manifest.sha256.json') -Encoding utf8
& (Join-Path $testRoot 'tools/Publish-Current.ps1') -PackagePath $hostOnly | Out-Null
foreach ($entry in $mixedEntries) { if ((Hash (Join-Path $current $entry.Path)) -ne $entry.SHA256) { throw 'Mixed-separator preservation failed.' } }
Write-Output "PASS publication: host update and generator removal, corrupt hash, external conflict, injected rollback, user/module preservation, host-only update. Test root: $testRoot"
Remove-Variable dbePublishTestInjectFailure,dbePublishTestFailureTarget -Scope Global
