# Farming / 云屿小筑

Unity客户端与.NET服务端的完整工程仓库。仓库根已从`Assets`调整到本目录，原Git提交历史保留；旧提交中的路径仍按当时结构查看。

## 当前状态

- M1基础框架已验收：T01–T68、58项验收通过。
- M2仅制定计划，尚未实施；范围为家园经营闭环。
- [M1实现方法与问题复盘](Assets/ReferenceDoc/06_M1_基础框架任务拆解.md)
- [M1可移植验收报告](Docs/Delivery/M1/M1_Acceptance.md)
- [M2具体开发计划](Assets/ReferenceDoc/06_M2_开发计划.md)

## 源码结构

| 目录 | 内容 |
|---|---|
| Assets | 客户端框架、Lua、场景/资源、生成协议与配置、测试、设计文档 |
| Packages / ProjectSettings | Unity依赖锁与工程设置 |
| Server | 正式.NET服务端、共享项目、生成工具与NUnit测试 |
| Shared | proto与Luban配置源xlsx/schema |
| Tools | 协议/配置生成、客户端/补丁构建和服务端运行入口 |
| Docs/Delivery | 可上传的验收摘要、实际测试报告、关键截图与证据清单 |

## 工具要求

已验证环境为Windows x64、Unity **6000.3.22f1**（含Windows IL2CPP模块及其要求的MSVC工具链）、.NET **8**目标框架、PowerShell 7与Windows PowerShell 5.1、Python 3及openpyxl、protoc。Unity包来源与锁定版本见`Packages/manifest.json`和`packages-lock.json`；xLua固定来源见迁移文档。不要手改Library/PackageCache。

克隆后在Unity Hub打开本目录，等待导入。生成工具缺失时明确安装/指定路径，脚本非0退出时先修当前错误，不跳过。

## 基本命令

从仓库根运行，显式指定当前机器路径；不要照抄历史日志中的盘符。

```powershell
$projectRoot = (Get-Location).Path
$protoc = (Get-Command protoc -ErrorAction Stop).Source
pwsh -NoProfile -File Tools/gen_proto.ps1 -RepoRoot $projectRoot -ProtocPath $protoc
powershell.exe -NoProfile -File Tools/gen_config.ps1 -RepoRoot $projectRoot `
  -SourceDir (Join-Path $projectRoot 'Shared/config') `
  -ExporterPath (Join-Path $projectRoot 'Tools/Luban/Luban/Luban.dll') -ConfigVersion 100
dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj -c Release
pwsh -NoProfile -File Tools/build_client_win.ps1 -RepoRoot $projectRoot `
  -UnityEditorPath 'C:/YourUnityEditor/Unity.exe' -OutputDir 'Builds/M1/Base 100' `
  -AppVersion 0.3.0 -ResVersion 100 -ProtocPath $protoc
```

服务端需要先生成配置和有效HTTP发布目录，再显式传入`DatabasePath/ContentRoot/ConfigDirectory`；补丁需要同一基包的`BaseBuildManifest`及对应版本配置。详细参数见各脚本和M1日志。M2的0.4/200示例需先完成M2计划中的构建改造，不能视为现在已经可用。

## 上传与证据边界

`.gitignore`排除Library/Temp、安装包、CDN副本、数据库、Token、私有凭证、IDE/代理本地配置和原始运行缓存。Unity`.meta`、所需插件/导表DLL、Server项目文件和配置源表保留。

本机`Docs/Acceptance`保留原始验收工作目录，不上传；`Tools/prepare_m1_delivery.py`按58项矩阵明确引用的文件复制到`Docs/Delivery/M1`，记录原始/导出SHA。可移植报告链接使用相对路径，安装包仅记录三SHA；源码上传不包含Unity或操作系统工具链。

历史HybridCLR日志/PoC保留当时原文和迁移说明，当前运行栈为xLua。不要把旧技术的通过结论替换成当前栈已通过。
