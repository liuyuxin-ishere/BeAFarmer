using System;
using System.Collections.Generic;
using Game.Framework.Account;
using Game.Framework.Gameplay;
using Game.Framework.Home;
using Game.Framework.UI;
using XLua;

namespace Game.Framework.Lua
{
    /// <summary>
    /// 主工程 xLua 生成清单。m2-bridge-0.4：LuaGameplayFacade + 必要 lease/int64 面。
    /// Binding/SessionContext/Dispatcher/Codec/Port/Proto/ClientGameplayClock 消息与控制类型不导出。
    /// T017：LuaGameplayFacade 新增 GetStatusJson/IsWritable/EstimateNowMs/RemainingMs/IsClockSynchronized。
    /// T035：LuaHomeFarmFacade 家园输入门控/手势/显示；Root 须 GenAll 重生 wrap，禁止手改 Gen。
    /// T041：HomeFarmNodeSelectHandler / ApplyNodeDisplays / TryResolveNodeAtScreen / AttachNodeDisplayTick / AttachRecoveryPoll；公开桥面增量 → 新基包，非纯 Lua 补丁。
    /// RemainingMs 为规范十进制 string（非 long）。
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
            typeof(LuaGameplayFacade),
            typeof(CommandObservation),
            typeof(GameplaySubscriptionLease),
            typeof(LuaHomeFarmFacade),
        };

        /// <summary>
        /// 显式黑名单：即使误加入 CallCSharp 也不得导出会话控制面。
        /// </summary>
        [BlackList]
        public static List<List<string>> LuaBlackList = new List<List<string>>
        {
            new List<string> { "Game.Framework.Gameplay.GameplayBridgeBinding", "ConfigureHost" },
            new List<string> { "Game.Framework.Gameplay.GameplayBridgeBinding", "BindPort" },
            new List<string> { "Game.Framework.Gameplay.GameplayBridgeBinding", "UnbindPort" },
            new List<string> { "Game.Framework.Gameplay.GameplayBridgeBinding", "Shutdown" },
            new List<string> { "Game.Framework.Gameplay.GameplaySessionContext", "MarkReady" },
            new List<string> { "Game.Framework.Gameplay.GameplaySessionContext", "BeginAuthentication" },
            new List<string> { "Game.Framework.Gameplay.GameplaySessionContext", "Invalidate" },
            new List<string> { "Game.Framework.Gameplay.ClientGameplayClock", "Synchronize" },
        };

        /// <summary>C# 回调 Lua 的入口与异步委托。</summary>
        [CSharpCallLua]
        public static List<Type> CSharpCallLuaTypes = new List<Type>
        {
            typeof(ILuaEntry),
            typeof(LuaCallback),
            typeof(ILuaUiController),
            typeof(LuaClickHandler),
            typeof(LuaHomeFarmFacade.HomeFarmGestureHandler),
            typeof(LuaHomeFarmFacade.HomeFarmCancelHandler),
            typeof(LuaHomeFarmFacade.HomeFarmDisplayTickHandler),
            typeof(LuaHomeFarmFacade.HomeFarmNodeSelectHandler),
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
