using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CloudIsle.Config
{
    /// <summary>
    /// PoC #7：极简 JSON 读取（仅支持导表产物：对象数组 / 扁平对象 / 基础类型）。
    /// 避免 Shared 依赖 UnityEngine.JsonUtility 或额外 NuGet。
    /// </summary>
    public static class SimpleJson
    {
        public sealed class Obj
        {
            readonly Dictionary<string, object> _map;

            public Obj(Dictionary<string, object> map)
            {
                _map = map;
            }

            public string GetString(string key)
            {
                if (!_map.TryGetValue(key, out var v) || v == null)
                    return "";
                return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            }

            public int GetInt(string key)
            {
                if (!_map.TryGetValue(key, out var v) || v == null)
                    return 0;
                if (v is long l)
                    return (int)l;
                if (v is double d)
                    return (int)Math.Round(d);
                return int.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }

            public long GetLong(string key)
            {
                if (!_map.TryGetValue(key, out var v) || v == null)
                    return 0L;
                if (v is long l)
                    return l;
                if (v is double d)
                    return (long)Math.Round(d);
                return long.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }

            public float GetFloat(string key)
            {
                if (!_map.TryGetValue(key, out var v) || v == null)
                    return 0f;
                if (v is double d)
                    return (float)d;
                if (v is long l)
                    return l;
                return float.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }

            public double GetDouble(string key)
            {
                if (!_map.TryGetValue(key, out var v) || v == null)
                    return 0d;
                if (v is double d)
                    return d;
                if (v is long l)
                    return l;
                return double.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }

            /// <summary>
            /// 必须存在且为 JSON 数值（解析为 long 或 double）。
            /// 拒绝缺失、null、字符串、boolean 及其他非数值；不走 <see cref="GetDouble"/> 的兼容转换。
            /// </summary>
            public double RequireJsonNumber(string key)
            {
                if (!_map.TryGetValue(key, out var v) || v == null)
                    throw new FormatException("expected json number " + key);
                if (v is double d)
                    return d;
                if (v is long l)
                    return l;
                throw new FormatException("expected json number " + key);
            }

            public bool GetBool(string key)
            {
                if (!_map.TryGetValue(key, out var v) || v == null)
                    return false;
                if (v is bool b)
                    return b;
                var s = Convert.ToString(v, CultureInfo.InvariantCulture);
                return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>键是否出现在这个对象里。</summary>
            /// <param name="key">要查的字段名。</param>
            /// <returns>字段存在时为 true。值是 null 也算存在。</returns>
            public bool Has(string key)
            {
                return _map.ContainsKey(key);
            }

            /// <summary>按字段名取出必须是 JSON 字符串的值。</summary>
            /// <param name="key">字段名。</param>
            /// <returns>字段里的字符串。</returns>
            public string RequireString(string key)
            {
                if (!_map.TryGetValue(key, out var v) || !(v is string text))
                    throw new FormatException("expected string " + key);
                return text;
            }

            /// <summary>按字段名取出嵌套对象。</summary>
            /// <param name="key">字段名。</param>
            /// <returns>该字段对应的对象。</returns>
            public Obj GetObject(string key)
            {
                if (!_map.TryGetValue(key, out var v) || !(v is Dictionary<string, object> map))
                    throw new FormatException("expected object " + key);
                return new Obj(map);
            }

            /// <summary>按字段名取出对象数组。</summary>
            /// <param name="key">字段名。</param>
            /// <returns>数组里的每个元素都是对象。</returns>
            public List<Obj> GetObjectArray(string key)
            {
                if (!_map.TryGetValue(key, out var v) || !(v is List<object> list))
                    throw new FormatException("expected array " + key);
                var result = new List<Obj>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    if (!(list[i] is Dictionary<string, object> map))
                        throw new FormatException("array element must be object");
                    result.Add(new Obj(map));
                }
                return result;
            }

            /// <summary>按字段名取出非负整数。拒绝字符串和小数。</summary>
            /// <param name="key">字段名。</param>
            /// <returns>字段中的整数。</returns>
            public long RequireLong(string key)
            {
                if (!_map.TryGetValue(key, out var v) || !(v is long number) || number < 0)
                    throw new FormatException("expected non-negative integer " + key);
                return number;
            }
        }

        public static List<Obj> ParseArray(string json)
        {
            var p = new Parser(json);
            p.SkipWs();
            if (p.Peek() != '[')
                throw new FormatException("expected JSON array");
            var list = p.ParseArray();
            var result = new List<Obj>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is Dictionary<string, object> map))
                    throw new FormatException("array element must be object");
                result.Add(new Obj(map));
            }
            return result;
        }

        /// <summary>解析一个 JSON 对象。末尾多余内容会失败。</summary>
        /// <param name="json">完整的 JSON 对象文本。</param>
        /// <returns>根对象。</returns>
        public static Obj ParseObject(string json)
        {
            var p = new Parser(json);
            p.SkipWs();
            if (p.Peek() != '{')
                throw new FormatException("expected JSON object");
            Obj result = new Obj(p.ParseObject());
            p.SkipWs();
            if (p.Peek() != '\0')
                throw new FormatException("trailing data");
            return result;
        }

        sealed class Parser
        {
            readonly string _s;
            int _i;

            public Parser(string s)
            {
                _s = s ?? throw new ArgumentNullException(nameof(s));
            }

            public char Peek() => _i < _s.Length ? _s[_i] : '\0';

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

            char Next()
            {
                if (_i >= _s.Length)
                    throw new FormatException("unexpected end");
                return _s[_i++];
            }

            public List<object> ParseArray()
            {
                Next(); // [
                var list = new List<object>();
                SkipWs();
                if (Peek() == ']')
                {
                    Next();
                    return list;
                }
                while (true)
                {
                    SkipWs();
                    list.Add(ParseValue());
                    SkipWs();
                    char c = Next();
                    if (c == ']')
                        break;
                    if (c != ',')
                        throw new FormatException("expected , or ]");
                }
                return list;
            }

            public Dictionary<string, object> ParseObject()
            {
                Next(); // {
                var map = new Dictionary<string, object>();
                SkipWs();
                if (Peek() == '}')
                {
                    Next();
                    return map;
                }
                while (true)
                {
                    SkipWs();
                    string key = ParseString();
                    SkipWs();
                    if (Next() != ':')
                        throw new FormatException("expected :");
                    SkipWs();
                    map[key] = ParseValue();
                    SkipWs();
                    char c = Next();
                    if (c == '}')
                        break;
                    if (c != ',')
                        throw new FormatException("expected , or }");
                }
                return map;
            }

            object ParseValue()
            {
                SkipWs();
                char c = Peek();
                if (c == '{')
                    return ParseObject();
                if (c == '[')
                    return ParseArray();
                if (c == '"')
                    return ParseString();
                if (c == 't')
                {
                    Expect("true");
                    return true;
                }
                if (c == 'f')
                {
                    Expect("false");
                    return false;
                }
                if (c == 'n')
                {
                    Expect("null");
                    return null;
                }
                return ParseNumber();
            }

            void Expect(string lit)
            {
                for (int k = 0; k < lit.Length; k++)
                {
                    if (Next() != lit[k])
                        throw new FormatException("expected " + lit);
                }
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
                                int code = Convert.ToInt32(new string(new[] { Next(), Next(), Next(), Next() }), 16);
                                sb.Append((char)code);
                                break;
                            default:
                                throw new FormatException("bad escape");
                        }
                    }
                    else
                        sb.Append(c);
                }
            }

            object ParseNumber()
            {
                int start = _i;
                if (Peek() == '-')
                    Next();
                while (char.IsDigit(Peek()))
                    Next();
                bool isFloat = false;
                if (Peek() == '.')
                {
                    isFloat = true;
                    Next();
                    while (char.IsDigit(Peek()))
                        Next();
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    isFloat = true;
                    Next();
                    if (Peek() == '+' || Peek() == '-')
                        Next();
                    while (char.IsDigit(Peek()))
                        Next();
                }
                string num = _s.Substring(start, _i - start);
                if (isFloat)
                    return double.Parse(num, CultureInfo.InvariantCulture);
                return long.Parse(num, CultureInfo.InvariantCulture);
            }
        }
    }
}
