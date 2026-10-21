#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从设计 xlsx 生成 Luban 特供 Datas（一表一文件）+ 汇总 xlsx + Defines。"""
from __future__ import annotations

import re
import sys
from pathlib import Path

try:
    import openpyxl
    from openpyxl import Workbook
except ImportError:
    import subprocess

    subprocess.check_call([sys.executable, "-m", "pip", "install", "openpyxl", "-q"])
    import openpyxl
    from openpyxl import Workbook

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "Assets" / "ReferenceDoc" / "云屿小筑_配置表与开发进度.xlsx"
OUT_DIR = ROOT / "Shared" / "config"
DATAS = OUT_DIR / "Datas"
DEFINES = OUT_DIR / "Defines"
COMBINED = ROOT / "Assets" / "ReferenceDoc" / "云屿小筑_配置表_Luban.xlsx"

ITEM_TYPE_MAP = {
    "crop": "Crop",
    "seed": "Seed",
    "product": "Craft",
    "gather": "Gather",
    "gather_rare": "Gather",
    "consumable": "Consumable",
    "tool": "Tool",
    "currency": "Currency",
    "furniture": "Furniture",
}

PART_MAP = {
    "hair": "Hair",
    "top": "Top",
    "bottom": "Bottom",
    "shoes": "Shoes",
    "hat": "Hat",
    "acc": "Acc",
    "dress": "Dress",
    "发型": "Hair",
    "上衣": "Top",
    "下装": "Bottom",
    "鞋子": "Shoes",
    "帽子": "Hat",
    "饰品": "Acc",
    "连衣裙": "Dress",
}

SOURCE_MAP = {
    "初始": "Initial",
    "商店": "Shop",
    "抽卡": "Gacha",
    "initial": "Initial",
    "shop": "Shop",
    "gacha": "Gacha",
}


def code_to_int(code) -> int:
    if code is None:
        return 0
    if isinstance(code, (int, float)):
        return int(code)
    s = str(code).strip()
    m = re.search(r"(\d+)$", s)
    if not m:
        raise ValueError(f"cannot parse code: {code}")
    return int(m.group(1))


def write_sheet(ws, headers, types, comments, rows):
    ws.append(["##var", *headers])
    ws.append(["##type", *types])
    ws.append(["##", *comments])
    for row in rows:
        ws.append([None, *row])


def save_table(name: str, headers, types, comments, rows, combined_wb: Workbook):
    wb = Workbook()
    ws = wb.active
    ws.title = name
    write_sheet(ws, headers, types, comments, rows)
    path = DATAS / f"{name}.xlsx"
    wb.save(path)

    cws = combined_wb.create_sheet(name)
    write_sheet(cws, headers, types, comments, rows)
    print(f"  OK {name}: {len(rows)} rows -> {path.name}")


def materials(mat_pairs):
    parts = []
    for mid, n in mat_pairs:
        if mid is None or n is None:
            continue
        if int(n) == 0:
            continue
        parts.append(f"{int(mid)}:{int(n)}")
    return ";".join(parts)


def main():
    if not SRC.is_file():
        print(f"missing {SRC}", file=sys.stderr)
        return 1

    DATAS.mkdir(parents=True, exist_ok=True)
    DEFINES.mkdir(parents=True, exist_ok=True)

    src = openpyxl.load_workbook(SRC, data_only=True)
    combined = Workbook()
    combined.remove(combined.active)

    # ---- Global (mode=one, horizontal) ----
    gmap = {}
    for r in range(4, src["Global"].max_row + 1):
        k = src["Global"].cell(r, 1).value
        v = src["Global"].cell(r, 2).value
        if k:
            gmap[str(k)] = v
    # split order_ticket_reward 3:1;6:1
    ticket = str(gmap.get("order_ticket_reward", "3:1;6:1"))
    at3, at6 = 1, 1
    for part in ticket.split(";"):
        if ":" in part:
            a, b = part.split(":", 1)
            if a.strip() == "3":
                at3 = int(float(b))
            if a.strip() == "6":
                at6 = int(float(b))

    g_headers = [
        "bag_init_slots",
        "bag_expand_step",
        "bag_expand_costs",
        "stack_default",
        "water_ratio",
        "fertilize_ratio",
        "speedup_sec_per_gem",
        "daily_reset_hour",
        "order_daily_count",
        "order_reroll_free",
        "order_reroll_costs",
        "order_ticket_at_3",
        "order_ticket_at_6",
        "order_all_done_gem",
        "rare_bonus_chance",
        "plaza_unlock_level",
        "gacha_unlock_level",
        "plaza_line_capacity",
        "chat_interval_sec",
        "new_player_gold",
        "new_player_gem",
        "new_player_ticket",
    ]
    g_types = [
        "int",
        "int",
        "list#sep=;,int",
        "int",
        "float",
        "float",
        "int",
        "int",
        "int",
        "int",
        "list#sep=;,int",
        "int",
        "int",
        "int",
        "float",
        "int",
        "int",
        "int",
        "int",
        "int",
        "int",
        "int",
    ]
    g_row = [
        int(gmap.get("bag_init_slots", 60)),
        int(gmap.get("bag_expand_step", 10)),
        str(gmap.get("bag_expand_costs", "")),
        int(gmap.get("stack_default", 99)),
        float(gmap.get("water_ratio", 0.1)),
        float(gmap.get("fertilize_ratio", 0.3)),
        int(gmap.get("speedup_sec_per_gem", 300)),
        int(gmap.get("daily_reset_hour", 0)),
        int(gmap.get("order_daily_count", 6)),
        int(gmap.get("order_reroll_free", 1)),
        str(gmap.get("order_reroll_costs", "")),
        at3,
        at6,
        int(gmap.get("order_all_done_gem", 20)),
        float(gmap.get("rare_bonus_chance", 0.1)),
        int(gmap.get("plaza_unlock_level", 7)),
        int(gmap.get("gacha_unlock_level", 9)),
        int(gmap.get("plaza_line_capacity", 100)),
        int(gmap.get("chat_interval_sec", 3)),
        int(gmap.get("new_player_gold", 200)),
        int(gmap.get("new_player_gem", 300)),
        int(gmap.get("new_player_ticket", 5)),
    ]
    save_table(
        "global",
        g_headers,
        g_types,
        g_headers,
        [g_row],
        combined,
    )

    # ---- Item ----
    ws = src["Item"]
    rows = []
    for r in range(4, ws.max_row + 1):
        iid = ws.cell(r, 1).value
        if iid is None:
            continue
        raw_type = str(ws.cell(r, 3).value or "").strip()
        rows.append(
            [
                int(iid),
                str(ws.cell(r, 2).value or ""),
                ITEM_TYPE_MAP.get(raw_type, "Crop"),
                int(ws.cell(r, 4).value or 0),
                int(ws.cell(r, 5).value or 0),
                int(ws.cell(r, 6).value or 0),
                int(ws.cell(r, 7).value or 0),
                str(ws.cell(r, 8).value or ""),
            ]
        )
    save_table(
        "item",
        ["id", "name", "type", "stackMax", "sellPrice", "buyPrice", "unlockLevel", "desc"],
        ["int", "string", "ItemType", "int", "int", "int", "int", "string"],
        ["物品ID", "名称", "类型", "堆叠", "售价", "购价", "解锁", "说明"],
        rows,
        combined,
    )

    # ---- Crop ----
    ws = src["Crop"]
    rows = []
    for r in range(4, ws.max_row + 1):
        code = ws.cell(r, 1).value
        if not code:
            continue
        rows.append(
            [
                code_to_int(code),
                str(code),
                str(ws.cell(r, 2).value or ""),
                int(ws.cell(r, 3).value),
                int(ws.cell(r, 4).value),
                int(ws.cell(r, 5).value or 0),
                int(ws.cell(r, 6).value or 0),
                int(ws.cell(r, 7).value or 0),
                int(ws.cell(r, 8).value or 0),
                int(ws.cell(r, 9).value or 0),
                str(ws.cell(r, 10).value or "25;25;25;25"),
            ]
        )
    save_table(
        "crop",
        [
            "id",
            "code",
            "name",
            "seedId",
            "yieldItemId",
            "unlockLevel",
            "growSec",
            "yieldMin",
            "yieldMax",
            "exp",
            "stageRatio",
        ],
        [
            "int",
            "string",
            "string",
            "int#ref=item",
            "int#ref=item",
            "int",
            "int",
            "int",
            "int",
            "int",
            "list#sep=;,int",
        ],
        ["id", "code", "名称", "种子", "产出", "解锁", "生长秒", "产min", "产max", "经验", "阶段比"],
        rows,
        combined,
    )

    # ---- Tool (joint key) ----
    ws = src["Tool"]
    rows = []
    for r in range(4, ws.max_row + 1):
        tid = ws.cell(r, 1).value
        if tid is None:
            continue
        mats = materials(
            [
                (ws.cell(r, 5).value, ws.cell(r, 6).value),
                (ws.cell(r, 7).value, ws.cell(r, 8).value),
            ]
        )
        rows.append(
            [
                int(tid),
                int(ws.cell(r, 2).value),
                int(ws.cell(r, 3).value or 0),
                int(ws.cell(r, 4).value or 0),
                mats,
                int(ws.cell(r, 9).value or 0),
                float(ws.cell(r, 10).value or 0),
            ]
        )
    save_table(
        "tool",
        ["toolId", "level", "needPlayerLevel", "goldCost", "materials", "yieldBonusMax", "rareChance"],
        ["int#ref=item", "int", "int", "int", "list#sep=;,ItemCount", "int", "float"],
        ["工具物品", "等级", "需求等级", "金币", "材料", "产量加成", "稀有率"],
        rows,
        combined,
    )

    # ---- Recipe ----
    ws = src["Recipe"]
    rows = []
    for r in range(4, ws.max_row + 1):
        code = ws.cell(r, 1).value
        if not code:
            continue
        fac = ws.cell(r, 3).value
        inputs = materials(
            [
                (ws.cell(r, 5).value, ws.cell(r, 6).value),
                (ws.cell(r, 7).value, ws.cell(r, 8).value),
            ]
        )
        out = materials([(ws.cell(r, 9).value, ws.cell(r, 10).value)])
        rows.append(
            [
                code_to_int(code),
                str(code),
                str(ws.cell(r, 2).value or ""),
                code_to_int(fac),
                int(ws.cell(r, 4).value or 0),
                inputs,
                out,
                int(ws.cell(r, 11).value or 0),
                int(ws.cell(r, 12).value or 0),
            ]
        )
    save_table(
        "recipe",
        ["id", "code", "name", "facilityId", "unlockLevel", "inputs", "output", "durationSec", "exp"],
        [
            "int",
            "string",
            "string",
            "int#ref=facility",
            "int",
            "list#sep=;,ItemCount",
            "ItemCount",
            "int",
            "int",
        ],
        ["id", "code", "名称", "设施", "解锁", "投入", "产出", "耗时", "经验"],
        rows,
        combined,
    )

    # ---- Facility ----
    ws = src["Facility"]
    rows = []
    for r in range(4, ws.max_row + 1):
        code = ws.cell(r, 1).value
        if not code:
            continue
        rows.append(
            [
                code_to_int(code),
                str(code),
                str(ws.cell(r, 2).value or ""),
                int(ws.cell(r, 3).value or 0),
                int(ws.cell(r, 4).value or 0),
                str(ws.cell(r, 5).value or ""),
            ]
        )
    save_table(
        "facility",
        ["id", "code", "name", "unlockLevel", "buildGold", "category"],
        ["int", "string", "string", "int", "int", "string"],
        ["id", "code", "名称", "解锁", "建造费", "类别"],
        rows,
        combined,
    )

    # ---- GatherNode ----
    ws = src["GatherNode"]
    rows = []
    for r in range(4, ws.max_row + 1):
        code = ws.cell(r, 1).value
        if not code:
            continue
        rows.append(
            [
                code_to_int(code),
                str(code),
                str(ws.cell(r, 2).value or ""),
                int(ws.cell(r, 3).value or 0),
                int(ws.cell(r, 4).value or 0),
                int(ws.cell(r, 5).value or 0),
                int(ws.cell(r, 6).value or 0),
                int(ws.cell(r, 7).value or 0),
                int(ws.cell(r, 8).value or 0),
                int(ws.cell(r, 9).value or 0),
                int(ws.cell(r, 10).value or 0),
                float(ws.cell(r, 11).value or 0),
                int(ws.cell(r, 12).value or 0),
            ]
        )
    save_table(
        "gathernode",
        [
            "id",
            "code",
            "name",
            "toolId",
            "unlockLevel",
            "count",
            "itemId",
            "yieldMin",
            "yieldMax",
            "cooldownSec",
            "rareItemId",
            "rareChance",
            "exp",
        ],
        [
            "int",
            "string",
            "string",
            "int#ref=item",
            "int",
            "int",
            "int#ref=item",
            "int",
            "int",
            "int",
            "int#ref=item",
            "float",
            "int",
        ],
        ["id", "code", "名称", "工具", "解锁", "数量", "物品", "产min", "产max", "冷却", "稀有物", "稀有率", "经验"],
        rows,
        combined,
    )

    # ---- FacilityLevel / Level / Plot / Furniture (straight) ----
    def copy_simple(sheet, name, headers, types, keep_cols, transform=None):
        ws = src[sheet]
        rows = []
        for r in range(4, ws.max_row + 1):
            vals = [ws.cell(r, c).value for c in keep_cols]
            if vals[0] is None:
                continue
            if transform:
                vals = transform(vals)
            rows.append(vals)
        save_table(name, headers, types, headers, rows, combined)

    ws = src["FacilityLevel"]
    rows = []
    for r in range(4, ws.max_row + 1):
        lv = ws.cell(r, 1).value
        if lv is None:
            continue
        rows.append(
            [
                int(lv),
                int(ws.cell(r, 2).value or 0),
                int(ws.cell(r, 3).value or 0),
                float(ws.cell(r, 4).value or 0),
                int(ws.cell(r, 5).value or 0),
                materials(
                    [
                        (ws.cell(r, 6).value, ws.cell(r, 7).value),
                        (ws.cell(r, 8).value, ws.cell(r, 9).value),
                        (ws.cell(r, 10).value, ws.cell(r, 11).value),
                    ]
                ),
            ]
        )
    save_table(
        "facilitylevel",
        ["level", "needPlayerLevel", "slots", "speedFactor", "goldCost", "materials"],
        ["int", "int", "int", "float", "int", "list#sep=;,ItemCount"],
        ["等级", "需求等级", "槽位", "速度", "金币", "材料"],
        rows,
        combined,
    )

    copy_simple(
        "Level",
        "level",
        ["level", "needExp"],
        ["int", "int"],
        [1, 2],
        transform=lambda v: [int(v[0]), int(v[1] or 0)],
    )

    copy_simple(
        "Plot",
        "plot",
        ["stage", "needPlayerLevel", "totalPlots", "unlockGold"],
        ["int", "int", "int", "int"],
        [1, 2, 3, 4],
        transform=lambda v: [int(v[0]), int(v[1] or 0), int(v[2] or 0), int(v[3] or 0)],
    )

    copy_simple(
        "Furniture",
        "furniture",
        ["id", "name", "sizeW", "sizeH", "price", "unlockLevel", "comfort"],
        ["int", "string", "int", "int", "int", "int", "int"],
        [1, 2, 3, 4, 5, 6, 7],
        transform=lambda v: [
            int(v[0]),
            str(v[1] or ""),
            int(v[2] or 0),
            int(v[3] or 0),
            int(v[4] or 0),
            int(v[5] or 0),
            int(v[6] or 0),
        ],
    )

    # ---- OrderRule / GachaRule as mode=one ----
    def kv_to_one(sheet, name, field_types: dict):
        ws = src[sheet]
        kv = {}
        for r in range(4, ws.max_row + 1):
            k = ws.cell(r, 1).value
            if k:
                kv[str(k)] = ws.cell(r, 2).value
        headers = list(field_types.keys())
        types = list(field_types.values())
        row = []
        for h, t in field_types.items():
            val = kv.get(h, 0 if t.startswith("int") or t.startswith("float") else "")
            if t.startswith("int"):
                row.append(int(float(val or 0)))
            elif t.startswith("float"):
                row.append(float(val or 0))
            else:
                row.append(str(val or ""))
        save_table(name, headers, types, headers, [row], combined)

    # Keep OrderRule/GachaRule as simplified one-row with string values where unknown
    # Prefer map-style fallback files for robustness: mode=map key/value
    def save_kv_map(sheet, name):
        ws = src[sheet]
        rows = []
        for r in range(4, ws.max_row + 1):
            k = ws.cell(r, 1).value
            if not k:
                continue
            rows.append([str(k), str(ws.cell(r, 2).value if ws.cell(r, 2).value is not None else ""), str(ws.cell(r, 3).value or "")])
        save_table(
            name,
            ["key", "value", "desc"],
            ["string", "string", "string"],
            ["键", "值", "说明"],
            rows,
            combined,
        )

    save_kv_map("OrderRule", "orderrule")
    save_kv_map("GachaRule", "gacharule")

    # ---- AvatarItem ----
    ws = src["AvatarItem"]
    rows = []
    for r in range(4, ws.max_row + 1):
        iid = ws.cell(r, 1).value
        if iid is None:
            continue
        part = str(ws.cell(r, 3).value or "")
        rarity = str(ws.cell(r, 4).value or "N").upper()
        source = str(ws.cell(r, 5).value or "")
        rows.append(
            [
                int(iid),
                str(ws.cell(r, 2).value or ""),
                PART_MAP.get(part, PART_MAP.get(part.lower(), "Acc")),
                rarity if rarity in ("N", "R", "SR", "SSR") else "N",
                SOURCE_MAP.get(source, SOURCE_MAP.get(source.lower(), "Gacha")),
                int(ws.cell(r, 6).value or 0),
            ]
        )
    save_table(
        "avataritem",
        ["id", "name", "part", "rarity", "source", "price"],
        ["int", "string", "AvatarPart", "Rarity", "AvatarSource", "int"],
        ["id", "名称", "部位", "稀有度", "来源", "价格"],
        rows,
        combined,
    )

    # ---- GachaPool ----
    ws = src["GachaPool"]
    rows = []
    for r in range(4, ws.max_row + 1):
        code = ws.cell(r, 1).value
        if not code:
            continue
        rows.append(
            [
                code_to_int(code),
                str(code),
                str(ws.cell(r, 2).value or ""),
                "Limited" if "限" in str(ws.cell(r, 3).value or "") or str(ws.cell(r, 3).value).lower() == "limited" else "Permanent",
                bool(int(ws.cell(r, 6).value or 0)),
                str(ws.cell(r, 5).value or "").replace(",", ";"),
            ]
        )
    save_table(
        "gachapool",
        ["id", "code", "name", "type", "hasUp", "upItemIds"],
        ["int", "string", "string", "PoolType", "bool", "list#sep=;,int"],
        ["id", "code", "名称", "类型", "有UP", "UP物品"],
        rows,
        combined,
    )

    # ---- GachaPoolItem (list) ----
    ws = src["GachaPoolItem"]
    rows = []
    for r in range(4, ws.max_row + 1):
        pool = ws.cell(r, 1).value
        if not pool:
            continue
        rarity = str(ws.cell(r, 2).value or "N").upper()
        rows.append(
            [
                code_to_int(pool),
                rarity if rarity in ("N", "R", "SR", "SSR") else "N",
                int(ws.cell(r, 3).value),
                bool(int(ws.cell(r, 5).value or 0)),
                int(ws.cell(r, 4).value or 1),
            ]
        )
    save_table(
        "gachapoolitem",
        ["poolId", "rarity", "itemId", "isUp", "weight"],
        ["int#ref=gachapool", "Rarity", "int#ref=avataritem", "bool", "int"],
        ["卡池", "稀有度", "外观", "UP", "权重"],
        rows,
        combined,
    )

    # ---- Activity ----
    ws = src["Activity"]
    rows = []
    for r in range(4, ws.max_row + 1):
        code = ws.cell(r, 1).value
        if not code:
            continue
        pool = ws.cell(r, 4).value
        rows.append(
            [
                code_to_int(code),
                str(code),
                str(ws.cell(r, 2).value or ""),
                str(ws.cell(r, 3).value or ""),
                code_to_int(pool) if pool else 0,
                int(ws.cell(r, 5).value or 0),
                int(ws.cell(r, 6).value or 0),
                int(ws.cell(r, 7).value or 0),
                str(ws.cell(r, 8).value or ""),
                str(ws.cell(r, 9).value or ""),
            ]
        )
    save_table(
        "activity",
        [
            "id",
            "code",
            "name",
            "type",
            "poolId",
            "startTime",
            "endTime",
            "preloadSec",
            "resourceBundle",
            "entryIcon",
        ],
        ["int", "string", "string", "string", "int#ref=gachapool", "long", "long", "int", "string", "string"],
        ["id", "code", "名称", "类型", "卡池", "开始", "结束", "预载", "资源包", "入口"],
        rows,
        combined,
    )

    combined.save(COMBINED)
    print(f"COMBINED -> {COMBINED}")

    # ---- Defines: __tables__ / __beans__ / __enums__ ----
    def write_define(path, headers, rows):
        wb = Workbook()
        ws = wb.active
        ws.title = "Sheet1"
        ws.append(["##var", *headers])
        ws.append(["##", *headers])
        for row in rows:
            ws.append([None, *row])
        wb.save(path)

    tables_rows = [
        # full_name, value_type, read_schema_from_file, input, index, mode, comment
        ["TbGlobal", "Global", True, "global.xlsx", None, "one", "全局常量"],
        ["TbItem", "Item", True, "item.xlsx", "id", "map", "物品"],
        ["TbCrop", "Crop", True, "crop.xlsx", "id", "map", "作物"],
        ["TbGatherNode", "GatherNode", True, "gathernode.xlsx", "id", "map", "采集点"],
        ["TbTool", "Tool", True, "tool.xlsx", "toolId+level", "map", "工具等级"],
        ["TbFacility", "Facility", True, "facility.xlsx", "id", "map", "设施"],
        ["TbFacilityLevel", "FacilityLevel", True, "facilitylevel.xlsx", "level", "map", "设施等级"],
        ["TbRecipe", "Recipe", True, "recipe.xlsx", "id", "map", "配方"],
        ["TbLevel", "Level", True, "level.xlsx", "level", "map", "等级"],
        ["TbPlot", "Plot", True, "plot.xlsx", "stage", "map", "地块"],
        ["TbOrderRule", "OrderRule", True, "orderrule.xlsx", "key", "map", "订单规则KV"],
        ["TbFurniture", "Furniture", True, "furniture.xlsx", "id", "map", "家具"],
        ["TbAvatarItem", "AvatarItem", True, "avataritem.xlsx", "id", "map", "外观"],
        ["TbGachaRule", "GachaRule", True, "gacharule.xlsx", "key", "map", "抽卡规则KV"],
        ["TbGachaPool", "GachaPool", True, "gachapool.xlsx", "id", "map", "卡池"],
        ["TbGachaPoolItem", "GachaPoolItem", True, "gachapoolitem.xlsx", None, "list", "卡池物品"],
        ["TbActivity", "Activity", True, "activity.xlsx", "id", "map", "活动"],
    ]
    write_define(
        DEFINES / "__tables__.xlsx",
        ["full_name", "value_type", "read_schema_from_file", "input", "index", "mode", "comment"],
        tables_rows,
    )

    # beans
    wb = Workbook()
    ws = wb.active
    ws.title = "Sheet1"
    ws.append(["##var", "full_name", "value_type", "sep", "alias", "comment", "tags", "group"])
    ws.append(["##", "全名", "类型", "分隔符", "别名", "注释", "标签", "分组"])
    # ItemCount fields need nested definition - Luban beans often defined as:
    # Actually for excel beans, format is different. Use field rows in __beans__
    # MiniTemplate style - check __beans__
    wb2 = openpyxl.load_workbook(
        Path(r"D:\Users\Lenovo\AppData\Local\Temp\luban_examples-main\MiniTemplate\Data\__beans__.xlsx"),
        data_only=True,
    )
    bws = wb2.active
    print("BEANS TEMPLATE:")
    for r in range(1, min(12, bws.max_row) + 1):
        print([bws.cell(r, c).value for c in range(1, min(10, bws.max_column) + 1)])

    # Write ItemCount bean in common excel bean format used by Luban 4:
    # Sheet with ##var full_name / parent / valueType / sep / ...
    # And a second approach: define bean fields in a sheet named by bean
    wb = Workbook()
    # Sheet1 = bean list
    ws = wb.active
    ws.title = "Sheet1"
    ws.append(["##var", "full_name", "parent", "valueType", "sep", "alias", "comment", "group", "tags"])
    ws.append(["##", "全名", "父类", "值类型", "分隔符", "别名", "注释", "分组", "标签"])
    ws.append([None, "ItemCount", None, None, ":", None, "物品数量", None, None])
    # Sheet ItemCount = fields
    fs = wb.create_sheet("ItemCount")
    fs.append(["##var", "name", "type", "group", "comment", "tags"])
    fs.append(["##", "字段名", "类型", "分组", "注释", "标签"])
    fs.append([None, "id", "int", None, "物品ID", None])
    fs.append([None, "count", "int", None, "数量", None])
    wb.save(DEFINES / "__beans__.xlsx")

    # enums
    wb = Workbook()
    ws = wb.active
    ws.title = "Sheet1"
    ws.append(["##var", "full_name", "flags", "unique", "group", "comment", "flags", "tags"])
    # Better follow MiniTemplate __enums__
    ewb = openpyxl.load_workbook(
        Path(r"D:\Users\Lenovo\AppData\Local\Temp\luban_examples-main\MiniTemplate\Data\__enums__.xlsx"),
        data_only=True,
    )
    ews = ewb.active
    print("ENUMS TEMPLATE:")
    for r in range(1, min(15, ews.max_row) + 1):
        print([ews.cell(r, c).value for c in range(1, min(10, ews.max_column) + 1)])

    wb = Workbook()
    ws = wb.active
    ws.title = "Sheet1"
    ws.append(["##var", "full_name", "flags", "unique", "group", "comment", "tags"])
    ws.append(["##", "全名", "是否flags", "是否唯一", "分组", "注释", "标签"])
    for name, comment in [
        ("ItemType", "物品类型"),
        ("AvatarPart", "外观部位"),
        ("Rarity", "稀有度"),
        ("AvatarSource", "外观来源"),
        ("PoolType", "卡池类型"),
    ]:
        ws.append([None, name, False, False, None, comment, None])

    def enum_sheet(book, name, items):
        s = book.create_sheet(name)
        s.append(["##var", "name", "alias", "value", "comment", "tags"])
        s.append(["##", "名称", "别名", "值", "注释", "标签"])
        for i, (n, v) in enumerate(items):
            s.append([None, n, None, v, None, None])

    enum_sheet(
        wb,
        "ItemType",
        [("Seed", 0), ("Crop", 1), ("Craft", 2), ("Gather", 3), ("Tool", 4), ("Consumable", 5), ("Furniture", 6), ("Currency", 7)],
    )
    enum_sheet(wb, "AvatarPart", [("Hair", 0), ("Top", 1), ("Bottom", 2), ("Shoes", 3), ("Hat", 4), ("Acc", 5), ("Dress", 6)])
    enum_sheet(wb, "Rarity", [("N", 0), ("R", 1), ("SR", 2), ("SSR", 3)])
    enum_sheet(wb, "AvatarSource", [("Initial", 0), ("Shop", 1), ("Gacha", 2)])
    enum_sheet(wb, "PoolType", [("Permanent", 0), ("Limited", 1)])
    wb.save(DEFINES / "__enums__.xlsx")

    print("Defines written.")
    return 0


if __name__ == "__main__":
    # Fix FacilityLevel transform - keep_cols wrong; rewrite call inline already
    sys.exit(main())
