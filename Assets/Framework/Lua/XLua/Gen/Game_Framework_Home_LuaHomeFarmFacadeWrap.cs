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
    public class GameFrameworkHomeLuaHomeFarmFacadeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(Game.Framework.Home.LuaHomeFarmFacade);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 20, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "AttachHandlers", _m_AttachHandlers_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "DetachHandlers", _m_DetachHandlers_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AttachNodeSelectHandler", _m_AttachNodeSelectHandler_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "DetachNodeSelectHandler", _m_DetachNodeSelectHandler_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AttachNodeDisplayTick", _m_AttachNodeDisplayTick_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "DetachNodeDisplayTick", _m_DetachNodeDisplayTick_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AttachRecoveryPoll", _m_AttachRecoveryPoll_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "DetachRecoveryPoll", _m_DetachRecoveryPoll_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetInputEnabled", _m_SetInputEnabled_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetToolMode", _m_SetToolMode_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "CancelActiveGesture", _m_CancelActiveGesture_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "IsRuntimeReady", _m_IsRuntimeReady_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EnsureRuntime", _m_EnsureRuntime_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "TryResolvePlotAtScreen", _m_TryResolvePlotAtScreen_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "TryResolveNodeAtScreen", _m_TryResolveNodeAtScreen_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "TryGetPlotScreenPoint", _m_TryGetPlotScreenPoint_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "TryGetNodeScreenPoint", _m_TryGetNodeScreenPoint_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ApplyPlotDisplays", _m_ApplyPlotDisplays_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ApplyNodeDisplays", _m_ApplyNodeDisplays_xlua_st_);
            
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "Game.Framework.Home.LuaHomeFarmFacade does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AttachHandlers_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Home.LuaHomeFarmFacade.HomeFarmGestureHandler _onGesture = translator.GetDelegate<Game.Framework.Home.LuaHomeFarmFacade.HomeFarmGestureHandler>(L, 1);
                    Game.Framework.Home.LuaHomeFarmFacade.HomeFarmCancelHandler _onCancel = translator.GetDelegate<Game.Framework.Home.LuaHomeFarmFacade.HomeFarmCancelHandler>(L, 2);
                    Game.Framework.Home.LuaHomeFarmFacade.HomeFarmDisplayTickHandler _onTick = translator.GetDelegate<Game.Framework.Home.LuaHomeFarmFacade.HomeFarmDisplayTickHandler>(L, 3);
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.AttachHandlers( _onGesture, _onCancel, _onTick );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_DetachHandlers_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _epoch = LuaAPI.lua_toint64(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.DetachHandlers( _epoch );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AttachNodeSelectHandler_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Home.LuaHomeFarmFacade.HomeFarmNodeSelectHandler _onNodeSelect = translator.GetDelegate<Game.Framework.Home.LuaHomeFarmFacade.HomeFarmNodeSelectHandler>(L, 1);
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.AttachNodeSelectHandler( _onNodeSelect );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_DetachNodeSelectHandler_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _epoch = LuaAPI.lua_toint64(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.DetachNodeSelectHandler( _epoch );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AttachNodeDisplayTick_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Home.LuaHomeFarmFacade.HomeFarmDisplayTickHandler _onTick = translator.GetDelegate<Game.Framework.Home.LuaHomeFarmFacade.HomeFarmDisplayTickHandler>(L, 1);
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.AttachNodeDisplayTick( _onTick );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_DetachNodeDisplayTick_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _epoch = LuaAPI.lua_toint64(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.DetachNodeDisplayTick( _epoch );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AttachRecoveryPoll_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Home.LuaHomeFarmFacade.HomeFarmDisplayTickHandler _onPoll = translator.GetDelegate<Game.Framework.Home.LuaHomeFarmFacade.HomeFarmDisplayTickHandler>(L, 1);
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.AttachRecoveryPoll( _onPoll );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_DetachRecoveryPoll_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _epoch = LuaAPI.lua_toint64(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.DetachRecoveryPoll( _epoch );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetInputEnabled_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    bool _enabled = LuaAPI.lua_toboolean(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.SetInputEnabled( _enabled );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetToolMode_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _mode = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.SetToolMode( _mode );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CancelActiveGesture_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _reason = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.CancelActiveGesture( _reason );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsRuntimeReady_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.IsRuntimeReady(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EnsureRuntime_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Home.LuaHomeFarmFacade.EnsureRuntime(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TryResolvePlotAtScreen_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _x = (float)LuaAPI.lua_tonumber(L, 1);
                    float _y = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.TryResolvePlotAtScreen( _x, _y );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TryResolveNodeAtScreen_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _x = (float)LuaAPI.lua_tonumber(L, 1);
                    float _y = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.TryResolveNodeAtScreen( _x, _y );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TryGetPlotScreenPoint_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _plotId = LuaAPI.xlua_tointeger(L, 1);
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.TryGetPlotScreenPoint( _plotId );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TryGetNodeScreenPoint_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _nodeInstanceId = LuaAPI.xlua_tointeger(L, 1);
                    
                        var gen_ret = Game.Framework.Home.LuaHomeFarmFacade.TryGetNodeScreenPoint( _nodeInstanceId );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyPlotDisplays_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _payload = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.ApplyPlotDisplays( _payload );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyNodeDisplays_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _payload = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Home.LuaHomeFarmFacade.ApplyNodeDisplays( _payload );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        
        
		
		
		
		
    }
}
