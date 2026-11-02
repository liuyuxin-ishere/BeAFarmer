# M2 ADR T029：AcceptedTaskState 格式与 schema7 迁移

版本：v1.1；日期：2026-10-25。  
依据：[06_M2_开发计划](06_M2_开发计划.md) P03/D05/P02、[T029_Root_Review](../../Docs/Delivery/M2/T029_Root_Review.md)。  
状态：**已写入（待 Root 独立复跑/验收）**。本 ADR 不表示 T029 已勾选；进度保持 28/80。Shared `CropSpec`/`GameConfigSnapshot.Crops` 为 runtime 输入，需新 GenAll/Native（不改旧 T028 指纹）。

## 1. 决策

| ID | 主题 | 结论 |
|---|---|---|
| S7 | SQLite schema | **新增 schema7**：表 `player_accepted_task(player_id PK, format_version, body)`。不改写已验收 schema3/5/6 语义；旧库无行 = 空 `AcceptedTaskState`。 |
| F1 | Codec 格式 | `AcceptedTaskStateCodec.CurrentFormatVersion = 1`；列 `format_version` 与 body 头一致；高格式/损坏/尾垃圾/重复 plotId → 抛错，**不得**静默降为空。 |
| C1 | Capture | `AcceptedHarvestSpec.Capture(CropSpec, configVersion)` 深拷贝产物 ID、产量区间、经验、生长秒、种子、解锁等级、公式身份 `crop-yield-range-v1` 与配置版本；**不持有**可变表引用。 |
| L1 | Legacy 无规格 | 存量 Growing 且无对应 accepted harvest：**禁止**用当前 Crop 表捏造已付费产物；T033 收获须拒绝（诊断 `legacy-growing-missing-accepted-harvest-spec`）。T029 只定义策略与仓储投影。 |
| T1 | 事务边界 | accepted body 与 player base / snapshot / shop sidecar / receipt / next / daily marker **同事务**；禁止无 guard 独立写 sidecar；禁止经 `PlayerStore.Save` 经济旁路抬版本写规格。 |
| U1 | 未知存量 | envelope 内 unknown key/value 原样往返；重复实例拒绝构造/解码。公开 `UnknownEntries` 的 value 为独立字节副本；构造输入数组与返回数组均不得改到内部存档；Encode 经内部只读路径，避免每次整表再拷。 |
| P1 | Locked/Empty 投影 | `plotId<=0` 或带 crop/plant/ready/watered/fertilized 活元数据 → `PlotPresentation.Unknown`；源字段完全保留，不得呈现为可操作空地/锁地，也不得静默清洗后当合法解锁。T025 正常解锁与 Growing 成熟边界不变。 |
| R1 | 配置删改 | 兼容补丁可删/改 Crop；200 已 Capture 规格仍足以结算。不得删改 200 稳定 Item/stack/Map 身份（既有 P02/V03 契约）。 |

## 2. 非目标（本任务）

- 不注册 Plant / Water / Fertilize / Harvest 成功路由（仍 504）。
- 不声称 Native201 / 真实收获闭环通过。
- 不修改冻结 Config200 JSON；隔离 201 仅测试夹具副本。

## 3. 空缺默认

| 情形 | 默认 |
|---|---|
| 从低于 schema7 升级后无 accepted 行 | `AcceptedTaskState.Empty` |
| Empty 状态提交 | 删除 sidecar 行（与商店零计数删行一致） |
| 格式 &gt; Current | 拒绝加载，不改库内容 |

## 4. 验证夹具（非 Root）

- `M2FarmStateTests` / `M2AcceptedHarvestSpecTests` / `M2AggregatePersistenceTests`
- 既有 `SqliteStoreTests` 扩展至 CurrentVersion=7
