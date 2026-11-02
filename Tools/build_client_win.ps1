<#
.SYNOPSIS
  T58/T027：gen_proto/gen_config、xLua GenAll、Yoo、标准 Win64 IL2CPP 基包与完整指纹。
  M2：传 -ProfilePath BuildProfiles/M2/base-200.json，输出到唯一候选目录（不覆盖 M1 冻结产物）。
  M1 遗留：省略 ProfilePath 时仍走历史 StageD 默认（相对 RepoRoot，无本机盘符硬编码）。
  所有 Unity phase 显式 -M2IsolateEditorRelay（M2-I001 仍 OPEN）。
#>
param(
    [string]$RepoRoot = '',
    [string]$UnityEditorPath = '',
    [string]$OutputDir = '',
    [string]$ProfilePath = '',
    [string]$AppVersion = '0.3.0',
    [int]$ResVersion = 100,
    [int]$ConfigVersion = 0,
    [string]$ProtocPath = '',
    [int]$UnityPhaseTimeoutSec = 0
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$root = [IO.Path]::GetFullPath($RepoRoot)
if ([string]::IsNullOrWhiteSpace($UnityEditorPath)) {
    throw 'UnityEditorPath required（传入本机 Unity.exe，禁止脚本内硬编码盘符）'
}
if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity不存在: $UnityEditorPath"
}
$UnityEditorPath = [IO.Path]::GetFullPath($UnityEditorPath)

$m2Mode = -not [string]::IsNullOrWhiteSpace($ProfilePath)
$profileFull = $null
$appVer = $AppVersion
$resVer = $ResVersion
$cfgVer = if ($ConfigVersion -gt 0) { $ConfigVersion } else { $ResVersion }
$contentPolicy = ''

if ($m2Mode) {
    if (-not [IO.Path]::IsPathRooted($ProfilePath)) {
        $profileFull = [IO.Path]::GetFullPath((Join-Path $root $ProfilePath))
    } else {
        $profileFull = [IO.Path]::GetFullPath($ProfilePath)
    }
    if (-not (Test-Path -LiteralPath $profileFull -PathType Leaf)) {
        throw "ProfilePath missing: $profileFull"
    }
    $raw = Get-Content -LiteralPath $profileFull -Raw -Encoding UTF8
    if ($raw -notmatch '"appVersion"\s*:\s*"([^"]+)"') { throw 'profile missing appVersion' }
    $appVer = $Matches[1]
    if ($raw -notmatch '"resVersion"\s*:\s*(\d+)') { throw 'profile missing resVersion' }
    $resVer = [int]$Matches[1]
    if ($raw -notmatch '"configVersion"\s*:\s*(\d+)') { throw 'profile missing configVersion' }
    $cfgVer = [int]$Matches[1]
    if ($raw -notmatch '"contentPolicy"\s*:\s*"([^"]+)"') { throw 'profile missing contentPolicy' }
    $contentPolicy = $Matches[1]
    if ($contentPolicy -ne 'M2Formal') {
        throw "T027 build_client_win M2 mode requires contentPolicy=M2Formal, got=$contentPolicy"
    }
    if ($appVer -ne '0.4.0' -or $resVer -ne 200 -or $cfgVer -ne 200) {
        throw "T027 early gate requires app=0.4.0 res/config=200, got app=$appVer res=$resVer cfg=$cfgVer"
    }
    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
        $OutputDir = Join-Path $root ("Builds\M2\" + $stamp + "\Base 200")
    }
    $evidence = Join-Path ([IO.Path]::GetDirectoryName(([IO.Path]::GetFullPath($OutputDir)))) '_build-logs'
} else {
    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        # D01：真实带空格输出目录（相对 RepoRoot，保留 M1 合法流）
        $OutputDir = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD\Freeze 100 Final'
    }
    $evidence = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD'
}

$out = [IO.Path]::GetFullPath($OutputDir)
if ($m2Mode) {
    $allowedRoots = @((Join-Path $root 'Builds/M2'), (Join-Path $root 'Docs/Acceptance/M2'))
    $inside = @($allowedRoots | Where-Object { $out.StartsWith([IO.Path]::GetFullPath($_).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if (-not $inside -or (Test-Path -LiteralPath $out)) { throw "M2 output must be a fresh child of Builds/M2 or Docs/Acceptance/M2: $out" }
}
New-Item -ItemType Directory -Path $out, $evidence -Force | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false

function Escape-WinProcessArgument([string]$value) {
    if ($null -eq $value) { return '""' }
    if ($value -notmatch '[\s"]') { return $value }
    return '"' + ($value -replace '"', '\"') + '"'
}

function New-EscapedArgumentList([string[]]$parts) {
    return (($parts | ForEach-Object { Escape-WinProcessArgument $_ }) -join ' ')
}

function Resolve-Protoc {
    param([string]$Explicit)
    $list = @()
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) { $list += $Explicit }
    $list += @(
        (Join-Path $root 'Tools\protoc\bin\protoc.exe')
    )
    $cmd = Get-Command protoc -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { $list += $cmd.Source }
    foreach ($c in $list) {
        if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return [IO.Path]::GetFullPath($c) }
    }
    return $null
}

function Get-PhaseTimeoutSec([string]$Phase) {
    if ($UnityPhaseTimeoutSec -gt 0) { return $UnityPhaseTimeoutSec }
    switch ($Phase) {
        'boot' { return 1800 }
        'gen' { return 3600 }
        'yoo' { return 3600 }
        'player' { return 10800 }
        default { return 3600 }
    }
}

function Stop-OwnedUnity([int]$ProcessId, [long]$StartTicks, [string]$label) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return }
    if ($proc.StartTime.ToUniversalTime().Ticks -ne $StartTicks -or -not $proc.Path -or
        -not [string]::Equals([IO.Path]::GetFullPath($proc.Path), $UnityEditorPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$label refuse stop: pid=$ProcessId start/path verification failed"
    }
    try { $proc.CloseMainWindow() | Out-Null } catch { }
    if (-not $proc.WaitForExit(20000)) {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
        $proc.WaitForExit(20000) | Out-Null
    }
    if (-not $proc.HasExited) {
        throw "$label still running after owned stop pid=$ProcessId"
    }
}

function Get-UnityProjectRelation([string]$CommandLine, [string]$ProjectRoot) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return 'unknown' }
    $projectArg = [regex]::Match($CommandLine, '(?i)(?:^|\s)-projectPath\s+(?:"(?<quoted>[^"]*)"|(?<plain>[^"\s]+))')
    if (-not $projectArg.Success) { return 'unknown' }
    $value = if ($projectArg.Groups['quoted'].Success) { $projectArg.Groups['quoted'].Value } else { $projectArg.Groups['plain'].Value }
    try {
        if (-not [IO.Path]::IsPathRooted($value)) { return 'unknown' }
        $candidateProject = [IO.Path]::GetFullPath($value).TrimEnd('\','/')
        $requestedProject = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\','/')
        if ([string]::Equals($candidateProject, $requestedProject, [StringComparison]::OrdinalIgnoreCase)) { return 'same' }
        return 'other'
    } catch { return 'unknown' }
}

function Get-BlockingUnityProcesses([string]$ProjectRoot) {
    foreach ($unityProcess in @(Get-Process Unity -ErrorAction SilentlyContinue)) {
        try {
            # CommandLine stays in memory;never log another project's path or arguments.
            $entry = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $unityProcess.Id) -Property ProcessId,ParentProcessId,CreationDate,CommandLine -ErrorAction Stop
            if (-not $entry) { if (-not $unityProcess.HasExited) { $unityProcess }; continue }
            $relation = Get-UnityProjectRelation $entry.CommandLine $ProjectRoot
            if ($relation -eq 'other') { continue }
            if ($relation -eq 'unknown' -and [regex]::IsMatch($entry.CommandLine, '(?i)(?:-assetImportWorker|-job-worker|-name\s+"?AssetImportWorker)')) {
                $parent = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $entry.ParentProcessId) -Property Name,CreationDate,CommandLine -ErrorAction Stop
                if ($parent -and $parent.Name -eq 'Unity.exe' -and $parent.CreationDate -le $entry.CreationDate -and
                    (Get-UnityProjectRelation $parent.CommandLine $ProjectRoot) -eq 'other') { continue }
            }
            $unityProcess
        } catch { if (-not $unityProcess.HasExited) { $unityProcess } }
    }
}

function Invoke-UnityPhase([string]$Phase, [string]$LogName) {
    $existing = @(Get-BlockingUnityProcesses $root)
    if ($existing) {
        throw ("已有Unity进程 PID=" + (($existing | ForEach-Object Id) -join ',') + "；禁止盲开第二实例")
    }
    $log = Join-Path $evidence $LogName
    $timeoutSec = Get-PhaseTimeoutSec $Phase
    $parts = @(
        '-batchmode', '-nographics',
        '-projectPath', $root,
        '-executeMethod', 'Game.Editor.Build.ClientBuild.BuildWindows',
        ("--m1-output=" + $out)
    )
    if ($m2Mode) {
        $parts += ("--m2-profile=" + $profileFull)
        $parts += ("--m2-app-version=" + $appVer)
        $parts += ("--m2-res-version=" + $resVer)
    } else {
        $parts += ("--m1-app-version=" + $appVer)
        $parts += ("--m1-res-version=" + $resVer)
    }
    $parts += ("--m1-phase=" + $Phase)
    # M2-I001 仍 OPEN：每个 Unity phase 显式隔离 EditorRelay。
    $parts += '-M2IsolateEditorRelay'
    $parts += '-logFile'
    $parts += $log
    $argLine = New-EscapedArgumentList $parts
    [IO.File]::WriteAllText((Join-Path $evidence ("build-client-$Phase-command.txt")), ($UnityEditorPath + ' ' + $argLine), $utf8)
    $p = Start-Process -FilePath $UnityEditorPath -ArgumentList $argLine -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $startTicks = $p.StartTime.ToUniversalTime().Ticks
    [IO.File]::WriteAllText((Join-Path $evidence ("build-client-$Phase.pid")), [string]$p.Id, $utf8)
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        [IO.File]::WriteAllText(
            (Join-Path $evidence ("build-client-$Phase-timeout.txt")),
            ("timeoutSec=" + $timeoutSec + "`npid=" + $p.Id + "`nphase=" + $Phase + "`n"),
            $utf8)
        Stop-OwnedUnity -ProcessId $p.Id -StartTicks $startTicks -label ("unity-phase-" + $Phase)
        throw "Unity phase=$Phase timeoutSec=$timeoutSec pid=$($p.Id) log=$log"
    }
    [IO.File]::WriteAllText((Join-Path $evidence ("build-client-$Phase-exit.txt")), [string]$p.ExitCode, $utf8)
    if ($p.ExitCode -ne 0) {
        throw "Unity phase=$Phase exit=$($p.ExitCode) pid=$($p.Id) log=$log"
    }
}

$protoc = Resolve-Protoc -Explicit $ProtocPath
if (-not $protoc) {
    throw 'protoc 未找到（已查 Tools/protoc、PATH）；拒绝跳过 gen_proto'
}
Write-Output "protoc=$protoc"
& (Join-Path $root 'Tools\gen_proto.ps1') -RepoRoot $root -ProtocPath $protoc
if ($LASTEXITCODE -ne 0) { throw "gen_proto exit=$LASTEXITCODE" }

$luban = Join-Path $root 'Tools\Luban\Luban\Luban.dll'
if (-not (Test-Path -LiteralPath $luban)) { throw "缺少 Luban: $luban" }
# ConfigVersion 必须正式匹配 profile（M2=200）；不可重写冻结 M1 配置树以外的目标。
powershell.exe -NoProfile -File (Join-Path $root 'Tools\gen_config.ps1') -RepoRoot $root -SourceDir (Join-Path $root 'Shared\config') -ExporterPath $luban -ConfigVersion $cfgVer
if ($LASTEXITCODE -ne 0) { throw "gen_config exit=$LASTEXITCODE" }

Invoke-UnityPhase -Phase 'boot' -LogName 'unity-clientbuild-boot.log'
Invoke-UnityPhase -Phase 'gen' -LogName 'unity-clientbuild-gen.log'
Start-Sleep -Seconds 2
Invoke-UnityPhase -Phase 'yoo' -LogName 'unity-clientbuild-yoo.log'
Invoke-UnityPhase -Phase 'player' -LogName 'unity-clientbuild-player.log'

$exe = Join-Path $out 'Player\Farming.exe'
$ga = Join-Path $out 'Player\GameAssembly.dll'
$meta = Join-Path $out 'Player\Farming_Data\il2cpp_data\Metadata\global-metadata.dat'
$manifest = Join-Path $out 'baseline-manifest.json'
$fp = Join-Path $out 'baseline-fingerprint.json'
if (-not (Test-Path -LiteralPath $exe)) { throw "缺少 Player: $exe" }
if (-not (Test-Path -LiteralPath $ga)) { throw "缺少 IL2CPP GameAssembly.dll: $ga" }
if (-not (Test-Path -LiteralPath $meta)) { throw "缺少 IL2CPP global-metadata.dat: $meta" }
if (-not (Test-Path -LiteralPath $manifest)) { throw "缺少 baseline: $manifest" }
if (-not (Test-Path -LiteralPath $fp)) { throw "缺少 fingerprint: $fp" }
if ($m2Mode) {
    $cand = Join-Path $out 'candidate-fingerprint.json'
    if (-not (Test-Path -LiteralPath $cand)) { throw "缺少 M2 candidate-fingerprint: $cand" }
    Write-Output "CandidateFingerprint=$cand"
}
Write-Output "OK OutputDir=$out"
Write-Output "Player=$exe"
Write-Output "Baseline=$manifest"
Write-Output "Fingerprint=$fp"
Write-Output "GameAssembly=$ga"
Write-Output "GlobalMetadata=$meta"
exit 0
