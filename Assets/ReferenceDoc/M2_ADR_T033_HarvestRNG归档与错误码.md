> Root 2026-10-26 已验收 T033：33/80，真实1221/353/24，严格来源r2 exit0；原轮次未验收文字保留为历史，规则决策以本ADR v1.1为准。

# M2 ADR T033：单地块 Harvest 的 RNG、归档与错误码

版本：v1.1；日期：2026-10-25。  
依据：[06_M2_开发计划](06_M2_开发计划.md) D08/T033、[05_协议与接口](05_协议与接口.md)、[M2_ADR_T029](M2_ADR_T029_AcceptedTaskState与schema7.md)、`Docs/Delivery/M2/T033_Root_Review.md`。  
状态：**已写入（Cursor 源码轮 r2；未跑测；T033 未勾选；进度保持 32/80）**。

## 1. 决策

| ID | 主题 | 结论 |
|---|---|---|
| R1 | RNG 所有权 | **HarvestPolicy** 在资格校验通过后、且仅当 inclusive 区间 `YieldMin < YieldMax` 时，经注入的 `IRandomSource` 抽一次；`YieldMin == YieldMax` 直接取该值且 **RngCalls=0**。生产默认 `ServerRandomSource`；测试/FaultWorker 可注入 `MinInclusiveRandomSource` 等受控源，**不**冒充默认真实分布，**不**改正式默认工厂为固定值。 |
| R2 | 抽选公式 | 仅用原 `AcceptedHarvestSpec`：公式身份必须为 `crop-yield-range-v1`；产物 ID / inclusive `[YieldMin,YieldMax]` / `Exp` 全部取自已存规格；**禁止**查当前 Crop 或客户端数量。区间跨度用 `long` 计算，避免 `max+1` 在 `Int32.MaxValue` 溢出。 |
| R3 | 资格 API | 保留计划向 `Evaluate(FarmInput, PlotIntent, nowMs)` 兼容重载：只做成熟/规格/元数据资格，成功时 `HasSettlement=false` 且不抽签；完整结算走带 `IRandomSource` / `CropRules`（仅稳定地块身份）的 overload。应用层正式路径必须走完整结算。`ComposeHarvestSettlement` 遇资格-only 或失败时 **Code 须非 0**（缺结算 → `BadPacket` + `harvest-settlement-required`），禁止 `Fail(code0)`。 |
| R4 | 抽签时机 | 资格失败（锁地/未成熟/缺规格/畸形/公式不支持/**存档 Plots 重复 plotId**/等）**不得**调用 RNG。已发生的抽签（含越界返回）必须在 `FarmHarvestResult.RngCalls` **如实保留实际调用次数**，不得因失败清零。COMMIT 成功后的 Query/重放/重启/强杀恢复 **RngCalls=0**、零规则。未提交失败事务无成功耐久结果可重放；重试允许重新求值与抽签，但只一次持久奖励。 |
| A1 | 归档版本 | 既有 `cmdresult-v1` / `cmdresult-v2` 字节与严格 EOF 解码不变；旧路由继续 `EncodeV2`。Harvest 成功资产变更使用显式 **`cmdresult-v3`**：在 v2 字段后追加 length-prefixed **业务摘要**（非 Ack/seq/Token/nextOp）。畸形/尾垃圾不得当成功。 |
| A2 | 摘要内容 | `harvest-summary-v1`：原目标 `plotId`、该项 `PlotResult.code`（成功须 0）、实际抽中非空 `gained[(itemId,count)]`、原规格 `expGained`。读盘路径（含手工编码恶坏输入）独立校验；不得仅靠 Encode 参数限制绕过。`HarvestResp.Results/Gained/ExpGained` 只从该冻结摘要 + 冻结 Batch 目标 Empty 地块还原，**禁止**从绝对后态 bag、升级后 exp、活配置倒算。失败无假 gained/exp/成功 Plot。 |
| A3 | 重放 / 发布 | 通用重放恢复原冻结 delta + 摘要，零规则/RNG；较新购买/重启后旧 key 仍回放原选中产量。Query `result_blob` 保持原始归档字节。Ack/seq/server time 仍为投递职责。新 v3 语义缺损（缺/坏摘要、plotResult≠0、空 gained、kind≠Harvest、冻结目标非 Empty）允许 `TryDecode`/GenericDecode 识别后走 **opaque/resync**；**Publisher 在任何 Batch/Resp 发送前明确失败**，不得假作旧 v1/v2 空字段成功。有效 v3 仍成功；旧 v1/v2 严格 EOF 与已保存真实旧字节兼容不可破坏。 |
| E1 | 错误码 | 锁地 **401**；未成熟（含 ready-1）**403**；满包/容量（含升级一次性奖励后）**301**；公开畸形/未知实例/空/多 ID/缺规格/规格不符/不支持公式/畸形元数据/重复存档目标/资格-only Compose 等 BadPacket → 正式面 **101**；legacy Growing 无 accepted：**诊断** `legacy-growing-missing-accepted-harvest-spec`（ADR029），正式面 101，禁止捏造产物。 |
| E2 | 成熟边界 | 以存档 `ReadyTime` 为准：`now < ready` → 403；`now >= ready` 可收（含 100% 照料 `ready==plant` 且 watered/fertilized）。无 care 的零期限或逆序元数据即使存态标 Ripe 也拒绝。 |
| C1 | 提交原子 | 一次 Composer：发奖 + 成长/解锁/一次性工具 + 目标 Empty + **仅移除该目标** accepted harvest；一次 Core 事务写资产/rev/receipt/next/sidecar。满包或后段失败全无；unknown/他块 paid/opaque 全保留。Empty sidecar 删行与等级赠品占格为既有生产行为，本 ADR 不改。 |

## 2. 非目标

- 不实现批量 T034 / 农场 UI T035。
- 不改 Proto/Shared 线契约、冻结 Config200、计划勾选或 RootReview。
- 不声称 Native201 / 新农场 UI 闭环。

## 3. 验证入口（非 Root；Cursor 未执行）

- `M2HarvestPolicyTests` / `M2HarvestCommitTests` / `M2CommandResultArchiveTests`（v3）/ `M2ProjectionPublisherTests`（Harvest v3 发布）
- 故障矩阵：真实 Plant → 单调时钟推进到 ready → 仅 arm Harvest；504 哨兵改 Gather；Worker/矩阵 MinInclusive = 受控抽签，非默认真实分布证明
