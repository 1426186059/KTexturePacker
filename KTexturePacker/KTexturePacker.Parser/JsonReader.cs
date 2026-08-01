using System;
using System.Collections.Generic;
using System.Globalization;

namespace KTexturePacker.Parser
{
    /// <summary>
    /// 极简 JSON 读取器（零依赖，仅供本解析库读取 KTexturePacker 导出的图集 JSON）。
    /// 支持：对象、数组、字符串（含转义）、数字、布尔、null。不支持注释与尾随逗号。
    /// 解析结果为 JsonValue 图：Object / Array / string / double / bool / null。
    /// </summary>
    internal sealed class JsonValue
    {
        public enum KindType { Null, Bool, Number, String, Array, Object }
        public KindType Kind;
        public bool Bool;
        public double Number;
        public string Str;
        public List<JsonValue> Items;          // Array
        public Dictionary<string, JsonValue> Members; // Object

        public bool IsObject => Kind == KindType.Object;
        public bool IsArray => Kind == KindType.Array;

        public bool TryGetValue(string name, out JsonValue v)
        {
            if (Members != null && Members.TryGetValue(name, out v)) return true;
            v = null;
            return false;
        }

        public string GetString(string name)
            => TryGetValue(name, out var v) && v.Kind == KindType.String ? v.Str : null;
        public int GetInt(string name)
            => TryGetValue(name, out var v) && v.Kind == KindType.Number ? (int)v.Number : 0;
        public bool GetBool(string name)
            => TryGetValue(name, out var v) && v.Kind == KindType.Bool && v.Bool;
    }

    internal sealed class JsonReader
    {
        private readonly string _s;
        private int _i;

        public JsonReader(string s) { _s = s; }

        public bool ReadObject(out JsonValue value)
        {
            SkipWs();
            return TryReadValue(out value) && value.Kind == JsonValue.KindType.Object;
        }

        private bool TryReadValue(out JsonValue value)
        {
            value = null;
            SkipWs();
            if (_i >= _s.Length) return false;
            char c = _s[_i];
            switch (c)
            {
                case '{': value = ReadObject(); return true;
                case '[': value = ReadArray(); return true;
                case '"': value = new JsonValue { Kind = JsonValue.KindType.String, Str = ReadString() }; return true;
                case 't': case 'f': value = new JsonValue { Kind = JsonValue.KindType.Bool, Bool = ReadBool() }; return true;
                case 'n': ReadNull(); value = new JsonValue { Kind = JsonValue.KindType.Null }; return true;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        value = new JsonValue { Kind = JsonValue.KindType.Number, Number = ReadNumber() };
                        return true;
                    }
                    value = null;
                    return false;
            }
        }

        private JsonValue ReadObject()
        {
            var obj = new JsonValue { Kind = JsonValue.KindType.Object, Members = new Dictionary<string, JsonValue>() };
            _i++; // {
            SkipWs();
            if (Peek() == '}') { _i++; return obj; }
            while (true)
            {
                SkipWs();
                string key = ReadString();
                SkipWs();
                if (Peek() != ':') throw new FormatException("Json: expected ':' at " + _i);
                _i++; // :
                if (!TryReadValue(out var v)) throw new FormatException("Json: invalid value at " + _i);
                obj.Members[key] = v;
                SkipWs();
                char ch = Peek();
                if (ch == ',') { _i++; continue; }
                if (ch == '}') { _i++; break; }
                throw new FormatException("Json: expected ',' or '}' at " + _i);
            }
            return obj;
        }

        private JsonValue ReadArray()
        {
            var arr = new JsonValue { Kind = JsonValue.KindType.Array, Items = new List<JsonValue>() };
            _i++; // [
            SkipWs();
            if (Peek() == ']') { _i++; return arr; }
            while (true)
            {
                if (!TryReadValue(out var v)) throw new FormatException("Json: invalid value at " + _i);
                arr.Items.Add(v);
                SkipWs();
                char ch = Peek();
                if (ch == ',') { _i++; continue; }
                if (ch == ']') { _i++; break; }
                throw new FormatException("Json: expected ',' or ']' at " + _i);
            }
            return arr;
        }

        private string ReadString()
        {
            if (Peek() != '"') throw new FormatException("Json: expected '\"' at " + _i);
            _i++; // "
            var sb = new System.Text.StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (_i >= _s.Length) throw new FormatException("Json: unterminated escape.");
                    char e = _s[_i++];
                    switch (e)
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
                            if (_i + 4 > _s.Length) throw new FormatException("Json: invalid \\u escape.");
                            string hex = _s.Substring(_i, 4);
                            _i += 4;
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Json: unterminated string.");
        }

        private bool ReadBool()
        {
            if (_s.Substring(_i, 4) == "true") { _i += 4; return true; }
            if (_i + 5 <= _s.Length && _s.Substring(_i, 5) == "false") { _i += 5; return false; }
            throw new FormatException("Json: invalid bool at " + _i);
        }

        private void ReadNull()
        {
            if (_i + 4 <= _s.Length && _s.Substring(_i, 4) == "null") { _i += 4; return; }
            throw new FormatException("Json: invalid null at " + _i);
        }

        private double ReadNumber()
        {
            int start = _i;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c >= '0' && c <= '9') _i++;
                else if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _i++;
                else break;
            }
            return double.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture);
        }

        private void SkipWs()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                else break;
            }
        }

        private char Peek()
        {
            SkipWs();
            return _i < _s.Length ? _s[_i] : '\0';
        }
    }
}
