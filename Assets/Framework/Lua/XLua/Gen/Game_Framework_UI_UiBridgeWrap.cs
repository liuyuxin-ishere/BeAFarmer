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
    public class GameFrameworkUIUiBridgeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(Game.Framework.UI.UiBridge);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 16, 6, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "EnsureManager", _m_EnsureManager_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ClearManager", _m_ClearManager_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "BindSceneService", _m_BindSceneService_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Register", _m_Register_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Open", _m_Open_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Close", _m_Close_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "CloseTop", _m_CloseTop_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "CloseAll", _m_CloseAll_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ShowToast", _m_ShowToast_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Confirm", _m_Confirm_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ApplySafeArea", _m_ApplySafeArea_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SafeAnchorMinX", _m_SafeAnchorMinX_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SafeAnchorMinY", _m_SafeAnchorMinY_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SafeAnchorMaxX", _m_SafeAnchorMaxX_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SafeAnchorMaxY", _m_SafeAnchorMaxY_xlua_st_);
            
			
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Manager", _g_get_Manager);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Scenes", _g_get_Scenes);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "OpenCount", _g_get_OpenCount);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "StackCount", _g_get_StackCount);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "LiveClicks", _g_get_LiveClicks);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "LiveCallbackLeases", _g_get_LiveCallbackLeases);
            
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "Game.Framework.UI.UiBridge does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EnsureManager_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Resource.ResMgr _resources = (Game.Framework.Resource.ResMgr)translator.GetObject(L, 1, typeof(Game.Framework.Resource.ResMgr));
                    XLua.LuaEnv _luaEnv = (XLua.LuaEnv)translator.GetObject(L, 2, typeof(XLua.LuaEnv));
                    
                        var gen_ret = Game.Framework.UI.UiBridge.EnsureManager( _resources, _luaEnv );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ClearManager_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.UI.UiManager _manager = (Game.Framework.UI.UiManager)translator.GetObject(L, 1, typeof(Game.Framework.UI.UiManager));
                    
                    Game.Framework.UI.UiBridge.ClearManager( _manager );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BindSceneService_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Scenes.SceneService _scenes = (Game.Framework.Scenes.SceneService)translator.GetObject(L, 1, typeof(Game.Framework.Scenes.SceneService));
                    
                    Game.Framework.UI.UiBridge.BindSceneService( _scenes );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Register_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _windowId = LuaAPI.lua_tostring(L, 1);
                    string _prefabAddress = LuaAPI.lua_tostring(L, 2);
                    int _layer = LuaAPI.xlua_tointeger(L, 3);
                    string _controllerModule = LuaAPI.lua_tostring(L, 4);
                    bool _modal = LuaAPI.lua_toboolean(L, 5);
                    bool _canGoBack = LuaAPI.lua_toboolean(L, 6);
                    
                    Game.Framework.UI.UiBridge.Register( _windowId, _prefabAddress, _layer, _controllerModule, _modal, _canGoBack );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Open_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _windowId = LuaAPI.lua_tostring(L, 1);
                    string _argsJson = LuaAPI.lua_tostring(L, 2);
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 3);
                    
                        var gen_ret = Game.Framework.UI.UiBridge.Open( _windowId, _argsJson, _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Close_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _windowId = LuaAPI.lua_tostring(L, 1);
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 2);
                    
                        var gen_ret = Game.Framework.UI.UiBridge.Close( _windowId, _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CloseTop_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                        var gen_ret = Game.Framework.UI.UiBridge.CloseTop( _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CloseAll_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.UI.UiBridge.CloseAll(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ShowToast_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _message = LuaAPI.lua_tostring(L, 1);
                    float _durationSeconds = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    Game.Framework.UI.UiBridge.ShowToast( _message, _durationSeconds );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Confirm_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _title = LuaAPI.lua_tostring(L, 1);
                    string _message = LuaAPI.lua_tostring(L, 2);
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 3);
                    
                        var gen_ret = Game.Framework.UI.UiBridge.Confirm( _title, _message, _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplySafeArea_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _x = (float)LuaAPI.lua_tonumber(L, 1);
                    float _y = (float)LuaAPI.lua_tonumber(L, 2);
                    float _w = (float)LuaAPI.lua_tonumber(L, 3);
                    float _h = (float)LuaAPI.lua_tonumber(L, 4);
                    int _screenW = LuaAPI.xlua_tointeger(L, 5);
                    int _screenH = LuaAPI.xlua_tointeger(L, 6);
                    
                    Game.Framework.UI.UiBridge.ApplySafeArea( _x, _y, _w, _h, _screenW, _screenH );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SafeAnchorMinX_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.UI.UiBridge.SafeAnchorMinX(  );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SafeAnchorMinY_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.UI.UiBridge.SafeAnchorMinY(  );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SafeAnchorMaxX_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.UI.UiBridge.SafeAnchorMaxX(  );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SafeAnchorMaxY_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.UI.UiBridge.SafeAnchorMaxY(  );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Manager(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.Push(L, Game.Framework.UI.UiBridge.Manager);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Scenes(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.Push(L, Game.Framework.UI.UiBridge.Scenes);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_OpenCount(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.UI.UiBridge.OpenCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_StackCount(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.UI.UiBridge.StackCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LiveClicks(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.UI.UiBridge.LiveClicks);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LiveCallbackLeases(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.UI.UiBridge.LiveCallbackLeases);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
		
		
		
		
    }
}
