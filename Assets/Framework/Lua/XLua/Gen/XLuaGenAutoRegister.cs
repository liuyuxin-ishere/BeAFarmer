#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

using System;
using System.Collections.Generic;
using System.Reflection;


namespace XLua.CSObjectWrap
{
    public class XLua_Gen_Initer_Register__
	{
        
        
        static void wrapInit0(LuaEnv luaenv, ObjectTranslator translator)
        {
        
            translator.DelayWrapLoader(typeof(Game.Framework.Lua.ProtoBridge), GameFrameworkLuaProtoBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.Lua.BridgeApi), GameFrameworkLuaBridgeApiWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.Lua.AsyncLease), GameFrameworkLuaAsyncLeaseWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.UI.ViewHost), GameFrameworkUIViewHostWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.UI.ClickSubscription), GameFrameworkUIClickSubscriptionWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.UI.CallbackLease), GameFrameworkUICallbackLeaseWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.UI.UiBridge), GameFrameworkUIUiBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.UI.UILayer), GameFrameworkUIUILayerWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.Account.AccountBridge), GameFrameworkAccountAccountBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.Account.AccountFlow), GameFrameworkAccountAccountFlowWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.Gameplay.LuaGameplayFacade), GameFrameworkGameplayLuaGameplayFacadeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.Gameplay.CommandObservation), GameFrameworkGameplayCommandObservationWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.Gameplay.GameplaySubscriptionLease), GameFrameworkGameplayGameplaySubscriptionLeaseWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(Game.Framework.Home.LuaHomeFarmFacade), GameFrameworkHomeLuaHomeFarmFacadeWrap.__Register);
        
        
        
        }
        
        public static void Init(LuaEnv luaenv, ObjectTranslator translator)
        {
            
            wrapInit0(luaenv, translator);
            
            
            translator.AddInterfaceBridgeCreator(typeof(Game.Framework.UI.ILuaUiController), GameFrameworkUIILuaUiControllerBridge.__Create);
            
            translator.AddInterfaceBridgeCreator(typeof(Game.Framework.Lua.ILuaEntry), GameFrameworkLuaILuaEntryBridge.__Create);
            
        }
	}
}

namespace XLua
{
	internal partial class InternalGlobals_Gen
    {
	    
        private delegate bool TryArrayGet(Type type, RealStatePtr L, ObjectTranslator translator, object obj, int index);
        private delegate bool TryArraySet(Type type, RealStatePtr L, ObjectTranslator translator, object obj, int array_idx, int obj_idx);
	    private static void Init(
            out Dictionary<Type, IEnumerable<MethodInfo>> extensionMethodMap,
            out TryArrayGet genTryArrayGetPtr,
            out TryArraySet genTryArraySetPtr)
		{
            XLua.LuaEnv.AddIniter(XLua.CSObjectWrap.XLua_Gen_Initer_Register__.Init);
            XLua.LuaEnv.AddIniter(XLua.ObjectTranslator_Gen.Init);
		    extensionMethodMap = new Dictionary<Type, IEnumerable<MethodInfo>>()
			{
			    
			};
			
            genTryArrayGetPtr = StaticLuaCallbacks_Wrap.__tryArrayGet;
            genTryArraySetPtr = StaticLuaCallbacks_Wrap.__tryArraySet;
		}
	}
}
