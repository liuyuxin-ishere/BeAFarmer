正在只读审查 `_watchFailure` 的全局升级路径与相关测试。## 结论

对端正常退出把 Host 拖死，根因是 **`WatchClosedAsync` 故障 → `_watchFailure` → `RunAsync` 的 `WhenAny` 整机关机**。`UnbindIfCurrent` 已从生产 `WatchClosedAsync` 去掉（对比 `Before/M1ServerHost.cs` 仍有该调用），224 绿说明**常见 peer exit 路径已通**；但 **`_watchFailure` 全局升级仍在**，任意会话观察任务一 fault 仍会停掉 TCP/HTTP/全体 peer。

---

## 机制（当前代码）

| 位置 | 行为 |
|------|------|
| `M1ServerHost.cs:55` | `_watchFailure` TCS |
| `M1ServerHost.cs:152-154` | `WhenAny(tasks + _watchFailure)`：watch 失败直接 `throw`，走与后台服务失败相同的停机路径 |
| `M1ServerHost.cs:202-209` | 每连接 `WatchClosedAsync` 的 `ContinueWith`：`done.IsFaulted` → `_watchFailure.TrySetResult(...)` |
| `M1ServerHost.cs:216-225` | 现仅 `Completion` + 去表 + `ReleaseSession`；**已无** `UnbindIfCurrent`（注释说明曾与 `SessionRegistry` 竞态） |
| `SessionRegistry.cs:275-298` / `334-351` | 断线解绑由 Registry 自己的 `RunWatchAsync`/`UnmapIfMatch` 负责；`UnbindIfCurrent` 仍要求在玩家队列内 |
| `TcpServer.cs:177-195` | 单会话异常已隔离，**不会**经 `_watchFailure` 停 Host |
| `M1ServerHostTests.cs:51-69` | `AuthenticatedPeerExit_...` 只覆盖**串行**同账号反复进出，**不**覆盖「一 peer 异常/退出时另一已认证 peer + 监听仍活」 |

`ClientSession.Completion` 走 `TrySetResult`（不 fault）；`ReleaseSession` 也难抛。但只要将来 cleanup 抛错、或再引入需队列上下文的调用，**仍会整机退出**。

---

## 最小生产修复（只动 Host）

目标：会话观察失败**只影响该连接清理**；`http`/`tcp`/`MetricsReporter` 仍在 `tasks` 里，意外结束照旧停 Host。

1. **删掉** `_watchFailure` 字段（约 L55）。
2. **`RunAsync` L152-155** 改回只对 `tasks` 做 `WhenAny`（不要再挂 `_watchFailure.Task`）。
3. **`DispatchAsync` L204-209** 的 continuation 只做 `_connectionWatches.TryRemove`；**不要** `TrySetResult` 到全局失败。若需可观测性：fault 时打已有 `trace`/logger 一行即可，**禁止**升级为 Host 失败。
4. （建议同改、仍很小）`WatchClosedAsync` 在 `await Completion` 之后用 `try/finally` 包清理，保证 `TryRemove` + 两次 `ReleaseSession` 尽量都执行；单步失败用 log，**不向外抛**——从源头避免 watch fault。

不要为此加钩子、不要改 `SessionRegistry`/`TcpServer`。

---

## 有意义的回归（`M1ServerHostTests`）

现有 `AuthenticatedPeerExit_KeepsHostListeningAndAcceptsNextLogin`（L51）不够：没有**并存**的已认证 peer。

新增例如 `OnePeerAbruptExit_KeepsOtherAuthenticatedPeerAndListenersAlive`：

1. 启动一个 `World` Host。  
2. **两个不同账号**注册并登录（A、B 同时在线；`AuthenticatedPlayerCount == 2`）。  
3. **不 Logout**，直接 Dispose/关 A 的 `TcpClient`（真实对端退出）。  
4. 等到 A 侧连接与认证清掉（例如 count → 1），并断言：  
   - Host 的 `RunAsync` **未**完成（未因 watch 失败退出）；  
   - B 仍能发 **Heartbeat** 且收到合法 `HeartbeatResp`；  
   - **HTTP** `version.json` 仍可读；  
   - 新 TCP 仍可 **Accept + 登录**（可再用 A 或第三账号）。  
5. 最后正常 `StopAsync`。

这直接锁住「连接级错误隔离」，同时不削弱「监听/后台任务失败仍停 Host」（已有 `OccupiedTcp_FailsStarted...` 等覆盖启动/监听失败）。

若还想覆盖 **watch 故意 fault** 而不扩大生产面：不必 Internals 注入；上述「continuation 不再升级 + cleanup 不外抛」后，并发 peer 测试已是行为契约。不要为测而去 mock `ReleaseSession`。

---

## Findings（优先序）

1. **阻断级设计残留**：`M1ServerHost.cs:55` + `152-154` + `207-208` — 任意 `_connectionWatches` fault → 全 Host 停；与 `TcpServer` 单会话隔离（`177-195`）不一致。  
2. **已修**：`WatchClosedAsync`（`216-225`）去掉 `UnbindIfCurrent`，避免 peer exit 因队列前置条件 fault；`SessionRegistry` 自有 watch（`275-298`）负责解绑。  
3. **测试缺口**：`M1ServerHostTests.cs:51-69` 未证明「一连接退出/观察失败时其他已认证 peer + TCP + HTTP 仍活」。