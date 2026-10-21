# Shared, local-only Native acceptance helpers. No credentials are printed.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-InRoot([string]$Path,[string]$OwnedRoot) {
    $full=[IO.Path]::GetFullPath($Path)
    $base=[IO.Path]::GetFullPath($OwnedRoot).TrimEnd('\')
    if($full -ne $base -and -not $full.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw "Path outside owned root: $full"
    }
    return $full
}

# Quote a single Windows process argument. Test paths never contain literal quotes.
function Quote-ProcessArg([string]$Value) {
    if($Value.Contains('"')){throw 'Literal quotes are not supported in test paths.'}
    return '"'+$Value+'"'
}

function Write-AtomicUtf8([string]$Path,[string]$Value) {
    $tmp=$Path+'.tmp'
    [IO.File]::WriteAllText($tmp,$Value,[Text.UTF8Encoding]::new($false))
    if(Test-Path -LiteralPath $Path){[IO.File]::Replace($tmp,$Path,$Path+'.previous')}
    else{[IO.File]::Move($tmp,$Path)}
}

function Get-FreePort {
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    try{$listener.Start();return ([Net.IPEndPoint]$listener.LocalEndpoint).Port}
    finally{$listener.Stop()}
}

function New-NativeFixture([string]$RepoRoot,[string]$RunDirectory,[string]$PlayerExe,[string]$ContentRoot) {
    $repo=[IO.Path]::GetFullPath($RepoRoot)
    $run=Assert-InRoot $RunDirectory $repo
    if(Test-Path -LiteralPath $run){throw "Run directory already exists: $run"}
    [IO.Directory]::CreateDirectory($run)|Out-Null
    $accept=Join-Path $run 'client'
    [IO.Directory]::CreateDirectory($accept)|Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $accept 'commands'))|Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $accept 'acks'))|Out-Null
    $user='m1'+[guid]::NewGuid().ToString('N').Substring(0,10)
    $password='Pw'+[guid]::NewGuid().ToString('N').Substring(0,16)
    [IO.File]::WriteAllLines((Join-Path $accept 'creds.private'),@($user,$password,'FinalNick'),[Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        Repo=$repo; Run=$run; Accept=$accept; Exe=[IO.Path]::GetFullPath($PlayerExe)
        Content=[IO.Path]::GetFullPath($ContentRoot); Database=(Join-Path $run 'players.db')
        StopFile=(Join-Path $run 'server.stop'); Server=$null; Player=$null
        PlayerStarted=[datetime]::MinValue; Tcp=(Get-FreePort); Http=(Get-FreePort)
        PlayerLog=''
        CommandNumber=0; Phase='setup'; Actions=[Collections.Generic.List[object]]::new()
    }
}

function Save-Action($Fixture,[string]$Name,$Actual) {
    $record=[ordered]@{utc=[datetime]::UtcNow.ToString('o');phase=$Fixture.Phase;name=$Name;actual=$Actual}
    $Fixture.Actions.Add($record)
    [IO.File]::AppendAllText((Join-Path $Fixture.Run 'actions.jsonl'),($record|ConvertTo-Json -Depth 15 -Compress)+"`n",[Text.UTF8Encoding]::new($false))
}

# All waits fail explicitly. Each polling sleep is short; the API caller can report progress.
function Wait-NativeCondition($Fixture,[string]$Name,[scriptblock]$Condition,[int]$Seconds=60) {
    $deadline=[datetime]::UtcNow.AddSeconds($Seconds)
    do {
        if(& $Condition){Save-Action $Fixture $Name 'passed';return}
        if($Fixture.Player -and $Fixture.Player.HasExited){throw "${Name}: Player exited $($Fixture.Player.ExitCode)"}
        Start-Sleep -Milliseconds 200
    }while([datetime]::UtcNow -lt $deadline)
    $state=Get-NativeState $Fixture
    Save-Action $Fixture ($Name+'-timeout') $state
    throw "Timed out waiting for $Name"
}

function Get-NativeState($Fixture) {
    $file=Join-Path $Fixture.Accept 'accept-state.txt'
    $state=@{}
    if(-not (Test-Path -LiteralPath $file)){return $state}
    try {
        foreach($line in [IO.File]::ReadAllLines($file)){
            $pair=$line.Split('=',2)
            if($pair.Count -eq 2){$state[$pair[0]]=$pair[1]}
        }
        if(-not $state.ContainsKey('utc') -or [datetime]::Parse($state.utc).ToUniversalTime() -lt $Fixture.PlayerStarted){return @{}}
    }catch [IO.IOException]{return @{}}
    return $state
}

function Send-NativeCommand($Fixture,[string]$Operation,[int]$Seconds=25) {
    $Fixture.CommandNumber++
    $id=('{0:d4}' -f $Fixture.CommandNumber)
    $file=Join-Path $Fixture.Accept "commands\$id.cmd"
    Write-AtomicUtf8 $file ($Operation+"`n")
    $ack=Join-Path $Fixture.Accept "acks\$id.ack"
    Wait-NativeCondition $Fixture ('ack-'+$Operation) {Test-Path -LiteralPath $ack} $Seconds
    $text=[IO.File]::ReadAllText($ack)
    if($text -notmatch '(?m)^result=ok\r?$'){throw "Command failed: $Operation ($text)"}
    Save-Action $Fixture ('command-'+$Operation) 'ok'
}

function Set-NativeVersion($Fixture,[int]$Version) {
    $source=Join-Path $Fixture.Content "$Version\version.json"
    $manifest=[IO.File]::ReadAllText($source)|ConvertFrom-Json
    if([int]$manifest.ResVersion -ne $Version){throw 'Incorrect version manifest.'}
    $manifest.BaseUrl="http://localhost:$($Fixture.Http)/$Version/"
    Write-AtomicUtf8 (Join-Path $Fixture.Content 'version.json') ($manifest|ConvertTo-Json -Depth 10)
    Save-Action $Fixture 'atomic-version-pointer' $Version
}

function Start-NativeServer($Fixture,[string]$ConfigDirectory,[int]$ConfigVersion,[int]$ProtocolVersion=1) {
    if($Fixture.Server -and -not $Fixture.Server.HasExited){throw 'Fixture server is already running.'}
    if(Test-Path -LiteralPath $Fixture.StopFile){Remove-Item -LiteralPath (Assert-InRoot $Fixture.StopFile $Fixture.Run)}
    $logs=Join-Path $Fixture.Run ('server-'+$Fixture.Phase)
    [IO.Directory]::CreateDirectory($logs)|Out-Null
    $args=@('-NoProfile','-File',(Join-Path $Fixture.Repo 'Tools/run_server.ps1'),
        '-RepoRoot',$Fixture.Repo,'-DatabasePath',$Fixture.Database,'-TcpPort',[string]$Fixture.Tcp,
        '-HttpPort',[string]$Fixture.Http,'-ContentRoot',$Fixture.Content,'-ConfigDirectory',$ConfigDirectory,
        '-ConfigVersion',[string]$ConfigVersion,'-RequiredConfigVersion','100','-ResVersion','100','-ProtoVersion',[string]$ProtocolVersion,
        '-LogDirectory',$logs,'-StopFile',$Fixture.StopFile,'-BuildArtifactsDirectory',
        (Join-Path $Fixture.Repo 'Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts'),'-SkipBuild')
    $line=($args|ForEach-Object {Quote-ProcessArg $_}) -join ' '
    $Fixture.Server=Start-Process -FilePath pwsh -ArgumentList $line -WorkingDirectory $Fixture.Repo -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'host.stdout') -RedirectStandardError (Join-Path $logs 'host.stderr')
    $deadline=[datetime]::UtcNow.AddSeconds(30)
    do {
        if($Fixture.Server.HasExited){throw "Server exited $($Fixture.Server.ExitCode)"}
        try {
            $result=Invoke-WebRequest -Uri "http://localhost:$($Fixture.Http)/version.json" -NoProxy -TimeoutSec 1
            if($result.StatusCode -eq 200){Save-Action $Fixture 'server-started' @{wrapperPid=$Fixture.Server.Id;database=$Fixture.Database;tcp=$Fixture.Tcp;http=$Fixture.Http};return}
        }catch{}
        Start-Sleep -Milliseconds 200
    }while([datetime]::UtcNow -lt $deadline)
    throw 'Server did not become ready.'
}

function Start-NativePlayer($Fixture) {
    if($Fixture.Player -and -not $Fixture.Player.HasExited){throw 'Fixture Player is already running.'}
    # An interrupted command without an ack must not be replayed on a new process.
    $commandDir=Assert-InRoot (Join-Path $Fixture.Accept 'commands') $Fixture.Run
    foreach($pending in @(Get-ChildItem -LiteralPath $commandDir -File -Filter '*.cmd')){
        Remove-Item -LiteralPath (Assert-InRoot $pending.FullName $Fixture.Run)
    }
    $Fixture.PlayerStarted=[datetime]::UtcNow
    $Fixture.PlayerLog=Join-Path $Fixture.Run ('player-'+[datetime]::UtcNow.ToString('HHmmssfff')+'.log')
    $args=@("--m1-accept-root=$($Fixture.Accept)","--m1-accept-http=http://localhost:$($Fixture.Http)/",
        '--m1-accept-tcp-host=127.0.0.1',"--m1-accept-tcp-port=$($Fixture.Tcp)",
        '-screen-width','1280','-screen-height','720','-screen-fullscreen','0','-logFile',$Fixture.PlayerLog)
    $line=($args|ForEach-Object {Quote-ProcessArg $_}) -join ' '
    $Fixture.Player=Start-Process -FilePath $Fixture.Exe -ArgumentList $line -WorkingDirectory (Split-Path $Fixture.Exe) -PassThru
    Save-Action $Fixture 'player-started' @{pid=$Fixture.Player.Id;exe=$Fixture.Exe;clientRoot=$Fixture.Accept}
}

function Stop-NativePlayer($Fixture) {
    if(-not $Fixture.Player -or $Fixture.Player.HasExited){return}
    try{Send-NativeCommand $Fixture 'quit' 10}catch{}
    if(-not $Fixture.Player.WaitForExit(10000)){
        $identity=Get-CimInstance Win32_Process -Filter ("ProcessId="+$Fixture.Player.Id)
        if($identity -and [string]::Equals($identity.ExecutablePath,$Fixture.Exe,[StringComparison]::OrdinalIgnoreCase) -and
            ([string]$identity.CommandLine).Contains($Fixture.Accept)){
            Stop-Process -Id $Fixture.Player.Id -Force
        }else{throw 'Refusing to stop a process with a different identity.'}
    }
    Save-Action $Fixture 'player-stopped' $Fixture.Player.ExitCode
}

function Stop-NativeServer($Fixture) {
    if(-not $Fixture.Server -or $Fixture.Server.HasExited){return}
    [IO.File]::WriteAllText($Fixture.StopFile,'stop')
    if(-not $Fixture.Server.WaitForExit(20000)){throw 'Owned Server did not stop after its stop file.'}
    Save-Action $Fixture 'server-stopped' $Fixture.Server.ExitCode
}

function Save-NativeState($Fixture,[string]$Name) {
    $state=@{}
    for($readAttempt=0;$readAttempt -lt 10;$readAttempt++){
        $state=Get-NativeState $Fixture
        if($state.Count -gt 0){break}
        Start-Sleep -Milliseconds 100
    }
    foreach($key in @('utc','auth','entry','committed','winLogin','winHome','resVersion','downloadFiles','luaModel')){
        if(-not $state.ContainsKey($key)){throw "Snapshot $Name is missing $key"}
    }
    Write-AtomicUtf8 (Join-Path $Fixture.Run ($Name+'.json')) ($state|ConvertTo-Json -Depth 20)
    Save-Action $Fixture $Name $state
    return $state
}

function Get-InstallHashes([string]$Exe) {
    $dir=Split-Path $Exe
    return [ordered]@{exe=(Get-FileHash -LiteralPath $Exe).Hash;
        gameAssembly=(Get-FileHash -LiteralPath (Join-Path $dir 'GameAssembly.dll')).Hash;
        metadata=(Get-FileHash -LiteralPath (Join-Path $dir 'Farming_Data/il2cpp_data/Metadata/global-metadata.dat')).Hash}
}
