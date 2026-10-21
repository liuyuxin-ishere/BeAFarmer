# PoC #7：导表 → 双端读同一份 JSON；Excel 覆盖导出使服务端读到新值
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$py = "python"
$gen = Join-Path $root "Tools\gen_config.py"
$sln = Join-Path $root "Server\CloudIsle.sln"
$serverProj = Join-Path $root "Server\CloudIsle.Server\CloudIsle.Server.csproj"
$configDir = Join-Path $root "Server\Config"

Write-Host "==> gen_config (baseline)"
& $py $gen
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> dotnet build"
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> server --config-smoke (baseline)"
$out1 = (& dotnet run --project $serverProj -c Release --no-build -- --config-smoke --config-dir $configDir | Out-String)
Write-Host $out1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($out1 -notmatch "bag_init_slots=60") {
    Write-Error "expected bag_init_slots=60 in baseline output"
}

Write-Host "==> gen_config with override bag_init_slots=61"
& $py $gen --set-global "bag_init_slots=61"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> server --config-smoke (override)"
$out2 = (& dotnet run --project $serverProj -c Release --no-build -- --config-smoke --config-dir $configDir | Out-String)
Write-Host $out2
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($out2 -notmatch "bag_init_slots=61") {
    Write-Error "expected bag_init_slots=61 after re-export"
}

Write-Host "==> restore baseline export"
& $py $gen
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "[Config PoC] verify OK (Excel/override re-export -> server reads new value)"
exit 0
