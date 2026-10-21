using CloudIsle.Proto;
using Google.Protobuf;

namespace Game.Framework.Account
{
    /// <summary>
    /// 把正式 PlayerFullData 编成 Lua 可用的 ProtoJSON。
    /// int64 由 JsonFormatter 输出为十进制字符串，避免 double 精度丢失。
    /// 不修改入参消息；每次 Format 产出独立字符串。
    /// </summary>
    public static class SnapshotCodec
    {
        static readonly JsonFormatter Formatter = new JsonFormatter(
            JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

        /// <summary>编码全量快照。data 为空返回空对象。</summary>
        public static string ToLuaJson(PlayerFullData data)
        {
            if (data == null)
                return "{}";
            return Formatter.Format(data);
        }

        /// <summary>从响应 bytes 解析 PlayerFullData（LoginResp / ReconnectResp 的 Data）。</summary>
        public static PlayerFullData ParseFullData(byte[] body)
        {
            if (body == null || body.Length == 0)
                return null;
            return PlayerFullData.Parser.ParseFrom(body);
        }
    }
}
