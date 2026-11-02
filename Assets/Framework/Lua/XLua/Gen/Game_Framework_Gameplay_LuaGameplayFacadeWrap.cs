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
    public class GameFrameworkGameplayLuaGameplayFacadeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(Game.Framework.Gameplay.LuaGameplayFacade);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 11, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "SendCommand", _m_SendCommand_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetStatusJson", _m_GetStatusJson_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "IsWritable", _m_IsWritable_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EstimateNowMs", _m_EstimateNowMs_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "RemainingMs", _m_RemainingMs_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "IsClockSynchronized", _m_IsClockSynchronized_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SyncState", _m_SyncState_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "CompareInt64Decimal", _m_CompareInt64Decimal_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "BigInt64Decimal", _m_BigInt64Decimal_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "MaxInt64Decimal", _m_MaxInt64Decimal_xlua_st_);
            
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "Game.Framework.Gameplay.LuaGameplayFacade does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SendCommand_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _key = LuaAPI.lua_tostring(L, 1);
                    string _intentJson = LuaAPI.lua_tostring(L, 2);
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 3);
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.SendCommand( _key, _intentJson, _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetStatusJson_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.GetStatusJson(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsWritable_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _reason;
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.IsWritable( out _reason );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    LuaAPI.lua_pushstring(L, _reason);
                        
                    
                    
                    
                    return 2;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EstimateNowMs_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.EstimateNowMs(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RemainingMs_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _deadlineMs = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.RemainingMs( _deadlineMs );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsClockSynchronized_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.IsClockSynchronized(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SyncState_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    Game.Framework.Lua.LuaCallback _done = translator.GetDelegate<Game.Framework.Lua.LuaCallback>(L, 1);
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.SyncState( _done );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CompareInt64Decimal_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _left = LuaAPI.lua_tostring(L, 1);
                    string _right = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.CompareInt64Decimal( _left, _right );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BigInt64Decimal_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.BigInt64Decimal(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_MaxInt64Decimal_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Gameplay.LuaGameplayFacade.MaxInt64Decimal(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        
        
		
		
		
		
    }
}
