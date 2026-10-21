namespace Game.Framework.UI
{
    /// <summary>
    /// 固定六层。数值即排序：越大越靠前。Lua 经桥使用同名整型。
    /// </summary>
    public enum UILayer
    {
        Scene = 0,
        Hud = 1,
        Window = 2,
        Popup = 3,
        Guide = 4,
        Toast = 5
    }
}
