<# Current-source E02/E03/E08/E10: existing foundation tests, no generated test substitutes. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$dir=Join-Path $PSScriptRoot 'Foundation-Final'
if(Test-Path -LiteralPath $dir){throw 'Do not overwrite a test report.'}
[IO.Directory]::CreateDirectory($dir)|Out-Null
if(Get-Process Unity -ErrorAction SilentlyContinue){throw 'Unity already running.'}
$filter='NetClientLifecycleTests;NetClientPendingTests;TcpTransportTests;TcpTransportReceiveTests;OpIdStoreTests;EventBusTests;ObjectPoolTests;TimerServiceTests;FileLoggerTests'
$args=@('-batchmode','-nographics','-projectPath',$RepoRoot,'-runTests','-testPlatform','EditMode','-testFilter',$filter,
    '-testResults',(Join-Path $dir 'results.xml'),'-logFile',(Join-Path $dir 'unity.log'))
$line=($args|ForEach-Object {Quote-ProcessArg $_}) -join ' '
Write-AtomicUtf8 (Join-Path $dir 'command.txt') $line
$p=Start-Process 'D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe' -ArgumentList $line -WorkingDirectory $RepoRoot -WindowStyle Hidden -PassThru
$p.WaitForExit()
Write-AtomicUtf8 (Join-Path $dir 'exit.txt') ([string]$p.ExitCode)
[xml]$xml=[IO.File]::ReadAllText((Join-Path $dir 'results.xml'))
$run=$xml.'test-run'
if($p.ExitCode -ne 0 -or [int]$run.total -lt 1 -or [int]$run.failed -ne 0 -or [int]$run.skipped -ne 0){throw 'Foundation regression failed.'}
Write-AtomicUtf8 (Join-Path $dir 'summary.json') (@{passed=$true;total=[int]$run.total;failed=[int]$run.failed;skipped=[int]$run.skipped;exit=$p.ExitCode}|ConvertTo-Json)
Write-Output "PASS foundation tests: $($run.total)"
