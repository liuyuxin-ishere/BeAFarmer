using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Google.Protobuf.Reflection;

namespace CloudIsle.Tools
{
    /// <summary>
    /// 从 protoc descriptor 检查 MsgId，并生成 MsgRegistry.cs。
    /// </summary>
    public sealed class ProtoRegistryGenerator
    {
        readonly List<MsgRow> _rows = new List<MsgRow>();
        readonly Dictionary<int, int> _responseByRequest = new Dictionary<int, int>();

        /// <summary>
        /// 读取 protoc 导出的 descriptor，检查 MsgId。
        /// 重复数值、缺少同名消息、把 Logout 配到 Kick 都抛 InvalidOperationException。
        /// </summary>
        /// <param name="descriptorPath">protoc --descriptor_set_out 写出的文件。</param>
        public void ValidateIds(string descriptorPath)
        {
            if (string.IsNullOrWhiteSpace(descriptorPath))
                throw new ArgumentException("descriptor 路径为空。", nameof(descriptorPath));
            if (!File.Exists(descriptorPath))
                throw new FileNotFoundException("找不到 descriptor。", descriptorPath);

            _rows.Clear();
            _responseByRequest.Clear();

            FileDescriptorSet set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(descriptorPath));
            var messageNames = new HashSet<string>(StringComparer.Ordinal);
            EnumDescriptorProto msgId = null;
            foreach (FileDescriptorProto file in set.File)
            {
                CollectMessages(file.MessageType, messageNames);
                foreach (EnumDescriptorProto enumType in file.EnumType)
                {
                    if (enumType.Name != "MsgId")
                        continue;
                    if (msgId != null)
                        throw new InvalidOperationException("descriptor 中存在多个 MsgId 枚举。");
                    msgId = enumType;
                }
            }

            if (msgId == null)
                throw new InvalidOperationException("descriptor 中没有 MsgId 枚举。");

            var numberToName = new Dictionary<int, string>();
            foreach (EnumValueDescriptorProto value in msgId.Value)
            {
                if (numberToName.ContainsKey(value.Number))
                    throw new InvalidOperationException("消息 ID 重复: " + value.Number + " (" + numberToName[value.Number] + ", " + value.Name + ")");
                numberToName.Add(value.Number, value.Name);
            }

            if (!numberToName.TryGetValue(0, out string zeroName) || zeroName != "MSG_NONE")
                throw new InvalidOperationException("消息 ID 0 只允许 MSG_NONE。");

            foreach (KeyValuePair<int, string> pair in numberToName.OrderBy(p => p.Key))
            {
                if (pair.Key == 0)
                    continue;

                string messageName = ToMessageName(pair.Value);
                if (!messageNames.Contains(messageName))
                    throw new InvalidOperationException("枚举 " + pair.Value + " 没有同名消息 " + messageName + "。");

                _rows.Add(new MsgRow(pair.Key, pair.Value, messageName));
            }

            foreach (MsgRow row in _rows)
            {
                if (!row.EnumName.EndsWith("_REQ", StringComparison.Ordinal))
                    continue;
                if (row.EnumName == "LOGOUT_REQ")
                    continue;

                int responseId = row.Id + 1;
                if (!numberToName.TryGetValue(responseId, out string responseName))
                    continue;
                if (responseName == "KICK_NTF" || !responseName.EndsWith("_RESP", StringComparison.Ordinal))
                    continue;

                _responseByRequest.Add(row.Id, responseId);
            }

            if (_responseByRequest.ContainsKey(1009) || _responseByRequest.ContainsValue(1010))
                throw new InvalidOperationException("LogoutReq 与 KickNtf 不能配成请求和响应。");
        }

        /// <summary>
        /// 按已通过校验的 descriptor 写出 MsgRegistry.cs。
        /// </summary>
        /// <param name="protoDir">Shared/proto，用于确认消息来源。</param>
        /// <param name="outputDir">放置 MsgRegistry.cs 的目录。</param>
        public void Generate(string protoDir, string outputDir)
        {
            if (_rows.Count == 0)
                throw new InvalidOperationException("请先调用 ValidateIds。");
            if (string.IsNullOrWhiteSpace(protoDir) || !File.Exists(Path.Combine(protoDir, "msg_id.proto")))
                throw new ArgumentException("proto 目录中没有 msg_id.proto。", nameof(protoDir));
            if (string.IsNullOrWhiteSpace(outputDir))
                throw new ArgumentException("输出目录为空。", nameof(outputDir));

            Directory.CreateDirectory(outputDir);
            File.WriteAllText(Path.Combine(outputDir, "MsgRegistry.cs"), BuildRegistry(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        static void CollectMessages(IEnumerable<DescriptorProto> messages, HashSet<string> names)
        {
            foreach (DescriptorProto message in messages)
            {
                names.Add(message.Name);
                CollectMessages(message.NestedType, names);
            }
        }

        /// <summary>HEARTBEAT_REQ 转为 HeartbeatReq。</summary>
        static string ToMessageName(string enumName)
        {
            var builder = new StringBuilder();
            foreach (string part in enumName.Split('_'))
            {
                if (part.Length == 0)
                    continue;
                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                    builder.Append(part.Substring(1).ToLowerInvariant());
            }
            return builder.ToString();
        }

        string BuildRegistry()
        {
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated> 由 ProtoRegistryGenerator 生成，不要手改。");
            builder.AppendLine("using System;");
            builder.AppendLine("using Google.Protobuf;");
            builder.AppendLine();
            builder.AppendLine("namespace CloudIsle.Proto");
            builder.AppendLine("{");
            builder.AppendLine("    public static class MsgRegistry");
            builder.AppendLine("    {");
            builder.AppendLine("        /// <summary>由消息 CLR 类型得到线路上的消息 ID。未知类型抛异常。</summary>");
            builder.AppendLine("        /// <param name=\"messageType\">例如 typeof(LoginReq)。</param>");
            builder.AppendLine("        public static ushort GetId(Type messageType)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (messageType == null)");
            builder.AppendLine("                throw new ArgumentNullException(nameof(messageType));");
            foreach (MsgRow row in _rows)
            {
                builder.AppendLine("            if (messageType == typeof(" + row.MessageName + "))");
                builder.AppendLine("                return " + row.Id + ";");
            }
            builder.AppendLine("            throw new InvalidOperationException(\"未知消息类型: \" + messageType.FullName);");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>按消息 ID 把 body 反序列化成 IMessage。未知 ID 抛异常。</summary>");
            builder.AppendLine("        /// <param name=\"msgId\">消息 ID。</param>");
            builder.AppendLine("        /// <param name=\"body\">该消息的 protobuf 正文，可以为空数组。</param>");
            builder.AppendLine("        public static IMessage Parse(ushort msgId, byte[] body)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (body == null)");
            builder.AppendLine("                throw new ArgumentNullException(nameof(body));");
            builder.AppendLine("            switch (msgId)");
            builder.AppendLine("            {");
            foreach (MsgRow row in _rows)
            {
                builder.AppendLine("                case " + row.Id + ":");
                builder.AppendLine("                    return " + row.MessageName + ".Parser.ParseFrom(body);");
            }
            builder.AppendLine("                default:");
            builder.AppendLine("                    throw new InvalidOperationException(\"未知消息 ID: \" + msgId);");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>查询请求是否有配对响应。</summary>");
            builder.AppendLine("        /// <param name=\"requestId\">请求消息 ID，例如 1005。</param>");
            builder.AppendLine("        /// <param name=\"responseId\">有配对时得到请求 ID + 1；没有配对时为 0。</param>");
            builder.AppendLine("        /// <returns>1009 和 1010 返回 false。</returns>");
            builder.AppendLine("        public static bool TryGetResponseId(ushort requestId, out ushort responseId)");
            builder.AppendLine("        {");
            builder.AppendLine("            switch (requestId)");
            builder.AppendLine("            {");
            foreach (KeyValuePair<int, int> pair in _responseByRequest.OrderBy(p => p.Key))
            {
                builder.AppendLine("                case " + pair.Key + ":");
                builder.AppendLine("                    responseId = " + pair.Value + ";");
                builder.AppendLine("                    return true;");
            }
            builder.AppendLine("                default:");
            builder.AppendLine("                    responseId = 0;");
            builder.AppendLine("                    return false;");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        readonly struct MsgRow
        {
            public MsgRow(int id, string enumName, string messageName)
            {
                Id = id;
                EnumName = enumName;
                MessageName = messageName;
            }

            public int Id { get; }
            public string EnumName { get; }
            public string MessageName { get; }
        }
    }
}
