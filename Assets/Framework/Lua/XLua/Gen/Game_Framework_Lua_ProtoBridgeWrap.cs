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
    public class GameFrameworkLuaProtoBridgeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(Game.Framework.Lua.ProtoBridge);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 9, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "BuildHeartbeatBytes", _m_BuildHeartbeatBytes_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "BuildAvatarBytes", _m_BuildAvatarBytes_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ReadHeartbeatClientTime", _m_ReadHeartbeatClientTime_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EchoInt64", _m_EchoInt64_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EchoBytes", _m_EchoBytes_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "VerifyAll", _m_VerifyAll_xlua_st_);
            
			
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "BigInt64", Game.Framework.Lua.ProtoBridge.BigInt64);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Contract", Game.Framework.Lua.ProtoBridge.Contract);
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "Game.Framework.Lua.ProtoBridge does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BuildHeartbeatBytes_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Lua.ProtoBridge.BuildHeartbeatBytes(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BuildAvatarBytes_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = Game.Framework.Lua.ProtoBridge.BuildAvatarBytes(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ReadHeartbeatClientTime_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    byte[] _payload = LuaAPI.lua_tobytes(L, 1);
                    
                        var gen_ret = Game.Framework.Lua.ProtoBridge.ReadHeartbeatClientTime( _payload );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EchoInt64_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _value = LuaAPI.lua_toint64(L, 1);
                    
                        var gen_ret = Game.Framework.Lua.ProtoBridge.EchoInt64( _value );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EchoBytes_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    byte[] _payload = LuaAPI.lua_tobytes(L, 1);
                    
                        var gen_ret = Game.Framework.Lua.ProtoBridge.EchoBytes( _payload );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_VerifyAll_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    byte[] _heartbeat = LuaAPI.lua_tobytes(L, 1);
                    long _echoed = LuaAPI.lua_toint64(L, 2);
                    byte[] _avatar = LuaAPI.lua_tobytes(L, 3);
                    
                        var gen_ret = Game.Framework.Lua.ProtoBridge.VerifyAll( _heartbeat, _echoed, _avatar );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        
        
		
		
		
		
    }
}
