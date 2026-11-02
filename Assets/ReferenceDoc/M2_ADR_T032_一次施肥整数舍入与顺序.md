# M2-ADR-T032：一次施肥整数舍入、扣肥与照料顺序

**状态：** Accepted（实现冻结；Root 未跑测试前不宣称验收）  
**日期：** 2026-10-25  
**关联：** D07 / M2-T032；承接 ADR T031 浇水整数网格  
**修订：** 2026-10-25 — 100% 同播种毫秒立即成熟表示与验证入口对齐（full ratio amendment）

## 决策

1. **比例网格与 T031 同源。** `fertilize_ratio` 配置字符串精确解析为万分比分子 / 分母 10000；正式 Config200 文本 `0.3`（及等价 `0.30`/`0.3000`）→ 分子 **3000**。禁止 float/double 或静默四舍五入；超过 4 位小数或越界失败。合法分子含 **0 与 10000**（不得为修边界而收窄）。
2. **缩时公式。** `remaining = readyTime - nowMs`（处理时当前存档期限）；`reduction = floor(remaining * numerator / 10000)`，商余防 Int64 乘溢出；`newReady = readyTime - reduction`。每次 Water/Fertilize **只**作用其处理时刻的剩余，**不**回取种植原时长。
3. **1ms 边界。** 剩余 1ms 时 30% 向下取整可为 0：仍扣权威肥料 **5001×1**、置 `fertilized=true`，期限可不变。`now >= ready`（成熟）拒绝：不扣肥、不改期限、不置标志；正式面码 **101**（禁 403）。
4. **成熟优先于重复。** 与 T031 Water 同序：先投影/显式成熟拒绝，再判已施肥 **405**。锁地 **401**；缺肥 **302**；缺 accepted / 规格错配 / 畸形 / 未配置施肥规则 → BadPacket→101，**不冒造**规格或物品。
5. **先水后肥 / 先肥后水。** 二者各自按**当时**剩余毫秒缩时；先执行者缩短后的 `readyTime` 成为后执行者的输入。标志互相独立保留（水不改 fertilized，肥不改 watered）。
6. **权威肥料。** itemId **5001** 须在当前快照物品表可解析（`TryGetItemStackMax`）；FromSnapshot 缺项失败。成功路径：Policy 声明消耗 + Composer 单次 `itemCosts` 与 `PlotReplace` 同事务；失败整单回滚。
7. **响应冻结。** `FertilizeResp.Plot` 仅自 FrozenDelta 按原请求 plotId 提取克隆；不得塞全表或重算。旧调用省略 targetPlotId 时 Plot 留空（兼容）。
8. **100% 同播种毫秒 → 合法立即成熟（表示冻结）。**
   - 合法 paid Growing 在 `plantTime == nowMs` 时，Water 或 Fertilize 分子 **10000/10000** 可将剩余压到 0，得 `readyTime == plantTime == nowMs`。
   - **存态仍为 Growing**；不改 `plantTime`、不把 `readyTime` 夹到 `now+1`、不禁止 100%。
   - **规范不变量**（`FarmStatePolicy.IsValidGrowingInvariant`）：正 plotId/正 crop、`plantTime≥0`；`readyTime > plantTime`，**或** `readyTime == plantTime` 且 `watered || fertilized`（照料证据）。
   - **投影**：满足不变量且 `nowMs ≥ readyTime` → **Ripe**（含立即成熟）；`ready-1` 仍 Growing。
   - **仍拒绝**：`readyTime < plantTime`（逆序）；`readyTime == plantTime` 且未水未肥（无 care 零期限畸形）→ Project=Unknown；Composer `PlotReplace` 亦拒。
   - Plant 路径仍禁止种出 `ready ≤ now`；本条只覆盖照料成功后的期限塌缩。

## 验证入口（须同不变量，禁止只修只读 Project）

| 入口 | 行为 |
|------|------|
| `FarmStatePolicy.IsValidGrowingInvariant` / `Project` | 唯一规范判定；立即成熟 → Ripe；无 care 零期限 / 逆序 → Unknown |
| `CropCarePolicy.Water` / `Fertilize` | 算术 `newReady ∈ [nowMs, oldReady]`；成功产出须满足上述不变量（100% 同毫秒置对应 care 标志） |
| `PlotReplacePatch.TryValidateStructure` | Growing 调用同一 `IsValidGrowingInvariant`；合法立即成熟可 Compose；非法等时/逆序拒绝，源不变 |
| `FarmCommandApplication` Water/Fertilize | Policy 成功后单次 Composer；须接受立即成熟候选；paid/opaque/他块/plantTime/crop 守恒 |
| 持久化 / Init 映射 | 按字段落库与装载；不额外强制 `ready>plant`；负时间仍拒。Composer 已拒的畸形不得进入成功提交 |

普通测试须覆盖：Water/Fertilize **两种** 100% 同毫秒纯规则 + 完整 Composer；source 指纹不变；paid/unknown 守恒；非法等时（无 care / 逆序）Project=Unknown 且 Compose 拒绝。

## 非目标

- Harvest / 批量 / 客户端倒计时权威 / Proto·冻结配置变更。
- 改写已验收 T031 Water 规则与返回值语义（含成熟优先、1ms 置标志、非 100% 同毫秒路径）。
- 为修边界改 plantTime、禁止分子 10000、或削弱 Root 断言。
