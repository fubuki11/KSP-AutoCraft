using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace KSPAutoCraft
{
    // Unity JsonUtility can omit nested DTOs from dynamically loaded KSP assemblies.
    // This writer handles explicit public-field API DTOs without Unity type-tree metadata.
    internal static class ApiJson
    {
        internal static string Serialize(object value)
        {
            var builder = new StringBuilder();
            Write(builder, value, 0);
            return builder.ToString();
        }

        private static void Write(StringBuilder builder, object value, int depth)
        {
            if (depth > 40) throw new InvalidOperationException("JSON object nesting exceeds the API limit.");
            if (value == null) { builder.Append("null"); return; }
            var type = value.GetType();
            if (value is string || value is char || type.IsEnum) { String(builder, value.ToString()); return; }
            if (value is bool) { builder.Append((bool)value ? "true" : "false"); return; }
            if (value is double)
            {
                double number = (double)value;
                builder.Append(double.IsNaN(number) || double.IsInfinity(number) ? "null" : number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is float)
            {
                float number = (float)value;
                builder.Append(float.IsNaN(number) || float.IsInfinity(number) ? "null" : number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64: case TypeCode.Decimal:
                    builder.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                    return;
            }
            bool first = true;
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                builder.Append('{');
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!(entry.Key is string)) throw new InvalidOperationException("JSON object keys must be strings.");
                    if (!first) builder.Append(',');
                    first = false;
                    String(builder, (string)entry.Key); builder.Append(':'); Write(builder, entry.Value, depth + 1);
                }
                builder.Append('}');
                return;
            }
            var sequence = value as IEnumerable;
            if (sequence != null)
            {
                builder.Append('[');
                int count = 0;
                foreach (object entry in sequence)
                {
                    if (++count > 100000) throw new InvalidOperationException("JSON collection exceeds the API limit.");
                    if (!first) builder.Append(',');
                    first = false;
                    Write(builder, entry, depth + 1);
                }
                builder.Append(']');
                return;
            }
            builder.Append('{');
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                if (!first) builder.Append(',');
                first = false;
                String(builder, field.Name); builder.Append(':'); Write(builder, field.GetValue(value), depth + 1);
            }
            builder.Append('}');
        }

        private static void String(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 32 || char.IsSurrogate(c)) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
