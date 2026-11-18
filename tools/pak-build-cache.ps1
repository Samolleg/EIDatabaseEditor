#requires -Version 7.6.10
#requires -PSEdition Core
# A receipt skips MSBuild only after the reviewed-source check has passed.
function Get-PakBuildHash([string]$Path) {
    $stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create()
    try{([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}
    finally{$stream.Dispose();$sha.Dispose()}
}
function Get-PakBuildIdentity([string[]]$Files) {
    (@($Files | Sort-Object -Unique | ForEach-Object {$_+'='+(Get-PakBuildHash $_)}) -join "`n")
}
function Test-PakBuildReceipt([string]$Receipt,[string]$Identity,[string[]]$Outputs) {
    if(![IO.File]::Exists($Receipt)){return $false}
    try{
        $data=[IO.File]::ReadAllText($Receipt)|ConvertFrom-Json
        return $data.version -eq 1 -and $data.inputs -ceq $Identity -and
            $data.outputs -ceq (Get-PakBuildIdentity $Outputs)
    }catch{return $false}
}
function Save-PakBuildReceipt([string]$Receipt,[string]$Identity,[string[]]$Outputs) {
    $text=[ordered]@{version=1;inputs=$Identity;outputs=(Get-PakBuildIdentity $Outputs)}|ConvertTo-Json
    [IO.File]::WriteAllText($Receipt,$text,[Text.UTF8Encoding]::new($false))
}
function Enter-PakBuildLock([string]$Path) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        try{return [IO.File]::Open($Path,'OpenOrCreate','ReadWrite','None')}
        catch [IO.IOException]{
            if(($_.Exception.HResult -band 0xffff) -notin 32,33){throw}
            if($timer.Elapsed.TotalSeconds -gt 120){throw 'Timed out waiting for the native PAK build.'}
            Start-Sleep -Milliseconds 200
        }
    }
}
