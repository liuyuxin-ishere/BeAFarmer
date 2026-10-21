using XLua;

namespace Game.Framework.UI
{
    /// <summary>
    /// Lua 窗口控制器钩子。由 UiManager 单点调用；不是 MonoBehaviour。
    /// </summary>
    [CSharpCallLua]
    public interface ILuaUiController
    {
        /// <param name="view">已绑定的 ViewHost；控制器不得长期持有 Dispose 后的实例。</param>
        void OnCreate(ViewHost view);

        /// <param name="argsJson">打开参数 JSON 字符串；无参时为空串。</param>
        void OnOpen(string argsJson);

        void OnClose();

        void OnDestroy();
    }
}
