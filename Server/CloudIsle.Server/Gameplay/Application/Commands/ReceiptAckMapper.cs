using CloudIsle.Domain.Commands;
using CloudIsle.Proto;
using Google.Protobuf;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 将 Lookup/Query 域结果投影为线 Ack / QueryResp。无网络路由；供 T011/T014 复用。
/// 权威 next 仅在已知归属时设置（HasNextOpId）；拒绝伪造 next=0 为权威。
/// </summary>
public static class ReceiptAckMapper
{
    /// <summary>写路径 Lookup → CommandAck（含预受理 OpGap→NotAccepted）。</summary>
    public static CommandAck ToWriteAck(CommandKey key, ReceiptLookup lookup)
    {
        if (lookup == null) throw new ArgumentNullException(nameof(lookup));
        return lookup.Kind switch
        {
            ReceiptLookupKind.RecordedMatched => OwnedAck(
                key, CommandDisposition.Recorded, lookup.NextOpId, lookup.ScopeOpen),
            ReceiptLookupKind.PayloadConflict => OwnedAck(
                key, CommandDisposition.Recorded, lookup.NextOpId, lookup.ScopeOpen),
            ReceiptLookupKind.Expired => OwnedAck(
                key, CommandDisposition.Expired, lookup.NextOpId, lookup.ScopeOpen),
            ReceiptLookupKind.NotRecorded => OwnedAck(
                key, CommandDisposition.Unspecified, lookup.NextOpId, lookup.ScopeOpen),
            ReceiptLookupKind.OpGap => OwnedAck(
                key, CommandDisposition.NotAccepted, lookup.NextOpId, lookup.ScopeOpen),
            ReceiptLookupKind.ScopeInvalid => new CommandAck
            {
                OperationScopeId = key.ScopeId,
                OpId = key.OpId,
                Disposition = CommandDisposition.NotAccepted
                // HasNextOpId == false
            },
            _ => throw new ArgumentOutOfRangeException(nameof(lookup), lookup.Kind, "unknown lookup kind")
        };
    }

    /// <summary>Query → CommandResultQueryResp；不调用 Domain/RNG。</summary>
    public static CommandResultQueryResp ToQueryResp(CommandKey key, ReceiptQuery query)
    {
        if (query == null) throw new ArgumentNullException(nameof(query));
        return query.Kind switch
        {
            ReceiptQueryKind.Recorded => MapRecorded(key, query),
            ReceiptQueryKind.NotRecorded => MapNotRecorded(key, query),
            ReceiptQueryKind.Expired => MapExpired(key, query),
            ReceiptQueryKind.ScopeInvalid => new CommandResultQueryResp
            {
                Code = 110,
                ResultState = CommandResultState.Unspecified,
                Ack = new CommandAck { Disposition = CommandDisposition.Unspecified }
                // 无 HasNextOpId / 无权威 scope_state / 无 blob
            },
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Kind, "unknown query kind")
        };
    }

    static CommandResultQueryResp MapRecorded(CommandKey key, ReceiptQuery query)
    {
        ReceiptDetails d = query.Details
            ?? throw new InvalidOperationException("Recorded query missing details");
        return new CommandResultQueryResp
        {
            Code = 0,
            OriginalCode = d.Code,
            CommittedRevision = d.CommittedRevision,
            ConfigVersion = d.ConfigVersion,
            ResultBlob = ByteString.CopyFrom(d.ResultBlob),
            ResultState = CommandResultState.Recorded,
            Ack = OwnedAck(key, CommandDisposition.Recorded, query.NextOpId, query.ScopeOpen)
        };
    }

    static CommandResultQueryResp MapNotRecorded(CommandKey key, ReceiptQuery query)
    {
        // code=0 + NOT_RECORDED + ack.UNSPECIFIED（含未来 op）；非 NotAccepted
        return new CommandResultQueryResp
        {
            Code = 0,
            ResultState = CommandResultState.NotRecorded,
            Ack = OwnedAck(key, CommandDisposition.Unspecified, query.NextOpId, query.ScopeOpen)
        };
    }

    static CommandResultQueryResp MapExpired(CommandKey key, ReceiptQuery query)
    {
        return new CommandResultQueryResp
        {
            Code = 111,
            ResultState = CommandResultState.Expired,
            Ack = OwnedAck(key, CommandDisposition.Expired, query.NextOpId, query.ScopeOpen)
        };
    }

    /// <summary>正数 next 才设置 presence；拒绝权威 next=0。</summary>
    static CommandAck OwnedAck(
        CommandKey key, CommandDisposition disposition, long nextOpId, bool scopeOpen)
    {
        if (nextOpId < 1)
            throw new ArgumentOutOfRangeException(
                nameof(nextOpId),
                "authoritative next_op_id must be positive when scope is owned");
        if (disposition is < CommandDisposition.Unspecified or > CommandDisposition.Expired)
            throw new ArgumentOutOfRangeException(nameof(disposition));

        return new CommandAck
        {
            OperationScopeId = key.ScopeId,
            OpId = key.OpId,
            Disposition = disposition,
            NextOpId = nextOpId,
            ScopeState = scopeOpen ? OperationScopeState.Open : OperationScopeState.Closed
        };
    }
}
