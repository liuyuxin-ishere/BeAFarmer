using System;
using System.Collections.Generic;
using Game.Framework.Account;
using Game.Framework.UI;
using XLua;

namespace Game.Framework.Lua
{
    /// <summary>
    /// 主工程 xLua 生成清单。Gen 与运行时同属 Game.Framework，避免 Framework↔独立 XLua 循环。
    /// GenPath 在 Editor 程序集配置，避免 Player 引用 Editor 特性。
    /// m1-bridge-0.3：含 ViewHost/UiBridge/AccountBridge/ILuaUiController/Shutdown。
    /// </summary>
    public static class LuaGenConfig
    {
        /// <summary>Lua 可调用的固定 C# 桥。不暴露 Protobuf 消息类型本身。</summary>
        [LuaCallCSharp]
        public static List<Type> LuaCallCSharpTypes = new List<Type>
        {
            typeof(ProtoBridge),
            typeof(BridgeApi),
            typeof(AsyncLease),
            typeof(ViewHost),
            typeof(ClickSubscription),
            typeof(CallbackLease),
            typeof(UiBridge),
            typeof(UILayer),
            typeof(AccountBridge),
            typeof(AccountFlow),
        };

        /// <summary>C# 回调 Lua 的入口与异步委托。</summary>
        [CSharpCallLua]
        public static List<Type> CSharpCallLuaTypes = new List<Type>
        {
            typeof(ILuaEntry),
            typeof(LuaCallback),
            typeof(ILuaUiController),
            typeof(LuaClickHandler),
        };
    }

    /// <summary>
    /// C# 调用 Lua 模块入口。phase 是本次启动阶段名，返回值带回 Lua 汇总。
    /// Shutdown 在 Env 关闭前关窗/退订；勿与 LuaBase.Dispose 混淆。
    /// </summary>
    public interface ILuaEntry
    {
        /// <param name="phase">启动阶段名，例如 boot。</param>
        string Start(string phase);

        /// <summary>宿主关闭前钩子：关窗、退订、释放回调。可空实现。</summary>
        void Shutdown();
    }

    /// <summary>
    /// Lua 异步完成回调。code 为完成码，payload 为完成说明。
    /// </summary>
    public delegate void LuaCallback(int code, string payload);
}
