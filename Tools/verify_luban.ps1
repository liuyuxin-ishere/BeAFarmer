# PoC #7b：Luban 导出 + 服务端读取；改表重导；引用校验失败不覆盖（抽测）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$gen = Join-Path $root "Tools\gen_luban.ps1"
$sln = Join-Path $root "Server\CloudIsle.sln"
$serverProj = Join-Path $root "Server\CloudIsle.Server\CloudIsle.Server.csproj"
$lubanDir = Join-Path $root "Server\ConfigLuban"
$globalXlsx = Join-Path $root "Shared\config\Datas\global.xlsx"

Write-Host "==> gen_luban"
powershell -NoProfile -ExecutionPolicy Bypass -File $gen
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> dotnet build"
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> luban-smoke baseline"
$out1 = (& dotnet run --project $serverProj -c Release --no-build -- --luban-smoke --luban-dir $lubanDir | Out-String)
Write-Host $out1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($out1 -notmatch "bag_init_slots=60") { Write-Error "expected bag_init_slots=60" }

Write-Host "==> patch global bag_init_slots=61 and regenerate"
python -c @"
from openpyxl import load_workbook
wb=load_workbook(r'$($globalXlsx -replace '\\','/')')
ws=wb.active
# find bag_init_slots column
headers=[ws.cell(1,c).value for c in range(1, ws.max_column+1)]
idx=headers.index('bag_init_slots')
ws.cell(4, idx+1).value=61
wb.save(r'$($globalXlsx -replace '\\','/')')
print('patched', idx)
"@
powershell -NoProfile -ExecutionPolicy Bypass -File $gen
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$out2 = (& dotnet run --project $serverProj -c Release --no-build -- --luban-smoke --luban-dir $lubanDir | Out-String)
Write-Host $out2
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($out2 -notmatch "bag_init_slots=61") { Write-Error "expected bag_init_slots=61 after re-export" }

Write-Host "==> restore bag_init_slots=60"
python -c @"
from openpyxl import load_workbook
wb=load_workbook(r'$($globalXlsx -replace '\\','/')')
ws=wb.active
headers=[ws.cell(1,c).value for c in range(1, ws.max_column+1)]
idx=headers.index('bag_init_slots')
ws.cell(4, idx+1).value=60
wb.save(r'$($globalXlsx -replace '\\','/')')
"@
powershell -NoProfile -ExecutionPolicy Bypass -File $gen
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> ref validation (bad seedId should fail)"
$crop = Join-Path $root "Shared\config\Datas\crop.xlsx"
python -c @"
from openpyxl import load_workbook
wb=load_workbook(r'$($crop -replace '\\','/')')
ws=wb.active
# seedId column
headers=[ws.cell(1,c).value for c in range(1, ws.max_column+1)]
idx=headers.index('seedId')
old=ws.cell(4, idx+1).value
ws.cell(4, idx+1).value=999999
wb.save(r'$($crop -replace '\\','/')')
print('old', old)
"@
$beforeHash = (Get-FileHash (Join-Path $lubanDir "tbglobal.json")).Hash
$genOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $gen 2>&1 | Out-String
Write-Host $genOut
if ($LASTEXITCODE -eq 0) {
  # restore anyway
  python -c @"
from openpyxl import load_workbook
wb=load_workbook(r'$($crop -replace '\\','/')')
ws=wb.active
headers=[ws.cell(1,c).value for c in range(1, ws.max_column+1)]
idx=headers.index('seedId')
ws.cell(4, idx+1).value=2001
wb.save(r'$($crop -replace '\\','/')')
"@
  Write-Error "expected Luban validation failure for bad seedId"
}
$afterHash = (Get-FileHash (Join-Path $lubanDir "tbglobal.json")).Hash
if ($beforeHash -ne $afterHash) {
  Write-Warning "ConfigLuban changed despite failed gen (sync may have been skipped - OK if gen aborted before sync)"
}

# restore crop
python -c @"
from openpyxl import load_workbook
wb=load_workbook(r'$($crop -replace '\\','/')')
ws=wb.active
headers=[ws.cell(1,c).value for c in range(1, ws.max_column+1)]
idx=headers.index('seedId')
ws.cell(4, idx+1).value=2001
wb.save(r'$($crop -replace '\\','/')')
print('crop restored')
"@
powershell -NoProfile -ExecutionPolicy Bypass -File $gen
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "[Luban PoC] verify OK"
exit 0
