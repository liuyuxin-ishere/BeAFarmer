<# D08: actual standard patch command rejects an owned, reversible fixed-C# difference. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$root=[IO.Path]::GetFullPath($RepoRoot)
$run=Join-Path $PSScriptRoot ('AotNegative-R2-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
[IO.Directory]::CreateDirectory($run)|Out-Null
$source=Join-Path $root 'Assets/Framework/Account/AccountSession.cs'
$original=[IO.File]::ReadAllBytes($source)
[IO.File]::WriteAllBytes((Join-Path $run 'AccountSession.before.cs.bytes'),$original)
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
$base=Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$exe=Join-Path $base 'Player/Farming.exe'
$before=Get-InstallHashes $exe
$pointer=Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/Root Final Publish R2/version.json'
$pointerHash=(Get-FileHash -LiteralPath $pointer).Hash
try{
    if(Get-Process Unity -ErrorAction SilentlyContinue){throw 'Unity already running.'}
    [IO.File]::WriteAllText($source,"// Owned D08 fixed-code difference; restored byte-for-byte in finally.`n"+[Text.Encoding]::UTF8.GetString($original),[Text.UTF8Encoding]::new($false))
    $output=Join-Path $run 'Attempt'
    $args=@('-NoProfile','-File',(Join-Path $root 'Tools/build_patch.ps1'),'-RepoRoot',$root,'-OutputDir',$output,
        '-BaseBuildManifest',(Join-Path $base 'baseline-manifest.json'),'-ResVersion','101','-ConfigVersion','101')
    $p=Start-Process pwsh -ArgumentList (($args|ForEach-Object {Quote-ProcessArg $_}) -join ' ') -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $run 'stdout.txt') -RedirectStandardError (Join-Path $run 'stderr.txt')
    $p.WaitForExit()
    $report=[IO.File]::ReadAllText((Join-Path $output 'patchbuild-report.txt'))
    if($p.ExitCode -eq 0 -or $report -notmatch 'aot-incompatible:fingerprint-changed:Assets/Framework/Account/AccountSession.cs'){
        throw 'The real patch build did not reject the fixed-C# change.'
    }
    if(Test-Path -LiteralPath (Join-Path $output 'version.json')){throw 'Rejected build published a pointer.'}
    $after=Get-InstallHashes $exe
    foreach($key in $before.Keys){if($before[$key] -ne $after[$key]){throw 'Rejected build changed the Player.'}}
    if((Get-FileHash -LiteralPath $pointer).Hash -ne $pointerHash){throw 'Rejected build changed the published pointer.'}
    Copy-Item -LiteralPath (Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/StageD/unity-patchbuild.log') -Destination (Join-Path $run 'unity.log')
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;scriptExit=$p.ExitCode;rejection=$report.Trim();playerUnchanged=$true;publishedPointerUnchanged=$true;noRejectedPointer=$true;sourceBeforeSha=$sourceHash}|ConvertTo-Json)
}finally{
    [IO.File]::WriteAllBytes($source,$original)
    if((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash){throw 'Fixed-C# source restoration failed.'}
}
Write-Output "PASS fixed-code negative: $run"
