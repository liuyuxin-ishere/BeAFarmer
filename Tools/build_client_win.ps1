<#
.SYNOPSIS
  T58：gen_proto/gen_config、xLua GenAll、Yoo、标准 Win64 IL2CPP 基包与完整指纹。
#>
param(
    [string]$RepoRoot = 'E:\Workbench\UnityWorkBench\Farming',
    [string]$UnityEditorPath = 'D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe',
    [string]$OutputDir = '',
    [string]$AppVersion = '0.3.0',
    [int]$ResVersion = 100,
    [string]$ProtocPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepoRoot)
if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity不存在: $UnityEditorPath"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    # D01：真实带空格输出目录
    $OutputDir = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD\Freeze 100 Final'
}
$out = [IO.Path]::GetFullPath($OutputDir)
$evidence = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD'
New-Item -ItemType Directory -Path $out, $evidence -Force | Out-Null

function Resolve-Protoc {
    param([string]$Explicit)
    $list = @()
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) { $list += $Explicit }
    $list += @(
        (Join-Path $root 'Tools\protoc\bin\protoc.exe'),
        'C:\Program Files\Protobuf\bin\protoc.exe',
        'D:\Users\Lenovo\anaconda3\Library\bin\protoc.exe'
    )
    $cmd = Get-Command protoc -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { $list += $cmd.Source }
    foreach ($c in $list) {
        if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return [IO.Path]::GetFullPath($c) }
    }
    return $null
}

function Invoke-UnityPhase([string]$Phase, [string]$LogName) {
    $existing = Get-Process Unity -ErrorAction SilentlyContinue
    if ($existing) {
        throw ("已有Unity进程 PID=" + (($existing | ForEach-Object Id) -join ',') + "；禁止盲开第二实例")
    }
    $log = Join-Path $evidence $LogName
    $argLine = '-batchmode -nographics'
    $argLine += ' -projectPath "' + $root + '"'
    $argLine += ' -executeMethod Game.Editor.Build.ClientBuild.BuildWindows'
    $argLine += ' "--m1-output=' + $out + '"'
    $argLine += ' --m1-app-version=' + $AppVersion
    $argLine += ' --m1-res-version=' + $ResVersion
    $argLine += ' --m1-phase=' + $Phase
    $argLine += ' -logFile "' + $log + '"'
    Set-Content -LiteralPath (Join-Path $evidence ("build-client-$Phase-command.txt")) -Value ($UnityEditorPath + ' ' + $argLine) -Encoding utf8
    $p = Start-Process -FilePath $UnityEditorPath -ArgumentList $argLine -WorkingDirectory $root -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath (Join-Path $evidence ("build-client-$Phase.pid")) -Value $p.Id
    $p.WaitForExit()
    Set-Content -LiteralPath (Join-Path $evidence ("build-client-$Phase-exit.txt")) -Value $p.ExitCode
    if ($p.ExitCode -ne 0) {
        throw "Unity phase=$Phase exit=$($p.ExitCode) log=$log"
    }
}

$protoc = Resolve-Protoc -Explicit $ProtocPath
if (-not $protoc) {
    throw 'protoc 未找到（已查 Tools/protoc、Program Files、anaconda、PATH）；拒绝跳过 gen_proto'
}
Write-Output "protoc=$protoc"
& (Join-Path $root 'Tools\gen_proto.ps1') -RepoRoot $root -ProtocPath $protoc
if ($LASTEXITCODE -ne 0) { throw "gen_proto exit=$LASTEXITCODE" }

$luban = Join-Path $root 'Tools\Luban\Luban\Luban.dll'
if (-not (Test-Path -LiteralPath $luban)) { throw "缺少 Luban: $luban" }
powershell.exe -NoProfile -File (Join-Path $root 'Tools\gen_config.ps1') -RepoRoot $root -SourceDir (Join-Path $root 'Shared\config') -ExporterPath $luban -ConfigVersion $ResVersion
if ($LASTEXITCODE -ne 0) { throw "gen_config exit=$LASTEXITCODE" }

Invoke-UnityPhase -Phase 'boot' -LogName 'unity-clientbuild-boot.log'
Invoke-UnityPhase -Phase 'gen' -LogName 'unity-clientbuild-gen.log'
Start-Sleep -Seconds 2
Invoke-UnityPhase -Phase 'yoo' -LogName 'unity-clientbuild-yoo.log'
Invoke-UnityPhase -Phase 'player' -LogName 'unity-clientbuild-player.log'

$exe = Join-Path $out 'Player\Farming.exe'
$manifest = Join-Path $out 'baseline-manifest.json'
$fp = Join-Path $out 'baseline-fingerprint.json'
if (-not (Test-Path -LiteralPath $exe)) { throw "缺少 Player: $exe" }
if (-not (Test-Path -LiteralPath $manifest)) { throw "缺少 baseline: $manifest" }
if (-not (Test-Path -LiteralPath $fp)) { throw "缺少 fingerprint: $fp" }
Write-Output "OK OutputDir=$out"
Write-Output "Player=$exe"
Write-Output "Baseline=$manifest"
Write-Output "Fingerprint=$fp"
exit 0
