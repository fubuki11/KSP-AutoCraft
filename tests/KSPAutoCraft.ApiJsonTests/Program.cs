using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using KSPAutoCraft;

internal static class Program
{
    private static int passed;
    private sealed class Inner { public string title = "测试\n\"合同\" 🚀"; public Vec location = new Vec(1.25, 2, 3); }
    private sealed class Envelope
    {
        public Inner[] contracts = { new Inner() };
        public Inner[] empty = new Inner[0];
        public object absent = null;
        public string[] capabilities = { "contracts", "world-context" };
        public bool available = true;
        [NonSerialized] public string ignored = "must-not-serialize";
        public string DangerousProperty { get { throw new Exception("Properties must not be evaluated"); } }
    }
    private sealed class Cycle { public Cycle child; }
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        passed++;
    }
    private static int Main()
    {
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using (var doc = JsonDocument.Parse(ApiJson.Serialize(new Envelope())))
            {
                var root = doc.RootElement;
                Check(root.GetProperty("contracts").GetArrayLength() == 1, "Nested DTO array preserved");
                var item = root.GetProperty("contracts")[0];
                Check(item.GetProperty("title").GetString() == new Inner().title, "Unicode and escapes round trip");
                Check(item.GetProperty("location").GetProperty("x").GetDouble() == 1.25, "Nested struct and invariant floating point");
                Check(root.GetProperty("empty").GetArrayLength() == 0, "Empty arrays retained");
                Check(root.GetProperty("absent").ValueKind == JsonValueKind.Null, "Null retained");
                Check(root.GetProperty("capabilities").GetArrayLength() == 2, "String arrays");
                Check(root.GetProperty("available").GetBoolean(), "Boolean");
                Check(!root.TryGetProperty("ignored", out _), "NonSerialized field omitted");
                Check(!root.TryGetProperty("DangerousProperty", out _), "Properties not evaluated");
            }
            using (var doc = JsonDocument.Parse(ApiJson.Serialize(new PlanResult { valid = true, resolved = new[] { new ResolvedPart() } })))
                Check(!doc.RootElement.TryGetProperty("resolved", out _), "Internal resolved graph excluded");
            using (var doc = JsonDocument.Parse(ApiJson.Serialize(new Dictionary<string, object> { { "quote\"", new object[] { 3L, double.PositiveInfinity, double.NaN, 4.5m } } })))
            {
                var a = doc.RootElement.GetProperty("quote\"");
                Check(a[0].GetInt64() == 3, "Integer dictionary value");
                Check(a[1].ValueKind == JsonValueKind.Null && a[2].ValueKind == JsonValueKind.Null, "Nonfinite values explicitly null");
                Check(a[3].GetDecimal() == 4.5m, "Decimal invariant culture");
            }
            var cycle = new Cycle(); cycle.child = cycle;
            bool rejected = false;
            try { ApiJson.Serialize(cycle); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Cycles bounded");
            for (int i = 0; i < 32; i++)
            {
                string input = "control" + (char)i;
                using (var doc = JsonDocument.Parse(ApiJson.Serialize(input))) Check(doc.RootElement.GetString() == input, "Control escape " + i);
            }
            Console.WriteLine("API JSON tests: " + passed + " passed, 0 failed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
