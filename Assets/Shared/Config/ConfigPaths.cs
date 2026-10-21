using System.IO;

namespace CloudIsle.Config
{
    /// <summary>
    /// 配置目录解析：仓库 <c>Server/Config</c> 与客户端 <c>StreamingAssets/Config</c> 内容一致。
    /// </summary>
    public static class ConfigPaths
    {
        public const string RelativeStreaming = "Config";

        /// <summary>
        /// 从任意启动目录向上查找含 manifest.json 的 Config 目录。
        /// </summary>
        public static string ResolveFromBaseDir(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                string beside = Path.Combine(dir.FullName, "Config");
                if (IsConfigDir(beside))
                    return beside;

                string underServer = Path.Combine(dir.FullName, "Server", "Config");
                if (IsConfigDir(underServer))
                    return underServer;

                string streaming = Path.Combine(dir.FullName, "Assets", "StreamingAssets", "Config");
                if (IsConfigDir(streaming))
                    return streaming;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Config directory with manifest.json not found from: " + startDir);
        }

        public static bool IsConfigDir(string path)
        {
            return Directory.Exists(path) && File.Exists(Path.Combine(path, "manifest.json"));
        }
    }
}
