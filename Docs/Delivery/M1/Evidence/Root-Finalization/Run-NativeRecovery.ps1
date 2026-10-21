<# Real Native C01-C12: outage/backoff, same DB restart, owned server crash, expiry, replacement login. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$repo=[IO.Path]::GetFullPath($RepoRoot)
$base=Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$seed=Join-Path $PSScriptRoot 'Native-20261021-141638-780'
$run=Join-Path $PSScriptRoot ('Recovery-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$f=New-NativeFixture $repo $run (Join-Path $base 'Player/Farming.exe') (Join-Path $run 'cdn')
$b=$null
[IO.Directory]::CreateDirectory((Join-Path $f.Content '100'))|Out-Null
Get-ChildItem -LiteralPath (Join-Path $base 'Publish/100') -File|ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $f.Content ('100/'+$_.Name))
}
# The source DB is from the completed, closed Native account test, never a live DB.
Copy-Item -LiteralPath (Join-Path $seed 'players.db') -Destination $f.Database
Copy-Item -LiteralPath (Join-Path $seed 'client/creds.private') -Destination (Join-Path $f.Accept 'creds.private') -Force
$probe=Join-Path $PSScriptRoot 'fixture-artifacts/bin/ProtocolFixture/release/ProtocolFixture.dll'
dotnet $probe prepare $f.Database (Join-Path $run 'prepared-saved-state.json') $PSScriptRoot
if($LASTEXITCODE -ne 0){throw 'Production PlayerStore save/reopen fixture failed.'}

function Model-Value($state){return (($state['luaModel']|ConvertFrom-Json)|ConvertTo-Json -Depth 30 -Compress)}
function Wait-Home([string]$Name){
    Wait-NativeCondition $f $Name {
        $s=Get-NativeState $f
        $s['auth'] -eq 'True' -and $s['mask'] -eq 'False' -and $s['winHome'] -eq 'True' -and $s['hudNick'] -eq 'FinalNick'
    } 95
}
try {
    $f.Phase='C01-R1'
    Set-NativeVersion $f 100
    Start-NativeServer $f (Join-Path $base 'ServerConfig100') 100
    Start-NativePlayer $f
    Wait-NativeCondition $f 'initial-login-ready' {(Get-NativeState $f)['winLogin'] -eq 'True'}
    Send-NativeCommand $f 'fill-creds'
    Send-NativeCommand $f 'login'
    Wait-Home 'initial-home'
    $r1=Save-NativeState $f 'C01-R1'
    $model1=Model-Value $r1
    $data=$r1['luaModel']|ConvertFrom-Json
    if([int]$data.info.gold -ne 201 -or @($data.bag).Count -ne 1 -or $data.bag[0].count -ne 7){throw 'Prepared full business data was not loaded into real Lua model.'}
    $token=Join-Path $f.Accept 'tokens/m1-token.txt'
    $tokenHash=(Get-FileHash -LiteralPath $token).Hash
    python (Join-Path $PSScriptRoot 'db_fixture.py') snapshot --database $f.Database --output (Join-Path $run 'C01-R1-database.json')
    if($LASTEXITCODE -ne 0){throw 'R1 database snapshot failed.'}

    $f.Phase='C02-C03-outage'
    Stop-NativeServer $f
    Wait-NativeCondition $f 'disconnect-mask' {(Get-NativeState $f)['mask'] -eq 'True'}
    [void](Save-NativeState $f 'C02-mask')
    Send-NativeCommand $f 'screenshot:reconnect_mask'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $lastAttempt=-1
    while($watch.Elapsed.TotalSeconds -lt 75){
        $state=Get-NativeState $f
        if($state.ContainsKey('reconnectAttempt') -and [int]$state['reconnectAttempt'] -ne $lastAttempt){
            $lastAttempt=[int]$state['reconnectAttempt']
            Save-Action $f 'observed-reconnect-attempt' @{elapsedSeconds=$watch.Elapsed.TotalSeconds;attempt=$lastAttempt;running=$state['reconnectRunning'];mask=$state['mask']}
        }
        if($f.Player.HasExited){throw 'Native Player exited during outage.'}
        Start-Sleep -Milliseconds 500
    }
    # Unity retains the log handle. Permit its writer while reading a point-in-time snapshot.
    $stream=[IO.FileStream]::new($f.PlayerLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $reader=[IO.StreamReader]::new($stream)
    try{$lines=$reader.ReadToEnd() -split "`n"}finally{$reader.Dispose()}
    $backoffs=@(foreach($line in $lines){if($line -match '\[ReconnectService\].*backoff-wait-seconds=(\d+)'){[int]$Matches[1]}})
    if($backoffs.Count -lt 6 -or (($backoffs|Select-Object -First 6)-join ',') -ne '1,2,4,8,16,30'){
        throw "Wrong actual Native backoff sequence: $($backoffs -join ',')"
    }
    Write-AtomicUtf8 (Join-Path $run 'C03-real-backoff.json') (@{elapsedSeconds=$watch.Elapsed.TotalSeconds;observed=$backoffs}|ConvertTo-Json)

    $f.Phase='C04-restore'
    Start-NativeServer $f (Join-Path $base 'ServerConfig100') 100
    Wait-Home 'token-auto-restore'
    $restored=Save-NativeState $f 'C04-restored'
    if((Model-Value $restored) -ne $model1 -or (Get-FileHash -LiteralPath $token).Hash -ne $tokenHash){throw 'Token restore changed full snapshot or issued a different token.'}
    Send-NativeCommand $f 'screenshot:restored_home'
    python (Join-Path $PSScriptRoot 'db_fixture.py') snapshot --database $f.Database --output (Join-Path $run 'C05-R2-database.json')
    if($LASTEXITCODE -ne 0){throw 'R2 save check failed.'}
    $db=Get-Content -LiteralPath (Join-Path $run 'C05-R2-database.json') -Raw|ConvertFrom-Json
    if(@($db.savedSnapshots).Count -ne 1 -or $db.savedSnapshots[0].savedRevision -lt 1){throw 'No real committed saved revision.'}
    $r2=Save-NativeState $f 'C05-R2'

    $f.Phase='C06-crash-owned-server'
    $owned=@(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'"|Where-Object {
        ([string]$_.CommandLine).Contains($f.Database) -and [string]$_.CommandLine -match 'CloudIsle.Server.dll'
    })
    if($owned.Count -ne 1 -or [int]$owned[0].ParentProcessId -ne $f.Server.Id){throw 'Owned server identity check failed before forced termination.'}
    Save-Action $f 'force-stop-owned-server' @{pid=$owned[0].ProcessId;parent=$f.Server.Id;database=$f.Database;expectedExe=$owned[0].ExecutablePath}
    Stop-Process -Id $owned[0].ProcessId -Force
    if(-not $f.Server.WaitForExit(10000)){throw 'Owned wrapper did not observe server crash.'}
    Wait-NativeCondition $f 'crash-disconnect-mask' {(Get-NativeState $f)['mask'] -eq 'True'}
    $f.Phase='C07-crash-restart'
    Start-NativeServer $f (Join-Path $base 'ServerConfig100') 100
    Wait-Home 'crash-auto-restore'
    $crashRestored=Save-NativeState $f 'C07-restored-R2'
    if((Model-Value $crashRestored) -ne (Model-Value $r2) -or (Get-FileHash -LiteralPath $token).Hash -ne $tokenHash){throw 'Crash restore changed committed data or token.'}

    $f.Phase='C08-C10-expiry'
    python (Join-Path $PSScriptRoot 'db_fixture.py') expire --database $f.Database --output (Join-Path $run 'C08-expired-records.json')
    if($LASTEXITCODE -ne 0){throw 'Expiration fixture failed.'}
    Send-NativeCommand $f 'drop-socket'
    Wait-NativeCondition $f '204-returns-login' {
        $s=Get-NativeState $f
        $s['lastBiz'] -eq '204' -and $s['winLogin'] -eq 'True' -and $s['auth'] -eq 'False' -and $s['hasToken'] -eq 'False' -and $s['reconnectRunning'] -eq 'False'
    }
    [void](Save-NativeState $f 'C09-expired-return-login')
    Send-NativeCommand $f 'fill-creds'
    Send-NativeCommand $f 'login'
    Wait-Home 'new-login-after-expiry'
    [void](Save-NativeState $f 'C10-relogin')
    $privateOld=Join-Path $run 'old-token.private'
    Copy-Item -LiteralPath $token -Destination $privateOld

    $f.Phase='C11-replacement'
    $b=New-NativeFixture $repo (Join-Path $run 'second-client') $f.Exe $f.Content
    $b.Tcp=$f.Tcp;$b.Http=$f.Http;$b.Phase='replacement-B'
    Copy-Item -LiteralPath (Join-Path $f.Accept 'creds.private') -Destination (Join-Path $b.Accept 'creds.private') -Force
    Start-NativePlayer $b
    Wait-NativeCondition $b 'B-login-ready' {(Get-NativeState $b)['winLogin'] -eq 'True'}
    Send-NativeCommand $b 'fill-creds'
    Send-NativeCommand $b 'login'
    Wait-NativeCondition $b 'B-authenticated-home' {
        $s=Get-NativeState $b;$s['auth'] -eq 'True' -and $s['winHome'] -eq 'True'
    }
    Wait-NativeCondition $f 'A-kicked-return-login' {
        $s=Get-NativeState $f;$s['winLogin'] -eq 'True' -and $s['auth'] -eq 'False' -and $s['hasToken'] -eq 'False' -and $s['reconnectRunning'] -eq 'False'
    }
    [void](Save-NativeState $f 'C11-A-kicked')
    [void](Save-NativeState $b 'C11-B-current')
    Send-NativeCommand $f 'screenshot:kicked_login'
    $f.Phase='C12-old-token'
    dotnet $probe reconnect $privateOld $f.Tcp 1 204 (Join-Path $run 'C12-old-token-rejection.json') $PSScriptRoot
    if($LASTEXITCODE -ne 0){throw 'Old token protocol rejection failed.'}
    Start-Sleep -Seconds 3
    if((Get-NativeState $b)['auth'] -ne 'True' -or (Get-NativeState $f)['auth'] -ne 'False'){
        throw 'Old client or token stole the current login back.'
    }
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;scope='C01-C12';nativeBackoff=$backoffs;committedRevision=$db.savedSnapshots[0].savedRevision;fullSnapshotAndTokenPreserved=$true}|ConvertTo-Json -Depth 12)
}catch{
    Write-AtomicUtf8 (Join-Path $run 'failure.json') (@{passed=$false;phase=$f.Phase;message=$_.Exception.Message;state=(Get-NativeState $f)}|ConvertTo-Json -Depth 25)
    throw
}finally{
    if($b){try{Stop-NativePlayer $b}catch{Save-Action $b 'cleanup-error' $_.Exception.Message}}
    try{Stop-NativePlayer $f}catch{Save-Action $f 'cleanup-error' $_.Exception.Message}
    try{Stop-NativeServer $f}catch{Save-Action $f 'server-cleanup-error' $_.Exception.Message}
}
Write-Output "PASS Native full recovery: $run"
