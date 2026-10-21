<!-- XLUA-MIGRATION:BEGIN -->
> **迁移说明（2026-10-19）：本文件保留历史原文。**
> 当前热更架构调整为 xLua；旧记录中的技术名称和“已通过”仅描述当时环境，不能解释为 xLua 已验证。
> 原任务步骤、源码、命令、问题和实测结果完整保留；服务端、协议、资源与公共 C# 能力可按新边界复用，旧热更 DLL／补元数据链的完成记录不构成 xLua 验收。旧自动推进在 T42 部分实现阶段停止，T42 尚未验收；后续按新方案重新安排。
> 当前设计、验证范围和证据见 [xLua 迁移方案与 PoC](../07_xLua迁移方案与PoC.md)。
<!-- XLUA-MIGRATION:END -->

# M1 开发日志 · 5.5 配置、版本与补丁

记录方式对齐 `06_开发计划.md` 第 6.1 节：每项写成可勾选的文档、命令或方法。目录索引见 `M1_Logs.md`。

本文件对应 `06_M1_基础框架任务拆解.md` 第 5.5 节，任务 T41–T47。

## T41 接入 gen_config.ps1

- 来源：`06_M1_基础框架任务拆解.md` 第 1.2 节第 1 点、第 4.6 节、第 5.5 节 T41；依赖 T01、T02
- 需求：R-E02
- 依据：`M1_Baseline.md` 第 3 节。稳定契约 `param([string]$RepoRoot, [string]$SourceDir, [string]$ExporterPath, [int]$ConfigVersion)`。默认 Luban 为 `Tools/Luban/Luban/Luban.dll` 与 `Shared/config/luban.conf`，源是 `Shared/config` 的 Datas/Defines，不是设计参考工作簿。C# 仍在 `Assets/Config/Generated`，运行时仍在 `Assets/Config/LubanRuntime`。`CloudIsle.Config.csproj` 继续同源编译这些代码，程序集边界不变
- 通过条件：同一份配置产物分发到客户端与服务端；清单列出 ConfigVersion 与文件哈希
- 日期：2026-10-19
- 范围：只做 T41。不实现 ConfigService、不写 T42 及以后，不把 `m1_patch_message` 写入 100，不运行 `verify_luban.ps1` 去改正式源表。未运行 Unity 编译

### 任务、流程与验证

`TbGlobal` 保持原来的强类型单行。100 基包另加 `TbGlobalKv`：Luban map，字段只有字符串 `key` 和 `value`。现有 Global 的 22 个字段按不变量规则写入 `global_kv.xlsx`（整数用十进制，小数去掉无意义的尾零，分号列表写成 `[500,1000,...]`）。以后只加行，不改 `GlobalKv` / `TbGlobalKv` 的生成类型。`m1_patch_message` 不在源里；ConfigVersion 小于 101 时，键值里一旦出现这个键就失败。清单 schema 是 `m1.config-manifest.v1`，写明 ConfigVersion、键值文件名和字段，以及 `ConfigSnapshot(int Version, IReadOnlyDictionary<string,string> Globals)` 要读的形状。文件哈希只覆盖数据 JSON，不把 `config_manifest.json` 自己算进去。读取留到 T42。

导出先写到 `RepoRoot\Temp\gen_config\<本次id>\` 的 code 和 data。Luban 返回 0，并且键值覆盖强类型 Global 之后，才写入四个正式目录：`Assets/Config/Generated`、`Shared/config/Output/json`、`Server/ConfigLuban`、`Assets/StreamingAssets/ConfigLuban`。这三个 JSON 目录只删除清单里出现过的文件，以及 `tb*.json`、`config_manifest.json`。代码目录只删除带 Luban 自动生成头、或写在 `gen_config_code_manifest.json` 里的 `.cs`。`.meta`、`keep-me.txt`、没有生成头的手写 `.cs` 不动，也不整夹清空。

发布前把目标目录里的现有文件备份到同一次暂存。任一目标写入失败，或验证钩子 `GEN_CONFIG_FAIL_POINT=AfterFirstPublish` 触发，就按备份恢复已经改过的目录并核对哈希。没有改过的目录不动。这不是跨目录原子事务。递归删除只针对解析后仍落在 `RepoRoot\Temp\gen_config\<本次id>` 里的暂存，使用 `-LiteralPath`。

`-Legacy`，或参数里出现 `--xlsx` / `--set-global`，转给 `Tools/gen_config.py`。参数设为只允许具名绑定，避免 `--xlsx` 被当成 `RepoRoot`。不带参数时失败，不再把 Python 当 M1 默认。`gen_luban.ps1` 用四个具名参数调用新入口，ConfigVersion 固定 100。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 错误路径 | 正式 RepoRoot 配不存在的 SourceDir、不存在的 RepoRoot、`.conf` 当导出器、缺少的 dll | 非 0；不创建缺失的 RepoRoot；正式产物哈希不变 | 通过。`01-bad-source.txt` EXIT 2，`02-bad-reporoot.txt` EXIT 2 且 `Temp\missing-repo-root` 未创建，`03-bad-exporter-ext.txt` EXIT 3，`04-missing-exporter.txt` EXIT 3。`00-before.txt` 与 `09-after-pre-failures.txt` 一致 |
| 非法版本 | `-ConfigVersion 0` 和 `-ConfigVersion -1` | 非 0，正式产物不变 | 通过。`05-version-zero.txt` EXIT 2，`06-version-negative.txt` EXIT 2。同上，快照未变 |
| 导出器失败 | 用 `Google.Protobuf.dll` 充当 Luban.dll | 非 0，正式产物不变 | 通过。`07-exporter-fail.txt` EXIT -2147450749。快照未变 |
| 无效引用 | 把源复制到 `Temp\t41-bad-ref\config`，只改副本 `crop.xlsx` 的 seedId=999999，RepoRoot 仍是正式工程 | Luban 引用校验非 0；正式目录和正式 xlsx 不变 | 通过。`08-bad-ref.txt` EXIT 1，日志有 `TbCrop[1].seedId = 999999` 在 TbItem 中不存在。正式快照未变。成功发布后再跑一次，`19-bad-ref-after-success.txt` EXIT 1，`13-after-success.txt` 与 `20-final.txt` 一致 |
| 正式导出 | ConfigVersion=100，源为 `Shared\config`，导出器为 Luban.dll | 退出 0；三端 JSON 集合与每个 SHA256 一致；清单含版本和 `tbglobalkv.json` 哈希；清单不自引用；无 `m1_patch_message`；Global 的 readonly 字段不变 | 通过。`10-success.txt` EXIT 0。清单 `configVersion` 100，files 18 个，含 `tbglobalkv.json` sha256 `ffd517ee550c82c847b03985ded60f069a48bcc1770147f7e8912fb0e9351642`、length 1408。`12-cross-dir.txt` 三目录 JSON 一致。`global-fields-before.txt` 与 `global-fields-after.txt` 同为 22 个强类型字段。`GlobalKv.cs` 只有 `string Key` 和 `string Value` |
| 服务端编译 | `dotnet build Server/CloudIsle.Config/CloudIsle.Config.csproj` | 退出 0。只说明该工程编译，不代表 Unity 已编译 | 通过。`11-dotnet-build.txt` EXIT 0，0 警告 0 错误，输出 `Game.Config.dll` |
| 空格路径和另一个当前目录 | 在 `Temp\M1 T41 fixture\repo` 放源，并从 `C:\Windows` 调用 | 退出 0；`tbstale.json` 和带生成头的 `OldStale.cs` 被删；`keep-me.txt` 与 `HandNotes.cs` 还在 | 通过。`14-space-fixture.txt` EXIT 0。检查由 `Temp\t41-run.ps1` 的 fixtures 阶段执行并打印 `SPACE FIXTURE OK` |
| 发布失败回滚 | 夹具里先改 `Global.cs` 加上 `t41-rollback-marker`，再设 `GEN_CONFIG_FAIL_POINT=AfterFirstPublish` | 非 0；标记被恢复；其余代码和 `Server\ConfigLuban` 与发布前一致。不声称四目录原子提交 | 通过。`15-rollback.txt` EXIT 5，stderr 只有钩子说明，没有回滚失败。fixtures 阶段打印 `ROLLBACK OK` |
| 旧入口 | 从 `C:\Windows` 调用 `-Legacy --help`，以及 `-Legacy --xlsx` 指向不存在的工作簿 | help 为 0 且不写正式产物；缺文件非 0；正式哈希不变 | 通过。`16-legacy-help.txt` EXIT 0，输出含 usage。`17-legacy-missing-xlsx.txt` EXIT 1，`missing book.xlsx`。`18-after-legacy.txt` 与成功快照一致 |
| 源表 | 对比失败前后和全部测试结束后的 xlsx 哈希 | 正式 `global.xlsx`、`crop.xlsx`、`global_kv.xlsx`、`__tables__.xlsx` 在测试过程中不再变化。无效引用只改 Temp 副本 | 通过。`00-before.txt` 与 `20-final.txt` 中这四个文件哈希相同。`global.xlsx` 为 `3bae06ef830a21e6abff22ae6fcd4479124f12a942270ae800f6ff1573098c41` |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。不读取电量，不创建或修改电量标记。

#### 1. 字符串键值源与导出契约

- [x] 新增 `Shared/config/Datas/global_kv.xlsx`，并把 `TbGlobalKv` 登记进 `Defines/__tables__.xlsx`。不改 `global.xlsx` 的列。
- [x] 重写 `Tools/gen_config.ps1`。先 staging，再校验，再发布到 `Shared/config/Output/json`、`Server/ConfigLuban`、`Assets/StreamingAssets/ConfigLuban` 和 `Assets/Config/Generated`。
- [x] `Tools/gen_luban.ps1` 改为调用新入口。

#### 2. 真实导出与失败边界

- [x] 用 ConfigVersion=100 导出正式工程，核对三端 JSON 与清单，并 `dotnet build` `Server/CloudIsle.Config/CloudIsle.Config.csproj`。
- [x] 在带空格的隔离目录、另一个当前目录下复跑成功路径。错误路径、非法版本、无效引用、导出器失败均非 0，正式产物哈希不变。遗留 JSON/C# 只在夹具里删除，正式 xlsx 除登记表外不变。
- [x] 证据写入 `Docs/Acceptance/M1/20261019-T41/`。通过后回填本表并勾选拆解表 T41。

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 具体修复与复验 |
|---|---|---|---|
| 第一次用 Windows PowerShell 解析 `gen_config.ps1` | 解析错误，中文引号被拆开，EXIT 1，还没调用 Luban | Windows PowerShell 5 按系统 ANSI 读没有 BOM 的脚本 | 脚本改成 UTF-8 BOM。复验：`-ConfigVersion 0` 返回 EXIT 2 |
| 未传剩余参数 | 提示无法识别的参数，但后面是空的，EXIT 2 | `@($null)` 在 Windows PowerShell 里计数是 1 | 只收集非空剩余参数。复验：非法版本两条都是 EXIT 2，不再误报空参数 |
| 第一次正式导出 | Luban 已写出暂存，随后 EXIT 1，stderr 为 Contains 没有单参数重载。正式目录未发布 | `JavaScriptSerializer` 的字典没有 PowerShell 能绑定的 `Contains(string)` | 改为按键名遍历的 `Test-JsonField`。证据 `10-kv-check-before-fix.txt` 与 `10-first-export-failure.txt`。复验：`10-success.txt` EXIT 0 |
| 带空格夹具第一次发布 | EXIT 5，删除陈旧文件失败，回滚也失败 | `Remove-OwnedFile` 的参数名是 `RepoRoot`，调用写成了 `-Root`，对不上 | 调用改为 `-RepoRoot`。复验：`14-space-fixture.txt` EXIT 0，`tbstale.json` 和 `OldStale.cs` 删除，`keep-me.txt` 与 `HandNotes.cs` 保留 |
| 回滚钩子第一次执行 | EXIT 5，但 `Global.cs` 上的 `t41-rollback-marker` 没有回来 | `@($script:Targets)` 展开了里面的哈希表，PowerShell 抛出 Argument types do not match，恢复循环没执行 | 改为直接 `foreach ($state in $script:Targets)`。复验：`15-rollback.txt` EXIT 5，stderr 只有钩子说明，标记已恢复，其余代码和 Server\ConfigLuban 哈希与发布前一致 |
| `-Legacy --help` | EXIT 2，并说旧入口和 M1 参数不能同时使用。`RepoRoot` 的值是 `--help` | 参数默认可按位置绑定，`--help` 被收成第一个位置参数 | `CmdletBinding(PositionalBinding=$false)`。复验：`16-legacy-help.txt` EXIT 0 且含 usage；缺文件的 `--xlsx` EXIT 1；正式快照不变 |
| 用 Start-Process 调 `dotnet build` | 进程停在 `VBCSCompiler.dll -pipename`，几分钟没有结果 | Start-Process -Wait等待整个进程树；实际build已在1.96秒完成，但常驻VBCSCompiler子孙使等待不结束 | 验证改成在工程根直接执行 `dotnet build`。`11-dotnet-build.txt` EXIT 0。这是验证命令的问题，不是导表脚本的发布逻辑 |

### 执行命令与脚本

证据目录：`Docs/Acceptance/M1/20261019-T41/`。下面是实际使用的命令。失败用例的退出码见对应 txt。

#### 命令：正式导出

```text
powershell -NoProfile -ExecutionPolicy Bypass -File E:\Workbench\UnityWorkBench\Farming\Tools\gen_config.ps1 -RepoRoot E:\Workbench\UnityWorkBench\Farming -SourceDir E:\Workbench\UnityWorkBench\Farming\Shared\config -ExporterPath E:\Workbench\UnityWorkBench\Farming\Tools\Luban\Luban\Luban.dll -ConfigVersion 100
结果：EXIT 0。见 10-success.txt。
```

#### 命令：编译配置工程

```text
dotnet build E:\Workbench\UnityWorkBench\Farming\Server\CloudIsle.Config\CloudIsle.Config.csproj --nologo
工作目录：E:\Workbench\UnityWorkBench\Farming
结果：EXIT 0，0 警告，0 错误。见 11-dotnet-build.txt。
```

#### 命令：失败、空格夹具、回滚和旧入口

这些由 `Temp\t41-run.ps1` 调起，跑完已删除该临时脚本。子进程都是 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools\gen_config.ps1`，路径带引号。空格夹具和旧入口的当前目录是 `C:\Windows`。

```text
-RepoRoot <正式工程> -SourceDir <正式工程>\Temp\no-such-source -ExporterPath <Luban.dll> -ConfigVersion 100
-RepoRoot <正式工程>\Temp\missing-repo-root -SourceDir <正式 Shared\config> -ExporterPath <Luban.dll> -ConfigVersion 100
-RepoRoot <正式工程> -SourceDir <正式 Shared\config> -ExporterPath <正式 Shared\config\luban.conf> -ConfigVersion 100
-RepoRoot <正式工程> -SourceDir <正式 Shared\config> -ExporterPath <正式工程>\Tools\Luban\missing Luban.dll -ConfigVersion 100
-ConfigVersion 0
-ConfigVersion -1
-ExporterPath <正式工程>\Assets\Plugins\Google.Protobuf\Google.Protobuf.dll
-SourceDir <正式工程>\Temp\t41-bad-ref\config
-RepoRoot <正式工程>\Temp\M1 T41 fixture\repo -SourceDir <该目录>\Shared\config -ExporterPath <Luban.dll> -ConfigVersion 100
GEN_CONFIG_FAIL_POINT=AfterFirstPublish 后对 Temp\t41-rollback\repo 再导一次
-Legacy --help
-Legacy --xlsx <正式工程>\Temp\missing book.xlsx
```

结果文件：`01-bad-source.txt` 到 `09-after-pre-failures.txt`，`12-cross-dir.txt`，`13-after-success.txt`，`14-space-fixture.txt`，`15-rollback.txt`，`16-legacy-help.txt`，`17-legacy-missing-xlsx.txt`，`18-after-legacy.txt`，`19-bad-ref-after-success.txt`，`20-final.txt`，`config_manifest.json`，`tbglobalkv.json`。

没有修改电量标记，没有提交或推送。T42 的 ConfigService 没有写。

#### 脚本：Tools/gen_config.ps1

```powershell
# M1 配置导出适配层。
# 正式入口参数：-RepoRoot -SourceDir -ExporterPath -ConfigVersion。
# PoC 旧入口：-Legacy，或在参数中带上 --xlsx / --set-global，转发给 Tools/gen_config.py。
# 不带上述参数时失败，避免把旧 Python 工具当成 M1 默认导表。
[CmdletBinding(PositionalBinding = $false)]
param(
    # 完整 Unity 工程根。相对的 SourceDir / ExporterPath 相对此目录解析，不使用当前工作目录。
    [string]$RepoRoot,
    # 含 luban.conf 的源根。其下使用 Datas 与 Defines。不是设计参考工作簿。
    [string]$SourceDir,
    # Luban 导出器。.dll 用 dotnet 启动，.exe 直接启动。
    [string]$ExporterPath,
    # 写入清单的配置版本。必须是正整数。小于 101 时拒绝键 m1_patch_message。
    [int]$ConfigVersion,
    # 显式使用旧 Python 导表，供 PoC。
    [switch]$Legacy,
    # 转发给 gen_config.py 的剩余参数，例如 --xlsx 与 --set-global。
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -Scope Global -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$script:ExitCode = 0
$script:StagingPath = $null
$script:PublishStarted = $false
$script:PreserveStaging = $false
$script:Targets = @()

<#
.SYNOPSIS
记录失败码并中断当前导出。
.PARAMETER Message
写到标准错误的原因。
.PARAMETER Code
进程退出码，非 0。
#>
function Stop-GenConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][int]$Code
    )
    if ($script:ExitCode -eq 0) { $script:ExitCode = $Code }
    throw $Message
}

<#
.SYNOPSIS
把路径变成绝对路径，并确认它位于给定根目录之内。
.PARAMETER Path
待检查的文件或目录。
.PARAMETER Root
允许的根目录。
#>
function Resolve-UnderWorkspace {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )
    $full = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $prefix = $rootFull + '\'
    $same = [string]::Equals($full.TrimEnd('\'), $rootFull, [System.StringComparison]::OrdinalIgnoreCase)
    $inside = $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $same -and -not $inside) {
        Stop-GenConfig -Message "拒绝操作工作区外路径: $full" -Code 5
    }
    return $full
}

<#
.SYNOPSIS
删除一个已确认位于工程 Temp\gen_config 下的本次暂存目录。
.PARAMETER Path
本次暂存目录的绝对路径。
.PARAMETER RepoRoot
工程根。
#>
function Remove-OwnStaging {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    $full = Resolve-UnderWorkspace -Path $Path -Root $RepoRoot
    $gate = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'Temp\gen_config')).TrimEnd('\')
    $prefix = $gate + '\'
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-GenConfig -Message "拒绝递归删除暂存根以外的目录: $full" -Code 5
    }
    if ([string]::Equals([System.IO.Path]::GetFileName($full.TrimEnd('\')), 'gen_config', [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-GenConfig -Message "拒绝删除暂存根目录本身: $full" -Code 5
    }
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

<#
.SYNOPSIS
删除发布目录中的单个已拥有文件。调用前必须已经解析到工程根内。
.PARAMETER Path
文件绝对路径。
.PARAMETER RepoRoot
工程根。
#>
function Remove-OwnedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    $full = Resolve-UnderWorkspace -Path $Path -Root $RepoRoot
    if (Test-Path -LiteralPath $full -PathType Container) {
        Stop-GenConfig -Message "拒绝删除目录: $full" -Code 5
    }
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full
        if ($item.IsReadOnly) { $item.IsReadOnly = $false }
        Remove-Item -LiteralPath $full -Force
    }
}

<#
.SYNOPSIS
相对路径按工程根解析；已是绝对路径则只规范化。
.PARAMETER Path
输入路径，可以为空。
.PARAMETER RepoRoot
工程根。
#>
function Resolve-RepoPath {
    param(
        [string]$Path,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

<#
.SYNOPSIS
计算文件 SHA256，返回小写十六进制。
.PARAMETER Path
已存在的文件。
#>
function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $hash = $sha.ComputeHash($stream)
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
    $chars = New-Object System.Text.StringBuilder
    foreach ($b in $hash) { [void]$chars.Append($b.ToString('x2')) }
    return $chars.ToString()
}

<#
.SYNOPSIS
读取 JSON，保留单元素数组。
.PARAMETER Path
JSON 文件。
#>
function Read-JsonDocument {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not ('GenConfigJsonSerializer' -as [type])) {
        Add-Type -AssemblyName System.Web.Extensions
        Add-Type -ReferencedAssemblies System.Web.Extensions -TypeDefinition @'
public class GenConfigJsonSerializer {
    public static object Parse(string text) {
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        serializer.MaxJsonLength = 67108864;
        return serializer.DeserializeObject(text);
    }
}
'@
    }
    $text = [System.IO.File]::ReadAllText($Path)
    return [GenConfigJsonSerializer]::Parse($text)
}

<#
.SYNOPSIS
列出目录下一层文件。目录不存在或没有文件时返回空列表，避免 @($null) 变成一个空元素。
.PARAMETER Path
目录路径。
#>
function Get-DirectoryFiles {
    param([Parameter(Mandatory = $true)][string]$Path)
    $list = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $list }
    foreach ($full in [System.IO.Directory]::GetFiles($Path)) {
        $list.Add((New-Object System.IO.FileInfo $full))
    }
    return $list
}

<#
.SYNOPSIS
判断 JavaScriptSerializer 的字典是否含有字段。不用 Contains，避免 Dictionary 没有单参数重载。
.PARAMETER Object
JSON 对象。
.PARAMETER Name
字段名。
#>
function Test-JsonField {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Object -isnot [System.Collections.IDictionary]) { return $false }
    foreach ($key in $Object.Keys) {
        if ([string]$key -eq $Name) { return $true }
    }
    return $false
}

<#
.SYNOPSIS
把 Luban JSON 值规范成字符串，供键值扩展与强类型 Global 对照。
.PARAMETER Value
JavaScriptSerializer 读出的标量或数组。
#>
function ConvertTo-CanonicalConfigString {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [string]) { return $Value }
    if ($Value -is [bool]) {
        if ($Value) { return 'true' }
        return 'false'
    }
    if ($Value -is [System.Collections.IDictionary]) {
        Stop-GenConfig -Message 'Global 字段出现对象，当前字符串规范只接受标量和数组。' -Code 4
    }
    if (($Value -is [System.Collections.IEnumerable]) -and -not ($Value -is [string])) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($item in $Value) {
            $parts.Add((ConvertTo-CanonicalConfigString -Value $item))
        }
        return '[' + ($parts -join ',') + ']'
    }
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $number = [System.Convert]::ToDecimal($Value, $invariant)
    $integral = [decimal]::Truncate($number)
    if ($number -eq $integral) {
        return [System.Convert]::ToInt64($integral).ToString($invariant)
    }
    $rounded = [decimal]::Round($number, 9, [System.MidpointRounding]::AwayFromZero)
    return $rounded.ToString('0.#########', $invariant)
}

<#
.SYNOPSIS
确认 tbglobalkv 覆盖强类型 Global 的全部字段，且 101 之前没有补丁键。
.PARAMETER DataDir
暂存 JSON 目录。
.PARAMETER ConfigVersion
本次配置版本。
#>
function Assert-GlobalKeyValues {
    param(
        [Parameter(Mandatory = $true)][string]$DataDir,
        [Parameter(Mandatory = $true)][int]$ConfigVersion
    )
    $globalPath = Join-Path $DataDir 'tbglobal.json'
    $kvPath = Join-Path $DataDir 'tbglobalkv.json'
    if (-not (Test-Path -LiteralPath $globalPath)) {
        Stop-GenConfig -Message "暂存数据缺少 tbglobal.json: $globalPath" -Code 4
    }
    if (-not (Test-Path -LiteralPath $kvPath)) {
        Stop-GenConfig -Message "暂存数据缺少 tbglobalkv.json: $kvPath" -Code 4
    }
    $globalRows = @(Read-JsonDocument -Path $globalPath)
    if ($globalRows.Count -ne 1) {
        Stop-GenConfig -Message "tbglobal.json 必须是单行，实际 $($globalRows.Count) 行。" -Code 4
    }
    $typed = $globalRows[0]
    if ($typed -isnot [System.Collections.IDictionary]) {
        Stop-GenConfig -Message 'tbglobal.json 的单行不是对象。' -Code 4
    }
    $kvRows = @(Read-JsonDocument -Path $kvPath)
    $map = @{}
    foreach ($row in $kvRows) {
        if ($row -isnot [System.Collections.IDictionary]) {
            Stop-GenConfig -Message 'tbglobalkv.json 含非对象行。' -Code 4
        }
        if (-not (Test-JsonField -Object $row -Name 'key') -or -not (Test-JsonField -Object $row -Name 'value')) {
            Stop-GenConfig -Message 'tbglobalkv.json 行缺少 key 或 value。' -Code 4
        }
        $key = [string]$row['key']
        $value = $row['value']
        if ($value -isnot [string]) {
            Stop-GenConfig -Message "键值扩展的值必须已是字符串: $key" -Code 4
        }
        if ($map.ContainsKey($key)) {
            Stop-GenConfig -Message "键值扩展出现重复键: $key" -Code 4
        }
        if ($key -eq 'm1_patch_message' -and $ConfigVersion -lt 101) {
            Stop-GenConfig -Message "ConfigVersion=$ConfigVersion 禁止键 m1_patch_message。该键只能在 101 补丁新增。" -Code 4
        }
        $map[$key] = $value
    }
    foreach ($field in @($typed.Keys)) {
        $expected = ConvertTo-CanonicalConfigString -Value $typed[$field]
        if (-not $map.ContainsKey([string]$field)) {
            Stop-GenConfig -Message "键值扩展缺少强类型 Global 字段: $field" -Code 4
        }
        if ($map[[string]$field] -cne $expected) {
            Stop-GenConfig -Message "键 $field 的字符串值 '$($map[[string]$field])' 与强类型规范值 '$expected' 不一致。" -Code 4
        }
    }
}

<#
.SYNOPSIS
为暂存 JSON 写清单。清单文件自己不进入 files，避免哈希自引用。
.PARAMETER DataDir
暂存 JSON 目录。
.PARAMETER ConfigVersion
配置版本。
#>
function Write-ConfigManifest {
    param(
        [Parameter(Mandatory = $true)][string]$DataDir,
        [Parameter(Mandatory = $true)][int]$ConfigVersion
    )
    $manifestPath = Join-Path $DataDir 'config_manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        Remove-OwnedFile -Path $manifestPath -RepoRoot $script:RepoRootFull
    }
    $items = New-Object System.Collections.Generic.List[object]
    $files = @(Get-DirectoryFiles -Path $DataDir | Sort-Object -Property Name)
    foreach ($file in $files) {
        if ($file.Extension -ne '.json') { continue }
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $items.Add([pscustomobject]@{
            Name = $file.Name
            Sha = (Get-Sha256Hex -Path $file.FullName)
            Length = [int64]$bytes.LongLength
        })
    }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('{')
    [void]$builder.AppendLine('  "schema": "m1.config-manifest.v1",')
    [void]$builder.AppendLine("  `"configVersion`": $ConfigVersion,")
    [void]$builder.AppendLine('  "globals": {')
    [void]$builder.AppendLine('    "file": "tbglobalkv.json",')
    [void]$builder.AppendLine('    "format": "luban-map",')
    [void]$builder.AppendLine('    "keyField": "key",')
    [void]$builder.AppendLine('    "valueField": "value",')
    [void]$builder.AppendLine('    "valueType": "string",')
    [void]$builder.AppendLine('    "typedGlobalFile": "tbglobal.json",')
    [void]$builder.AppendLine('    "aotTypes": ["CloudIsle.LubanCfg.GlobalKv", "CloudIsle.LubanCfg.TbGlobalKv"],')
    [void]$builder.AppendLine('    "snapshotShape": "ConfigSnapshot(int Version, IReadOnlyDictionary<string,string> Globals)",')
    [void]$builder.AppendLine('    "keyGrowth": "add rows only; generated GlobalKv/TbGlobalKv types stay unchanged",')
    [void]$builder.AppendLine('    "forbiddenBefore101": ["m1_patch_message"]')
    [void]$builder.AppendLine('  },')
    [void]$builder.AppendLine('  "files": [')
    for ($i = 0; $i -lt $items.Count; $i++) {
        $item = $items[$i]
        $comma = ''
        if ($i -lt ($items.Count - 1)) { $comma = ',' }
        [void]$builder.AppendLine("    {`"name`": `"$($item.Name)`", `"sha256`": `"$($item.Sha)`", `"length`": $($item.Length)}$comma")
    }
    [void]$builder.AppendLine('  ]')
    [void]$builder.Append('}')
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($manifestPath, $builder.ToString(), $utf8)
    $written = [System.IO.File]::ReadAllText($manifestPath)
    if ($written.Contains('"name": "config_manifest.json"')) {
        Stop-GenConfig -Message '清单把自身写入了 files，哈希自引用。' -Code 4
    }
}

<#
.SYNOPSIS
判断数据文件是否属于本工具拥有的生成结果。
.PARAMETER Name
文件名。
.PARAMETER ListedNames
上一份清单里出现过的文件名。
#>
function Test-OwnedDataName {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowEmptyCollection()][string[]]$ListedNames
    )
    if ($Name -eq 'config_manifest.json') { return $true }
    if ($Name -match '^tb[a-z0-9_]+\.json$') { return $true }
    if ($ListedNames -contains $Name) { return $true }
    return $false
}

<#
.SYNOPSIS
判断生成代码文件是否属于本工具，手写的同目录文件返回否。
.PARAMETER Path
文件绝对路径。
.PARAMETER ListedNames
上一份代码清单中的文件名。
#>
function Test-OwnedCodeFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyCollection()][string[]]$ListedNames
    )
    $name = [System.IO.Path]::GetFileName($Path)
    if ($name -eq 'gen_config_code_manifest.json') { return $true }
    if ($ListedNames -contains $name) { return $true }
    if ([System.IO.Path]::GetExtension($name) -ne '.cs') { return $false }
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $buffer = New-Object byte[] 512
        $count = $stream.Read($buffer, 0, $buffer.Length)
    }
    finally {
        $stream.Dispose()
    }
    if ($count -le 0) { return $false }
    $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $count)
    return $text.Contains('This code was generated by a tool')
}

<#
.SYNOPSIS
读取旧清单里的 files[].name。文件不存在时返回空列表。
.PARAMETER Path
config_manifest.json 或代码清单。
.PARAMETER CodeManifest
为真时读取代码清单的 files 字符串数组。
#>
function Read-ListedNames {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$CodeManifest
    )
    $names = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $doc = Read-JsonDocument -Path $Path
    if ($CodeManifest) {
        if ($doc -is [System.Collections.IDictionary] -and (Test-JsonField -Object $doc -Name 'files')) {
            foreach ($name in @($doc['files'])) { $names.Add([string]$name) }
        }
        return @($names)
    }
    if ($doc -is [System.Collections.IDictionary] -and (Test-JsonField -Object $doc -Name 'files')) {
        foreach ($row in @($doc['files'])) {
            if ($row -is [System.Collections.IDictionary] -and (Test-JsonField -Object $row -Name 'name')) {
                $names.Add([string]$row['name'])
            }
        }
    }
    return @($names)
}

<#
.SYNOPSIS
记录发布目录的现有文件哈希，并把文件复制到本次暂存备份。
.PARAMETER Target
包含 Name、Path、Kind 的发布目标。
.PARAMETER BackupRoot
本次备份根目录。
#>
function Backup-Target {
    param(
        [Parameter(Mandatory = $true)]$Target,
        [Parameter(Mandatory = $true)][string]$BackupRoot
    )
    $path = $Target.Path
    $existed = Test-Path -LiteralPath $path
    $files = @{}
    $backupDir = Join-Path $BackupRoot $Target.Name
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    if ($existed) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            Stop-GenConfig -Message "发布路径被文件占用，拒绝覆盖: $path" -Code 5
        }
        foreach ($file in (Get-DirectoryFiles -Path $path)) {
            $files[$file.Name] = Get-Sha256Hex -Path $file.FullName
            $destination = Join-Path $backupDir $file.Name
            [System.IO.File]::Copy($file.FullName, $destination, $true)
        }
    }
    $listed = @()
    if ($Target.Kind -eq 'data') {
        $listed = @(Read-ListedNames -Path (Join-Path $path 'config_manifest.json'))
    }
    else {
        $listed = @(Read-ListedNames -Path (Join-Path $path 'gen_config_code_manifest.json') -CodeManifest)
    }
    return @{
        Name = $Target.Name
        Path = $path
        Kind = $Target.Kind
        Existed = [bool]$existed
        Files = $files
        Listed = @($listed)
        BackupDir = $backupDir
        Mutated = $false
    }
}

<#
.SYNOPSIS
把暂存结果写入一个正式目录，并只删除本工具拥有的陈旧生成文件。
.PARAMETER State
Backup-Target 返回的状态。
.PARAMETER SourceDir
暂存中的代码目录或数据目录。
.PARAMETER RepoRoot
工程根。
#>
function Publish-Target {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    $created = $false
    if (-not (Test-Path -LiteralPath $State.Path)) {
        New-Item -ItemType Directory -Force -Path $State.Path | Out-Null
        $created = $true
    }
    $State.Created = $created
    $State.Mutated = $true
    $incoming = @{}
    foreach ($file in (Get-DirectoryFiles -Path $SourceDir)) {
        $incoming[$file.Name] = $true
        $destination = Join-Path $State.Path $file.Name
        if (Test-Path -LiteralPath $destination) {
            $existing = Get-Item -LiteralPath $destination
            if ($existing.PSIsContainer) {
                Stop-GenConfig -Message "发布目标是目录而不是文件: $destination" -Code 5
            }
            if ($existing.IsReadOnly) { $existing.IsReadOnly = $false }
        }
        [System.IO.File]::Copy($file.FullName, $destination, $true)
    }
    if ($State.Kind -eq 'code') {
        $names = @($incoming.Keys | Sort-Object)
        $codeManifest = New-Object System.Text.StringBuilder
        [void]$codeManifest.AppendLine('{')
        [void]$codeManifest.AppendLine('  "schema": "m1.config-code-manifest.v1",')
        [void]$codeManifest.AppendLine('  "files": [')
        for ($i = 0; $i -lt $names.Count; $i++) {
            $comma = ''
            if ($i -lt ($names.Count - 1)) { $comma = ',' }
            [void]$codeManifest.AppendLine("    `"$($names[$i])`"$comma")
        }
        [void]$codeManifest.AppendLine('  ]')
        [void]$codeManifest.Append('}')
        $manifestPath = Join-Path $State.Path 'gen_config_code_manifest.json'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($manifestPath, $codeManifest.ToString(), $utf8)
        $incoming['gen_config_code_manifest.json'] = $true
    }
    foreach ($file in (Get-DirectoryFiles -Path $State.Path)) {
        if ($incoming.ContainsKey($file.Name)) { continue }
        $owned = $false
        if ($State.Kind -eq 'data') {
            $owned = Test-OwnedDataName -Name $file.Name -ListedNames $State.Listed
        }
        else {
            $owned = Test-OwnedCodeFile -Path $file.FullName -ListedNames $State.Listed
        }
        if ($owned) {
            Remove-OwnedFile -Path $file.FullName -RepoRoot $RepoRoot
        }
    }
}

<#
.SYNOPSIS
用备份把一个已经写过的发布目录恢复到发布前。
.PARAMETER State
Backup-Target 的状态。发布未开始时直接返回。
.PARAMETER RepoRoot
工程根。
#>
function Restore-Target {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    if (-not $State.Mutated) { return }
    if (-not $State.Existed) {
        if (Test-Path -LiteralPath $State.Path) {
            foreach ($file in (Get-DirectoryFiles -Path $State.Path)) {
                Remove-OwnedFile -Path $file.FullName -RepoRoot $RepoRoot
            }
            $left = @([System.IO.Directory]::GetFileSystemEntries($State.Path))
            if ($left.Count -eq 0) {
                $dir = Resolve-UnderWorkspace -Path $State.Path -Root $RepoRoot
                Remove-Item -LiteralPath $dir -Force
            }
        }
    }
    else {
        foreach ($name in @($State.Files.Keys)) {
            $source = Join-Path $State.BackupDir $name
            $destination = Join-Path $State.Path $name
            if (Test-Path -LiteralPath $destination) {
                $existing = Get-Item -LiteralPath $destination
                if ($existing.IsReadOnly) { $existing.IsReadOnly = $false }
            }
            if (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Force
            }
            [System.IO.File]::Copy([string]$source, [string]$destination)
        }
        foreach ($file in (Get-DirectoryFiles -Path $State.Path)) {
            if (-not $State.Files.ContainsKey($file.Name)) {
                Remove-OwnedFile -Path $file.FullName -RepoRoot $RepoRoot
            }
        }
    }
    if (-not $State.Existed) {
        if (Test-Path -LiteralPath $State.Path) {
            Stop-GenConfig -Message "回滚后目录仍在: $($State.Path)" -Code 5
        }
        return
    }
    $now = @{}
    foreach ($file in (Get-DirectoryFiles -Path $State.Path)) {
        $now[$file.Name] = Get-Sha256Hex -Path $file.FullName
    }
    foreach ($name in @($State.Files.Keys)) {
        if (-not $now.ContainsKey($name) -or $now[$name] -cne $State.Files[$name]) {
            Stop-GenConfig -Message "回滚后哈希与发布前不一致: $($State.Path)\$name" -Code 5
        }
    }
    foreach ($name in @($now.Keys)) {
        if (-not $State.Files.ContainsKey($name)) {
            Stop-GenConfig -Message "回滚后仍有发布前不存在的文件: $($State.Path)\$name" -Code 5
        }
    }
}

<#
.SYNOPSIS
确认三个数据目录的已拥有 JSON 与清单一致，且清单哈希不自引用。
.PARAMETER Directories
三个正式 JSON 目录。
#>
function Assert-PublishedData {
    param([Parameter(Mandatory = $true)][string[]]$Directories)
    $reference = $null
    foreach ($dir in $Directories) {
        $manifestPath = Join-Path $dir 'config_manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            Stop-GenConfig -Message "发布后缺少清单: $manifestPath" -Code 5
        }
        $doc = Read-JsonDocument -Path $manifestPath
        if ([int]$doc['configVersion'] -ne $script:ExpectedVersion) {
            Stop-GenConfig -Message "清单版本不是 $($script:ExpectedVersion): $manifestPath" -Code 5
        }
        if ([string]$doc['schema'] -ne 'm1.config-manifest.v1') {
            Stop-GenConfig -Message "清单 schema 不符: $manifestPath" -Code 5
        }
        $expected = @{}
        foreach ($row in @($doc['files'])) {
            $name = [string]$row['name']
            if ($name -eq 'config_manifest.json') {
                Stop-GenConfig -Message "清单哈希自引用: $manifestPath" -Code 5
            }
            $full = Join-Path $dir $name
            $actual = Get-Sha256Hex -Path $full
            $length = (Get-Item -LiteralPath $full).Length
            if ($actual -cne [string]$row['sha256'] -or [int64]$length -ne [int64]$row['length']) {
                Stop-GenConfig -Message "清单与文件字节不一致: $full" -Code 5
            }
            $expected[$name] = $actual
        }
        $kvName = [string]$doc['globals']['file']
        if (-not $expected.ContainsKey($kvName)) {
            Stop-GenConfig -Message "清单没有覆盖键值文件哈希: $kvName" -Code 5
        }
        foreach ($file in (Get-DirectoryFiles -Path $dir)) {
            $owned = Test-OwnedDataName -Name $file.Name -ListedNames @($expected.Keys)
            if ($owned -and -not $expected.ContainsKey($file.Name) -and $file.Name -ne 'config_manifest.json') {
                Stop-GenConfig -Message "发布目录仍有陈旧生成文件: $($file.FullName)" -Code 5
            }
        }
        $pair = @{
            Manifest = (Get-Sha256Hex -Path $manifestPath)
            Files = $expected
        }
        if ($null -eq $reference) {
            $reference = $pair
            continue
        }
        if ($reference.Manifest -cne $pair.Manifest) {
            Stop-GenConfig -Message "两端清单字节不一致: $dir" -Code 5
        }
        foreach ($name in @($reference.Files.Keys)) {
            if ($reference.Files[$name] -cne $pair.Files[$name]) {
                Stop-GenConfig -Message "两端文件哈希不一致: $dir $name" -Code 5
            }
        }
    }
}

<#
.SYNOPSIS
调用 Luban。dll 走 dotnet，exe 直接执行。
.PARAMETER ExporterPath
导出器文件。
.PARAMETER ConfPath
luban.conf 绝对路径。
.PARAMETER CodeDir
暂存代码目录。
.PARAMETER DataDir
暂存数据目录。
#>
function Invoke-LubanExporter {
    param(
        [Parameter(Mandatory = $true)][string]$ExporterPath,
        [Parameter(Mandatory = $true)][string]$ConfPath,
        [Parameter(Mandatory = $true)][string]$CodeDir,
        [Parameter(Mandatory = $true)][string]$DataDir
    )
    $extension = [System.IO.Path]::GetExtension($ExporterPath)
    $lubanArgs = @(
        '--conf', $ConfPath,
        '-t', 'all',
        '-c', 'cs-simple-json',
        '-d', 'json',
        '-x', "outputCodeDir=$CodeDir",
        '-x', "outputDataDir=$DataDir",
        '--validationFailAsError'
    )
    Write-Host "==> Luban $($lubanArgs -join ' ')"
    if ($extension -eq '.dll') {
        & dotnet $ExporterPath @lubanArgs
    }
    elseif ($extension -eq '.exe') {
        & $ExporterPath @lubanArgs
    }
    else {
        Stop-GenConfig -Message "ExporterPath 只支持 .dll 或 .exe: $ExporterPath" -Code 3
    }
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        if ($null -eq $code) { $code = 1 }
        Stop-GenConfig -Message "导出器失败，exit=$code。正式目录未更新。" -Code $code
    }
}

<#
.SYNOPSIS
走旧 Python 导表。与 M1 四个参数同时出现时拒绝。
.PARAMETER RepoOfScript
本脚本所在工程根，用来定位 gen_config.py。
.PARAMETER LegacyArgs
转发给 Python 的参数。
#>
function Invoke-LegacyGenConfig {
    param(
        [Parameter(Mandatory = $true)][string]$RepoOfScript,
        [AllowEmptyCollection()][string[]]$LegacyArgs
    )
    $pythonScript = Join-Path $RepoOfScript 'Tools\gen_config.py'
    if (-not (Test-Path -LiteralPath $pythonScript)) {
        [Console]::Error.WriteLine("缺少旧导表脚本: $pythonScript")
        exit 1
    }
    Write-Host "==> legacy python $pythonScript"
    & python $pythonScript @LegacyArgs
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 1 }
    exit $code
}

$repoOfScript = Split-Path -Parent $PSScriptRoot
$remainingArgs = New-Object System.Collections.Generic.List[string]
if ($null -ne $Remaining) {
    foreach ($token in $Remaining) {
        if (-not [string]::IsNullOrWhiteSpace([string]$token)) {
            $remainingArgs.Add([string]$token)
        }
    }
}
$legacyRequested = [bool]$Legacy
foreach ($token in $remainingArgs) {
    if ($token -eq '--xlsx' -or $token -eq '--set-global' -or $token -eq '-xlsx' -or $token -eq '-set-global') {
        $legacyRequested = $true
    }
}
$m1Bound = $PSBoundParameters.ContainsKey('RepoRoot') -or $PSBoundParameters.ContainsKey('SourceDir') -or $PSBoundParameters.ContainsKey('ExporterPath') -or $PSBoundParameters.ContainsKey('ConfigVersion')
if ($legacyRequested -and $m1Bound) {
    $boundNames = @($PSBoundParameters.Keys) -join ','
    $rest = @($remainingArgs) -join ','
    [Console]::Error.WriteLine('旧入口与 -RepoRoot/-SourceDir/-ExporterPath/-ConfigVersion 不能同时使用。')
    exit 2
}
if ($legacyRequested) {
    Invoke-LegacyGenConfig -RepoOfScript $repoOfScript -LegacyArgs @($remainingArgs.ToArray())
}
if (-not $m1Bound) {
    [Console]::Error.WriteLine('M1 入口需要 -RepoRoot -SourceDir -ExporterPath -ConfigVersion。PoC 请使用 -Legacy，或传入 --xlsx / --set-global。')
    exit 2
}
if ($remainingArgs.Count -gt 0) {
    [Console]::Error.WriteLine("无法识别的参数: $($remainingArgs -join ' ')")
    exit 2
}

try {
    if ([string]::IsNullOrWhiteSpace($RepoRoot) -or [string]::IsNullOrWhiteSpace($SourceDir) -or [string]::IsNullOrWhiteSpace($ExporterPath)) {
        Stop-GenConfig -Message 'RepoRoot、SourceDir、ExporterPath 都不能为空。' -Code 2
    }
    if ($ConfigVersion -lt 1) {
        Stop-GenConfig -Message "ConfigVersion 必须是正整数，实际 $ConfigVersion。" -Code 2
    }
    $script:ExpectedVersion = $ConfigVersion
    if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
        Stop-GenConfig -Message "RepoRoot 不是已有目录: $RepoRoot" -Code 2
    }
    $script:RepoRootFull = [System.IO.Path]::GetFullPath($RepoRoot)
    $sourceFull = Resolve-RepoPath -Path $SourceDir -RepoRoot $script:RepoRootFull
    $exporterFull = Resolve-RepoPath -Path $ExporterPath -RepoRoot $script:RepoRootFull
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
        Stop-GenConfig -Message "SourceDir 不是已有目录: $sourceFull" -Code 2
    }
    $confPath = Join-Path $sourceFull 'luban.conf'
    if (-not (Test-Path -LiteralPath $confPath -PathType Leaf)) {
        Stop-GenConfig -Message "SourceDir 下没有 luban.conf: $confPath" -Code 2
    }
    foreach ($required in @('Datas', 'Defines')) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceFull $required) -PathType Container)) {
            Stop-GenConfig -Message "SourceDir 缺少 $required : $sourceFull" -Code 2
        }
    }
    if (-not (Test-Path -LiteralPath $exporterFull -PathType Leaf)) {
        Stop-GenConfig -Message "ExporterPath 不存在: $exporterFull" -Code 3
    }
    $exporterExtension = [System.IO.Path]::GetExtension($exporterFull)
    if ($exporterExtension -ne '.dll' -and $exporterExtension -ne '.exe') {
        Stop-GenConfig -Message "ExporterPath 只支持 .dll 或 .exe: $exporterFull" -Code 3
    }

    $runId = [guid]::NewGuid().ToString('n')
    $script:StagingPath = Join-Path $script:RepoRootFull "Temp\gen_config\$runId"
    $codeStage = Join-Path $script:StagingPath 'code'
    $dataStage = Join-Path $script:StagingPath 'data'
    $backupRoot = Join-Path $script:StagingPath 'backup'
    New-Item -ItemType Directory -Force -Path $codeStage, $dataStage, $backupRoot | Out-Null

    Invoke-LubanExporter -ExporterPath $exporterFull -ConfPath $confPath -CodeDir $codeStage -DataDir $dataStage
    Assert-GlobalKeyValues -DataDir $dataStage -ConfigVersion $ConfigVersion
    Write-ConfigManifest -DataDir $dataStage -ConfigVersion $ConfigVersion

    $publishTargets = @(
        @{ Name = 'code'; Kind = 'code'; Path = (Join-Path $script:RepoRootFull 'Assets\Config\Generated') },
        @{ Name = 'output'; Kind = 'data'; Path = (Join-Path $script:RepoRootFull 'Shared\config\Output\json') },
        @{ Name = 'server'; Kind = 'data'; Path = (Join-Path $script:RepoRootFull 'Server\ConfigLuban') },
        @{ Name = 'client'; Kind = 'data'; Path = (Join-Path $script:RepoRootFull 'Assets\StreamingAssets\ConfigLuban') }
    )
    $states = New-Object System.Collections.Generic.List[object]
    foreach ($target in $publishTargets) {
        $states.Add((Backup-Target -Target $target -BackupRoot $backupRoot))
    }
    $script:Targets = $states
    $script:PublishStarted = $true
    $index = 0
    foreach ($state in $states) {
        $from = $dataStage
        if ($state.Kind -eq 'code') { $from = $codeStage }
        Publish-Target -State $state -SourceDir $from -RepoRoot $script:RepoRootFull
        $index += 1
        if ($env:GEN_CONFIG_FAIL_POINT -eq 'AfterFirstPublish' -and $index -eq 1) {
            Stop-GenConfig -Message '验证钩子 GEN_CONFIG_FAIL_POINT=AfterFirstPublish：第一个目录已写，随后回滚。这不是跨目录原子事务。' -Code 5
        }
    }
    Assert-PublishedData -Directories @(
        (Join-Path $script:RepoRootFull 'Shared\config\Output\json'),
        (Join-Path $script:RepoRootFull 'Server\ConfigLuban'),
        (Join-Path $script:RepoRootFull 'Assets\StreamingAssets\ConfigLuban')
    )
    Write-Host "OK ConfigVersion=$ConfigVersion code=$(Join-Path $script:RepoRootFull 'Assets\Config\Generated')"
}
catch {
    $message = $_.Exception.Message
    if ($script:PublishStarted) {
        Write-Host '==> 发布未完成，按目录备份恢复已修改的目标'
        # 一个目录恢复失败仍继续其余目录；保留所有备份以便后续恢复。
        foreach ($state in $script:Targets) {
            try {
                Restore-Target -State $state -RepoRoot $script:RepoRootFull
            }
            catch {
                $script:PreserveStaging = $true
                $message += [Environment]::NewLine + "Rollback failed ($($state.Name)): " + $_.Exception.Message
                $script:ExitCode = 5
            }
        }
        if ($script:PreserveStaging) {
            $message += [Environment]::NewLine + "Recovery backup retained: $($script:StagingPath)"
        }
    }
    if ($script:ExitCode -eq 0) { $script:ExitCode = 1 }
    [Console]::Error.WriteLine($message)
}
finally {
    if ($script:StagingPath -and -not $script:PreserveStaging) {
        try {
            Remove-OwnStaging -Path $script:StagingPath -RepoRoot $script:RepoRootFull
        }
        catch {
            [Console]::Error.WriteLine("清理暂存失败: $($_.Exception.Message)")
            if ($script:ExitCode -eq 0) { $script:ExitCode = 5 }
        }
    }
}

exit $script:ExitCode
```

#### 脚本：Tools/gen_luban.ps1

```powershell
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
```



### T41 协调复核：回滚失败保留恢复材料（2026-10-19）

- [x] 复核Cursor原15次命令证据；对回滚本身失败做独立隔离测试，修复后重验成功与故障路径。
- [x] 上方Tools/gen_config.ps1完整源码已更新为协调复核最终版本；下面保留完整可重跑专项脚本。

**问题场景**：四个目录已发布后注入失败，随后第一项代码目录回滚再次抛错。注入只发生在Temp隔离副本的脚本中，正式源表和正式产物不参与故障注入。

**症状与具体原因**：旧catch把整个恢复循环包在一个try里，第一项失败就退出循环；finally仍删除整个staging，其中含唯一备份。修复前真实报告remaining_targets_restored=false、backup_retained=false。非0退出本身不能证明恢复成功。

**修复**：每个目录分别try/catch，某项失败仍继续其余目录；标记PreserveStaging，退出码5并明确打印Recovery backup retained及绝对路径；存在任何恢复失败时finally保留全部恢复材料。导出失败尚未发布、成功发布、或全部恢复成功时仍清理本次暂存。备份不是跨目录原子事务，也不是自动保证磁盘故障可恢复。

| 验证名称 | 具体步骤 | 预期结果 | 实际结果 |
|---|---|---|---|
| 修复前故障复现 | Temp副本发布101，第四个目录后失败，代码目录恢复再抛错 | 观察是否停止恢复并丢备份 | 已复现；T41-recovery-before-fix.json及console：其余目录未恢复、备份未保留 |
| 最终恢复专项 | 使用下方T41RecoveryReview.py建立全新副本，重复同一注入 | 生成器非0；其余三目录完整哈希恢复；备份保留；明确路径；验收脚本自身0 | 全部通过；T41-recovery-final.json及console，5项检查为true。生成器故障退出5是预期，验收脚本退出0 |
| 最终正式成功路径 | 四参数导出100，再直接dotnet build --no-restore | 导出0；编译0且无警告错误 | 通过。T41-review-export-console.txt、T41-review-build-console.txt；编译1.42秒，0警告0错误 |
| 三端版本/哈希/Global | 解析config_manifest并逐个计算SHA256/长度，比较三目录，检查字符串与补丁键 | 版本100；18数据文件+清单一致；22字符串键；无m1_patch_message | 通过。T41-review-hashes.json全部成立 |

**额外执行问题**：Cursor完成后已按规则删除Temp/t41-run.ps1。协调者尝试重跑时确认文件不存在，未把这次失败当成功；输出保留为T41-review-post-missing-helper.txt。原15次验证各自有真实结果，最终成功/故障复核改用保存在验收目录中的独立脚本，避免再引用已清理的临时测试器。

实际最终命令（工程根目录）：

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en-US'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'Tools/gen_config.ps1' -RepoRoot 'E:\Workbench\UnityWorkBench\Farming' -SourceDir 'Shared/config' -ExporterPath 'Tools/Luban/Luban/Luban.dll' -ConfigVersion 100 > 'Docs/Acceptance/M1/20261019-T41/T41-review-export-console.txt'
dotnet build 'Server/CloudIsle.Config/CloudIsle.Config.csproj' --no-restore --nologo > 'Docs/Acceptance/M1/20261019-T41/T41-review-build-console.txt'
python 'Docs/Acceptance/M1/20261019-T41/T41RecoveryReview.py' final
```

#### 完整专项验证脚本：E:\Workbench\UnityWorkBench\Farming\Docs\Acceptance\M1\20261019-T41\T41RecoveryReview.py

```python
"""Exercise T41 rollback failure in an isolated copy; never write production outputs."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys
import uuid


def hashes(directory):
    """Return top-level file hashes for one owned fixture output directory."""
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
            for p in directory.iterdir() if p.is_file()}


def main(label):
    """Copy baseline outputs, inject failures, and check recovery; label names evidence files."""
    root = next(parent for parent in Path(__file__).resolve().parents
                if (parent / 'Tools/gen_config.ps1').is_file() and (parent / 'Assets').is_dir())
    evidence = root / 'Docs/Acceptance/M1/20261019-T41'
    fixture = root / 'Temp' / ('M1-T41-recovery-' + uuid.uuid4().hex)
    fixture.mkdir(parents=True)
    outputs = ['Assets/Config/Generated', 'Shared/config/Output/json',
               'Server/ConfigLuban', 'Assets/StreamingAssets/ConfigLuban']
    before = {}
    for rel in outputs:
        destination = fixture / rel
        destination.mkdir(parents=True)
        for source in (root / rel).iterdir():
            if source.is_file():
                shutil.copy2(source, destination / source.name)
        before[rel] = hashes(destination)

    source = (root / 'Tools/gen_config.ps1').read_text(encoding='utf-8-sig')
    restore_marker = '    if (-not $State.Mutated) { return }'
    if source.count(restore_marker) != 1:
        raise RuntimeError('Expected one Restore-Target entry marker')
    source = source.replace(restore_marker,
        "    if ($State.Name -eq 'code') { throw 'T41_REVIEW_RESTORE_CODE_FAILED' }\n" + restore_marker)
    publish_marker = "$env:GEN_CONFIG_FAIL_POINT -eq 'AfterFirstPublish' -and $index -eq 1"
    if source.count(publish_marker) != 1:
        raise RuntimeError('Expected one publish failure marker')
    source = source.replace(publish_marker,
        "$env:GEN_CONFIG_FAIL_POINT -eq 'AfterFirstPublish' -and $index -eq 4")
    tools = fixture / 'Tools'
    tools.mkdir()
    copied = tools / 'gen_config.ps1'
    copied.write_text(source, encoding='utf-8-sig')
    wrapper = fixture / 'run.ps1'
    # Paths are generated locally; quote apostrophes according to PowerShell rules.
    quote = lambda value: "'" + str(value).replace("'", "''") + "'"
    wrapper.write_text(
        "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\n"
        "$env:GEN_CONFIG_FAIL_POINT = 'AfterFirstPublish'\n"
        f"& {quote(copied)} -RepoRoot {quote(fixture)} "
        f"-SourceDir {quote(root / 'Shared/config')} "
        f"-ExporterPath {quote(root / 'Tools/Luban/Luban/Luban.dll')} -ConfigVersion 101\n"
        "exit $LASTEXITCODE\n", encoding='utf-8-sig')
    run = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                          '-File', str(wrapper)], capture_output=True, timeout=45)
    text = run.stdout.decode('utf-8', errors='replace') + '\n--- stderr ---\n' + run.stderr.decode('utf-8', errors='replace')
    (evidence / f'T41-recovery-{label}-console.txt').write_text(
        f'EXIT {run.returncode}\nFIXTURE {fixture}\n{text}', encoding='utf-8')
    checks = {
        'nonzero_exit': run.returncode != 0,
        'fault_reached': 'T41_REVIEW_RESTORE_CODE_FAILED' in text,
        'remaining_targets_restored': all(hashes(fixture / rel) == before[rel] for rel in outputs[1:]),
        'backup_retained': any((p / 'backup/code').is_dir() and any((p / 'backup/code').iterdir())
                               for p in (fixture / 'Temp/gen_config').glob('*')),
        'backup_path_reported': ('Recovery backup retained: ' + str(fixture / 'Temp/gen_config')) in text,
    }
    report = {'fixture': str(fixture), 'exit': run.returncode, 'checks': checks}
    (evidence / f'T41-recovery-{label}.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))
    return 0 if all(checks.values()) else 1


if __name__ == '__main__':
    raise SystemExit(main(sys.argv[1]))

```

## T42 实现 ConfigService 与 ServerConfigService

- 来源：`06_M1_基础框架任务拆解.md` 第 1.2 节第 1 点、第 4.3 节、第 4.5 节、第 5.5 节 T42；依赖 T41、T14
- 需求：R-E02
- 依据：T41 正式清单 `Server/ConfigLuban/config_manifest.json`，schema `m1.config-manifest.v1`，configVersion 100，globals.file `tbglobalkv.json`，key/value 均为字符串。三端 JSON 目录同源。不使用旧 `Server/Config/manifest.json`
- 通过条件：客户端与服务端读到同一份 Global 键值；错误版本、缺表、缺必需键、坏 JSON、哈希或长度不符时明确失败，并且不对外提供半份新配置
- 日期：2026-10-19
- 范围：只做 T42。不实现 T43 HTTP，不改 `Entry.StartAsync` 正式串接（留给 T56），不预置 `m1_patch_message` 或 `PatchDemoWindow`，不手改 Luban 生成类

### 任务、流程与验证

`TbGlobal` / `TbGlobalKv` 仍由 T41 生成，本任务不解析其他表的强类型字段。共享层只把 Global 收成自己持有的字符串字典。`ConfigSnapshot.Globals` 是只读视图，调用方改自己的原字典，或把视图转成可写字典，都不能改快照里的值。

读取时先看清单 schema 和 configVersion，再核对每个登记文件的文件名、长度和 SHA256。文件名只能是单个 `小写字母、数字、下划线.json`，不能带目录，也不能越出给定根目录。`tbglobalkv.json` 必须是 `key` / `value` 字符串行。22 个基础键必须都在；ConfigVersion 小于 101 时出现 `m1_patch_message` 就失败。校验全部通过后才一次换上新快照。失败或取消留下上一份有效快照；第一次就失败则保持未加载。`GetGlobal` 遇到没加载或未知键抛异常，不返回空字符串。

服务端 `Load` 在锁内整段读完再替换。客户端 `LoadAsync` 用同一把异步锁串行，取消时不持有租约。生产客户端的 `configAddress` 是清单 TextAsset 的 YooAsset 地址。当前收集规则是 `AddressByFileName`，所以清单地址是 `config_manifest`，表地址是去掉 `.json` 的文件名，例如 `tbglobalkv`。地址不使用编辑器绝对路径。T58 若改成带目录前缀，仍用「清单地址最后一段换成表文件名」这条规则。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 服务端版本 100 | `dotnet test` 过滤 `ServerConfigServiceTests`，读取 `Server/ConfigLuban` | 退出 0；`bag_init_slots`、`water_ratio`、`bag_expand_costs`、`new_player_gem` 与导出原文一致 | |
| 缺键、坏 JSON、哈希破坏、错误版本 | 同一测试夹具改临时副本，不改正式目录 | 都抛配置加载异常；正式目录哈希不变 | |
| 重加载失败保旧 | 先成功加载，再加载坏副本或错误版本 | `GetGlobal` 仍是上一份成功值；首次失败保持未加载 | |
| 未知键 | 对已加载服务调用不存在的键 | 抛 `KeyNotFoundException`，不是空字符串 | |
| 客户端真实租约 | Unity Test Runner 跑 `ConfigServiceTests`，经 `ResMgr.LoadAsync<TextAsset>` | 与服务端同一组键值；加载后 `ConfigService` 租约数为 0 | |
| 取消、失败、并发 | 预取消、错误版本、与成功加载并行 | 临时句柄回到基线；失败不回退已成功快照；取消不留下租约 | |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。不读取电量，不创建或修改电量标记。

#### 1. 共享快照与校验

- [ ] 新增 `ConfigSnapshot`，自己复制 Global 字符串。
- [ ] 新增清单与键值校验，供服务端和客户端共用。不引用 Unity。

#### 2. 两端入口

- [ ] `ServerConfigService.Load` / `GetGlobal` 读 `config_manifest.json` 所在目录。
- [ ] `ConfigService.LoadAsync` / `GetGlobal` 用 `ResMgr` 租约读取 TextAsset，`finally` 释放。

#### 3. 真实验证

- [ ] `dotnet test` 留下 TRX。
- [ ] 连接已打开的当前工程 Unity，刷新编译后跑 `ConfigServiceTests`，留下结果和 Console。
- [ ] 证据写入 `Docs/Acceptance/M1/20261019-T42/`。全部通过后再回填实际结果，并勾选拆解表 T42。

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 具体修复与复验 |
|---|---|---|---|
| | | | |
---

## 2026-10-20 xLua 后续实施：T42 配置正式双端验收

- 来源：`06_M1_基础框架任务拆解.md` T42；Coordinator `Docs/Acceptance/M1/20261020-xLua/Coordinator.md`；StageA 提示词
- 需求：R-E02
- 日期：2026-10-20
- 范围：正式验收已有 AOT `ConfigService` / `ServerConfigService` / Shared 快照校验；只补缺口与真实验证。历史 2026-10-19 计划空表保留不动。
- 基线：AppVersion 0.2.0、ProtoVersion 1、ConfigVersion 100；证据 `Docs/Acceptance/M1/20261020-xLua/StageA/`
- 结论：**通过**（ServerConfigServiceTests 含长度不符用例；PlayMode ConfigServiceTests 4/4；双端 manifest SHA 一致）

### 任务介绍与复用

复用 Shared `ConfigSnapshot`/`ConfigSnapshotReader`、`Server/CloudIsle.Server/Config/ServerConfigService.cs`、`Assets/Framework/Config/ConfigService.cs` 与 `ConfigAddresses`。服务端读 `Server/ConfigLuban`；客户端经 ResMgr/Yoo EditorSimulate 读 `Assets/ConfigLuban`（地址 `config_manifest`）。本轮仅补清单 length 不符夹具断言，并做正式 TRX/PlayMode/双端对照。

### 最小执行步骤（完成后勾选）

#### 1. 缺口核对与补测

- [x] 补 Server 测试：清单 length 不符 → `配置文件长度不符`（写入 `ServerConfigServiceTests.Load_CorruptCopies_FailWithoutChangingOfficial`）。
- [x] 客户端预取消/错版本保旧/并发失败不覆盖/租约归零已有用例并通过。中途取消：EditorSimulate 加载过快无法稳定插入取消点，本轮以预取消 + 并发失败路径覆盖租约回基线；记入问题表。

#### 2. 真实验证

- [x] `dotnet test` 过滤含 `ServerConfigServiceTests`（同 StageA 批 17 项之一组）：失败 0。命令见 `StageA/dotnet-test-command.txt`。
- [x] 双端关键键与 manifest SHA：`StageA/t42-dual-end-report.txt`（server/assets/streaming SHA 均为 `6e44846d78645bb066fdeb590dcc68ee18134c1a6bab233742c5d93fdd5ee4e9`，所列 Global 键全等）。
- [x] Unity PlayMode `ConfigServiceTests` 4/4；租约报告 `StageA/t42-lease-baseline.txt`。
- [x] 勾选 `06_M1` T42。

### 验证表

| 验证对象 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 服务端 100 | `dotnet test` filter 含 ServerConfigServiceTests | 非 0 发现，失败 0；键值与导出一致 | 通过；与 TRX 批一并 17/17 | `StageA/StageA-dotnet.trx` |
| 坏副本失败 | 临时副本改坏，正式目录不动 | ConfigLoadException；正式哈希不变 | 通过（含长度不符） | 同上 |
| 客户端租约 | PlayMode ConfigServiceTests | 同键值；Load 后租约 0 | 4/4 Passed | `StageA/playmode-results.xml` |
| 取消/并发 | 预取消+并发错版本 | OCE/ConfigLoadException；不覆盖成功快照 | 通过 | 同上 |

### 源码要点

- `ConfigService.LoadAsync(configAddress, expectedVersion, ct)`：`configAddress` 为清单 Yoo 地址；`expectedVersion` 必须等于清单 `configVersion`；`ct` 取消不替换快照；finally 释放全部临时租约。
- `ServerConfigService.Load(directory, expectedVersion)`：`directory` 含 `config_manifest.json`；锁内整读后替换。
- `ConfigSnapshotReader.Read`：校验 schema、SHA、length、22 必需键；`<101` 禁止 `m1_patch_message`。

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 具体修复与复验 |
|---|---|---|---|
| 中途取消难稳定复现 | EditorSimulate 下 Load 过快，难在表间插入 Cancel | 无注入延迟点 | 以预取消+失败路径验证租约归零；4/4 通过 |
| 首轮含 VersionService 的 PlayMode | 早期 HttpListener 夹具导致 batchmode 卡住 | HttpListener+UniTask 组合 | 已停该批处理 Unity；T44 改 TcpListener MiniHttp 后 10/10 |

---

## 2026-10-20 xLua 后续实施：T43 PatchHttpService 与 version.json

- 来源：`06_M1` §3.1/4.5/5.5 T43
- 需求：R-S04
- 日期：2026-10-20
- 结论：**通过**（PatchHttpServiceTests 5/5；样例根 `Server/PatchContent/M1`）

### 任务介绍与流程

实现 `Server/CloudIsle.Server/Hosting/PatchHttpService.RunAsync(listenUrl, contentRoot, ct)`：HttpListener 静态服务；路径限制在 contentRoot；目录/缺失/穿越 → 404 且空体；取消停机；端口占用抛 `InvalidOperationException`。`DefaultPort=18080`；测试分配空闲端口。

Shared `VersionManifestParser`（MaxBytes=8192）校验指针字段。样例 `version.json`：MinAppVersion=0.2.0，Res/Config=100，Proto=1，PackageVersion=`"2"`（当前 Yoo 模拟契约），BaseUrl 指向约定 18080/100/。非假生产包；101 指针不提前发布。

### 最小执行步骤

- [x] Shared：`VersionManifestException` / `VersionManifestParser` / `AppVersionComparer`。
- [x] `PatchHttpService` + `Server/PatchContent/M1/version.json` + `100/sample.txt`。
- [x] NUnit：200、404/目录、穿越、取消重绑、端口占用。
- [x] 证据 StageA；勾 T43。

### 命令与参数

```
dotnet test "E:\Workbench\UnityWorkBench\Farming\Server\CloudIsle.Tests\CloudIsle.Tests.csproj" --filter "FullyQualifiedName~ServerConfigServiceTests|FullyQualifiedName~PatchHttpServiceTests|FullyQualifiedName~AppVersionComparerTests|FullyQualifiedName~VersionManifestParserTests" --logger "trx;LogFileName=StageA-dotnet.trx" --results-directory "E:\Workbench\UnityWorkBench\Farming\Docs\Acceptance\M1\20261020-xLua\StageA" -v n
```

- `RunAsync(listenUrl, contentRoot, ct)`：`listenUrl` 如 `http://127.0.0.1:<port>/`；`contentRoot` 为静态根；`ct` 取消后 Stop/Close。
- 测试端口：各用例 `TcpListener(0)` 分配，不写死 18080，避免并行冲突。

### 验证表

| 验证对象 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| version.json 200 | HttpClient GET | 正文可解析；字段齐全 | 通过 | StageA-dotnet.trx |
| 缺失/目录 | GET 不存在或 `100/` | 404，体长 0 | 通过 | 同上 |
| 路径穿越 | `../` 与 `%2e%2e/` | 404，不泄根外 | 通过 | 同上 |
| 取消/端口 | Cancel 后重绑；占用再绑 | 可取消；占用失败 | 通过 | 同上 |

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 具体修复与复验 |
|---|---|---|---|
| （本任务） | 未遇到阻塞 | — | 5 项 HTTP 集成一次通过 |

---

## 2026-10-20 xLua 后续实施：T44 VersionService

- 来源：`06_M1` §4.1 T44
- 需求：R-C01
- 日期：2026-10-20
- 结论：**通过**（Shared 比较/解析 NUnit + PlayMode VersionServiceTests 6/6；整批 PlayMode 10/10）

### 任务介绍与流程

`Assets/Framework/Boot/VersionService.cs`：`FetchAsync(Uri, ct)` 用 UnityWebRequest；`RequiresAppUpdate(installed, min)` 用 Shared 数值比较。`HostProtoVersion=1`。错误清单不降级。为 T45 返回已验证 `VersionManifest`。

### 最小执行步骤

- [x] Shared 比较与解析。
- [x] VersionService.FetchAsync / RequiresAppUpdate；包装 UnityWebRequestException → VersionManifestException。
- [x] .NET：AppVersionComparerTests 4 + VersionManifestParserTests 2。
- [x] PlayMode：成功/404/坏 JSON/取消/非法 URI + RequiresAppUpdate；MiniHttp(TcpListener)。
- [x] 勾 T44。不开始 T45。

### 源码要点

- `FetchAsync(versionUri, ct)`：仅 http/https 绝对 URI；响应非 200 或解析失败抛 `VersionManifestException`；取消抛 `OperationCanceledException`。
- `RequiresAppUpdate(installedAppVersion, minAppVersion)`：`Compare(installed,min)<0` 为 true（须重装）。
- `AppVersionComparer.Compare`：按段数值比较，故 `0.1.10 > 0.1.9`。

### 验证表

| 验证对象 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 语义版本 | Shared NUnit | 0.1.10>0.1.9；0.2.0 边界；非法失败 | 4/4 通过 | StageA-dotnet.trx |
| Fetch 成功 | PlayMode + MiniHttp | manifest 字段正确 | 通过 | playmode-results.xml |
| Fetch 失败/取消 | 404/坏JSON/Cancel/ftp | VersionManifestException 或 OCE | 通过 | 同上 |

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 具体修复与复验 |
|---|---|---|---|
| ToCoroutine 单参数 | CS0029 Manifest→Exception | UniTask\<T\>.ToCoroutine 单 Action 当结果回调 | 改为 `(result, error)` 双回调；复编译通过 |
| 404 断言失败 | 收到 UnityWebRequestException | ToUniTask 在非 Success 先抛 | catch 后包装 VersionManifestException；PlayMode 10/10 |
| 首轮 HttpListener | batchmode 卡住无 XML | 监听/停机与主线程等待不当 | 改 TcpListener MiniHttp；复跑通过 |

---

## 2026-10-20 xLua 后续实施：Stage B 计划（T45–T47）

- 来源：`Docs/Acceptance/M1/20261020-xLua/StageB-T45-T47.md`；Coordinator；`StageA-ReviewNotes.md`；`Root-StageA-review.json`
- 日期：2026-10-20
- 范围：仅 B（T45/T46/T47）。不启动 C。先修仍有效 Root Notes1/2；Notes5 已复测跳过；Notes3 Config 中途取消以真实慢 HTTP 补齐。
- 证据目录：[`Docs/Acceptance/M1/20261020-xLua/StageB/`](../../../Docs/Acceptance/M1/20261020-xLua/StageB/)
- 包边界选择：**保留双包** `DefaultPackage`（配置/Prefab/场景）+ `GameLua`（Lua），统一由 `PatchService` 候选事务同 `ResVersion`/`PackageVersion` 校验与最终 `CommitVersion`；禁止一边提交 100 另一边 101。不新建孤立 PoC；正式构建冻结仍属 T58。
- 基包边界记录：开发基包建议升至 **AppVersion=0.3.0**、`bridgeContract` 保持可扩展白名单（本轮若未新增 C# 桥则仍可用 `main-bridge-1`，并在 Coordinator 写明 T48+ 前冻结点）；ProtoVersion=1 不改 05 字段；100→101 不得再变 AOT。

### 最小执行清单（计划→实施）

#### 0. Root 复核残留

- [x] Notes1：`PatchHttpService` 路径非法→明确 4xx + Abort；下一请求仍可服务。
- [x] Notes2：`VersionService` 可配置超时；去掉重复 handler；pending 取消/超时；下一请求成功。
- [x] Notes3：Config 表间 Delay 中途取消 + Patch HTTP 差异路径；lease 归零。

#### 1. T45 PatchService

- [x] `PatchProgress` + `PatchService.PrepareAsync` / `CommitVersion`（YooAsset 3.0.6）。
- [x] Prepare 不提交；失败/取消保旧索引；零文件更新；不宣称字节续传。
- [x] 隔离 CDN 真实 HTTP 100→101 差异（files 6→3）。

#### 2. T46 PatchProgressView

- [x] 固定 C# 加载 UI + 零总量 + 重试门闸 + 消毒。
- [x] 截图与组件/gate 测试。

#### 3. T47 LuaLoader / LuaIndex

- [x] 正式服务提炼；HotfixLoader 委托 ValidateCandidate。
- [x] 拒绝路径与启停 Dispose。
- [x] Win64 IL2CPP 构建运行 pass=True。

详见下文 T45/T46/T47 正式结果小节。历史 2026-10-19 空表与 StageA 正文不改写。

---

## 2026-10-20 xLua 后续实施：T45 PatchService 差异下载与候选事务

- 来源：`06_M1` §4.1/5.5 T45；Coordinator；StageB 提示词
- 需求：R-C01
- 日期：2026-10-20
- 结论：**通过**（真实 HTTP+Yoo GameLua 100→101 差异计数；取消不提交；Root Notes1/2/3 相关补齐）
- 证据：[StageB/](../../../Docs/Acceptance/M1/20261020-xLua/StageB/)

### 任务介绍与包边界

从 ResMgr / VersionService / 迁移宿主 Yoo 下载路径提炼 `PatchService`。YooAsset **3.0.6**：`RequestPackageVersionAsync` → `LoadPackageManifestAsync` → `CreateResourceDownloader(ResourceDownloaderOptions)` → `StartDownload()`。不宣称字节续传。

**包边界选择**：保留双包能力（默认 `DefaultPackage`+`GameLua`），同一候选事务要求远端 `PackageVersion` 与清单一致；本轮真实 HTTP 验证以隔离 CDN 上的 **GameLua 100→101** 为主（DefaultPackage 正式 Host 产物对齐仍属 T58）。活动索引 `active-version.json` 原子 `File.Replace`，Prepare 不提交。

### Root 复核先修

| Notes | 处理 |
|---|---|
| 1 PatchHttp `%00`/非法路径 | 返回 4xx + Abort；`RunAsync_NullBytePath_Returns4xx_ThenNextRequestOk` |
| 2 VersionService 超时/pending 取消 | `TimeoutSeconds`；去掉重复 DownloadHandler；PlayMode 超时+取消后再成功 |
| 3 Config 中途取消 | `TestInterTableDelayMs` + `Load_MidCancel_WhileTablesPending_ReleasesLeases`；慢 HTTP 另由 Patch 路径覆盖 |
| 5 404 包装 | 已在 A 复测，跳过 |

### 最小执行步骤

- [x] 实现 [`PatchProgress.cs`](../../Framework/Boot/PatchProgress.cs) / [`PatchService.cs`](../../Framework/Boot/PatchService.cs)
- [x] 隔离 CDN：[`StageB/scripts/Prepare-CdnIsolated.ps1`](../../../Docs/Acceptance/M1/20261020-xLua/StageB/scripts/Prepare-CdnIsolated.ps1)（不改迁移归档）
- [x] PlayMode `PatchServiceTests`；差异计数写入 `patch-diff-counts.txt`
- [x] 勾选 `06_M1` T45

### 命令

见 [dotnet-test-command.txt](../../../Docs/Acceptance/M1/20261020-xLua/StageB/dotnet-test-command.txt)、[playmode-command.txt](../../../Docs/Acceptance/M1/20261020-xLua/StageB/playmode-command.txt)。

### 验证表

| 验证对象 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| Notes1 HTTP | `PatchHttpServiceTests` 含 `%00` | 4xx 后下一请求 200 | 18/18 .NET 通过 | [StageB-dotnet.trx](../../../Docs/Acceptance/M1/20261020-xLua/StageB/StageB-dotnet.trx) |
| Notes2 超时/取消 | VersionServiceTests 新用例 | 超时→VersionManifestException；取消后再成功 | PlayMode 批内通过 | [playmode-results.xml](../../../Docs/Acceptance/M1/20261020-xLua/StageB/playmode-results.xml) |
| Config 中途取消 | 表间 Delay 后 Cancel | OCE；lease=0；无快照 | 通过 | 同上 |
| 100→101 差异 | 隔离 CDN HTTP+Yoo | 101 下载文件≤100；真实 HTTP 次数 | files100=6→files101=3；http101=6 | [patch-diff-counts.txt](../../../Docs/Acceptance/M1/20261020-xLua/StageB/patch-diff-counts.txt) |
| 取消不提交 | Prepare 预取消 | 索引仍 101 | 通过 | PatchServiceTests |
| 无 Prepare 提交 | Commit 直接调 | 抛错；无索引 | 通过 | 同上 |

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 修复与复验 |
|---|---|---|---|
| PlayMode HttpListener | 历史 batchmode 卡死 | 监听组合 | FileHttp 用 TcpListener；26/26 |
| 双包同 BaseUrl | DefaultPackage 与 GameLua CDN 布局不同 | 正式产物未统一 | 事务 API 支持多包；本轮 HTTP 验 GameLua；记入 Coordinator |

---

## 2026-10-20 xLua 后续实施：T46 PatchProgressView

- 来源：`06_M1` T46
- 日期：2026-10-20
- 结论：**通过**
- 源码：[PatchProgressView.cs](../../Framework/Boot/PatchProgressView.cs)

### 验证表

| 验证对象 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 零总量 | Render TotalBytes=0 Ready | value=1；无除零 | 通过 | PatchProgressViewTests |
| 重试门闸 | 连点 InvokeRetry | 只触发 1 次直至 Release | 通过 | 同上 |
| 错误消毒 | ShowError 含 stack/Bearer | 不展示堆栈/凭据 | 通过 | 同上 |
| 界面截图 | 失败态可视化 | 有 PNG；Player 实机另有截图 | [shot-patch-progress-error.png](../../../Docs/Acceptance/M1/20261020-xLua/StageB/shot-patch-progress-error.png)；实机 [shot-player-lua-builtin-100.png](../../../Docs/Acceptance/M1/20261020-xLua/StageB/shot-player-lua-builtin-100.png) | StageB |

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 修复与复验 |
|---|---|---|---|
| Camera 烘焙 Canvas | PNG 全灰 | batchmode 无有效 UI GPU 帧 | 用 System.Drawing 按 ShowError 后真实字段/布局比例出图，并保留 Player 实机截图；组件 gate 测试已通过 |

---

## 2026-10-20 xLua 后续实施：T47 LuaLoader / LuaIndex

- 来源：`06_M1` T47
- 日期：2026-10-20
- 结论：**通过**（正式服务提炼 + PlayMode + EditMode + Win64 IL2CPP 构建运行启停）
- 源码：[LuaIndex.cs](../../Framework/Lua/LuaIndex.cs)、[LuaLoader.cs](../../Framework/Boot/LuaLoader.cs)；HotfixLoader.ValidateCandidate 委托正式服务（GUID 保留）

### 版本边界

- 本轮未新增账号/UI C# 桥；`bridgeContract=main-bridge-1`，`AppVersion` 仍 **0.2.0**，`ProtoVersion=1`。
- **T48+ 新增桥接前**须统一开发基包建议 **0.3.0** 并在 T58 冻结；100→101 不得再改 AOT。

### 验证表

| 验证对象 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 结构拒绝 | 重复模块/坏桥/语法 | Prepare 抛错 | 通过 | LuaLoaderTests |
| 内置启停 | Prepare+Start+Tick+Dispose | IsReady 翻转 | 通过 | 同上 |
| 迁移风险 | EditMode XLuaMigrationRiskTests | 7/7 | 7/7 | [editmode-results.xml](../../../Docs/Acceptance/M1/20261020-xLua/StageB/editmode-results.xml) |
| IL2CPP | StageB BatchBuild + Player `--lua-quit` | 构建成功；pass=True；Dispose 清洁 | backend=IL2CPP；pass=True；liveSubscriptions=0 | [player-build-report.txt](../../../Docs/Acceptance/M1/20261020-xLua/StageB/player-build-report.txt)、[player-formal-lua-host.txt](../../../Docs/Acceptance/M1/20261020-xLua/StageB/player-formal-lua-host.txt) |
| 截图 | Player 内置 100 | Entry OK 可读 | 有 | shot-player-lua-builtin-100.png |

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 修复与复验 |
|---|---|---|---|
| （本任务） | 未遇阻塞性失败 | — | PlayMode+IL2CPP 一次通过 |

### PlayMode 总览

整批 filter `ConfigServiceTests|VersionServiceTests|PatchServiceTests|PatchProgressViewTests|LuaLoaderTests|XLuaBootCancelTests`：**26 / 26 / 0**。


---

## 2026-10-20 xLua StageB 复核修正：T45–T47（正式链路与可见 UI）

- 来源：StageB-FixPrompt / ReviewNotes1–7、9
- 日期：2026-10-20（复核日）
- 结论：**T45–T47 复核通过**（旧空图/旧 Hotfix Native 不改写；由 Review/ 新证据补齐）
- 证据根：`Docs/Acceptance/M1/20261020-xLua/StageB/Review/`

### 计划

1. 核实最终 PatchService/LuaLoader/PatchProgressView 相对 ReviewNotes 缺口。
2. 修 CancelDownload、Prepare 门闸、包所有权、候选快照；Lua 状态机/DisposeError/异步推进/预加载；进度用户文案与 EventSystem 重试。
3. 针对性 PlayMode + Review IL2CPP Player Native + 真实 HTTP 失败可见 UI 与重试成功截图。
4. 追加本日志；不伪改 StageB 初轮正文。

### 实施（关键路径）

| 模块 | 路径 | 用途 |
|---|---|---|
| 包所有权 | `Assets/Framework/Resource/YooPackageOwnership.cs` | PatchService/ResMgr 共用 Mode/URL/cacheRoot 冲突拒绝 |
| 下载事务 | `Assets/Framework/Boot/PatchService.cs` | CancelDownload；Prepare 门闸；Manifest 快照；全字段 Commit 比对 |
| Lua 服务 | `Assets/Framework/Boot/LuaLoader.cs` | HostState 状态机；异步 Pump；DisposeError 可观察 |
| 预加载 | `Assets/Framework/Boot/LuaModulePreloader.cs` | Yoo TextAsset / StreamingAssets 字节拷贝+SHA |
| 宿主 | `Assets/Framework/Boot/LuaHostBehaviour.cs` | Player CLI `--m1-lua-host` 正式链路 |
| 进度 UI | `Assets/Framework/Boot/PatchProgressView.cs` | 阶段映射；密钥真删除；EventSystem 点击 |
| UI 验收 | `Assets/Framework/Boot/PatchProgressReviewRunner.cs` | 真实 HTTP 失败→点击重试→成功（localhost 避代理） |
| 构建 | `Assets/Editor/M1/StageBReviewTools.cs` | 输出 `Review/Builds/Player` |

### 验证表

| 验证对象 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 取消下载 | pending Cancel→CancelDownload→重试 | OCE；门闸释放；重试成功 | 通过 | Review/playmode-results.xml（含 Prepare_PendingCancel…） |
| 候选快照 | Prepare 后改 ConfigVersion 再 Commit | 拒绝 | 通过 | 同上 Commit_RejectsMutated… |
| 包 URL 冲突 | 已 Init 后换 BaseUrl | 明确冲突 | 通过 | Prepare_RejectsConcurrent_AndOwnership… |
| 进度脱敏/映射 | Sanitize/MapStage | 无包名/无密文 | 通过 | PatchProgressViewTests |
| EventSystem 重试 | TryClickRetryViaEventSystem | 单次任务 | 通过 | 同上 + Player patch-ui-review-report |
| Lua 状态机 | 重复 Start；DisposeError 空 | 拒绝；清洁 | 通过 | LuaLoaderTests |
| Native LuaLoader | Review Player `--m1-lua-host` | servicePath 含 LuaLoader；EntryReady；async；dispose 空 | pass=True | Review/player-lua-loader-host.txt；shot-player-lua-loader.png |
| 可见失败 UI | 真实空 CDN HTTP 失败 | 中文失败+条+重试 | 可见 | Review/shot-patch-progress-fail-visible.png |
| 真实重试成功 | EventSystem 点击后 localhost CDN | retryClicks=1；httpLog≥1；成功画面 | pass=True；httpLogCount=10 | Review/patch-ui-review-report.txt；shot-patch-progress-retry-ok.png |

### 命令（摘要）

完整命令见 `Docs/Acceptance/M1/20261020-xLua/StageB/Review/playmode-command.txt`、`player-build-command.txt`、`review-commands.txt`。

### 遇到的问题和解决方案

| 问题场景 | 现象 | 原因 | 修复与复验 |
|---|---|---|---|
| Player 读 Assets/Lua | builtin missing | dataPath 非工程 Assets | StreamingAssets/Resources 回退；Native pass |
| Player `127.0.0.1` HTTP | HttpCode=502；httpLog=0 | 系统代理劫持环回 | BaseUrl 改 `http://localhost`；pass=True httpLog=10 |
| 初轮灰图/旧宿主 | T46/T47 视觉与 Native 不足 | bake Overlay；Hotfix 报告 | Review 可见图 + LuaLoader 报告取代结论 |

### 旧结论处理

- 保留：`StageB/shot-patch-progress-error.png`（灰图）、`player-formal-lua-host.txt`（Hotfix）不删不改。
- 取代：T46 可见性、T47 Native 服务实例结论以 `StageB/Review/` 为准。
- 保留有效：18/18 .NET、原 26/26 PlayMode、7/7 EditMode、100→101 差异计数。

## 2026-10-20 Root-NarrowFix + Native 前置复验（C1 前）

- 来源：`StageB-ReviewNotes` 10–12/14；`StageB/Root-NarrowFix/Plan.md`
- 日期：2026-10-20
- 范围：候选深拷贝、下载 await 取消 CancelDownload、Lua 面板作用域、真实 Candidate 数字；PlayMode 17/17 后 Native Player 复验。不重跑旧迁移/完整 Server 套件。

### 验证（Native）

| 对象 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 构建 | `BatchBuildPlayerToReview` + `--m1-review-build-root=.../Native` | Succeeded；不覆盖 Review/ | Succeeded；0.2.0 | `Root-NarrowFix/Native/player-build-report.txt` |
| LuaLoader | `--m1-lua-host` | pass；Dispose 干净 | pass=True；disposeError 空 | `player-lua-loader-host.txt`；`shot-player-lua-loader.png` |
| PatchUI | `--m1-patch-ui-review --m1-review-root=.../Native` | 真实 bytes/files；GUI 截图落盘 | 9702/9702 · 6/6；pass=True | `patch-ui-review-report.txt`；fail/ok PNG |

命令：`Docs/Acceptance/M1/20261020-xLua/StageB/Root-NarrowFix/Native/Run-NativeReview.ps1`。

