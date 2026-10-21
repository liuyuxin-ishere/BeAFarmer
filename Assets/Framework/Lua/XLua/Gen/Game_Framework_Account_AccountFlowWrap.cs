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
    public class GameFrameworkAccountAccountFlowWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(Game.Framework.Account.AccountFlow);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 6, 3, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "Bind", _m_Bind_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Shutdown", _m_Shutdown_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EnterHome", _m_EnterHome_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EnterLogin", _m_EnterLogin_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ApplyWindowsForCurrentAddress", _m_ApplyWindowsForCurrentAddress_xlua_st_);
            
			
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "LastError", _g_get_LastError);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "IsSwitching", _g_get_IsSwitching);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "DesiredAddress", _g_get_DesiredAddress);
            
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "Game.Framework.Account.AccountFlow does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Bind_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Scenes.SceneService _scenes = (Game.Framework.Scenes.SceneService)translator.GetObject(L, 1, typeof(Game.Framework.Scenes.SceneService));
                    
                    Game.Framework.Account.AccountFlow.Bind( _scenes );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Shutdown_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountFlow.Shutdown(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EnterHome_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountFlow.EnterHome(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EnterLogin_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    Game.Framework.Account.AccountFlow.EnterLogin(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyWindowsForCurrentAddress_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _currentAddress = LuaAPI.lua_tostring(L, 1);
                    
                    Game.Framework.Account.AccountFlow.ApplyWindowsForCurrentAddress( _currentAddress );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LastError(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushstring(L, Game.Framework.Account.AccountFlow.LastError);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsSwitching(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, Game.Framework.Account.AccountFlow.IsSwitching);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_DesiredAddress(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushstring(L, Game.Framework.Account.AccountFlow.DesiredAddress);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
		
		
		
		
    }
}
