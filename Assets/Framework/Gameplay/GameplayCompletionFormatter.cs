using System;
using System.Collections.Generic;
using System.Text;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 命令完成结构化 JSON：Lua 不得仅凭 code 猜测 disposition。
    /// 农场逐项结果经 results/resultsStatus；成功码 0 显式写出。
    /// </summary>
    public static class GameplayCompletionFormatter
    {
        public static string Format(
            int code,
            string disposition,
            string message,
            long committedRevision = 0,
            bool cleared = false,
            FarmCommandDisplayResults displayResults = null)
        {
            var sb = new StringBuilder(192);
            sb.Append("{\"code\":").Append(code);
            sb.Append(",\"disposition\":\"").Append(Escape(disposition ?? "")).Append('"');
            sb.Append(",\"message\":\"").Append(Escape(message ?? "")).Append('"');
            sb.Append(",\"committedRevision\":\"").Append(GameplayInt64.Format(committedRevision)).Append('"');
            sb.Append(",\"cleared\":").Append(cleared ? "true" : "false");
            AppendDisplayResults(sb, displayResults);
            sb.Append('}');
            return sb.ToString();
        }

        public static string Format(CommandClientOutcome outcome)
        {
            if (outcome == null)
                return Format(-2, "Unknown", "null-outcome");
            return Format(
                outcome.Code,
                outcome.Disposition,
                outcome.Message,
                outcome.CommittedRevision,
                outcome.Cleared,
                outcome.DisplayResults);
        }

        static void AppendDisplayResults(StringBuilder sb, FarmCommandDisplayResults display)
        {
            if (display == null || display.Availability == FarmResultsAvailability.None)
                return;

            string status;
            if (display.Availability == FarmResultsAvailability.Present)
                status = "present";
            else if (display.Availability == FarmResultsAvailability.Unavailable)
                status = "unavailable";
            else
                status = "error";

            sb.Append(",\"resultsStatus\":\"").Append(status).Append('"');
            if (!string.IsNullOrEmpty(display.ArchiveKind))
                sb.Append(",\"archiveKind\":\"").Append(Escape(display.ArchiveKind)).Append('"');

            sb.Append(",\"results\":[");
            IReadOnlyList<FarmPlotDisplayResult> items = display.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                FarmPlotDisplayResult row = items[i];
                // plotId/code 显式整数；code==0 不得省略
                sb.Append("{\"plotId\":").Append(row.PlotId);
                sb.Append(",\"code\":").Append(row.Code);
                sb.Append('}');
            }

            sb.Append(']');
        }

        public static string FormatLocal(int code, string reason)
        {
            string disposition = "Local";
            if (code == GameplayBridgeBinding.CodeNotReady)
                disposition = "NotReady";
            else if (code == GameplayBridgeBinding.CodeUnknownCommand)
                disposition = "NotAccepted";
            else if (code == GameplayBridgeBinding.CodeInvalidDto)
                disposition = "NotAccepted";
            return Format(code, disposition, reason ?? "");
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
