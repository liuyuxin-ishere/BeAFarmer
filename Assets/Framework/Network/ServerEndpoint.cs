using System;

namespace Game.Framework.Network
{
    /// <summary>
    /// 一次 TCP 连接要使用的主机和端口。不含账号、超时或重连策略。
    /// </summary>
    public sealed class ServerEndpoint
    {
        /// <summary>
        /// 记录连接目标。
        /// </summary>
        /// <param name="host">主机名或 IP。空白时抛出异常。</param>
        /// <param name="port">端口，必须在 1 到 65535。</param>
        public ServerEndpoint(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("主机名为空", nameof(host));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 1 到 65535");

            Host = host;
            Port = port;
        }

        /// <summary>主机名或 IP。</summary>
        public string Host { get; }

        /// <summary>端口。</summary>
        public int Port { get; }
    }
}
