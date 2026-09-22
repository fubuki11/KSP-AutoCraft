using System;
using System.IO;
using KSPAutoCraft;

internal static class Program
{
    private const string Valid = "{\"name\":\"Test\",\"facility\":\"VAB\",\"parts\":[{\"id\":\"root\",\"partName\":\"test.part\",\"stage\":-1}]}";
    private static int count;
    private static void Check(bool condition) { count++; if (!condition) throw new Exception("JSON test failed: " + count); }
    private static void Reject(string json)
    {
        try { PlanJson.Read(json); }
        catch (FormatException) { count++; return; }
        throw new Exception("Invalid JSON accepted: " + json);
    }
    private static int Main(string[] args)
    {
        try
        {
            var result = PlanJson.Read(Valid);
            Check(result.parts.Length == 1 && result.parts[0].stage == -1 && result.maxCost == 0);
            Check(PlanJson.Read(" \n\t" + Valid + "\r\n").name == "Test");
            Check(PlanJson.Read(Valid.Replace("Test", "\\u706b箭\\\"A\\\"")).name == "火箭\"A\"");
            Check(PlanJson.Read(Valid.Replace("\"facility\"", "\"maxCost\":1.5e+3,\"facility\"")).maxCost == 1500);
            foreach (var bad in new[] { "", "null", "[]", "{}", Valid + "{}", Valid + "x", Valid.Substring(0, Valid.Length - 1),
                Valid.Replace("\"stage\":-1", "\"stage\":1.5"), Valid.Replace("\"stage\":-1", "\"stage\":1e0"),
                Valid.Replace("\"stage\":-1", "\"stage\":2147483648"), Valid.Replace(",\"stage\":-1", ""),
                Valid.Replace("\"stage\":-1", "\"stage\":-1,\"stage\":0"), Valid.Replace("\"parts\"", "\"Parts\""),
                Valid.Replace("\"name\":\"Test\",", ""), Valid.Replace("\"facility\"", "\"maxCots\":1,\"facility\""),
                Valid.Replace("Test", "bad\nname"), Valid.Replace("Test", "\\x00"), Valid.Replace("Test", "\\uZZZZ"),
                Valid.Replace("Test", new string('a', 4097)), Valid.Replace("\"Test\"", "12"),
                Valid.Replace("\"stage\":-1", "\"stage\":true"), Valid.Replace("\"stage\":-1", "\"stage\":null"),
                Valid.Replace("\"stage\":-1", "\"stage\":0,"), Valid.Replace("\"stage\":-1", "\"stage\":0,\"x\":1"),
                Valid.Replace("\"stage\":-1", "\"stage\":0,\"resources\":[{}]"),
                Valid.Replace("\"stage\":-1", "\"stage\":0,\"resources\":[{\"name\":\"Fuel\",\"amount\":1,\"x\":0}]"),
                Valid.Replace("\"name\"", "\"\\u006eame\":\"X\",\"name\"") }) Reject(bad);
            foreach (var value in new[] { "NaN", "Infinity", "-Infinity", "1e9999", "00", "+1", ".1", "1.", "1e", "--1", "true", "\"1\"" })
                Reject(Valid.Replace("\"facility\"", "\"maxCost\":" + value + ",\"facility\""));
            foreach (var value in new[] { "0", "-0", "1", "1.1", "1e-3", "1E+3", "-1.1" })
                Check(PlanValidator.Finite(PlanJson.Read(Valid.Replace("\"facility\"", "\"maxCost\":" + value + ",\"facility\"")).maxCost));
            string withResource = Valid.Replace("\"stage\":-1", "\"stage\":-1,\"resources\":[{\"name\":\"Fuel\",\"amount\":1.25}]");
            Check(PlanJson.Read(withResource).parts[0].resources[0].amount == 1.25);
            var surface = PlanJson.Read(Valid.Replace("\"stage\":-1", "\"stage\":-1,\"attachment\":\"surface\",\"radialAngleDegrees\":90,\"surfaceHeight\":0.5"));
            Check(surface.parts[0].attachment == "surface" && surface.parts[0].radialAngleDegrees == 90 && surface.parts[0].surfaceHeight == 0.5);
            if (args.Length > 0) Check(PlanJson.Read(File.ReadAllText(args[0])).parts.Length == 3);
            Console.WriteLine("JSON contract tests: " + count + " passed, 0 failed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
