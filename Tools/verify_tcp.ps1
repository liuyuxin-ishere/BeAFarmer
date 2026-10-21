# PoC #6：启动 TCP 服务端，跑控制台客户端验证登录 + SQLite 读写
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "Server\CloudIsle.sln"
$serverProj = Join-Path $root "Server\CloudIsle.Server\CloudIsle.Server.csproj"
$clientProj = Join-Path $root "Server\CloudIsle.PocClient\CloudIsle.PocClient.csproj"
$port = 17777
$db = Join-Path $env:TEMP "cloudisle_poc6.db"
if (Test-Path $db) { Remove-Item $db -Force }

Write-Host "==> dotnet build $sln"
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> start server port=$port db=$db"
$server = Start-Process -FilePath "dotnet" -ArgumentList @(
    "run", "--project", $serverProj, "-c", "Release", "--no-build", "--",
    "--host", "127.0.0.1", "--port", "$port", "--db", $db
) -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $env:TEMP "cloudisle_poc6_server.out") -RedirectStandardError (Join-Path $env:TEMP "cloudisle_poc6_server.err")

try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ($server.HasExited) { break }
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $iar = $tcp.BeginConnect("127.0.0.1", $port, $null, $null)
            $ok = $iar.AsyncWaitHandle.WaitOne(200)
            if ($ok -and $tcp.Connected) { $tcp.Close(); $ready = $true; break }
            $tcp.Close()
        } catch {}
    }
    if (-not $ready) {
        Write-Error "Server did not accept connections. Exit=$($server.HasExited) code=$($server.ExitCode)"
    }

    Write-Host "==> run PocClient"
    dotnet run --project $clientProj -c Release --no-build -- --host 127.0.0.1 --port $port --user poc6_verify --pass poc --delta 10
    $code = $LASTEXITCODE
    exit $code
}
finally {
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
