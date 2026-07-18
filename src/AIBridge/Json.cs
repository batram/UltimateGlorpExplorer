#if MONO
using System.Collections;
using System.Text;

namespace UnityExplorer.AIBridge
{
    // Minimal net35-safe JSON writer. Supports null, bool, string, numeric types,
    // IDictionary (string keys) and IEnumerable.
    public static class Json
    {
        public static string Serialize(object value)
        {
            StringBuilder sb = new();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            switch (value)
            {
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case int or long or short or byte or sbyte or uint or ulong or ushort:
                    sb.Append(value.ToString());
                    return;
                case float f:
                    sb.Append(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case IDictionary dict:
                    {
                        sb.Append('{');
                        bool first = true;
                        foreach (DictionaryEntry entry in dict)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            WriteString(sb, entry.Key?.ToString() ?? "null");
                            sb.Append(':');
                            Write(sb, entry.Value);
                        }
                        sb.Append('}');
                        return;
                    }
                case IEnumerable list:
                    {
                        sb.Append('[');
                        bool first = true;
                        foreach (object item in list)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            Write(sb, item);
                        }
                        sb.Append(']');
                        return;
                    }
                default:
                    WriteString(sb, value.ToString());
                    return;
            }
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
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
#endif
