namespace Game.Framework.UI
{
    /// <summary>Lua/C# 注册项。重复 windowId 拒绝。</summary>
    public sealed class UiWindowRegistration
    {
        public string WindowId;
        public string PrefabAddress;
        public UILayer Layer;
        public string ControllerModule;
        public bool Modal;
        public bool CanGoBack = true;
    }
}
