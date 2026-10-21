#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

using XLua;
using System.Collections.Generic;


namespace XLua.CSObjectWrap
{
    using Utils = XLua.Utils;
    public class GameFrameworkAccountAccountBridgeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(Game.Framework.Account.AccountBridge);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 29, 3, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "Configure", _m_Configure_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Connect", _m_Connect_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Register", _m_Register_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Login", _m_Login_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "CommitLoginToken", _m_CommitLoginToken_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "DiscardPendingToken", _m_DiscardPendingToken_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "DiscardPendingTokenIfAttempt", _m_DiscardPendingTokenIfAttempt_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Restore", _m_Restore_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Logout", _m_Logout_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "CancelAll", _m_CancelAll_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Pump", _m_Pump_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "HasToken", _m_HasToken_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ClearToken", _m_ClearToken_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "LastSnapshotJson", _m_LastSnapshotJson_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "LastBizCode", _m_LastBizCode_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "LastErrorKind", _m_LastErrorKind_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "IsAuthenticated", _m_IsAuthenticated_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "PendingRequestCount", _m_PendingRequestCount_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ShowDisconnectMask", _m_ShowDisconnectMask_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "HideDisconnectMask", _m_HideDisconnectMask_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "StopReconnect", _m_StopReconnect_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "IsReconnectRunning", _m_IsReconnectRunning_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "DropLiveSocketForAcceptance", _m_DropLiveSocketForAcceptance_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "IsDisconnectMaskVisible", _m_IsDisconnectMaskVisible_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetKickHandler", _m_SetKickHandler_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetRestoredHandler", _m_SetRestoredHandler_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetExpiredHandler", _m_SetExpiredHandler_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Dispose", _m_Dispose_xlua_st_);
            
			
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Session", _g_get_Session);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "ActiveLeaseCount", _g_get_ActiveLeaseCount);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "ReconnectAttempt", _g_get_ReconnectAttempt);
            
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "Game.Framework.Account.AccountBridge does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Configure_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _tcpHost = LuaAPI.lua_tostring(L, 1);
                    int _tcpPort = LuaAPI.xlua_tointeger(L, 2);
                    string _appVersion = LuaAPI.lua_tostring(L, 3);
                    int _resVersion = LuaAPI.xlua_tointeger(L, 4);
                    int _configVersion = LuaAPI.xlua_tointeger(L, 5);
                    string _deviceId = LuaAPI.lua_tostring(L, 6);
                    string _tokenDirectory = LuaAPI.lua_tostring(L, 7);
                    
                    Game.Framework.Account.AccountBridge.Configure( _tcpHost, _tcpPort, _appVersion, _resVersion, _configVersion, _deviceId, _tokenDirectory );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Connect_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.Connect( _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Register_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _username = LuaAPI.lua_tostring(L, 1);
                    string _password = LuaAPI.lua_tostring(L, 2);
                    string _nickname = LuaAPI.lua_tostring(L, 3);
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 4);
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.Register( _username, _password, _nickname, _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Login_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _username = LuaAPI.lua_tostring(L, 1);
                    string _password = LuaAPI.lua_tostring(L, 2);
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 3);
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.Login( _username, _password, _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CommitLoginToken_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.CommitLoginToken(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_DiscardPendingToken_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountBridge.DiscardPendingToken(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_DiscardPendingTokenIfAttempt_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _attempt = LuaAPI.xlua_tointeger(L, 1);
                    
                    Game.Framework.Account.AccountBridge.DiscardPendingTokenIfAttempt( _attempt );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Restore_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.Restore( _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Logout_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.Logout( _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CancelAll_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountBridge.CancelAll(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Pump_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _maxMessages = LuaAPI.xlua_tointeger(L, 1);
                    
                    Game.Framework.Account.AccountBridge.Pump( _maxMessages );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasToken_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.HasToken(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ClearToken_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountBridge.ClearToken(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_LastSnapshotJson_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.LastSnapshotJson(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_LastBizCode_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.LastBizCode(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_LastErrorKind_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.LastErrorKind(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsAuthenticated_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.IsAuthenticated(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_PendingRequestCount_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.PendingRequestCount(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ShowDisconnectMask_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _message = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Account.AccountBridge.ShowDisconnectMask( _message );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HideDisconnectMask_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountBridge.HideDisconnectMask(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_StopReconnect_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountBridge.StopReconnect(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsReconnectRunning_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.IsReconnectRunning(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_DropLiveSocketForAcceptance_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountBridge.DropLiveSocketForAcceptance(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsDisconnectMaskVisible_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Account.AccountBridge.IsDisconnectMaskVisible(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetKickHandler_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _handler = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                    Game.Framework.Account.AccountBridge.SetKickHandler( _handler );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetRestoredHandler_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _handler = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                    Game.Framework.Account.AccountBridge.SetRestoredHandler( _handler );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetExpiredHandler_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _handler = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                    Game.Framework.Account.AccountBridge.SetExpiredHandler( _handler );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Dispose_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountBridge.Dispose(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Session(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.Push(L, Game.Framework.Account.AccountBridge.Session);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_ActiveLeaseCount(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.Account.AccountBridge.ActiveLeaseCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_ReconnectAttempt(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.Account.AccountBridge.ReconnectAttempt);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
		
		
		
		
    }
}
