<# C13: production server requires Proto2, unchanged Native Proto1 must display 106 and stop. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$base=Join-Path $RepoRoot 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$run=Join-Path $PSScriptRoot ('Protocol-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$f=New-NativeFixture $RepoRoot $run (Join-Path $base 'Player/Farming.exe') (Join-Path $run 'cdn')
[IO.Directory]::CreateDirectory((Join-Path $f.Content '100'))|Out-Null
Get-ChildItem -LiteralPath (Join-Path $base 'Publish/100') -File|ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $f.Content ('100/'+$_.Name))
}
try{
    $f.Phase='C13-protocol-mismatch'
    Set-NativeVersion $f 100
    Start-NativeServer $f (Join-Path $base 'ServerConfig100') 100 2
    Start-NativePlayer $f
    Wait-NativeCondition $f 'login-ready' {(Get-NativeState $f)['winLogin'] -eq 'True'}
    Send-NativeCommand $f 'fill-creds'
    Send-NativeCommand $f 'login'
    Wait-NativeCondition $f '106-update-required' {
        $s=Get-NativeState $f
        $s['lastBiz'] -eq '106' -and $s['winLogin'] -eq 'True' -and $s['auth'] -eq 'False' -and
        $s['reconnectRunning'] -eq 'False' -and $s['hasToken'] -eq 'False'
    }
    Start-Sleep -Seconds 3
    $state=Save-NativeState $f 'C13-version-mismatch'
    if($state['registerMessage'] -notmatch '版本|更新'){throw '106 did not display an update message.'}
    if($state['reconnectRunning'] -ne 'False'){throw '106 started an infinite reconnect.'}
    Send-NativeCommand $f 'screenshot:version_update_required'
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;scope='C13';clientProto=1;serverProto=2;responseCode=106;updateMessage=$state['registerMessage'];reconnectRunning=$false}|ConvertTo-Json)
}catch{
    Write-AtomicUtf8 (Join-Path $run 'failure.json') (@{passed=$false;message=$_.Exception.Message;state=(Get-NativeState $f)}|ConvertTo-Json -Depth 20)
    throw
}finally{
    try{Stop-NativePlayer $f}catch{Save-Action $f 'cleanup-error' $_.Exception.Message}
    try{Stop-NativeServer $f}catch{Save-Action $f 'server-cleanup-error' $_.Exception.Message}
}
Write-Output "PASS Native protocol mismatch: $run"
