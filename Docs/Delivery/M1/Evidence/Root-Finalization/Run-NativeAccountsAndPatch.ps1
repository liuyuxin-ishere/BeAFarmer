<#
.SYNOPSIS
  Real Native account lifecycle and same-install/cache 100→101→cached101.
  Every wait checks fresh state from the current process and fails on timeout.
.PARAMETER RepoRoot
  Project root; all test data stays inside this workspace.
#>
param(
    [string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming',
    [string]$PublishRoot=''
)
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$repo=[IO.Path]::GetFullPath($RepoRoot)
$base=Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$published=if($PublishRoot){[IO.Path]::GetFullPath($PublishRoot)}else{Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final Publish R2'}
$run=Join-Path $PSScriptRoot ('Native-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$fixture=New-NativeFixture $repo $run (Join-Path $base 'Player/Farming.exe') (Join-Path $run 'cdn')

function Copy-VersionFiles([string]$Source,[string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination)|Out-Null
    Get-ChildItem -LiteralPath $Source -File|ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination $_.Name)
    }
}
Copy-VersionFiles (Join-Path $base 'Publish/100') (Join-Path $fixture.Content '100')
Copy-VersionFiles (Join-Path $published '101') (Join-Path $fixture.Content '101')

function Canonical($Value) {
    if($null -eq $Value){return $null}
    if($Value -is [pscustomobject]){
        $map=[ordered]@{}
        foreach($property in @($Value.PSObject.Properties|ForEach-Object {$_.Name}|Sort-Object)){$map[$property]=Canonical $Value.$property}
        return $map
    }
    if($Value -is [array]){return ,@($Value|ForEach-Object {Canonical $_})}
    return $Value
}
function Model-Text($State) {
    if($State['luaModel'] -eq '{}'){throw 'Lua model observer did not return business data.'}
    return (Canonical ($State['luaModel']|ConvertFrom-Json))|ConvertTo-Json -Depth 30 -Compress
}
function Wait-Login([int]$Version) {
    Wait-NativeCondition $fixture "login-ready-$Version" {
        $s=Get-NativeState $fixture
        $s['winLogin'] -eq 'True' -and $s['committed'] -eq 'True' -and $s['resVersion'] -eq [string]$Version
    }
}
function Login-Existing([string]$SnapshotName) {
    Send-NativeCommand $fixture 'fill-creds'
    Send-NativeCommand $fixture 'login'
    Wait-NativeCondition $fixture 'authenticated-home' {
        $s=Get-NativeState $fixture
        $s['auth'] -eq 'True' -and $s['winHome'] -eq 'True' -and $s['hudNick'] -eq 'FinalNick' -and $s['luaNick'] -eq 'FinalNick'
    }
    $s=Save-NativeState $fixture $SnapshotName
    if($s['mask'] -ne 'False'){throw 'Home remained behind a reconnect mask.'}
    return $s
}

$before=Get-InstallHashes $fixture.Exe
Write-AtomicUtf8 (Join-Path $run 'install-before.json') ($before|ConvertTo-Json)
$success=$false
try {
    $fixture.Phase='100-account'
    Set-NativeVersion $fixture 100
    Start-NativeServer $fixture (Join-Path $base 'ServerConfig100') 100
    Start-NativePlayer $fixture
    Wait-Login 100
    $initial=Save-NativeState $fixture 'A02-B02-login100'
    if($initial['patchModule'] -ne 'False' -or $initial['globalPatch'] -ne '' -or $initial['winPatch'] -ne 'False'){
        throw '100 unexpectedly contains new patch content.'
    }
    if([int]$initial['downloadFiles'] -le 0){throw 'Fresh100 did not report real bundle downloads.'}
    Send-NativeCommand $fixture 'screenshot:100_login'
    Send-NativeCommand $fixture 'fill-creds'
    Send-NativeCommand $fixture 'register'
    Wait-NativeCondition $fixture 'registration-business-result' {
        $s=Get-NativeState $fixture
        $s['registerMessage'] -match '注册成功|请登录'
    }
    $registered=Save-NativeState $fixture 'A03-register-not-authorized'
    if($registered['auth'] -ne 'False' -or $registered['hasToken'] -ne 'False'){
        throw 'Registration unexpectedly authorized the client.'
    }
    $s1=Login-Existing 'A04-S1-home100'
    $model1=Model-Text $s1
    $id1=($s1['luaModel']|ConvertFrom-Json).info.playerId
    if([string]$id1 -eq '0' -or [string]::IsNullOrEmpty([string]$id1)){throw 'Missing real player identity.'}
    Send-NativeCommand $fixture 'screenshot:100_home'
    Send-NativeCommand $fixture 'logout'
    Wait-NativeCondition $fixture 'logout-to-login' {
        $s=Get-NativeState $fixture
        $s['auth'] -eq 'False' -and $s['hasToken'] -eq 'False' -and $s['winLogin'] -eq 'True' -and $s['winHome'] -eq 'False'
    }
    $loggedOut=Save-NativeState $fixture 'A06-logout'
    $clearModel=$loggedOut['luaModel']|ConvertFrom-Json
    if([string]$clearModel.info.playerId -ne '0' -or $clearModel.info.nickname -ne ''){throw 'Logout did not clear Lua model.'}
    $again=Login-Existing 'A07-login-again'
    if((Model-Text $again) -ne $model1){throw 'Business snapshot changed after logout/login.'}
    Stop-NativePlayer $fixture
    Start-NativePlayer $fixture
    Wait-Login 100
    $reopened=Login-Existing 'A10-reopen-same-install'
    if((Model-Text $reopened) -ne $model1){throw 'Business snapshot changed after restart.'}
    Stop-NativePlayer $fixture

    $fixture.Phase='101-update'
    Set-NativeVersion $fixture 101
    Stop-NativeServer $fixture
    Start-NativeServer $fixture (Join-Path $base 'ServerConfig101') 101
    Start-NativePlayer $fixture
    Wait-Login 101
    $updated=Save-NativeState $fixture 'B06-updated101'
    if($updated['globalPatch'] -ne '热更新验证成功' -or $updated['patchModule'] -ne 'True'){
        throw 'Actual 101 module or Luban Global is missing.'
    }
    if([int]$updated['downloadFiles'] -le 0 -or [int]$updated['downloadFiles'] -ge [int]$initial['downloadFiles']){
        throw '101 did not report a positive differential bundle download.'
    }
    $home101=Login-Existing 'B07-home101'
    if((Model-Text $home101) -ne $model1){throw 'Business snapshot changed during resource update.'}
    Send-NativeCommand $fixture 'open-patchdemo'
    Wait-NativeCondition $fixture 'real-patch-prefab-opened' {(Get-NativeState $fixture)['winPatch'] -eq 'True'}
    Send-NativeCommand $fixture 'screenshot:101_patch_window'
    [void](Save-NativeState $fixture 'B08-real-prefab-and-global')
    Stop-NativePlayer $fixture

    $fixture.Phase='101-cache-reuse'
    Start-NativePlayer $fixture
    Wait-Login 101
    $cached=Save-NativeState $fixture 'B10-cached101'
    $cachedCount=0
    if(-not $cached.ContainsKey('downloadFiles') -or -not [int]::TryParse($cached['downloadFiles'],[ref]$cachedCount) -or $cachedCount -ne 0){
        throw 'Cached101 download observation is missing, invalid, or nonzero.'
    }
    [void](Login-Existing 'B10-cached-home')
    Send-NativeCommand $fixture 'open-patchdemo'
    Wait-NativeCondition $fixture 'cached-patch-prefab-opened' {(Get-NativeState $fixture)['winPatch'] -eq 'True'}
    Stop-NativePlayer $fixture
    $after=Get-InstallHashes $fixture.Exe
    Write-AtomicUtf8 (Join-Path $run 'install-after.json') ($after|ConvertTo-Json)
    foreach($key in $before.Keys){if($before[$key] -ne $after[$key]){throw "Installed file changed: $key"}}
    $success=$true
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;run=$run;installUnchanged=$true;playerId=$id1;initialDownloadFiles=$initial['downloadFiles'];updateDownloadFiles=$updated['downloadFiles'];cachedDownloadFiles=$cached['downloadFiles']}|ConvertTo-Json)
} catch {
    Write-AtomicUtf8 (Join-Path $run 'failure.json') (@{passed=$false;phase=$fixture.Phase;message=$_.Exception.Message;state=(Get-NativeState $fixture)}|ConvertTo-Json -Depth 20)
    throw
} finally {
    try{Stop-NativePlayer $fixture}catch{Save-Action $fixture 'player-cleanup-error' $_.Exception.Message}
    try{Stop-NativeServer $fixture}catch{Save-Action $fixture 'server-cleanup-error' $_.Exception.Message}
}
if(-not $success){exit 1}
Write-Output "PASS Native account/update/cache: $run"
