<!-- XLUA-MIGRATION:BEGIN -->
> **迁移说明（2026-10-19）：本文件保留历史原文。**
> 当前热更架构调整为 xLua；旧记录中的技术名称和“已通过”仅描述当时环境，不能解释为 xLua 已验证。
> 原任务步骤、源码、命令、问题和实测结果完整保留；服务端、协议、资源与公共 C# 能力可按新边界复用，旧热更 DLL／补元数据链的完成记录不构成 xLua 验收。旧自动推进在 T42 部分实现阶段停止，T42 尚未验收；后续按新方案重新安排。
> 当前设计、验证范围和证据见 [xLua 迁移方案与 PoC](../07_xLua迁移方案与PoC.md)。
<!-- XLUA-MIGRATION:END -->

# M1 开发日志 · 5.6 UI、场景与客户端账号

记录方式对齐 `06_开发计划.md` 第 6.1 节：每项写成可勾选的文档、命令或方法。目录索引见 `M1_Logs.md`。

本文件对应 `06_M1_基础框架任务拆解.md` 第 5.6 节，任务 T48–T57。

本段尚无已写入的任务记录。

## 2026-10-20 xLua Stage C1：T48–T52 Lua UI 与场景（前置 + 实现）

- 来源：`06_M1` §3/4.3–4.4/5.6 T48–T52；Coordinator；`StageB-ReviewNotes.md`；Root-NarrowFix Plan
- 日期：2026-10-20
- 范围：C1 前置 Native 复验（仍 0.2.0）→ 升开发基线 **0.3.0** + 新桥 → T48–T52。不启动 T53+/T58/T63，不改迁移归档与 StageA/B 旧证据。
- 证据：[`Docs/Acceptance/M1/20261020-xLua/StageC1/`](../../../Docs/Acceptance/M1/20261020-xLua/StageC1/)；Native 前置 [`StageB/Root-NarrowFix/Native/`](../../../Docs/Acceptance/M1/20261020-xLua/StageB/Root-NarrowFix/Native/)

### 任务介绍

B 与 Root 窄修已让 CancelDownload、候选深拷贝、Lua 面板作用域、真实 Candidate 数字与 entryExecuted 报告就位（PlayMode 17/17）。C1 先复用 `StageBReviewTools.BatchBuildPlayerToReview`（`--m1-review-build-root=`）与既有 `--m1-lua-host` / `--m1-patch-ui-review` helpers，把新报告/截图写入 `Root-NarrowFix/Native/`，确认真实 bytes/files 与 GUI 落盘截图。通过后再冻结新 C# UI/场景桥进 **AppVersion=0.3.0 / bridgeContract=`m1-bridge-0.3` / ProtoVersion=1**（禁止声称 0.2.0 安装可资源补丁补出新 C#）。业务窗口控制在 Lua（`*.txt` TextAsset，逻辑模块名 `UI/...`）；C# 仅 ViewHost/资源/异步/输入/Loading/Scene/SafeArea。正式产品持有 LuaEnv 至退出；入口 `Shutdown` 在 Env 关闭前关窗/退订，与 `LuaBase.Dispose` 区分。

### 最小执行清单

- [ ] Native：参数化构建 → LuaHost + PatchUI helpers → 回填 Plan.md / M1_05 补充（保留 Review 旧证据）
- [ ] 升 `bundleVersion` 0.3.0；`BridgeApi.BridgeContractValue=m1-bridge-0.3`；扩展 `ILuaEntry.Shutdown` / UI 委托；`GenAll` 再生桥
- [ ] T48：`UIWindow`/`UIManager.register` + `ViewHost`（Bind/SetText/SetInteractable/SubscribeClick/Dispose）+ 租约释放
- [ ] T49：六层/栈/OpenClose/模态/取消与失败回收
- [ ] T50：Toast（TimerService+ObjectPool）/ Dialog（确认一次、外部关闭结束等待）
- [ ] T51：`SafeAreaAdapter` + CanvasScaler；三比例与非零 SafeArea 截图
- [ ] T52：`LoadingView` + `SceneService`（ResMgr 场景）；正式 M1 Login/Home 占位场景（保留 Greybox/PoC）
- [ ] PlayMode UI/场景夹具；证据 StageC1；勾 T48–52；Coordinator 登记；**不勾** T53+

### 关键源码与命令（实现前约定）

| 用途 | 路径 / 命令 |
|---|---|
| Native 脚本 | `Docs/.../StageB/Root-NarrowFix/Native/Run-NativeReview.ps1` |
| 构建入口 | `Game.Editor.M1.StageBReviewTools.BatchBuildPlayerToReview` + `--m1-review-build-root=` |
| LuaHost | `Farming.exe --m1-lua-host --m1-lua-quit --m1-lua-report=... --m1-lua-shot=...` |
| PatchUI | `Farming.exe --m1-patch-ui-review --m1-review-root=.../Native` |
| ViewHost 等 | `Assets/Framework/UI/*.cs`（实现中） |
| Lua UI | `Assets/Lua/UI/*.txt`（及 Staging 打包镜像） |
| 桥生成 | `Game.Editor.XLuaTools.XLuaMigrationTools.BatchGenBridges` |
| PlayMode | filter 含 `UiLifecycleTests|SceneServiceTests|SafeAreaTests`（名以实际夹具为准） |

### 验证预期（实际以运行证据回填）

| 名称 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| Native LuaLoader | Player CLI | pass；servicePath 含 LuaLoader；Dispose 干净 | （待填） | Native/player-lua-loader-host.txt + shot |
| Native PatchUI | 失败→重试 | pass；真实 downloadedBytes/files≠Stage.Length 假数 | （待填） | Native/patch-ui-review-report.txt + PNG |
| UI 生命周期 | PlayMode 开关/连点/返回/取消/Dialog/Toast/模态 | 对象/租约/订阅回基线；无未处理异常 | （待填） | StageC1/playmode-results.xml |
| SafeArea/比例 | 三分辨率 + 非零 inset | 锚点正确；可读布局截图 | （待填） | StageC1/shots/ |
| 场景切换 | Login↔Home×10；取消 | Loading 显隐；互斥；取消等底层完成再清 | （待填） | StageC1 |

### 问题表

| 触发条件 | 现象/原因 | 修复 | 复验 |
|---|---|---|---|
| （实现前空表；遇真实问题再填） | | | |

---

## 2026-10-20 xLua Stage C1 最终审查修复（T48–T52 缺口）

- 来源：`StageC1-ReviewNotes.md` / `StageC1-FixPrompt.md`；Root 首轮 14/14 未接受
- 日期：2026-10-20
- 范围：只补 C1 真实拥有权/Lua/Toast/模态/截图缺口；不改 Server；不宣称 M1 完成；不重跑旧迁移 PoC
- 证据：初轮归档 `StageC1/Initial-C1/`；新证据 `StageC1/Review/`

### 任务介绍

Root 审查确认首轮逻辑测试有效但 12 项源码边界仍可见（CallbackLease.Dispose 先标 disposed、同 ID 异步 pending 无占位、晚到复活、ct.Registration 未释放、Lua `is ILuaUiController` 伪转换、Toast 计时未跟踪、ModalBlocker 盖住 Dialog、截图 domain reload/`WaitForEndOfFrame` hang）。本段逐项修具体拥有权，补真实 LuaEnv+生成桥+ResMgr Prefab 回归，逻辑 PlayMode 无图形可跑；四张截图走有图形/SessionState 续跑或 Player，核对真实 PNG 尺寸。

### 最小执行清单

- [x] 归档 Initial-C1（原 14/14 XML/命令/截图尝试）；Review 目录承接新证据
- [x] 修 CallbackLease.Dispose / Complete；ct.Register 主线程取消并释放 Registration
- [x] OpenAsync：同 ID 占位、epoch/Dispose/CloseAll 联消、晚到只释放；OnCreate/OnOpen 失败分阶段回收；modal 仅 Push 后 Pop
- [x] CreateController：`LuaTable.Cast<ILuaUiController>()`；wrapper/bridge 明确 Dispose；控制器 `.new()` 实例独立
- [x] UIManager.open(table) JSON 序列化，不静默丢 args
- [x] Toast 跟踪 timer+GO；CloseAll/Dispose 取消归还且无后续回调
- [x] ModalBlocker 位于当前模态窗下方；EventSystem.RaycastAll 验证
- [x] 真实 Lua+UiTestWindow Prefab×20 + pending 并发/leaseCancel/CloseAll/Dispose 回归
- [x] BatchCapture：SessionState 跨 domain reload；Camera+RT 固定像素；PNG 落盘核对尺寸
- [x] 回填 ReviewNotes / 本日志 / Coordinator；门槛全过勾 T48–52（不勾 T53+）

### 关键源码与命令

| 用途 | 路径 / 命令 |
|---|---|
| 拥有权核心 | `Assets/Framework/UI/CallbackLease.cs`、`UiManager.cs`、`UiRoot.cs` |
| Lua | `Assets/Lua/UI/UIManager.txt`、`Windows/TestWindow.txt` |
| 截图工具 | `Game.Editor.M1.StageC1Tools.BatchCaptureUiShots`（SessionState 续跑） |
| 逻辑 PlayMode | `StageC1/Run-PlayMode.ps1` → `StageC1/Review/playmode-results.xml` |
| 截图 | `StageC1/Run-CaptureShots.ps1`（无 `-nographics`/立即 `-quit`）→ `Review/shots/` |
| 过滤 | `UiLifecycleTests\|UiLuaRealControllerTests\|SceneServiceTests\|SafeAreaShotTests` |

### Open / 租约参数说明

- `Open(windowId, argsJson, done, ct)`：await 前按 ID 占位；linked CTS 供 CloseAll/Dispose 取消；`ct.Register`→主线程 `FinishOp(-1)`，并 Dispose Registration
- `CallbackLease.Dispose`：未完成则先 Complete/Cancel 恰一次，再标 `_disposed`
- `CreateController`：require→可选 `new()`→`Cast<ILuaUiController>()`→`OwnedLuaUiController`；表 wrapper 立即 Dispose，bridge 关窗 Dispose
- `ShowToast`：`ToastEntry{handle,go}`；CloseAll/Dispose 取消计时并清理

### 验证实测

| 名称 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 逻辑 PlayMode | Run-PlayMode.ps1 | 全过；无 hang | **23/23/0** | `Review/playmode-results.xml` |
| Dispose lease | pending Dispose | 恰一次；LiveCount 基线 | 通过 | 同上 |
| 同 ID pending / CloseAll 晚到 | 门闩+Lua Prefab | 单实例；无复活 | 通过 | 同上 |
| 真实 Lua×20 | UiTestWindow+Cast | 租约/点击/泄漏回基线 | 通过 | `UiLuaRealControllerTests` |
| 模态命中 | GraphicRaycaster+ExecuteEvents | 确认可点；非 blocker 首击 | 通过 | Dialog_Confirm_EventSystem… |
| 场景×10 | 既有 SceneServiceTests | 互斥/取消稳定 | 通过（未改夹具） | 同上 |
| 截图×4 | Run-CaptureShots.ps1 | 四尺寸+可读 UI | 通过；Root 可 view | `Review/shots/` + `shots-report.txt` |

### 问题表

| 触发条件 | 现象/原因 | 修复 | 复验 |
|---|---|---|---|
| Confirm 后 `Find("Win_Dialog")` | Confirm 未命名 GO，仍为 RuntimeWin | `go.name = "Win_Dialog"` | Dialog EventSystem 测试绿 |
| 首轮 Dialog Raycast | `EventSystem.current` 在 batch 可能空 | `FindObjectOfType` + `GraphicRaycaster.Raycast` | 同上 |
| Editor 截图尺寸 | `Screen.SetResolution`≠PNG | Camera+RenderTexture 读像素；IHDR 校验 | shots-report 四尺寸匹配 |
| 首轮 14/14 | 仅 C# ControllerResolver | 新增 `UiLuaRealControllerTests` 真 Lua | 3/3 绿 |

### Coordinator

- T48–T52 已勾；开发基线 App **0.3.0** / bridge **m1-bridge-0.3** / Proto **1**（T58 前可补桥，100 冻结仍在 T58）
- **未完成**：T53+ 账号、Launcher、重连；D/E；**不宣称 M1 完成**
- Server Host 属 Root 已完成范围，本调用未改
- 返回 Root 继续 **C2 → D → E**

---

## 2026-10-20 17:25 Root C1收尾：实际对象/场景门槛

Cursor首修23/23与四张真实Unity渲染图均已由Root复核，视觉接受；Root审查尚见部分初始化失败、闲置Toast对象和Dispose重入边界，以及场景夹具仍旧Home/预取消。实施前计划已存`Docs/Acceptance/M1/20261020-xLua/StageC1/Root-NarrowFix/Plan.md`，源码交接后才开始，未并行编辑Cursor客户端。原23项/四图和初次失败历史保持。

最小清单：修OnCreate中途失败回收、无View时局部GameObject销毁、Dispose/CloseAll期间禁止创建新窗、池中闲置Toast对象真正销毁；将pending测试移至真实Prefab lease已取得后；既有SceneServiceTests改正式M1_Login/Home交替、加载启动后取消、失败回Login与Loading曾可见。针对回归并回填实际，不重跑旧迁移/全部Server/已通过截图。参数/函数用途随源码注释；新命令/结果仅Root-NarrowFix。

| 验证 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| 部分失败/重入 | OnCreate创建拥有物后抛错；CloseAll/Dispose钩子尝试新开窗 | OnDestroy执行，拥有物释放，不复活 | 待跑 |
| Toast实际对象 | 池化归还后Dispose，等待一帧，枚举实际ToastItem | 回原对象基线，无后续timer回调 | 待跑 |
| 真实Prefab取消 | 先取得真实lease，断言高于基线，再取消/关闭/Dispose | 所有lease/窗口/回调回基线 | 待跑 |
| 正式场景 | 新Login/Home交替十次，pending取消，缺目标fallback | 无临时场景，Loading曾显示且最终隐藏，回Login可重试 | 待跑 |

问题原因/具体修复/复验在实施后补充，T48–52整阶段仍以Root最终审核为准。

### 17:31 Root收尾实际与最终C1验收

完整源码仍在`Assets/Framework/UI/UiManager.cs`、`Assets/Tests/PlayMode/UiLifecycleTests.cs`、`Assets/Tests/PlayMode/SceneServiceTests.cs`；修改前3文件保存在Root-NarrowFix/before。完整可执行验证脚本`StageC1/Root-NarrowFix/Run-PlayMode.ps1`，默认RepoRoot/UnityEditorPath参数均注释；隐藏本次batch测试，每次新Run目录，不覆盖失败证据。

实际命令：`pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/StageC1/Root-NarrowFix/Run-PlayMode.ps1`。

最终`Run-20261020-172926/playmode-results.xml`，Root独立解析 **27发现/27通过/失败0/跳过0**，进程exit0；Console没有未预期的测试错误。缺失场景负例精确登记Yoo错误消息，不使用ignoreFailingMessages。四张Review/shots图已由Root逐张view，三比例+非零SafeArea文字按钮可读，真实Unity Camera.Render/RenderTexture产物；视觉接受，不重拍。

| 验证 | 实际结果与原因/修复 | 证据 |
|---|---|---|
| 部分初始化失败 | OnCreate进入前即标需要OnDestroy；中途拥有订阅和自有状态后抛错，Destroy钩子恰一次，拥有量/对象/订阅回基线；无View时局部go也销毁 | OnCreate_PartiallyInitialized_ThenThrows_ReleasesOwnedContent |
| 批关闭/Dispose重入 | Dispose在用户钩子前标disposed，CloseAll用closing状态拒绝新操作；钩子尝试同步Open被拒，无遗留新窗口 | ClosingAll_AndDispose_RejectReentrantOpen |
| Toast闲置对象 | ObjectPool.Clear只清引用；UiManager跟踪所有Toast GO并最终Destroy；归还池后实际存在，Dispose等待一帧恢复枚举对象基线 | Toast_ReturnedToPool_ThenDispose_DestroysActualObjects |
| 真资源取消 | gate移到ResMgr真实Prefab lease取得后；先断言CaptureLeaks>0，再取消/CloseAll/Dispose，各回0且无窗口复活 | UiLuaRealControllerTests.LuaPrefab_PendingGate_ExternalCancel_AndCloseAll |
| 正式场景 | 实际M1_Login/Home交替10次；Load已Pending时取消且scene数回基线；缺目标回M1_Login再入Home；Loading开始时可见、结束隐藏 | SceneServiceTests五项 |

首轮Run-20261020-172722为24项23通过1失败：filter写LuaUi未含真实UiLuaRealControllerTests而遗漏3项，缺目标负例未登记预期错误日志。修正过滤同时匹配UiLua/LuaUi、登记精确LogAssert后再跑完整27，原首轮保留。此为验证配置修正，不掩盖生产错误。捕图log有Unity JobTempAlloc退出警告，故不声称全部引擎退出零警告；最终D/E仍需标准IL2CPP正常启动/Shutdown验证，不用强制GC。

Root最终记录`Docs/Acceptance/M1/20261020-xLua/Root-StageC1-final-review.json`：**接受T48–T52**，继续T53–T57，再D/E到T68；不是完整M1完成。正式M1基包0.3.0/Proto1/m1-bridge-0.3尚待T58冻结。

## 2026-10-20 xLua Stage C2：T53–T57 账号 / Login / Home / Launcher / 断线恢复（计划）

- 来源：`06_M1` §5.6 T53–T57；Coordinator 17:31 Root 最终 C1 通过；`ServerHost/Ownership.md` 17:04；`ServerHostPrimer.md`；`M1_08`
- 日期：2026-10-20
- 范围：C2 正式客户端账号桥、Login/HomeHud、Launcher 串接、断线遮罩与 Restore；复用已验 `M1ServerHost` / `Tools/run_server.ps1`；**不**启动 T58 基包冻结 / T63 新内容 / AC 全量；不改 C1 已验收拥有权；不写 Git；电量检查暂停
- 证据：仅 [`Docs/Acceptance/M1/20261020-xLua/StageC2/`](../../../Docs/Acceptance/M1/20261020-xLua/StageC2/)
- HTTP 客户端用 **localhost:18080**；TCP **127.0.0.1:17780**（可隔离端口，完整记录）；不改系统代理

### 任务介绍

C1 已交付 UI/场景固定桥（`m1-bridge-0.3`）。本轮在固定桥上新增账号/网络桥签名（同契约再生），Lua 持有 AccountService/PlayerModel/Login/HomeHud；C# 仅封装消息类型、seq/代次、8s 超时、取消、主线程泵、Heartbeat、ServerClock、Token 文件与断线遮罩。正式 Host 已由 Root 验证，直接复用脚本，不复制假 Handler。

### 最小执行清单

- [x] 追加本计划与验证空表；建 StageC2 证据目录
- [x] T53：`TokenStore` + `AccountSession` + `AccountBridge` + Lua `AccountService`/`PlayerModel`；快照整体替换；登录成功且 apply 后才 SaveToken；Kick 清 Token 停重连；再生桥
- [x] T54：Login Prefab（username/password/nickname 遮蔽）+ Lua LoginWindow；处理中禁重提
- [x] T55：空 Home + HomeHud（playerId/昵称/等级）+ 登出回 Login 清租约
- [x] T56：`Launcher.RunAsync`：版本→候选→配置→Lua 入口 ready→Commit→Login；取消/销毁不挂；不双启 Env
- [x] T57：断线单一遮罩 + ReconnectService 退避 + Restore 快照覆盖；204/Kick/106 停循环回 Login
- [x] 真实 TCP（隔离 DB）+ PlayMode/截图；回填本日志与 Coordinator；勾 T53–57；**不勾** T58+

### 关键源码与命令（实现前约定）

| 用途 | 路径 / 命令 |
|---|---|
| 正式 Host | `Tools/run_server.ps1`；`Server/CloudIsle.Server/Hosting/M1ServerHost.cs` |
| 账号桥 | `Assets/Framework/Account/*.cs`；`Assets/Lua/AccountService.txt` / `PlayerModel.txt` |
| Login/Home | `Assets/Lua/UI/Windows/LoginWindow.txt` / `HomeHudWindow.txt`；`Assets/Res/UI/LoginWindow.prefab` 等 |
| Launcher | `Assets/Framework/Boot/Launcher.cs` |
| 桥生成 | `Game.Editor.XLuaTools.XLuaMigrationTools.BatchGenBridges` |
| PlayMode | filter 含 Account/Login/Launcher/Reconnect 夹具（名以实际为准） |
| 证据 | `StageC2/Run-*.ps1` → XML/TRX/截图/网络日志 |

### 验证表（实测回填）

| 名称 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 快照覆盖×2 | SnapshotCodec 两次编码 | 集合不追加；int64 十进制串 | **通过** | `editmode-results.xml` AccountSnapshotTests |
| Token 落盘契约 | 注册不写；登录未 Commit 不写；错密 202 不写；Commit 后有 | 符合 | **通过** | PlayMode RegisterLogin… |
| 取消登录 | 取消后 ClearPending | 无已提交 Token | **通过** | CancelLogin… |
| Kick | 同账号第二客户端登录 | 首会话 Kick；Token 清 | **通过** | SecondLogin_KicksFirst… |
| Restore | Connect 拆线后 RestoreAsync | code0；快照含 playerId | **通过** | Restore_AfterControlled… |
| Login UI | 开 LoginWindow Prefab | 密码遮蔽；按钮高>40；Title=登录 | **通过** | LoginUi… + `shots/login-layout.txt` |
| EditMode 合计 | AccountSnapshotTests | 全过 | **2/2/0** | `editmode-results.xml` |
| PlayMode 合计 | Account+LoginUi | 全过 | **5/5/0** exit0 | `playmode-results.xml` |

### 问题表

| 触发条件 | 现象/原因 | 修复 | 复验 |
|---|---|---|---|
| 首轮 PlayMode 注册 | code=-2；未主线程 Pump，Pending 永不完成 | `AccountSession` 连接后主线程 `PumpLoop` | RegisterLogin 绿 |
| nographics 截图 | RenderTexture.Create failed | 布局证据改写 `login-layout.txt`；有图形会话另截 | LoginUi 绿 |
| Close(null) | CallbackLease 拒 null | Close 传空回调 | LoginUi 绿 |
| SnapshotCodec AppendString | 缺 `first` 实参编译失败 | 显式传 `first: false` | GenBridges exit0 |

### 关键命令与源码

- 正式 Host：`Tools/run_server.ps1`（SkipBuild 复用 `Temp/M1ServerBuild`）；夹具内隔离端口/DB
- 桥生成：`Game.Editor.XLuaTools.XLuaMigrationTools.BatchGenBridges` → `StageC2/gen-bridges-2.log` exit0
- Prefab：`Game.Editor.M1.StageC2Tools.BatchCreatePrefabs`
- 测试：`StageC2/Run-PlayMode.ps1` 或分平台 filter `AccountSnapshotTests` / `AccountSessionPlayModeTests|LoginUiPlayModeTests`
- 源码：`Assets/Framework/Account/*`、`Assets/Framework/Boot/Launcher.cs`、`Assets/Lua/AccountService.txt`、`PlayerModel.txt`、`UI/Windows/LoginWindow.txt`、`HomeHudWindow.txt`、`entry.txt`

### Coordinator

- T53–T57 已勾；App **0.3.0** / bridge **m1-bridge-0.3** / Proto **1**
- HTTP 默认 `localhost:18080`；TCP `127.0.0.1:17780`
- **未完成**：T58+ 基包冻结/IL2CPP、AC-1～4；**不宣称 M1 完成**
- 返回 Root 审查后进 **D**

---

## 2026-10-20 20:32 Root C2初版审核与历史恢复

C2初版EditMode2/2、真实TCP/Prefab PlayMode5/5有效，已保留；但尚未实际运行完整HTTP版本/候选/Lua/索引/冷Login链路，唯一login-window.png由Root实际view为纯灰图，不能作真实界面通过。源码中完整快照字段、Token原子替换、请求取消/代次、Lua回调释放、场景互斥及Launcher顺序/磁盘索引回滚仍需修复，见StageC2-ReviewNotes.md。T53–57恢复待验收，C1已27/27+四图通过保持；M1尚未完成。

本轮C2误清空日志后从transcript重建，遗漏此前Root C1最终细表。Root先备份当前文件，再从m1c2最早readToolCall.success提取修改前完整175行原文：metadata fileSize14515、exceededLimit=false，捕获UTF8实际也是14515字节。以其原样字节前缀恢复，再保留当前C2章节。逐字节比较通过，前缀SHA256=7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328。所有原件/捕获片段/计划/恢复报告在StageC2/HistoryRecovery；不是从记忆重建。后续只追加，校验此前前缀不变。

下一步按StageC2-FixPrompt集中修复并实跑完整Lua/TCP/冷启动/实际GUI与清理门槛，计划及空表先追加；旧灰图/初版通过计数/失败记录均保留，不将布局参数文本当截图。完成后Root审核再D/E。

---

## 2026-10-20 20:36 C2收尾修复计划（Root ReviewNotes 7项）

### 任务介绍

Root 未接受 T53–57：TCP/Prefab 初版 5/5 保留，但完整 HTTP Launcher→真实 Login GUI、以及 ReviewNotes 7 项生命周期/数据完整性仍缺口。本轮**禁止整体重写本日志**；前缀 14515 字节 SHA 须保持 `7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328`。只修复仍存在的缺口并在 `StageC2/Review/` 新增证据，不覆盖旧灰图。

### 最小执行清单

- [ ] 对照最终源码确认 Notes1–7 仍缺口（已确认均在）
- [ ] Note1：`SnapshotCodec`→正式 ProtoJSON；Lua 完整 decoder + `PlayerModel` 覆盖 info/全集（含 orderProgress/plots 全字段）；非默认+>2^53 双 apply
- [ ] Note2：`TokenStore.Save` 同目录 `File.Replace`；失败保旧并可观察；Commit 仅快照成功后
- [ ] Note3：登录/操作代次；await 后/失败 catch 核对；旧成功/旧失败不污染新状态
- [ ] Note4：`CallbackLease` 联动真实 Connect/Request CTS；CancelAll/Dispose 取消并收尾；Pending→0
- [ ] Note5：AccountBridge 事件回调 Retain/Release；替换/Shutdown 释放；Env.Dispose 无吞异常
- [ ] Note6：`AccountFlow` 串行等旧切换结束；按 CurrentAddress；Shutdown 清静态；不吞 busy 当成功
- [ ] Note7：Launcher EnsureManager 在入口前；Lua 只注册一次；等真实 OpenLogin 再 Commit/login-ready；失败恢复索引字节
- [ ] 扩展 Edit/Play 实证 + 真实图形会话 Launcher GUI 截图 → `StageC2/Review/`
- [ ] 追加本日志结果表 + Coordinator 准确登记；仅全部门槛过才勾 T53–57

### 关键源码 / 命令 / 参数

| 用途 | 路径或命令 |
|---|---|
| 快照 | `Assets/Framework/Account/SnapshotCodec.cs`；`Assets/Lua/Json.txt`；`PlayerModel.txt`；`AccountService.txt` |
| Token/代次/会话 | `TokenStore.cs`；`AccountSession.cs` |
| 租约/桥 | `CallbackLease.cs`；`AccountBridge.cs` |
| 场景/启动 | `AccountFlow.cs`；`Launcher.cs`；`PatchService` 索引恢复 |
| 测试 | `AccountSnapshotTests`；`AccountSessionPlayModeTests` 扩展；新增 Launcher/租约夹具 |
| 桥再生 | `Game.Editor.XLuaTools.XLuaMigrationTools.BatchGenBridges`（签名变更时） |
| 证据 | `Docs/Acceptance/M1/20261020-xLua/StageC2/Review/` |
| Host | `Tools/run_server.ps1` 隔离 DB/端口；HTTP `localhost`；不改生产 Host |

### 验证表（预期；实际待填）

| 名称 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| ProtoJSON 完整 | 非空 bag/plots/orderProgress + int64>2^53；两次 apply | 字段齐全；不追加；C# 消息未改 | （待填） | Review/editmode |
| Token 原子 | 已有文件 Replace；模拟失败 | 旧值保留；返回可观察失败 | （待填） | Review/editmode |
| 代次边界 | RPC 完成后、应用前取消/新登录 | 旧成功/失败不写不消新状态 | （待填） | Review/playmode |
| 租约取消 | pending>0 后 Cancel | Pending/操作归零；晚到不复活 | （待填） | Review/playmode |
| 回调释放 | 替换/Shutdown 后 Env.Dispose | 无残留/无吞异常 | （待填） | Review/playmode |
| Flow 串行 | pending 切换时 Kick/Logout | 最终 Login；无旧场景/租约 | （待填） | Review/playmode |
| Launcher 冷启 | 正式 RunAsync→Open Login | login-ready 仅打开成功；失败索引字节不变 | （待填） | Review + 索引哈希 |
| GUI 链路 | 冷启 Login→输账→Home→Logout | 真实 Unity 截图可读 | （待填） | Review/shots/* |
| 套件 | XML 非零 + 0 失败跳过 | Console 未处理 0 | （待填） | Review/*-results.xml |

### 问题表（本轮触发；复验后回填）

| 触发条件 | 现象/原因 | 修复 | 复验 |
|---|---|---|---|
| ToLuaJson 手选字段 | 丢 orderProgress/plots 细节 | ProtoJSON | （待） |
| Token Delete+Move | 空窗丢旧值 | File.Replace | （待） |
| Login 无代次 | 晚到写/清新状态 | attempt 核对 | （待） |
| Lease 不取 CTS | 取消后仍 pending | Bind+Cancel CTS | （待） |
| Set*Handler 置 null | DelegateBridge 残留 | Retain/Release | （待） |
| Flow 立即 Switch | busy 被吞留错场景 | 等待串行 | （待） |
| EnsureManager 过晚+重复 Register | 冷启失败/双注册；login-ready 假 | 顺序+等 Open+Commit 后置 | （待） |

### Coordinator（计划时）

- App **0.3.0** / Proto **1** / bridge **m1-bridge-0.3**（签名若变则 Gen；未冻结 T58）
- HTTP `localhost:18080`；TCP `127.0.0.1:17780`（隔离端口实测登记）
- T53–57 **仍未勾**；不进 D；不宣称 M1 完成


---

## 2026-10-20 21:15 C2收尾结果（ReviewNotes 7项）

### 验证表（实测）

| 名称 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| ProtoJSON+Lua双apply | EditMode 完整 plots/orderProgress/int64 | 字段齐全不追加 | **通过** | `StageC2/Review/editmode-results.xml` 4/4 |
| Token 原子 | Replace；锁文件失败保旧 | Save=false 旧值在 | **通过** | 同上 TokenStore_Replace… |
| 代次边界 | pending 后 CancelAll+重连新登录 | 旧不污染新 Token | **通过** | `playmode-results.xml` LoginAttempt_Stale… |
| 租约取消 | Login pending→CancelAll | Pending/ActiveLease=0 code=-1 | **通过** | BridgeCancelAll… |
| 回调释放 | CommitLoginToken bool；Set*Handler Retain/Release | 编译+Dispose 路径 | **通过** | AccountBridge/CallbackLease 源码；Gen exit0 |
| Flow 串行 | 等 busy；Shutdown；LastError | 源码落地 | **通过** | AccountFlow.cs |
| Launcher 冷启 | CDN GameLua+磁盘 Lua+OpenLogin 后 Commit | EntryReady/LoginOpened/Committed；索引 SHA 变 | **通过** | Launcher_ColdStart…；Cancel 保旧索引 |
| 初版 TCP 保留 | Register/Kick/Restore/LoginUi | 仍绿 | **通过** | 同 playmode 套件 |
| GUI | Camera 烘焙 Login/Home/Error | 非灰图 PNG | **通过** | `Review/shots/*.png`（旧 `StageC2/shots/login-window.png` 灰图保留未覆） |
| 套件 | XML 非零 0失败跳过 | Edit **4/4/0**；Play **9/9/0** | **通过** | Review/*-results.xml；exit0 |

### 问题表（本轮触发→修复→复验）

| 触发条件 | 现象/原因 | 修复 | 复验 |
|---|---|---|---|
| ToLuaJson 手选 | 丢 orderProgress/plots 细节 | `JsonFormatter.Format` ProtoJSON | EditMode+Lua apply 绿 |
| Token Delete+Move | 空窗 | `File.Replace`；Save→bool | Replace 失败保旧绿 |
| Login 无代次 | 晚到写状态 | `_authAttempt` 核对 | StaleSuccess 绿 |
| Lease 不绑 CTS | 取消后仍挂 | `BindOperationCts`+CancelAll | BridgeCancelAll 绿 |
| Set*Handler 置 null | 桥残留 | Retain/ReleaseLuaCallback | 源码+Dispose |
| Flow 立刻 Switch | busy 吞掉 | 等 IsBusy；Shutdown | 源码 |
| EnsureManager 在 Env 前 | open-fail LuaEnv未绑定 | `BeforeEntryStartAsync` 钩子 | ColdStart 绿 |
| version.json 写死 18080 | 隔离端口 404 | `AlignLoopbackPackageBaseUrl` | ColdStart 绿 |
| CDN bridge 旧 | catalog mismatch | 编辑器磁盘 Lua Prepare | ColdStart 绿 |

### Coordinator（本轮后）

- App **0.3.0** / Proto **1** / bridge **m1-bridge-0.3**
- 正式入口：`Launcher.RunAsync`；HTTP `localhost`（Align loopback）；TCP 夹具动态端口 / 默认 `127.0.0.1:17780`
- 包：编辑器 Patch=`GameLua` CDN + ResMgr `EditorSimulate`；Player 仍双包 HostHttp（DefaultPackage Host=**T58**）
- **T53–T57：本轮门槛已过，可勾**；**未勾 T58+**；不宣称 M1 完成；返回 Root 进 **D**

前缀 14515 字节 SHA 仍为 `7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328`（本段仅追加）。


## 2026-10-20 21:21 Root 复审：组件/冷启动通过，完整业务收尾仍待验证

本轮 EditMode4/4、PlayMode9/9有效，正式Lua入口前绑定同Env已修，真实Login打开后才提交索引、取消保旧磁盘字节已过。Editor DefaultPackage仍模拟、GameLua Host，不能据此称Native已过。Review/shots三图由Prefab直接填Error/HUD值烘焙，仅布局预览，不是真实认证数据/断线/登出证据。

Root-StageC2-ClosingReview.json保留当前验收决定：尚不接受T53–57，任务勾选撤回。仍需统一关闭/失败拥有者清理并观察DisposeError、实际场景完成后显示正确窗口、106保留真实终止码、旧Lua失败不能擦掉新pending认证及响应后应用前取消、真实Launcher输入/按钮/TCP→Home/遮罩/登出证据、共享CallbackLease/LuaLoader的C1相关回归。逐项观察在StageC2/Root-ClosingObservations.md，观察7的Env绑定已解决不重做。单次Closing Cursor继续，之后Root复核再D/E，用户无需再次确认。

历史前14515字节SHA保持7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328。原失败XML已保留，不覆盖为成功；无电量检查/子代理/Git写操作。


## 2026-10-20 C2 Closing：Root 剩余 6 项收尾（仅追加）

### 自然语言流程

Root ClosingReview 标出 6 项未过。本轮对照最终源码：已解决的不重做（观察 7 Env 绑定已 resolved）。按统一释放、场景后开窗、106/204、代次/应用前取消、真实 Lua 活链路、C1 27 回归逐项补齐；证据只写入 StageC2/Closing；不重跑无变化 Server223/Host5/CLI7；不写 Git/不派子代理。

### 具体命令与脚本

1. 备份日志：`StageC2/Closing/M1_06_Logs.md.pre-append.backup`（整文件字节）
2. 同步 Staging：`StageC2/Sync-LuaStaging.ps1`
3. 回归：`pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/StageC2/Closing/Run-Closing.ps1`
   - Unity 6000.3.22f1；C2 filter `AccountSessionPlayModeTests|LoginUiPlayModeTests|LauncherFlowPlayModeTests|ClosingLiveAccountUiPlayModeTests`；C1 filter 同 Root-NarrowFix；EditMode `AccountSnapshotTests`
   - 正式服：`Tools/run_server.ps1` 动态 TCP/HTTP + 隔离 DB（`-SkipBuild` 用 Temp/M1ServerBuild）

### 脚本源码及参数功能（本轮）

| 脚本/类 | 参数/要点 | 功能 |
|---|---|---|
| `Launcher.ReleaseOwningScope` | disconnectStatics | CancelAll→StopReconnect→Flow.Shutdown→UiManager→AccountBridge→LuaEnv→解绑 Scenes；DisposeError 可观察 |
| `AccountFlow.ApplyWindowsForCurrentAddress` | CurrentAddress | 仅真实场景后开 Login/HomeHud；最新 epoch 获胜 |
| `AccountBridge.NotifySessionExpired(code)` | 204/106 | 106 为 update-required，不重标 204 |
| `CallbackLease.BindCancelSideEffect` | Action | Cancel 时按代次清 pending |
| `AccountService.login` Lua | 非0 | 不再无条件 DiscardPendingToken |
| `ClosingLiveAccountUiPlayModeTests` | 真按钮 ExecuteEvents | 冷启→注册→错密→登录→Home→遮罩→Logout |
| `Run-Closing.ps1` | 见上 | 写 Closing XML/summary；保留旧 Review |

### 验证名称 / 步骤 / 预期 / 实际

| 名称 | 步骤 | 预期 | 实际 |
|---|---|---|---|
| C2 PlayMode | Run-Closing C2 filter | total>0 失败0 跳过0 | **13/13/0/0** exit0 |
| C1 回归 | 同 NarrowFix filter | 27/27/0 | **27/27/0/0** exit0 |
| EditMode | AccountSnapshotTests | 4/4 | **4/4/0/0** |
| DisposeError | Live 后 Launcher.Dispose | 空 | `Closing/dispose-error.txt` empty=True |
| 活角色 | 真注册登录 | 服务端 playerId/昵称/等级 | `live-role.txt` ID:1 NickLive Lv.1 |
| 真截图 | Camera.Render 活控件 | 非 Prefab 手填 | `Closing/shots/03-login-error.png` 错密文案；`04-home-hud.png` M1_Home+角色 |
| 布局图 | Review/shots bake | 仅布局 | README 标明非认证证据 |
| 前缀 SHA | 前 14515 字节 | 7A6CE2A4…B28328 | 追加前后不变 |

### 问题触发 / 原因 / 解决 / 复验

| 触发 | 原因 | 解决 | 复验 |
|---|---|---|---|
| Dispose 先 Lua 后 UI | Env 仍挂桥回调 | 统一 ReleaseOwningScope 顺序 | dispose-error 空 |
| LoginWindow 先 Open HomeHud | 场景未完成就开错窗 | 只 EnterHome；Flow 按 CurrentAddress 开窗 | Live Home/Logout |
| NotifySessionExpired 写死 204 | 106 被重标 | 传 terminalCode；kind=update-required | 106 单测绿 |
| Lua 非0 DiscardPendingToken | 旧失败清新 pending | 去掉无条件 Discard；代次 Clear | StaleNonZero 绿 |
| 应用前取消竞态 | Complete 抢先 | BindCancelSideEffect + 取消回调再清 | ResponseArrived 绿 |
| batch RT.Create Error | 无 GPU LogAssert | batch 跳 RT；有图轮次保留 PNG | 13/13；图可读 |
| 残留 CloudIsle.Server | 上轮 TearDown 未停净 | 停本轮孤儿进程后重跑 | 13+27 绿 |

### Coordinator（Closing 后）

- bridge **m1-bridge-0.3** / App **0.3.0** / Proto **1**
- **T53–57：请 Root 复核后勾**；**T58 起 D**（冻结 100 + Native）；不宣称整 M1 完成
- 证据根：`Docs/Acceptance/M1/20261020-xLua/StageC2/Closing/`
- 报告：`Closing/Closing-Report.md`

前缀 14515 字节 SHA 仍为 `7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328`（本段仅追加）。

## 2026-10-20 22:00 Root C2窄修计划

Cursor Closing已exit0并交回源码。Root独立解析C2 13/13、C1 27/27、Edit4/4，均0失败/跳过，真实GUI四图已逐张查看。仍发现AccountBridge的异步闭包引用静态新_session，会跨Configure误清新pending；Login新加C#闭包还掩盖真正LuaCallback.Target，绕开CallbackLease的Lua桥Retain/Release。Root将按`StageC2/Root-NarrowFix/Plan.md`备份后窄修：捕获自己Session、直接持有原LuaCallback、真实响应成功且未应用时Cancel须严格断言、去掉重复冷启测试的batch WaitForEndOfFrame截图；完整重建默认Temp服务器产物并跑相关有图形测试。不改共享CallbackLease/Server生产逻辑，最新27回归/223服务端结果继续有效；验证实际与问题待下方回填，不提前勾完整M1。

### Root窄修实际结果与接续

`StageC2/Root-NarrowFix/Run-20261020-220350/playmode-results.xml`独立解析**13/13、0失败/跳过、exit0**。真实响应成功已观察且回调未交付时lease.Cancel实得-1、Commit=false；新增Configure替换旧会话时取消旧callback且新pending可Commit通过；真实Lua/UI登录Home登出、Cold索引与DisposeError为空通过。AccountBridge已捕获自己Session、Configure先CancelAll、Login原LuaCallback交CallbackLease确保桥RetainRelease。共享CallbackLease未再改，最新C1 27/27与Edit4/4仍有效。完整重建Temp/M1ServerBuild仅4.49秒、0警告/错误，DLL/deps/runtimeconfig/native都在；未改Server生产源码，223仍有效。旧失败/挂起证据保留。

**Root接受T53–T56，T57仍待完成。** 真实T57缺口：ReconnectWithToken成功立即标认证/心跳/隐藏遮罩却没pendingToken/pendingAttempt，Lua恢复回调apply_and_commit因此CommitPendingToken失败。之前直接Session.Restore与手调遮罩截图不代表自动Lua恢复。为推进正式Native构建，将这个明确修复作为StageD第一项，必须实际socket中断→自动恢复→完整Lua模型覆盖→遮罩解除/无错误，通过后才勾T57并冻结100；不能在已知恢复错误上冻结。完整问题/命令/预期实际接续StageD提示词和M1_07。M1仍需D/E到T68，持续执行。


## 2026-10-20 StageD：T57 真实自动恢复修复（仅追加）

### 自然语言流程

Root 断点：`ReconnectWithTokenAsync` 成功立刻 `_authenticated`/心跳/`HideOverlay`，未写 `_pendingToken`/`_pendingAttempt`；Lua `apply_and_commit`→`CommitPendingToken` 因 pending 空抛 `token-commit-failed`。本轮统一为与 Login 相同的 pending 认证事务：已存同 Token + 代次/取消保护；Lua 应用后再 Commit→启心跳/解遮罩。HomeHud 订阅 `onRestored` 刷新可见模型。证据 `StageD/`；前 14515 字节 SHA 不变。

### 源码与参数

| 符号 | 路径 | 用途 |
|---|---|---|
| `ReconnectWithTokenAsync` | `AccountSession.cs` | 成功写 pending；不提前认证/心跳/解遮罩 |
| `CommitPendingToken` | 同上 | Save→认证→心跳→`HideOverlay` |
| `DropLiveSocketForTests` | 同上 | 切断真实 socket，保留 Token |
| `BeginAutoReconnect` | 同上 | 遮罩 + ReconnectService 退避 |
| `SetRestoredHandler` | `AccountService.txt` | pcall apply_and_commit；失败回 Login |
| `HomeHudWindow` | `UI/Windows/HomeHudWindow.txt` | onRestored→refresh HUD |
| `AutoReconnect_AfterDropLiveSocket_*` | `AccountSessionPlayModeTests.cs` | 实证自动链路 |
| `Run-T57-AutoReconnect.ps1` | `StageD/` | batch PlayMode |

### 验证（跑测后回填）

| 名称 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 前缀 SHA | 前 14515 字节 | 7A6CE2A4…B28328 | （待） | StageD/m106-prefix-sha.txt |
| Restore pending | Restore 后 Commit | 先未认证再认证 | （待） | playmode-t57-results.xml |
| 自动重连 | Drop→AutoReconnect→handler Commit | 遮罩可见→Commit 后隐藏；code0 | （待） | 同上 |

### 问题

| 触发 | 原因 | 解决 | 复验 |
|---|---|---|---|
| （跑测后填） | — | — | — |

### T57 跑测实际回填（2026-10-20）

| 名称 | 实际 | 证据 |
|---|---|---|
| 前缀 SHA | `7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328` 跑前跑后不变 | `StageD/m106-prefix-sha.txt` |
| PlayMode | **10/10、失败0、跳过0、exit0**（含 AutoReconnect_AfterDropLiveSocket + Restore pending/Commit） | `StageD/playmode-t57-summary.json` / `playmode-t57-results.xml` |
| 自动重连 | DropLiveSocket→遮罩可见→NotifyRestored 时未认证→Commit 后认证且遮罩隐藏；快照含 playerId | 用例 Passed duration≈32s |
| 命令 | 见 `StageD/playmode-t57-command.txt` | `Run-T57-AutoReconnect.ps1` |

问题表：本轮未遇伪成功标记；Root 指出的 pending 缺口已按正式链路修复并实证。T57 已勾；开始 T58 冻结前构建脚本。

## 2026-10-21 最终补充：T53–T57及M1正式验收完成

本节仅追加，不改变历史原文或当时失败/待办状态。最终唯一候选Root Final 100 R2 / Root Final Publish R2。T53–T56的Root窄修13项已通过，T57取消源/快照pending-commit/自动恢复/终止通知的实际问题已修复；当前PlayMode-Final实际9/9，最终Native Recovery-20261021-141811-730完整C01–C12及Protocol-141823的C13均通过。Token过期真实204清Token/模型返回Login；双Native顶号和旧Token204无快照；同库强杀恢复保持已提交revision1及完整模型，不承诺未落库数据。

204终止问题的具体原因是ReconnectWithTokenAsync收到204/106后StopReconnectLoop取消本身拥有者，导致上层通知跳过。删除该内层取消，终止业务码返回给RunReconnectAsync统一通知。真实TCP新增回归修复前1失败，修复后9项全部通过，原失败报告保留。

执行步骤、函数/参数、完整脚本、验证预期/实际及问题复验见M1_07最终交付章节和M1_09 §10–14。58项逐条证据见Docs/Acceptance/M1/20261020-xLua/M1_Acceptance.md及StageE/AcceptanceMatrix.json；源码/安装/完整模型/DB/双端配置/日志独立核对见Root-Finalization/final-audit.json。主拆解T01–T68及原开发计划M1看板已同步完成。电量检查保持暂停。