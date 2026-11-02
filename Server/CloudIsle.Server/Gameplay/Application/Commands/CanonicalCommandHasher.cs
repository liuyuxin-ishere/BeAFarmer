using System;
using CloudIsle.Commands;
using CloudIsle.Domain.Commands;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 规范命令摘要。格式版本固定 <c>cmdhash-v1</c>；输出 64 小写 hex SHA-256。
/// 委托 Shared <see cref="CmdHashV1"/>，保证服务端与客户端黄金向量一致。
/// </summary>
public static class CanonicalCommandHasher
{
    public const string FormatVersion = CmdHashV1.FormatVersion;

    /// <param name="command">已冻结信封；业务载荷须已由工厂规范化。</param>
    public static CommandHash Compute(CommandEnvelope command)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));

        string hex = CmdHashV1.ComputeHex(
            command.Kind,
            command.ExpectedRevision,
            command.ConfigVersion,
            command.CopyCanonicalBusinessPayload());
        return new CommandHash(hex);
    }
}
