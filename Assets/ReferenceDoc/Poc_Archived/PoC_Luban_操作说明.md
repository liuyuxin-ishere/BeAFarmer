<!-- XLUA-MIGRATION:BEGIN -->
> **迁移说明（2026-10-19）：本文件保留历史原文。**
> 当前热更架构调整为 xLua；旧记录中的技术名称和“已通过”仅描述当时环境，不能解释为 xLua 已验证。
> 这是原 PoC 的历史操作记录，原代码与结果保留。与热更实现无关的协议、配置、资源和玩法经验可复用；涉及 HybridCLR、热更 DLL 或补充元数据的操作已由 xLua 方案取代，不应作为当前生产流程执行。
> 当前设计、验证范围和证据见 [xLua 迁移方案与 PoC](../07_xLua迁移方案与PoC.md)。
<!-- XLUA-MIGRATION:END -->

# PoC #7b Luban 接入：配置与测试

> Luban **4.8.0** / Unity **6000.3.22f1** / .NET **8**  
> 对应：`03` §13、`04` §5.4 / §16、计划 `#7b`

---

## 1. 目的

| 目标 | 通过标准 | 结果 |
|---|---|---|
| 基于现有设计 xlsx 生成 **Luban 特供**数据源 | `##var/##type` 一表一文件 + 汇总 xlsx | ✔ |
| Luban 命令行可导出 | `Tools/gen_luban.ps1` 成功 | ✔ |
| 代码 `cs-simple-json` + 数据 `json` | `Assets/Config/Generated` + `Server/ConfigLuban` | ✔ |
| Runtime 源码同源（不装 UPM） | `Assets/Config/LubanRuntime` | ✔ |
| 双端读取 | 服务端 `--luban-smoke` / 编辑器菜单 3 | ✔ |
| `mode=one`（Global） | `TbGlobal.BagInitSlots` 可读 | ✔ |
| 列表字段 | `Crop.stageRatio` 长度 4 | ✔ |
| 联合主键 | `toolId+level` **本版本不可用**，回退 `list` | △ 回退 |
| `#ref` 校验 | 错误 seedId 导出失败（exit≠0） | ✔ |
| 改表重导 | `bag_init_slots` 60→61 服务端读到新值 | ✔ |
| 校验失败不覆盖发布目录 | staging 后再 sync | ✔（见问题） |

**一句话**：从设计簿生成 Luban 工程表，导出强类型 C# + JSON，客户端/服务端同源加载；记录与文档预期不符处及回退。

---

## 2. 执行的操作

### 2.1 生成 Luban 特供表

1. 源：`Assets/ReferenceDoc/云屿小筑_配置表与开发进度.xlsx`（设计/进度簿，**不再作为 Luban 输入**）
2. 脚本：`Tools/migrate_xlsx_to_luban.py`
3. 产出：
   - **一表一文件**：`Shared/config/Datas/*.xlsx`（`##var` / `##type` / `##`）
   - **汇总特供簿**：`Assets/ReferenceDoc/云屿小筑_配置表_Luban.xlsx`（多 Sheet，便于对照）
   - `Shared/config/Defines/__tables__.xlsx`（及 beans/enums 占位）
   - `Shared/config/luban.conf`

主要转换：

- `Global`：KV → `mode=one` 单行宽表；`order_ticket_reward` 拆为 `order_ticket_at_3/6`
- `Crop` 等：`C01` → `id=1` + `code=C01`
- `Item.type` 等：映射到文档枚举名后，因 enums 表头问题 **暂改 string**
- 材料列：合并为 `id:n;…` 字符串（ItemCount Bean 未接通）

### 2.2 安装工具与 Runtime

- 下载 Luban **4.8.0** → `Tools/Luban/`（`VERSION` 文件记录）
- 从 `luban_unity` 拷贝 Runtime 源码 → `Assets/Config/LubanRuntime`（**不含** `SimpleJSONUnity.cs`）
- asmdef：`Game.Config`（`noEngineReferences`）
- 服务端工程：`Server/CloudIsle.Config` 同源编译 `Assets/Config/**/*.cs`

### 2.3 导出与验证

```powershell
powershell -File Tools/gen_luban.ps1      # 导出
powershell -File Tools/verify_luban.ps1   # 冒烟+改值+ref
dotnet run --project Server/CloudIsle.Server -c Release -- --luban-smoke
```

Unity 菜单：`Farming/Luban PoC/1~3`

---

## 3. 执行结果

### 3.1 基线读取（服务端）

```
[Luban PoC] Server OK ... bag_init_slots=60 item1001=小麦/8 crop1.growSec=120 stageRatio=4 tools=9
```

### 3.2 改表重导

`global.xlsx` 中 `bag_init_slots`：60 → 61 → 恢复 60；服务端分别读到 61 / 60。

### 3.3 引用校验

故意将 `crop.xlsx` 首行 `seedId=999999`：

```
记录 "TbCrop[1].seedId" = 999999 ... 在引用表:"TbItem" 中不存在
exit code: 1
```

`gen_luban.ps1` 使用 staging 目录，失败时**不**再 sync 到 `Server/ConfigLuban` / `StreamingAssets/ConfigLuban`。

### 3.4 #7b 清单对照

| # | 项 | 结果 |
|---|---|---|
| 1 | Luban 本机运行 | ✔ 4.8.0 |
| 2 | 表头与注册 | ✔ Item/Global/Crop 等 17 表 |
| 3 | 联合主键 / 列式表 | △ Global `one` ✔；Tool 联合主键 ✖→list |
| 4 | 列表 / Bean | △ `stageRatio` 列表 ✔；ItemCount Bean 未用（改 string） |
| 5 | ref 校验 | ✔ |
| 6 | 双端编译 | ✔ Server + Game.Config（Unity 待菜单确认） |
| 7 | 热更程序集内加载 | △ 编辑器菜单可读；Hotfix 挂载未在本 PoC 强制跑通 |
| 8 | 改表重导 | ✔ 服务端；YooAsset 补丁链路未做 |
| 9 | 新旧对比 | △ 未做逐字段自动 diff（值抽查一致） |

---

## 4. 遇到的问题与处理

| # | 问题 | 处理 |
|---|---|---|
| 1 | `index=toolId+level` 报「单主键表不能含多个 key」 | Tool 改为 `mode=list`；访问用 `DataList` |
| 2 | `__beans__` / `__enums__` `*fields/*items` 解析缺 `alias` 列 | 暂空 enums；材料/枚举字段改用 `string`，后续按官方样例重填 |
| 3 | `list#sep=;,int` 非法 | 改为 `(list#sep=;),int` |
| 4 | `Level` 类型名与字段 `level` 冲突 | `value_type` 改为 `LevelRow` |
| 5 | `#ref=Item` 找不到表 | 改为 `#ref=TbItem`（表全名） |
| 6 | 校验失败仍可能写出 staging/Output | `gen_luban.ps1` 先写 `_staging_json`，成功后再拷贝发布目录 |
| 7 | 文档倾向枚举/ItemCount，本机样例表头难一次写对 | 记入回退：string 列；M1 再补 `__enums__`/`ItemCount` |

---

## 5. 关键路径

| 用途 | 路径 |
|---|---|
| 设计源（非 Luban 输入） | `Assets/ReferenceDoc/云屿小筑_配置表与开发进度.xlsx` |
| Luban 汇总特供簿 | `Assets/ReferenceDoc/云屿小筑_配置表_Luban.xlsx` |
| Luban 工程 | `Shared/config/`（`luban.conf` / `Datas` / `Defines`） |
| 工具 | `Tools/Luban/`、`migrate_xlsx_to_luban.py`、`gen_luban.ps1`、`verify_luban.ps1` |
| 生成代码 / Runtime | `Assets/Config/Generated`、`Assets/Config/LubanRuntime` |
| 数据发布 | `Server/ConfigLuban`、`Assets/StreamingAssets/ConfigLuban` |

---

## 6. 后续建议

1. 按 DataTables 样例补齐 `__enums__` / `ItemCount`，把 string 列改回强类型。  
2. 确认 Luban 5.x 是否支持联合主键；否则长期用 list + 查询辅助。  
3. 把 `Game.Config` 加入 HybridCLR 热更列表，并做 YooAsset config 包补丁验收（#7b-8）。  
4. 自写管线保留为回退：`Tools/gen_config.py` / `verify_config.ps1`。
