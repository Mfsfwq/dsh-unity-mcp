// 最小 JSON 实现（零依赖）。
// 对象 -> Dictionary<string, object>，数组 -> List<object>，数字 -> double/long，布尔/字符串/null 原样。
// 之所以不用 Newtonsoft：本插件刻意保持零第三方包，避免 mcp-unity 那种依赖链兼容问题。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DshBridge
{
    public static class Json
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var parser = new Parser(text);
            return parser.ParseValue();
        }

        public static string Stringify(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }

            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }

            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is float) { sb.Append(((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is double) { sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is decimal)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }

            var genericDict = v as IDictionary<string, object>;
            if (genericDict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in genericDict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }

            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- 读取辅助 ----
        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static string Str(IDictionary<string, object> obj, string key, string @default = null)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v) || v == null) return @default;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(IDictionary<string, object> obj, string key, bool @default = false)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v) || v == null) return @default;
            if (v is bool) return (bool)v;
            var s = v as string;
            if (s != null) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0;
        }

        public static double Num(IDictionary<string, object> obj, string key, double @default = 0)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v) || v == null) return @default;
            if (v is double) return (double)v;
            if (v is long) return (long)v;
            return @default;
        }

        public static List<object> Arr(IDictionary<string, object> obj, string key)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }

        sealed class Parser
        {
            readonly string _text;
            int _pos;

            public Parser(string text) { _text = text; _pos = 0; }

            public object ParseValue()
            {
                SkipWhitespace();
                if (_pos >= _text.Length) throw new FormatException("JSON 提前结束");

                char c = _text[_pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>();
                _pos++; // {
                SkipWhitespace();
                if (Peek() == '}') { _pos++; return result; }

                while (true)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':') throw new FormatException("JSON 对象缺少冒号，位置 " + _pos);
                    _pos++;
                    result[key] = ParseValue();
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { _pos++; continue; }
                    if (c == '}') { _pos++; return result; }
                    throw new FormatException("JSON 对象格式错误，位置 " + _pos);
                }
            }

            List<object> ParseArray()
            {
                var result = new List<object>();
                _pos++; // [
                SkipWhitespace();
                if (Peek() == ']') { _pos++; return result; }

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { _pos++; continue; }
                    if (c == ']') { _pos++; return result; }
                    throw new FormatException("JSON 数组格式错误，位置 " + _pos);
                }
            }

            string ParseString()
            {
                if (Peek() != '"') throw new FormatException("JSON 字符串应以引号开始，位置 " + _pos);
                _pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (_pos >= _text.Length) throw new FormatException("JSON 字符串未闭合");
                    char c = _text[_pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }

                    char esc = _text[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(_text.Substring(_pos, 4), 16));
                            _pos += 4;
                            break;
                        default: throw new FormatException("未知转义 \\" + esc);
                    }
                }
            }

            object ParseNumber()
            {
                int start = _pos;
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E' || (c >= '0' && c <= '9')) _pos++;
                    else break;
                }
                string token = _text.Substring(start, _pos - start);
                long l;
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw new FormatException("JSON 数字非法: " + token);
            }

            void Expect(string token)
            {
                if (_pos + token.Length > _text.Length || _text.Substring(_pos, token.Length) != token)
                    throw new FormatException("JSON 期望 " + token + "，位置 " + _pos);
                _pos += token.Length;
            }

            char Peek()
            {
                if (_pos >= _text.Length) throw new FormatException("JSON 提前结束");
                return _text[_pos];
            }

            void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _pos++;
                    else break;
                }
            }
        }
    }
}
