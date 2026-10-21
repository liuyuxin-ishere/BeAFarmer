<!-- XLUA-MIGRATION:BEGIN -->
> **迁移说明（2026-10-19）：本文件保留历史原文。**
> 当前热更架构调整为 xLua；旧记录中的技术名称和“已通过”仅描述当时环境，不能解释为 xLua 已验证。
> 原任务步骤、源码、命令、问题和实测结果完整保留；服务端、协议、资源与公共 C# 能力可按新边界复用，旧热更 DLL／补元数据链的完成记录不构成 xLua 验收。旧自动推进在 T42 部分实现阶段停止，T42 尚未验收；后续按新方案重新安排。
> 当前设计、验证范围和证据见 [xLua 迁移方案与 PoC](../07_xLua迁移方案与PoC.md)。
<!-- XLUA-MIGRATION:END -->

# M1 开发日志 · 5.7 构建与验收交付

记录方式对齐 `06_开发计划.md` 第 6.1 节：每项写成可勾选的文档、命令或方法。目录索引见 `M1_Logs.md`。

本文件对应 `06_M1_基础框架任务拆解.md` 第 5.7 节，任务 T58–T68。

本段尚无已写入的任务记录。

## 2026-10-20 17:12 T60/T61独立前置与实际门槛

本轮实现计划先记录在[M1_08_ServerHost_Logs.md](M1_08_ServerHost_Logs.md)，包含自然语言流程、最小步骤、具体可执行命令、完整源码/参数函数注释链接、验证预期/实际及问题原因修复复验。T60/T61不依赖尚未完成的客户端窗口；Root在Cursor C1只写客户端时独立实施Server，使用隔离artifacts/测试DB。

| 任务 | 交付与实际结果 | 证据/限制 |
|---|---|---|
| T60 | 正式M1ServerHost+Program明确--m1+Tools/run_server.ps1；HTTP/TCP/账号/令牌/保存/指标共用正式类。真实脚本7场景通过：build/空格路径/正常停机exit0；TCP/HTTP占用、错配置、非法DB、缺内容、无效端口均exit1，原因核对。中文UTF8问题已修复复跑 | `Docs/Acceptance/M1/20261020-xLua/ServerHost/CLI Validation 20261020-170049/results.json`；最终100/101发布目录仍等T58/T59 |
| T61 | 最终Host/CLI接入后程序集，完整CloudIsle.Tests不设过滤；Root独立读TRX：221执行/221通过/失败0/跳过0，exit0；另Host针对性5/5复验 | `ServerHost/TestResults/t61-server-full.trx`、`host-final-tests.trx`；后续D核对具体覆盖，若真改Server业务语义须相应复验 |

本记录只补T60/T61已有实跑，不勾T58/T59/T62–68，不代表客户端账号/完整M1或四项最终验收通过。D阶段读取[ServerHost交接说明](../../../Docs/Acceptance/M1/20261020-xLua/ServerHost/Ownership.md)后复用已有Host与脚本，避免重复实现。

### 19:52 T61最新全套计数

A11/E11明确命名PlayerPersistenceTests已补2项真实端到端夹具（见M1_08最新节/完整源码），正向修改保存重开与SQLite实际只读失败/Dirty保持/恢复重试均通过。最终无过滤全套`ServerHost/TestResults/t61-server-final223.trx`为 **223/223、失败0/跳过0、exit0**，Root独立解析。生产Server未改；此前221/5/7证据保留，最新以223为准。

## 2026-10-20 T63独立配置源准备（Root，尚未构建/发布101）

### 任务介绍

在C2账户流程由唯一Cursor收尾时，Root准备互不依赖的101配置工作簿。实施前具体计划已写入`Docs/Acceptance/M1/20261020-xLua/Config101Preview/Prepare101-Plan.md`。只在该目录导出，不修改Shared当前100数据源/Unity代码/资产，不竞争Cursor拥有权。将原Global KV表的第25行格式延续到新第26行，增加`m1_patch_message`=`热更新验证成功`；保留原字符串类型、所有旧值、公式、样式与原生设置，再交D用于完整版本化101输入及真实Luban导表。

### 具体步骤与命令

1. [x] 使用已说明的spreadsheets技能与Artifact Tool导入`Shared/config/Datas/global_kv.xlsx`，实际inspect/render；Root已查看`GlobalKv/before.png`。实际sheet=`global_kv`、A1:C25，原53个非空字符串/22个KV，公式0。原SHA=`a7c2c848d5c7a4fb5809bf8512942e34733efa306b3209eb1e9e55ad35ea0851`。
2. [x] 首次作者命令前成功执行marker恰好一次，退出0，证据`authoring-marker.json`；后续重跑不重复marker。
3. [x] 完整作者脚本`Docs/Acceptance/M1/20261020-xLua/Config101Preview/prepare-101.mjs`：参数2原xlsx、参数3独立输出目录；校验源SHA与末行，`copyFrom(A25:C25,'all')`→A26:C26；只写B26/C26，新增行wrapText/rowHeight36；`recalculate`后检查旧值/公式，inspect/render并export，重新导入逐值/公式确认。完整源码含功能/参数注释，非手改导表JSON。
4. [x] `preserve-native-margins.py`只恢复SDK不保留的打印pageMargins XML，保留目标工作表XML命名空间；所有其他ZIP部分逐字节不变，不使用openpyxl保存。参数原源和独立目标，路径限定Export101，不改源。`check-native.py`用openpyxl3.0.10**只读**独立比值/公式/类型/font/fill/border/alignment/number_format/protection、sheet顺序、验证/合并/冻结/尺寸/打印设置/图表原生特性等，752项一致。
5. [x] Root查看`Export101/after.png`，原表布局延续，新第26行键和值完整换行可读。源SHA未变，最终输出`Export101/global_kv.xlsx` SHA=`bf132a49c842c410900d4329d511710791907591f9961d259b83e5d9666c9944`。
6. [ ] D将此已验证xlsx复制到完整版本化Config101的Datas，与100其他数据/Defines/conf共同建立输入清单，真实`gen_config.ps1 -ConfigVersion101`；之后101 Lua/Prefab/配置打包、同100安装Native热更与三哈希校验。**不因工作簿准备勾T63。**

```powershell
# 以下node为已核实支持的缓存运行时；node_modules Junction仅只读引用受支持缓存。
$node='C:/Users/Lenovo/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'
$base='Docs/Acceptance/M1/20261020-xLua/Config101Preview'
# marker只执行过一次，不在重跑builder时重复执行：
# & $node '<skill>/container_tools/mark_artifact_operation_started.mjs' --operation-kind edit --expected-output-count 1 --output-format xlsx
& $node "$base/prepare-101.mjs" 'Shared/config/Datas/global_kv.xlsx' "$base/Export101"
python "$base/check-native.py" 'Shared/config/Datas/global_kv.xlsx' "$base/Export101/global_kv.xlsx" "$base/Export101/native-structure-check.json"
```

### 验证步骤、预期与实际

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| 100源完整性 | 作者前后SHA256并独立只读再比 | 源不改 | 通过，a7c2…0851保持 |
| 旧值/类型/公式 | SDK再导入+独立只读比原A1:C25 | 原53非空字符串和原公式一致 | 通过 |
| 新键唯一 | B26/C26精确值、总行26、原旧键无此键 | 仅新增允许的字符串KV | 通过 |
| 样式与原生设置 | 独立752项比对，ZIP页边距保留复验 | 无旧样式/设置丢失 | 最终752/752，差异0 |
| 真实表格预览 | Root view实际ArtifactTool before/after图 | 新行可读、旧结构一致 | 通过；原列宽较窄的既有裁切保持，新行局部换行 |
| 导表/资源/Native | D/E后续真实Luban/101发布/同安装验证 | 新Global生效且安装不变 | 尚未执行，不记通过 |

### 遇到的问题和解决方法

| 情况与问题 | 具体原因 | 解决方法 | 复验 |
|---|---|---|---|
| 首次SDK导出后打印页边距不同 | Artifact Tool导出默认0.7/0.75/0.3，源为0.75/1/0.5；两次有界公开help未提供打印边距API | 作者仍只用Artifact Tool，之后ZIP级复制源pageMargins属性，保持其他ZIP部分不变 | 独立page_margins相等，752项全过；native-margin-preservation.json |
| 首次条件格式比对报差异 | openpyxl空ConditionalFormattingList对象按身份比较，并非实际有规则丢失 | 只读比较实际sqref/rules列表；源与目标均空 | 通过，不改表内容 |
| 原生保留首次没匹配目标pageMargins | SDK用x:命名空间前缀、源用默认命名空间 | 匹配可选前缀并保留目标前缀，ET再验证标准SpreadsheetML命名空间与属性 | 通过，未影响其他ZIP部分 |

最初独立差异报告保留为`first-native-structure-differences.json`，最终报告为`native-structure-check.json`。`result.json`仅证明工作簿准备，不冒称Luban/Native已验。Marker已执行一次，不再执行。原ReferenceDoc工作簿没有修改。

## 2026-10-20 StageD 本轮计划：T57 先修自动恢复 → T58–T63（Cursor）

### 自然语言任务介绍

按 Root-StageC2-final-review：T53–T56 已接受，**T57 未勾**（Reconnect 成功未写 pending，Lua Commit 失败）。本轮单实例先修真实自动恢复并实证，再冻结 100 并完成 T58–T63。新证据仅 `Docs/Acceptance/M1/20261020-xLua/StageD/`。T57 修复另只追加 M1_06，保护前 14515 字节 SHA=`7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328`。无子代理、无电量检查、无 Git 写。AC-1～4 / E 组留下一调用。

### 复用与流程

1. 复用 C2 `AccountSession`/`AccountBridge`/`AccountService`/`ReconnectService`、正式 `Tools/run_server.ps1`、Temp/M1ServerBuild、Root 窄修 Retain/Configure 捕获。
2. T57：Reconnect 成功统一为 pending 认证事务（同已存 Token+代次）；Lua apply 后再 Commit→启心跳/解遮罩；204/106/Kick 停重试回 Login。
3. T58–T59：`build_client_win.ps1` / `build_patch.ps1` + baseline；App **0.3.0** / Proto **1** / bridge **m1-bridge-0.3**。
4. T60/T61：复用 Root Host/CLI/223 证据核对勾选，不重写。
5. T62：UIResourceTests 真实 Lua+Yoo。
6. T63：复制已验证 Config101Preview xlsx→版本化输入→真实 Luban；新增 Lua/Prefab/Global；不重复 marker。

### 最小执行清单

| 步骤 | 命令/路径 | 用途 |
|---|---|---|
| 修 T57 | `Assets/Framework/Account/AccountSession.cs`；`Assets/Lua/AccountService.txt`；`HomeHudWindow.txt` | pending 事务 + HUD 刷新 |
| 跑 T57 | `pwsh -File Docs/.../StageD/Run-T57-AutoReconnect.ps1` | 真实 DropSocket→AutoReconnect |
| T58 | `Tools/build_client_win.ps1`（本轮实现） | 冻结 100 + baseline |
| T59 | `Tools/build_patch.ps1` | 独立 101；AOT 拒绝 |
| T60/61 | 引用 ServerHost 证据 | 勾选 |
| T62 | PlayMode UIResourceTests | XML |
| T63 | Config101 输入 + Luban + 内容 | 三者新增 |

### 验证表（本轮开始为空，通过后回填实际）

| 名称 | 具体步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| T57 自动恢复 | DropLiveSocket→BeginAutoReconnect→Commit | pending 后认证；遮罩后解；HUD 覆盖 | （待跑） | StageD/playmode-t57-* |
| M1_06 前缀 | 跑前跑后 SHA | 7A6CE2A4…B28328 | （待跑） | StageD/m106-prefix-sha.txt |
| T58 基包 | build_client_win -ResVersion100 | exit0；manifest；IL2CPP | （待） | StageD/ |
| T59 补丁 | build_patch→101；AOT 负例 | 独立目录；差异非0 | （待） | StageD/ |
| T60 | 核对 CLI 7 场景 + 发布目录 | 可复用勾选 | （待） | ServerHost/ |
| T61 | 核对 223/223 trx | 0 失败勾选 | （待） | t61-server-final223.trx |
| T62 | ≥2 PlayMode | 资源回基线 | （待） | StageD/ |
| T63 | Luban101+Prefab+Lua+KV | 100无/101有；安装哈希不变 | （待） | StageD/ |

### 问题表（开始为空）

| 触发 | 原因 | 解决 | 复验 |
|---|---|---|---|
| （无） | — | — | — |

不提前勾 M1；T57 通过前不冻结 100。

## 2026-10-21 Root未接受旧D：阻断修复→最终100→StageE T64–T68（D2）

### 自然语言任务介绍

Root 实读最终源码后**未接受**旧轮「T57–T63 全部通过」。本轮只修 StageD-RootClosure-ToE 列出的 8 项实际门槛：真实 LoginWindow/Lua apply_and_commit 驱动（替换 C# Commit bypass）、正式 Boot 串接 PatchProgressView、creds.private 私有凭证、protoc 强制 gen、结构化 baseline-fingerprint、已发布 101 不可变+原子切指针、100 排除 PatchDemo Prefab、Pump 异常可观察。修好后空格路径完整构建最终 100、Native UI 登录/断线恢复，再直接执行 StageE-T64-T68 全部 58 项。单 Cursor、无子代理/Git/杀用户进程；M1_06 仅追加且前 14515 字节 SHA 保持 7A6CE…B28328。

### 具体实施与命令清单

| 步骤 | 命令/路径 | 用途 |
|---|---|---|
| 修驱动 | `LauncherBootBehaviour.AcceptUiLoginAsync` + `creds.private`；`Run-NativeAccept.ps1` | 真实 UI→Lua apply；CLI 无密文 |
| PatchUI | `Launcher.PreparePatchWithUiAsync` | 进度/失败/重试串行 |
| 指纹/不可变 | `ClientBuild.WriteBaselineFingerprint`；`PatchBuild` 重写 | 结构化指纹；101 不可覆盖 |
| PatchDemo | `Assets/Res/UI_Patch101`；100 校验无、101 staging 短暂拷入 | 正式 100 不混入 |
| 构建 100 | `Tools/build_client_win.ps1` → `StageD/Freeze 100 Final` | 空格路径 IL2CPP+fingerprint |
| Native | `StageD/Run-NativeAccept.ps1` | UI login+drop+nickMatch |
| Config101 | 复制 bf132…9944 → 版本化输入 + Luban101 | 不 marker |
| StageE | `StageE-T64-T68.md` / AcceptanceMatrix 58 | 全真实证据 |

### 验证表（执行前为空，通过后回填）

| 名称 | 具体步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| T57 UI/Lua Native | Run-NativeAccept；observe login-ok/mask/restore nickMatch | 非 hasPlayerId 布尔；biz 快照脱敏 | （待） | StageD/NativeAccept/ |
| T58 空格100 | build_client_win Output=`Freeze 100 Final` | exit0；fingerprint；三SHA | （待） | StageD/Freeze 100 Final/ |
| T59 不可变+负例 | build_patch；改 AccountSession 拒绝；恢复 | 原目录 bytes+指针不变 | （待） | StageD/Publish*/ |
| T60/61 | 引用 CLI7 / 223 trx | 可复用 | （待） | ServerHost/ |
| T62 | UIResource PlayMode | ≥2 pass | （待） | StageD/ |
| T63 | 100无/101有 PatchDemo+Global | manifest/address/cfg | （待） | StageD/ |
| StageE 58 | AcceptanceMatrix 逐条 | 全 PASS 或诚实阻塞 | （待） | Acceptance/ |

### 问题表（开始为空）

| 触发 | 原因 | 解决 | 复验 |
|---|---|---|---|
| （无） | — | — | — |

Root 未接受旧 D 原因摘要：AcceptLoginAsync C# bypass；Launcher 无 PatchUI；CLI 明文凭证；protoc 跳过；ValidateBaseline json.Contains+缺键跳过；101 Move 空窗；PatchDemo 仍可能进 100；Pump 空 catch。本轮以修复实证为准，不保留旧「全部通过」声明。

## 2026-10-21 10:12 集中修复检查点（四门槛→Verified100/101）

### 自然语言任务介绍

Root 结束 m1closure 并要求**先关闭四门槛再交复核**，不扩大填 58。范围：①普通模式 Patch 失败 UI 等待真实按钮（禁止默认 2 帧自动点/耗尽销毁）；②accept-root 本地命令驱动（注册/登录/登出/重试/开 PatchDemo/ScreenCapture/脱敏 state）；③ CompilationPipeline 完整指纹+native/lock，删旧 fallback；④新根 Verified 100 + Verified Publish 101，catalog 真含 UI/Windows/PatchDemoWindow，同安装 100→101 Native。单构建收口。

### 具体实施与命令清单

| 步骤 | 路径/命令 | 用途 |
|---|---|---|
| PatchUI | `Launcher.PreparePatchWithUiAsync` | 正常等按钮；accept 仅命令触发点击 |
| Accept 驱动 | `LauncherBootBehaviour` commands/ + ScreenCapture | A/B 所需逐步驱动 |
| FileLogger | Launcher 接入 `FileLogger` | E10 滚动脱敏日志 |
| 指纹 | `ClientBuild`/`PatchBuild` 公共收集器 | Player 源/插件/lock |
| 构建100 | `build_client_win` → `StageD/Verified 100` | 空格路径完整 IL2CPP |
| 构建101 | Luban101 + `build_patch` → `StageD/Verified Publish` | 新根不可覆盖坏101 |
| Native | 同 accept-root：100→切指针101→开面板 | 真截图+三SHA |

### 验证表（执行前空，通过后填）

| 名称 | 步骤 | 预期 | 实际 | 证据 |
|---|---|---|---|---|
| 普通重试 | 坏资源后观 UI | 错误保持；仅按钮续 | （待） | Verified/ |
| Accept 命令 | register/login/logout/retry/open/shot | 各一次 ack+state | （待） | accept-root/ |
| 指纹负例 | 改非旧四文件 C#/插件 fixture | 拒绝；Player三SHA不变 | （待） | StageD/FpNeg/ |
| catalog101 | 构建后读 modules | 含 UI/Windows/PatchDemoWindow | （待） | Verified Publish/ |
| 100→101 | 同 cache 指针切换 | Home→PatchDemo+Global；三SHA同 | （待） | shots/ |

### 问题表（开始为空）

| 触发 | 原因 | 解决 | 复验 |
|---|---|---|---|
| （无） | — | — | — |

## 2026-10-21 最终交付：T57–T68与58项验收全部通过（R2）

### 任务介绍

本轮以同一个最终Windows x64标准IL2CPP基包及其101补丁完成M1，不把旧迁移小演示、组件布尔值或CLI退出0当作完整业务通过。先写计划，再修复已复现的取消/会话清理/构建索引/驱动问题，保留失败，重新冻结R2并实际运行账号、热更新和恢复流程。最终逐项检查58项证据并回填本节。原日志中的待执行/失败描述保留为当时记录，以本节和M1_Acceptance.md为最终状态。

### 已执行步骤清单

- [x] T57：移除ReconnectWithTokenAsync在204/106返回前的自我取消；真实TCP回归修复前失败、后9项通过；R2 Native过期/顶号/106完整通过。
- [x] T58：Build-FinalCandidate.ps1调用正式build_client_win，执行gen_proto、Luban100、GenAll、Yoo、Win64 IL2CPP。Root Final 100 R2带空格输出；3342文件完整指纹，exit0。
- [x] T59：真实导101、正式build_patch，在Root Final Publish R2生成独立101；固定C#差异真实负例exit1且源码原字节恢复，安装与发布指针不变。既有native/import设置差异和不可变发布负例继续适用。
- [x] T60：正式run_server组合TCP/HTTP/DB/账号/保存/日志指标；修Host重复解绑与连接观察全局升级；两个不同账号并存RST后另一账号/HTTP/新TCP仍存活。原CLI端口/参数失败证据保持，225项最新测试通过。
- [x] T61：当前服务端225/225、失败0、跳过0，含保存、鉴权、幂等、只读失败后恢复。
- [x] T62：UIResource两个当前PlayMode用例通过；开关/取消后资源回基线。
- [x] T63：实际100没有新模块/Prefab/Global，101三者皆有；UI显示实际Global的热更新验证成功，无硬编码成功回退。
- [x] T64：真实注册未授权→登录→Home/S1→登出清Token/模型→再登录→退出重开→S1保持。
- [x] T65：同安装同缓存100→101→缓存101，实际下载42→5→0，三个安装文件SHA不变；坏CDN副本可见失败/不激活→恢复SHA→真实按钮重试成功。
- [x] T66：真实75秒以上退避1/2/4/8/16/30；原库恢复、拥有进程强杀同库恢复、SavedRevision1、过期204回Login、双Native顶号、旧Token204无快照、106更新引导均通过。
- [x] T67：正式成功/失败路径、当前225服务端+9受影响PlayMode+69基础EditMode+7输入模态均通过；复用未变更C1 27/C2 13证据。
- [x] T68：Finalize-Acceptance.py独立检查证据与冻结源码/安装/模型/DB/配置/私密值日志扫描后，58项逐条pass，M1_Acceptance.md生成，主计划与进度看板同步完成。

### 命令、参数与函数说明

以下命令的工程根为E:\Workbench\UnityWorkBench\Farming，实际命令记录和stdout/stderr/exit均在Root-Finalization目录。引用最终R2目录，旧候选仅诊断保留。

```powershell
# 完整100、真实Luban101、独立101补丁，最后把工作配置恢复100。
pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Build-FinalCandidate.ps1
# 账号与同缓存更新、损坏资源、完整恢复、版本不匹配分别保存独立运行目录。
pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-NativeAccountsAndPatch.ps1
pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-NativeBadResource.ps1
pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-NativeRecovery.ps1
pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-NativeProtocolMismatch.ps1
# 标准补丁脚本拒绝固定代码差异；finally原字节恢复；不覆盖旧报告。
pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-FinalAotNegative.ps1
# 当前基础现有测试；不新增镜像实现的断言。
pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-FinalFoundation.ps1
# 只核对实测产物/逐项汇总，不执行或替代测试。
python Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Finalize-Acceptance.py
```

| 参数或函数 | 功能与约束 |
|---|---|
| RepoRoot | 工程绝对根；所有相对路径从这里解析，测试写入限本工程拥有目录 |
| OutputDir / BaseBuildManifest | 唯一新候选输出与冻结基包清单；新候选存在即拒绝，避免覆盖历史 |
| ResVersion / ConfigVersion / AppVersion | 本轮资源配置100或101、安装0.3.0；Proto1及bridge0.3固定，101不能改变AOT |
| UnityEditorPath / ProtocPath | 显式或已定位真实工具；缺失早期非0，不能跳过生成 |
| DatabasePath / ContentRoot / ConfigDirectory | 隔离SQLite、完整资源发布根、严格版本ServerConfig100/101 |
| TcpPort / HttpPort / ProtocolVersion | 每次新分配测试端口；仅C13要求服务端Proto2以触发真实106 |
| StopFile / BuildArtifactsDirectory / SkipBuild | 拥有服务正常停机信号、稳定Release输出、验证已有产物后免重复构建 |
| Invoke-Recorded | 引号传参，隐藏后台辅助窗，保存独立stdout/stderr，WaitForExit后检查真实exit |
| Assert-InRoot / Quote-ProcessArg | 路径规范化后验证拥有范围；对已知无引号参数正确引用含空格路径 |
| New-NativeFixture | 新拥有RunDir/accept-root/私有随机凭证/DB/端口/进程句柄；凭证不进CLI |
| Write-AtomicUtf8 / Set-NativeVersion | 同目录临时文件写完后Replace/Move，版本指针最后切换，保留旧可用目录 |
| Get-NativeState / Wait-NativeCondition | 只接受本进程启动后的UTC新状态；超时明确失败，不默认把缺失字段当0 |
| Send-NativeCommand | 写唯一命令ID，等实际成功ack；点击回执后另等真实业务完成条件 |
| Start/Stop-NativePlayer | 独立缓存/Token/日志；重启去掉未完成旧命令；正常退出优先，强制退出只对精确exe与accept-root匹配的拥有进程 |
| Start/Stop-NativeServer | 正式run_server与独立DB/配置/日志/端口；等真实HTTP就绪，StopFile正常结束；C06另核对父PID和DB才强杀 |
| Save-NativeState / Save-Action | 必需字段检查、完整脱敏Lua模型、实际业务结果、UTC及阶段保存；不含密码或Token明文 |
| Get-InstallHashes | 实际exe、GameAssembly.dll、global-metadata.dat SHA256，逐项比较更新前后 |
| ProtocolFixture.prepare | 离线调用生产PlayerStore.Save并重新打开核对，产生revision1/gold201/bag1×7；无管理接口 |
| ProtocolFixture.reconnect | 读取私有旧Token，用真实FrameCodec/protobuf/TCP请求，断言204且无快照，不输出Token |
| ProtocolFixture.config | 正式ServerConfigService加载实际101并读取新Global，与Native值核对 |
| db_fixture.snapshot / expire | 只读公开业务与revision摘要；或只在隔离库把有效Token到期设0，报告数量与时间，不打印Token摘要/明文 |
| CleanupConnectionPart / ReportConnectionFailure | 单连接清理逐项执行；失败仅记sessionId/阶段/类型，诊断失败也不把该连接错误变成Host停机 |
| Input夹具SetUp/TearDown | 明确手动更新、后台焦点路由并恢复原设置/设备，保证前台和batch可重现，不改生产输入 |
| Finalize-Acceptance | 先检查225/9/69/7和复用27/13实际数量、失败跳过，随后3342源码SHA/安装3SHA/完整模型与DB/配置/日志私密值检查；全部满足才回填58项 |

### 验证名称、具体步骤、预期与实际

| 名称 | 具体步骤 | 预期结果 | 实际结果与证据 |
|---|---|---|---|
| 服务端完整测试 | 正式dotnet test Release，独立artifacts，解析TRX | 实际用例>0、失败/跳过0 | 225/225；ServerTests/root-server-final225.trx |
| 并存连接隔离 | 两不同账号登录，A的TCP RST，B心跳/HTTP/新TCP登录 | Host与B保持服务 | 通过，OnePeerAbruptExit测试 |
| 204终止通知 | 无效已存Token真实TCP触发恢复 | 通知一次、清Token/遮罩/循环 | 修复前1失败；修复后9/9；Expiry-Red与PlayMode-Final分别保留 |
| 最终账号更新 | 四次重开，同安装同缓存，打开实际101新Prefab | S1保持、三SHA不变、缓存复用 | Native-141638通过42→5→0，Root查看真实Home/新面板 |
| 坏资源重试 | CDN副本截断8字节→新缓存→恢复→真Retry | 失败可见且无坏提交，重试串行成功 | BadResource-141811通过，attempt1、2 |
| 原生恢复 | 完整退避、同库恢复、强杀、过期、顶号、旧Token | R2已提交数据恢复，失效/顶号停止抢回 | Recovery-141811 C01–C12全部通过，revision1 |
| 原生106 | Proto1客户端对Proto2服务端真实登录 | 明确更新提示，无无限重连 | Protocol-141823通过，Root查看UI |
| 固定代码负例 | 临时改AccountSession字节，正式build_patch | 拒绝、不发布、安装不变、源码恢复 | AotNegative-R2-141623 exit1，完整复验通过 |
| 基础回归 | 现有网络/OpId/事件/池/计时/日志集 | 失败/跳过0 | Foundation-Final 69/69 |
| 输入/模态 | 手动设备更新与焦点设置，真实InputSystem/EventSystem | 移动/点/拖/滚轮与模态均正确 | 首轮3/7，修夹具后Input-Fixed 7/7 |
| 最终独立核对 | 汇总器逐项读实测及3342源码，检查已知私密值日志 | 不缺证据、不以null冒充0、不泄露 | 58/58；19配置JSON一致；39日志/6已知私密值0匹配 |

### 遇到的问题、具体原因、解决与复验

| 触发情况/问题 | 具体原因 | 解决方式 | 实际复验 |
|---|---|---|---|
| Native启动只见空场景 | 根CTS误用无参数CreateLinkedTokenSource | 根作用域new CancellationTokenSource | 重新完整构建后进入真实Lua Login/Home |
| 101catalog缺新模块 | 插入只匹配LF，源为CRLF | 构建前规范化结构换行 | R2实际模块/新Prefab/Global与截图通过 |
| Native完整模型观察为{} | Json模块原只有decode，观察器调用不存在encode | 实现确定性plain-data encode，保留int64字符串 | S1/R1/R2所有完整模型比较及DB核对通过 |
| 截图没有Overlay或截到其它应用 | Camera.Render不含Overlay、桌面捕获受遮挡 | Unity完整ScreenCapture，等文件IEND再ack | Root查看实际完整Home/新面板/错误/遮罩/恢复/Login |
| 重启立刻退出 | 消费命令文件未删或quit先退出后回执 | 持久去重/消费删除/先ack后Quit，重启退役未完成旧命令 | 多次同accept-root重开通过 |
| 对端退出导致Host停止 | Host重复调用队列内解绑、连接watch错误全局升级 | Registry唯一解绑，Host逐项隔离清理与安全诊断 | 连续12次退出及并存RST、225项通过 |
| 204清Token但Home/Lua未清 | 内层取消自身CTS，上层终止通知未发 | 终止码正常返回，上层统一通知 | 红1失败→绿9通过，R2 Native C09实际通过 |
| 读取活跃Unity日志失败 | ReadAllLines未允许Unity持续写 | FileStream ReadWrite共享，finally Dispose | 完整75秒以上退避读取通过 |
| 批处理键鼠读0 | 默认Editor焦点/路由/自动更新使设备事件未给GameView | 夹具明确手动更新/IgnoreFocus/全部路由，结束恢复 | 原3/7报告保留，Input-Fixed 7/7，无Player改动 |
| 旧矩阵仍4pass/54pending | 分阶段证据未逐条汇总，状态落后于实现 | 逐条实际映射与汇总前硬检查 | 58项全部pass，主计划68项及看板同步 |

### 最终交付位置

- Docs/Acceptance/M1/20261020-xLua/M1_Acceptance.md：58项逐条实际与证据、三SHA、真实成功截图。
- StageE/AcceptanceMatrix.json：58项pass/0pending，包含每项实际结果和证据路径。
- Root-Finalization/final-audit.json：3342源码、安装、模型/DB、19配置JSON和实际私密值日志独立核对。
- 唯一最终安装Root Final 100 R2/Player/Farming.exe；补丁Root Final Publish R2/101。旧失败与候选不覆盖。

### 当前具体脚本内容快照

下列内容是最终实施文件的完整快照，代码内保留函数/参数注释；已有历史源码不重构。实际输入凭证是运行时随机生成的私有文件，不在本快照内。

#### Tools/build_client_win.ps1

```powershell
<#
.SYNOPSIS
  T58：gen_proto/gen_config、xLua GenAll、Yoo、标准 Win64 IL2CPP 基包与完整指纹。
#>
param(
    [string]$RepoRoot = 'E:\Workbench\UnityWorkBench\Farming',
    [string]$UnityEditorPath = 'D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe',
    [string]$OutputDir = '',
    [string]$AppVersion = '0.3.0',
    [int]$ResVersion = 100,
    [string]$ProtocPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepoRoot)
if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity不存在: $UnityEditorPath"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    # D01：真实带空格输出目录
    $OutputDir = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD\Freeze 100 Final'
}
$out = [IO.Path]::GetFullPath($OutputDir)
$evidence = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD'
New-Item -ItemType Directory -Path $out, $evidence -Force | Out-Null

function Resolve-Protoc {
    param([string]$Explicit)
    $list = @()
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) { $list += $Explicit }
    $list += @(
        (Join-Path $root 'Tools\protoc\bin\protoc.exe'),
        'C:\Program Files\Protobuf\bin\protoc.exe',
        'D:\Users\Lenovo\anaconda3\Library\bin\protoc.exe'
    )
    $cmd = Get-Command protoc -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { $list += $cmd.Source }
    foreach ($c in $list) {
        if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return [IO.Path]::GetFullPath($c) }
    }
    return $null
}

function Invoke-UnityPhase([string]$Phase, [string]$LogName) {
    $existing = Get-Process Unity -ErrorAction SilentlyContinue
    if ($existing) {
        throw ("已有Unity进程 PID=" + (($existing | ForEach-Object Id) -join ',') + "；禁止盲开第二实例")
    }
    $log = Join-Path $evidence $LogName
    $argLine = '-batchmode -nographics'
    $argLine += ' -projectPath "' + $root + '"'
    $argLine += ' -executeMethod Game.Editor.Build.ClientBuild.BuildWindows'
    $argLine += ' "--m1-output=' + $out + '"'
    $argLine += ' --m1-app-version=' + $AppVersion
    $argLine += ' --m1-res-version=' + $ResVersion
    $argLine += ' --m1-phase=' + $Phase
    $argLine += ' -logFile "' + $log + '"'
    Set-Content -LiteralPath (Join-Path $evidence ("build-client-$Phase-command.txt")) -Value ($UnityEditorPath + ' ' + $argLine) -Encoding utf8
    $p = Start-Process -FilePath $UnityEditorPath -ArgumentList $argLine -WorkingDirectory $root -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath (Join-Path $evidence ("build-client-$Phase.pid")) -Value $p.Id
    $p.WaitForExit()
    Set-Content -LiteralPath (Join-Path $evidence ("build-client-$Phase-exit.txt")) -Value $p.ExitCode
    if ($p.ExitCode -ne 0) {
        throw "Unity phase=$Phase exit=$($p.ExitCode) log=$log"
    }
}

$protoc = Resolve-Protoc -Explicit $ProtocPath
if (-not $protoc) {
    throw 'protoc 未找到（已查 Tools/protoc、Program Files、anaconda、PATH）；拒绝跳过 gen_proto'
}
Write-Output "protoc=$protoc"
& (Join-Path $root 'Tools\gen_proto.ps1') -RepoRoot $root -ProtocPath $protoc
if ($LASTEXITCODE -ne 0) { throw "gen_proto exit=$LASTEXITCODE" }

$luban = Join-Path $root 'Tools\Luban\Luban\Luban.dll'
if (-not (Test-Path -LiteralPath $luban)) { throw "缺少 Luban: $luban" }
powershell.exe -NoProfile -File (Join-Path $root 'Tools\gen_config.ps1') -RepoRoot $root -SourceDir (Join-Path $root 'Shared\config') -ExporterPath $luban -ConfigVersion $ResVersion
if ($LASTEXITCODE -ne 0) { throw "gen_config exit=$LASTEXITCODE" }

Invoke-UnityPhase -Phase 'boot' -LogName 'unity-clientbuild-boot.log'
Invoke-UnityPhase -Phase 'gen' -LogName 'unity-clientbuild-gen.log'
Start-Sleep -Seconds 2
Invoke-UnityPhase -Phase 'yoo' -LogName 'unity-clientbuild-yoo.log'
Invoke-UnityPhase -Phase 'player' -LogName 'unity-clientbuild-player.log'

$exe = Join-Path $out 'Player\Farming.exe'
$manifest = Join-Path $out 'baseline-manifest.json'
$fp = Join-Path $out 'baseline-fingerprint.json'
if (-not (Test-Path -LiteralPath $exe)) { throw "缺少 Player: $exe" }
if (-not (Test-Path -LiteralPath $manifest)) { throw "缺少 baseline: $manifest" }
if (-not (Test-Path -LiteralPath $fp)) { throw "缺少 fingerprint: $fp" }
Write-Output "OK OutputDir=$out"
Write-Output "Player=$exe"
Write-Output "Baseline=$manifest"
Write-Output "Fingerprint=$fp"
exit 0

```


#### Tools/build_patch.ps1

```powershell
<#
.SYNOPSIS
  T59：基于冻结 baseline 生成独立 101 补丁目录；AOT 差异非0退出。
.PARAMETER RepoRoot
  仓库根。
.PARAMETER UnityEditorPath
  Unity.exe。
.PARAMETER OutputDir
  发布根（其下 101/ 与 version.json）。
.PARAMETER ResVersion
  默认 101。
.PARAMETER ConfigVersion
  默认与 ResVersion 相同。
.PARAMETER BaseBuildManifest
  T58 baseline-manifest.json 路径。
.PARAMETER SkipUnity
  仅跑 ValidateBaseline（须另有已 Stage 的 101 内容时不用）。
#>
param(
    [string]$RepoRoot = 'E:\Workbench\UnityWorkBench\Farming',
    [string]$UnityEditorPath = 'D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe',
    [string]$OutputDir = '',
    [int]$ResVersion = 101,
    [int]$ConfigVersion = 101,
    [string]$BaseBuildManifest = '',
    [string]$AppVersion = '0.3.0',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepoRoot)
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD\Publish'
}
$out = [IO.Path]::GetFullPath($OutputDir)
if ([string]::IsNullOrWhiteSpace($BaseBuildManifest)) {
    $BaseBuildManifest = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD\Freeze100\baseline-manifest.json'
}
$base = [IO.Path]::GetFullPath($BaseBuildManifest)
$evidence = Join-Path $root 'Docs\Acceptance\M1\20261020-xLua\StageD'
New-Item -ItemType Directory -Path $out, $evidence -Force | Out-Null

if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity不存在: $UnityEditorPath"
}
if (-not (Test-Path -LiteralPath $base -PathType Leaf)) {
    throw "缺少 BaseBuildManifest: $base"
}
$existing = Get-Process Unity -ErrorAction SilentlyContinue
if ($existing) {
    throw ("已有Unity进程 PID=" + (($existing | ForEach-Object Id) -join ',') + "；禁止盲开第二实例")
}

$method = if ($ValidateOnly) { 'Game.Editor.Build.PatchBuild.ValidateBaselineOnly' } else { 'Game.Editor.Build.PatchBuild.BuildPatch' }
$log = Join-Path $evidence $(if ($ValidateOnly) { 'unity-patch-validate.log' } else { 'unity-patchbuild.log' })
# 单字符串 ArgumentList：含空格路径必须整体引号，避免 Start-Process 拆参。
$argLine = '-batchmode -nographics'
$argLine += ' -projectPath "' + $root + '"'
$argLine += ' -executeMethod ' + $method
$argLine += ' "--m1-output=' + $out + '"'
$argLine += ' --m1-res-version=' + $ResVersion
$argLine += ' --m1-config-version=' + $ConfigVersion
$argLine += ' --m1-app-version=' + $AppVersion
$argLine += ' "--m1-base-manifest=' + $base + '"'
$argLine += ' -logFile "' + $log + '"'
Set-Content -LiteralPath (Join-Path $evidence 'build-patch-command.txt') -Value ($UnityEditorPath + ' ' + $argLine) -Encoding utf8
$p = Start-Process -FilePath $UnityEditorPath -ArgumentList $argLine -WorkingDirectory $root -WindowStyle Hidden -PassThru
$p.WaitForExit()
Set-Content -LiteralPath (Join-Path $evidence 'build-patch-exit.txt') -Value $p.ExitCode
if ($p.ExitCode -ne 0) {
    throw "build_patch exit=$($p.ExitCode) log=$log"
}
Write-Output "OK Publish=$out Res=$ResVersion"
exit 0

```


#### Tools/run_server.ps1

```powershell
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
启动必须精确加载的配置版本。
.PARAMETER RequiredConfigVersion
登录最低配置版本，可以低于兼容的启动数据版本。
.PARAMETER ResVersion
登录最低资源版本。
.PARAMETER AppVersion
登录最低安装包版本，默认M1固定桥的0.3.0。
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
    [ValidateRange(1,2147483647)][int]$ConfigVersion = 100,
    [ValidateRange(1,2147483647)][int]$RequiredConfigVersion = 100,
    [ValidateRange(1,2147483647)][int]$ResVersion = 100,
    [string]$AppVersion = '0.3.0',
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
        '--min-app-version', $AppVersion, '--proto-version', [string]$ProtoVersion,
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

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Build-FinalCandidate.ps1

```powershell
<# Rebuild after the terminal-reconnect fix; keep previous candidates and their evidence intact. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$root=[IO.Path]::GetFullPath($RepoRoot)
$base=Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$publish=Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/Root Final Publish R2'
if(Test-Path -LiteralPath $base){throw 'Candidate already exists. Do not overwrite frozen evidence.'}
if(Test-Path -LiteralPath $publish){throw 'Publish root already exists.'}
function Invoke-Recorded([string]$Exe,[string[]]$Arguments,[string]$Name){
    $line=($Arguments|ForEach-Object {Quote-ProcessArg $_}) -join ' '
    $p=Start-Process -FilePath $Exe -ArgumentList $line -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $PSScriptRoot ($Name+'.stdout')) -RedirectStandardError (Join-Path $PSScriptRoot ($Name+'.stderr'))
    $p.WaitForExit()
    Write-AtomicUtf8 (Join-Path $PSScriptRoot ($Name+'.exit.txt')) ([string]$p.ExitCode)
    if($p.ExitCode -ne 0){throw "$Name failed with exit $($p.ExitCode)"}
}
$history=Join-Path $PSScriptRoot 'Before-R2-BuildLogs'
[IO.Directory]::CreateDirectory($history)|Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/StageD') -File|Where-Object {
    $_.Name -match '^(build-client-|unity-clientbuild-|build-patch-|unity-patchbuild)'
}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $history $_.Name)}
Invoke-Recorded 'pwsh' @('-NoProfile','-File',(Join-Path $root 'Tools/build_client_win.ps1'),'-RepoRoot',$root,'-OutputDir',$base,'-ResVersion','100') 'r2-client-build'
Copy-Item -LiteralPath (Join-Path $root 'Server/ConfigLuban') -Destination (Join-Path $base 'ServerConfig100') -Recurse
Invoke-Recorded 'powershell.exe' @('-NoProfile','-File',(Join-Path $root 'Tools/gen_config.ps1'),'-RepoRoot',$root,
    '-SourceDir',(Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/StageD/ConfigSource101'),
    '-ExporterPath',(Join-Path $root 'Tools/Luban/Luban/Luban.dll'),'-ConfigVersion','101') 'r2-config101'
Copy-Item -LiteralPath (Join-Path $root 'Server/ConfigLuban') -Destination (Join-Path $base 'ServerConfig101') -Recurse
Invoke-Recorded 'pwsh' @('-NoProfile','-File',(Join-Path $root 'Tools/build_patch.ps1'),'-RepoRoot',$root,
    '-OutputDir',$publish,'-BaseBuildManifest',(Join-Path $base 'baseline-manifest.json'),'-ResVersion','101','-ConfigVersion','101') 'r2-patch-build'
Invoke-Recorded 'powershell.exe' @('-NoProfile','-File',(Join-Path $root 'Tools/gen_config.ps1'),'-RepoRoot',$root,
    '-SourceDir',(Join-Path $root 'Shared/config'),'-ExporterPath',(Join-Path $root 'Tools/Luban/Luban/Luban.dll'),'-ConfigVersion','100') 'r2-restore-config100'
Write-AtomicUtf8 (Join-Path $PSScriptRoot 'r2-build-result.json') (@{passed=$true;base=$base;publish=$publish;sha=(Get-InstallHashes (Join-Path $base 'Player/Farming.exe'))}|ConvertTo-Json -Depth 6)
Write-Output 'PASS final R2 full build and patch.'

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/NativeHarness.ps1

```powershell
# Shared, local-only Native acceptance helpers. No credentials are printed.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-InRoot([string]$Path,[string]$OwnedRoot) {
    $full=[IO.Path]::GetFullPath($Path)
    $base=[IO.Path]::GetFullPath($OwnedRoot).TrimEnd('\')
    if($full -ne $base -and -not $full.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw "Path outside owned root: $full"
    }
    return $full
}

# Quote a single Windows process argument. Test paths never contain literal quotes.
function Quote-ProcessArg([string]$Value) {
    if($Value.Contains('"')){throw 'Literal quotes are not supported in test paths.'}
    return '"'+$Value+'"'
}

function Write-AtomicUtf8([string]$Path,[string]$Value) {
    $tmp=$Path+'.tmp'
    [IO.File]::WriteAllText($tmp,$Value,[Text.UTF8Encoding]::new($false))
    if(Test-Path -LiteralPath $Path){[IO.File]::Replace($tmp,$Path,$Path+'.previous')}
    else{[IO.File]::Move($tmp,$Path)}
}

function Get-FreePort {
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    try{$listener.Start();return ([Net.IPEndPoint]$listener.LocalEndpoint).Port}
    finally{$listener.Stop()}
}

function New-NativeFixture([string]$RepoRoot,[string]$RunDirectory,[string]$PlayerExe,[string]$ContentRoot) {
    $repo=[IO.Path]::GetFullPath($RepoRoot)
    $run=Assert-InRoot $RunDirectory $repo
    if(Test-Path -LiteralPath $run){throw "Run directory already exists: $run"}
    [IO.Directory]::CreateDirectory($run)|Out-Null
    $accept=Join-Path $run 'client'
    [IO.Directory]::CreateDirectory($accept)|Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $accept 'commands'))|Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $accept 'acks'))|Out-Null
    $user='m1'+[guid]::NewGuid().ToString('N').Substring(0,10)
    $password='Pw'+[guid]::NewGuid().ToString('N').Substring(0,16)
    [IO.File]::WriteAllLines((Join-Path $accept 'creds.private'),@($user,$password,'FinalNick'),[Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        Repo=$repo; Run=$run; Accept=$accept; Exe=[IO.Path]::GetFullPath($PlayerExe)
        Content=[IO.Path]::GetFullPath($ContentRoot); Database=(Join-Path $run 'players.db')
        StopFile=(Join-Path $run 'server.stop'); Server=$null; Player=$null
        PlayerStarted=[datetime]::MinValue; Tcp=(Get-FreePort); Http=(Get-FreePort)
        PlayerLog=''
        CommandNumber=0; Phase='setup'; Actions=[Collections.Generic.List[object]]::new()
    }
}

function Save-Action($Fixture,[string]$Name,$Actual) {
    $record=[ordered]@{utc=[datetime]::UtcNow.ToString('o');phase=$Fixture.Phase;name=$Name;actual=$Actual}
    $Fixture.Actions.Add($record)
    [IO.File]::AppendAllText((Join-Path $Fixture.Run 'actions.jsonl'),($record|ConvertTo-Json -Depth 15 -Compress)+"`n",[Text.UTF8Encoding]::new($false))
}

# All waits fail explicitly. Each polling sleep is short; the API caller can report progress.
function Wait-NativeCondition($Fixture,[string]$Name,[scriptblock]$Condition,[int]$Seconds=60) {
    $deadline=[datetime]::UtcNow.AddSeconds($Seconds)
    do {
        if(& $Condition){Save-Action $Fixture $Name 'passed';return}
        if($Fixture.Player -and $Fixture.Player.HasExited){throw "${Name}: Player exited $($Fixture.Player.ExitCode)"}
        Start-Sleep -Milliseconds 200
    }while([datetime]::UtcNow -lt $deadline)
    $state=Get-NativeState $Fixture
    Save-Action $Fixture ($Name+'-timeout') $state
    throw "Timed out waiting for $Name"
}

function Get-NativeState($Fixture) {
    $file=Join-Path $Fixture.Accept 'accept-state.txt'
    $state=@{}
    if(-not (Test-Path -LiteralPath $file)){return $state}
    try {
        foreach($line in [IO.File]::ReadAllLines($file)){
            $pair=$line.Split('=',2)
            if($pair.Count -eq 2){$state[$pair[0]]=$pair[1]}
        }
        if(-not $state.ContainsKey('utc') -or [datetime]::Parse($state.utc).ToUniversalTime() -lt $Fixture.PlayerStarted){return @{}}
    }catch [IO.IOException]{return @{}}
    return $state
}

function Send-NativeCommand($Fixture,[string]$Operation,[int]$Seconds=25) {
    $Fixture.CommandNumber++
    $id=('{0:d4}' -f $Fixture.CommandNumber)
    $file=Join-Path $Fixture.Accept "commands\$id.cmd"
    Write-AtomicUtf8 $file ($Operation+"`n")
    $ack=Join-Path $Fixture.Accept "acks\$id.ack"
    Wait-NativeCondition $Fixture ('ack-'+$Operation) {Test-Path -LiteralPath $ack} $Seconds
    $text=[IO.File]::ReadAllText($ack)
    if($text -notmatch '(?m)^result=ok\r?$'){throw "Command failed: $Operation ($text)"}
    Save-Action $Fixture ('command-'+$Operation) 'ok'
}

function Set-NativeVersion($Fixture,[int]$Version) {
    $source=Join-Path $Fixture.Content "$Version\version.json"
    $manifest=[IO.File]::ReadAllText($source)|ConvertFrom-Json
    if([int]$manifest.ResVersion -ne $Version){throw 'Incorrect version manifest.'}
    $manifest.BaseUrl="http://localhost:$($Fixture.Http)/$Version/"
    Write-AtomicUtf8 (Join-Path $Fixture.Content 'version.json') ($manifest|ConvertTo-Json -Depth 10)
    Save-Action $Fixture 'atomic-version-pointer' $Version
}

function Start-NativeServer($Fixture,[string]$ConfigDirectory,[int]$ConfigVersion,[int]$ProtocolVersion=1) {
    if($Fixture.Server -and -not $Fixture.Server.HasExited){throw 'Fixture server is already running.'}
    if(Test-Path -LiteralPath $Fixture.StopFile){Remove-Item -LiteralPath (Assert-InRoot $Fixture.StopFile $Fixture.Run)}
    $logs=Join-Path $Fixture.Run ('server-'+$Fixture.Phase)
    [IO.Directory]::CreateDirectory($logs)|Out-Null
    $args=@('-NoProfile','-File',(Join-Path $Fixture.Repo 'Tools/run_server.ps1'),
        '-RepoRoot',$Fixture.Repo,'-DatabasePath',$Fixture.Database,'-TcpPort',[string]$Fixture.Tcp,
        '-HttpPort',[string]$Fixture.Http,'-ContentRoot',$Fixture.Content,'-ConfigDirectory',$ConfigDirectory,
        '-ConfigVersion',[string]$ConfigVersion,'-RequiredConfigVersion','100','-ResVersion','100','-ProtoVersion',[string]$ProtocolVersion,
        '-LogDirectory',$logs,'-StopFile',$Fixture.StopFile,'-BuildArtifactsDirectory',
        (Join-Path $Fixture.Repo 'Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts'),'-SkipBuild')
    $line=($args|ForEach-Object {Quote-ProcessArg $_}) -join ' '
    $Fixture.Server=Start-Process -FilePath pwsh -ArgumentList $line -WorkingDirectory $Fixture.Repo -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'host.stdout') -RedirectStandardError (Join-Path $logs 'host.stderr')
    $deadline=[datetime]::UtcNow.AddSeconds(30)
    do {
        if($Fixture.Server.HasExited){throw "Server exited $($Fixture.Server.ExitCode)"}
        try {
            $result=Invoke-WebRequest -Uri "http://localhost:$($Fixture.Http)/version.json" -NoProxy -TimeoutSec 1
            if($result.StatusCode -eq 200){Save-Action $Fixture 'server-started' @{wrapperPid=$Fixture.Server.Id;database=$Fixture.Database;tcp=$Fixture.Tcp;http=$Fixture.Http};return}
        }catch{}
        Start-Sleep -Milliseconds 200
    }while([datetime]::UtcNow -lt $deadline)
    throw 'Server did not become ready.'
}

function Start-NativePlayer($Fixture) {
    if($Fixture.Player -and -not $Fixture.Player.HasExited){throw 'Fixture Player is already running.'}
    # An interrupted command without an ack must not be replayed on a new process.
    $commandDir=Assert-InRoot (Join-Path $Fixture.Accept 'commands') $Fixture.Run
    foreach($pending in @(Get-ChildItem -LiteralPath $commandDir -File -Filter '*.cmd')){
        Remove-Item -LiteralPath (Assert-InRoot $pending.FullName $Fixture.Run)
    }
    $Fixture.PlayerStarted=[datetime]::UtcNow
    $Fixture.PlayerLog=Join-Path $Fixture.Run ('player-'+[datetime]::UtcNow.ToString('HHmmssfff')+'.log')
    $args=@("--m1-accept-root=$($Fixture.Accept)","--m1-accept-http=http://localhost:$($Fixture.Http)/",
        '--m1-accept-tcp-host=127.0.0.1',"--m1-accept-tcp-port=$($Fixture.Tcp)",
        '-screen-width','1280','-screen-height','720','-screen-fullscreen','0','-logFile',$Fixture.PlayerLog)
    $line=($args|ForEach-Object {Quote-ProcessArg $_}) -join ' '
    $Fixture.Player=Start-Process -FilePath $Fixture.Exe -ArgumentList $line -WorkingDirectory (Split-Path $Fixture.Exe) -PassThru
    Save-Action $Fixture 'player-started' @{pid=$Fixture.Player.Id;exe=$Fixture.Exe;clientRoot=$Fixture.Accept}
}

function Stop-NativePlayer($Fixture) {
    if(-not $Fixture.Player -or $Fixture.Player.HasExited){return}
    try{Send-NativeCommand $Fixture 'quit' 10}catch{}
    if(-not $Fixture.Player.WaitForExit(10000)){
        $identity=Get-CimInstance Win32_Process -Filter ("ProcessId="+$Fixture.Player.Id)
        if($identity -and [string]::Equals($identity.ExecutablePath,$Fixture.Exe,[StringComparison]::OrdinalIgnoreCase) -and
            ([string]$identity.CommandLine).Contains($Fixture.Accept)){
            Stop-Process -Id $Fixture.Player.Id -Force
        }else{throw 'Refusing to stop a process with a different identity.'}
    }
    Save-Action $Fixture 'player-stopped' $Fixture.Player.ExitCode
}

function Stop-NativeServer($Fixture) {
    if(-not $Fixture.Server -or $Fixture.Server.HasExited){return}
    [IO.File]::WriteAllText($Fixture.StopFile,'stop')
    if(-not $Fixture.Server.WaitForExit(20000)){throw 'Owned Server did not stop after its stop file.'}
    Save-Action $Fixture 'server-stopped' $Fixture.Server.ExitCode
}

function Save-NativeState($Fixture,[string]$Name) {
    $state=@{}
    for($readAttempt=0;$readAttempt -lt 10;$readAttempt++){
        $state=Get-NativeState $Fixture
        if($state.Count -gt 0){break}
        Start-Sleep -Milliseconds 100
    }
    foreach($key in @('utc','auth','entry','committed','winLogin','winHome','resVersion','downloadFiles','luaModel')){
        if(-not $state.ContainsKey($key)){throw "Snapshot $Name is missing $key"}
    }
    Write-AtomicUtf8 (Join-Path $Fixture.Run ($Name+'.json')) ($state|ConvertTo-Json -Depth 20)
    Save-Action $Fixture $Name $state
    return $state
}

function Get-InstallHashes([string]$Exe) {
    $dir=Split-Path $Exe
    return [ordered]@{exe=(Get-FileHash -LiteralPath $Exe).Hash;
        gameAssembly=(Get-FileHash -LiteralPath (Join-Path $dir 'GameAssembly.dll')).Hash;
        metadata=(Get-FileHash -LiteralPath (Join-Path $dir 'Farming_Data/il2cpp_data/Metadata/global-metadata.dat')).Hash}
}

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-NativeAccountsAndPatch.ps1

```powershell
<#
.SYNOPSIS
  Real Native account lifecycle and same-install/cache 100→101→cached101.
  Every wait checks fresh state from the current process and fails on timeout.
.PARAMETER RepoRoot
  Project root; all test data stays inside this workspace.
#>
param(
    [string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming',
    [string]$PublishRoot=''
)
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$repo=[IO.Path]::GetFullPath($RepoRoot)
$base=Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$published=if($PublishRoot){[IO.Path]::GetFullPath($PublishRoot)}else{Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final Publish R2'}
$run=Join-Path $PSScriptRoot ('Native-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$fixture=New-NativeFixture $repo $run (Join-Path $base 'Player/Farming.exe') (Join-Path $run 'cdn')

function Copy-VersionFiles([string]$Source,[string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination)|Out-Null
    Get-ChildItem -LiteralPath $Source -File|ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination $_.Name)
    }
}
Copy-VersionFiles (Join-Path $base 'Publish/100') (Join-Path $fixture.Content '100')
Copy-VersionFiles (Join-Path $published '101') (Join-Path $fixture.Content '101')

function Canonical($Value) {
    if($null -eq $Value){return $null}
    if($Value -is [pscustomobject]){
        $map=[ordered]@{}
        foreach($property in @($Value.PSObject.Properties|ForEach-Object {$_.Name}|Sort-Object)){$map[$property]=Canonical $Value.$property}
        return $map
    }
    if($Value -is [array]){return ,@($Value|ForEach-Object {Canonical $_})}
    return $Value
}
function Model-Text($State) {
    if($State['luaModel'] -eq '{}'){throw 'Lua model observer did not return business data.'}
    return (Canonical ($State['luaModel']|ConvertFrom-Json))|ConvertTo-Json -Depth 30 -Compress
}
function Wait-Login([int]$Version) {
    Wait-NativeCondition $fixture "login-ready-$Version" {
        $s=Get-NativeState $fixture
        $s['winLogin'] -eq 'True' -and $s['committed'] -eq 'True' -and $s['resVersion'] -eq [string]$Version
    }
}
function Login-Existing([string]$SnapshotName) {
    Send-NativeCommand $fixture 'fill-creds'
    Send-NativeCommand $fixture 'login'
    Wait-NativeCondition $fixture 'authenticated-home' {
        $s=Get-NativeState $fixture
        $s['auth'] -eq 'True' -and $s['winHome'] -eq 'True' -and $s['hudNick'] -eq 'FinalNick' -and $s['luaNick'] -eq 'FinalNick'
    }
    $s=Save-NativeState $fixture $SnapshotName
    if($s['mask'] -ne 'False'){throw 'Home remained behind a reconnect mask.'}
    return $s
}

$before=Get-InstallHashes $fixture.Exe
Write-AtomicUtf8 (Join-Path $run 'install-before.json') ($before|ConvertTo-Json)
$success=$false
try {
    $fixture.Phase='100-account'
    Set-NativeVersion $fixture 100
    Start-NativeServer $fixture (Join-Path $base 'ServerConfig100') 100
    Start-NativePlayer $fixture
    Wait-Login 100
    $initial=Save-NativeState $fixture 'A02-B02-login100'
    if($initial['patchModule'] -ne 'False' -or $initial['globalPatch'] -ne '' -or $initial['winPatch'] -ne 'False'){
        throw '100 unexpectedly contains new patch content.'
    }
    if([int]$initial['downloadFiles'] -le 0){throw 'Fresh100 did not report real bundle downloads.'}
    Send-NativeCommand $fixture 'screenshot:100_login'
    Send-NativeCommand $fixture 'fill-creds'
    Send-NativeCommand $fixture 'register'
    Wait-NativeCondition $fixture 'registration-business-result' {
        $s=Get-NativeState $fixture
        $s['registerMessage'] -match '注册成功|请登录'
    }
    $registered=Save-NativeState $fixture 'A03-register-not-authorized'
    if($registered['auth'] -ne 'False' -or $registered['hasToken'] -ne 'False'){
        throw 'Registration unexpectedly authorized the client.'
    }
    $s1=Login-Existing 'A04-S1-home100'
    $model1=Model-Text $s1
    $id1=($s1['luaModel']|ConvertFrom-Json).info.playerId
    if([string]$id1 -eq '0' -or [string]::IsNullOrEmpty([string]$id1)){throw 'Missing real player identity.'}
    Send-NativeCommand $fixture 'screenshot:100_home'
    Send-NativeCommand $fixture 'logout'
    Wait-NativeCondition $fixture 'logout-to-login' {
        $s=Get-NativeState $fixture
        $s['auth'] -eq 'False' -and $s['hasToken'] -eq 'False' -and $s['winLogin'] -eq 'True' -and $s['winHome'] -eq 'False'
    }
    $loggedOut=Save-NativeState $fixture 'A06-logout'
    $clearModel=$loggedOut['luaModel']|ConvertFrom-Json
    if([string]$clearModel.info.playerId -ne '0' -or $clearModel.info.nickname -ne ''){throw 'Logout did not clear Lua model.'}
    $again=Login-Existing 'A07-login-again'
    if((Model-Text $again) -ne $model1){throw 'Business snapshot changed after logout/login.'}
    Stop-NativePlayer $fixture
    Start-NativePlayer $fixture
    Wait-Login 100
    $reopened=Login-Existing 'A10-reopen-same-install'
    if((Model-Text $reopened) -ne $model1){throw 'Business snapshot changed after restart.'}
    Stop-NativePlayer $fixture

    $fixture.Phase='101-update'
    Set-NativeVersion $fixture 101
    Stop-NativeServer $fixture
    Start-NativeServer $fixture (Join-Path $base 'ServerConfig101') 101
    Start-NativePlayer $fixture
    Wait-Login 101
    $updated=Save-NativeState $fixture 'B06-updated101'
    if($updated['globalPatch'] -ne '热更新验证成功' -or $updated['patchModule'] -ne 'True'){
        throw 'Actual 101 module or Luban Global is missing.'
    }
    if([int]$updated['downloadFiles'] -le 0 -or [int]$updated['downloadFiles'] -ge [int]$initial['downloadFiles']){
        throw '101 did not report a positive differential bundle download.'
    }
    $home101=Login-Existing 'B07-home101'
    if((Model-Text $home101) -ne $model1){throw 'Business snapshot changed during resource update.'}
    Send-NativeCommand $fixture 'open-patchdemo'
    Wait-NativeCondition $fixture 'real-patch-prefab-opened' {(Get-NativeState $fixture)['winPatch'] -eq 'True'}
    Send-NativeCommand $fixture 'screenshot:101_patch_window'
    [void](Save-NativeState $fixture 'B08-real-prefab-and-global')
    Stop-NativePlayer $fixture

    $fixture.Phase='101-cache-reuse'
    Start-NativePlayer $fixture
    Wait-Login 101
    $cached=Save-NativeState $fixture 'B10-cached101'
    $cachedCount=0
    if(-not $cached.ContainsKey('downloadFiles') -or -not [int]::TryParse($cached['downloadFiles'],[ref]$cachedCount) -or $cachedCount -ne 0){
        throw 'Cached101 download observation is missing, invalid, or nonzero.'
    }
    [void](Login-Existing 'B10-cached-home')
    Send-NativeCommand $fixture 'open-patchdemo'
    Wait-NativeCondition $fixture 'cached-patch-prefab-opened' {(Get-NativeState $fixture)['winPatch'] -eq 'True'}
    Stop-NativePlayer $fixture
    $after=Get-InstallHashes $fixture.Exe
    Write-AtomicUtf8 (Join-Path $run 'install-after.json') ($after|ConvertTo-Json)
    foreach($key in $before.Keys){if($before[$key] -ne $after[$key]){throw "Installed file changed: $key"}}
    $success=$true
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;run=$run;installUnchanged=$true;playerId=$id1;initialDownloadFiles=$initial['downloadFiles'];updateDownloadFiles=$updated['downloadFiles'];cachedDownloadFiles=$cached['downloadFiles']}|ConvertTo-Json)
} catch {
    Write-AtomicUtf8 (Join-Path $run 'failure.json') (@{passed=$false;phase=$fixture.Phase;message=$_.Exception.Message;state=(Get-NativeState $fixture)}|ConvertTo-Json -Depth 20)
    throw
} finally {
    try{Stop-NativePlayer $fixture}catch{Save-Action $fixture 'player-cleanup-error' $_.Exception.Message}
    try{Stop-NativeServer $fixture}catch{Save-Action $fixture 'server-cleanup-error' $_.Exception.Message}
}
if(-not $success){exit 1}
Write-Output "PASS Native account/update/cache: $run"

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-NativeBadResource.ps1

```powershell
<# Corrupt only an owned CDN copy, use an empty client cache, repair, then click the real Retry. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$repo=[IO.Path]::GetFullPath($RepoRoot)
$base=Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$publish=Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final Publish R2/101'
$run=Join-Path $PSScriptRoot ('BadResource-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$f=New-NativeFixture $repo $run (Join-Path $base 'Player/Farming.exe') (Join-Path $run 'cdn')
$versionDir=Join-Path $f.Content '101'
[IO.Directory]::CreateDirectory($versionDir)|Out-Null
Get-ChildItem -LiteralPath $publish -File|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $versionDir $_.Name)}
$blob=Get-ChildItem -LiteralPath $versionDir -File -Filter '*.bundle'|Sort-Object Length -Descending|Select-Object -First 1
if(-not $blob){throw 'No real bundle found in published version.'}
$blobPath=Assert-InRoot $blob.FullName $run
$good=[IO.File]::ReadAllBytes($blobPath)
$goodHash=(Get-FileHash -LiteralPath $blobPath).Hash
$installBefore=Get-InstallHashes $f.Exe
[IO.File]::WriteAllBytes($blobPath,[byte[]]@(0,1,2,3,4,5,6,7))
$f.Phase='bad-resource'
try {
    Set-NativeVersion $f 101
    Start-NativeServer $f (Join-Path $base 'ServerConfig101') 101
    Start-NativePlayer $f
    Wait-NativeCondition $f 'visible-update-error' {(Get-NativeState $f)['patchRetryVisible'] -eq 'True'} 120
    $failed=Save-NativeState $f 'B12-visible-error-no-activation'
    if($failed['entry'] -ne 'False' -or $failed['committed'] -ne 'False' -or $failed['winLogin'] -ne 'False'){
        throw 'Invalid candidate was activated.'
    }
    Send-NativeCommand $f 'screenshot:download_error'
    # Leave the error visible while the owned CDN copy is repaired.
    Start-Sleep -Seconds 1
    [IO.File]::WriteAllBytes($blobPath,$good)
    if((Get-FileHash -LiteralPath $blobPath).Hash -ne $goodHash){throw 'Resource repair hash differs.'}
    Save-Action $f 'B13-resource-restored' @{file=$blob.Name;sha256=$goodHash}
    Send-NativeCommand $f 'click-patch-retry'
    Wait-NativeCondition $f 'retry-enters-login' {
        $s=Get-NativeState $f
        $s['winLogin'] -eq 'True' -and $s['committed'] -eq 'True' -and $s['entry'] -eq 'True'
    } 90
    $repaired=Save-NativeState $f 'B14-retry-success'
    Send-NativeCommand $f 'screenshot:retry_login'
    $log=[IO.File]::ReadAllText((Join-Path $f.Accept 'native-observe.log'))
    $attempts=@([regex]::Matches($log,'launcher patch-prepare-attempt (\d+)')|ForEach-Object {$_.Groups[1].Value})
    if(($attempts -join ',') -ne '1,2'){throw "Unexpected retry pipelines: $($attempts -join ',')"}
    Stop-NativePlayer $f
    $after=Get-InstallHashes $f.Exe
    foreach($key in $installBefore.Keys){if($installBefore[$key] -ne $after[$key]){throw 'Installed Player changed.'}}
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;bundle=$blob.Name;originalSha256=$goodHash;attempts=$attempts;installationUnchanged=$true}|ConvertTo-Json -Depth 10)
}catch{
    Write-AtomicUtf8 (Join-Path $run 'failure.json') (@{passed=$false;message=$_.Exception.Message;state=(Get-NativeState $f)}|ConvertTo-Json -Depth 20)
    throw
}finally{
    try{Stop-NativePlayer $f}catch{Save-Action $f 'player-cleanup-error' $_.Exception.Message}
    try{Stop-NativeServer $f}catch{Save-Action $f 'server-cleanup-error' $_.Exception.Message}
}
Write-Output "PASS Native corrupt resource/repair/real Retry: $run"

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-NativeRecovery.ps1

```powershell
<# Real Native C01-C12: outage/backoff, same DB restart, owned server crash, expiry, replacement login. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$repo=[IO.Path]::GetFullPath($RepoRoot)
$base=Join-Path $repo 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$seed=Join-Path $PSScriptRoot 'Native-20261021-141638-780'
$run=Join-Path $PSScriptRoot ('Recovery-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$f=New-NativeFixture $repo $run (Join-Path $base 'Player/Farming.exe') (Join-Path $run 'cdn')
$b=$null
[IO.Directory]::CreateDirectory((Join-Path $f.Content '100'))|Out-Null
Get-ChildItem -LiteralPath (Join-Path $base 'Publish/100') -File|ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $f.Content ('100/'+$_.Name))
}
# The source DB is from the completed, closed Native account test, never a live DB.
Copy-Item -LiteralPath (Join-Path $seed 'players.db') -Destination $f.Database
Copy-Item -LiteralPath (Join-Path $seed 'client/creds.private') -Destination (Join-Path $f.Accept 'creds.private') -Force
$probe=Join-Path $PSScriptRoot 'fixture-artifacts/bin/ProtocolFixture/release/ProtocolFixture.dll'
dotnet $probe prepare $f.Database (Join-Path $run 'prepared-saved-state.json') $PSScriptRoot
if($LASTEXITCODE -ne 0){throw 'Production PlayerStore save/reopen fixture failed.'}

function Model-Value($state){return (($state['luaModel']|ConvertFrom-Json)|ConvertTo-Json -Depth 30 -Compress)}
function Wait-Home([string]$Name){
    Wait-NativeCondition $f $Name {
        $s=Get-NativeState $f
        $s['auth'] -eq 'True' -and $s['mask'] -eq 'False' -and $s['winHome'] -eq 'True' -and $s['hudNick'] -eq 'FinalNick'
    } 95
}
try {
    $f.Phase='C01-R1'
    Set-NativeVersion $f 100
    Start-NativeServer $f (Join-Path $base 'ServerConfig100') 100
    Start-NativePlayer $f
    Wait-NativeCondition $f 'initial-login-ready' {(Get-NativeState $f)['winLogin'] -eq 'True'}
    Send-NativeCommand $f 'fill-creds'
    Send-NativeCommand $f 'login'
    Wait-Home 'initial-home'
    $r1=Save-NativeState $f 'C01-R1'
    $model1=Model-Value $r1
    $data=$r1['luaModel']|ConvertFrom-Json
    if([int]$data.info.gold -ne 201 -or @($data.bag).Count -ne 1 -or $data.bag[0].count -ne 7){throw 'Prepared full business data was not loaded into real Lua model.'}
    $token=Join-Path $f.Accept 'tokens/m1-token.txt'
    $tokenHash=(Get-FileHash -LiteralPath $token).Hash
    python (Join-Path $PSScriptRoot 'db_fixture.py') snapshot --database $f.Database --output (Join-Path $run 'C01-R1-database.json')
    if($LASTEXITCODE -ne 0){throw 'R1 database snapshot failed.'}

    $f.Phase='C02-C03-outage'
    Stop-NativeServer $f
    Wait-NativeCondition $f 'disconnect-mask' {(Get-NativeState $f)['mask'] -eq 'True'}
    [void](Save-NativeState $f 'C02-mask')
    Send-NativeCommand $f 'screenshot:reconnect_mask'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $lastAttempt=-1
    while($watch.Elapsed.TotalSeconds -lt 75){
        $state=Get-NativeState $f
        if($state.ContainsKey('reconnectAttempt') -and [int]$state['reconnectAttempt'] -ne $lastAttempt){
            $lastAttempt=[int]$state['reconnectAttempt']
            Save-Action $f 'observed-reconnect-attempt' @{elapsedSeconds=$watch.Elapsed.TotalSeconds;attempt=$lastAttempt;running=$state['reconnectRunning'];mask=$state['mask']}
        }
        if($f.Player.HasExited){throw 'Native Player exited during outage.'}
        Start-Sleep -Milliseconds 500
    }
    # Unity retains the log handle. Permit its writer while reading a point-in-time snapshot.
    $stream=[IO.FileStream]::new($f.PlayerLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $reader=[IO.StreamReader]::new($stream)
    try{$lines=$reader.ReadToEnd() -split "`n"}finally{$reader.Dispose()}
    $backoffs=@(foreach($line in $lines){if($line -match '\[ReconnectService\].*backoff-wait-seconds=(\d+)'){[int]$Matches[1]}})
    if($backoffs.Count -lt 6 -or (($backoffs|Select-Object -First 6)-join ',') -ne '1,2,4,8,16,30'){
        throw "Wrong actual Native backoff sequence: $($backoffs -join ',')"
    }
    Write-AtomicUtf8 (Join-Path $run 'C03-real-backoff.json') (@{elapsedSeconds=$watch.Elapsed.TotalSeconds;observed=$backoffs}|ConvertTo-Json)

    $f.Phase='C04-restore'
    Start-NativeServer $f (Join-Path $base 'ServerConfig100') 100
    Wait-Home 'token-auto-restore'
    $restored=Save-NativeState $f 'C04-restored'
    if((Model-Value $restored) -ne $model1 -or (Get-FileHash -LiteralPath $token).Hash -ne $tokenHash){throw 'Token restore changed full snapshot or issued a different token.'}
    Send-NativeCommand $f 'screenshot:restored_home'
    python (Join-Path $PSScriptRoot 'db_fixture.py') snapshot --database $f.Database --output (Join-Path $run 'C05-R2-database.json')
    if($LASTEXITCODE -ne 0){throw 'R2 save check failed.'}
    $db=Get-Content -LiteralPath (Join-Path $run 'C05-R2-database.json') -Raw|ConvertFrom-Json
    if(@($db.savedSnapshots).Count -ne 1 -or $db.savedSnapshots[0].savedRevision -lt 1){throw 'No real committed saved revision.'}
    $r2=Save-NativeState $f 'C05-R2'

    $f.Phase='C06-crash-owned-server'
    $owned=@(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'"|Where-Object {
        ([string]$_.CommandLine).Contains($f.Database) -and [string]$_.CommandLine -match 'CloudIsle.Server.dll'
    })
    if($owned.Count -ne 1 -or [int]$owned[0].ParentProcessId -ne $f.Server.Id){throw 'Owned server identity check failed before forced termination.'}
    Save-Action $f 'force-stop-owned-server' @{pid=$owned[0].ProcessId;parent=$f.Server.Id;database=$f.Database;expectedExe=$owned[0].ExecutablePath}
    Stop-Process -Id $owned[0].ProcessId -Force
    if(-not $f.Server.WaitForExit(10000)){throw 'Owned wrapper did not observe server crash.'}
    Wait-NativeCondition $f 'crash-disconnect-mask' {(Get-NativeState $f)['mask'] -eq 'True'}
    $f.Phase='C07-crash-restart'
    Start-NativeServer $f (Join-Path $base 'ServerConfig100') 100
    Wait-Home 'crash-auto-restore'
    $crashRestored=Save-NativeState $f 'C07-restored-R2'
    if((Model-Value $crashRestored) -ne (Model-Value $r2) -or (Get-FileHash -LiteralPath $token).Hash -ne $tokenHash){throw 'Crash restore changed committed data or token.'}

    $f.Phase='C08-C10-expiry'
    python (Join-Path $PSScriptRoot 'db_fixture.py') expire --database $f.Database --output (Join-Path $run 'C08-expired-records.json')
    if($LASTEXITCODE -ne 0){throw 'Expiration fixture failed.'}
    Send-NativeCommand $f 'drop-socket'
    Wait-NativeCondition $f '204-returns-login' {
        $s=Get-NativeState $f
        $s['lastBiz'] -eq '204' -and $s['winLogin'] -eq 'True' -and $s['auth'] -eq 'False' -and $s['hasToken'] -eq 'False' -and $s['reconnectRunning'] -eq 'False'
    }
    [void](Save-NativeState $f 'C09-expired-return-login')
    Send-NativeCommand $f 'fill-creds'
    Send-NativeCommand $f 'login'
    Wait-Home 'new-login-after-expiry'
    [void](Save-NativeState $f 'C10-relogin')
    $privateOld=Join-Path $run 'old-token.private'
    Copy-Item -LiteralPath $token -Destination $privateOld

    $f.Phase='C11-replacement'
    $b=New-NativeFixture $repo (Join-Path $run 'second-client') $f.Exe $f.Content
    $b.Tcp=$f.Tcp;$b.Http=$f.Http;$b.Phase='replacement-B'
    Copy-Item -LiteralPath (Join-Path $f.Accept 'creds.private') -Destination (Join-Path $b.Accept 'creds.private') -Force
    Start-NativePlayer $b
    Wait-NativeCondition $b 'B-login-ready' {(Get-NativeState $b)['winLogin'] -eq 'True'}
    Send-NativeCommand $b 'fill-creds'
    Send-NativeCommand $b 'login'
    Wait-NativeCondition $b 'B-authenticated-home' {
        $s=Get-NativeState $b;$s['auth'] -eq 'True' -and $s['winHome'] -eq 'True'
    }
    Wait-NativeCondition $f 'A-kicked-return-login' {
        $s=Get-NativeState $f;$s['winLogin'] -eq 'True' -and $s['auth'] -eq 'False' -and $s['hasToken'] -eq 'False' -and $s['reconnectRunning'] -eq 'False'
    }
    [void](Save-NativeState $f 'C11-A-kicked')
    [void](Save-NativeState $b 'C11-B-current')
    Send-NativeCommand $f 'screenshot:kicked_login'
    $f.Phase='C12-old-token'
    dotnet $probe reconnect $privateOld $f.Tcp 1 204 (Join-Path $run 'C12-old-token-rejection.json') $PSScriptRoot
    if($LASTEXITCODE -ne 0){throw 'Old token protocol rejection failed.'}
    Start-Sleep -Seconds 3
    if((Get-NativeState $b)['auth'] -ne 'True' -or (Get-NativeState $f)['auth'] -ne 'False'){
        throw 'Old client or token stole the current login back.'
    }
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;scope='C01-C12';nativeBackoff=$backoffs;committedRevision=$db.savedSnapshots[0].savedRevision;fullSnapshotAndTokenPreserved=$true}|ConvertTo-Json -Depth 12)
}catch{
    Write-AtomicUtf8 (Join-Path $run 'failure.json') (@{passed=$false;phase=$f.Phase;message=$_.Exception.Message;state=(Get-NativeState $f)}|ConvertTo-Json -Depth 25)
    throw
}finally{
    if($b){try{Stop-NativePlayer $b}catch{Save-Action $b 'cleanup-error' $_.Exception.Message}}
    try{Stop-NativePlayer $f}catch{Save-Action $f 'cleanup-error' $_.Exception.Message}
    try{Stop-NativeServer $f}catch{Save-Action $f 'server-cleanup-error' $_.Exception.Message}
}
Write-Output "PASS Native full recovery: $run"

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-NativeProtocolMismatch.ps1

```powershell
<# C13: production server requires Proto2, unchanged Native Proto1 must display 106 and stop. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$base=Join-Path $RepoRoot 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$run=Join-Path $PSScriptRoot ('Protocol-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$f=New-NativeFixture $RepoRoot $run (Join-Path $base 'Player/Farming.exe') (Join-Path $run 'cdn')
[IO.Directory]::CreateDirectory((Join-Path $f.Content '100'))|Out-Null
Get-ChildItem -LiteralPath (Join-Path $base 'Publish/100') -File|ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $f.Content ('100/'+$_.Name))
}
try{
    $f.Phase='C13-protocol-mismatch'
    Set-NativeVersion $f 100
    Start-NativeServer $f (Join-Path $base 'ServerConfig100') 100 2
    Start-NativePlayer $f
    Wait-NativeCondition $f 'login-ready' {(Get-NativeState $f)['winLogin'] -eq 'True'}
    Send-NativeCommand $f 'fill-creds'
    Send-NativeCommand $f 'login'
    Wait-NativeCondition $f '106-update-required' {
        $s=Get-NativeState $f
        $s['lastBiz'] -eq '106' -and $s['winLogin'] -eq 'True' -and $s['auth'] -eq 'False' -and
        $s['reconnectRunning'] -eq 'False' -and $s['hasToken'] -eq 'False'
    }
    Start-Sleep -Seconds 3
    $state=Save-NativeState $f 'C13-version-mismatch'
    if($state['registerMessage'] -notmatch '版本|更新'){throw '106 did not display an update message.'}
    if($state['reconnectRunning'] -ne 'False'){throw '106 started an infinite reconnect.'}
    Send-NativeCommand $f 'screenshot:version_update_required'
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;scope='C13';clientProto=1;serverProto=2;responseCode=106;updateMessage=$state['registerMessage'];reconnectRunning=$false}|ConvertTo-Json)
}catch{
    Write-AtomicUtf8 (Join-Path $run 'failure.json') (@{passed=$false;message=$_.Exception.Message;state=(Get-NativeState $f)}|ConvertTo-Json -Depth 20)
    throw
}finally{
    try{Stop-NativePlayer $f}catch{Save-Action $f 'cleanup-error' $_.Exception.Message}
    try{Stop-NativeServer $f}catch{Save-Action $f 'server-cleanup-error' $_.Exception.Message}
}
Write-Output "PASS Native protocol mismatch: $run"

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-FinalAotNegative.ps1

```powershell
<# D08: actual standard patch command rejects an owned, reversible fixed-C# difference. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$root=[IO.Path]::GetFullPath($RepoRoot)
$run=Join-Path $PSScriptRoot ('AotNegative-R2-'+[datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
[IO.Directory]::CreateDirectory($run)|Out-Null
$source=Join-Path $root 'Assets/Framework/Account/AccountSession.cs'
$original=[IO.File]::ReadAllBytes($source)
[IO.File]::WriteAllBytes((Join-Path $run 'AccountSession.before.cs.bytes'),$original)
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
$base=Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/Root Final 100 R2'
$exe=Join-Path $base 'Player/Farming.exe'
$before=Get-InstallHashes $exe
$pointer=Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/Root Final Publish R2/version.json'
$pointerHash=(Get-FileHash -LiteralPath $pointer).Hash
try{
    if(Get-Process Unity -ErrorAction SilentlyContinue){throw 'Unity already running.'}
    [IO.File]::WriteAllText($source,"// Owned D08 fixed-code difference; restored byte-for-byte in finally.`n"+[Text.Encoding]::UTF8.GetString($original),[Text.UTF8Encoding]::new($false))
    $output=Join-Path $run 'Attempt'
    $args=@('-NoProfile','-File',(Join-Path $root 'Tools/build_patch.ps1'),'-RepoRoot',$root,'-OutputDir',$output,
        '-BaseBuildManifest',(Join-Path $base 'baseline-manifest.json'),'-ResVersion','101','-ConfigVersion','101')
    $p=Start-Process pwsh -ArgumentList (($args|ForEach-Object {Quote-ProcessArg $_}) -join ' ') -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $run 'stdout.txt') -RedirectStandardError (Join-Path $run 'stderr.txt')
    $p.WaitForExit()
    $report=[IO.File]::ReadAllText((Join-Path $output 'patchbuild-report.txt'))
    if($p.ExitCode -eq 0 -or $report -notmatch 'aot-incompatible:fingerprint-changed:Assets/Framework/Account/AccountSession.cs'){
        throw 'The real patch build did not reject the fixed-C# change.'
    }
    if(Test-Path -LiteralPath (Join-Path $output 'version.json')){throw 'Rejected build published a pointer.'}
    $after=Get-InstallHashes $exe
    foreach($key in $before.Keys){if($before[$key] -ne $after[$key]){throw 'Rejected build changed the Player.'}}
    if((Get-FileHash -LiteralPath $pointer).Hash -ne $pointerHash){throw 'Rejected build changed the published pointer.'}
    Copy-Item -LiteralPath (Join-Path $root 'Docs/Acceptance/M1/20261020-xLua/StageD/unity-patchbuild.log') -Destination (Join-Path $run 'unity.log')
    Write-AtomicUtf8 (Join-Path $run 'result.json') (@{passed=$true;scriptExit=$p.ExitCode;rejection=$report.Trim();playerUnchanged=$true;publishedPointerUnchanged=$true;noRejectedPointer=$true;sourceBeforeSha=$sourceHash}|ConvertTo-Json)
}finally{
    [IO.File]::WriteAllBytes($source,$original)
    if((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash){throw 'Fixed-C# source restoration failed.'}
}
Write-Output "PASS fixed-code negative: $run"

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Run-FinalFoundation.ps1

```powershell
<# Current-source E02/E03/E08/E10: existing foundation tests, no generated test substitutes. #>
param([string]$RepoRoot='E:\Workbench\UnityWorkBench\Farming')
. (Join-Path $PSScriptRoot 'NativeHarness.ps1')
$dir=Join-Path $PSScriptRoot 'Foundation-Final'
if(Test-Path -LiteralPath $dir){throw 'Do not overwrite a test report.'}
[IO.Directory]::CreateDirectory($dir)|Out-Null
if(Get-Process Unity -ErrorAction SilentlyContinue){throw 'Unity already running.'}
$filter='NetClientLifecycleTests;NetClientPendingTests;TcpTransportTests;TcpTransportReceiveTests;OpIdStoreTests;EventBusTests;ObjectPoolTests;TimerServiceTests;FileLoggerTests'
$args=@('-batchmode','-nographics','-projectPath',$RepoRoot,'-runTests','-testPlatform','EditMode','-testFilter',$filter,
    '-testResults',(Join-Path $dir 'results.xml'),'-logFile',(Join-Path $dir 'unity.log'))
$line=($args|ForEach-Object {Quote-ProcessArg $_}) -join ' '
Write-AtomicUtf8 (Join-Path $dir 'command.txt') $line
$p=Start-Process 'D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe' -ArgumentList $line -WorkingDirectory $RepoRoot -WindowStyle Hidden -PassThru
$p.WaitForExit()
Write-AtomicUtf8 (Join-Path $dir 'exit.txt') ([string]$p.ExitCode)
[xml]$xml=[IO.File]::ReadAllText((Join-Path $dir 'results.xml'))
$run=$xml.'test-run'
if($p.ExitCode -ne 0 -or [int]$run.total -lt 1 -or [int]$run.failed -ne 0 -or [int]$run.skipped -ne 0){throw 'Foundation regression failed.'}
Write-AtomicUtf8 (Join-Path $dir 'summary.json') (@{passed=$true;total=[int]$run.total;failed=[int]$run.failed;skipped=[int]$run.skipped;exit=$p.ExitCode}|ConvertTo-Json)
Write-Output "PASS foundation tests: $($run.total)"

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/db_fixture.py

```python
"""Read business snapshots / expire tokens only in this task's isolated DBs.
Never selects password fields or token hashes. The real DB is never copied or
rewritten while a server is running; sqlite transactions perform expiration.
"""
import argparse
import hashlib
import json
import sqlite3
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("action", choices=["snapshot", "expire"])
parser.add_argument("--database", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
owned_root = Path(__file__).resolve().parent
database = Path(args.database).resolve()
output = Path(args.output).resolve()
if not database.is_relative_to(owned_root) or not output.is_relative_to(owned_root):
    raise RuntimeError("fixture paths must be inside Root-Finalization")
mode = "ro" if args.action == "snapshot" else "rw"
with sqlite3.connect(database.as_uri() + "?mode=" + mode, uri=True, timeout=10) as connection:
    if args.action == "expire":
        cursor = connection.execute("UPDATE auth_token SET expires_at_ms=0 WHERE revoked=0")
        connection.commit()
        result = {"expiredCurrentRecords": cursor.rowcount, "expiresAtMs": 0}
    else:
        connection.row_factory = sqlite3.Row
        players = [dict(row) for row in connection.execute("SELECT * FROM player ORDER BY id")]
        saved = []
        for row in connection.execute("SELECT player_id,saved_revision,body FROM player_snapshot ORDER BY player_id"):
            body = bytes(row["body"])
            saved.append({"playerId": row["player_id"], "savedRevision": row["saved_revision"],
                          "bodyBytes": len(body), "bodySha256": hashlib.sha256(body).hexdigest()})
        result = {"players": players, "savedSnapshots": saved}
with output.open("x", encoding="utf-8") as handle:
    json.dump(result, handle, ensure_ascii=False, indent=2)
print(json.dumps({"action": args.action, "output": str(output)}, ensure_ascii=False))

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/ProtocolFixture/Program.cs

```csharp
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using CloudIsle.Server.Config;
using Google.Protobuf;

// Local test fixture only. No service endpoint, production protocol or user DB
// is modified. Credentials/Token values are never printed or put in reports.
try
{
    string action = args[0];
    string root = Path.GetFullPath(args[^1]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    string Owned(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path outside owned fixture root");
        return full;
    }
    object report;
    string output;
    if (action == "prepare")
    {
        string database = Owned(args[1]);
        output = Owned(args[2]);
        if (!File.Exists(database)) throw new FileNotFoundException("Existing fixture DB required");
        PlayerFullData expected;
        long wantedRevision;
        using (var store = new SqliteStore())
        {
            store.Initialize(database);
            var players = new PlayerStore(store);
            PlayerState before = await players.LoadAsync(1, CancellationToken.None);
            expected = before.Data.Clone();
            expected.Info.Gold += 1; // Actual non-default committed business marker.
            expected.Bag.Clear();
            expected.Bag.Add(new ItemStack { ItemId = 1, Count = 7 });
            wantedRevision = checked(before.Revision + 1);
            await players.SaveAsync(new PlayerState(1, expected, wantedRevision, before.SavedRevision), CancellationToken.None);
        }
        using (var reopened = new SqliteStore())
        {
            reopened.Initialize(database);
            PlayerState saved = await new PlayerStore(reopened).LoadAsync(1, CancellationToken.None);
            if (!saved.Data.Equals(expected) || saved.SavedRevision != wantedRevision || saved.Dirty)
                throw new InvalidOperationException("Real production save/reopen did not preserve the full snapshot");
            report = new { passed = true, savedRevision = saved.SavedRevision, dirty = saved.Dirty,
                businessSnapshot = JsonDocument.Parse(JsonFormatter.Default.Format(saved.Data)).RootElement.Clone() };
        }
    }
    else if (action == "reconnect")
    {
        string tokenFile = Owned(args[1]);
        int port = int.Parse(args[2]);
        int protocol = int.Parse(args[3]);
        int expectedCode = int.Parse(args[4]);
        output = Owned(args[5]);
        string token = File.ReadAllText(tokenFile).Trim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, timeout.Token);
        var request = new ReconnectReq { Token = token, ProtoVersion = protocol };
        await tcp.GetStream().WriteAsync(FrameCodec.Encode(MsgRegistry.GetId(typeof(ReconnectReq)), 91, request.ToByteArray()), timeout.Token);
        byte[] prefix = new byte[4];
        await tcp.GetStream().ReadExactlyAsync(prefix, timeout.Token);
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(prefix));
        if (length < FrameCodec.HeaderSize || length > FrameCodec.MaxPayloadLength) throw new IOException("Invalid frame length");
        byte[] bytes = new byte[4 + length];
        prefix.CopyTo(bytes, 0);
        await tcp.GetStream().ReadExactlyAsync(bytes.AsMemory(4), timeout.Token);
        if (FrameCodec.TryDecode(bytes, out Packet packet, out int consumed) != DecodeStatus.Success
            || consumed != bytes.Length || packet.MsgId != MsgRegistry.GetId(typeof(ReconnectResp)) || packet.Seq != 91)
            throw new IOException("Wrong production response frame");
        var response = ReconnectResp.Parser.ParseFrom(packet.Body);
        if (response.Code != expectedCode || response.Data != null)
            throw new InvalidOperationException("Expected a rejection without any business snapshot");
        report = new { passed = true, code = response.Code, hasSnapshot = response.Data != null, protocol };
    }
    else if (action == "config")
    {
        string directory = Owned(args[1]);
        int version = int.Parse(args[2]);
        output = Owned(args[3]);
        var service = new ServerConfigService();
        service.Load(directory, version);
        string value = service.GetGlobal("m1_patch_message");
        if (version != 101 || value != "热更新验证成功")
            throw new InvalidOperationException("Production ServerConfigService returned the wrong patch value");
        report = new { passed = true, version, key = "m1_patch_message", value };
    }
    else throw new ArgumentException("Unknown fixture action");
    if (File.Exists(output)) throw new IOException("Evidence already exists");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS fixture: " + output);
    return 0;
}
catch (Exception error)
{
    // Never print request bodies or credential-bearing arguments.
    Console.Error.WriteLine("Fixture failed: " + error.GetType().Name + ": " + error.Message);
    return 1;
}

```


#### Server/CloudIsle.Server/Hosting/M1ServerHost.cs

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using CloudIsle.Net;
using CloudIsle.Server.Account;
using CloudIsle.Server.Config;
using CloudIsle.Server.Data;
using CloudIsle.Server.Diagnostics;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;

namespace CloudIsle.Server.Hosting;

/// <summary>正式M1组合根的显式选项；不读取旧PoC数据库或使用旧PoC消息。</summary>
public sealed class M1ServerOptions
{
    /// <summary>本次实例的SQLite文件。目录由已有SqliteStore创建。</summary>
    public string DatabasePath { get; init; } = "";
    /// <summary>包含正式config_manifest.json的目录。</summary>
    public string ConfigDirectory { get; init; } = "";
    /// <summary>启动必须精确加载的配置版本。</summary>
    public int ConfigVersion { get; init; } = 100;
    /// <summary>静态HTTP内容根，包含version.json及版本目录。</summary>
    public string ContentRoot { get; init; } = "";
    /// <summary>HTTP监听前缀；本机Native客户端使用localhost避开127代理问题。</summary>
    public string HttpPrefix { get; init; } = "http://localhost:18080/";
    /// <summary>TCP绑定地址。端口0仅供隔离集成测试。</summary>
    public IPEndPoint TcpEndpoint { get; init; } = new(IPAddress.Loopback, 17780);
    /// <summary>本实例拥有的日志目录，不使用全局Serilog静态实例。</summary>
    public string LogDirectory { get; init; } = "";
    /// <summary>正式登录协议必须与此值相等。</summary>
    public int ProtoVersion { get; init; } = 1;
    /// <summary>允许登录的最低安装包版本。</summary>
    public string MinAppVersion { get; init; } = "0.3.0";
    /// <summary>登录最低资源版本；较新的兼容客户端允许通过。</summary>
    public int RequiredResVersion { get; init; } = 100;
    /// <summary>登录最低配置版本；与启动数据的精确ConfigVersion区分。</summary>
    public int RequiredConfigVersion { get; init; } = 100;
    /// <summary>令牌有效期，默认24小时；原文不入日志或数据库。</summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(24);
    /// <summary>正常停机保存预算，耗尽会明确失败而不是假称已保存。</summary>
    public TimeSpan ShutdownSaveBudget { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// 复用正式TCP/账号/会话/令牌/保存/HTTP服务的组合根。每个实例只能Run一次。
/// 生产CLI由后续Program/T60脚本调用本类；集成测试也经同一真实Socket入口。
/// </summary>
public sealed class M1ServerHost
{
    readonly M1ServerOptions _options;
    readonly ConcurrentDictionary<long, ClientSession> _connections = new();
    readonly ConcurrentDictionary<long, Task> _connectionWatches = new();
    readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Action<long, string, string>? _connectionFaultLog;
    TcpServer? _tcp;
    SessionRegistry? _sessions;
    MessageRouter? _router;
    HeartbeatHandler? _heartbeat;
    int _runOnce;

    /// <summary>复制启动选项；路径只在Run时验证，便于CLI报告具体配置错误。</summary>
    /// <param name="options">DB/配置/监听/版本/日志的明确配置。</param>
    public M1ServerHost(M1ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.TcpEndpoint);
        _options = new M1ServerOptions
        {
            DatabasePath = options.DatabasePath, ConfigDirectory = options.ConfigDirectory,
            ConfigVersion = options.ConfigVersion, ContentRoot = options.ContentRoot,
            HttpPrefix = options.HttpPrefix, LogDirectory = options.LogDirectory,
            TcpEndpoint = new IPEndPoint(options.TcpEndpoint.Address, options.TcpEndpoint.Port),
            ProtoVersion = options.ProtoVersion, MinAppVersion = options.MinAppVersion,
            RequiredResVersion = options.RequiredResVersion,
            RequiredConfigVersion = options.RequiredConfigVersion,
            TokenLifetime = options.TokenLifetime, ShutdownSaveBudget = options.ShutdownSaveBudget
        };
    }

    /// <summary>TCP/HTTP均绑定后完成；启动失败则异常完成。</summary>
    public Task Started => _started.Task;
    /// <summary>实际TCP端点（测试端口0时读取系统分配值）。</summary>
    public IPEndPoint? TcpEndpoint => _tcp?.LocalEndpoint;
    /// <summary>实际TCP连接数，不把玩家缓存数充作在线人数。</summary>
    public int ConnectionCount => _tcp?.ConnectionCount ?? 0;
    /// <summary>真实已认证会话数量。</summary>
    public int AuthenticatedPlayerCount => _sessions?.AuthenticatedPlayerCount ?? 0;

    /// <summary>启动并运行全部生产组件；取消会等待关闭和保存，失败会清理后向调用者交付异常。</summary>
    /// <param name="ct">本实例的停止信号。不会影响其他实例或数据库。</param>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _runOnce, 1) != 0)
            throw new InvalidOperationException("M1ServerHost实例只能启动一次");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var logging = new LoggingSetup();
        SqliteStore? store = null;
        PlayerQueue? players = null;
        SaveService? saves = null;
        var tasks = new List<Task>();
        var failures = new List<Exception>();
        Exception? primary = null;
        bool loggingConfigured = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            ValidateOptions();
            var config = new ServerConfigService();
            config.Load(Path.GetFullPath(_options.ConfigDirectory), _options.ConfigVersion);
            logging.Configure(Path.GetFullPath(_options.LogDirectory), "Information");
            loggingConfigured = true;
            _connectionFaultLog = (id, stage, errorType) => logging.Logger.Warning(
                "M1 session cleanup failed sessionId={SessionId} stage={Stage} errorType={ErrorType}",
                id, stage, errorType);
            Action<string> trace = text => logging.Logger.Information("M1 {Trace}", text);

            store = new SqliteStore();
            store.Initialize(Path.GetFullPath(_options.DatabasePath));
            trace($"database schema initialized version={SqliteStore.CurrentVersion}");
            players = new PlayerQueue();
            var playerStore = new PlayerStore(store);
            var cache = new PlayerCache(playerStore);
            _sessions = new SessionRegistry(players, SessionRegistry.DefaultKickBudget, trace);
            saves = new SaveService(players, cache, playerStore);
            _router = new MessageRouter(SendAsync, CloseAsync, new SystemRouterClock(), trace);
            _heartbeat = new HeartbeatHandler(SendAsync);
            _heartbeat.Register(_router);
            var loginOptions = new LoginSessionOptions(
                _options.ProtoVersion, _options.RequiredResVersion, _options.RequiredConfigVersion,
                _options.TokenLifetime, _options.MinAppVersion);
            var accounts = new AccountHandler(
                new AccountRepository(store, new PasswordHasher()), players, cache,
                new TokenService(store), _sessions, FindSession, loginOptions, saves, SendAsync);
            accounts.RegisterRoutes(_router);

            _tcp = new TcpServer(DispatchAsync);
            Task http = new PatchHttpService().RunAsync(
                _options.HttpPrefix, Path.GetFullPath(_options.ContentRoot), lifetime.Token);
            tasks.Add(http);
            // HttpListener绑定在第一个await之前执行；先观察同步启动失败，不留下半个TCP服务。
            if (http.IsCompleted)
                await http.ConfigureAwait(false);
            Task tcp = _tcp.RunAsync(_options.TcpEndpoint, lifetime.Token);
            tasks.Add(tcp);
            Task first = await Task.WhenAny(_tcp.Started, tcp, http).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (!_tcp.Started.IsCompletedSuccessfully || http.IsCompleted)
                throw new InvalidOperationException("M1监听在启动阶段意外结束");
            saves.Start();
            tasks.Add(MetricsReporter.Create(logging, _tcp, _sessions, _router, players).RunAsync(lifetime.Token));
            _started.TrySetResult();
            trace($"server started tcp={_tcp.LocalEndpoint} http={_options.HttpPrefix} config={_options.ConfigVersion}");

            // Only listener/background-service failures own the Host lifetime.
            // An individual peer's cleanup cannot terminate unrelated connections.
            Task ended = await Task.WhenAny(tasks).ConfigureAwait(false);
            await ended.ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                throw new InvalidOperationException("M1后台服务意外结束");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _started.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            primary = ex;
            _started.TrySetException(ex);
        }
        finally
        {
            lifetime.Cancel();
            // 即使一个组件失败，也继续等待其他组件退出和释放监听。
            foreach (Task task in tasks)
                await ObserveShutdownAsync(() => task, failures, true).ConfigureAwait(false);
            await ObserveShutdownAsync(() => Task.WhenAll(_connectionWatches.Values), failures).ConfigureAwait(false);
            _connectionFaultLog = null;
            if (_sessions != null)
                await ObserveShutdownAsync(_sessions.WhenIdleAsync, failures).ConfigureAwait(false);
            if (saves != null)
            {
                await ObserveShutdownAsync(saves.StopAsync, failures).ConfigureAwait(false);
                using var saveBudget = new CancellationTokenSource(_options.ShutdownSaveBudget);
                await ObserveShutdownAsync(() => saves.FlushDirtyAsync(saveBudget.Token), failures).ConfigureAwait(false);
                await ObserveShutdownAsync(() => saves.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            }
            try { players?.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            try { store?.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            _connections.Clear();
            if (loggingConfigured)
                logging.Logger.Information("M1 server stopped; cleanupFailures={Count}", failures.Count);
        }

        if (primary != null)
            ExceptionDispatchInfo.Capture(primary).Throw();
        if (failures.Count > 0)
            throw new AggregateException("M1停机未全部成功", failures);
    }

    /// <summary>在处理第一帧时登记实际连接，并观察关闭；所有业务仍交给已有正式路由。</summary>
    async Task DispatchAsync(ClientSession session, Packet packet, CancellationToken ct)
    {
        if (_connections.TryAdd(session.SessionId, session))
        {
            Task watch = WatchClosedAsync(session);
            _connectionWatches[session.SessionId] = watch;
            _ = watch.ContinueWith(done =>
            {
                _connectionWatches.TryRemove(session.SessionId, out _);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        var context = _sessions!.CreateRequestContext(session, packet.MsgId, packet.Seq);
        await _router!.DispatchAsync(context, packet, ct).ConfigureAwait(false);
    }

    /// <summary>会话完全关闭后移除每连接状态；旧连接不能解绑取代它的新连接。</summary>
    async Task WatchClosedAsync(ClientSession session)
    {
        try { await session.Completion.ConfigureAwait(false); }
        catch (Exception ex) { ReportConnectionFailure(session.SessionId, "completion", ex); }
        finally
        {
            // Each release runs even when another release fails. Keep the failure local.
            CleanupConnectionPart(session.SessionId, "connection", () => _connections.TryRemove(session.SessionId, out _));
            CleanupConnectionPart(session.SessionId, "router", () => _router?.ReleaseSession(session.SessionId));
            CleanupConnectionPart(session.SessionId, "heartbeat", () => _heartbeat?.ReleaseSession(session.SessionId));
        }
        // SessionRegistry owns its identity-checked disconnect watch. Calling
        // UnbindIfCurrent here races that watch and violates its player-queue
        // precondition, turning an ordinary client exit into a Host failure.
    }

    void CleanupConnectionPart(long sessionId, string stage, Action release)
    {
        try { release(); }
        catch (Exception ex) { ReportConnectionFailure(sessionId, stage, ex); }
    }

    void ReportConnectionFailure(long sessionId, string stage, Exception error)
    {
        // Exception messages can contain peer data. Only type and numeric identity are logged.
        try { _connectionFaultLog?.Invoke(sessionId, stage, error.GetType().Name); }
        catch { /* A failed diagnostic sink must not promote a peer failure to a Host failure. */ }
    }

    /// <summary>按连接编号查真实会话；不复用已关闭连接。</summary>
    ClientSession? FindSession(long sessionId) =>
        _connections.TryGetValue(sessionId, out ClientSession? session) && !session.IsCloseRequested ? session : null;

    /// <summary>路由/账号/心跳共用唯一发送边界。</summary>
    Task SendAsync(RequestContext context, Packet packet, CancellationToken ct) =>
        (FindSession(context.SessionId) ?? throw new IOException("会话已关闭")).SendAsync(packet, ct);

    /// <summary>正式解析拒绝达到上限时仅关闭对应会话。</summary>
    Task CloseAsync(RequestContext context, string reason, CancellationToken ct) =>
        FindSession(context.SessionId)?.CloseAsync(reason, ct) ?? Task.CompletedTask;

    /// <summary>只忽略由本实例停止造成的取消；其他退出错误必须被报告。</summary>
    static async Task ObserveShutdownAsync(Func<Task> operation, List<Exception> failures, bool expectedCancellation = false)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (expectedCancellation) { }
        catch (Exception ex) { failures.Add(ex); }
    }

    /// <summary>拒绝空路径和非法监听/期限，不为错误配置创建伪成功目录。</summary>
    void ValidateOptions()
    {
        foreach (string path in new[] { _options.DatabasePath, _options.ConfigDirectory, _options.ContentRoot, _options.LogDirectory })
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("M1路径参数不能为空");
        if (!Directory.Exists(_options.ContentRoot)) throw new DirectoryNotFoundException(_options.ContentRoot);
        if (!Uri.TryCreate(_options.HttpPrefix, UriKind.Absolute, out Uri? prefix) || prefix.Scheme != "http" || prefix.Port <= 0)
            throw new ArgumentException("HTTP监听前缀非法");
        if (_options.ShutdownSaveBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(_options.ShutdownSaveBudget));
    }
}

```


#### Assets/Tests/PlayMode/MouseKeyboardInputTests.cs

```csharp
using System.Collections.Generic;
using Game.Framework.Platform;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace Game.Framework.PlayModeTests
{
    public sealed class MouseKeyboardInputTests
    {
        readonly List<InputDevice> _removed = new List<InputDevice>();
        InputActionAsset _asset;
        MouseKeyboardInput _input;
        Keyboard _keyboard;
        Mouse _mouse;
        InputSettings.UpdateMode _previousUpdateMode;
        InputSettings.BackgroundBehavior _previousBackgroundBehavior;
#if UNITY_EDITOR
        InputSettings.EditorInputBehaviorInPlayMode _previousEditorBehavior;
#endif

        [SetUp]
        public void SetUp()
        {
            // Explicitly own input updates and focus routing in headless TestRunner too.
            _previousUpdateMode = InputSystem.settings.updateMode;
            _previousBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            _previousEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
            var present = new List<InputDevice>();
            foreach (var device in InputSystem.devices)
                present.Add(device);
            for (int i = 0; i < present.Count; i++)
            {
                if (present[i] is Keyboard || present[i] is Mouse)
                {
                    InputSystem.RemoveDevice(present[i]);
                    _removed.Add(present[i]);
                }
            }

            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _asset = CreateAsset();
            _input = new MouseKeyboardInput(_asset);
            _input.Enable();
        }

        [TearDown]
        public void TearDown()
        {
            if (_input != null)
                _input.Disable();
            if (_keyboard != null)
                InputSystem.RemoveDevice(_keyboard);
            if (_mouse != null)
                InputSystem.RemoveDevice(_mouse);
            for (int i = 0; i < _removed.Count; i++)
                InputSystem.AddDevice(_removed[i]);
            _removed.Clear();
            if (_asset != null)
                Object.Destroy(_asset);
            InputSystem.settings.updateMode = _previousUpdateMode;
            InputSystem.settings.backgroundBehavior = _previousBackgroundBehavior;
#if UNITY_EDITOR
            InputSystem.settings.editorInputBehaviorInPlayMode = _previousEditorBehavior;
#endif
        }

        [Test]
        public void Move_LogsNormalizedVector()
        {
            LogAssert.Expect(LogType.Log, "[Input] move=(0,1)");
            SetKeyboard(Key.W);
            _input.Tick();

            Vector2 move = _input.ReadMove();
            Assert.That(move.y, Is.GreaterThan(0f));
            Assert.That(move.magnitude, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void Tap_LogsWithoutDrag()
        {
            int drags = 0;
            _input.DragStarted += _ => drags++;
            _input.Dragged += _ => drags++;
            _input.DragEnded += _ => drags++;

            SetMouse(new Vector2(5f, 5f), true, 0f);
            _input.Tick();
            Assert.That(_input.WasTapPressed(), Is.False);

            LogAssert.Expect(LogType.Log, "[Input] tap=(6,6)");
            SetMouse(new Vector2(6f, 6f), false, 0f);
            _input.Tick();

            Assert.That(_input.WasTapPressed(), Is.True);
            Assert.That(drags, Is.EqualTo(0));
        }

        [Test]
        public void Drag_LogsThreePhases()
        {
            var phases = new List<string>();
            _input.DragStarted += _ => phases.Add("start");
            _input.Dragged += _ => phases.Add("drag");
            _input.DragEnded += _ => phases.Add("end");

            SetMouse(new Vector2(0f, 0f), true, 0f);
            _input.Tick();
            Assert.That(phases, Is.Empty);
            Assert.That(_input.WasTapPressed(), Is.False);

            LogAssert.Expect(LogType.Log, "[Input] drag-start=(0,0)");
            LogAssert.Expect(LogType.Log, "[Input] drag=(20,0)");
            SetMouse(new Vector2(20f, 0f), true, 0f);
            _input.Tick();
            Assert.That(_input.WasTapPressed(), Is.False);

            LogAssert.Expect(LogType.Log, "[Input] drag-end=(20,0)");
            SetMouse(new Vector2(20f, 0f), false, 0f);
            _input.Tick();

            Assert.That(phases, Is.EqualTo(new[] { "start", "drag", "end" }));
            Assert.That(_input.WasTapPressed(), Is.False);
        }

        [Test]
        public void Zoom_LogsScroll()
        {
            LogAssert.Expect(LogType.Log, "[Input] zoom=2.5");
            SetMouse(new Vector2(0f, 0f), false, 2.5f);
            _input.Tick();
            Assert.That(_input.ReadZoom(), Is.EqualTo(2.5f));
        }

        [Test]
        public void Disable_SuppressesOutput()
        {
            var lines = new List<string>();
            Application.LogCallback handler = (condition, stack, type) =>
            {
                if (condition.StartsWith("[Input]"))
                    lines.Add(condition);
            };
            Application.logMessageReceived += handler;
            try
            {
                _input.Disable();
                int drags = 0;
                _input.DragStarted += _ => drags++;
                SetKeyboard(Key.W);
                _input.Tick();
                Assert.That(lines, Is.Empty);
                Assert.That(_input.ReadMove(), Is.EqualTo(Vector2.zero));
                Assert.That(drags, Is.EqualTo(0));
            }
            finally
            {
                Application.logMessageReceived -= handler;
            }
        }

        static InputActionAsset CreateAsset()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            InputActionMap map = asset.AddActionMap("MouseKeyboard");
            map.AddAction("Move", InputActionType.Value)
                .AddCompositeBinding("Dpad")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            map.AddAction("Point", InputActionType.Value)
                .AddBinding("<Mouse>/position");
            map.AddAction("Press", InputActionType.Button)
                .AddBinding("<Mouse>/leftButton");
            map.AddAction("Zoom", InputActionType.Value)
                .AddBinding("<Mouse>/scroll/y");
            return asset;
        }

        void SetKeyboard(Key key)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
            InputSystem.Update();
        }

        void SetMouse(Vector2 position, bool leftDown, float scrollY)
        {
            MouseState state = new MouseState
            {
                position = position,
                scroll = new Vector2(0f, scrollY)
            };
            if (leftDown)
                state = state.WithButton(MouseButton.Left);
            InputSystem.QueueStateEvent(_mouse, state);
            InputSystem.Update();
        }
    }
}

```


#### Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Finalize-Acceptance.py

```python
"""Reconcile the 58 existing requirements with actual reports and final R2 artifacts.

No test is executed here. Assertions check recorded results, business state, source
fingerprints and credentials in owned logs before writing the acceptance record.
"""
from pathlib import Path
import collections
import datetime
import hashlib
import json
import sqlite3
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
EVIDENCE = HERE.parent
NATIVE = HERE / "Native-20261021-141638-780"
RECOVERY = HERE / "Recovery-20261021-141811-730"
BAD = HERE / "BadResource-20261021-141811-409"
PROTOCOL = HERE / "Protocol-20261021-141823-622"
NEGATIVE = HERE / "AotNegative-R2-20261021-141623-394"
BASE = EVIDENCE / "Root Final 100 R2"
PUBLISH = EVIDENCE / "Root Final Publish R2"
SERVER = HERE / "ServerTests/root-server-final225.trx"
C1 = EVIDENCE / "StageC1/Root-NarrowFix/Run-20261020-172926/playmode-results.xml"
C2 = EVIDENCE / "StageC2/Root-NarrowFix/Run-20261020-220350/playmode-results.xml"
PLAY = HERE / "PlayMode-Final/results.xml"
FOUNDATION = HERE / "Foundation-Final/results.xml"
INPUT = HERE / "Input-Fixed/results.xml"


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.file_digest(Path(path).open("rb"), "sha256").hexdigest()


def passed(path):
    result = read(path)
    assert result["passed"] is True, str(path)
    return result


def unity_report(path, count):
    result = ET.parse(path).getroot()
    assert result.get("result") == "Passed", str(path)
    assert int(result.get("total")) == count, str(path)
    assert int(result.get("failed")) == int(result.get("skipped")) == 0
    cases = result.findall(".//test-case")
    assert len(cases) == count and all(x.get("result") == "Passed" for x in cases)
    return cases


def model(state):
    return json.loads(state["luaModel"])


def require(state, **expected):
    for key, value in expected.items():
        assert state[key] == value, (key, state.get(key), value)


# Read actual results, never infer pass from process exit alone.
native = passed(NATIVE / "result.json")
recovery = passed(RECOVERY / "result.json")
bad = passed(BAD / "result.json")
protocol = passed(PROTOCOL / "result.json")
negative = passed(NEGATIVE / "result.json")
build = passed(HERE / "r2-build-result.json")
cli = passed(HERE / "CliNegative/result.json")
server_config = passed(HERE / "server-config101-read.json")
for name in ("r2-client-build", "r2-config101", "r2-patch-build", "r2-restore-config100"):
    assert (HERE / (name + ".exit.txt")).read_text().strip() == "0"
unity_report(C1, 27)
unity_report(C2, 13)
unity_report(PLAY, 9)
foundation_cases = unity_report(FOUNDATION, 69)
unity_report(INPUT, 7)
trx = ET.parse(SERVER).getroot()
counters = trx.find(".//{*}Counters")
assert counters.get("total") == counters.get("passed") == "225"
assert counters.get("failed") == counters.get("notExecuted") == "0"
server_cases = trx.findall(".//{*}UnitTestResult")
assert len(server_cases) == 225 and all(x.get("outcome") == "Passed" for x in server_cases)
server_names = {x.get("testName") for x in server_cases}
for name in ("OnePeerAbruptExit_KeepsOtherAuthenticatedPeerAndListenersAlive",
             "AuthenticatedPeerExit_KeepsHostListeningAndAcceptsNextLogin",
             "DatabaseReadOnly_KeepsDirty_ThenWritableRetryPersists"):
    assert name in server_names

initial = read(NATIVE / "A02-B02-login100.json")
registered = read(NATIVE / "A03-register-not-authorized.json")
s1 = read(NATIVE / "A04-S1-home100.json")
logout = read(NATIVE / "A06-logout.json")
require(initial, resVersion="100", patchModule="False", winPatch="False", globalPatch="", winLogin="True")
require(registered, auth="False", hasToken="False", registerMessage="注册成功，请登录")
require(logout, auth="False", hasToken="False", winLogin="True", winHome="False")
assert model(logout)["info"]["playerId"] == "0"
for name in ("A07-login-again", "A10-reopen-same-install", "B07-home101", "B10-cached-home"):
    assert model(read(NATIVE / (name + ".json"))) == model(s1)
updated = read(NATIVE / "B08-real-prefab-and-global.json")
require(updated, resVersion="101", winPatch="True", patchModule="True", globalPatch="热更新验证成功")
assert [int(native[k]) for k in ("initialDownloadFiles", "updateDownloadFiles", "cachedDownloadFiles")] == [42, 5, 0]
before, after = read(NATIVE / "install-before.json"), read(NATIVE / "install-after.json")
assert before == after
installed = {"exe": BASE / "Player/Farming.exe", "gameAssembly": BASE / "Player/GameAssembly.dll",
             "metadata": BASE / "Player/Farming_Data/il2cpp_data/Metadata/global-metadata.dat"}
assert all(sha(path) == before[key].lower() for key, path in installed.items())

# Verify the complete frozen source set is still exactly the recorded bytes.
fingerprint = read(BASE / "baseline-fingerprint.json")
assert fingerprint["schema"] == "m1-baseline-fingerprint-v2"
files = fingerprint["files"]
assert len(files) == fingerprint["count"] == len({x["path"] for x in files}) == 3342
assert all(sha(ROOT / x["path"]) == x["sha256"].lower() for x in files)
assert "Assets/Framework/Lua/XLua/Plugins/x86_64/xlua.dll" in {x["path"] for x in files}
assert "Packages/packages-lock.json" in {x["path"] for x in files}
for version, directory in ((100, BASE / "Publish/100"), (101, PUBLISH / "101")):
    report = (directory / f"DefaultPackage_{version}.report").read_text(encoding="utf-8-sig")
    assert ("PatchDemoWindow" in report) == (version == 101)
    for bundle in directory.glob("*.bundle"):
        assert bundle.stat().st_size > 8

# Read-only SQL confirmation; report only counts and public business data.
with sqlite3.connect((NATIVE / "players.db").as_uri() + "?mode=ro", uri=True) as db:
    db_counts = {table: db.execute("SELECT COUNT(*) FROM " + table).fetchone()[0]
                 for table in ("account", "player")}
assert db_counts == {"account": 1, "player": 1}
database = read(NATIVE / "A05-A08-database-snapshot.json")["players"][0]
info = model(s1)["info"]
for sql_key, lua_key in (("id", "playerId"), ("nickname", "nickname"), ("level", "level"),
                         ("exp", "exp"), ("gold", "gold"), ("gem", "gem"), ("ticket", "ticket"),
                         ("shard", "shard"), ("bag_slots", "bagSlots"), ("guide_step", "guideStep")):
    assert str(database[sql_key]) == str(info[lua_key])
assert recovery["committedRevision"] == 1 and recovery["fullSnapshotAndTokenPreserved"]
assert recovery["nativeBackoff"][:6] == [1, 2, 4, 8, 16, 30]
r1 = read(RECOVERY / "C01-R1.json")
for name in ("C04-restored", "C05-R2", "C07-restored-R2", "C10-relogin"):
    assert model(read(RECOVERY / (name + ".json"))) == model(r1)
assert model(read(RECOVERY / "second-client/C11-B-current.json")) == model(r1)
expired = read(RECOVERY / "C09-expired-return-login.json")
require(expired, lastBiz="204", auth="False", hasToken="False", winLogin="True", reconnectRunning="False")
assert model(expired)["info"]["playerId"] == "0"
require(read(RECOVERY / "C11-A-kicked.json"), auth="False", hasToken="False", winLogin="True", reconnectRunning="False")
assert read(RECOVERY / "C12-old-token-rejection.json")["hasSnapshot"] is False
assert protocol["responseCode"] == 106 and protocol["reconnectRunning"] is False
error = read(BAD / "B12-visible-error-no-activation.json")
require(error, entry="False", committed="False", winLogin="False")
assert bad["attempts"] == ["1", "2"] and bad["installationUnchanged"]
assert negative["playerUnchanged"] and negative["noRejectedPointer"] and negative["publishedPointerUnchanged"]
duplicate = EVIDENCE / "StageE/Root-ProtoDuplicate/Run-20261020-223154-240/result.json"
dup = passed(duplicate)
assert sha(ROOT / "Tools/gen_proto.ps1").upper() == dup["sourceScriptSha256"].upper()
assert dup["lastSuccessfulGeneratedUnchanged"]

# Both current export copies must match byte-for-byte, and actual 101 readers agree.
def data_hashes(directory):
    return {p.name: sha(p) for p in directory.glob("*.json")}

config_hashes = data_hashes(ROOT / "Server/ConfigLuban")
assert config_hashes and config_hashes == data_hashes(ROOT / "Assets/ConfigLuban")
assert config_hashes == data_hashes(ROOT / "Assets/StreamingAssets/ConfigLuban")
assert server_config["version"] == 101 and server_config["value"] == updated["globalPatch"]

# Check owned logs against actual known private inputs, without revealing those inputs.
secrets = set()
logs = []
for run in (NATIVE, RECOVERY, BAD, PROTOCOL):
    for private in run.rglob("creds.private"):
        lines = private.read_text(encoding="utf-8-sig").splitlines()
        if len(lines) >= 2:
            secrets.add(lines[1])
    for token in list(run.rglob("m1-token.txt")) + list(run.rglob("old-token.private")):
        value = token.read_text(encoding="utf-8-sig").strip()
        if value:
            secrets.add(value)
    logs.extend(p for p in run.rglob("*") if p.is_file() and p.suffix in (".log", ".stdout", ".stderr"))
for log in logs:
    text = log.read_text(encoding="utf-8-sig", errors="replace")
    assert not any(secret in text for secret in secrets), "Private value found in " + str(log)
client_log = NATIVE / "client/client-logs/client.log"
assert client_log.stat().st_size > 0
metric_log = NATIVE / "server-100-account/server.log"
assert "metrics connections=" in metric_log.read_text(encoding="utf-8-sig")

audit = {"passed": True, "date": "2026-10-21", "frozenFiles": 3342, "installedSha256": before,
         "serverTests": 225, "affectedPlayMode": 9, "foundationEditMode": 69, "inputPlayMode": 7,
         "reusedC1PlayMode": 27, "reusedC2PlayMode": 13, "databaseCounts": db_counts,
         "knownPrivateValuesChecked": len(secrets), "ownedLogsChecked": len(logs), "privateValueMatches": 0,
         "matchingCurrentConfigJsonCount": len(config_hashes), "config101Value": server_config["value"]}
(HERE / "final-audit.json").write_text(json.dumps(audit, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

mapping = {}


def item(key, actual, *evidence):
    assert key not in mapping
    for path in evidence:
        assert Path(path).exists(), str(path)
    mapping[key] = (actual, [Path(x).relative_to(ROOT).as_posix() for x in evidence])


actions = NATIVE / "actions.jsonl"
item("A01", "新隔离DB初始化schema2；正式TCP/HTTP就绪；最终停机cleanupFailures=0。", actions, metric_log)
item("A02", "最终R2标准IL2CPP100进入真实Login，entry/committed=True。", NATIVE / "A02-B02-login100.json", NATIVE / "client/shots/100_login.png")
item("A03", "真实按钮注册成功；仍未认证且无Token；独立DB account/player各1行。", NATIVE / "A03-register-not-authorized.json", HERE / "final-audit.json")
item("A04", "真实Lua登录/模型应用/Token提交/家园HUD一致：ID1、FinalNick、Lv1。", NATIVE / "A04-S1-home100.json", NATIVE / "client/shots/100_home.png")
item("A05", "S1完整Lua业务模型已保存；公开业务字段逐项与只读DB快照一致。", NATIVE / "A04-S1-home100.json", NATIVE / "A05-A08-database-snapshot.json")
item("A06", "真实LogoutReq1009被处理；返回Login、无Token、模型playerId0；服务端登出撤销/保存测试通过。", NATIVE / "A06-logout.json", metric_log, SERVER)
item("A07", "再次登录完整业务模型等于S1。", NATIVE / "A07-login-again.json")
item("A08", "命令回执后正常退出；拥有进程exit0、日志保留可读。", actions)
item("A09", "同安装同缓存的新进程重新进入Login后再登录。", actions, NATIVE / "A10-reopen-same-install.json")
item("A10", "重开后登录的完整Lua业务模型与S1相等。", NATIVE / "A10-reopen-same-install.json", HERE / "final-audit.json")
item("A11", "PlayerPersistenceTests实际修改后保存重载、只读失败恢复均通过；Native恢复另验证非默认gold201/bag1×7/revision1。", SERVER, RECOVERY / "prepared-saved-state.json")
item("B01", "最终R2安装三个SHA实际计算，标准Win64 IL2CPP/App0.3/Proto1。", NATIVE / "install-before.json", BASE / "baseline-manifest.json")
item("B02", "100无新模块/Global/窗口；实际100 Yoo报告无PatchDemoWindow。", NATIVE / "A02-B02-login100.json", BASE / "Publish/100/DefaultPackage_100.report")
item("B03", "100进程完全退出，后续101启动为新进程/新Lua环境。", actions)
item("B04", "正式补丁脚本exit0；101新增Lua/Prefab/Global；复用R2安装。", HERE / "r2-patch-build.exit.txt", PUBLISH / "101/DefaultPackage_101.report", NATIVE / "B08-real-prefab-and-global.json")
item("B05", "完整101目录先复制到自有CDN，所有flat文件复制完成后才切指针。", HERE / "Run-NativeAccountsAndPatch.ps1", actions)
item("B06", "原子换version.json为101；100目录保留；ServerConfig101加载成功。", actions, NATIVE / "B06-updated101.json")
item("B07", "原路径/同缓存启动101，实际差异下载5文件，少于100初装42文件。", NATIVE / "B06-updated101.json", NATIVE / "result.json")
item("B08", "真实UiBridge异步打开新Prefab；实际Global为热更新验证成功；Root已查看完整帧。", NATIVE / "B08-real-prefab-and-global.json", NATIVE / "client/shots/101_patch_window.png")
item("B09", "exe/GameAssembly/global-metadata更新前后逐一相同。", NATIVE / "install-before.json", NATIVE / "install-after.json")
item("B10", "同缓存再次启动101下载0文件，登录后新Prefab仍可打开。", NATIVE / "B10-cached101.json", NATIVE / "B10-cached-home.json", actions)
item("B11", "仅截断自有CDN的实际bundle副本，新缓存隔离；原清单保留。", BAD / "result.json", HERE / "Run-NativeBadResource.ps1")
item("B12", "实际可见更新失败/重试；Lua未启动、候选未提交。", BAD / "B12-visible-error-no-activation.json", BAD / "client/shots/download_error.png")
item("B13", "恢复原bundle字节并核对原SHA256。", BAD / "result.json", BAD / "actions.jsonl")
item("B14", "点击生产Retry按钮后进入Login；仅prepare尝试1、2，无双流水线。", BAD / "B14-retry-success.json", BAD / "result.json", BAD / "client/shots/retry_login.png")
item("C01", "production PlayerStore.Save/reopen产生revision1非默认快照，Native读取gold201/bag1×7作为R1。", RECOVERY / "prepared-saved-state.json", RECOVERY / "C01-R1.json", RECOVERY / "C01-R1-database.json")
item("C02", "关闭自有测试通路后真实遮罩出现；全屏raycastTarget按源码阻止输入，Root已查看实际遮罩。", RECOVERY / "C02-mask.json", RECOVERY / "client/shots/reconnect_mask.png", ROOT / "Assets/Framework/Account/DisconnectOverlay.cs")
item("C03", "实际观察75秒以上，退避1/2/4/8/16/30，后续30；单循环尝试序列。", RECOVERY / "C03-real-backoff.json", RECOVERY / "actions.jsonl")
item("C04", "原端口/原DB恢复，无密码命令，原Token与完整Lua模型保持，Home恢复且遮罩消失。", RECOVERY / "C04-restored.json", RECOVERY / "result.json", RECOVERY / "client/shots/restored_home.png")
item("C05", "强杀前只读DB确认SavedRevision1与实际snapshot摘要。", RECOVERY / "C05-R2.json", RECOVERY / "C05-R2-database.json")
item("C06", "核对exe/父PID/精确DB后仅强杀自有dotnet，客户端进入重连。", RECOVERY / "actions.jsonl", HERE / "Run-NativeRecovery.ps1")
item("C07", "同DB/配置/端口重启，原Token自动恢复，完整业务模型等于R2。", RECOVERY / "C07-restored-R2.json", RECOVERY / "result.json")
item("C08", "隔离DB真实事务将1条当前Token到期时间设为0；报告不含Token。", RECOVERY / "C08-expired-records.json")
item("C09", "真实204已返回Login，Token/模型清空，重连停止；已复验本轮修复。", RECOVERY / "C09-expired-return-login.json", PLAY)
item("C10", "通过真实Login按钮重新获得认证，完整快照保持R2。", RECOVERY / "C10-relogin.json")
item("C11", "第二个Native同账号登录，A清Token/模型回Login且不抢回，B保持Home。", RECOVERY / "C11-A-kicked.json", RECOVERY / "second-client/C11-B-current.json", RECOVERY / "client/shots/kicked_login.png")
item("C12", "用A旧Token实际TCP请求返回204且无快照；B仍认证，未被抢回。", RECOVERY / "C12-old-token-rejection.json", RECOVERY / "actions.jsonl")
item("C13", "实际Proto1客户端对Proto2服务端Login返回106，UI显示版本不兼容请更新且无重连。", PROTOCOL / "result.json", PROTOCOL / "C13-version-mismatch.json", PROTOCOL / "client/shots/version_update_required.png")
item("D01", "带空格Root Final 100 R2目录完整gen_proto/gen_config/GenAll/Yoo/IL2CPP构建exit0。", HERE / "r2-client-build.exit.txt", HERE / "r2-client-build.stdout", BASE / "baseline-manifest.json")
item("D02", "正式build_patch基于R2完整指纹exit0输出独立101；安装三SHA保持。", HERE / "r2-patch-build.exit.txt", HERE / "r2-patch-build.stdout", NATIVE / "install-after.json")
item("D03", "正式run_server指定独立DB/配置/端口，真实TCP/HTTP/metrics可用，正常停机cleanupFailures0。", actions, metric_log, SERVER)
item("D04", "dotnet test实际225/225，失败0、跳过0；新增并存连接RST隔离测试通过。", SERVER, HERE / "ServerTests/r2-exit.txt")
item("D05", "当前PlayMode的UIResource两个实际用例均通过，开关和场景取消后资源回基线。", PLAY)
item("D06", "不存在Unity路径实际exit1，明确缺失路径，未创建成功输出目录。", HERE / "CliNegative/result.json", HERE / "CliNegative/stderr.txt")
item("D07", "真实protoc生成后重复ID校验exit1；最后成功的5个生成文件SHA不变，当前主脚本SHA仍相同。", duplicate)
item("D08", "真实build_patch拒绝固定AccountSession差异；源码逐字节恢复、Player和原发布指针不变、无错误101指针。", NEGATIVE / "result.json", NEGATIVE / "Attempt/patchbuild-report.txt", HERE / "final-audit.json")
item("E01", "当前225内FrameCodec11/PipelineReader11/MessageRouter11覆盖半包、粘包、超长、未知ID、坏protobuf等。", SERVER)
item("E02", "当前69项基础回归内Pending/生命周期/传输超时、取消、旧代次收尾等全部通过。", FOUNDATION)
item("E03", "当前OpIdStore8项覆盖重启持续递增、失败不发号、不同玩家、并发与边界。", FOUNDATION)
item("E04", "当前Idempotency11项覆盖重复执行一次、不同负载拒绝、64项淘汰与TTL等。", SERVER)
item("E05", "复用同一UiManager/资源实现的C1最终27项：真实Lua/Prefab20次开关，生命周期/订阅/资源回基线；当前UIResource再通过。", C1, PLAY)
item("E06", "C1真实SceneService10次切换、取消/失败回Login/Loading与常驻Root通过；当前冷启动和Native场景链再通过。", C1, PLAY, NATIVE / "A04-S1-home100.json")
item("E07", "C1三分辨率与非零safearea测试及4张实际UI截图已Root查看；相关布局代码未改变。", C1, EVIDENCE / "StageC1/Review/shots/shot-1280x720.png", EVIDENCE / "StageC1/Review/shots/shot-1920x1080.png", EVIDENCE / "StageC1/Review/shots/shot-1080x1920.png", EVIDENCE / "StageC1/Review/shots/shot-safearea-nonzero.png")
item("E08", "当前事件/对象池/计时器12项通过，含退订、重复归还、取消和非缩放计时。", FOUNDATION)
item("E09", "当前真实InputSystem设备5项和EventSystem模态点击2项全部通过；移动/点击/拖拽/滚轮与遮挡覆盖。", INPUT)
item("E10", "Native正式滚动client.log及服务端metrics保留；实际请求有session/msg/seq关联；FileLogger滚动/脱敏和Diagnostics测试通过；实际私密值扫描0匹配。", client_log, metric_log, FOUNDATION, SERVER, HERE / "final-audit.json")
item("E11", "当前真实SQLITE_READONLY8失败保持Dirty，恢复可写后重试持久化成功。", SERVER)
item("E12", "真实版本化xlsx导101；当前双端JSON逐文件SHA一致；Native与正式ServerConfigService均读取新值；错误清单/版本/必需键测试通过。", HERE / "r2-config101.stdout", HERE / "server-config101-read.json", NATIVE / "B08-real-prefab-and-global.json", SERVER, HERE / "final-audit.json")

matrix_path = EVIDENCE / "StageE/AcceptanceMatrix.json"
matrix = read(matrix_path)
assert len(matrix["steps"]) == len(mapping) == 58
if not (HERE / "AcceptanceMatrix.before-final.json").exists():
    (HERE / "AcceptanceMatrix.before-final.json").write_bytes(matrix_path.read_bytes())
for step in matrix["steps"]:
    actual, evidence = mapping[step["id"]]
    step.update(status="pass", actual=actual, evidence=evidence)
matrix.update(status="pass", reviewedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
              sourceSha256AtFinalReview=sha(ROOT / "Assets/ReferenceDoc/06_M1_基础框架任务拆解.md"),
              candidate="Root Final 100 R2 + Root Final Publish R2", passed=58, failed=0, pending=0,
              audit="Docs/Acceptance/M1/20261020-xLua/Root-Finalization/final-audit.json")
matrix_path.write_text(json.dumps(matrix, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def link(path, label=None):
    absolute = Path(path).resolve()
    return f"[{label or absolute.name}](<{absolute.as_posix()}>)"


lines = ["# M1 最终验收记录", "", "日期：2026-10-21。范围：Windows x64 标准 IL2CPP / xLua；服务端 .NET 8。", "",
         "**结论：A01–A11、B01–B14、C01–C13、D01–D08、E01–E12，共58项通过。T01–T68完成。**", "",
         "## 1. 唯一最终候选", "", "- AppVersion 0.3.0 / ProtoVersion 1 / bridge m1-bridge-0.3；资源与配置100→101。",
         "- " + link(BASE / "Player/Farming.exe", "100安装程序"),
         "- " + link(BASE / "baseline-manifest.json", "基包清单") + "；" + link(BASE / "baseline-fingerprint.json", "3342文件冻结指纹"),
         "- " + link(PUBLISH, "101发布目录") + "；" + link(HERE / "final-audit.json", "独立核对结果"),
         "- 旧候选和失败证据保留作诊断；最终运行及新旧资源验证均使用上述R2安装。", "",
         "| 安装文件 | 更新前后相同的SHA-256 |", "|---|---|"]
for key, value in before.items():
    lines.append(f"| {key} | `{value.lower()}` |")
lines += ["", "## 2. 实测结果", "", "| 验证 | 结果 |", "|---|---|",
          "| 原生账号/100→101/缓存复用 | 通过，实际下载42→5→0，安装三SHA不变 |",
          "| 原生损坏资源→修复→生产按钮重试 | 通过，仅prepare1、2，无坏候选激活 |",
          "| 原生恢复 | 完整75秒退避；同库恢复/强杀恢复；revision1；204清模型回登录；双客户端顶号；旧Token204 |",
          "| 原生106 | 显示版本不兼容请更新，无无限重连 |",
          "| 服务端NUnit | 225/225，0失败、0跳过；含不同账号并存的RST隔离 |",
          "| 当前受影响PlayMode | 9/9，0失败、0跳过 |",
          "| 当前基础EditMode | 69/69，0失败、0跳过 |",
          "| 当前输入/模态PlayMode | 7/7，0失败、0跳过 |",
          "| 复用未变更的UI/场景及账号事务证据 | C1 27/27，C2 13/13；不重复计入上述当前测试总数 |", "",
          "## 3. 58项逐条证据", "", "| 编号 | 实际结果 | 证据 |", "|---|---|---|"]
for step in matrix["steps"]:
    refs = "；".join(link(ROOT / p) for p in step["evidence"])
    lines.append(f"| {step['id']} | 通过：{step['actual']} | {refs} |")
lines += ["", "## 4. 重现入口", "", "以下脚本包含具体参数、显式超时与拥有进程清理；凭证只存私有测试文件，不写日志或命令行。",
          "构建脚本拒绝覆盖已存在候选，若重新构建应先选择新的输出目录，保留当前冻结证据。", ""]
for name in ("Build-FinalCandidate.ps1", "Run-NativeAccountsAndPatch.ps1", "Run-NativeBadResource.ps1", "Run-NativeRecovery.ps1", "Run-NativeProtocolMismatch.ps1", "Run-FinalAotNegative.ps1", "Run-FinalFoundation.ps1", "Finalize-Acceptance.py"):
    lines.append("- " + link(HERE / name))
lines += ["", "## 5. 问题修复、证据适用范围", "",
          "- 204/106终止分支曾取消自身重连CTS，导致Lua终止通知丢失；修复前真实用例失败，修复后9项回归及R2 Native过期流程通过。",
          "- Host曾重复解绑并将单连接观察错误升级为停机；现Registry唯一清理账号绑定，Host逐项清理连接/路由/心跳并记录非敏感错误。不同账号并存RST测试及225项回归通过。监听/配置/后台服务失败仍保持全局失败退出。",
          "- catalog的CRLF匹配、完整Lua模型编码、Overlay截图、回执消费/跨重启去重等问题已修复，旧失败目录保留。",
          "- Native强杀恢复只承诺最后已提交的revision1；非默认数据由离线夹具调用生产PlayerStore.Save/reopen产生，没有新增运行时管理接口。",
          "- Windows x64为本次验收范围；Android/iOS、后续种植等正式玩法属于后续工作。",
          "- Unity日志保留引擎退出时的分配诊断及许可连接重试信息，未据测试通过宣称全部引擎日志零警告。",
          "- 历史HybridCLR实测原文保留迁移说明；当前结果仅依据xLua实际产物，未替换历史技术名称。", "",
          "## 6. 实际画面", "", "100家园：", "", "![](<" + (NATIVE / "client/shots/100_home.png").as_posix() + ">)", "",
          "同安装更新101后的真实新面板与Global：", "", "![](<" + (NATIVE / "client/shots/101_patch_window.png").as_posix() + ">)", ""]
(EVIDENCE / "M1_Acceptance.md").write_text("\n".join(lines), encoding="utf-8")
print(json.dumps({"passed": 58, "failed": 0, "pending": 0, "sourceFingerprintFiles": 3342,
                  "serverTests": 225, "foundation": 69, "input": 7, "affectedPlayMode": 9}, ensure_ascii=False))

```
