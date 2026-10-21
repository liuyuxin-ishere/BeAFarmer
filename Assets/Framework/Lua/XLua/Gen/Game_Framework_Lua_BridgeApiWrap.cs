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
    public class GameFrameworkLuaBridgeApiWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(Game.Framework.Lua.BridgeApi);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 23, 3, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "BindConfigService", _m_BindConfigService_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetGlobal", _m_GetGlobal_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Reset", _m_Reset_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AddModule", _m_AddModule_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "BindPanelRoot", _m_BindPanelRoot_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ApplyConfig", _m_ApplyConfig_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AppVersion", _m_AppVersion_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "BridgeContract", _m_BridgeContract_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ResourceVersion", _m_ResourceVersion_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "HasModule", _m_HasModule_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "HasConfigKey", _m_HasConfigKey_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ConfigValue", _m_ConfigValue_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "CreatePanel", _m_CreatePanel_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "RollbackPanels", _m_RollbackPanels_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Pump", _m_Pump_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NoteAsync", _m_NoteAsync_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "MarkEntryExecuted", _m_MarkEntryExecuted_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "PanelDump", _m_PanelDump_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ConfigKeyDump", _m_ConfigKeyDump_xlua_st_);
            
			
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "BridgeContractValue", Game.Framework.Lua.BridgeApi.BridgeContractValue);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "AppVersionValue", Game.Framework.Lua.BridgeApi.AppVersionValue);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "RequiredProtoVersion", Game.Framework.Lua.BridgeApi.RequiredProtoVersion);
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "AsyncCount", _g_get_AsyncCount);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "EntryExecuted", _g_get_EntryExecuted);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "PanelCount", _g_get_PanelCount);
            
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "Game.Framework.Lua.BridgeApi does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BindConfigService_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Config.ConfigService _service = (Game.Framework.Config.ConfigService)translator.GetObject(L, 1, typeof(Game.Framework.Config.ConfigService));
                    
                    Game.Framework.Lua.BridgeApi.BindConfigService( _service );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetGlobal_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _key = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.GetGlobal( _key );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Reset_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _resourceVersion = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Lua.BridgeApi.Reset( _resourceVersion );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AddModule_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _moduleName = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Lua.BridgeApi.AddModule( _moduleName );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BindPanelRoot_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    UnityEngine.Font _font = (UnityEngine.Font)translator.GetObject(L, 2, typeof(UnityEngine.Font));
                    
                    Game.Framework.Lua.BridgeApi.BindPanelRoot( _root, _font );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyConfig_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _json = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Lua.BridgeApi.ApplyConfig( _json );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AppVersion_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.AppVersion(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BridgeContract_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.BridgeContract(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ResourceVersion_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.ResourceVersion(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasModule_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _moduleName = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.HasModule( _moduleName );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasConfigKey_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _key = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.HasConfigKey( _key );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ConfigValue_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _key = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.ConfigValue( _key );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CreatePanel_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _panelId = LuaAPI.lua_tostring(L, 1);
                    string _title = LuaAPI.lua_tostring(L, 2);
                    string _body = LuaAPI.lua_tostring(L, 3);
                    
                    Game.Framework.Lua.BridgeApi.CreatePanel( _panelId, _title, _body );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RollbackPanels_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _keepCount = LuaAPI.xlua_tointeger(L, 1);
                    
                    Game.Framework.Lua.BridgeApi.RollbackPanels( _keepCount );
                    
                    
                    
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
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.AsyncLease _lease = (Game.Framework.Lua.AsyncLease)translator.GetObject(L, 1, typeof(Game.Framework.Lua.AsyncLease));
                    
                    Game.Framework.Lua.BridgeApi.Pump( _lease );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NoteAsync_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _code = LuaAPI.xlua_tointeger(L, 1);
                    string _payload = LuaAPI.lua_tostring(L, 2);
                    int _calls = LuaAPI.xlua_tointeger(L, 3);
                    
                    Game.Framework.Lua.BridgeApi.NoteAsync( _code, _payload, _calls );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_MarkEntryExecuted_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Lua.BridgeApi.MarkEntryExecuted(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_PanelDump_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.PanelDump(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ConfigKeyDump_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Lua.BridgeApi.ConfigKeyDump(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_AsyncCount(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.Lua.BridgeApi.AsyncCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_EntryExecuted(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, Game.Framework.Lua.BridgeApi.EntryExecuted);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_PanelCount(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.Lua.BridgeApi.PanelCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
		
		
		
		
    }
}
