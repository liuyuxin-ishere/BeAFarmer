using System;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 旧草稿公开桥已移除。请使用 <see cref="LuaGameplayFacade"/>；
    /// 绑定请用 internal <see cref="GameplayBridgeBinding"/>（友元测试）。
    /// 保留类型名以免残留生成 Wrap 引用瞬间编译失败；不含 Bind/Send。
    /// </summary>
    [Obsolete("Use LuaGameplayFacade; binding is GameplayBridgeBinding (internal).", false)]
    public static class GameplayBridge
    {
        public const int CodeNotReady = GameplayBridgeBinding.CodeNotReady;
        public const int CodeUnknownCommand = GameplayBridgeBinding.CodeUnknownCommand;
        public const int CodeInvalidDto = GameplayBridgeBinding.CodeInvalidDto;
    }
}
