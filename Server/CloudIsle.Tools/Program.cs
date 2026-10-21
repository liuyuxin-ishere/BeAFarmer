using System;

namespace CloudIsle.Tools
{
    internal static class Program
    {
        /// <summary>
        /// validate &lt;descriptor路径&gt;：只检查 ID。
        /// generate &lt;proto目录&gt; &lt;输出目录&gt; &lt;descriptor路径&gt;：检查通过后写出 MsgRegistry.cs。
        /// </summary>
        static int Main(string[] args)
        {
            try
            {
                var generator = new ProtoRegistryGenerator();
                if (args.Length == 2 && args[0] == "validate")
                {
                    generator.ValidateIds(args[1]);
                    return 0;
                }

                if (args.Length == 4 && args[0] == "generate")
                {
                    generator.ValidateIds(args[3]);
                    generator.Generate(args[1], args[2]);
                    return 0;
                }

                Console.Error.WriteLine("用法: validate <descriptor> | generate <protoDir> <outputDir> <descriptor>");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
