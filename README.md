# Farming / 云屿小筑

基于 Unity、C# / xLua 和 .NET 8 服务端的家园经营游戏工程。当前为 Windows 灰盒功能版本，M2 已验收 **T001～T042（42/80）**；设施建造 T043 正在审查，尚未合入。本仓库发布客户端/服务端源码、资源、协议、配置源表与构建工具。

## 试玩版下载、安装与复现

当前可玩包是 **T042 r5**（Windows x64，资源/配置版本 200，桥契约 `m2-bridge-0.4`）。客户端只连本机，需要在同一台电脑上先启动服务端，再打开游戏。

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 克隆仓库：`git clone https://github.com/liuyuxin-ishere/BeAFarmer.git`，之后的命令都在仓库根目录执行。服务端脚本会编译 `Server/CloudIsle.Server`，并读取仓库里的 `Server/ConfigLuban`。
3. 从 [T042 r5 Release](https://github.com/liuyuxin-ishere/BeAFarmer/releases/tag/t042-r5) 下载 `T042-r5-Player.zip` 和 `T042-r5-Publish.zip`，解压到两个目录。`Player` 里要能看到 `Farming.exe` 和 `Farming_Data`。`Publish` 里要有 `version.json`。
4. 启动服务端。把 `-ContentRoot` 换成你解压出来的 `Publish` 目录：

```powershell
& './Tools/run_server.ps1' `
  -ContentRoot 'D:/CloudIslePlay/Publish' `
  -DatabasePath 'Server/Data/Playtest/playtest.db' `
  -LogDirectory 'Server/Logs/Playtest' `
  -BuildArtifactsDirectory 'Temp/PlaytestBuild'
```

5. 终端出现 `server started` 后，用浏览器打开 `http://localhost:18080/version.json`，确认资源和配置版本是 200。再运行解压目录中的 `Player/Farming.exe`。
6. 在登录页填写自己的用户名、密码和昵称，先注册，再登录。没有公共账号。建议体验顺序是：商店买种、播种并浇水、成熟后收获、背包出售、升级、采集、升级工具。新号没有额外的演示金币。

服务端用 Ctrl+C 停止。下次用同一个 `playtest.db` 启动，进度还在。如果只看到空场景，先确认 18080 上的 `version.json` 能打开，再重新启动客户端。日志一般在 `%USERPROFILE%/AppData/LocalLow/DefaultCompany/Farming/Player.log`。

游戏 TCP 是 `127.0.0.1:17780`，资源 HTTP 是 `http://localhost:18080/`。这套包不支持朋友连到你的电脑上玩；对方需要按同样步骤在自己的电脑上运行服务端和客户端。

## 已实现功能

| 模块 | 已实现内容 |
|---|---|
| 启动与热更新 | 版本清单、应用/资源/配置/协议/桥契约校验，Lua catalog与哈希验证，资源下载与启动激活；已验证同一基包重启加载200/201资源配置。C#、桥契约或协议变化仍需新基包。 |
| 账号与存档 | 注册、登录、登出、会话与重连、SQLite持久化、完整模型同步；命令回执和原请求重放避免重复扣费或发奖。 |
| 种植 | 24块固定地图土地，初始解锁6块；选种、单块/拖拽批量播种、浇水、施肥、自然成熟、手动收获和离线恢复。 |
| 成长 | 服务端经验、升级、等级解锁、按配置一次性工具赠品；背包、商店及采集资格随模型更新。 |
| 背包 | 物品堆叠、容量检查、产物出售、金币扩容；不足与满包拒绝时保持资产一致。 |
| 商店 | 商品列表、购买数量、金币支付、等级限制及失败反馈；服务端具有限购/日切规则，当前200配置商品不限购。 |
| 采集 | 13个稳定采集点，按等级、工具及冷却判断资格；不同点独立计时，冷却归零后手动采集，奖励由服务端提交。 |
| 工具 | 等级及下一档条件展示，确认升级后原子扣金币/材料；支持取消与失败反馈。 |

已做真实 Windows IL2CPP 客户端验证，覆盖种植、采集、经济操作的断线、进程异常退出和恢复。项目仍为灰盒，美术、文案与最终适配尚未完成；设施建造/升级、加工、订单及后续M2内容不属于当前完成范围。热更新200→201演示使用隔离配置，不会在普通200服务中自动出现。

## 工程结构

| 路径 | 内容 |
|---|---|
| `Assets/` | 客户端框架、Lua业务、场景、UI、游戏资源、生成协议/配置与设计文档 |
| `Packages/`、`ProjectSettings/` | Unity依赖与项目设置 |
| `Server/CloudIsle.Server/` | 正式TCP/HTTP服务端、账号、存档和玩法规则 |
| `Server/CloudIsle.Shared/`、`Server/CloudIsle.Config/` | 服务端同源编译共享C#与生成配置 |
| `Shared/` | protobuf协议与Luban配置源表/定义 |
| `Tools/` | 协议/配置生成、客户端/补丁构建、服务器启动 |
| `BuildProfiles/M2/base-200.json` | M2基包版本及内容策略 |

## 环境与构建

已验证环境：Windows x64、Unity **6000.3.22f1**（Windows IL2CPP模块及对应MSVC工具链）、.NET **8**、PowerShell、Python 3 / openpyxl、protoc。Unity包版本见 `Packages/manifest.json` 和 `packages-lock.json`。克隆后用Unity Hub打开仓库根目录，等待资源导入；不要修改 `Library/PackageCache`。

在仓库根目录执行，Unity路径替换为本机位置：

```powershell
$projectRoot = (Get-Location).Path
$protoc = (Get-Command protoc -ErrorAction Stop).Source
& './Tools/build_client_win.ps1' -RepoRoot $projectRoot `
  -UnityEditorPath 'C:/YourUnityEditor/Unity.exe' `
  -ProfilePath 'BuildProfiles/M2/base-200.json' `
  -OutputDir 'Builds/M2/Local/Base 200' -ProtocPath $protoc
```

构建脚本执行协议/配置生成、xLua代码生成、资源处理与Windows IL2CPP构建。成功后保留完整 `Player/` 和 `Publish/` 目录；单独复制exe无法运行。仓库不附带Unity、系统工具链或已构建安装包。

## 启动服务器与试玩

先启动服务端，再打开客户端。以下示例使用上一步生成的发布目录与独立本地数据库：

```powershell
& './Tools/run_server.ps1' `
  -ContentRoot 'Builds/M2/Local/Base 200/Publish' `
  -DatabasePath 'Server/Data/Playtest/playtest.db' `
  -LogDirectory 'Server/Logs/Playtest' `
  -BuildArtifactsDirectory 'Temp/PlaytestBuild'
```

终端显示 `server started` 后，游戏TCP为 `127.0.0.1:17780`，资源HTTP为 `http://localhost:18080/`。打开 `http://localhost:18080/version.json` 应返回资源/配置200。然后启动 `Builds/M2/Local/Base 200/Player/Farming.exe`。服务端用Ctrl+C正常停止，同一数据库重启保留进度。

登录页先填写自选用户名、密码和昵称，点击注册，成功后点击登录；没有公开的统一默认账号。建议从“商店买种→种植/浇水→自然成熟后收获→背包出售→升级解锁→采集/工具升级”体验。普通新号没有演示夹具额外准备的5000金币等资源，各功能受实际等级、材料和金币条件限制。

如果只有空场景，先检查资源服务是否已启动：启动时无法连接18080会导致版本清单加载失败，需要待服务就绪后重新打开客户端。客户端日志通常位于 `%USERPROFILE%/AppData/LocalLow/DefaultCompany/Farming/Player.log`。
