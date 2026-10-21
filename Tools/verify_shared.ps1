# PoC #5：验证 Game.Shared 在服务端 .NET 工程中可编译并运行（冒烟，不启 TCP）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "Server\CloudIsle.sln"

if (-not (Test-Path $sln)) {
    Write-Error "Missing solution: $sln"
}

Write-Host "==> dotnet build $sln"
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> dotnet run CloudIsle.Server --shared-smoke"
dotnet run --project (Join-Path $root "Server\CloudIsle.Server\CloudIsle.Server.csproj") -c Release --no-build -- --shared-smoke
exit $LASTEXITCODE
