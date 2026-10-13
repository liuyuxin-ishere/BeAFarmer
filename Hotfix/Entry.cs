using UnityEngine;
using UnityEngine.UI;

namespace Game.Hotfix
{
    /// <summary>
    /// 热更入口。修改 HotfixVersion 后仅替换补丁 DLL 即可验证 PoC #2。
    /// </summary>
    public static class Entry
    {
        /// <summary>补丁版本标记：改这一行，重新编译并替换 DLL 即可验证热更。</summary>
        public const string HotfixVersion = "v2";

        public static string Start(Text statusText)
        {
            string message = $"[Hotfix] Entry.Start OK — patch {HotfixVersion}";
            Debug.Log(message);
            if (statusText != null)
                statusText.text = message;
            return message;
        }
    }
}
