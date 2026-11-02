using System;

namespace Game.Framework.Gameplay
{
    /// <summary>单写待确认状态机。</summary>
    public enum PendingCommandState
    {
        Prepared = 1,
        InFlight = 2,
        Unknown = 3,
        RecordedAwaitModel = 4,
        Completed = 5,
        LockedCorrupt = 6,
        Archived = 7
    }

    /// <summary>装载结果。</summary>
    public enum PendingLoadKind
    {
        Empty = 0,
        Ok = 1,
        CorruptLocked = 2,
        MissingPayloadLocked = 3
    }

    /// <summary>命令完成观察结果（非 Lua 回调面）。</summary>
    public sealed class CommandClientOutcome
    {
        readonly FarmCommandDisplayResults _displayResults;

        public CommandClientOutcome(
            int code,
            string message,
            long committedRevision,
            bool awaitsModel,
            bool cleared,
            string disposition = null,
            FarmCommandDisplayResults displayResults = null)
        {
            Code = code;
            Message = message ?? "";
            CommittedRevision = committedRevision;
            AwaitsModel = awaitsModel;
            Cleared = cleared;
            Disposition = disposition ?? InferDisposition(code, message, awaitsModel, cleared);
            // Unknown / NotAccepted / Terminal 等不得携带已提交逐项结果
            if (!MayExposeCommittedResults(Disposition, awaitsModel, cleared, code))
                _displayResults = FarmCommandDisplayResults.None;
            else
                _displayResults = displayResults == null
                    ? FarmCommandDisplayResults.None
                    : displayResults.Clone();
        }

        public int Code { get; }
        public string Message { get; }
        public long CommittedRevision { get; }
        public bool AwaitsModel { get; }
        public bool Cleared { get; }
        /// <summary>Recorded / NotAccepted / Unknown / Expired / Local / …</summary>
        public string Disposition { get; }

        /// <summary>最终完成回调用展示信封克隆。</summary>
        public FarmCommandDisplayResults DisplayResults => _displayResults.Clone();

        static bool MayExposeCommittedResults(
            string disposition, bool awaitsModel, bool cleared, int code)
        {
            _ = code;
            if (awaitsModel)
                return false;
            if (string.Equals(disposition, "Unknown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disposition, "NotAccepted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disposition, "Terminal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disposition, "Expired", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disposition, "PayloadConflict", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disposition, "NotRecorded", StringComparison.OrdinalIgnoreCase))
                return false;
            // 仅最终 Recorded（cleared）可带槽位冻结结果；其它 disposition 不携带
            return cleared
                   && string.Equals(disposition, "Recorded", StringComparison.OrdinalIgnoreCase);
        }

        static string InferDisposition(int code, string message, bool awaitsModel, bool cleared)
        {
            if (awaitsModel)
                return "Recorded";
            if (code == 111 || (message != null && message.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0))
                return "Expired";
            if (code == 112 || (message != null && message.IndexOf("payload-conflict", StringComparison.OrdinalIgnoreCase) >= 0))
                return "PayloadConflict";
            if (code == 106 || code == 204 || code == 110)
                return "Terminal";
            if (message != null && message.StartsWith("not-accepted", StringComparison.OrdinalIgnoreCase))
                return "NotAccepted";
            if (message != null && message.IndexOf("recorded-refusal", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Recorded";
            if (cleared && code == 0)
                return "Recorded";
            if (message != null && message.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Unknown";
            return code == 0 ? "Recorded" : "Unknown";
        }
    }
}
