# Greybox networked loop smoke (T02-T13 numeric)
param(
    [int]$Port = 17779
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "Server\CloudIsle.sln"
$serverProj = Join-Path $root "Server\CloudIsle.Server\CloudIsle.Server.csproj"
$smokeProj = Join-Path $root "Server\CloudIsle.GreyboxSmoke\CloudIsle.GreyboxSmoke.csproj"
$db = Join-Path $env:TEMP "cloudisle_greybox.db"
$serverOut = Join-Path $env:TEMP "cloudisle_greybox_server.out"
$serverErr = Join-Path $env:TEMP "cloudisle_greybox_server.err"

if (Test-Path $db) { Remove-Item $db -Force }

Write-Host "==> dotnet build"
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) { exit 2 }
dotnet build $smokeProj -c Release
if ($LASTEXITCODE -ne 0) { exit 2 }

Write-Host "==> start server port=$Port"
$server = Start-Process -FilePath "dotnet" -ArgumentList @(
    "run", "--project", $serverProj, "-c", "Release", "--no-build", "--",
    "--host", "127.0.0.1", "--port", "$Port", "--db", $db, "--no-plaza"
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
        if (Test-Path $serverOut) { Get-Content $serverOut -Tail 40 }
        if (Test-Path $serverErr) { Get-Content $serverErr -Tail 40 }
        Write-Error "server not ready"
        exit 2
    }

    Write-Host "==> greybox smoke"
    dotnet run --project $smokeProj -c Release --no-build -- --host 127.0.0.1 --port $Port
    exit $LASTEXITCODE
}
finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
