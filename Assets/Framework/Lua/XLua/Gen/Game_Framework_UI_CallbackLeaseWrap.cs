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
    public class GameFrameworkUICallbackLeaseWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(Game.Framework.UI.CallbackLease);
			Utils.BeginObjectRegister(type, L, translator, 0, 5, 1, 0);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "BindOperationCts", _m_BindOperationCts);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "BindCancelSideEffect", _m_BindCancelSideEffect);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "Complete", _m_Complete);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "Cancel", _m_Cancel);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "Dispose", _m_Dispose);
			
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "IsCompleted", _g_get_IsCompleted);
            
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 3, 1, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "RetainLuaCallback", _m_RetainLuaCallback_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ReleaseLuaCallback", _m_ReleaseLuaCallback_xlua_st_);
            
			
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "LiveCount", _g_get_LiveCount);
            
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 2 && translator.Assignable<Game.Framework.Lua.LuaCallback>(L, 2))
				{
					Game.Framework.Lua.LuaCallback _callback = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 2);
					
					var gen_ret = new Game.Framework.UI.CallbackLease(_callback);
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to Game.Framework.UI.CallbackLease constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BindOperationCts(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                Game.Framework.UI.CallbackLease gen_to_be_invoked = (Game.Framework.UI.CallbackLease)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    System.Threading.CancellationTokenSource _cts = (System.Threading.CancellationTokenSource)translator.GetObject(L, 2, typeof(System.Threading.CancellationTokenSource));
                    
                    gen_to_be_invoked.BindOperationCts( _cts );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BindCancelSideEffect(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                Game.Framework.UI.CallbackLease gen_to_be_invoked = (Game.Framework.UI.CallbackLease)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    System.Action _sideEffect = translator.GetDelegate<System.Action>(L, 2);
                    
                    gen_to_be_invoked.BindCancelSideEffect( _sideEffect );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Complete(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                Game.Framework.UI.CallbackLease gen_to_be_invoked = (Game.Framework.UI.CallbackLease)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    int _code = LuaAPI.xlua_tointeger(L, 2);
                    string _payload = LuaAPI.lua_tostring(L, 3);
                    
                    gen_to_be_invoked.Complete( _code, _payload );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Cancel(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                Game.Framework.UI.CallbackLease gen_to_be_invoked = (Game.Framework.UI.CallbackLease)translator.FastGetCSObj(L, 1);
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    string _reason = LuaAPI.lua_tostring(L, 2);
                    
                    gen_to_be_invoked.Cancel( _reason );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1) 
                {
                    
                    gen_to_be_invoked.Cancel(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to Game.Framework.UI.CallbackLease.Cancel!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Dispose(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                Game.Framework.UI.CallbackLease gen_to_be_invoked = (Game.Framework.UI.CallbackLease)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.Dispose(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RetainLuaCallback_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _callback = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                    Game.Framework.UI.CallbackLease.RetainLuaCallback( _callback );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ReleaseLuaCallback_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& translator.Assignable<Game.Framework.Lua.LuaCallback>(L, 1)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 2)) 
                {
                    Game.Framework.Lua.LuaCallback _callback = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    bool _swallowDisposeErrors = LuaAPI.lua_toboolean(L, 2);
                    
                    Game.Framework.UI.CallbackLease.ReleaseLuaCallback( _callback, _swallowDisposeErrors );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& translator.Assignable<Game.Framework.Lua.LuaCallback>(L, 1)) 
                {
                    Game.Framework.Lua.LuaCallback _callback = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                    Game.Framework.UI.CallbackLease.ReleaseLuaCallback( _callback );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to Game.Framework.UI.CallbackLease.ReleaseLuaCallback!");
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LiveCount(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, Game.Framework.UI.CallbackLease.LiveCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsCompleted(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                Game.Framework.UI.CallbackLease gen_to_be_invoked = (Game.Framework.UI.CallbackLease)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.IsCompleted);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
		
		
		
		
    }
}
