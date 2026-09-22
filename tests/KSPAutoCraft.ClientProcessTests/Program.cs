using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using KSPAutoCraft;

internal static class Program
{
    private static int passed;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        passed++;
    }
    private static ClientProcess Launch(string mode, int timeout, params string[] args)
    {
        var command = new List<string>();
        string executable = Environment.ProcessPath;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) command.Add(typeof(Program).Assembly.Location);
        command.Add("--child"); command.Add(mode); command.AddRange(args);
        var process = new ClientProcess();
        process.Start(executable, command.ToArray(), AppContext.BaseDirectory, new Dictionary<string, string> { { "AUTOCRAFT_TEST_VALUE", "child-only" } }, timeout);
        return process;
    }
    private static ClientProcessResult Wait(ClientProcess process)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 10000)
        {
            var result = process.Poll();
            if (result != null) return result;
            Thread.Sleep(10);
        }
        throw new Exception("Runner did not finish in test deadline.");
    }
    private static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--child")
        {
            switch (args[1])
            {
                case "echo": Console.WriteLine(JsonSerializer.Serialize(args.Skip(2).ToArray())); return 0;
                case "environment": Console.Write(Environment.GetEnvironmentVariable("AUTOCRAFT_TEST_VALUE")); return 0;
                case "fail": Console.Write("partial"); Console.Error.Write("fixture error"); return 23;
                case "sleep": Thread.Sleep(10000); return 0;
                case "flood": Console.Write(new string('x', 120000)); Console.Error.Write(new string('y', 120000)); Thread.Sleep(10000); return 0;
            }
            return 2;
        }
        try
        {
            string[] values = { "", "simple", "with spaces", "C:\\space dir\\", "quotes\"and\\\"slashes", "中文 🚀", "x & echo unexpected", "line1\nline2" };
            using (var process = Launch("echo", 8000, values))
            {
                var result = Wait(process);
                Check(result.state == "completed" && result.exitCode == 0, "Normal completion");
                Check(JsonSerializer.Deserialize<string[]>(result.stdout).SequenceEqual(values), "Windows argument quoting and Unicode round trip");
                Check(result.stderr == "", "Output channels stay separate");
                Check(process.Poll() == result, "Polling completion is stable");
            }
            using (var process = Launch("environment", 8000)) Check(Wait(process).stdout == "child-only", "Child environment supplied");
            Check(Environment.GetEnvironmentVariable("AUTOCRAFT_TEST_VALUE") != "child-only", "Parent environment untouched");
            using (var process = Launch("fail", 8000))
            {
                var result = Wait(process);
                Check(result.state == "failed" && result.exitCode == 23, "Native failure preserved");
                Check(result.stdout == "partial" && result.stderr == "fixture error", "Failed output fully drained");
            }
            using (var process = Launch("sleep", 150)) Check(Wait(process).state == "timeout", "Timeout terminates owned work");
            using (var process = Launch("sleep", 8000))
            {
                Check(process.Cancel().state == "cancelled", "Explicit cancellation");
                Check(!process.Running, "Cancelled job cannot be treated as running");
            }
            using (var process = Launch("flood", 8000))
            {
                var result = Wait(process);
                Check(result.state == "output_limit", "Excessive output terminates work");
                Check(result.stdout.Length + result.stderr.Length <= 65536, "Output storage bounded");
            }
            var disposed = Launch("sleep", 8000); disposed.Dispose(); disposed.Dispose();
            Check(!disposed.Running, "Dispose is idempotent and prevents result consumption");
            Console.WriteLine("Client process tests: " + passed + " passed, 0 failed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
