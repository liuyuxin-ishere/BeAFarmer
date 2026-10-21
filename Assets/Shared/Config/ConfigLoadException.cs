using System;

namespace CloudIsle.Config
{
    /// <summary>配置清单、文件或 Global 键值没有通过校验。</summary>
    public sealed class ConfigLoadException : Exception
    {
        /// <summary>用说明原因的中文消息创建加载失败。</summary>
        /// <param name="message">失败原因。调用方可以把它展示给启动日志。</param>
        public ConfigLoadException(string message)
            : base(message)
        {
        }
    }
}
