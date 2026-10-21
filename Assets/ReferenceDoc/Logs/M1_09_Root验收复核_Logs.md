# M1 Root 验收复核日志

日期：2026-10-21。范围：复核 T57–T63 的真实证据，关闭构建/发布缺口，继续 T64–T68。用户已授权持续使用本地 Cursor 执行；电量检查保持暂停。历史记录保留原文，以下记录当前实施与复核结论。

## 1. 任务介绍

上一轮 Cursor 已生成标准 IL2CPP 100 候选与 101 补丁，并报告 T57–T63 完成。先读最终生产代码与实测产物，确认它们确实满足任务的验证要求，再进入账号存档、同安装更新、网络恢复和命令行脚本的完整验收。发现的问题先修复，再重跑受影响流程；未变化的服务端与基础组件证据准确引用。所有必需项目通过后才能填写最终 M1 验收记录。

## 2. 最小执行清单与具体命令

- [x] 使用 `PoC/xLuaMigration/.coordinator/Get-Status.ps1 -Stage m1dcontinue` 读取 Cursor 结果摘要，确认进程退出码为 0；不打印私有推理或原始凭证。
- [x] 读取 `Assets/Editor/Build/{ClientBuild,PatchBuild}.cs`、`Tools/{build_client_win,build_patch}.ps1`、`Assets/Framework/Boot/{Launcher,LauncherBootBehaviour}.cs` 与 StageD Native 观测证据。
- [x] 将具体问题和继续实施要求写入 `Docs/Acceptance/M1/20261020-xLua/StageD-RootClosure-ToE.md`，要求实际修复后直接推进 StageE，不再次请求计划确认。
- [x] 使用 `agent.ps1 --resume 83073751-316c-418f-91f5-8c8a406dad1b -p --auto-review --trust --output-format stream-json <提示正文>` 继续同一 Cursor 会话；只运行一个 Cursor。`--resume` 延续上下文；`-p` 为非交互执行；`--auto-review` 保留自动审批；`stream-json` 写入私有状态文件，由安全摘要脚本读取。
- [x] 独立按原字节计算 M1_06 前 14515 字节 SHA-256，核对历史原文未变；计算 `Tools/gen_proto.ps1` SHA，确认先前隔离重复 ID 负例适用。
- [ ] 审查最终完整 AOT/Proto/native/schema/依赖指纹、路径保护与不可变发布的实际失败证据。
- [ ] 核对真实带空格输出的完整构建，验证最终 100 的 Lua 登录、HomeHUD、断线恢复与退出清理；重新记录三个安装文件 SHA。
- [ ] 核对 100 不含新模块/Prefab/Global，101 包含三者，并在同一安装与缓存完成更新、缓存复用、坏资源失败及真实按钮重试。
- [ ] 逐条复核 `StageE/AcceptanceMatrix.json` 全部 58 项，包括真实完整退避、同 DB 服务端强杀重启、Token 失效与顶号。
- [ ] 检查真实截图、测试 XML/TRX 的实际用例数、失败/跳过与控制台，填最终验收记录和任务状态。

源码修改、函数与参数说明、完整命令及修复后的验证证据由本轮 Cursor 写入 M1_07；本日志记录 Root 独立复核，避免并行改写同一日志。

## 3. 验证步骤、预期与实际

| 名称 | 具体步骤 | 预期结果 | 实际结果 |
|---|---|---|---|
| Cursor 退出 | 查询 m1dcontinue 状态及执行会话 | 有真实结果与退出码 | 已退出 0；退出成功本身不等于任务验收通过 |
| Native 登录完整性 | 对照 AcceptLoginAsync 与观测日志 | 实际 Lua apply/模型/HUD/场景/Commit 一致 | 旧驱动直接 C# 登录及 Commit；旧日志只证明部分链路，完整 T57 暂不接受，已交修复 |
| 正式启动失败重试 | 读 Launcher 的补丁准备与 Host 错误分支 | 显示进度、可见错误、真实重试且无双下载 | 旧代码 progress=null、仅写异常，正式链路缺失，已交修复 |
| 补丁兼容性 | 对照 baseline 写入与 ValidateBaseline | 完整结构化指纹、缺失/新增/修改均拒绝 | 旧清单四文件，新增两项因缺键跳过，不满足门槛，已交修复 |
| 发布失败保护 | 读 staging→版本目录→指针顺序 | 失败不破坏已发布目录及指针；不能写 Player | 旧目录替换仍有空窗、Player 只有报告文字，已交修复 |
| 历史原文保护 | 读字节、取前14515字节、SHA256 | `7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328` | 通过，原文前缀逐次保持 |
| 协议负例适用性 | 计算实际主脚本 SHA | 与 Root D07 实测源一致 | 通过，`B5E0230B68F9C392921769E7B9DD38807089A3FF74346F2AB0C6CFF5A1E3A702`，当前可复用 D07 |
| 完整 M1 | 复核58项与实际产物/截图 | 全部必需项目通过 | 进行中，当前未宣称 M1 完成 |

## 4. 遇到的问题与解决方法

| 发生情况与问题 | 具体原因 | 解决方法 | 复验 |
|---|---|---|---|
| Cursor 报告 Native 通过但不能证明 Home 完整流程 | 验收驱动绕过 Lua/UI 直接 C# Commit；快照只检查字段存在 | 改用真实 LoginWindow 或同一 Lua 窗口流程，记录完整脱敏业务快照、Lua 模型和 HUD；修复后重建100 | 待本轮实际结果 |
| 构建可能跳过协议生成 | protoc 候选漏已登记 PATH，缺失时仅警告 | 显式/登记/PATH 定位，工具缺失早期非0；本轮完整构建执行生成 | 待 |
| 已发布101同版本替换可能破坏可用目录 | Move旧目录后再Move staging，失败没有可靠回滚 | 发布版本不可变或等价安全切换，完整内容就绪后原子换指针；失败对比原目录与指针SHA | 待 |
| 命令和 Unity 启动日志可能含测试密码 | CLI传明文凭证且保存原命令 | 使用私有输入或进程内生成；旧拥有证据脱敏保存，日志不得含密码/Token明文 | 待 |
| 100重新构建可能带101新Prefab | collector按目录采集，模块排除不等于资源排除 | 版本化构建来源或可靠采集过滤，核对实际包清单，保留100源可重建 | 待 |

证据目录：`Docs/Acceptance/M1/20261020-xLua/`。旧阶段声明保留；以本轮最终复核结果确定验收状态。

## 5. 09:28 Native 真机运行失败与立即修复

**操作与证据**：根据本轮 runner 的 `NativeAccept/pids.txt`，核对 PID24796 的真实可执行路径为 `StageD/Freeze 100 Final/Player/Farming.exe`，使用 `StageE/Root-Visual/Capture-OwnedPlayer.ps1` 截取真实窗口1280×720像素并查看。截图只有空场景，未出现登录UI；日志仅Awake，未执行Launcher。该图片仅作为内部失败诊断，不作为成功展示。

**原因**：真实 Player.log 抛出 `ArgumentException: No tokens were supplied`，调用栈定位 `LauncherBootBehaviour.Start()` 的零参数 `CancellationTokenSource.CreateLinkedTokenSource()`。该API要求至少一个需要关联的Token；宿主根取消源没有父Token。

**修复内容**：Root在Cursor等待本次有界Native工具返回期间，将唯一该行恢复为 `new CancellationTokenSource()`。Start拥有根取消源，OnDestroy取消它，后续Launcher再关联自身拥有范围。未改其它共享源，未关闭Cursor/服务/Player。

**验证步骤**：必须重新构建100后再运行Native，预期出现实际LoginWindow、Lua登录/模型/HUD和自动恢复。运行中的旧候选不会自动更新。实际复验待本轮Cursor执行，未记通过。

## 6. 09:36 启动异常复验通过，继续完整验收

实际重建清单时间09:34:13。Root读取09:34:59–09:35:05的真实Native日志：Launcher进入Login，真实输入/注册按钮显示“注册成功，请登录”，登录按钮流程后 `home=True`、`auth=True`、Lua昵称与HUD均为NativeNick。切断socket前本地昵称已污染为POLLUTED_LOCAL；随后出现遮罩，自动恢复后Lua和HUD均回NativeNick，`nickMatch=True`，业务快照playerId=3、level=1、gold=200、gem=300、ticket=5、bagSlots=60前后一致。退出 `disposeError empty=True`。

`NativeAccept/native-accept-summary.json` 实际ok=true。其session字段在退出后显示未认证，是Dispose后的状态，**不作为登出/Token撤销证明**；登出需A组另测。该复验证明Start取消源修复及本次UI/Lua自动恢复链，不代表全部58项通过，也不代表最终100冻结指纹或B组损坏资源重试已验收。当前矩阵仍4pass/54pending。

Root尝试捕获Home时进程已由runner正常结束，截图脚本按进程身份拒绝，没有产生伪截图；后续E流程选取真实成功界面截图。

## 7. StageE 初次证据复核与 T63 索引修复

- Root实读Core脚本：从全新缓存直接读取101，只跑一次登录/短线恢复；尚不能证明先运行100、保持缓存更新101、再次复用缓存，也不能证明A组完整登出和C组强杀/失效/顶号。此结论已写给Cursor继续实施。
- Root查看两张截图：A04实际截到浏览器页面，排除成功集；C04是真实Home，ID1/StageENick/Lv1/登出可读，但没有PatchDemo。Root已修改自己拥有的截图脚本，增加前台窗口所属PID校验；系统拒绝置前时明确失败，不能把其它应用像素当游戏证据。
- Root初次解析回归XML为6总/2通过/4失败，后续重跑覆盖。09:52:26最新XML独立解析为**6/6通过、失败0、跳过0**；早期失败原因需从未覆盖日志保留，不能隐去发生过程。

**T63问题与实际原因**：当前catalog标101，却没有新模块。StageFormalLua用AppendLine写Windows CRLF，追加代码只匹配LF结束结构，导致模块文件存在但从未插入索引。

**具体脚本修复**：Root将 `ClientBuild.AppendPatchDemoToStaging` 读取catalog的行改为：

```csharp
// Normalize Windows structural line endings before inserting the new module.
string catalog = File.ReadAllText(catalogPath).Replace("\r\n", "\n");
```

这是Editor构建代码，不改变Player AOT。待验证：重新实际导101配置，在新拥有发布根构建101；核对索引/Prefab/新Global三者并实际打开新面板。旧坏101保留诊断，遵循已发布版本不可覆盖。未据本次源代码修改提前勾B08或T63。

## 8. Root 接管验证修复计划（2026-10-21 11:26）

### 任务介绍

Cursor已实现v2完整指纹、实际命令驱动和新的100/101构建，但验证脚本仍有会被覆盖的启动标记等待、超时后继续执行和截图缺UI问题。Root在核实本轮CLI身份后结束临时CLI并接管源码与验证，继续完成M1；这不是用户暂停。保留既有源码和失败证据，先修确定缺陷，再一次构建一致的基包，逐步实跑并检查实际状态。随后继续剩余E验收，不请求计划确认。

### 具体步骤清单

- [ ] 备份当前Host、Driver、Launcher及验证脚本；仅清理核实属于本轮accept-root/DB的临时进程。
- [ ] 驱动在启动失败前可用；按命令ID消费并回执，退出前先回执；截图等待真实完整帧及文件写完。
- [ ] 状态记录完整Lua业务模型、实际窗口/场景、注册错误提示、版本/Global/下载与重连状态，凭证脱敏。
- [ ] 修复脚本：原子指针、按进程启动时间等待新状态、每个超时明确失败、每个回执检查实际result、try/finally清理。
- [ ] 用带空格新目录构建标准IL2CPP100；真实生成协议/配置/GenAll/Yoo/完整v2指纹，保存最终三SHA。
- [ ] 从版本化工作簿真实导101，新拥有发布根生成101；同安装同状态100→101→缓存复用、坏资源失败→修复→真按钮重试。
- [ ] Root实际查看成功截图、独立解析报告与安装SHA；继续A/C/D/E剩余项目并完成T68。

### 验证表

| 名称 | 具体步骤 | 预期 | 实际 |
|---|---|---|---|
| 命令与状态 | 注册/登录/登出/退出逐步等待 | 业务完成与点击回执分开，超时不能假通过 | 待 |
| 完整画面 | Native完整帧截图并Root view | 含UI，可读且对应实际步骤 | 待 |
| 启动失败重试 | 损坏未缓存资源→恢复→真按钮 | 错误保留、无坏提交、重试串行 | 待 |
| 基包一致性 | 当前源→Player→101前后三SHA | 同安装不变，来源指纹对应 | 待 |

### 已定位的问题与解决方案

| 情况 | 原因 | 修复方案 | 复验 |
|---|---|---|---|
| 101等待180秒仍不结束 | 脚本向观测日志追加marker，Host新进程整文件重写覆盖marker | 按新进程时间/实时状态等待，不使用会被覆盖的标记 | 待 |
| 等待失败仍拍照/退出并计成功 | 多处while超时无最终断言，只有文件存在检查 | 每个等待返回明确失败，校验业务状态及实际截图内容 | 待 |
| Home截图只有M1_Login场景 | Camera.Render未包含ScreenSpaceOverlay UI | Native完整ScreenCapture并等文件完成 | 待 |
| 跨重启重放退出命令 | 消费后命令文件残留，回执前Application.Quit | 一次性消费+持久回执去重，先回执再退出 | 待 |

## 9. 原生账号/更新与服务端复核已通过，继续恢复验收

实际证据 `Root-Finalization/Native-20261021-115404-107/result.json`：注册未授权、登录、登出清Token/模型、再登录、退出重开、同安装同缓存100→101、新Prefab窗口与实际Global、再次缓存复用全部通过；下载文件数42→5→0，三安装SHA不变。Root已view真实100 Home和101“热更新验证成功”窗口；截图来自Unity完整帧，非桌面或绘制面板。

实际坏资源证据 `BadResource-20261021-114550-099/result.json`：仅损坏自己的CDN副本，用新缓存启动，出现可见错误且entry/commit为False；Root view错误与真实重试后的Login图。修复原文件并核对SHA后点击真实按钮，只有prepare1、2，成功进入Login，Player三SHA不变。

服务端问题：Host与SessionRegistry重复观察连接关闭，Host在玩家队列外调用UnbindIfCurrent，可能在注册表自身清理前触发队列守卫，导致客户端自然退出时整个Host停止。Root移除Host重复解绑，保留SessionRegistry唯一身份清理；新增真实已登录TCP客户端连续12次自然断开/再登录、HTTP始终可用的回归。完整测试先因导出目录101与夹具100不符失败11项，原TRX保留；恢复100后最终 **224/224、0失败、0跳过**，`ServerTests/root-server-final224-config100.trx`。当前224取代旧223作为最新服务端结果。

Cursor ask只读复核提出缺失downloadFiles被转0、重启残留命令、未断言更新实际下载等3项，Root已修脚本并用保存的真实42/5/0状态重新核对。未改变Player，未伪改原实测。

### 下一任务：真实Native恢复流程

- [ ] 新独立DB登录并记录R1/DB快照。
- [ ] 正常停止测试通路，观察Native遮罩与默认完整退避，再恢复原端口/原DB，不输入密码自动恢复。
- [ ] 保存快照R2确认SavedRevision后，只强杀核实本轮拥有的dotnet服务端，再用原DB/配置重启并自动恢复。
- [ ] 在隔离SQLite事务将当前Token过期，切真实socket，观察204停止重试返回Login；重新登录。
- [ ] 第二Native客户端同账号登录，原客户端收到顶号返回Login且不抢回。
- [ ] 引用最新Host真实TCP旧Token204/错Proto106测试，并用Native收到106显示更新提示、无重连循环。
- [ ] 关闭本轮测试进程，填实际命令/截图/完整状态和问题表。

## 10. C组实测发现终止通知丢失：修复与重新冻结计划

### 任务介绍

Recovery-20261021-121150-489已经实际完成完整退避、恢复R1、SavedRevision=1、强杀自有服务端及同库恢复R2。C08将隔离DB中的有效Token到期时间设为0后，真实Reconnect返回204，Token已清、重连已停，但Lua模型和Home未清，未回Login，因此C09失败，不能把该轮标为完成。定位是ReconnectWithTokenAsync在返回终止业务码前调用StopReconnectLoop，取消其拥有的RecoverAsync，跳过上层NotifySessionExpired。先记录失败，补真实TCP回归，移除内层自我取消，让上层统一发送终止通知，再重新冻结基包及101并完成所有Native验收。

### 具体步骤

- [x] 保留Recovery-120811的日志共享冲突与Recovery-121150的真实204失败证据。
- [x] 修正日志读句柄为FileShare.ReadWrite，匹配带UTC字段的实际backoff日志；75秒真实观察序列1/2/4/8/16/30。
- [x] 新增AccountSessionPlayModeTests.AutoReconnect_InvalidStoredToken_Notifies204ExactlyOnce：实际登录提交Token，将隔离本地Token替换为合法格式但DB不存在的值；切真实socket、Pump，等待真实204回调一次，并断言清Token/认证/遮罩/循环。
- [ ] 先运行该用例保存修复前失败，再移除ReconnectWithTokenAsync终止分支的StopReconnectLoop；终止通知由RunReconnectAsync处理，不改变协议。
- [ ] 在Root Final 100 R2完整构建100，在Root Final Publish R2构建101；重新执行A/B、坏资源、完整C与106提示。
- [ ] 当前受影响的PlayMode回归重新通过，矩阵按新候选证据回填，最后记录T68。

### 验证步骤

| 名称 | 具体步骤 | 预期结果 | 实际结果 |
|---|---|---|---|
| 204失败复现 | 已登录Native设置DB expiry=0，切socket | 清Token/模型并返回Login | 失败：lastBiz204、TokenFalse、循环False，但HomeTrue/Lua仍保留；证据failure.json保留 |
| 已提交恢复 | PlayerStore.Save准备gold201/bag1×7/revision1，停机/恢复/强杀/恢复 | 同Token和完整Lua业务数据保持 | 本轮C01–C07通过，强杀前核对进程exe、父PID和精确DB路径 |
| Unity当前回归 | UIResource2/Launcher2/Closing2/Account2 | 实际8项且无失败跳过 | 8/8通过，Root-Finalization/PlayMode/results.xml；该组未覆盖真实终止通知，因此新增用例 |
| 终止回归 | 真实TCP无效Token响应204 | 回调一次且停止循环/遮罩 | 待 |
| CLI路径负例 | build_client_win传不存在Unity路径 | 非0，明确路径且无成功输出目录 | 通过，exit1，CliNegative/result.json |

### 问题与解决方法

1. Unity持有-logFile写句柄，File.ReadAllLines不允许并发写，导致验收脚本失败；改为带ReadWrite共享的FileStream/StreamReader并Dispose，保留首次失败。
2. 重连内层收到204/106时取消自身CTS，上层按取消退出，没有NotifySessionExpired；由上层在RecoverAsync正常返回false后清理并通知，补真实响应回归及Native复验。
3. 三个PlayMode服务端夹具依赖Unity会清理的Temp目录；显式传稳定的Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts，按实际目录决定SkipBuild，8项已通过。
## 11. 最近任务完成与阶段总结（2026-10-21，按用户要求汇报）

### 任务目标与已完成步骤

完成204/106终止分支的自我取消修复与当前受影响回归。本轮不启动下一轮完整构建；用户要求先汇报当前状态。未将M1标完成，电量检查继续暂停。

- [x] 修复前：Expiry-Red/results.xml真实1项失败、exit2；期望收到204，实际终止回调仍为0。
- [x] 修改Assets/Framework/Account/AccountSession.cs的ReconnectWithTokenAsync：删除204/106分支StopReconnectLoop，保留清pending/Token/遮罩和真实业务码返回；新增注释说明取消拥有者会跳过Lua终止事件。RunReconnectAsync在RecoverAsync正常返回false后统一通知。
- [x] 修复后：PlayMode-Final/results.xml实际9/9、失败0、跳过0、Unity exit0。包括真实TCP非法已存Token→204通知恰好一次、原自动恢复、顶号、106业务码、冷启动真实Lua注册登录登出、启动取消及2项UIResource。
- [x] 历史M1_06前14515字节SHA再次保持7A6CE2A46D063BC99C19F65F4FB1F33507241947F77E2F293EA6BA5A70B28328。
- [x] 准备Build-FinalCandidate.ps1与Native脚本新的R2目录；尚未执行构建，不能把R2目录作为已生成交付。

### 命令与参数

实际Unity命令见Root-Finalization/PlayMode-Final/command.txt：`-batchmode -nographics`批处理；`-projectPath`指定本工程；`-runTests -testPlatform PlayMode`运行真实PlayMode；`-testFilter`限定受影响9项；`-testResults`写独立XML；`-logFile`写独立日志。未加`-quit`，由TestRunner结束进程。私有凭证不在参数/日志输出中。

后续（尚未执行）`pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/Root-Finalization/Build-FinalCandidate.ps1`：顺序运行正式完整100构建、复制100 ServerConfig、真实Luban101导表、复制101 ServerConfig、正式101补丁构建、恢复工作目录配置100。Invoke-Recorded等待进程并检查真实退出码；新候选目录存在即失败，旧候选保留。参数RepoRoot为工程根。Native脚本已指向Root Final 100 R2 / Root Final Publish R2，因此须先构建后运行。

### 当前M1状态

- T01–T56：已有实现及分阶段通过证据；xLua迁移、旧依赖配置删除及清理后验证已做，历史原文保留迁移说明。
- T57：自动恢复成功链已实际通过；本轮终止通知缺陷修复9项回归通过，最新IL2CPP客户端端到端复验仍待。
- T58–T63：正式脚本、标准IL2CPP100、独立101、新Lua模块/Prefab/Global、v2完整指纹、不可变发布、服务端和资源测试已有实测。服务端最新224/224；因本轮固定C#变更，需要新基包重新冻结100/101。
- T64/T65：旧一致候选上真实账号生命周期及100→101→缓存101已通过，下载42→5→0、安装3SHA不变；新基包需回归后才能最终关闭。
- T66：Native完整退避/同库恢复/强杀恢复C01–C07已有实测，C09暴露本轮缺陷；最新基包上的失效回登录、双客户端顶号、旧Token拒绝、106更新提示仍需完成。
- T67：主要成功路径、224个服务端测试、Unity资源测试、坏Unity路径非0、重复协议ID保留成功产物、v2固定代码/native拒绝等已有证据；最终候选证据需整理并逐条复核。
- T68：尚未完成。AcceptanceMatrix仍保留4项pass/54项pending的旧登记，未反映已取得的全部证据；必须真实逐项回填，不能将未回填简单解读为仅完成4项，也不能直接批量勾全部。

### 问题、原因与下一步

确认的主要原因是账号网络重连、Lua模型应用、认证提交、场景切换之间的完成/取消顺序缺少统一拥有者；Host/Registry曾重复处理关闭；部分验收脚本只确认点击或文件存在，不能证明业务完成，导致较晚发现问题。xLua本身不是已确认的主因。

下一轮依次：重建与冻结唯一R2安装→完整Native A/B/坏资源/C组/106→核对当前指纹与三SHA及真实截图→逐条映射58项矩阵→回填主计划、子Log和最终M1验收记录。

如果重来，先重设计AccountSession/AccountService/AccountFlow的会话状态机与取消归属，再建立SessionRegistry唯一关闭拥有者；将网络响应→Lua快照应用→Token Commit→Home拆成显式事务阶段；终止原因作为结果返回，统一发送一次终止事件；从第一天用固定候选产物和真实Native验收，测试状态只做观察，不代替生产成功条件。
## 12. 继续M1：连接故障隔离与最终R2验收计划

### 自然语言任务介绍

用户已要求继续开发。先运行已准备的R2完整构建，将204修复纳入最终安装；并根据用户对服务端解耦的质疑关闭连接观察错误全局升级的残留。Cursor只读审查已确认Host._watchFailure仍将单连接清理错误当作全局错误，建议移除该传播、逐项清理与安全日志，使用不同账号真实并存客户端验证存活。服务端修改独立于正在构建的Unity AOT。

### 最小执行步骤与验证

- [x] 运行Build-FinalCandidate.ps1；独立Root Final 100 R2 / Root Final Publish R2，完整正式脚本；本节记录时仍在构建，不预记成功。
- [x] 同一Cursor会话ask只读审查；结果ConnectionIsolation-Review-result.md，不编辑源/不管理进程。
- [ ] 仅改Host：移除_watchFailure全局通路；完成任务无论成功失败都清理连接/路由/心跳；失败只写sessionId/阶段/异常类型，避免敏感异常正文。
- [ ] 新真实集成用例：两个不同账号同时登录，A使用RST退出；B心跳/HTTP/新TCP登录仍可用；保留监听/配置全局失败测试。
- [ ] 正式服务端完整测试并重建稳定Release产物。
- [ ] R2 Native A/B、坏资源、完整退避/同库强杀恢复/过期/顶号/旧Token/106；失败停在当前步骤修复。
- [ ] 最后固定候选证据、基础检查与58项矩阵逐条回填、日志与主计划同步，实际全部通过后才完成T68。

### 问题预留

| 触发情况 | 问题与原因 | 修复方法 | 预期/实际 |
|---|---|---|---|
| 任意连接watch未来发生异常 | Host.WhenAny同时监视_watchFailure，误停全服务 | 从Host全局任务集合剥离连接故障，清理隔离并记录诊断 | 待实际测试 |
## 13. R2实测已完成，输入夹具批处理兼容修复

### 当前实测

- 完整构建与真实导表：r2-client-build / r2-config101 / r2-patch-build / r2-restore-config100四进程均exit0。最终Root Final 100 R2三SHA为exe29ab39…d0f1、GameAssembly91b7d8…d079、metadata cd94d9…09a9。未覆盖旧候选。
- 服务端最新root-server-final225.trx：225/225、0失败/跳过；实际稳定Release产物已重建。新增两个不同账号并存、一个RST、另一个Heartbeat/HTTP/新TCP登录全部存活测试。
- Native-20261021-141638-780：实际A组账号、B组同安装同缓存100→101→缓存101通过，下载42→5→0，安装三SHA不变。Root查看100 Home与101新Prefab完整帧。
- BadResource-20261021-141811-409：实际CDN坏bundle/新缓存→可见错误且未entry/commit→恢复原SHA→真实Retry成功；prepare仅1、2。Root查看错误UI。
- Protocol-20261021-141823-622：实际客户端Proto1对服务端Proto2，106/版本更新文案/无自动重连通过，Root查看实际Login错误提示。
- Recovery-20261021-141811-730：完整75秒以上真实退避1/2/4/8/16/30、原库恢复、SavedRevision1、拥有进程强杀/原库恢复、DB真实Token过期204清模型回Login、重新登录、第二Native顶号、旧Token真实TCP204无快照且B未被抢回，全通过。Root查看遮罩/恢复Home/被顶回Login完整帧。
- Foundation-Final/results.xml：当前基础69/69、0失败/跳过；包括Pending/生命周期/传输、OpId、核心事件池计时、FileLogger。
- AotNegative-R2-20261021-141623-394：真实build_patch固定C#差异exit1，明确fingerprint-changed AccountSession；源码finally逐字节恢复，安装三SHA和原发布指针保持，无错误101指针。

### 输入夹具问题与修复

最后E09新跑Input-Final结果3/7通过、4失败，原报告保留。两个EventSystem模态点击用例通过；键鼠设备事件在无界面批处理下读取均为0。原T17前台Editor测试5/5仍为历史有效，但不据此跳过本次失败。

查已安装InputSystem1.20.0官方包只读源码InputSettings/InputManager确认：默认Editor路由与焦点会影响GameView设备更新。修测试MouseKeyboardInputTests：SetUp记录原updateMode/backgroundBehavior/editorInputBehavior，设置ProcessEventsManually、IgnoreFocus、AllDeviceInputAlwaysGoesToGameView；各用例自己QueueStateEvent和InputSystem.Update；TearDown恢复原设置和原设备。生产输入代码、ProjectSettings与Player均不改。Input-Fixed将保存独立复验，未用降低断言或跳过用例处理。

### 验收汇总器

Finalize-Acceptance.py只读取实测报告，不运行测试、不按退出码盲勾。检查225/9/69/7实际用例数与失败跳过、3342冻结文件逐SHA、安装三SHA、完整业务模型/DB、真实已知凭证在拥有日志中0匹配、双端配置SHA与101真实值；全部满足后才逐项写58项实际结果和具体证据，以及M1_Acceptance.md。首次汇总遇到Input-Final实际失败已正确拒绝，未生成全通过矩阵。
## 14. 最终结论：T01–T68及58项全部通过

Input-Fixed实际7/7、0失败/跳过、Unity exit0。首轮Input-Final 3/7原报告保留；修改仅限测试夹具，并恢复原全局设置，Player源码未变。

Finalize-Acceptance.py真实运行成功：58pass/0failed/0pending，3342个冻结文件逐SHA仍对应当前Player源码，服务端225、受影响PlayMode9、基础EditMode69、输入模态PlayMode7；复用未变更C1 27/C2 13证据。当前双端19个JSON文件SHA一致，Native与生产ServerConfigService读取101新值一致；39个拥有日志对6个实际已知私密值扫描0匹配。历史M1_06前14515字节7A6CE…B28328再次保持，最终状态仅追加。

主拆解68行任务、原开发计划M1的21项与看板已同步。M1_07最终章节包含自然语言任务、已执行步骤、实际命令、每个参数/函数说明、验证预期/实际、问题原因/修复/复验，以及本轮完整具体脚本源码快照。最终报告M1_Acceptance.md给出全部58项实际证据和真实成功截图；旧候选与失败不覆盖。

本次完成范围为Windows x64/.NET8的M1基础框架。后续玩法与移动端验证不计入本次通过。完成原生测试已正常关闭拥有客户端与服务器；电量检查仍暂停，无Git写操作。
## 15. 关闭前一致性复核完成

Cursor同会话ask只读最终复核退出0，未发现实质性一致性阻断；审查边界明确为文档/矩阵/关键result/报告计数/Host源码，没有冒称重新执行全部测试。结果保存Root-Finalization/Final-Consistency-Review-result.md。Root已独立完成实际3342文件哈希、安装3SHA、模型/DB/配置/日志检查，并检查最终报告86个唯一链接目标均存在。主计划68项、矩阵58项一致，Unity/测试Player进程计数0。所有必需M1工作完成，无待处理阻断；不扩展后续玩法。电量检查保持暂停。