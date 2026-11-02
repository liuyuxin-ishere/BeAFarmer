# Farming / 云屿小筑

基于 Unity、C# / xLua 和 .NET 8 服务端的家园经营游戏工程。当前为 Windows 灰盒功能版本，M2 已验收 **T001～T042（42/80）**；设施建造 T043 正在审查，尚未合入。本仓库发布客户端/服务端源码、资源、协议、配置源表与构建工具。

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

开发范围见 [M2开发计划](Assets/ReferenceDoc/06_M2_开发计划.md)。测试代码、原始验收材料、各类Log文档、构建包、演示录像及本地存档不随源码仓库发布；本地保留这些材料。

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

## 仓库内容边界

`.gitignore` 排除Unity/IDE缓存、测试及专用测试宿主、Cursor工作副本、原始验收材料、Log文档、视频/构建产物、数据库、会话Token、私有配置及签名材料。Unity资源与必要 `.meta`、生产项目文件、所需插件/生成工具和配置源表保留。密码、密钥和账号数据只保存在本地，不应提交。
