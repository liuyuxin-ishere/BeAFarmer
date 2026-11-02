using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 结构化 JSON 读取：区分 string / number token，支持 Unicode 转义属性名。
    /// 不把整数 number 静默当 long（避免 double）；int64 业务字段必须走字符串。
    /// </summary>
    public static class GameplayStrictJson
    {
        /// <summary>最大输入字节数（对齐 ≤64KiB 包体）。</summary>
        public const int MaxLength = 65536;

        /// <summary>最大嵌套深度（对象/数组）。</summary>
        public const int MaxDepth = 32;

        public abstract class Node
        {
        }

        public sealed class ObjectNode : Node
        {
            public readonly Dictionary<string, Node> Fields = new Dictionary<string, Node>(StringComparer.Ordinal);
        }

        public sealed class ArrayNode : Node
        {
            public readonly List<Node> Items = new List<Node>();
        }

        public sealed class StringNode : Node
        {
            public StringNode(string value) { Value = value ?? ""; }
            public string Value { get; }
        }

        /// <summary>JSON 数字字面量（保留原文，不经 double）。</summary>
        public sealed class NumberNode : Node
        {
            public NumberNode(string literal) { Literal = literal ?? ""; }
            public string Literal { get; }
        }

        public sealed class BoolNode : Node
        {
            public BoolNode(bool value) { Value = value; }
            public bool Value { get; }
        }

        public sealed class NullNode : Node
        {
            public static readonly NullNode Instance = new NullNode();
        }

        public static Node Parse(string json)
        {
            return Parse(json, MaxLength);
        }

        /// <summary>
        /// 结构化解析；<paramref name="maxLength"/> 允许指纹等超大文档在调用方显式放宽上限。
        /// </summary>
        public static Node Parse(string json, int maxLength)
        {
            if (json == null)
                throw new FormatException("json null");
            if (maxLength < 1)
                throw new FormatException("maxLength must be positive");
            if (json.Length > maxLength)
                throw new FormatException("json length exceeds " + maxLength);
            var p = new Parser(json);
            p.SkipWs();
            Node n = p.ParseValue(0);
            p.SkipWs();
            if (!p.Eof)
                throw new FormatException("trailing data");
            return n;
        }

        sealed class Parser
        {
            readonly string _s;
            int _i;

            public Parser(string s) { _s = s; }

            public bool Eof => _i >= _s.Length;

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                        _i++;
                    else
                        break;
                }
            }

            char Peek() => _i < _s.Length ? _s[_i] : '\0';

            char Next()
            {
                if (_i >= _s.Length)
                    throw new FormatException("unexpected end");
                return _s[_i++];
            }

            public Node ParseValue(int depth)
            {
                if (depth > MaxDepth)
                    throw new FormatException("json depth exceeds " + MaxDepth);
                SkipWs();
                char c = Peek();
                if (c == '{')
                    return ParseObject(depth);
                if (c == '[')
                    return ParseArray(depth);
                if (c == '"')
                    return new StringNode(ParseString());
                if (c == 't')
                {
                    Expect("true");
                    return new BoolNode(true);
                }

                if (c == 'f')
                {
                    Expect("false");
                    return new BoolNode(false);
                }

                if (c == 'n')
                {
                    Expect("null");
                    return NullNode.Instance;
                }

                if (c == '-' || (c >= '0' && c <= '9'))
                    return new NumberNode(ParseNumberLiteral());
                throw new FormatException("unexpected '" + c + "'");
            }

            ObjectNode ParseObject(int depth)
            {
                Next(); // {
                var obj = new ObjectNode();
                SkipWs();
                if (Peek() == '}')
                {
                    Next();
                    return obj;
                }

                while (true)
                {
                    SkipWs();
                    if (Peek() != '"')
                        throw new FormatException("expected property name");
                    string name = ParseString();
                    SkipWs();
                    if (Next() != ':')
                        throw new FormatException("expected :");
                    SkipWs();
                    Node value = ParseValue(depth + 1);
                    if (obj.Fields.ContainsKey(name))
                        throw new FormatException("duplicate property: " + name);
                    obj.Fields.Add(name, value);
                    SkipWs();
                    char c = Next();
                    if (c == '}')
                        break;
                    if (c != ',')
                        throw new FormatException("expected , or }");
                }

                return obj;
            }

            ArrayNode ParseArray(int depth)
            {
                Next(); // [
                var arr = new ArrayNode();
                SkipWs();
                if (Peek() == ']')
                {
                    Next();
                    return arr;
                }

                while (true)
                {
                    arr.Items.Add(ParseValue(depth + 1));
                    SkipWs();
                    char c = Next();
                    if (c == ']')
                        break;
                    if (c != ',')
                        throw new FormatException("expected , or ]");
                }

                return arr;
            }

            string ParseNumberLiteral()
            {
                int start = _i;
                if (Peek() == '-')
                    Next();
                if (Peek() == '0')
                {
                    Next();
                }
                else
                {
                    if (Peek() < '1' || Peek() > '9')
                        throw new FormatException("bad number");
                    while (Peek() >= '0' && Peek() <= '9')
                        Next();
                }

                if (Peek() == '.')
                {
                    Next();
                    if (Peek() < '0' || Peek() > '9')
                        throw new FormatException("bad fraction");
                    while (Peek() >= '0' && Peek() <= '9')
                        Next();
                }

                if (Peek() == 'e' || Peek() == 'E')
                {
                    Next();
                    if (Peek() == '+' || Peek() == '-')
                        Next();
                    if (Peek() < '0' || Peek() > '9')
                        throw new FormatException("bad exponent");
                    while (Peek() >= '0' && Peek() <= '9')
                        Next();
                }

                return _s.Substring(start, _i - start);
            }

            string ParseString()
            {
                if (Next() != '"')
                    throw new FormatException("expected string");
                var sb = new StringBuilder();
                while (true)
                {
                    char c = Next();
                    if (c == '"')
                        return sb.ToString();
                    if (c == '\\')
                    {
                        char e = Next();
                        switch (e)
                        {
                            case '"':
                            case '\\':
                            case '/':
                                sb.Append(e);
                                break;
                            case 'b':
                                sb.Append('\b');
                                break;
                            case 'f':
                                sb.Append('\f');
                                break;
                            case 'n':
                                sb.Append('\n');
                                break;
                            case 'r':
                                sb.Append('\r');
                                break;
                            case 't':
                                sb.Append('\t');
                                break;
                            case 'u':
                                int code = 0;
                                for (int k = 0; k < 4; k++)
                                {
                                    char h = Next();
                                    code <<= 4;
                                    if (h >= '0' && h <= '9')
                                        code |= h - '0';
                                    else if (h >= 'a' && h <= 'f')
                                        code |= h - 'a' + 10;
                                    else if (h >= 'A' && h <= 'F')
                                        code |= h - 'A' + 10;
                                    else
                                        throw new FormatException("bad unicode escape");
                                }

                                sb.Append((char)code);
                                break;
                            default:
                                throw new FormatException("bad escape");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            void Expect(string lit)
            {
                for (int k = 0; k < lit.Length; k++)
                {
                    if (Next() != lit[k])
                        throw new FormatException("expected " + lit);
                }
            }
        }
    }
}
