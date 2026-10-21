# PoC #10: empty plaza + N bots stability
param(
    [int]$Count = 100,
    [int]$Duration = 120,
    [int]$Port = 17778,
    [int]$ConnectRate = 20
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "Server\CloudIsle.sln"
$serverProj = Join-Path $root "Server\CloudIsle.Server\CloudIsle.Server.csproj"
$botProj = Join-Path $root "Server\CloudIsle.Bot\CloudIsle.Bot.csproj"
$db = Join-Path $env:TEMP "cloudisle_poc10.db"
$reportDir = Join-Path $root "Docs\Perf"
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$report = Join-Path $reportDir "plaza_bots_$stamp.txt"
$serverOut = Join-Path $env:TEMP "cloudisle_poc10_server.out"
$serverErr = Join-Path $env:TEMP "cloudisle_poc10_server.err"

if (Test-Path $db) { Remove-Item $db -Force }
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

Write-Host "==> dotnet build $sln"
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) { exit 2 }

Write-Host "==> start server port=$Port"
$server = Start-Process -FilePath "dotnet" -ArgumentList @(
    "run", "--project", $serverProj, "-c", "Release", "--no-build", "--",
    "--host", "127.0.0.1", "--port", "$Port", "--db", $db,
    "--plaza-tick-ms", "100", "--plaza-line-cap", "100"
) -PassThru -NoNewWindow -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr

try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        if ($server.HasExited) { break }
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $iar = $tcp.BeginConnect("127.0.0.1", $Port, $null, $null)
            $ok = $iar.AsyncWaitHandle.WaitOne(200)
            if ($ok -and $tcp.Connected) { $tcp.Close(); $ready = $true; break }
            $tcp.Close()
        } catch {}
    }
    if (-not $ready) {
        Write-Host "---- server.out ----"
        if (Test-Path $serverOut) { Get-Content $serverOut -Tail 40 }
        Write-Host "---- server.err ----"
        if (Test-Path $serverErr) { Get-Content $serverErr -Tail 40 }
        Write-Error "Server did not accept connections. Exit=$($server.HasExited) code=$($server.ExitCode)"
        exit 2
    }

    Write-Host "==> run bots count=$Count duration=$Duration report=$report"
    dotnet run --project $botProj -c Release --no-build -- --host 127.0.0.1 --port $Port --count $Count --duration $Duration --connect-rate $ConnectRate --move-hz 5 --report $report
    $code = $LASTEXITCODE

    Write-Host "==> server metrics (tail)"
    if (Test-Path $serverOut) { Get-Content $serverOut -Tail 30 }
    if (Test-Path $report) {
        Write-Host "==> bot report"
        Get-Content $report
    }
    exit $code
}
finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
