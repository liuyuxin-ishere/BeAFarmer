<#
.SYNOPSIS
启动正式M1 TCP/HTTP/账号/保存服务，失败退出非0；不启动旧PoC。
.PARAMETER RepoRoot
工程绝对路径；省略时从脚本位置解析，不依赖调用者当前目录。
.PARAMETER DatabasePath
本次SQLite文件；默认Server/Data/M1/m1.db，不使用poc.db。
.PARAMETER TcpPort
正式TCP端口，默认17780。
.PARAMETER HttpPort
localhost静态补丁HTTP端口，默认18080。
.PARAMETER ContentRoot
包含version.json与完整版本目录的发布根。
.PARAMETER ConfigDirectory
带config_manifest.json的正式Luban输出。
.PARAMETER ConfigVersion
启动必须精确加载的配置版本（生产M2默认200）。
.PARAMETER RequiredConfigVersion
登录最低配置版本（生产M2默认200）。
.PARAMETER ResVersion
登录最低资源版本（生产M2默认200）。
.PARAMETER AppVersion
登录最低安装包版本（生产M2默认0.4.0）。
.PARAMETER BridgeContract
登录桥契约（生产M2默认m2-bridge-0.4）。
.PARAMETER ProtoVersion
客户端必须精确匹配的正式协议整数。
.PARAMETER LogDirectory
本次实例的日志目录。
.PARAMETER StopFile
可选正常停止信号文件；启动前必须不存在。创建它等效于请求Ctrl+C停机保存。
.PARAMETER BuildArtifactsDirectory
独立dotnet构建输出，默认Temp/M1ServerBuild，避免共享bin/obj。
.PARAMETER SkipBuild
仅运行该输出目录内已经构建的Release服务；文件不存在仍失败。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$DatabasePath,
    [ValidateRange(1,65535)][int]$TcpPort = 17780,
    [ValidateRange(1,65535)][int]$HttpPort = 18080,
    [string]$ContentRoot = 'Server/PatchContent/M1',
    [string]$ConfigDirectory = 'Server/ConfigLuban',
    [ValidateRange(1,2147483647)][int]$ConfigVersion = 200,
    [ValidateRange(1,2147483647)][int]$RequiredConfigVersion = 200,
    [ValidateRange(1,2147483647)][int]$ResVersion = 200,
    [string]$AppVersion = '0.4.0',
    [string]$BridgeContract = 'm2-bridge-0.4',
    [ValidateRange(1,2147483647)][int]$ProtoVersion = 1,
    [string]$LogDirectory = 'Server/Logs/M1',
    [string]$StopFile,
    [string]$BuildArtifactsDirectory = 'Temp/M1ServerBuild',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
# 手动检查原生命令退出码，保证Windows PowerShell/pwsh行为一致。
$PSNativeCommandUseErrorActionPreference = $false
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

<# 把显式路径相对RepoRoot解析；不删除/移动路径，也不读取全局凭据。 #>
function Resolve-M1Path {
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)][string]$Root)
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $Root $Path))
}

try {
    $root = [IO.Path]::GetFullPath($RepoRoot)
    $project = Join-Path $root 'Server/CloudIsle.Server/CloudIsle.Server.csproj'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'RepoRoot中缺少正式服务端项目' }
    $content = Resolve-M1Path $ContentRoot $root
    $config = Resolve-M1Path $ConfigDirectory $root
    if (-not (Test-Path -LiteralPath (Join-Path $content 'version.json') -PathType Leaf)) { throw 'ContentRoot中缺少version.json' }
    if (-not (Test-Path -LiteralPath (Join-Path $config 'config_manifest.json') -PathType Leaf)) { throw 'ConfigDirectory中缺少config_manifest.json' }
    if ([string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath = 'Server/Data/M1/m1.db' }
    $db = Resolve-M1Path $DatabasePath $root
    $logs = Resolve-M1Path $LogDirectory $root
    $artifacts = Resolve-M1Path $BuildArtifactsDirectory $root
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    if (-not $SkipBuild) {
        & $dotnet build $project -c Release --artifacts-path $artifacts --nologo -v minimal -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw ('正式服务端编译失败，exit=' + $LASTEXITCODE) }
    }
    $assembly = Join-Path $artifacts 'bin/CloudIsle.Server/release/CloudIsle.Server.dll'
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw '正式服务端Release产物缺失' }
    $hostArguments = @(
        '--m1', '--repo-root', $root, '--db', $db,
        '--config-dir', $config, '--config-version', [string]$ConfigVersion,
        '--content-root', $content, '--log-dir', $logs,
        '--tcp-port', [string]$TcpPort, '--http-port', [string]$HttpPort,
        '--min-app-version', $AppVersion, '--bridge-contract', $BridgeContract,
        '--proto-version', [string]$ProtoVersion,
        '--res-version', [string]$ResVersion, '--required-config-version', [string]$RequiredConfigVersion
    )
    if (-not [string]::IsNullOrWhiteSpace($StopFile)) {
        $hostArguments += @('--stop-file', (Resolve-M1Path $StopFile $root))
    }
    & $dotnet $assembly @hostArguments
    $serverExit = $LASTEXITCODE
    if ($serverExit -ne 0) { Write-Error ('正式M1服务端失败，exit=' + $serverExit) -ErrorAction Continue }
    exit $serverExit
} catch {
    [Console]::Error.WriteLine('[run_server] ' + $_.Exception.Message)
    exit 1
}
