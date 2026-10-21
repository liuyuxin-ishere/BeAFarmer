<!-- XLUA-MIGRATION:BEGIN -->
> **迁移说明（2026-10-19）：本文件保留历史原文。**
> 当前热更架构调整为 xLua；旧记录中的技术名称和“已通过”仅描述当时环境，不能解释为 xLua 已验证。
> 本文是迁移前的工程快照；原包版本、路径、GUID、哈希及 HybridCLR 安装状态保留为当时事实。主工程是否已完成 xLua 迁移，须以新方案中的实际验收与清理记录为准。
> 当前设计、验证范围和证据见 [xLua 迁移方案与 PoC](07_xLua迁移方案与PoC.md)。
<!-- XLUA-MIGRATION:END -->

# M1 开发基线（T01）

记录日期：2026-10-18（Asia/Shanghai）。范围：工程定位、依赖版本、现有工具入口、PoC 结论和后续任务衔接。本文记录本机现状，不表示 M1 框架已实现。

## 1. 工程定位

完整 Unity 工程已定位到 `E:\Workbench\UnityWorkBench\Farming`，通过 `ProjectSettings/ProjectVersion.txt`、`Packages/manifest.json` 和 Assets Git 工作树元数据交叉确认。

当前 Codex 工作区为 `C:\Users\Lenovo\.codex\worktrees\f95f\Assets`，是 Assets 仓库的分离工作树；其上级不是完整 Unity 工程。原工程根目录、Git 仓库根目录和当前工作树是三个不同位置。

### 1.1 已确认路径

| 用途 | 完整路径 | 核对依据 |
|---|---|---|
| Unity 工程根目录 | `E:\Workbench\UnityWorkBench\Farming` | 含 Assets、Packages、ProjectSettings、Server、Shared、Tools |
| 原工程 Assets / Git 主工作树 | `E:\Workbench\UnityWorkBench\Farming\Assets` | 当前工作树的 Git common-dir 指向此处的 `.git` |
| 本次文档交付工作树 | `C:\Users\Lenovo\.codex\worktrees\f95f\Assets` | 分离 HEAD 的 Assets 工作树 |
| Unity 编辑器 | `D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe` | 文件存在；Unity Hub 编辑器记录相符 |
| 服务端解决方案 | `E:\Workbench\UnityWorkBench\Farming\Server\CloudIsle.sln` | 已读取解决方案和项目文件 |
| 服务端入口 | `E:\Workbench\UnityWorkBench\Farming\Server\CloudIsle.Server\Program.cs` | TCP、数据库、配置读取和冒烟入口 |
| 工具目录 | `E:\Workbench\UnityWorkBench\Farming\Tools` | 已读取生成与验证脚本 |
| 协议源目录 | `E:\Workbench\UnityWorkBench\Farming\Shared\proto` | 已有 `poc.proto`、`plaza.proto` |
| Luban 配置源目录 | `E:\Workbench\UnityWorkBench\Farming\Shared\config` | `luban.conf`、Datas、Defines |
| 原工程参考文档 | `E:\Workbench\UnityWorkBench\Farming\Assets\ReferenceDoc` | 含较新的架构、协议、Luban 与灰盒 PoC 资料 |
| 历史性能证据 | `E:\Workbench\UnityWorkBench\Farming\Docs\Perf` | 含机器人验证报告 |
| 已有 Windows 构建 | `E:\Workbench\UnityWorkBench\Farming\Builds\HybridCLR_PoC_Win64` | 存在 Farming.exe、GameAssembly.dll、UnityPlayer.dll |

下文未另行说明的相对路径均相对上述完整 Unity 工程根目录。M1 拆解文档脚本参数中的 `RepoRoot` 应传此工程根目录，而不是 Assets Git 仓库根目录。

### 1.2 版本控制与工作树边界

- 当前 HEAD：`f37bd5083b88ad5087356b95b78db570667cdd25`。主工作树登记的 HEAD 相同，但实际磁盘内容有差异，不能据此认定文件完全一致。
- Git common-dir：`E:/Workbench/UnityWorkBench/Farming/Assets/.git`。完整工程根目录本身没有 `.git`；其同级 Server、Shared、Tools、Packages、ProjectSettings 不在这个 Assets 仓库的版本控制范围内。
- 原工程已有 `Assets/Config`、Luban 编辑器工具和更新后的共享程序集设置；当前工作树没有完整同步这些内容。原工程中的 `Assets/HotUpdate/Entry.cs` 已不存在，当前工作树仍保留。
- 本次未取得原工程完整 `git status` 结果，不声明主工作树干净，也不把上述提交号视为 Server/Tools/配置源的版本号。第 7 节保留关键文件哈希作为本次磁盘快照证据。
- 后续 T02 应先对照原工程已有实现整理迁移清单，再修改当前工作树，避免把已完成的 PoC 调整重复实现或覆盖回旧版。本次只在当前工作树交付文档。

## 2. 依赖与环境

### 2.1 实际版本

| 组件 | 本机版本 / 状态 | 证据 |
|---|---|---|
| Unity | `6000.3.22f1`，revision `1c726e1fb402` | `ProjectSettings/ProjectVersion.txt` |
| Windows IL2CPP | 已安装、已验证，作为 M0 已满足前提 | 用户确认；编辑器 `PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_il2cpp` 存在 |
| HybridCLR | `9.0.2`，本地 embedded 包 | `Packages/com.code-philosophy.hybridclr/package.json`；manifest 为 `file:com.code-philosophy.hybridclr` |
| YooAsset | `3.0.6`，OpenUPM 来源 | `Packages/manifest.json`、`packages-lock.json` |
| Universal RP | `17.3.0` | 同上 |
| Input System | `1.20.0` | 同上 |
| Unity Test Framework | `1.6.0` | 同上 |
| UGUI | `2.0.0` | 同上 |
| Scriptable Build Pipeline | 实际锁定 `2.6.1` | `packages-lock.json`；区别于依赖声明请求的版本 |
| Google.Protobuf | 客户端 DLL / 服务端包均为 `3.21.12` | `Assets/Plugins/Google.Protobuf/Google.Protobuf.dll` 与 `CloudIsle.Shared.csproj` |
| protoc | `libprotoc 3.20.3` | `D:\Users\Lenovo\anaconda3\Library\bin\protoc.exe --version` |
| .NET SDK | 已安装 `8.0.404`、`10.0.301`；当前默认 `10.0.301` | `dotnet --list-sdks`、`dotnet --version`；未发现工程根或 Server 下的 global.json |
| .NET 运行时 | 已有 `Microsoft.NETCore.App 8.0.11` | `dotnet --list-runtimes` |
| Microsoft.Data.Sqlite | `8.0.11` | `Server/CloudIsle.Server/CloudIsle.Server.csproj` |
| Luban | `4.8.0` | `Tools/Luban/VERSION`；程序为 `Tools/Luban/Luban/Luban.dll` |
| Python / openpyxl | `3.11.7` / `3.0.10` | `D:\Users\Lenovo\anaconda3\python.exe` 版本与包元数据 |

`dotnet` 实际路径为 `C:\Program Files\dotnet\dotnet.exe`。现有 PowerShell 脚本通过 PATH 调用 dotnet/python；记录的默认 SDK 不等于服务端目标框架。服务端目标仍为 `net8.0`，后续构建应明确 SDK 选择并固化。

在已核对的 manifest、Framework/Hotfix 源码和服务端 csproj/源码中，未定位到 UniTask、Dapper、Serilog、Pipelines 的实际接入证据。它们仍属于 M1 设计要求；这里不据此推翻用户确认的选型验证，也不登记为已落地依赖。

### 2.2 程序集与构建设置

| 项目 / 设置 | 原工程实际状态 | 后续衔接 |
|---|---|---|
| `CloudIsle.Server` | `net8.0`，引用 Shared、Config 项目和 Microsoft.Data.Sqlite | 保留现有工程，T05 补齐正式服务端和测试结构 |
| `CloudIsle.Shared` | `netstandard2.1`；输出 `Game.Shared`；同源编译 `Assets/Shared/**/*.cs`；`LangVersion=latest` | T02/T05 约束共享代码使用 Unity 支持的语言和 API 范围 |
| `CloudIsle.Config` | `netstandard2.1`；输出 `Game.Config`；同源编译 `Assets/Config/**/*.cs`；允许 unsafe | T41/T42 沿用已验证配置读表代码 |
| 其他已有项目 | `CloudIsle.PocClient`、`CloudIsle.Bot`、`CloudIsle.GreyboxSmoke` | 作为已有 PoC 工具，不等同于 M1 NUnit 测试工程 |
| `Game.Shared.asmdef` | 原工程 `noEngineReferences=true`；当前工作树仍为旧状态 | T02 对照后迁移 |
| `Game.Hotfix.asmdef` | 原工程引用 `Game.Framework`、`Game.Shared`、`UnityEngine.UI` | 现有热更入口为 `Game.Hotfix.Entry.Start(Text)` |
| `Game.Config.asmdef` | `noEngineReferences=true`，`allowUnsafeCode=true` | 当前不在 HybridCLR 热更程序集名单 |
| HybridCLR 设置 | `enable=1`，`useGlobalIl2cpp=0`；热更程序集列表为 `Game.Hotfix` | M1 需明确配置类型的 AOT 边界 |
| Player 设置 | `productName=Farming`；`bundleVersion=0.1.0`；Standalone `scriptingBackend=1`；`apiCompatibilityLevel=6`；`activeInputHandler=2` | 后三项记录序列化原值，未在本次修改 |
| YooAsset 内置包 | `DefaultPackage`，当前版本文件内容为 `2` | M1 验收的 100/101 是独立约定，不是当前包版本 |

HybridCLR 设置证据为 `ProjectSettings/HybridCLRSettings.asset`，Player 设置证据为 `ProjectSettings/ProjectSettings.asset`。本次只读文件，未启动 Unity 重新导入、构建或改设置。

## 3. 现有配置导出管线

### 3.1 主流程：Luban

依据原工程 `04_技术方案与架构.md` 的 Luban 补充记录、`PoC_Luban_操作说明.md` 和实际脚本，当前主流程为 Luban，旧 Python 导表保留作回退。Luban 的“有条件采用”限制见第 6 节。

| 环节 | 已有路径 / 行为 |
|---|---|
| 数据源 | `Shared/config/Datas/*.xlsx`，当前 17 份数据表 |
| 类型定义 | `Shared/config/Defines/__tables__.xlsx`、`__beans__.xlsx`、`__enums__.xlsx` |
| 导出配置 | `Shared/config/luban.conf`；target=`all`，groups=`c,s`，manager=`Tables`，topModule=`CloudIsle.LubanCfg` |
| 脚本入口 | `Tools/gen_luban.ps1`；无自定义参数，从 `$PSScriptRoot` 推导工程根目录 |
| C# 输出 | `Assets/Config/Generated`；运行时代码在 `Assets/Config/LubanRuntime` |
| JSON 暂存 | `Shared/config/Output/_staging_json` |
| JSON 归档 / 服务端 / 客户端 | `Shared/config/Output/json` / `Server/ConfigLuban` / `Assets/StreamingAssets/ConfigLuban` |
| Unity 菜单入口 | `Farming/Luban PoC/1. Generate (gen_luban.ps1)`；定义于 `Assets/Editor/Luban/LubanPoCTools.cs` |
| 读取入口 | 服务端 `RunLubanSmoke(string? configDir)`；客户端菜单 `Farming/Luban PoC/3. Load StreamingAssets ConfigLuban (Client)` |

已有导表命令如下，供后续任务执行；T01 未运行这些会更新产物的命令：

```powershell
$projectRoot = 'E:\Workbench\UnityWorkBench\Farming'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'Tools/gen_luban.ps1')
```

脚本内部调用的实际参数契约：

```text
dotnet <Luban.dll> --conf <Shared/config/luban.conf>
  -t all -c cs-simple-json -d json
  -x outputCodeDir=<Assets/Config/Generated>
  -x outputDataDir=<Shared/config/Output/_staging_json>
  --validationFailAsError
```

脚本在导表失败时退出，不向三处正式 JSON 目录同步；成功后复制同一批 JSON。C# 直接输出到 Generated，复制步骤也未实现整体原子切换或删除目标目录遗留文件，因此不能把它描述为完整的事务式发布。

本次只读比较发现：三处正式 Luban JSON 目录的文件集合均为 17 个，逐文件 SHA-256 完全一致。这证明当前已导出产物一致，不等同于本次从源表重新生成通过。

`Assets/ReferenceDoc/云屿小筑_配置表与开发进度.xlsx` 是设计工作簿；`云屿小筑_配置表_Luban.xlsx` 是整理副本。它们都不是当前 `luban.conf` 的直接数据输入。`Tools/migrate_xlsx_to_luban.py` 用于初次迁移，不作为日常导表入口。

### 3.2 保留流程：Python 自写导表

| 项目 | 现状 |
|---|---|
| 入口 | `Tools/gen_config.ps1` 转发参数给 `python Tools/gen_config.py` |
| 参数 | `--xlsx`：`pathlib.Path`，默认设计工作簿；`--set-global`：可重复的 `key=value` 字符串覆盖项，用于 PoC |
| 输出代码 | `Assets/Shared/Config/Generated/ConfigTables.g.cs` |
| 输出数据 | `Assets/StreamingAssets/Config` 和 `Server/Config` |
| 清单 | `manifest.json` 记录 source、tables；尚不是带 ConfigVersion 和哈希的 M1 清单 |
| 当前一致性 | 两端各 18 个 JSON（17 表与 manifest），文件集合和逐文件 SHA-256 一致 |

```powershell
$projectRoot = 'E:\Workbench\UnityWorkBench\Farming'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'Tools/gen_config.ps1') --xlsx (Join-Path $projectRoot 'Assets/ReferenceDoc/云屿小筑_配置表与开发进度.xlsx')
```

M1 拆解中 `gen_config.ps1 -RepoRoot -SourceDir -ExporterPath -ConfigVersion` 是 T41 要实现的适配层契约，现有同名脚本尚不支持这些参数。T41 应接入当前 Luban 主流程并保留回退用途的清晰入口，不按文件名误判为已经完成。

## 4. 现有运行入口与参数

### 4.1 服务端入口

入口文件：`Server/CloudIsle.Server/Program.cs`，方法 `Task<int> Main(string[] args)`。

| 参数名 | 解析后的类型 / 默认值 | 行为 |
|---|---|---|
| `--host` | `string`，`127.0.0.1` | TCP 监听地址 |
| `--port` | `int`，`17777` | TCP 监听端口 |
| `--db` | `string`，`AppContext.BaseDirectory/data/poc.db` | SQLite 文件；默认相对应用输出目录，不是工程根目录 |
| `--no-plaza` | `bool` 开关，默认未启用 | 禁用默认启用的广场模块 |
| `--plaza-tick-ms` | `int`，`100` | 广场 Tick 间隔，毫秒 |
| `--plaza-line-cap` | `int`，`100` | 单线容量 |
| `--shared-smoke` | `bool` 开关 | 调用 `int RunSharedSmoke()` 后退出 |
| `--config-smoke`、`--config-dir` | `bool`、`string?` | 调用 `int RunConfigSmoke(string? configDir)` 读取旧配置 |
| `--luban-smoke`、`--luban-dir` | `bool`、`string?` | 调用 `int RunLubanSmoke(string? configDir)` 读取 Luban 配置 |

三个 smoke 开关按 Shared → Config → Luban 顺序判断，应一次只传一个。已有启动方式如下；这是入口登记，本次未启动进程或创建数据库：

```powershell
$projectRoot = 'E:\Workbench\UnityWorkBench\Farming'
& dotnet run --project (Join-Path $projectRoot 'Server/CloudIsle.Server/CloudIsle.Server.csproj') -- --host 127.0.0.1 --port 17777 --db (Join-Path $projectRoot 'Server/data/poc-manual.db') --no-plaza
```

### 4.2 已有验证工具

| 脚本 | 参数 / 作用 | 既有产物或运行特征 |
|---|---|---|
| `Tools/verify_shared.ps1` | 无自定义参数；构建并执行 `--shared-smoke` | 同源协议与规则验证 |
| `Tools/verify_tcp.ps1` | 无自定义参数；构建、启动服务端、运行 PoC 客户端 | 端口 17777；会清理 `%TEMP%/cloudisle_poc6.db`；日志位于临时目录 |
| `Tools/verify_config.ps1` | 旧导表与服务端读表验证 | 会重新导出旧配置，不属于只读检查 |
| `Tools/verify_luban.ps1` | Luban 两端读取和错误引用验证 | 会修改源表验证 60 → 61 → 60 与无效引用，并恢复；本次未执行 |
| `Tools/verify_plaza_bots.ps1` | `[int]$Count=100`、`$Duration=120`（秒）、`$Port=17778`、`$ConnectRate=20` | 临时数据库 `cloudisle_poc10.db`；报告写入 `Docs/Perf/plaza_bots_<时间戳>.txt` |
| `Tools/verify_greybox.ps1` | `[int]$Port=17779` | 使用 `--no-plaza` 和临时数据库 `cloudisle_greybox.db` |

现有 Windows PoC 构建目录及 Unity 菜单可复用为后续构建依据，尚不能替代 T58/T59 的正式构建脚本。`Tools/gen_proto.ps1`、`build_client_win.ps1`、`build_patch.ps1`、`run_server.ps1` 和 `Server/CloudIsle.Tests` 当前尚未建立。

### 4.3 M1 后续验收固定约定

下表是本次为后续验收登记的路径和端口约定，不是已经启动的服务或已经生成的产物。基于完整工程根目录解析；本次未创建目录、占用端口或改动 PoC 配置。T60 启动时仍需检查端口占用。

| 用途 | M1 约定 |
|---|---|
| TCP | `127.0.0.1:17780`，与既有 17777–17779 PoC 入口区分 |
| 补丁 HTTP | `http://127.0.0.1:18080/` |
| 独立验收数据库 | `Server/data/m1-test.db` |
| HTTP ContentRoot | `Server/PatchContent/M1`；版本入口为其下 `version.json` |
| 100 基包输出 | `Builds/M1/Win64/Base100` |
| 101 补丁输出 | `Bundles/M1/101`；发布时复制到 ContentRoot 下相应版本目录 |
| 验收证据目录 | `Docs/Acceptance/M1/<run-id>` |
| 基包版本 / 补丁版本 | AppVersion=`0.1.0`、ProtoVersion=`1`；基包 ResVersion/ConfigVersion=`100`，补丁为 `101` |

版本 100/101 仅用于 M1 隔离验收。现有 PoC 资源包版本为 2；尚未定位到正式 M1 版本服务和 ConfigVersion 清单，其实现归 T41/T43/T58/T59。

## 5. PoC 结论与证据

证据分为三类：**用户确认**（IL2CPP 和模块有效性已验证）、**历史记录**（原工程说明与报告）、**本次只读核对**（路径、版本、源码和哈希）。本次没有重新执行 PoC，以下“通过”不表示本次新增运行结果。

| 项目 | 采用的结论 | 可追溯证据 |
|---|---|---|
| IL2CPP | 已安装且有效，T01 无安装阻塞 | 用户最新确认、实际安装目录、已有 Windows IL2CPP 构建文件 |
| URP | 已验证 | 原工程 `Assets/ReferenceDoc/04_技术方案与架构.md` 第 17 节汇总 |
| HybridCLR | 已验证热更加载链 | 同上及 `PoC_HybridCLR_操作说明.md`；现有 Hotfix DLL 与 AOT 元数据文件 |
| YooAsset | 已验证资源包加载 / 替换 | `PoC_YooAsset_操作说明.md`；DefaultPackage 版本 2 的现有产物 |
| Protobuf | 已验证 IL2CPP 与热更使用 | `PoC_Protobuf_操作说明.md`；`StreamingAssets/AOT/Google.Protobuf.dll.bytes` |
| Game.Shared | 已验证 Unity/.NET 同源编译 | `PoC_GameShared_操作说明.md`；Shared 项目源码引用 |
| TCP + SQLite | 已验证 PoC 登录 / 数据持久化 | `PoC_ServerTcp_操作说明.md`、verify_tcp 与现有服务端源码 |
| 自写导表 | 已验证，保留回退 | `PoC_ConfigExport_操作说明.md`；两端现有 JSON 哈希一致 |
| Luban | 已验证基础管线，历史 ADR 为有条件采用 | `PoC_Luban_操作说明.md`；04 的 Luban 补充记录；三处 JSON 哈希一致 |
| 换装合并 | 历史记录通过 | `PoC_换装合并Demo_操作说明.md` 与 04 第 17 节 |
| 服务端机器人 | 历史记录通过；保留实际报告数值 | `PoC_服务端稳定性测试_操作说明.md` 和下述报告 |
| 灰盒玩法 | 历史记录通过 | `灰盒玩法测试_操作说明.md`、现有 GreyboxSmoke 项目 |
| Android | 历史记录曾延期；不是本次 Windows M1 前提 | 不用旧“延期”推翻用户的模块确认，也不据此虚构新增 Android 实测记录 |

上述简写 PoC 文档均位于原工程 `Assets/ReferenceDoc`。机器人原始证据为 `Docs/Perf/plaza_bots_20261013_155039.txt`：100 个请求连接、100 个连接成功、100 个进入、0 掉线，持续 604.7 秒；平均 RTT 4.0 ms、P99 RTT 18.7 ms，`pass=True`。这里是约十分钟的 RTT 结果，不作为三十分钟测试或服务端 Tick P99 的证明。

历史有效性验证满足 T01 的前提登记要求；正式 M1 的账号协议、自动重连、HTTP 补丁、版本兼容、资源生命周期等仍按各自任务和验收步骤独立验证。

## 6. 现状与 M1 设计的差异

| 差异 | 对后续工作的具体影响 | 对应任务 |
|---|---|---|
| 当前工作树与原工程 Assets 不一致 | 先核对已完成的 Shared、Hotfix、Config 设置；迁移时保留原工程较新逻辑，不直接用当前旧文件覆盖 | T02 |
| Server 已存在，而 NUnit 工程未建立 | 在既有解决方案扩展正式框架 / 测试，不创建第二套互不关联的服务端 | T05、T08、T61 |
| 默认 dotnet 是 SDK 10，目标框架为 .NET 8 | 明确并锁定 SDK；Shared 的 `LangVersion=latest` 不作为 Unity 可用语法的保证 | T02、T05、T58–T60 |
| protoc 3.20.3 与 Protobuf 运行库 3.21.12 不同 | 记录并固化生成器 / 运行库组合，生成后验证两端；本次不因版本不同直接判定失效 | T04 |
| 已有 `Game.Config` 未列入热更程序集 | 必须登记配置类型所属程序集及其 AOT 兼容约束；不能假设重新生成 C# 就能仅发补丁 | T02、T41、T42、T59 |
| Luban Global 是 `mode=one` 的强类型字段 | 当前并非任意字符串键值表。新增 `m1_patch_message` 需要先设计不改变 AOT 类型的键值扩展结构，并纳入 100 基包；101 才能只增数据与热更 UI | T41、T42、T58、T63 |
| Luban 的 ItemCount / 枚举仍有字符串表示，Tool 复合键保留列表式方案 | 沿用已验证边界，后续类型化改造需单独验证；不宣称所有目标表结构已完整实现 | T41、T42 |
| 当前 gen_config.ps1 是旧 Python 包装器 | 改造为稳定入口并接入 Luban；补 ConfigVersion、文件哈希、输出完整性和两端一致性验证 | T41 |
| Luban 失败保护只覆盖 JSON 同步前的校验 | M1 需补正式发布暂存、清理遗留产物和完整版本切换，不能把当前复制逻辑当作回滚机制 | T41、T45、T59 |
| 热更入口仍为 `Start(Text)`；缺 AOT 元数据有警告后继续的 PoC 路径 | 保留 PoC 适配入口；正式启动接入 BootContext / CancellationToken，并实现失败停止 | T47、T56 |
| 已有服务端是 PoC 账号 / player 表与 Token 逻辑 | 正式注册、账号表、密码 / Token 存储、顶号及跨重启恢复仍需实现和验收 | T28、T33–T38 |
| 当前资源是 PoC 包，正式 HTTP / 构建脚本未建立 | 复用加载调用，实现完整远端更新、版本服务和 100/101 独立构建链 | T13、T43–T47、T58–T60 |
| 部分设计依赖尚未定位到接入证据 | 后续任务核对安装来源与 API；不把 M0 的选型通过写成 M1 已集成 | T02、T23、T28、T40 |
| Assets 仓库未覆盖完整工程 | 在后续涉及 Server/Tools/Packages 的改动前确定完整工程的版本管理和交付方式；本次未迁移仓库 | 后续实施准备 |

以上均已形成后续输入，不阻塞“登记现状”的 T01；也不代表对应实现任务可以提前勾选。

## 7. 本次核对记录与完成判定

### 7.1 单步核对结果

| 步骤 | 已执行的只读动作 | 通过证据 |
|---|---|---|
| T01-01 | 读取当前工作树与 Git 元数据，定位原工程 | 三个根目录及 common-dir 已区分，见 1.1/1.2 |
| T01-02 | 读取 ProjectVersion、manifest、packages-lock、HybridCLR 包信息和设置 | Unity、包版本及热更程序集记录完整，见第 2 节 |
| T01-03 | 检查编辑器 / IL2CPP 目录，接受已验证前提 | 不再安排安装或重跑选型 PoC |
| T01-04 | 读取 Server 解决方案、项目和 Program 入口 | 目标框架、包引用、启动参数与数据库定位已记录 |
| T01-05 | 读取导表脚本、Luban 配置与数据目录 | 源表 → 生成器 → 代码 / JSON → 两端输出可逐项定位 |
| T01-06 | 比较现有配置输出的文件集合及 SHA-256 | Luban 三处各 17 个、旧配置两处各 18 个 JSON 全部一致 |
| T01-07 | 阅读原工程 PoC 说明、架构记录和性能报告 | 用户确认、历史验证、本次核对三类证据已区分 |
| T01-08 | 对照当前工作树与原工程关键文件 | Shared/Hotfix/Config 等差异已交给 T02 及相关任务 |
| T01-09 | 输出基线并登记后续验收路径和端口 | 本文第 4.3 节约定齐全；本地任务清单同步 T01 完成 |

本次核对的 29 个关键文件路径全部存在。导出一致性采用已有产物哈希比较；未运行导表、修改 Excel、启动服务端、创建数据库、重新构建客户端或执行 M1 运行验收。

### 7.2 关键文件 SHA-256 快照

此快照补充 Git 无法覆盖的工程文件，便于后续判断基线是否发生变化。路径相对完整工程根目录，记录时间为 2026-10-18。

| 文件 | SHA-256 |
|---|---|
| `ProjectSettings/ProjectVersion.txt` | `4c5c83b324e4beafbe03132a68b0e9d87e93d6a11e687f6cb5c0fc514219e154` |
| `Packages/manifest.json` | `cd98da1601be2f1e0b933e6f4d12cb4ce9f140224c667edd5cca42d6f70b6d6d` |
| `Packages/packages-lock.json` | `04fcca7bb355fcf4161f785f4218af8337cc27d656a4064712b68f7a0c710e22` |
| `Server/CloudIsle.Server/CloudIsle.Server.csproj` | `f413b2fb9a60236b043784d2cb5c224be086e5be880609efb96fe20f7af8915d` |
| `Shared/config/luban.conf` | `bed77b38e0b2369ca15eecd6ec1c7e833dd0c979f021c4c9ef2b226235b85a52` |
| `Tools/gen_luban.ps1` | `5559c6aecd1880f842625968f00a984be63f92ae329888cedc1449e1910270de` |
| `Tools/gen_config.ps1` | `31347db29b0c16a96ce8b5ac13cdeff6c05898d8a35fca8ebdf35bc786ae2a2d` |
| `Tools/Luban/Luban/Luban.dll` | `1547bd69fabc6655c8f6a8d0f6e001c451bcc8131e082b96b1229dd6410bbe89` |
| `Docs/Perf/plaza_bots_20261013_155039.txt` | `4aa6d5db8a2f4ccb8127f84f1b7f465c2ec0f2fc014f6d67081fc86958f79db1` |

### 7.3 T01 完成判定

- [x] 完整 Unity / Server / Tools / 配置源路径明确。
- [x] 实际 Unity、包、导表工具和 .NET 版本可追溯。
- [x] 已有导表命令、参数、输入输出和客户端 / 服务端读取入口明确。
- [x] IL2CPP 与模块有效性按已验证前提登记，没有重跑安装。
- [x] PoC 结论有出处，并与未实现的 M1 正式功能区分。
- [x] 工作树差异、配置热更约束和后续任务映射已记录。

**T01：完成。** 完成范围为开发基线登记；T02–T68 与 M1 端到端验收保持待办。
