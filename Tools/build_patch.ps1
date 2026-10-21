<#
.SYNOPSIS
  T59：基于冻结 baseline 生成独立 101 补丁目录；AOT 差异非0退出。
.PARAMETER RepoRoot
  仓库根。
.PARAMETER UnityEditorPath
  Unity.exe。
.PARAMETER OutputDir
  发布根（其下 101/ 与 version.json）。
.PARAMETER ResVersion
  默认 101。
.PARAMETER ConfigVersion
  默认与 ResVersion 相同。
.PARAMETER BaseBuildManifest
  T58 baseline-manifest.json 路径。
.PARAMETER SkipUnity
  仅跑 ValidateBaseline（须另有已 Stage 的 101 内容时不用）。
#>
param(
    [string]$RepoRoot = 'E:\Workbench\UnityWorkBench\Farming',
    [string]$UnityEditorPath = 'D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe',
    [string]$OutputDir = '',
    [int]$ResVersion = 101,
    [int]$ConfigVersion = 101,
    [string]$BaseBuildManifest = '',
    [string]$AppVersion = '0.3.0',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepoRoot)
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD\Publish'
}
$out = [IO.Path]::GetFullPath($OutputDir)
if ([string]::IsNullOrWhiteSpace($BaseBuildManifest)) {
    $BaseBuildManifest = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD\Freeze100\baseline-manifest.json'
}
$base = [IO.Path]::GetFullPath($BaseBuildManifest)
$evidence = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD'
New-Item -ItemType Directory -Path $out, $evidence -Force | Out-Null

if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity不存在: $UnityEditorPath"
}
if (-not (Test-Path -LiteralPath $base -PathType Leaf)) {
    throw "缺少 BaseBuildManifest: $base"
}
$existing = Get-Process Unity -ErrorAction SilentlyContinue
if ($existing) {
    throw ("已有Unity进程 PID=" + (($existing | ForEach-Object Id) -join ',') + "；禁止盲开第二实例")
}

$method = if ($ValidateOnly) { 'Game.Editor.Build.PatchBuild.ValidateBaselineOnly' } else { 'Game.Editor.Build.PatchBuild.BuildPatch' }
$log = Join-Path $evidence $(if ($ValidateOnly) { 'unity-patch-validate.log' } else { 'unity-patchbuild.log' })
# 单字符串 ArgumentList：含空格路径必须整体引号，避免 Start-Process 拆参。
$argLine = '-batchmode -nographics'
$argLine += ' -projectPath "' + $root + '"'
$argLine += ' -executeMethod ' + $method
$argLine += ' "--m1-output=' + $out + '"'
$argLine += ' --m1-res-version=' + $ResVersion
$argLine += ' --m1-config-version=' + $ConfigVersion
$argLine += ' --m1-app-version=' + $AppVersion
$argLine += ' "--m1-base-manifest=' + $base + '"'
$argLine += ' -logFile "' + $log + '"'
Set-Content -LiteralPath (Join-Path $evidence 'build-patch-command.txt') -Value ($UnityEditorPath + ' ' + $argLine) -Encoding utf8
$p = Start-Process -FilePath $UnityEditorPath -ArgumentList $argLine -WorkingDirectory $root -WindowStyle Hidden -PassThru
$p.WaitForExit()
Set-Content -LiteralPath (Join-Path $evidence 'build-patch-exit.txt') -Value $p.ExitCode
if ($p.ExitCode -ne 0) {
    throw "build_patch exit=$($p.ExitCode) log=$log"
}
Write-Output "OK Publish=$out Res=$ResVersion"
exit 0
