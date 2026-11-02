param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$ProtocPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ProtocPath)) {
    Write-Error "找不到 protoc: $ProtocPath"
    exit 1
}

$protoDir = Join-Path $RepoRoot 'Shared\proto'
$toolsProject = Join-Path $RepoRoot 'Server\CloudIsle.Tools\CloudIsle.Tools.csproj'
$dest = Join-Path $RepoRoot 'Assets\Shared\Proto\Generated'
$stageParent = Join-Path $RepoRoot 'Assets\Shared\Proto'
$stage = Join-Path $stageParent ('Generated.stage-' + [guid]::NewGuid().ToString('N'))
$desc = Join-Path $stage 'formal.pb'

# 正式协议白名单：进入 descriptor 校验与 MsgRegistry。poc.proto 不算正式。
$formalProtos = @(
    'common.proto',
    'system.proto',
    'inventory.proto',
    'farm.proto',
    'gather.proto',
    'craft.proto',
    'order.proto',
    'shop.proto',
    'sync.proto',
    'msg_id.proto'
)

function Remove-Stage {
    if (Test-Path -LiteralPath $stage) {
        $resolvedStage = [System.IO.Path]::GetFullPath($stage)
        $resolvedParent = [System.IO.Path]::GetFullPath($stageParent).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedStage.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolvedStage) -notmatch '^Generated\.stage-[0-9a-f]{32}$') {
            throw "拒绝清理超出协议临时目录的路径: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

function Ensure-CsMeta {
    param([Parameter(Mandatory = $true)][string]$CsPath)
    $metaPath = $CsPath + '.meta'
    if (Test-Path -LiteralPath $metaPath) {
        return
    }
    $guid = [guid]::NewGuid().ToString('N')
    @"
fileFormatVersion: 2
guid: $guid
"@ | Set-Content -LiteralPath $metaPath -Encoding utf8
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    $formalPaths = @()
    foreach ($name in $formalProtos) {
        $path = Join-Path $protoDir $name
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Error "缺少正式 proto: $path"
            Remove-Stage
            exit 1
        }
        $formalPaths += $path
    }

    & $ProtocPath --proto_path="$protoDir" --csharp_out="$stage" --descriptor_set_out="$desc" `
        --include_imports @formalPaths
    if ($LASTEXITCODE -ne 0) {
        Remove-Stage
        exit $LASTEXITCODE
    }

    $pocPath = Join-Path $protoDir 'poc.proto'
    if (Test-Path -LiteralPath $pocPath) {
        & $ProtocPath --proto_path="$protoDir" --csharp_out="$stage" $pocPath
        if ($LASTEXITCODE -ne 0) {
            Remove-Stage
            exit $LASTEXITCODE
        }
    }

    & dotnet run --project $toolsProject -- validate $desc
    if ($LASTEXITCODE -ne 0) {
        Remove-Stage
        exit $LASTEXITCODE
    }

    & dotnet run --project $toolsProject -- generate $protoDir $stage $desc
    if ($LASTEXITCODE -ne 0) {
        Remove-Stage
        exit $LASTEXITCODE
    }

    # 同步 .cs：保留已有 .meta，为新增 .cs 创建 .meta；删除已不存在的 .cs（及其 .meta）。
    if (-not (Test-Path -LiteralPath $dest)) {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
    }

    $stageCs = @(Get-ChildItem -LiteralPath $stage -Filter '*.cs' -File)
    if ($stageCs.Count -eq 0) {
        Write-Error '正式生成未产出任何 .cs'
        Remove-Stage
        exit 1
    }

    $stageNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $stageCs) {
        [void]$stageNames.Add($file.Name)
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $dest $file.Name) -Force
        Ensure-CsMeta -CsPath (Join-Path $dest $file.Name)
    }

    Get-ChildItem -LiteralPath $dest -Filter '*.cs' -File | ForEach-Object {
        if (-not $stageNames.Contains($_.Name)) {
            Remove-Item -LiteralPath $_.FullName -Force
            $oldMeta = $_.FullName + '.meta'
            if (Test-Path -LiteralPath $oldMeta) {
                Remove-Item -LiteralPath $oldMeta -Force
            }
        }
    }
}
finally {
    Remove-Stage
}

exit 0
