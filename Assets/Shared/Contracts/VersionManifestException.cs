using System;

namespace CloudIsle.Contracts
{
    /// <summary>版本清单字节、字段或语义版本没有通过校验。</summary>
    public sealed class VersionManifestException : Exception
    {
        /// <summary>用说明原因的中文消息创建清单失败。</summary>
        /// <param name="message">失败原因。调用方可写入启动日志，不得降级为默认清单。</param>
        public VersionManifestException(string message)
            : base(message)
        {
        }
    }
}
