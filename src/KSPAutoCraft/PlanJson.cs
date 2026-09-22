using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KSPAutoCraft
{
    // Deliberately narrow JSON reader. JsonUtility silently ignores misspelled fields
    // and coerces numbers; plans instead require exact, duplicate-free field names.
    internal sealed class PlanJson
    {
        private readonly string text;
        private int index;
        private PlanJson(string text) { this.text = text ?? ""; }
        internal static CraftPlan Read(string json)
        {
            var reader = new PlanJson(json);
            var plan = reader.Plan();
            reader.Space();
            if (reader.index != reader.text.Length) throw reader.Error("Unexpected trailing JSON content.");
            return plan;
        }

        private CraftPlan Plan()
        {
            var plan = new CraftPlan();
            var fields = Object(key => {
                switch (key)
                {
                    case "name": plan.name = String(); break;
                    case "facility": plan.facility = String(); break;
                    case "parts": plan.parts = Array(Part, 128); break;
                    case "maxWetMassTonnes": plan.maxWetMassTonnes = Number(); break;
                    case "maxCost": plan.maxCost = Number(); break;
                    default: throw Error("Unknown craft plan field.");
                }
            });
            Required(fields, "name", "facility", "parts");
            return plan;
        }

        private PlanPart Part()
        {
            var part = new PlanPart();
            var fields = Object(key => {
                switch (key)
                {
                    case "id": part.id = String(); break;
                    case "partName": part.partName = String(); break;
                    case "parentId": part.parentId = String(); break;
                    case "parentNodeId": part.parentNodeId = String(); break;
                    case "childNodeId": part.childNodeId = String(); break;
                    case "stage": part.stage = Integer(); break;
                    case "rollDegrees": part.rollDegrees = Number(); break;
                    case "attachment": part.attachment = String(); break;
                    case "radialAngleDegrees": part.radialAngleDegrees = Number(); break;
                    case "surfaceHeight": part.surfaceHeight = Number(); break;
                    case "surfaceOrientation": part.surfaceOrientation = String(); break;
                    case "mirrorOf": part.mirrorOf = String(); break;
                    case "resources": part.resources = Array(Resource, 128); break;
                    default: throw Error("Unknown part field.");
                }
            });
            Required(fields, "id", "partName", "stage");
            return part;
        }

        private ResourceAmount Resource()
        {
            var resource = new ResourceAmount();
            var fields = Object(key => {
                switch (key)
                {
                    case "name": resource.name = String(); break;
                    case "amount": resource.amount = Number(); break;
                    default: throw Error("Unknown resource field.");
                }
            });
            Required(fields, "name", "amount");
            return resource;
        }

        private void Required(HashSet<string> fields, params string[] names)
        {
            foreach (var name in names) if (!fields.Contains(name)) throw Error("Missing required field: " + name);
        }

        private HashSet<string> Object(Action<string> value)
        {
            Take('{');
            var fields = new HashSet<string>(StringComparer.Ordinal);
            if (Consume('}')) return fields;
            do
            {
                string key = String();
                if (!fields.Add(key)) throw Error("Duplicate JSON field.");
                Take(':');
                value(key);
                if (Consume('}')) return fields;
                Take(',');
            } while (true);
        }

        private T[] Array<T>(Func<T> item, int limit)
        {
            Take('[');
            var values = new List<T>();
            if (Consume(']')) return values.ToArray();
            do
            {
                if (values.Count >= limit) throw Error("JSON array exceeds the supported size.");
                values.Add(item());
                if (Consume(']')) return values.ToArray();
                Take(',');
            } while (true);
        }

        private string String()
        {
            Take('"');
            var value = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return value.ToString();
                if (c < 32) throw Error("Unescaped control character in string.");
                if (c == '\\')
                {
                    if (index >= text.Length) throw Error("Incomplete string escape.");
                    switch (text[index++])
                    {
                        case '"': c = '"'; break;
                        case '\\': c = '\\'; break;
                        case '/': c = '/'; break;
                        case 'b': c = '\b'; break;
                        case 'f': c = '\f'; break;
                        case 'n': c = '\n'; break;
                        case 'r': c = '\r'; break;
                        case 't': c = '\t'; break;
                        case 'u':
                            ushort code;
                            if (index + 4 > text.Length || !ushort.TryParse(text.Substring(index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                throw Error("Invalid Unicode escape.");
                            c = (char)code;
                            index += 4;
                            break;
                        default: throw Error("Invalid string escape.");
                    }
                }
                value.Append(c);
                if (value.Length > 4096) throw Error("JSON string is too long.");
            }
            throw Error("Unterminated string.");
        }

        private int Integer()
        {
            string token = NumberToken();
            int result;
            if (!int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
                throw Error("stage must be an integer token, without a decimal or exponent.");
            return result;
        }
        private double Number()
        {
            double result;
            if (!double.TryParse(NumberToken(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) || !PlanValidator.Finite(result))
                throw Error("Expected a finite number.");
            return result;
        }
        private string NumberToken()
        {
            Space();
            int start = index;
            if (index < text.Length && text[index] == '-') index++;
            if (index < text.Length && text[index] == '0') index++;
            else Digits();
            if (index < text.Length && text[index] == '.') { index++; Digits(); }
            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                Digits();
            }
            return text.Substring(start, index - start);
        }
        private void Digits()
        {
            int start = index;
            while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            if (start == index) throw Error("Expected a JSON number.");
        }
        private void Space()
        {
            while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\r' || text[index] == '\n')) index++;
        }
        private bool Consume(char c)
        {
            Space();
            if (index < text.Length && text[index] == c) { index++; return true; }
            return false;
        }
        private void Take(char c) { if (!Consume(c)) throw Error("Expected '" + c + "'."); }
        private FormatException Error(string message) { return new FormatException(message + " At character " + index + "."); }
    }
}
