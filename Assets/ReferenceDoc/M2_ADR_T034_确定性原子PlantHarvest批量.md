> Root2026-10-26已验收T034/34 of80。实际1326/353/24与严格来源r2 exit0；新batch的canonicalPlant/Empty与恰一次base→new推进由Root补强，旧single v3/v1/v2约束保持；原轮次未验收说明保留为历史。

# M2 ADR T034：确定性原子 Plant/Harvest 批量

版本：v1.2；日期：2026-10-26。  
依据：[06_M2_开发计划](06_M2_开发计划.md) D08/T034、[05_协议与接口](05_协议与接口.md)、[M2_ADR_T030](M2_ADR_T030_单地块播种.md)、[M2_ADR_T033](M2_ADR_T033_HarvestRNG归档与错误码.md)、`Docs/Delivery/M2/T034_Root_Review.md`（含 Actual r2 six disk counterexamples）。  
状态：**r3 归档一致性修订（Cursor 源码轮；未跑测；T034 未勾选；进度保持 33/80）**。

## 1. 决策

| ID | 主题 | 结论 |
|---|---|---|
| B1 | 公开域边界 | 计划名落地为不可变 `BatchIntent` / `NormalizedFarmBatch`、`FarmBatchNormalizer.Normalize(intent,maxItems)`、`FarmBatchExecutor.Evaluate(source,batch,ctx)` → `BatchDomainResult`。纯求值无 DB/网络/队列；整批共用原 `CommandContext`（同一 Config/Now/Random）；防御拷贝入参与结果；源聚合不可变；禁止跨命令复用可变候选或 RNG 计数器。 |
| B2 | 基数与整包拒绝 | `maxItems=24`。空 / `>24` / 重复 `plotId`（即使种子不同）/ 规范布局畸形或尾垃圾 / 非正 `plotId`（Plant 含非正 `seedId`）→ **整包公开 101**，不进入逐项、不抽签、无部分效果。正但未知稳定实例 → **单项业务拒绝**，永不创建实例。稳定按数值 `plotId` 升序，不按入参顺序。Canonical 排序哈希保持兼容：同 key 置换为同一原操作；改种子/目标仍 112。 |
| B3 | 单项兼容 | `items`/`plot_ids` **恰好 1**：完整保留 T030–T033 成功/拒绝/重放语义（含顶层业务码、单地块 `harvest-summary-v1`、Plant 无摘要 EncodeV2、拒绝 Results 空）。Water/Fertilize 保持单地块协议路由。 |
| B4 | 逐项执行 | `2..24`：对本地演化候选逐项跑既有 Plant/Harvest 策略 + **恰好一次** Composer；仅成功项替换候选，失败项保留该项前候选并继续。容量/成长/一次工具赠品相对**当前本地候选**重算；禁止整批按原聚合求值或失败项清作物/规格。unknown / 他块 paid / opaque / 无关库存货币按单路由守恒。 |
| B5 | Harvest RNG | 仅资格通过项按该项求值时的原 AcceptedHarvestSpec 抽选；实际 RNG 次数如实累计（含抽后容量失败与聚合溢出等异常拒绝）。资格失败不抽。每命令独立 `CallCountingRandomSource` 装饰，禁止跨命令共享可变计数。默认 `ServerRandomSource` 不改为 Min；受控 Worker 必须显式标注。成功项只清自己的作物与 paid；满包失败两项都保留。顺序成长与一次赠品按累积候选发生；原 200 paid 可在真实 201 删 Crop 下按原规格结算。 |
| T1 | 顶层 code | 语法已接受的批量（`2..24`）：顶层 **code=0 表示已处理（Recorded）**，不表示逐项全成功；部分/全部业务失败仍 Recorded。单项（`count==1`）拒绝语义不变（顶层=该项业务码）。逐项原 code 按稳定顺序完整返回。 |
| T2 | Results / 奖 | 成功 `PlotResult`：该项冻结后态 Plot（自冻结 Delta 按 plotId 提取）；失败不得捏造成功 Plot。Harvest `Gained`/`ExpGained` = 成功项原抽中奖励的 **checked 聚合**（归档显式冻结），禁止从后态 bag / 升级后 exp / 活配置倒算。提交前守卫整数溢出，禁止回绕计数。 |
| C1 | 一次事务 | 一玩家 lane 受理/求值；**一次** Core 事务、一份回执、一次 nextOp。任一项成功 → 资产 revision **恰 +1**，冻结 delta 跨原源→最终候选。全部业务失败 → Recorded、nextOp 推进、revision/资产/规格字节不变，完整逐项结果仍原样重放。禁止逐项回执/提交/独立 Save/嵌套队列。基建 DB 故障回滚此前本地成功项与 receipt/next；重试不得双结耐久奖。 |
| A1 | 归档版本 | 严格保留 `cmdresult-v1`/`v2` 原字节与 EOF；T033 成功单地块 Harvest 仍用 **`cmdresult-v3` + `harvest-summary-v1`**，不得放宽畸形摘要/头/冻结目标校验。批量（Plant/Harvest `2..24`，含全失败）使用 **`cmdresult-v3` + `farm-batch-summary-v1`**。Query 返回原 raw receipt 字节。重放零规则/RNG；Ack/seq/next/ServerTime 仍为投递职责。 |
| A2 | farm-batch-summary-v1 布局 | 大端：`lp-utf8 "farm-batch-summary-v1"` + `i32 kind(1=Plant,2=Harvest)` + `i32 n(2..24)` + 每项 `(plotId, code, gainN, (itemId,count)*gainN, exp)`（稳定升序）+ `i32 aggGainN` + 聚合 `(itemId,count)*` + `i32 aggExp` + **严格 EOF**。Plant 项 `gainN=0` 且 `exp=0`；失败项 gains 空/`exp=0`；Harvest 成功项 gains 非空。聚合仅含成功 Harvest 原抽中量的 checked 和；全失败时聚合空/`aggExp=0`。读盘独立校验，不得靠 Encode 参数绕过。 |
| A3 | 无变更重放 | 全失败：`EncodeV3(code=0, revision=原值, projection=null, delta=null, summary=farm-batch-summary-v1)`。读盘/GenericReplay：**二者必须皆 null**；夹带任一非空 → 拒发/resync，不得静默丢弃矛盾资产后当成功。有成功项须冻结 delta，且 Plots 内 plotId **唯一**（禁止 first-match 歧义）；合法无关唯一地块与未知字段可保留。 |
| A4 | farm-batch 一致性（r3） | **仅** `farm-batch-summary-v1`：header `committedRevision`/`configVersion` 与 projection.Revision、delta.NewRevision/ConfigVersion 一致；成功 Plant 冻结目标须 **canonical Growing**（`PlotGrowing` + `CropId>0`），不得 Empty 占位。GenericReplay 无外部 key，只验可证明的 header/projection/delta；**Publisher** 另验投递 `scopeId`/`opId` 与 delta、以及 ReceiptDetails 与归档头。截断新 v3（前缀可识别但 `TryDecodeV3=false`）→ 拒发，**禁止**当 legacy 空成功。全失败 raw 夹带 delta：即使 GenericReplay detach 后 `FrozenDelta=null`，Publisher **仍查原 archivedDelta** 拒发。不回溯收紧单地块 `harvest-summary-v1` / 严格 v1/v2 / null-blob 单元路径。 |
| P1 | 发布 | Publisher 在任何 Batch/Resp 发送前校验整份摘要/头/冻结关系；**识别为新 Plant/Harvest cmdresult-v3 后禁止回退 legacy 空 Results 假成功**（坏摘要、kind 不匹配、截断 v3、全失败夹带 delta/projection（含 raw archive）、冻结重复 plot、Growing/revision/config/scope/op 不一致均抛错拒发）。有效旧 v1/v2 与 T033 harvest-summary-v1 严格兼容。Handler 单目标校验对批量返回 null 时不得静默丢 Results——批量必须走摘要。客户端仍只消费 Batch 资产；无 Proto/Shared/冻结 JSON 变更。 |
| F1 | 故障矩阵 | 生产 `EconomyFaultRoute.PlantBatch` / `HarvestBatch` 真实 Host/Core/SQLite/FaultWorker：各四场景（rollback+retry、samekey/diffbody、commit/drop/query/replay、exact AfterCommitBeforeDeliver owned 进程强杀同 DB 恢复）。HarvestBatch 须真实 Plant 批量 paid 后固定时钟成熟再 Arm。原八路由全保留；禁止单块代批量/FakeGoldBump/造杀标记/throw 冒强杀。每命令独立 RNG、精确 probe MatchKey、受控 Min 与默认 RNG 分离、十条生产场景路线保持。 |

## 2. 非目标

- 不实现农场 UI T035 / Gather 等无关模块。
- 不改 Proto/Shared 线契约、冻结 Config200、计划勾选、`T034_Root_Review` 或 Root 私有夹具。
- 不声称 Cursor 已跑测通过或 Native 新农场 UI 闭环。

## 3. 验证入口（非 Root；Cursor 未执行）

- `M2FarmBatchNormalizerTests` / `M2FarmBatchExecutorTests` / `M2FarmBatchCommitTests` / Archive+Publisher：截断 Plant v3、Empty≠Growing、revision/config/scope·op、全失败 raw delta 经 detach 后拒发；正例 fixture 含一致 revision/config/scope/op/projection
- 故障矩阵：原八路由 + `PlantBatch`/`HarvestBatch` 四场景；受控 Min Worker 显式标注，与默认 RNG 分离；r2 已绿 HostKill2/RNG/并发/旧 T033 578 字节 v3 行为保持
- **Cursor 未跑测；不得声称绿。** Root 导入后实跑验收。
