<# Corrupt only an owned CDN copy, use an empty client cache, repair, then click the real Retry. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$repo=[IO.Path]::GetFullPath($RepoRoot)
$base=Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$publish=Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final Publish R2/101'
$run=Join-Path $PSScriptRoot ('BadResource-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$f=New-NativeFixture $repo $run (Join-Path $base 'Player/Farming.exe') (Join-Path $run 'cdn')
$versionDir=Join-Path $f.Content '101'
[IO.Directory]::CreateDirectory($versionDir)|Out-Null
Get-ChildItem -LiteralPath $publish -File|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $versionDir $_.Name)}
$blob=Get-ChildItem -LiteralPath $versionDir -File -Filter '*.bundle'|Sort-Object Length -Descending|Select-Object -First 1
if(-not $blob){throw 'No real bundle found in published version.'}
$blobPath=Assert-InRoot $blob.FullName $run
$good=[IO.File]::ReadAllBytes($blobPath)
$goodHash=(Get-FileHash -LiteralPath $blobPath).Hash
$installBefore=Get-InstallHashes $f.Exe
[IO.File]::WriteAllBytes($blobPath,[byte[]]@(0,1,2,3,4,5,6,7))
$f.Phase='bad-resource'
try {
    Set-NativeVersion $f 101
    Start-NativeServer $f (Join-Path $base 'ServerConfig101') 101
    Start-NativePlayer $f
    Wait-NativeCondition $f 'visible-update-error' {(Get-NativeState $f)['patchRetryVisible'] -eq 'True'} 120
    $failed=Save-NativeState $f 'B12-visible-error-no-activation'
    if($failed['entry'] -ne 'False' -or $failed['committed'] -ne 'False' -or $failed['winLogin'] -ne 'False'){
        throw 'Invalid candidate was activated.'
    }
    Send-NativeCommand $f 'screenshot:download_error'
    # Leave the error visible while the owned CDN copy is repaired.
    Start-Sleep -Seconds 1
    [IO.File]::WriteAllBytes($blobPath,$good)
    if((Get-FileHash -LiteralPath $blobPath).Hash -ne $goodHash){throw 'Resource repair hash differs.'}
    Save-Action $f 'B13-resource-restored' @{file=$blob.Name;sha256=$goodHash}
    Send-NativeCommand $f 'click-patch-retry'
    Wait-NativeCondition $f 'retry-enters-login' {
        $s=Get-NativeState $f
        $s['winLogin'] -eq 'True' -and $s['committed'] -eq 'True' -and $s['entry'] -eq 'True'
    } 90
    $repaired=Save-NativeState $f 'B14-retry-success'
    Send-NativeCommand $f 'screenshot:retry_login'
    $log=[IO.File]::ReadAllText((Join-Path $f.Accept 'native-observe.log'))
    $attempts=@([regex]::Matches($log,'launcher patch-prepare-attempt (\d+)')|ForEach-Object {$_.Groups[1].Value})
    if(($attempts -join ',') -ne '1,2'){throw "Unexpected retry pipelines: $($attempts -join ',')"}
    Stop-NativePlayer $f
    $after=Get-InstallHashes $f.Exe
    foreach($key in $installBefore.Keys){if($installBefore[$key] -ne $after[$key]){throw 'Installed Player changed.'}}
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;bundle=$blob.Name;originalSha256=$goodHash;attempts=$attempts;installationUnchanged=$true}|ConvertTo-Json -Depth 10)
}catch{
    Write-AtomicUtf8 (Join-Path $run 'failure.json') (@{passed=$false;message=$_.Exception.Message;state=(Get-NativeState $f)}|ConvertTo-Json -Depth 20)
    throw
}finally{
    try{Stop-NativePlayer $f}catch{Save-Action $f 'player-cleanup-error' $_.Exception.Message}
    try{Stop-NativeServer $f}catch{Save-Action $f 'server-cleanup-error' $_.Exception.Message}
}
Write-Output "PASS Native corrupt resource/repair/real Retry: $run"
