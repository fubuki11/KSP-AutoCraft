using System;
using System.IO;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;

internal static class Program
{
    private static int passed;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        passed++;
        Console.WriteLine("PASS " + label);
    }

    private static void SharingViolation(Action operation, string label)
    {
        try { operation(); }
        catch (IOException error) when ((error.HResult & 0xffff) == 32)
        {
            passed++;
            Console.WriteLine("PASS reproduced ERROR_SHARING_VIOLATION: " + label);
            return;
        }
        throw new Exception("Expected Windows sharing violation: " + label);
    }

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 1)
        {
            Console.Error.WriteLine("Run on Windows with the release ZIP path as the only argument.");
            return 2;
        }
        string parent = Path.Combine(Path.GetTempPath(), "opencode");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        string folder = Path.Combine(parent, "autocraft-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.zip");
            string cache = Path.Combine(folder, "cache.zip");
            string moved = Path.Combine(folder, "moved.zip");
            File.Copy(Path.GetFullPath(args[0]), source);
            // CKAN 1.36.4 TryGetFileModules constructs ZipFile without disposing it.
            // Keep the reader alive explicitly so the regression is deterministic.
            using (var reader = new ZipFile(source))
            {
                var entry = reader.GetEntry("KSPAutoCraft.ckan");
                Check(entry != null, "embedded CKAN metadata is discoverable");
                using (var stream = reader.GetInputStream(entry))
                using (var metadata = JsonDocument.Parse(stream))
                    Check(metadata.RootElement.GetProperty("identifier").GetString() == "KSPAutoCraft", "embedded metadata is valid");
                using (var secondReader = new ZipFile(source))
                    Check(secondReader.TestArchive(true), "CRC scan remains possible while import reader is open");
                SharingViolation(() => File.Move(source, moved), "same-volume move while import reader remains open");
                File.Copy(source, cache);
                Check(File.Exists(source) && new FileInfo(cache).Length == new FileInfo(source).Length,
                    "copy and KEEP original succeeds (No at the delete prompt)");
                SharingViolation(() => File.Delete(source), "delete after cross-volume-style copy while reader remains open");
                Check(File.Exists(source), "failed deletion leaves original intact");
                GC.KeepAlive(reader);
            }
            File.Delete(source);
            Check(!File.Exists(source), "deleting after reader disposal succeeds");
            using (var cached = new ZipFile(cache)) Check(cached.TestArchive(true), "retained cached archive remains valid");
            Console.WriteLine("CKAN ZIP handle regression: " + passed + " passed, 0 failed.");
            Console.WriteLine("This reproduces the ZIP-reader/delete conflict using CKAN's ZIP library, not a full GUI installation.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { Directory.Delete(folder, true); }
    }
}
