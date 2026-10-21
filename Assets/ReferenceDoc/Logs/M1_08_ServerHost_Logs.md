# M1 xLua 正式服务端组合入口补充日志

日期：2026-10-20。此子日志记录 T53 客户端真实账号联调与 T60 一键服务端脚本所需的生产组合入口，不重写既有 T28–T40 历史，不提前勾选 T53/T60。

## 任务介绍与流程

现有默认 Program 仍启动旧 PoC 协议。正式 AccountHandler、TokenService、SessionRegistry、PlayerCache/SaveService、TcpServer 和 PatchHttpService 已分别实现。将这些生产类组合为统一 M1ServerHost，验证真实 TCP 注册/登录/登出、HTTP、隔离数据库、取消停机和错误启动清理；后续 C2/T60 直接复用该入口，不复制假处理器。

当前 Cursor C1 只修改客户端窗口/场景，Root 独立负责 Server/Hosting/M1ServerHost 与对应测试，构建输出隔离。M1 正式基包预计0.3.0、Proto1、资源/配置100；HTTP客户端用localhost。

## 最小步骤清单

- [x] 定义明确选项：DB/Config/HTTP内容/日志目录、TCP端点、HTTP前缀、App/Res/Proto/Config门槛。
- [x] 启动前验证配置，初始化已有SqliteStore/账号/玩家队列/缓存/令牌/会话/保存服务。
- [x] 用真实TcpServer与正式Router注册Register/Login/Reconnect/Logout/Heartbeat，按会话ID发送，不混用Poc消息。
- [x] 跟踪真实会话关闭，清理Router/Heartbeat和本地映射；保留旧连接不能解绑新连接的既有规则。
- [x] 协调TCP/HTTP/保存/指标任务，端口或配置失败取消其他组件，正常停止保存并释放资源。
- [x] 真TCP/HTTP集成测试及重启DB/Token验证；记录精确命令和实际非零测试数。
- [x] 回填源码参数/函数说明、问题原因修复与复验；告知C2/T60复用接口。

## 验证计划

| 名称 | 具体步骤 | 预期结果 | 实际结果/证据 |
|---|---|---|---|
| 正式账号链 | 隔离DB启动Host，真实Socket发正式注册/登录/登出帧 | code0、同playerId、登出不伪回包 | 通过，FormalHost测试，见下方TRX |
| HTTP | 请求version.json和不存在文件 | 200字段正确/404 | 通过，FormalHost测试 |
| 取消与端口 | 取消Host，随后重绑TCP/HTTP | 所有监听释放，无悬挂任务 | 通过，认证/连接均0并同端口重启 |
| 配置/启动失败 | 错版本/占用端口启动 | 明确异常，另一监听也关闭 | 通过，OccupiedTcp/WrongConfig两测试 |
| 服务端重启 | 原DB重启，用有效Token发Reconnect | 恢复同玩家/业务快照 | 通过，RestartSameDatabase测试 |

## 命令与源码

证据目录 `Docs/Acceptance/M1/20261020-xLua/ServerHost/`。不读取电量，无Git写操作，不修改客户端UI/基包或旧验收原件。

### 实施与实际验证（16:53回填）

上述六项组合/验证步骤均完成；最后的C2/T60消费入口仍由对应任务实施，本日志不提前勾任务。

源码完整内容位于[生产Host](../../../Server/CloudIsle.Server/Hosting/M1ServerHost.cs)和[真实集成测试](../../../Server/CloudIsle.Tests/M1ServerHostTests.cs)；每个公开选项、方法及测试辅助函数均有用途注释。选项区分精确启动ConfigVersion和允许登录的最低RequiredConfigVersion，避免后续兼容101被错误拒绝。

Host.RunAsync为唯一生命周期：验证配置→实例日志→隔离指定DB→共用玩家队列/缓存→会话/SaveService/正式AccountHandler九参→HTTP/TCP绑定→周期保存与真实指标。Started只有两个监听均绑定才成功；Session.Completion后清Router/Heartbeat和当前身份映射；停机取消监听，等待收包与会话观察，再Stop/FlushDirty/Dispose保存及队列DB。保存预算取消作为错误报告，不吞掉假称成功。生产Program的明确M1命令入口尚待C2/T60复用Host。

完整命令（工程根目录，独立artifacts避开其他阶段obj/bin）：

```powershell
dotnet restore Server/CloudIsle.Tests/CloudIsle.Tests.csproj --artifacts-path Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts --ignore-failed-sources --nologo -v minimal -p:NuGetAudit=false
dotnet build Server/CloudIsle.Server/CloudIsle.Server.csproj --artifacts-path Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts --no-restore --nologo -v minimal
dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --artifacts-path Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts --no-restore --nologo --filter FullyQualifiedName~M1ServerHostTests --logger 'trx;LogFileName=host-tests.trx' --results-directory Docs/Acceptance/M1/20261020-xLua/ServerHost/TestResults -v minimal
```

`--artifacts-path`隔离构建缓存/产物，`--no-restore`仅在本目录恢复之后使用，`--filter`只跑这五项Host组合风险，TRX记录真实发现/通过/失败数。首次测试编译旧MessageRouterTests有NUnit2005写法建议；未为本任务改历史测试。生产Host构建0警告0错误。

| 名称 | 具体实跑与实际结果 | 证据 |
|---|---|---|
| 正式账号链/HTTP | FormalHost_HttpAndAccountAndHeartbeatAndLogout：HTTP200/404、TCP1003/1005/1001/1009；全部code0、真实玩家/Token、无登出回包只EOF；敏感日志断言通过 | ServerHost/TestResults/host-tests.trx |
| 停机释放 | 取消后连接/认证数0，原TCP/HTTP端点立即重启正常，cleanupFailures=0 | 同TRX |
| 原DB重启 | RestartSameDatabase_ReconnectsThenLogoutRevokesToken：同DB新Host、未撤销Token恢复同playerId/完整快照，登出后204 | 同TRX |
| 顶号/版本 | ReplacementLogin_KicksOldAndRejectsStaleTokenAndProto：真实1010/seq0/KickElsewhere、旧连接关闭、旧Token204、错误Proto106且当前认证数仍1 | 同TRX |
| 启动失败 | OccupiedTcp与WrongConfigVersion：真实SocketException/配置异常；清理先启动HTTP，修正后同地址新Host成功 | 同TRX |

Root独立读取TRX counters：**total5/executed5/passed5/failed0/notExecuted0**，进程exit0。仅证明正式Host组合入口；不代替客户端界面、业务存档修改和最终AC1–4。

C2/T60调用契约已交接至[ServerHost/Ownership.md](../../../Docs/Acceptance/M1/20261020-xLua/ServerHost/Ownership.md)。

## 问题与解决方法

| 什么条件下 | 现象 | 原因 | 具体修复及复验 |
|---|---|---|---|
| 首次以新artifacts目录直接--no-restore | NETSDK1004，尚无project.assets.json | 隔离输出首次未恢复依赖 | 先restore再build；生产构建0错误 |
| sandbox运行dotnet restore/build | exit1且未给出编译错误详情 | 执行环境限制；不是源码编译失败 | 已授权命令以require_escalated在相同工作区输出执行，restore/build/test正常通过；未改全局SDK |
| 源码编写阶段检查停机路径 | 如果统一忽略OCE会把保存超时误报成功 | listener正常取消与Flush预算超时语义不同 | 只对已取消的监听任务忽略取消，保存OCE加入失败；无注入伪通过记录 |

## 16:56 后续独立实施计划：正式CLI与T60脚本

Root继续独立负责Server范围并扩展到Program中明确`--m1`分支、新M1ServerCommand及Tools/run_server.ps1，不修改C1客户端。保留旧PoC的明确已有入口行为，正式脚本总是传--m1；不会把17777旧服务当正式协议。

步骤：严格解析DB/配置/日志/HTTP内容/端口/版本参数→构造已验Host→Ctrl+C或显式stop-file请求正常停机→退出码传至脚本。脚本支持完整路径和空格，错误参数/配置/端口/DB失败非0；验证实际CLI启动、HTTP200、stop-file保存退出及占用端口/错误配置。先完成再回填测试结果并交接C2/T60，不提前声称已通过。

### 17:04 CLI实际结果

完整实现：[M1ServerCommand](../../../Server/CloudIsle.Server/Hosting/M1ServerCommand.cs)、[Program的--m1分支](../../../Server/CloudIsle.Server/Program.cs)、[Tools/run_server.ps1](../../../Tools/run_server.ps1)。PowerShell参数帮助与内部函数注释说明所有路径、版本和退出方式；C#严格拒绝未知/重复/缺值参数。生产库默认Server/Data/M1/m1.db；测试全部显式新库。正常Ctrl+C/stop-file取消后由Host等待保存、关闭监听并输出停机，非旧PoC端口17777。

实际执行命令：

```powershell
pwsh -NoProfile -File Docs/Acceptance/M1/20261020-xLua/ServerHost/Run-CliValidation.ps1
dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --artifacts-path Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts --no-restore --nologo --filter FullyQualifiedName~M1ServerHostTests --logger 'trx;LogFileName=host-final-tests.trx' --results-directory Docs/Acceptance/M1/20261020-xLua/ServerHost/TestResults -v quiet -p:NoWarn=NUnit2005
```

CLI验证器[完整源码](../../../Docs/Acceptance/M1/20261020-xLua/ServerHost/Run-CliValidation.ps1)使用ProcessStartInfo.ArgumentList逐参数启动正式脚本（支持空格），独占DB/配置副本/HTTP根/日志与停止文件；不是假服务。每项保留stdout/stderr，除了exit码还校验真实失败类型/中文信息，避免参数绑定错误被错当数据库/端口验证。最终证据 `ServerHost/CLI Validation 20261020-170049/results.json`，**7项全部通过**：

| 名称 | 实际步骤与结果 | 预期/实际退出 |
|---|---|---|
| build+空格路径+正常保存 | 不传SkipBuild，真Release编译；DB/Content/Config/Log含空格；HTTP200和TCP连接；创建停止文件；schema initialized version2、started与stopped cleanupFailures0 | 0 / 0 |
| TCP占用 | 测试自有TcpListener占端口，正式脚本实际SocketException | 非0 / 1 |
| HTTP占用 | 测试自有HttpListener占前缀，正式Host报无法绑定补丁HTTP | 非0 / 1 |
| 配置不匹配 | -ConfigVersion999，真实ConfigLoadException | 非0 / 1 |
| DB非法 | 测试新库路径预先建为目录，真实SqliteException Error14 | 非0 / 1 |
| 内容缺失 | ContentRoot不存在，明确缺version.json | 非0 / 1 |
| 端口非法 | TcpPort0由脚本参数范围拒绝 | 非0 / 1 |

首次CLI验证6项通过但stderr中文乱码（165913子目录保留）；原因是Windows原生命令/重定向编码不一致。将正式CLI和脚本输出显式UTF8、验证器流明确UTF8后重跑上述7项，中文错误原因可读；未改机器系统编码/代理。最终集成TRX再次 **5/5、失败0、跳过0**、进程exit0，覆盖正式CLI接入后的同一生产Host。`-p:NoWarn=NUnit2005`仅隐藏旧断言写法建议，不压制编译错误或测试结果。

已交接C2/T60复用脚本；T60单项门槛满足，但最终CDN与A/D验收仍等T58–68，本日志不代表完整M1通过。

## 17:06 T61全服务端NUnit门槛执行计划

正式Host与CLI均已固定，趁C1只改客户端，Root以同一独立artifacts运行全部CloudIsle.Tests：不设过滤，不重写已有注册/持久化/幂等测试。先检查实际全套TRX非零发现/执行数、0失败/0跳过；若有失败只修具体原因，保留原失败证据。验证临时DB来自已有隔离夹具，正式/PoC库不动。命令和实际结果执行后回填；UI资源/Native/账号界面尚不由此证明。

### T61本轮实际结果（17:06）

```powershell
dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --artifacts-path Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts --no-build --no-restore --nologo --logger 'trx;LogFileName=t61-server-full.trx' --results-directory Docs/Acceptance/M1/20261020-xLua/ServerHost/TestResults -v quiet
```

同一最终Host/CLI接入后的已编程序集，不过滤测试。进程exit0，Root独立读取TRX：**total221/executed221/passed221/failed0/notExecuted0**。覆盖已有CloudIsle.Tests全部生产实现夹具，包括账号/密码/昵称、顶号/Token/登出/跨重启、玩家队列、保存成功/失败Dirty保持、幂等以及新Host真实网络组合。临时库来自各测试独占目录，未接用户正式数据。证据 `ServerHost/TestResults/t61-server-full.trx`，后续D确认T61清单具体覆盖再勾，不重复为了计数新增自我镜像测试。后续若改变Server业务语义，最终相关门槛须再验；仅客户端改动不要求重跑全部服务端。

## 19:48 A11/E11明确命名存档夹具补充计划（实施前）

最终验收明确要求PlayerPersistenceTests的“真实修改后保存重载”及“数据库不可写保持Dirty/恢复重试”。已有221项覆盖组件语义，补两个面向该验收的端到端夹具，复用正式AccountRepository注册/SqliteStore/PlayerStore/PlayerQueue/PlayerCache/SaveService；不开放玩家改名API或无鉴权调试协议。

步骤：新隔离DB正式注册玩家→玩家队列内ReplaceNickname测试钩子修改→真实Flush→关闭/重开同DB验证标记昵称和业务快照；另一用例在同一真实SQLite工作连接启用PRAGMA query_only，实际写入失败时验证Dirty/SavedRevision不误确认，关闭query_only后重新Flush成功。只新增Server/CloudIsle.Tests/PlayerPersistenceTests.cs，C2负责客户端，无并行共享文件。先实际跑新用例，再最终完整套件，结果回填；源代码/命令有函数参数注释，不把计划算通过。

### 19:52 A11/E11补充夹具实际结果

完整源码`Server/CloudIsle.Tests/PlayerPersistenceTests.cs`，2项均通过：ModifiedNickname_SaveCloseReopen_PreservesFullBusinessSnapshot实际注册新玩家、队列修改昵称、SaveService提交并关闭重开DB，完整PlayerFullData相等且标记昵称持久化；DatabaseReadOnly_KeepsDirty_ThenWritableRetryPersists在正式SQLite唯一工作连接PRAGMA query_only=ON，实际SQLITE_READONLY=8失败，Dirty和SavedRevision保持、已提交快照不变，OFF后同快照重试提交成功且Dirty清除。临时凭据只在内存，证据仅业务快照，无Token/密码；没有新业务改名API。

实际命令：

```powershell
dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --artifacts-path Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts --no-restore --nologo --filter FullyQualifiedName~PlayerPersistenceTests --logger 'trx;LogFileName=player-persistence-tests.trx' --results-directory Docs/Acceptance/M1/20261020-xLua/ServerHost/TestResults -v quiet -p:NoWarn=NUnit2005
dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --artifacts-path Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts --no-build --no-restore --nologo --logger 'trx;LogFileName=t61-server-final223.trx' --results-directory Docs/Acceptance/M1/20261020-xLua/ServerHost/TestResults -v quiet
```

前者2/2，后者完整 **223发现/223执行/223通过/失败0/跳过0**，均exit0，Root独立解析TRX。证据保留原221/原Host5/CLI7，这次仅新增必要验收夹具，不修改生产Server。实际新DB在Temp/m1-player-persistence-*，正例有before.json/after-reopen.json供A11复核。问题：未遇到额外实现失败；query_only故障为预先设计的真实负例，finally恢复。T53–57仍由唯一Cursor实施，完整M1尚未完成。
