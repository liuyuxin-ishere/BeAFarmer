using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Lua
{
    /// <summary>
    /// 固定 C# UI 宿主与资源查询。签名属于 bridgeContract m2-bridge-0.4（含 GameplayBridge）。
    /// 旧 m1-bridge-0.3 / 0.2.0 安装不能靠资源补丁补出本桥；补丁不新增 C# 类型。
    /// Player AppVersion 使用 Application.version，不得与 bridgeContract 混称。
    /// </summary>
    public static class BridgeApi
    {
        /// <summary>C# 桥接契约名。补丁只换 Lua/配置，不改此常量。M2：m2-bridge-0.4。</summary>
        public const string BridgeContractValue = "m2-bridge-0.4";

        /// <summary>历史别名：等同 BridgeContractValue。新代码请用 BridgeContractValue。</summary>
        public const string AppVersionValue = BridgeContractValue;

        /// <summary>候选 catalog 要求的协议版本。与 Application.version 独立。</summary>
        public const int RequiredProtoVersion = 1;

        static readonly HashSet<string> Modules = new HashSet<string>();
        static RuntimeConfig _config;
        static Game.Framework.Config.ConfigService _configService;
        static string _resourceVersion = "";
        static Transform _panelRoot;
        static Font _font;
        static readonly List<string> Panels = new List<string>();
        static readonly List<GameObject> PanelObjects = new List<GameObject>();

        /// <summary>绑定正式 ConfigService（Launcher Load 后）。补丁不得改此签名。</summary>
        public static void BindConfigService(Game.Framework.Config.ConfigService service) =>
            _configService = service;

        /// <summary>
        /// 读 Global 字符串键。未加载或未知键返回空串（不抛），供 Lua 面板使用。
        /// </summary>
        public static string GetGlobal(string key)
        {
            if (_configService == null || string.IsNullOrEmpty(key))
                return "";
            try
            {
                return _configService.GetGlobal(key) ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>已完成的异步回调次数。只由 NoteAsync 写入。</summary>
        public static int AsyncCount { get; private set; }

        /// <summary>入口 Start 正常返回后为 true。中途抛错保持 false。</summary>
        public static bool EntryExecuted { get; private set; }

        /// <summary>当前已跟踪面板数。</summary>
        public static int PanelCount => Panels.Count;

        /// <summary>重置本次进程内的桥接状态。resourceVersion 来自候选资源目录。</summary>
        public static void Reset(string resourceVersion)
        {
            Modules.Clear();
            _config = null;
            _resourceVersion = resourceVersion ?? "";
            Panels.Clear();
            PanelObjects.Clear();
            AsyncCount = 0;
            EntryExecuted = false;
        }

        /// <summary>登记预检通过的 Lua 模块名。moduleName 与 require 名一致。</summary>
        public static void AddModule(string moduleName)
        {
            if (!string.IsNullOrEmpty(moduleName))
                Modules.Add(moduleName);
        }

        /// <summary>绑定通用面板根节点。Lua 不直接操作 UGUI。</summary>
        public static void BindPanelRoot(Transform root, Font font)
        {
            _panelRoot = root;
            _font = font;
        }

        /// <summary>应用配置文本。json 为 cfg_runtime 的原文。</summary>
        public static void ApplyConfig(string json)
        {
            _config = JsonUtility.FromJson<RuntimeConfig>(json);
        }

        /// <summary>返回 Player AppVersion（Application.version），不是 bridgeContract。</summary>
        public static string AppVersion() => Application.version;

        /// <summary>返回固定桥接契约名。</summary>
        public static string BridgeContract() => BridgeContractValue;

        /// <summary>返回本次候选资源版本。</summary>
        public static string ResourceVersion() => _resourceVersion;

        /// <summary>模块名是否在本次候选目录中。</summary>
        public static bool HasModule(string moduleName) => Modules.Contains(moduleName);

        /// <summary>配置键是否存在。不存在则不能当空串冒充。</summary>
        public static bool HasConfigKey(string key)
        {
            if (_config == null || _config.keys == null || string.IsNullOrEmpty(key))
                return false;

            foreach (var item in _config.keys)
            {
                if (item == key)
                    return true;
            }

            return false;
        }

        /// <summary>读取已声明的配置值。key 不存在时返回空串。</summary>
        public static string ConfigValue(string key)
        {
            if (!HasConfigKey(key) || _config == null)
                return "";

            if (key == "banner")
                return _config.banner ?? "";
            if (key == "m1_patch_message")
                return _config.m1_patch_message ?? "";
            return "";
        }

        /// <summary>
        /// 用通用宿主创建一个面板。对象记入 PanelObjects，失败回滚时按清单销毁。
        /// </summary>
        public static void CreatePanel(string panelId, string title, string body)
        {
            Panels.Add((panelId ?? "") + "|" + (title ?? "") + "|" + (body ?? ""));
            if (_panelRoot == null)
            {
                PanelObjects.Add(null);
                return;
            }

            // 根上补垂直布局，避免多面板叠在同一锚点导致截图文字不可读。
            if (_panelRoot.GetComponent<VerticalLayoutGroup>() == null)
            {
                var group = _panelRoot.gameObject.AddComponent<VerticalLayoutGroup>();
                group.childAlignment = TextAnchor.UpperLeft;
                group.spacing = 12f;
                group.padding = new RectOffset(24, 24, 24, 24);
                group.childControlHeight = true;
                group.childControlWidth = true;
                group.childForceExpandHeight = false;
                group.childForceExpandWidth = true;
            }

            var go = new GameObject(string.IsNullOrEmpty(panelId) ? "panel" : panelId);
            go.transform.SetParent(_panelRoot, false);
            var text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 28;
            text.color = Color.white;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = (title ?? "") + "\n" + (body ?? "");
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 72;
            layout.preferredHeight = 96;
            PanelObjects.Add(go);
        }

        /// <summary>
        /// 丢掉 keepCount 之后跟踪到的面板。只撤销宿主自己创建并记录的对象，不是任意 Lua 沙箱。
        /// </summary>
        public static void RollbackPanels(int keepCount)
        {
            if (keepCount < 0)
                keepCount = 0;

            while (Panels.Count > keepCount)
            {
                var last = Panels.Count - 1;
                Panels.RemoveAt(last);
                var go = PanelObjects[last];
                PanelObjects.RemoveAt(last);
                if (go != null)
                    UnityEngine.Object.Destroy(go);
            }

            EntryExecuted = false;
        }

        /// <summary>驱动一次租约。lease 为 Lua 持有的同一个异步对象。</summary>
        public static void Pump(AsyncLease lease)
        {
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));
            lease.Pump();
        }

        /// <summary>记录 Lua 回调。calls 是 Lua 侧累计次数。</summary>
        public static void NoteAsync(int code, string payload, int calls)
        {
            AsyncCount = calls;
            Debug.Log("async callback code=" + code + " payload=" + payload + " calls=" + calls);
        }

        /// <summary>仅在 Start 正常返回后调用。</summary>
        public static void MarkEntryExecuted() => EntryExecuted = true;

        /// <summary>拼接已跟踪面板。</summary>
        public static string PanelDump() => string.Join(" || ", Panels.ToArray());

        /// <summary>拼接配置键。</summary>
        public static string ConfigKeyDump()
        {
            if (_config == null || _config.keys == null)
                return "";
            return string.Join(",", _config.keys);
        }
    }

    /// <summary>cfg_runtime 字段。100 只有 banner；101 增加 m1_patch_message。</summary>
    [Serializable]
    public class RuntimeConfig
    {
        public string banner;
        public string m1_patch_message;
        public string[] keys;
    }

    /// <summary>目录里的一个 Lua 模块。</summary>
    [Serializable]
    public class CatalogModule
    {
        public string name;
        public string address;
        public string sha256;
    }

    /// <summary>
    /// 运行时目录。entryModule 决定 require 名。
    /// bridgeContract / requiredAppVersion / protoVersion 必须与宿主显式校验，不能用同源编译常量互相比。
    /// </summary>
    [Serializable]
    public class RuntimeCatalog
    {
        public string resVersion;
        public string entryModule;
        public string configAddress;
        public string configSha256;
        public string bridgeContract;
        public string requiredAppVersion;
        public int protoVersion;
        public CatalogModule[] modules;
    }

    /// <summary>主工程哈希工具。</summary>
    public static class HashUtil
    {
        /// <summary>计算字节的 SHA256 十六进制小写。</summary>
        public static string Sha256(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(data ?? Array.Empty<byte>());
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var item in hash)
                    builder.Append(item.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
