using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.UI
{
    /// <summary>
    /// T026 U01：Bag/Shop/HomeHud 生产视图构造。Editor 烘焙 Prefab 与 runtime 缺资源回退共用。
    /// </summary>
    public static class M2U01WindowBuilder
    {
        public static GameObject BuildHomeHud(Transform parent)
        {
            // 底左紧凑角：身份/金币/等级/容量/解锁 + Bag/Shop/Farm/Gather/Tool/Logout。
            // 不盖南侧花丛(2011–2013)投影；不关闭 Image raycast、不做全局 click-through。
            var root = Panel(parent, "HomeHudWindow", new Vector2(0.02f, 0.02f), new Vector2(0.34f, 0.36f),
                new Color(0.10f, 0.16f, 0.12f, 0.95f));
            Text(root.transform, "Title", "家园", new Vector2(0.03f, 0.88f), new Vector2(0.97f, 0.98f), 22);
            Text(root.transform, "PlayerId", "ID:", new Vector2(0.03f, 0.78f), new Vector2(0.50f, 0.88f), 16);
            Text(root.transform, "Nickname", "昵称", new Vector2(0.50f, 0.78f), new Vector2(0.97f, 0.88f), 16);
            // 统计区：Gold 独占全宽一行，避免长 Int64 三行遮住 ID/工具；Level/BagSlots 次行；Unlocks 余高。
            Text(root.transform, "Gold", "金币: 0", new Vector2(0.03f, 0.70f), new Vector2(0.97f, 0.78f), 16);
            Text(root.transform, "Level", "Lv.0", new Vector2(0.03f, 0.62f), new Vector2(0.50f, 0.70f), 16);
            Text(root.transform, "BagSlots", "背包: 0/0", new Vector2(0.50f, 0.62f), new Vector2(0.97f, 0.70f), 14);
            UnlocksText(root.transform, "UnlocksLabel", "解锁", new Vector2(0.03f, 0.36f), new Vector2(0.97f, 0.62f), 14);
            Button(root.transform, "BagButton", "背包", new Vector2(0.02f, 0.19f), new Vector2(0.18f, 0.34f));
            Button(root.transform, "ShopButton", "商店", new Vector2(0.19f, 0.19f), new Vector2(0.35f, 0.34f));
            Button(root.transform, "FarmButton", "农场", new Vector2(0.36f, 0.19f), new Vector2(0.52f, 0.34f));
            Button(root.transform, "GatherButton", "采集", new Vector2(0.53f, 0.19f), new Vector2(0.69f, 0.34f));
            Button(root.transform, "ToolButton", "工具", new Vector2(0.70f, 0.19f), new Vector2(0.86f, 0.34f));
            Button(root.transform, "LogoutButton", "登出", new Vector2(0.02f, 0.03f), new Vector2(0.22f, 0.17f));
            return root;
        }

        /// <summary>
        /// T035：农场控制窗（选种/模式/倒计时/反馈）。
        /// 右侧窄条：倒计时分页可达全部 24 行；打开时不挡灰盒地投影（配合相机略偏左）。
        /// </summary>
        public static GameObject BuildFarm(Transform parent)
        {
            var root = Panel(parent, "FarmWindow", new Vector2(0.70f, 0.02f), new Vector2(0.98f, 0.78f),
                new Color(0.10f, 0.14f, 0.12f, 0.94f));
            Text(root.transform, "Title", "农场", new Vector2(0.04f, 0.90f), new Vector2(0.96f, 0.98f), 26);
            Text(root.transform, "GoldLabel", "金币: 0", new Vector2(0.04f, 0.82f), new Vector2(0.52f, 0.90f), 18);
            Text(root.transform, "LevelLabel", "Lv.0", new Vector2(0.52f, 0.82f), new Vector2(0.96f, 0.90f), 18);
            Text(root.transform, "SeedLabel", "未选种", new Vector2(0.04f, 0.74f), new Vector2(0.96f, 0.82f), 18);
            Button(root.transform, "PrevSeedButton", "上一种", new Vector2(0.04f, 0.65f), new Vector2(0.48f, 0.74f));
            Button(root.transform, "NextSeedButton", "下一种", new Vector2(0.52f, 0.65f), new Vector2(0.96f, 0.74f));
            Text(root.transform, "ModeLabel", "模式: 播种", new Vector2(0.04f, 0.59f), new Vector2(0.96f, 0.65f), 18);
            Button(root.transform, "PlantModeButton", "播种", new Vector2(0.04f, 0.49f), new Vector2(0.27f, 0.59f));
            Button(root.transform, "WaterModeButton", "浇水", new Vector2(0.29f, 0.49f), new Vector2(0.52f, 0.59f));
            Button(root.transform, "FertilizeModeButton", "施肥", new Vector2(0.54f, 0.49f), new Vector2(0.75f, 0.59f));
            Button(root.transform, "HarvestModeButton", "收获", new Vector2(0.77f, 0.49f), new Vector2(0.96f, 0.59f));
            // CountdownLabel 保持 ViewHost 直系字段名；页控保证 720p/1080p 下 24 行均可读可达
            ListText(root.transform, "CountdownLabel", "", new Vector2(0.04f, 0.32f), new Vector2(0.96f, 0.49f), 18);
            Button(root.transform, "CountdownPrevButton", "上页", new Vector2(0.04f, 0.26f), new Vector2(0.48f, 0.32f));
            Button(root.transform, "CountdownNextButton", "下页", new Vector2(0.52f, 0.26f), new Vector2(0.96f, 0.32f));
            // Reserve the complete 24-item feedback area;do not hide later item codes behind Text truncation.
            Text(root.transform, "StatusLabel", "", new Vector2(0.04f, 0.02f), new Vector2(0.68f, 0.26f), 18);
            Button(root.transform, "CloseButton", "关闭", new Vector2(0.70f, 0.02f), new Vector2(0.96f, 0.12f));
            return root;
        }

        public static GameObject BuildBag(Transform parent)
        {
            var root = Panel(parent, "BagWindow", new Vector2(0.12f, 0.12f), new Vector2(0.88f, 0.88f),
                new Color(0.12f, 0.14f, 0.18f, 0.97f));
            Text(root.transform, "Title", "背包", new Vector2(0.05f, 0.90f), new Vector2(0.95f, 0.98f), 30);
            Text(root.transform, "GoldLabel", "金币: 0", new Vector2(0.05f, 0.82f), new Vector2(0.50f, 0.90f), 22);
            Text(root.transform, "CapacityLabel", "容量: 0/0", new Vector2(0.50f, 0.82f), new Vector2(0.95f, 0.90f), 22);
            // 列表区固定视口：Truncate 防溢出；分页由 Presenter 保证每项可选可读。
            ListText(root.transform, "BagListLabel", "", new Vector2(0.05f, 0.42f), new Vector2(0.95f, 0.82f), 18);
            Text(root.transform, "SelectedLabel", "未选中物品", new Vector2(0.05f, 0.34f), new Vector2(0.95f, 0.42f), 18);
            Button(root.transform, "PrevButton", "上一项", new Vector2(0.05f, 0.24f), new Vector2(0.28f, 0.34f));
            Button(root.transform, "NextButton", "下一项", new Vector2(0.30f, 0.24f), new Vector2(0.53f, 0.34f));
            Input(root.transform, "CountInput", "数量", new Vector2(0.55f, 0.24f), new Vector2(0.95f, 0.34f));
            Text(root.transform, "StatusLabel", "", new Vector2(0.05f, 0.16f), new Vector2(0.95f, 0.24f), 20);
            Button(root.transform, "SellButton", "出售", new Vector2(0.05f, 0.04f), new Vector2(0.32f, 0.14f));
            Button(root.transform, "ExpandButton", "扩容", new Vector2(0.36f, 0.04f), new Vector2(0.63f, 0.14f));
            Button(root.transform, "CloseButton", "关闭", new Vector2(0.68f, 0.04f), new Vector2(0.95f, 0.14f));
            return root;
        }

        public static GameObject BuildShop(Transform parent)
        {
            var root = Panel(parent, "ShopWindow", new Vector2(0.12f, 0.12f), new Vector2(0.88f, 0.88f),
                new Color(0.14f, 0.12f, 0.10f, 0.97f));
            Text(root.transform, "Title", "商店", new Vector2(0.05f, 0.90f), new Vector2(0.95f, 0.98f), 30);
            Text(root.transform, "GoldLabel", "金币: 0", new Vector2(0.05f, 0.82f), new Vector2(0.50f, 0.90f), 22);
            Text(root.transform, "CapacityLabel", "背包: 0/0", new Vector2(0.50f, 0.82f), new Vector2(0.95f, 0.90f), 22);
            ListText(root.transform, "ShopListLabel", "", new Vector2(0.05f, 0.44f), new Vector2(0.95f, 0.82f), 18);
            Text(root.transform, "SelectedLabel", "未选中商品", new Vector2(0.05f, 0.36f), new Vector2(0.95f, 0.44f), 18);
            Text(root.transform, "UnitPriceLabel", "", new Vector2(0.05f, 0.30f), new Vector2(0.95f, 0.36f), 18);
            Button(root.transform, "PrevButton", "上一项", new Vector2(0.05f, 0.20f), new Vector2(0.28f, 0.30f));
            Button(root.transform, "NextButton", "下一项", new Vector2(0.30f, 0.20f), new Vector2(0.53f, 0.30f));
            Input(root.transform, "CountInput", "数量", new Vector2(0.55f, 0.20f), new Vector2(0.95f, 0.30f));
            Text(root.transform, "StatusLabel", "", new Vector2(0.05f, 0.12f), new Vector2(0.95f, 0.20f), 20);
            Button(root.transform, "BuyButton", "购买", new Vector2(0.15f, 0.03f), new Vector2(0.48f, 0.11f));
            Button(root.transform, "CloseButton", "关闭", new Vector2(0.52f, 0.03f), new Vector2(0.85f, 0.11f));
            return root;
        }

        /// <summary>T041：采集控制窗（非 modal）；右上窄条，不挡东侧矿石(2004/2010)投影。</summary>
        public static GameObject BuildGather(Transform parent)
        {
            var root = Panel(parent, "GatherWindow", new Vector2(0.80f, 0.45f), new Vector2(0.98f, 0.98f),
                new Color(0.10f, 0.16f, 0.14f, 0.94f));
            Text(root.transform, "Title", "采集", new Vector2(0.04f, 0.90f), new Vector2(0.96f, 0.98f), 22);
            Text(root.transform, "GoldLabel", "金币: 0", new Vector2(0.04f, 0.82f), new Vector2(0.52f, 0.90f), 15);
            Text(root.transform, "LevelLabel", "Lv.0", new Vector2(0.52f, 0.82f), new Vector2(0.96f, 0.90f), 15);
            ListText(root.transform, "NodeListLabel", "", new Vector2(0.04f, 0.48f), new Vector2(0.96f, 0.82f), 15);
            Text(root.transform, "SelectedLabel", "点击家园采集点选择", new Vector2(0.04f, 0.40f), new Vector2(0.96f, 0.48f), 15);
            Text(root.transform, "DetailLabel", "", new Vector2(0.04f, 0.34f), new Vector2(0.96f, 0.40f), 14);
            Text(root.transform, "CooldownLabel", "", new Vector2(0.04f, 0.28f), new Vector2(0.96f, 0.34f), 14);
            Button(root.transform, "PrevButton", "上一点", new Vector2(0.04f, 0.18f), new Vector2(0.48f, 0.28f));
            Button(root.transform, "NextButton", "下一点", new Vector2(0.52f, 0.18f), new Vector2(0.96f, 0.28f));
            Text(root.transform, "StatusLabel", "", new Vector2(0.04f, 0.10f), new Vector2(0.96f, 0.18f), 14);
            Button(root.transform, "GatherButton", "采集", new Vector2(0.04f, 0.02f), new Vector2(0.48f, 0.10f));
            Button(root.transform, "CloseButton", "关闭", new Vector2(0.52f, 0.02f), new Vector2(0.96f, 0.10f));
            return root;
        }

        /// <summary>T041：工具升级窗（modal）；确认由 DialogService。</summary>
        public static GameObject BuildTool(Transform parent)
        {
            var root = Panel(parent, "ToolWindow", new Vector2(0.14f, 0.14f), new Vector2(0.86f, 0.86f),
                new Color(0.12f, 0.14f, 0.18f, 0.97f));
            Text(root.transform, "Title", "工具", new Vector2(0.05f, 0.90f), new Vector2(0.95f, 0.98f), 28);
            Text(root.transform, "GoldLabel", "金币: 0", new Vector2(0.05f, 0.82f), new Vector2(0.50f, 0.90f), 20);
            Text(root.transform, "LevelLabel", "Lv.0", new Vector2(0.50f, 0.82f), new Vector2(0.95f, 0.90f), 20);
            ListText(root.transform, "ToolListLabel", "", new Vector2(0.05f, 0.48f), new Vector2(0.95f, 0.82f), 17);
            Text(root.transform, "SelectedLabel", "未选中工具", new Vector2(0.05f, 0.40f), new Vector2(0.95f, 0.48f), 17);
            Text(root.transform, "CostLabel", "", new Vector2(0.05f, 0.28f), new Vector2(0.95f, 0.40f), 16);
            Button(root.transform, "PrevButton", "上一项", new Vector2(0.05f, 0.18f), new Vector2(0.32f, 0.28f));
            Button(root.transform, "NextButton", "下一项", new Vector2(0.34f, 0.18f), new Vector2(0.61f, 0.28f));
            Text(root.transform, "StatusLabel", "", new Vector2(0.05f, 0.10f), new Vector2(0.95f, 0.18f), 18);
            Button(root.transform, "UpgradeButton", "升级", new Vector2(0.15f, 0.02f), new Vector2(0.48f, 0.10f));
            Button(root.transform, "CloseButton", "关闭", new Vector2(0.52f, 0.02f), new Vector2(0.85f, 0.10f));
            return root;
        }

        static GameObject Panel(Transform parent, string name, Vector2 amin, Vector2 amax, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            if (parent != null)
                go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = color;
            return go;
        }

        static void Text(Transform parent, string name, string value, Vector2 amin, Vector2 amax, int size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var text = go.GetComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = value ?? "";
        }

        /// <summary>HUD 解锁区：左上换行，限制在锚点矩形内，避免居中多行 preferredHeight 穿透数值行/按钮。</summary>
        static void UnlocksText(Transform parent, string name, string value, Vector2 amin, Vector2 amax, int size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var text = go.GetComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = value ?? "";
        }

        static void ListText(Transform parent, string name, string value, Vector2 amin, Vector2 amax, int size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var text = go.GetComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.text = value ?? "";
        }

        static void Button(Transform parent, string name, string label, Vector2 amin, Vector2 amax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = new Color(0.25f, 0.55f, 0.35f, 1f);
            Text(go.transform, "Text", label, Vector2.zero, Vector2.one, 24);
        }

        static void Input(Transform parent, string name, string placeholder, Vector2 amin, Vector2 amax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.22f, 0.24f, 0.28f, 1f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.GetComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = 22;
            text.color = Color.white;
            text.supportRichText = false;
            var trt = (RectTransform)textGo.transform;
            trt.anchorMin = new Vector2(0.04f, 0.1f);
            trt.anchorMax = new Vector2(0.96f, 0.9f);
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            phGo.transform.SetParent(go.transform, false);
            var ph = phGo.GetComponent<Text>();
            ph.font = text.font;
            ph.fontSize = 22;
            ph.color = new Color(1f, 1f, 1f, 0.35f);
            ph.text = placeholder;
            var prt = (RectTransform)phGo.transform;
            prt.anchorMin = trt.anchorMin;
            prt.anchorMax = trt.anchorMax;
            prt.offsetMin = Vector2.zero;
            prt.offsetMax = Vector2.zero;

            var input = go.GetComponent<InputField>();
            input.textComponent = text;
            input.placeholder = ph;
        }

        static Font BuiltinFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font != null ? font : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }
}
