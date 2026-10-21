﻿# Luban PoC 兼容入口。
# 菜单和旧调用仍执行本文件；实际导表改走 gen_config.ps1 的 M1 参数。
# ConfigVersion 固定为 100。不在这里保留第二套复制逻辑。
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$gen = Join-Path $PSScriptRoot 'gen_config.ps1'
$sourceDir = Join-Path $repoRoot 'Shared\config'
$exporterPath = Join-Path $repoRoot 'Tools\Luban\Luban\Luban.dll'
& $gen -RepoRoot $repoRoot -SourceDir $sourceDir -ExporterPath $exporterPath -ConfigVersion 100
exit $LASTEXITCODE
