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
    
    public class GameFrameworkUIUILayerWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(Game.Framework.UI.UILayer), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(Game.Framework.UI.UILayer), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(Game.Framework.UI.UILayer), L, null, 7, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Scene", Game.Framework.UI.UILayer.Scene);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Hud", Game.Framework.UI.UILayer.Hud);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Window", Game.Framework.UI.UILayer.Window);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Popup", Game.Framework.UI.UILayer.Popup);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Guide", Game.Framework.UI.UILayer.Guide);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Toast", Game.Framework.UI.UILayer.Toast);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(Game.Framework.UI.UILayer), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushGameFrameworkUIUILayer(L, (Game.Framework.UI.UILayer)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Scene"))
                {
                    translator.PushGameFrameworkUIUILayer(L, Game.Framework.UI.UILayer.Scene);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Hud"))
                {
                    translator.PushGameFrameworkUIUILayer(L, Game.Framework.UI.UILayer.Hud);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Window"))
                {
                    translator.PushGameFrameworkUIUILayer(L, Game.Framework.UI.UILayer.Window);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Popup"))
                {
                    translator.PushGameFrameworkUIUILayer(L, Game.Framework.UI.UILayer.Popup);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Guide"))
                {
                    translator.PushGameFrameworkUIUILayer(L, Game.Framework.UI.UILayer.Guide);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Toast"))
                {
                    translator.PushGameFrameworkUIUILayer(L, Game.Framework.UI.UILayer.Toast);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for Game.Framework.UI.UILayer!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for Game.Framework.UI.UILayer! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
}