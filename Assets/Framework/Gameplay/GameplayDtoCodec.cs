using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using CloudIsle.Proto;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 结构化意图校验与 Req/Resp/Delta 编解码。int64 经 Descriptor 递归。
    /// FindField 仅精确 ProtoName/JsonName；嵌套消息用 MessageType.Parser。
    /// </summary>
    public static class GameplayDtoCodec
    {
        static readonly JsonFormatter Formatter = new JsonFormatter(
            JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

        static readonly HashSet<string> ForbiddenIntentFieldNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "opId", "op_id", "operationScopeId", "operation_scope_id", "scope", "scopeId", "scope_id",
                "expectedRevision", "expected_revision", "revision", "committedRevision", "committed_revision",
                "configVersion", "config_version", "config",
                "price", "reward", "gold", "gem", "ticket", "buyPrice", "sellPrice",
            };

        static readonly HashSet<string> MetaProtoNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "op_id", "operation_scope_id", "expected_revision", "config_version",
            };

        public static bool TryValidateIntent(string key, string json, out CommandIntent intent, out string error)
        {
            intent = null;
            error = null;
            if (!GameplayCommandAllowlist.TryGet(key, out CommandDescriptor desc))
            {
                error = "unknown-command:" + key;
                return false;
            }

            try
            {
                GameplayStrictJson.Node root = GameplayStrictJson.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                if (!(root is GameplayStrictJson.ObjectNode obj))
                {
                    error = "intent-root-must-be-object";
                    return false;
                }

                foreach (string name in obj.Fields.Keys)
                {
                    if (ForbiddenIntentFieldNames.Contains(name))
                    {
                        error = "forbidden-intent-field:" + name;
                        return false;
                    }
                }

                IMessage msg = desc.RequestParser.ParseFrom(Array.Empty<byte>());
                FillMessage(msg, obj, allowMeta: false);
                intent = new CommandIntent(key, desc.RequestParser, msg.ToByteArray());
                return true;
            }
            catch (Exception ex)
            {
                error = "intent-invalid:" + ex.Message;
                return false;
            }
        }

        /// <summary>幂等：已有 wire 则解析返回克隆，不重算。首次编码 SetWireOnce（不写 hash）。</summary>
        public static IMessage BuildWireRequest(PendingCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            CommandDescriptor desc = GameplayCommandAllowlist.Get(command.Intent.CommandKey);
            if (command.HasWireBytes)
                return desc.RequestParser.ParseFrom(command.PeekWireBytes());

            IMessage msg = desc.RequestParser.ParseFrom(command.Intent.PeekBusinessBytes());
            ApplyMeta(msg, command.OpId, command.OperationScopeId, command.ExpectedRevision, command.ConfigVersion);
            command.SetWireOnce(msg.ToByteArray());
            return desc.RequestParser.ParseFrom(command.PeekWireBytes());
        }

        public static bool TryDecodeResponse(string key, byte[] payload, out string json, out string error)
        {
            json = null;
            error = null;
            if (!GameplayCommandAllowlist.TryGet(key, out CommandDescriptor desc))
            {
                error = "unknown-command:" + key;
                return false;
            }

            try
            {
                IMessage msg = desc.ResponseParser.ParseFrom(payload ?? Array.Empty<byte>());
                json = Formatter.Format(msg);
                return true;
            }
            catch (Exception ex)
            {
                error = "resp-invalid:" + ex.Message;
                return false;
            }
        }

        public static bool TryDecodeDelta(byte[] payload, out string json, out string error)
        {
            json = null;
            error = null;
            try
            {
                var msg = PlayerDeltaBatchNtf.Parser.ParseFrom(payload ?? Array.Empty<byte>());
                json = Formatter.Format(msg);
                return true;
            }
            catch (Exception ex)
            {
                error = "delta-invalid:" + ex.Message;
                return false;
            }
        }

        public static bool TryEncodeWireJson(string key, string wireJson, out byte[] payload, out string error)
        {
            payload = null;
            error = null;
            if (!GameplayCommandAllowlist.TryGet(key, out CommandDescriptor desc))
            {
                error = "unknown-command:" + key;
                return false;
            }

            try
            {
                GameplayStrictJson.Node root = GameplayStrictJson.Parse(string.IsNullOrWhiteSpace(wireJson) ? "{}" : wireJson);
                if (!(root is GameplayStrictJson.ObjectNode obj))
                {
                    error = "wire-root-must-be-object";
                    return false;
                }

                IMessage msg = desc.RequestParser.ParseFrom(Array.Empty<byte>());
                FillMessage(msg, obj, allowMeta: true);
                payload = msg.ToByteArray();
                return true;
            }
            catch (Exception ex)
            {
                error = "wire-invalid:" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 内部 Descriptor 校验（测试用）：如 PlayerInfo.gold 嵌套 int64。
        /// 不向 Lua 暴露任意 Type 入口。
        /// </summary>
        internal static bool TryFillByDescriptor(
            MessageParser parser,
            string json,
            bool allowMeta,
            out IMessage message,
            out string error)
        {
            message = null;
            error = null;
            try
            {
                GameplayStrictJson.Node root = GameplayStrictJson.Parse(json ?? "{}");
                if (!(root is GameplayStrictJson.ObjectNode obj))
                {
                    error = "root-must-be-object";
                    return false;
                }

                IMessage msg = parser.ParseFrom(Array.Empty<byte>());
                FillMessage(msg, obj, allowMeta);
                message = msg;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static void ApplyMeta(IMessage msg, long opId, long scopeId, long revision, int configVersion)
        {
            MessageDescriptor d = msg.Descriptor;
            TrySetInt64(msg, d, "op_id", opId);
            TrySetInt64(msg, d, "operation_scope_id", scopeId);
            TrySetInt64(msg, d, "expected_revision", revision);
            FieldDescriptor cfg = d.FindFieldByName("config_version");
            if (cfg != null && cfg.FieldType == FieldType.Int32)
                cfg.Accessor.SetValue(msg, configVersion);
        }

        static void TrySetInt64(IMessage msg, MessageDescriptor d, string protoName, long value)
        {
            FieldDescriptor f = d.FindFieldByName(protoName);
            if (f == null)
                return;
            if (IsInt64Field(f))
                f.Accessor.SetValue(msg, value);
        }

        static void FillMessage(IMessage msg, GameplayStrictJson.ObjectNode obj, bool allowMeta)
        {
            MessageDescriptor descriptor = msg.Descriptor;
            var seen = new HashSet<FieldDescriptor>();
            foreach (KeyValuePair<string, GameplayStrictJson.Node> pair in obj.Fields)
            {
                string jsonName = pair.Key;
                FieldDescriptor field = FindFieldExact(descriptor, jsonName);
                if (field == null)
                    throw new FormatException("unknown-field:" + jsonName);
                if (!seen.Add(field))
                    throw new FormatException("duplicate-semantic-field:" + jsonName);
                if (!allowMeta && IsMetaField(field))
                    throw new FormatException("forbidden-meta-field:" + field.Name);

                SetField(msg, field, pair.Value, allowMeta);
            }
        }

        static bool IsMetaField(FieldDescriptor field)
        {
            return MetaProtoNames.Contains(field.Name);
        }

        /// <summary>仅精确 ProtoName 或 JsonName（Unicode 解码后的字面名）。</summary>
        static FieldDescriptor FindFieldExact(MessageDescriptor descriptor, string name)
        {
            foreach (FieldDescriptor f in descriptor.Fields.InDeclarationOrder())
            {
                if (string.Equals(f.JsonName, name, StringComparison.Ordinal))
                    return f;
                if (string.Equals(f.Name, name, StringComparison.Ordinal))
                    return f;
            }

            return null;
        }

        static void SetField(IMessage msg, FieldDescriptor field, GameplayStrictJson.Node node, bool allowMeta)
        {
            if (field.IsMap)
                throw new FormatException("map-not-supported-in-intent:" + field.Name);
            if (field.IsRepeated)
            {
                if (!(node is GameplayStrictJson.ArrayNode arr))
                    throw new FormatException("expected-array:" + field.Name);
                IList list = (IList)field.Accessor.GetValue(msg);
                list.Clear();
                for (int i = 0; i < arr.Items.Count; i++)
                    list.Add(ReadSingular(field, arr.Items[i], allowMeta));
                return;
            }

            field.Accessor.SetValue(msg, ReadSingular(field, node, allowMeta));
        }

        static object ReadSingular(FieldDescriptor field, GameplayStrictJson.Node node, bool allowMeta)
        {
            if (node is GameplayStrictJson.NullNode)
            {
                if (IsInt64Field(field))
                    throw new FormatException("int64-must-be-decimal-string:" + field.Name);
                if (field.FieldType == FieldType.Message)
                    return null;
                return GetDefault(field);
            }

            if (field.FieldType == FieldType.Message)
            {
                if (!(node is GameplayStrictJson.ObjectNode obj))
                    throw new FormatException("expected-object:" + field.Name);
                // Google.Protobuf 3.21：MessageDescriptor.Parser；避免 Activator 以利 AOT
                MessageParser nestedParser = field.MessageType.Parser
                    ?? throw new FormatException("no-parser:" + field.Name);
                IMessage sub = nestedParser.ParseFrom(Array.Empty<byte>());
                FillMessage(sub, obj, allowMeta);
                return sub;
            }

            if (IsInt64Field(field))
            {
                if (!(node is GameplayStrictJson.StringNode sn))
                    throw new FormatException("int64-must-be-decimal-string:" + field.Name);
                if (!GameplayInt64.TryParse(sn.Value, out long v, out string err))
                    throw new FormatException(err ?? "int64-parse");
                return v;
            }

            if (field.FieldType == FieldType.Int32 || field.FieldType == FieldType.SInt32
                || field.FieldType == FieldType.SFixed32 || field.FieldType == FieldType.UInt32
                || field.FieldType == FieldType.Fixed32)
            {
                return ReadInt32(node, field.Name);
            }

            if (field.FieldType == FieldType.Bool)
            {
                if (node is GameplayStrictJson.BoolNode b)
                    return b.Value;
                throw new FormatException("expected-bool:" + field.Name);
            }

            if (field.FieldType == FieldType.String)
            {
                if (node is GameplayStrictJson.StringNode s)
                    return s.Value;
                throw new FormatException("expected-string:" + field.Name);
            }

            if (field.FieldType == FieldType.Enum)
            {
                EnumValueDescriptor evd;
                if (node is GameplayStrictJson.StringNode es)
                {
                    evd = field.EnumType.FindValueByName(es.Value);
                    if (evd == null)
                        throw new FormatException("bad-enum:" + es.Value);
                }
                else if (node is GameplayStrictJson.NumberNode en)
                {
                    if (!int.TryParse(en.Literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ev))
                        throw new FormatException("bad-enum-number");
                    evd = field.EnumType.FindValueByNumber(ev);
                    if (evd == null)
                        throw new FormatException("bad-enum-number:" + ev);
                }
                else
                {
                    throw new FormatException("expected-enum:" + field.Name);
                }

                return evd.Number;
            }

            if (field.FieldType == FieldType.Float || field.FieldType == FieldType.Double)
            {
                string lit = node is GameplayStrictJson.NumberNode n ? n.Literal
                    : node is GameplayStrictJson.StringNode s2 ? s2.Value : null;
                if (lit == null)
                    throw new FormatException("expected-number:" + field.Name);
                if (field.FieldType == FieldType.Float)
                    return float.Parse(lit, CultureInfo.InvariantCulture);
                return double.Parse(lit, CultureInfo.InvariantCulture);
            }

            throw new FormatException("unsupported-field-type:" + field.FieldType);
        }

        static int ReadInt32(GameplayStrictJson.Node node, string name)
        {
            string lit;
            if (node is GameplayStrictJson.NumberNode n)
                lit = n.Literal;
            else if (node is GameplayStrictJson.StringNode s)
                lit = s.Value;
            else
                throw new FormatException("expected-int32:" + name);
            if (lit.IndexOf('.') >= 0 || lit.IndexOf('e') >= 0 || lit.IndexOf('E') >= 0)
                throw new FormatException("int32-not-integer:" + name);
            if (!long.TryParse(lit, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
                throw new FormatException("int32-parse:" + name);
            if (v < int.MinValue || v > int.MaxValue)
                throw new FormatException("int32-overflow:" + name);
            return (int)v;
        }

        static bool IsInt64Field(FieldDescriptor field)
        {
            return field.FieldType == FieldType.Int64
                   || field.FieldType == FieldType.SInt64
                   || field.FieldType == FieldType.SFixed64
                   || field.FieldType == FieldType.UInt64
                   || field.FieldType == FieldType.Fixed64;
        }

        static object GetDefault(FieldDescriptor field)
        {
            switch (field.FieldType)
            {
                case FieldType.Bool: return false;
                case FieldType.String: return "";
                case FieldType.Int32: return 0;
                case FieldType.Int64: return 0L;
                default: return null;
            }
        }
    }
}
