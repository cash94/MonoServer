// Небольшой JSON без зависимостей: System.Text.Json для netstandard2.0 — это
// ещё несколько сборок в пакете, а нужно немного — разобрать тело запроса
// TorrServer ({"action":"add","link":…}) и собрать ответ.
//   Json.Parse(text) → Dictionary<string, object> / List<object> / string /
//                      double / bool / null
//   new JsonWriter().Obj().Key("a").Val(1).End() — запись по порядку
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TorrStreamTorrServer
{
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("JSON: лишнее после значения на " + i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
            return Parse(text) as Dictionary<string, object> ?? throw new FormatException("JSON: ждали объект");
        }

        public static string Str(this Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
            if (v is string s) return s;
            if (v is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            return null;
        }

        public static long? Num(this Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
            if (v is double d) return (long)d;
            long n;
            if (v is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return null;
        }

        public static bool? Bool(this Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
            if (v is bool b) return b;
            if (v is double d) return d != 0;
            if (v is string s) return s == "true" || s == "1";
            return null;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: неожиданный конец");
            char c = s[i];
            if (c == '{')
            {
                var o = new Dictionary<string, object>();
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    SkipWs(s, ref i);
                    string key = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("JSON: ждали «:» на " + i);
                    i++;
                    o[key] = ParseValue(s, ref i);
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    throw new FormatException("JSON: ждали «,» или «}» на " + i);
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(ParseValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    throw new FormatException("JSON: ждали «,» или «]» на " + i);
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException("JSON: непонятный символ на " + i);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("JSON: ждали строку на " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON: обрыв \\u");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("JSON: незакрытая строка");
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Значение из Parse обратно в текст (для «data» и настроек как есть)</summary>
        public static string Write(object v)
        {
            var w = new JsonWriter();
            w.Val(v);
            return w.ToString();
        }
    }

    public class JsonWriter
    {
        readonly StringBuilder sb = new StringBuilder();
        // true — в текущем объекте/массиве уже есть элемент (нужна запятая)
        readonly Stack<bool> hasItem = new Stack<bool>();
        bool afterKey;

        void Sep()
        {
            if (afterKey) { afterKey = false; return; }
            if (hasItem.Count > 0)
            {
                if (hasItem.Peek()) sb.Append(',');
                hasItem.Pop();
                hasItem.Push(true);
            }
        }

        public JsonWriter Obj() { Sep(); sb.Append('{'); hasItem.Push(false); return this; }
        public JsonWriter Arr() { Sep(); sb.Append('['); hasItem.Push(false); return this; }
        public JsonWriter EndObj() { hasItem.Pop(); sb.Append('}'); return this; }
        public JsonWriter EndArr() { hasItem.Pop(); sb.Append(']'); return this; }

        public JsonWriter Key(string k)
        {
            Sep();
            sb.Append(Json.Quote(k)).Append(':');
            afterKey = true;
            return this;
        }

        public JsonWriter Raw(string json) { Sep(); sb.Append(json); return this; }
        public JsonWriter Val(string s) { Sep(); sb.Append(Json.Quote(s)); return this; }
        public JsonWriter Val(long n) { Sep(); sb.Append(n.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Val(double n) { Sep(); sb.Append(n.ToString("R", CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Val(bool b) { Sep(); sb.Append(b ? "true" : "false"); return this; }

        public JsonWriter Val(object v)
        {
            if (v == null) return Raw("null");
            if (v is string s) return Val(s);
            if (v is bool b) return Val(b);
            if (v is double d) return (d == Math.Floor(d) && Math.Abs(d) < 1e15) ? Val((long)d) : Val(d);
            if (v is int i) return Val((long)i);
            if (v is long l) return Val(l);
            if (v is Dictionary<string, object> o)
            {
                Obj();
                foreach (var kv in o) { Key(kv.Key); Val(kv.Value); }
                return EndObj();
            }
            if (v is List<object> a)
            {
                Arr();
                foreach (var x in a) Val(x);
                return EndArr();
            }
            return Val(v.ToString());
        }

        public JsonWriter Prop(string k, string v) { return Key(k).Val(v); }
        public JsonWriter Prop(string k, long v) { return Key(k).Val(v); }
        public JsonWriter Prop(string k, double v) { return Key(k).Val(v); }
        public JsonWriter Prop(string k, bool v) { return Key(k).Val(v); }

        public override string ToString() { return sb.ToString(); }
    }
}
