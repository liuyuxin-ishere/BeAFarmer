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

function Remove-Stage {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    & $ProtocPath --proto_path="$protoDir" --csharp_out="$stage" --descriptor_set_out="$desc" `
        (Join-Path $protoDir 'common.proto') `
        (Join-Path $protoDir 'system.proto') `
        (Join-Path $protoDir 'msg_id.proto')
    if ($LASTEXITCODE -ne 0) {
        Remove-Stage
        exit $LASTEXITCODE
    }

    & $ProtocPath --proto_path="$protoDir" --csharp_out="$stage" (Join-Path $protoDir 'poc.proto')
    if ($LASTEXITCODE -ne 0) {
        Remove-Stage
        exit $LASTEXITCODE
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

    $published = Join-Path $stageParent 'Generated.publishing'
    if (Test-Path -LiteralPath $published) {
        Remove-Item -LiteralPath $published -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $published | Out-Null
    Copy-Item -Path (Join-Path $stage '*.cs') -Destination $published
    if (Test-Path -LiteralPath $dest) {
        Remove-Item -LiteralPath $dest -Recurse -Force
    }
    Rename-Item -LiteralPath $published -NewName 'Generated'
}
finally {
    Remove-Stage
}

exit 0
