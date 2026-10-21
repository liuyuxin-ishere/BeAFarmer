using XLua;

namespace Game.Framework.UI
{
    /// <summary>按钮点击回调。须列入 CSharpCallLua 生成委托桥。</summary>
    [CSharpCallLua]
    public delegate void LuaClickHandler();
}
