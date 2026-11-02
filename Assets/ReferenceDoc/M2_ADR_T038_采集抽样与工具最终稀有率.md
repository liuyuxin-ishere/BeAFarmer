# M2 ADR T038：采集抽样边界与工具最终稀有率

版本：v1.2；日期：2026-10-27。  
依据：[03_数值与配置表](03_数值与配置表.md) §4、[06_M2_开发计划](06_M2_开发计划.md) D09/T038、[T038_Root_Review](../../Docs/Delivery/M2/T038_Root_Review.md)（含第二/三轮具体修订）。  
状态：**已写入（待 Root 独立复跑/验收）**。本 ADR 不表示 T038 已勾选；进度保持 37/80。

## 1. 决策

| ID | 主题 | 结论 |
|---|---|---|
| R1 | 稀有概率来源 | **仅工具当前等级** `Tool.rareChance` 为最终命中率；**不**与 `GatherNode.rareChance` 相加/相乘。节点表稀有率仅作配置投影保留。 |
| R2 | 稀有物身份 | 稀有物品 ID / 经验仍取节点表；命中时恰好 +1 个 `rareItemId`。 |
| Y1 | 普通产量区间 | inclusive `[yieldMin, yieldMax + yieldBonusMax]`；`yieldBonusMax` **扩大上限**，不是每次产量固定加数。退化区间不抽签。 |
| P1 | 概率定点 | 装载时将 `[0,1]` 转为 ppm（分母 `1_000_000`）；`≤0→0`、`≥1→Scale`；中间 `Round AwayFromZero`。抽样：`0` 永不命中且不抽签；`Scale` 必中且不抽签；否则 `NextInt(0,Scale)`，`roll < ppm` 命中。 |
| P3 | 概率 JSON 形态 | GatherNode/Tool 的 `rareChance` **必须存在且为 JSON 数值**（`RequireJsonNumber`）；拒绝缺失、null、字符串、boolean。显式数字 0/1 合法；NaN/Infinity/越界仍拒。旧字段继续走 `GetDouble` 兼容，不全局收紧。 |
| P2 | 越界 RNG | 测试注入若返回值 ∉ `[min,max)`：**拒绝**发奖，不以取模掩盖。 |
| O1 | 抽签次序 | 资格成功后：先普通产量，再稀有 Bernoulli；失败路径在资格前不调用 RNG。 |
| T1 | needPlayerLevel | 仅工具升级门槛（T040）；采集资格**不**用它拒绝已有高等级工具。解锁看节点 `unlockLevel`。 |
| T2 | 节点/工具物品类别与 level1 | `GatherNode.toolId` 与 Tool 表每行 `toolId` 均须引用同一 Item 装载过程中 **`Item.type==Tool`** 的集合（不按 5xxx/stackMax/硬编码 5101–5103 推断）；并继续满足 **Tool level1**。仅 KnownItem+level1 不足——消耗品 5001/货币 9001 即使 Node 与 Tool 同时重指向仍拒绝；缺定义拒绝，不伪造默认工具/不清零冷却规则。 |
| C1 | Composer | 普通+稀有+经验+节点冷却同一 `DomainEffects`，Composer 恰好一次；301/赠品净容量失败全无，冷却不推进。 |

## 2. 非目标

- 不验收 T039 随机结果随回执/丢响应/强杀重放。
- 不注册正式 Gather 网络规则/Native 采集 UI（T041/T042）。
- 不改冻结 200 JSON、生成协议、旧测试断言或原 80 编号勾选。

## 3. 与 Harvest RNG 兼容

复用既有 `IRandomSource` / `ServerRandomSource`（`[min,max)` + 生产锁）及 `HarvestPolicy.TrySelectInclusiveYield`；不改变 Harvest 区间语义。Gather 稀有抽签为新增调用，不改 Shared 生产单例契约。
