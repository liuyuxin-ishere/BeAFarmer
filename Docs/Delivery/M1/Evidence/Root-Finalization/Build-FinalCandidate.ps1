<# Rebuild after the terminal-reconnect fix; keep previous candidates and their evidence intact. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$root=[IO.Path]::GetFullPath($RepoRoot)
$base=Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$publish=Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/Root Final Publish R2'
if(Test-Path -LiteralPath $base){throw 'Candidate already exists. Do not overwrite frozen evidence.'}
if(Test-Path -LiteralPath $publish){throw 'Publish root already exists.'}
function Invoke-Recorded([string]$Exe,[string[]]$Arguments,[string]$Name){
    $line=($Arguments|ForEach-Object {Quote-ProcessArg $_}) -join ' '
    $p=Start-Process -FilePath $Exe -ArgumentList $line -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $PSScriptRoot ($Name+'.stdout')) -RedirectStandardError (Join-Path $PSScriptRoot ($Name+'.stderr'))
    $p.WaitForExit()
    Write-AtomicUtf8 (Join-Path $PSScriptRoot ($Name+'.exit.txt')) ([string]$p.ExitCode)
    if($p.ExitCode -ne 0){throw "$Name failed with exit $($p.ExitCode)"}
}
$history=Join-Path $PSScriptRoot 'Before-R2-BuildLogs'
[IO.Directory]::CreateDirectory($history)|Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/StageD') -File|Where-Object {
    $_.Name -match '^(build-client-|unity-clientbuild-|build-patch-|unity-patchbuild)'
}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $history $_.Name)}
Invoke-Recorded 'pwsh' @('-NoProfile','-File',(Join-Path $root 'Tools/build_client_win.ps1'),'-RepoRoot',$root,'-OutputDir',$base,'-ResVersion','100') 'r2-client-build'
Copy-Item -LiteralPath (Join-Path $root 'Server/ConfigLuban') -Destination (Join-Path $base 'ServerConfig100') -Recurse
Invoke-Recorded 'powershell.exe' @('-NoProfile','-File',(Join-Path $root 'Tools/gen_config.ps1'),'-RepoRoot',$root,
    '-SourceDir',(Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/StageD/ConfigSource101'),
    '-ExporterPath',(Join-Path $root 'Tools/Luban/Luban/Luban.dll'),'-ConfigVersion','101') 'r2-config101'
Copy-Item -LiteralPath (Join-Path $root 'Server/ConfigLuban') -Destination (Join-Path $base 'ServerConfig101') -Recurse
Invoke-Recorded 'pwsh' @('-NoProfile','-File',(Join-Path $root 'Tools/build_patch.ps1'),'-RepoRoot',$root,
    '-OutputDir',$publish,'-BaseBuildManifest',(Join-Path $base 'baseline-manifest.json'),'-ResVersion','101','-ConfigVersion','101') 'r2-patch-build'
Invoke-Recorded 'powershell.exe' @('-NoProfile','-File',(Join-Path $root 'Tools/gen_config.ps1'),'-RepoRoot',$root,
    '-SourceDir',(Join-Path $root 'Shared/config'),'-ExporterPath',(Join-Path $root 'Tools/Luban/Luban/Luban.dll'),'-ConfigVersion','100') 'r2-restore-config100'
Write-AtomicUtf8 (Join-Path $PSScriptRoot 'r2-build-result.json') (@{passed=$true;base=$base;publish=$publish;sha=(Get-InstallHashes (Join-Path $base 'Player/Farming.exe'))}|ConvertTo-Json -Depth 6)
Write-Output 'PASS final R2 full build and patch.'
