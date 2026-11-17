#requires -Version 7.6.10
#requires -PSEdition Core
$ErrorActionPreference = 'Stop'
$workspaceRoot = $PSScriptRoot
while (!(Test-Path -LiteralPath (Join-Path $workspaceRoot 'RULES.md') -PathType Leaf)) {
    $parent = Split-Path -Parent $workspaceRoot
    if (!$parent -or $parent -eq $workspaceRoot) { throw 'Project RULES.md not found above worktree.' }
    $workspaceRoot = $parent
}

$hostRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot 'builds/dbeditor/current'))
$moduleRelative = 'Modules\malachite\current\'
$target = Join-Path $hostRoot $moduleRelative
$source = Join-Path $workspaceRoot 'builds/malachite/current'
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Нет комплекта Малахита: $source. Сначала заполните builds\malachite\current приёмкой задания Малахита."
}
$allowed = @('Malachite.Core.dll','Malachite.UI.Wpf.dll','Malachite.Module.dll','Malachite.Module.deps.json','module.json','README.md','tools/ScriptCheck/ScriptCheck.exe','tools/ScriptCheck/README.md')
$sdkNames = @('ModuleContracts.V1.dll','ModuleContracts.Wpf.V1.dll')
$capabilities = @('workspace.read.v1','navigation.record.v1','files.export-copy.v1','documents.session.v1','documents.blobs.v1','transactions.v1','files.browse-folder.v1','saves.artifact-kind.v1')
function Assert-Closed {
    foreach ($p in @(Get-Process -Name EvilIslands.DatabaseEditor -ErrorAction SilentlyContinue)) {
        if (-not $p.Path -or $p.Path.StartsWith($hostRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close the editor in builds/dbeditor/current before updating Malachite.'
        }
    }
}
function Assert-NoLinks([string]$Path) {
    $node = [IO.Path]::GetFullPath($Path)
    while ($node) {
        if ((Test-Path -LiteralPath $node) -and ((Get-Item -LiteralPath $node).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse point: $node" }
        $node = Split-Path -Parent $node
    }
}
Assert-NoLinks $source
Assert-NoLinks $target
Assert-Closed
$lockPath = Join-Path $workspaceRoot 'docs/dbeditor-current-update.lock'
Assert-NoLinks $lockPath
$updateLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
if (Test-Path -LiteralPath (Join-Path $hostRoot 'Modules\malachite\0.4.0\module.json')) {
    throw 'Legacy Malachite folder detected. Migrate it to Modules\malachite\current with Publish-Current before updating; two copies must not be loaded.'
}
if ($source.Equals($target, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source and destination must differ.' }
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.scriptCheck.commit -notmatch '^[0-9a-f]{40}$' -or $manifest.scriptCheck.runtime -ne 'static CRT/MFC, x64' -or
    $manifest.scriptCheck.path -ne 'tools/ScriptCheck/ScriptCheck.exe' -or
    $manifest.scriptCheck.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $source 'tools/ScriptCheck/ScriptCheck.exe')).Hash) { throw 'Invalid ScriptCheck identity.' }
if (@($manifest.files).Count -ne $allowed.Count -or @($manifest.files.name | Sort-Object -Unique).Count -ne $allowed.Count) { throw 'Unexpected module file list.' }
foreach ($file in $manifest.files) {
    if ($file.name -notin $allowed) { throw "Disallowed module file: $($file.name)" }
    $path = Join-Path $source $file.name
    Assert-NoLinks $path
    if ((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256 -or (Get-Item -LiteralPath $path).Length -ne $file.length) { throw "Module hash/length mismatch: $($file.name)" }
}
if (@($manifest.sdk).Count -ne 2 -or @($manifest.sdk.name | Sort-Object -Unique).Count -ne 2) { throw 'Invalid SDK manifest.' }
foreach ($sdk in $manifest.sdk) {
    if ($sdk.name -notin $sdkNames -or (Get-FileHash -LiteralPath (Join-Path $hostRoot $sdk.name)).Hash -ne $sdk.sha256) { throw 'SDK differs from the current host; rebuild only Malachite against current SDK.' }
}
$contract = Get-Content -LiteralPath (Join-Path $source 'module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($contract.moduleId -ne 'malachite' -or $contract.entryAssembly -ne 'Malachite.Module.dll' -or $contract.entryType -ne 'Malachite.Module.Entry' -or
    $contract.contractMajor -ne 1 -or $contract.contractMinor -ne 0 -or $contract.runtime -ne 'net9.0-windows' -or $contract.architecture -ne 'any' -or
    $null -eq $contract.requiredCapabilities -or @($contract.requiredCapabilities | Where-Object { $_ -notin $capabilities }).Count) { throw 'Incompatible module contract/capabilities.' }
$hostManifestPath = Join-Path $hostRoot 'host-manifest.sha256.json'
$oldManifestBytes = [IO.File]::ReadAllBytes($hostManifestPath)
$oldManifestHash = (Get-FileHash -LiteralPath $hostManifestPath).Hash
$hostManifest = Get-Content -LiteralPath $hostManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$binaryHashes = @{}
Get-ChildItem -LiteralPath $hostRoot -File | Where-Object { $_.Extension -in @('.dll','.exe') } | ForEach-Object { $binaryHashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
foreach ($name in @($allowed) + 'manifest.json') {
    $path = Join-Path $target $name
    Assert-NoLinks $path
    if (Test-Path -LiteralPath $path) {
        $tracked = @($hostManifest | Where-Object { $_.Path.Replace('/','\') -eq ($moduleRelative + $name.Replace('/','\')) })
        if ($tracked.Count -ne 1 -or (Get-FileHash -LiteralPath $path).Hash -ne $tracked[0].SHA256) { throw "Current module file changed outside updater: $name" }
    }
}
$backup = @{}
foreach ($name in @($allowed) + 'manifest.json') {
    $path = Join-Path $target $name
    $backup[$name] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
Assert-Closed
try {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($name in @($allowed) + 'manifest.json') {
        New-Item -ItemType Directory -Path (Split-Path -Parent (Join-Path $target $name)) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name) -Force
        if ((Get-FileHash -LiteralPath (Join-Path $source $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $target $name)).Hash) { throw "Copy verification failed: $name" }
    }
    $next = @($hostManifest | Where-Object { $_.Path.Replace('/','\') -notin @($backup.Keys | ForEach-Object { $moduleRelative + $_.Replace('/','\') }) })
    foreach ($name in @($allowed) + 'manifest.json') {
        $file = Get-Item -LiteralPath (Join-Path $target $name)
        $next += [pscustomobject]@{ Path=$moduleRelative+$name.Replace('/','\'); Length=$file.Length; SHA256=(Get-FileHash -LiteralPath $file.FullName).Hash }
    }
    foreach ($path in $binaryHashes.Keys) { if ((Get-FileHash -LiteralPath $path).Hash -ne $binaryHashes[$path]) { throw "Host binary changed concurrently: $path" } }
    if ((Get-FileHash -LiteralPath $hostManifestPath).Hash -ne $oldManifestHash) { throw 'Host manifest changed concurrently.' }
    $json = ConvertTo-Json -InputObject $next -Depth 5
    $nextManifest = Join-Path $hostRoot ('.host-manifest-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($nextManifest, $json, [Text.UTF8Encoding]::new($false))
        [IO.File]::Replace($nextManifest, $hostManifestPath, ($nextManifest + '.bak'))
    } finally { foreach ($temporaryFile in @($nextManifest, ($nextManifest + '.bak'))) { if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -ErrorAction SilentlyContinue } } }
} catch {
    foreach ($name in $backup.Keys) {
        $path = Join-Path $target $name
        if ($null -eq $backup[$name]) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
        else { [IO.File]::WriteAllBytes($path, $backup[$name]) }
    }
    throw
}
Write-Output "PASS: Malachite updated in $target; host EXE/DLL unchanged, no host build."
} finally { $updateLock.Dispose() }
