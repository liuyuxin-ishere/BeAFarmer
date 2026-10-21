namespace CSObjectWrapEditor
{
    /// <summary>
    /// 配置 xLua 生成目录。必须与 Game.Framework 作用域一致，供 GeneratorConfig 静态构造读取。
    /// </summary>
    public static class LuaGenPathConfig
    {
        /// <summary>
        /// 生成输出路径（相对工程根）。Generator 会补末尾斜杠。
        /// </summary>
        [GenPath]
        public static string GenPath = "Assets/Framework/Lua/XLua/Gen";
    }
}
