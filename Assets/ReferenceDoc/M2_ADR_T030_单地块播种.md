# M2 ADR T030：单地块播种接线

版本：v1.1；日期：2026-10-25。  
依据：[06_M2_开发计划](06_M2_开发计划.md) D06、[T030_Root_Review](../../Docs/Delivery/M2/T030_Root_Review.md)。  
状态：**已写入（待 Root 独立复跑/验收）**。本 ADR 不表示 T030 已勾选；进度保持 **29/80**。  
v1.1：Root 拒绝 v1.0 Y1「Results 暂空」延期；改为单地块成功必须填一项冻结 `PlotResult`。

## 1. 决策

| ID | 主题 | 结论 |
|---|---|---|
| P1 | 规则结果类型 | 计划表 `RuleResult` 在种植侧具体化为 `FarmPlantResult`（与 `FarmUnlockResult`/`TradeResult` 同族）；含 SeedId、Growing `PlotStateView`、完整 `AcceptedHarvestSpec`。 |
| W1 | Wire 基数 | T030 正式 `Plant` 规范业务字节 `items` **必须恰好 1**；0/多块 → 公开 101，批量留给 T034。不另起 Batch 事务。 |
| S1 | 陈旧 accepted | 目标地块若已存在 accepted harvest（即便呈现为空地）→ 拒绝，诊断 `plant-stale-accepted-conflict`；**不得** `WithHarvest` 静默覆盖已付费规格。 |
| C1 | 时钟 | `nowMs < 0` 与 `checked(nowMs + growSec*1000)` 溢出 → BadPacket；客户端 readyTime/产量/经验一律忽略。 |
| R1 | 504 哨兵 | T030 起 `Plant` 生产注册；未实现写入口 504 哨兵改测 `Water`（Harvest/Fertilize/Batch 仍 504）。 |
| Y1 | 响应 Results | **单地块成功必须** `PlantResp.Results` 恰一项：`plotId`=原请求、`code`=0、`plot`=原提交冻结 `PlotData` 克隆。同步/冷重启/同 key 重放仍返回该冻结结果，不得按当前缓存或当前配置重算。`FrozenDelta.Plots` 为全地块列表——只按已哈希校验的原 Plant 唯一 `plotId` 提取，不得全塞 Results 或按 timestamp 猜目标。业务拒绝/畸形请求 Results 为空，不捏造成功项；客户端模型仍只消费 Batch。批量多块 Results 留给 T034；无 Proto/Shared 变更。 |

## 2. 非目标（本任务）

- 不注册 Water / Fertilize / Harvest / batch 成功路由。
- 不改客户端桥/UI；不声称生产 Harvest 或 Native201。
- 不修改冻结 Config200 JSON / Item / Map 身份。

## 3. 空缺默认

| 情形 | 默认 |
|---|---|
| 未知种子 | `ItemInvalid` 306 |
| 锁地 | 05 `ERR_PLOT_LOCKED` 401；与非法实例/脏空地分开 |
| 未知实例/不可用空地 | BadPacket → 公开 101 |
| 非空 Growing/Ripe | `PlotNotEmpty` 402 |
| 等级不足 | `LevelLocked` 108 |
| 无种 | `ItemNotEnough` 302 |

Root最终协议复核更正：首轮把锁地并入101，与05正式错误码401冲突，独立实际2项检查1过1败后修正。401仅在Server域定义常量，不修改既有Proto/Shared/客户端ABI；旧已记录结果仍按原回执重放，不重写历史。Crop的条件目前为直接等级门槛，因此沿用正式108；未另引入独立作物解锁状态。全部无变化/回执/事务与冻结结果断言继续保留。
