# M2 ADR T031：一次浇水整数舍入与成熟拒绝码

版本：v1.0；日期：2026-10-25。  
依据：[06_M2_开发计划](06_M2_开发计划.md) D07/T031、[03_数值与配置表](03_数值与配置表.md) `water_ratio`、[05_协议与接口](05_协议与接口.md)、[T031_Root_Review](../../Docs/Delivery/M2/T031_Root_Review.md)。  
状态：**已写入（待 Root 独立复跑/验收）**。本 ADR 不表示 T031 已勾选；进度保持 **30/80**。

## 1. 决策

| ID | 主题 | 结论 |
|---|---|---|
| R1 | 比例来源 | Config200 `Globals.Globals["water_ratio"]` 配置字符串；正式值 **0.10**（表/导出可为 `"0.1"`）。权威 `CropCareRules.FromSnapshot` 精确解析，**禁止** float/double 或默默舍精度。 |
| R2 | 万分比 | `denominator = 10000`；分子 `numerator ∈ [0, 10000]`。`"0.1"`/`"0.10"`/`"0.1000"` → 1000；超过 4 位小数或越界 → 装载/求值显式失败。 |
| R3 | 缩时公式 | `remainingMs = readyTime - nowMs`（须 &gt; 0）；`reduction = floor(remainingMs * numerator / denominator)`；`newReady = oldReady - reduction`。用商余（`q=remaining/denom`，`r=remaining%denom`，`reduction=q*num+(r*num)/denom`）避免 Int64 乘溢出；**不**换算秒。 |
| R4 | 1ms 边界 | 剩余 **1ms** 且 10%：`reduction=0`，deadline **不变**，但 `watered=true` 恰好一次。 |
| R5 | 成熟拒绝 | `nowMs >= readyTime`（含投影 Ripe / 存态 Ripe）**拒绝**，不得把 deadline 改回未来。 |
| R6 | 成熟公开码 | **05 无独立「照料时已成熟」码**。域诊断 `care-already-mature` + 域码 `BadPacket(103)`；正式面映射 **101 ERR_PARAM**。**禁止**借用 **403 ERR_CROP_NOT_RIPE**（含义相反：未成熟）。已浇水用 **405**；锁地 **401**。 |
| R7 | 效果范围 | 仅目标地块 `watered` + `readyTime`；保留 `plantTime`/`cropId`/`fertilized`、全未知字段/旧地块、accepted 原始规格与 opaque、商店配额/背包/货币/经验/其他模块。无物品/货币消耗。 |
| R8 | 已付费独立 | 不得从当前 Crop 重算原生长/奖励；隔离 201 删 Crop 后仍按原存 deadline/spec 浇水。新显式 Water 用**当前**已验证 ratio；被动换配置/离线/重开不得改已付费 deadline。 |
| R9 | 提交 | 一次 Composer 候选 + 一次 Core 提交，与 rev/receipt/next 原子。同 key 重放不再缩时；异 body **112**；并发只能一次成功。 |
| R10 | 响应 | 正式 `WaterResp.Plot` = 冻结目标 Plot 克隆（自 `FrozenDelta.Plots` 按原请求 plotId 提取）；模型仍只消费 Batch。 |

## 2. 非目标（本任务）

- 不注册 Fertilize / Harvest / batch。
- 不另起队列/事务/仓储。
- 不改 Proto / Shared 编码器形状 / 客户端以求方便。
- 不声称 Harvest / Native201 / 测试已绿。

## 3. 空缺默认

| 情形 | 默认 |
|---|---|
| 缺独立成熟 care 码 | 101 + ADR 本表 R6；待协议增码前不借用 403 |
| ratio 缺键/畸形 | `CropCareRules.FromSnapshot` 抛错；运行中规则无效 → BadPacket/101 |
| 缺/不匹配 accepted | BadPacket/101；不发明输出、不清洗旧数据 |
