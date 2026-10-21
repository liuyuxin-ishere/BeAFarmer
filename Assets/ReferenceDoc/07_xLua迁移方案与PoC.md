# xLua 架构迁移方案与 PoC

日期：2026-10-19 起，2026-10-20 继续。状态：第4节与第7.4节的“通过”只代表初轮范围。本轮回归已回填到第7.7节。主工程修复复核、清理、FinalPolish、配置导出同步与 ReferenceDoc 现行对齐见第7.8–7.11节与 `Docs/Acceptance/xLuaMigration/20261020/MainProject/`（含 `Cleanup/`、`Cleanup/Final/`、`ConfigExport/`）。现行 01/04/05/06/06M1 已按实际源码与证据应用（草稿非验收通过）。旧 T42 仍未完成。旧 HybridCLR 包/HybridCLRData/StreamingAssets 旧 DLL 已删。

## 1. 目标与执行顺序

按用户指令盘点Assets/ReferenceDoc全部文件，验证xLua技术栈，再以真实结果修改现行文档。原M1自动推进已停止，T42有未验收部分代码；不会继续按旧热更设计实施。电量检查保持暂停。

采用经隔离 PoC 验证的边界：C#基础框架、共享协议/配置和服务端保留；客户端热更业务由Lua模块承载，xLua负责互调。构建前生成必要桥接代码并进行IL2CPP裁剪保护。用户已确认本次包括主工程：先迁移并验证当前实现，再删除旧依赖与配置并复验。PoC使用独立工程，不能代替主工程验收；主工程实施记录见验收目录 MainProjectMigration.md。

## 2. 全目录修改与保留清单

原始目录58个文件：26 Markdown、2工作簿、30Unity meta。完整路径、修改前SHA256与命中行见验收目录ReferenceDoc-inventory-before.json。工作簿XML检查未发现HybridCLR等旧栈内容。用户已确认历史原文保留并加迁移说明；19份历史文档已完成通知，原正文核验不变。

| 文件 | 处理 | 修改/保留部分及理由 |
|---|---|---|
| `01_项目说明与范围.md` | 改写现行设计 | 热更边界、Lua入口/生命周期、C#桥接、构建/补丁与验收；保留玩法、协议、服务端与性能目标 |
| `01_项目说明与范围.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `02_玩法设计.md` | 保留 | 玩法与数值规则不由热更引擎决定；无需术语替换 |
| `02_玩法设计.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `03_数值与配置表.md` | 保留 | 玩法与数值规则不由热更引擎决定；无需术语替换 |
| `03_数值与配置表.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `04_技术方案与架构.md` | 改写现行设计 | 热更边界、Lua入口/生命周期、C#桥接、构建/补丁与验收；保留玩法、协议、服务端与性能目标 |
| `04_技术方案与架构.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `05_协议与接口.md` | 补充边界 | 保留消息ID/字段/错误码；补充C#协议与Lua之间的bytes、int64、回调和取消契约 |
| `05_协议与接口.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `06_M1_基础框架任务拆解.md` | 改写现行设计 | 热更边界、Lua入口/生命周期、C#桥接、构建/补丁与验收；保留玩法、协议、服务端与性能目标 |
| `06_M1_基础框架任务拆解.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `06_开发计划.md` | 改写现行设计 | 热更边界、Lua入口/生命周期、C#桥接、构建/补丁与验收；保留玩法、协议、服务端与性能目标 |
| `06_开发计划.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs/M1_01_Logs.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Logs/M1_01_Logs.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs/M1_02_Logs.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Logs/M1_02_Logs.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs/M1_03_Logs.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Logs/M1_03_Logs.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs/M1_04_Logs.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Logs/M1_04_Logs.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs/M1_05_Logs.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Logs/M1_05_Logs.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs/M1_06_Logs.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Logs/M1_06_Logs.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs/M1_07_Logs.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Logs/M1_07_Logs.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs/M1_Logs.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Logs/M1_Logs.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Logs.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `M1_Baseline.md` | 保留历史+迁移说明 | 原时间点路径/版本/哈希/实测不改；指向新xLua基线，区分旧 HybridCLR 历史基线与主工程迁移后基线（主工程已迁 xLua，见第7.8–7.11节） |
| `M1_Baseline.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_ConfigExport_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_ConfigExport_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_GameShared_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_GameShared_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_HybridCLR_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_HybridCLR_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_Luban_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_Luban_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_Protobuf_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_Protobuf_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_ServerTcp_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_ServerTcp_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_YooAsset_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_YooAsset_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_换装合并Demo_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_换装合并Demo_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/PoC_服务端稳定性测试_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/PoC_服务端稳定性测试_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/云屿小筑_配置表与开发进度.xlsx` | 保留 | 数值/配置内容、格式和公式；XML全检未发现旧栈术语 |
| `Poc_Archived/云屿小筑_配置表与开发进度.xlsx.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived/灰盒玩法测试_操作说明.md` | 保留记录+迁移说明 | 保留原始脚本、版本、验证结果；标注历史身份与受影响任务，不改成xLua已通过 |
| `Poc_Archived/灰盒玩法测试_操作说明.md.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `Poc_Archived.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |
| `云屿小筑_配置表_Luban.xlsx` | 保留 | 数值/配置内容、格式和公式；XML全检未发现旧栈术语 |
| `云屿小筑_配置表_Luban.xlsx.meta` | 保留 | GUID、导入与目录元数据；对应文档原位编辑，不重建meta |

## 3. 最小执行步骤

- [x] 停止旧T42 Cursor任务，保留部分源码和既有Unity会话。
- [x] 全目录文件及工作簿XML盘点，保存修改前哈希。
- [x] 查阅腾讯xLua官方说明与固定版本发布信息。
- [x] 在独立PoC工程接入固定版本xLua、生成桥接，验证编辑器运行。
- [x] 构建并运行Windows x64 IL2CPP，验证互调/异步回调/协议数据与清理。
- [x] 验证同一安装包Lua100→101资源更新、新Lua模块与新配置；保存安装文件哈希。
- [x] 验证缺脚本/坏脚本/校验失败不激活，旧可用缓存保留。
- [x] 根据结果重写现行架构/任务/构建验收，标注历史资料（01/04/05/06/06M1；05 仅追加边界）。
- [x] 检查链接、Mermaid、代码围栏、残留旧架构要求与工作簿/meta哈希（见第7.11节）。

## 4. PoC验证计划

下表“实际”列是初轮结论，不是本轮回归结果。本轮以第7.7节回填为准。

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| 来源与版本 | 固定官方源码commit、Lua53原生库、Unity版本并记录SHA256 | 可追溯且可复现 | 通过。commit `161a675`，Lua53 `xlua.dll` SHA256 `4d7426d6…fc471`。未称最新版。见 `Docs/Acceptance/xLuaMigration/20261019/sources.json` |
| 互调与桥接 | C#调用Lua入口，Lua调用生成桥接的C# API | 编辑器与IL2CPP真实通过 | 通过。编辑器 `editor-proof.txt` 为 `ENABLE_IL2CPP=0`；Player `player-100.txt` 为 `ENABLE_IL2CPP=1`，`entry` 含 `proto=ok` |
| 协议与整数 | 使用现有C# Protobuf，验证bytes、repeated/map与大int64跨边界 | 数据准确，无精度损失 | 通过。契约 `lua53-integer`。`9007199254740993` 经生成桥接往返后 `VerifyAll` 返回 ok；未改主工程协议库 |
| 异步与清理 | Lua注册C#回调，取消/释放后检查引用和LuaEnv关闭 | 回调一次；关闭后无遗留执行 | 通过。`asyncCount=1`，`liveSubscriptions=0`，关闭前后计数同为 1。未用循环强制 GC |
| 资源与更新 | YooAsset加载Lua TextAsset/配置，100后仅发布101，新进程启动同安装 | 安装文件不变；新模块/配置有效 | 通过。环回 HTTP 日志有 `PocLua.version` 与 bundle。安装三个哈希前后相同。101 截图出现 `m1_patch_message` / `101-only-panel` |
| 失败不激活 | 缺脚本/语法错/损坏资源后启动 | 错误明确，不执行候选入口、不写坏活动索引 | 通过。201 缺入口、202 语法错误、203 损坏 catalog bundle 后活动索引仍为 100，入口未执行。`发布/packages/203` 归档哈希未改 |
| 文档一致性 | 全目录复扫，核对入口/桥接/构建/验收互相一致 | 现行设计可实施；历史身份清晰 | 见第7.11节与 `validate_xlua_docs.py` 最终报告 |

## 5. 问题与解决方法

1. 独立工程 `Packages/manifest.json` 被 PowerShell UTF-8 BOM 写成非法 JSON，且 `file:` 相对路径少了一层，包解析失败。去掉 BOM，改为 `file:../../Packages/...`。复验：`unity-gen.log` 之后能完成 GenAll，`gen-compiled.txt` 为 `wraps=5 link=True`。
2. IL2CPP 裁剪 Google.Protobuf 时找不到 `System.Runtime.CompilerServices.Unsafe`。从主工程插件目录复制该 DLL，SHA256 `bfc8f02a…e3d3`。复验：`player-build-report.txt` 为 `result=Succeeded`、`backend=IL2CPP`。
3. 【初轮，本轮撤回】Player 的 UnityWebRequest 把 `127.0.0.1:18119` 交给系统代理 `127.0.0.1:7078`，返回 502；编辑器同一 URL 返回 200。初轮因此给 YooAsset 挂了 `DirectLoopbackHttp`。随后同一套正式 Player 在 `localhost` 上用原传输已能通过，见 `PoC-network-findings.md`。本轮删除该自定义后端，服务前缀与 CDN 参数统一为 `localhost:18119`，不改系统代理。初轮 502 原文仍留在 `player-100-proxy-502.txt`。
4. 第一次损坏测试改到的是最大文件 `PocLua_203.report`，包仍能激活成 203。选择器改为只改 `*.bundle`。复验：`player-corrupt.txt` 下载 `fa7063b54aae408b33842d266d3565a9.bundle` 失败，活动版本保持 100，入口未执行。归档 `发布/packages/203` 中该 bundle 的 SHA256 `e2788769…5625` 与损坏副本 `6389064b…0ba3` 不同。
5. 【初轮】`Serve-Cdn.ps1` 前缀与 Player URL 曾一个用 `localhost`、一个用 `127.0.0.1`，HTTP.sys 返回 400，请求没有进入监听器。这不是代理故障。本轮两边都改为 `http://localhost:18119/`，避免再混用前缀。

## 6. 官方依据

- [Tencent/xLua](https://github.com/Tencent/xLua)
- [固定Windows版本](https://github.com/Tencent/xLua/releases/tag/v2.1.16_ndkr21b_with_win)：commit 161a6757af2ac30049e9c863534086430a12cb53；PoC优先Lua53原生库，实际兼容性需实测。
- [桥接与裁剪配置](https://github.com/Tencent/xLua/blob/master/Assets/XLua/Doc/configure.md)
- [LuaEnv/CustomLoader API](https://github.com/Tencent/xLua/blob/master/Assets/XLua/Doc/XLua_API.md)

## 7. PoC实施（独立工程，2026-10-19已执行）

本节追加实施与验证。第2节清单保持；现行 01/04/05/06/06M1 的对齐见第7.11节。旧T42不标完成。

### 7.1 范围与固定输入

- 工程根：`PoC/xLuaMigration/`。Unity工程在 `UnityProject/`；同级 `Tools/`、`Packages/`、`Builds/`、`发布/`、`缓存/`。验收证据在 `Docs/Acceptance/xLuaMigration/20261019/`。
- Unity编辑器：`D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe`。主工程进程不关闭、不作为执行目标。
- xLua固定为 tag `v2.1.16_ndkr21b_with_win`，commit `161a6757af2ac30049e9c863534086430a12cb53`。只使用 Lua53 源码与 Windows x64 原生库，不拼接 LuaJIT 或其他版本。不把该 tag 写成最新版本。
- 只提取 XLua `Src`、`Editor`、Windows64 Lua53 插件和许可证。不导入官方示例工程。
- YooAsset 3.0.6 从 `Library/PackageCache/com.tuyoogame.yooasset@e448e243828c` 只读复制到独立 `Packages` 后嵌入。不安装 HybridCLR，不使用 il2cpp_plus。
- 协议程序集复制 `Server/CloudIsle.Shared/bin/Debug/netstandard2.1/Game.Shared.dll` 与 `Assets/Plugins/Google.Protobuf/Google.Protobuf.dll`。不引用 Game.Hotfix。正式消息使用已有 `HeartbeatReq`（字段1 `client_time`，MsgId 1001）和 `AvatarData`（字段1 `owned` repeated，字段2 `equipped` map）。C# Protobuf 保持 AOT，Lua 只搬运 bytes 与桥接整数。
- 热更主路线是生成的 LuaCallCSharp / CSharpCallLua。不用 xLua.hotfix 注入任意 C# 方法。
- 整数契约：Lua53 的 64 位整数（`math.type == "integer"`）。禁止把 `9007199254740993` 经 double 静默截断。若实测整数桥接失败，改为十进制字符串并在问题表记录，不能假装整数已通过。
- 资源版本 100 与 101 共用同一套 C# 桥接签名和 `AppVersion=poc-bridge-1`。新增 C# API、原生库或桥接签名才升 AppVersion；只增 Lua 模块不升。

### 7.2 最小步骤

1. 校验 `Downloads/provenance.json` 的 SHA256 后解包，复制 YooAsset 与协议 DLL，写来源哈希。
2. 创建独立 Unity 工程并写入最小运行时/编辑器脚本、Lua 内容、link.xml 基线。
3. 批处理 `PocBatch.GenBridges` 执行 GenAll。新进程等编译结束后再构建资源包与 IL2CPP Player。
4. 环回 HTTP 发布 100，编辑器 Play 与 IL2CPP Player 各跑正式闭环。
5. 保持安装目录不变，只切换 CDN 到 101，新进程验证新模块与配置。
6. 用隔离缓存副本验证缺模块、语法错误、损坏包不提交活动索引。
7. 把命令、日志、哈希、截图和未通过项回填本节与第4节。

### 7.3 模块地址、桥接基线、活动版本提交顺序

| Lua模块名 | YooAsset地址 | 100 | 101 |
|---|---|---|---|
| entry | lua_entry | 有 | 有 |
| panel_base | lua_panel_base | 有 | 有 |
| proto_case | lua_proto_case | 有 | 有 |
| async_case | lua_async_case | 有 | 有 |
| m1_patch_message | lua_m1_patch_message | 无 | 有 |
| （配置，不是模块） | cfg_runtime | 键 banner | 增加键 m1_patch_message |

桥接基线（AppVersion `poc-bridge-1`，签名不变）：`ProtoBridge.BuildHeartbeatBytes`、`BuildAvatarBytes`、`ReadHeartbeatClientTime`、`EchoInt64`、`EchoBytes`、`VerifyAll`；`BridgeApi.HasModule`、`CreatePanel`、`AppVersion`、`ResourceVersion`；`AsyncLease.Subscribe/Unsubscribe/Dispose`；`ILuaEntry.Start(string)`；`PocCallback(int,string)`。

活动版本提交顺序（本轮修正初轮“编译通过后、执行入口前就 Commit”）：YooAsset 下载并完成清单校验 → 用句柄把 catalog 与模块读成 TextAsset → 比对 catalog 内 SHA256 → 检查入口名合法且该模块已载入，活动索引将写入的契约与 `ProtoBridge.Contract` 一致 → 临时 LuaEnv 只编译不执行入口 → 缺模块或语法失败时入口调用次数为 0，不写 `active-index.json` → 正式 LuaEnv 按 catalog 的入口名 require 并调用 `Start` → 入口运行时失败则回滚已跟踪的宿主面板，不改活动索引；这不是任意 Lua 沙箱，未跟踪的 Lua 文件副作用不在此列 → 入口返回且异步回调按租约完成后才写入活动索引。旧索引按整文件哈希保留，不只比较版本字符串。

### 7.4 实施验证计划（初轮范围）

下表全部“通过”只描述初轮。异步跨帧、入口提交顺序、标准 UnityWebRequest、严格失败阶段都不在初轮结论内。本轮结果见 7.7，初轮证据保留在 `Docs/Acceptance/xLuaMigration/20261019/Initial-PoC/`。

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| 来源哈希 | 按 provenance 校验 zip/tgz；记录 commit、许可证、原生库与复制 DLL 的 SHA256 | 与固定 tag 一致；不称最新版 | 通过。`sources.json`、`xLua-LICENSE.TXT`。xLua 源码与二进制均为 MIT；本次用的是固定 tag，不是最新版 |
| 编辑器互调 | 独立工程批处理进入 Play：LuaEnv、C# 调 `ILuaEntry.Start`、Lua 调已生成桥接、CSharpCallLua 回调/接口可用 | 报告 editor-proof 通过；日志来自该工程 | 通过。`editor-proof.txt`：`entry=phase=boot;proto=ok;async=calls=1;patch=absent` |
| 编辑器协议整数 | `HeartbeatReq`/`AvatarData` bytes 经 Lua 回传；`9007199254740993` 用 Lua53 integer 往返；repeated/map 字段号不变 | 精确相等；`math.type` 为 integer | 通过。Lua 在 `math.type` 不是 integer 时会中断；实际 `proto=ok` |
| 编辑器异步清理 | Lua 订阅一次异步回调；取消/Dispose；Tick 后释放 LuaFunction、LuaTable、委托；关闭 LuaEnv 后再触发宿主，计数不再增加 | 回调一次；关闭后 0 次遗留；无循环强制 GC | 通过。`asyncCount=1`，`asyncBeforeDispose=1`，`asyncAfterDispose=1`，`liveSubscriptions=0` |
| 编辑器 YooAsset | Host 模式环回 HTTP 下载后 `LoadAssetAsync<TextAsset>`，CustomLoader 读已复制字节再 require；随后 `Release` 句柄 | 不是 File.ReadAllText；句柄 IsValid 变 false | 通过。`cdn-http.log` 20:44:13 的 GET；`handleReleased=True` |
| IL2CPP Player | 同一套检查在 Windows x64 IL2CPP Player 启动日志出现 `ENABLE_IL2CPP=1`；工程无 HybridCLR | 编辑器结果不算此项 | 通过。`player-100.txt` 与 `player-100-player.log` 均为 `ENABLE_IL2CPP=1`。通过项里没有 HybridCLR 程序集；工程未装该包 |
| 100到101 | 记录 exe、GameAssembly.dll、global-metadata.dat 哈希；CDN 换 101；同一 exe 新进程出现 `m1_patch_message` 面板与配置键；截图 | 三个安装哈希不变；100 日志无该模块 | 通过。`install-hash-before-101.txt` 与 `install-hash-after-101.txt` 三个哈希一致。`shot-100.png` 只有 `panel_base/base-100`；`shot-101.png` 增加 `m1_patch_message/101-only-panel` |
| 失败不激活 | 隔离缓存分别对缺入口、语法错误、损坏 bundle 启动 | 活动索引仍是旧版本；不执行失败入口；好包归档仍在 | 通过。`player-missing.txt`、`player-syntax.txt`、`player-corrupt.txt`：`activeAfter=100`，`entryExecuted=False` |
| 桥接进包 | 构建前独立进程 GenAll；Player 能调用生成包装类；link.xml 保留 | 不能只在编辑器反射成功 | 通过。`Assets/XLua/Gen/link.xml` 与 5 个 Wrap 在第二次进程编译后进 IL2CPP Player，Player 能跑通上述 Lua 调用 |

### 7.5 问题与解决方法

与第5节相同，另加本轮：

6. 编辑器复用旧的 `缓存/editor-100` 时，里面已有 `PocLua_100.hash`，YooAsset 不再下载新清单，于是执行了上一轮 Lua，`async=calls=0`。这是同版本号缓存命中，不是代理故障。本轮改用新的 `缓存/regression-editor-100` 与 `缓存/regression-100`，没有禁止正常缓存复用。
7. `PocCallback` 委托类型不能直接写成 `is IDisposable`，编译错误 `CS8121`。改为装箱后再释放生成委托。
8. 已删除 `DirectLoopbackHttp`。`Serve-Cdn.ps1` 前缀和 CDN 参数都是 `http://localhost:18119/`。本轮 Player 日志只有 `transport=UnityWebRequestBackend`。未改系统代理。
9. 协调者看到的首次损坏报告 `activeAfter=203` 已不在仓库里，没有补造。`Initial-PoC/player-corrupt.txt` 仍是后来那次 catalog bundle。本轮另选 203 独有的 `lua_corrupt_probe`。

证据目录：`Docs/Acceptance/xLuaMigration/20261019/`。可重跑脚本：`PoC/xLuaMigration/Tools/Run-XLuaPoc.ps1`、`Serve-Cdn.ps1`。命令记录：`commands.log`。初轮原文留在 `Initial-PoC/`。

### 7.6 结果边界

初轮编辑器与 IL2CPP 技术项曾记为通过，该结论只覆盖初轮源码与 `DirectLoopbackHttp`。文档一致性未做，01/04/06 未改。主工程未迁移。旧 T42 不标完成。本轮改为 YooAsset 自带传输，不再把初轮通过写成当前结果。

### 7.7 本轮缺口复验

只修了隔离 PoC。未改 01/04/06，未迁移主工程，未 reset/revert/clean，未提交。触发项是协调者对半成品的审查，以及初轮为绕过 502 加入的 `DirectHttp`。localhost 原传输依据见 `PoC-network-findings.md`。

命令：`Run-XLuaPoc.ps1 -Step packages`，随后 `editor`、`player`、`run`。最后一轮 Player 安装哈希：

- exe `29ab39aae700ec2503f7b7fe7de2d853e661c5588c5a225c540ad81474dcd0f1`
- GameAssembly `e732883654fc5fd590f3b0aa4d5d14af2793e760d3ef2b112a0b017cfd0cd043`
- global-metadata `b65f3f076a5ce49555fdd81198f0e6eaf17a6e2155c81fb0e3009ef1c577dbb8`

101 前后这三项相同，见 `install-hash-before-101.txt` 与 `install-hash-after-101.txt`。

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| 入口提交顺序 | 校验入口名与模块存在、契约一致；成功跑完入口和异步后才写 active-index；204 为语法正确但 Start 抛错 | 缺模块/语法 0 次调用；运行时失败不改索引并回滚已跟踪面板；不声称任意 Lua 沙箱 | 通过。`player-missing.txt`、`player-syntax.txt` 的 `entryInvocations=0`，活动索引哈希保持 `761c93f7…7604`。`player-runtime.txt`：`packageVersion=204`，`entryInvocations=1`，`entryExecuted=False`，`sideEffectRolledBack=True`，面板不再含 `must-rollback`，`sandbox=host-tracked-panels-only` |
| 异步租约 | 回调至少跨一帧且只一次；取消路径先登记再等待再退订；Dispose 后推进同一租约 | `asyncFrames=1`，`asyncCount=1`，取消等待至少 1 帧，关闭前后是同一个 ProducerId，无 Dispose 泄漏，`gcForced=0` | 通过。编辑器 `editor-proof.txt` 与 `player-100.txt` 均为 `async=armed`、`asyncFrames=1`、`cancelRegistered=True`、`cancelWaitFrames=1`、`asyncBeforeDispose=1`、`asyncAfterDispose=1`、`disposeError` 为空。100 的 live id `0fe9a546…b5c` 在 Dispose 前后相同 |
| 迭代器异常 | 手写嵌套 MoveNext，yield 不放进 try/catch | 内层抛错仍写出拒绝报告，不改活动索引 | 通过。`player-iterator.txt`：`rejectStage=iterator-fault`，原因含 `iterator-fault synthetic`，`entryInvocations=0`，索引哈希不变 |
| 失败报告条件 | 拒绝用例同时要求 IL2CPP、桥接基线、候选版本、阶段和原因 | 早期断网或初始化失败不能冒充缺模块、语法或损坏包 | 通过。201/202/203/204 报告都有 `ENABLE_IL2CPP=1`、`appVersion=poc-bridge-1`、`contract=lua53-integer`，且 `packageVersion` 与 `rejectStage` 分别是 201/missing-module、202/syntax、203/corrupt-bundle、204/runtime-start |
| 路径边界 | 删除/移动目标必须是 PoC 根加目录分隔符之下 | 根目录和 `xLuaMigrationSibling` 被拒绝 | 通过。`path-guard.txt`：`refusedCount=3`，同级目录被拒绝。未删除 PoC 根，未动主工程 Unity（pid 5284） |
| 标准传输 | 移除 DirectHttp；HTTP.sys 与 CDN 都用 localhost:18119 | 重建后的 Player 用 YooAsset 默认传输完成 100 与 101 | 通过。服务日志以 `listen http://localhost:18119/` 开头。`player-100.txt` 为 `pass=true`、`transport=UnityWebRequestBackend`。Player 日志没有 DirectHttp |
| 损坏包 | 只损坏 203 独有且实际请求的新 bundle；100 已有文件保持完整 | HTTP 200 打到该副本，校验失败，活动索引与旧文件哈希不变 | 通过。目标 `cb8f59fb51ce96d1e4dcb1e0b0b582d5.bundle` 不在 100 包内。归档哈希 `14b32939…d978`，损坏副本 `c4819a9b…67aa`。`cdn-http-regression.log` 21:39:22 对该文件返回 200。报告含 `DataFileCrcError` 与 `watchedUrl=http://localhost:18119/cb8f59fb…bundle`。`cache-preserve-corrupt.txt` 旧索引哈希不变，25 个旧文件仍在 |
| 证据时序 | 报告、安装哈希、截图晚于本轮最终源码 | 源码再改就重新构建并重跑 | 通过。`run` 在最终 `PocRunner.cs` 与脚本之后比较了报告、两张截图和安装哈希的写入时间。`shot-100.png` 为 host/res 100 与 `base-100`；`shot-101.png` 增加 `m1_patch_message` / `101-only-panel`。100 没有该模块和配置键 |

本轮没有把编辑器结果当作 IL2CPP 结果。入口副作用只回滚了宿主登记的面板，没有 Lua 沙箱。主工程迁移见第7.8节。

### 协调者源码复核补充（2026-10-20）
补验报告中的gcForced=0表示项目未额外强制GC；固定版本xLua的LuaEnv.Dispose内部会调用GC.Collect及终结器等待，不能解释成供应商释放过程完全不GC。退订和清理必须先完成，主工程按实际拥有权管理桥接回调。隔离 PoC 报告不代替主工程验收；主工程证据在 `20261020/MainProject/`。

### 7.8 主工程修复复核与验证（2026-10-20）

在已接入的主工程源码上按 MainReviewNotes 修复后实测。独立 PoC 八份报告已按 SHA 恢复到 `20261019/`，与主证据隔离。

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| Gen/编译 | BatchGenBridges + AssertGenCompiled | wraps>0 且 link.xml | 通过。`gen-compiled.txt` wraps=5 link=True |
| 风险 EditMode | `XLuaMigrationRiskTests` | 非零且通过 | 通过 7/7（坏 bridgeContract、重复映射、嵌套异常、原子索引失败保旧、AppVersion≠bridgeContract） |
| 启动取消 | PlayMode `XLuaBootCancelTests`（含提交前屏障中途取消与宿主销毁） | 预取消/中途 OCE；销毁结束等待；LiveSubscriptions=0；DisposeError 空串 | 通过 3/3。FinalPolish 去掉恒真断言与全面 `ignoreFailingMessages` 后复跑见 `Cleanup/Final/playmode-results.xml` |
| 配置 PlayMode | `ConfigServiceTests` 激活模拟清单后跑原四项 | 非零且全通过；不标 T42 完成 | 通过 4/4。收集路径改 `Assets/ConfigLuban`（StreamingAssets 副本保留） |
| Editor 证明 | BatchEnterEditorProof（文件请求跨域重载） | pass、asyncFrames≥1、appVersion=0.2.0、bridgeContract=main-bridge-1 | 通过。`20261020/MainProject/editor-proof.txt` |
| 标准 IL2CPP | BatchBuildPlayer | Succeeded、ENABLE_IL2CPP=1 | 通过。清理前后均 Succeeded |
| 内置100 | Farming.exe 无 CDN | pass、patch=absent | 通过。`Cleanup/player-post-builtin-100.txt` |
| 100→101 | localhost CDN + 同安装缓存 | 三哈希不变；101 独有面板/文案 | 通过。`Cleanup/player-post-100/101.txt`；`install-hash-post-*.txt` HASH_EQUAL_POST；截图 `shot-post-100/101.png`（Lua/文案演示，**非 T63 Prefab**） |
| 候选拒绝 | 主 Player HTTP：缺入口/语法/坏桥接/重复/局部租约/异步抛错/提交前嵌套/索引写失败 | 阶段匹配；索引不变；非早期错误冒充 | 通过。`Cleanup/player-reject-*.txt`；嵌套从 100→101 注入，`indexUnchanged=True` |
| GUID | Loader/场景 meta | 原 GUID | Loader `57deb669…f2d64d`；场景 `5010700a…3300d` |
| T63 | 新 Lua+Prefab+键 | 正式验收 | **未完成**；当前演示仅 Lua/文案，无新 Prefab |

### 7.9 主工程旧依赖清理与复验（2026-10-20）

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| 清理矩阵 | 删除 HybridCLR 包/设置/HybridCLRData/StreamingAssets AOT 元数据目录等 | 活动引用清零；保留 HotfixLoader GUID 与 xLua 场景 | 通过。`Cleanup/deleted-list.txt`；活动 Assets 扫描 HybridCLR 运行依赖 0 命中 |
| 工具链 | 清理后批处理确认标准 Unity IL2CPP 路径 | 非 HybridCLRData/il2cpp_plus | `Cleanup/il2cpp-toolchain.txt` → `…/6000.3.22f1/Editor/Data/il2cpp` |
| 清理后构建运行 | Gen + PlayMode7 + Win64 IL2CPP + 内置100 + 100→101 | 无 HybridCLR 包/程序集；三哈希不变 | 通过。`Cleanup/post-player-result.txt`；ScriptAssemblies 无 HybridCLR*.dll |
| 残留旧 DLL | Cleanup 后现场仍有 `Assets/StreamingAssets/Game.Hotfix.dll.bytes`（摘要曾误称已无） | 源与最终 Player StreamingAssets 均无；原件快照保留 | FinalPolish 已删并重建确认。`Cleanup/Final/deleted-hotupdate-dll.txt`；`player-streamingassets-scan.txt` staleCount=0；原件 SHA=`8595498d…023f6` |
| FinalPolish 复验 | 收紧 BootCancel 断言后 PlayMode7 + 实际重建 + 内置100 + 100→101 | 7/7；同安装三 SHA 不变；UI 本轮不改 | 通过。`Cleanup/Final/round-result.txt` `HASH_EQUAL_FINAL`；视觉终证仍为 Root 已审 `Cleanup/shot-post-100/101.png`；重建旁证 `Final/shot-100/101.png` |

平台限制：本轮仅 Windows x64 IL2CPP；未验收 Android/iOS；完整 M1 未完成。证据根：`Docs/Acceptance/xLuaMigration/20261020/MainProject/Cleanup/`（含 `Final/`）。步骤与问题表：`20261019/MainProjectMigration.md`。

### 7.10 配置导出同步前置（MainReviewNotes #20，2026-10-20）

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| 缺口 | Cleanup 将 Yoo 收集改为 `Assets/ConfigLuban`，`gen_config.ps1` 原只同步 Server + StreamingAssets | 同事务增加 Yoo 镜像；保留 StreamingAssets 直读 | 已修。`Tools/gen_config.ps1` 增加 `yooCollect`→`Assets/ConfigLuban`；`Assert-PublishedData` 含四数据目录 |
| 正式导出 | T41 原命令：`-RepoRoot … -SourceDir Shared\config -ExporterPath Tools\Luban\Luban\Luban.dll -ConfigVersion 100` | EXIT 0；四数据目录清单/表 SHA 一致；源 xlsx 不变 | 通过。`20261020/MainProject/ConfigExport/10-success.txt` EXIT 0；`12-cross-dir.txt` ALL_FOUR_DATA_DIRS_EQUAL；`tbglobalkv` SHA 与 T41 一致 `ffd517ee…1642`；`source-xlsx-after-check.txt` unchanged |
| 新增目的地回滚 | `GEN_CONFIG_FAIL_POINT=AfterYooCollect` | 非 0 退出；Yoo JSON 恢复；四目录仍一致 | 通过。exit=5；`20-yoo-rollback-check.txt` ROLLBACK_OK；`21-post-rollback-equal.txt` |
| Native | 运行 C# / 配置 bytes 未改语义 | 不重跑无关 Player | 未重跑 Native；配置 JSON SHA 与导前一致 |

区分：导表源=`Shared/config`；生成代码=`Assets/Config/Generated`；四数据出口=Shared Output / Server / StreamingAssets（历史直读）/ `Assets/ConfigLuban`（Yoo 可收集）。**不得**声称 StreamingAssets 可直接 EditorSimulate 收集。

### 7.11 ReferenceDoc 现行对齐与校验（2026-10-20）

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| Notes17 核实 | 读 `XLuaBootCancelTests` | DisposeError 空串；无全面 ignore | 已满足。`Is.EqualTo(string.Empty)`；Destroy 用例 `ignoreFailingMessages=false`+finally 恢复；证据 `Cleanup/Final/playmode-results.xml` |
| 应用草稿 | DocumentDrafts×5 → ReferenceDoc，对照最终源码修正 | `*.txt` TextAsset；catalog 字段；不称完整 T63/M1/移动端 | 已应用。脚本 `20261020/MainProject/ApplyDocumentDrafts.py`；04 §§15–17 / 05 正文与原件一致 |
| 历史保留 | 19 份历史 MD 正文；02/03/xlsx/30meta 字节 | 不变 | 由 `validate_xlua_docs.py` 最终报告核对 |
| 独立 PoC SHA | 只读比对八份报告与 `PoC-coordinator-review.json` | 八 SHA 匹配；不重跑 PoC | 见 `Root-PoC-restoration.json` / 本轮只读复核 |
| 文档校验 | `20261019/validate_xlua_docs.py`（无 `--preservation-only`） | errors=[] | 见同目录 `ReferenceDoc-validation.json` |

主工程 Lua 实际为 `Assets/Lua/*.txt`；入口 `ILuaEntry.Start(phase)`；`bridgeContract=main-bridge-1` 与 `Application.version=0.2.0` 分开。未声称完整 M1 或移动端完成。
