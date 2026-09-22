using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace KSPAutoCraft
{
    internal sealed class ClientProcessResult
    {
        internal string state, stdout, stderr;
        internal int exitCode;
    }

    // Owns one child process. Readers never access Unity and have bounded buffers.
    internal sealed class ClientProcess : IDisposable
    {
        private const int MaxCharacters = 65536;
        private readonly object gate = new object();
        private readonly StringBuilder stdout = new StringBuilder(), stderr = new StringBuilder();
        private readonly Stopwatch timer = new Stopwatch();
        private Process process;
        private Task outputReader, errorReader;
        private bool overflow, disposed;
        private int timeoutMilliseconds;
        private ClientProcessResult result;

        internal double ElapsedSeconds { get { return timer.Elapsed.TotalSeconds; } }
        internal bool Running { get { return process != null && result == null && !disposed; } }

        internal void Start(string executable, string[] arguments, string directory, IDictionary<string, string> environment, int timeout)
        {
            if (process != null || result != null || disposed) throw new InvalidOperationException("Process runner cannot be reused.");
            if (timeout < 1) throw new ArgumentOutOfRangeException(nameof(timeout));
            timeoutMilliseconds = timeout;
            var info = new ProcessStartInfo {
                FileName = executable, Arguments = Arguments(arguments), WorkingDirectory = directory,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            if (environment != null) foreach (var pair in environment) info.EnvironmentVariables[pair.Key] = pair.Value;
            process = new Process { StartInfo = info };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Could not start Python.");
                timer.Start();
                var output = process.StandardOutput;
                var errors = process.StandardError;
                outputReader = Task.Run(() => Read(output, stdout));
                errorReader = Task.Run(() => Read(errors, stderr));
            }
            catch { process.Dispose(); process = null; throw; }
        }

        private void Read(TextReader reader, StringBuilder target)
        {
            var buffer = new char[2048];
            try
            {
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (gate)
                    {
                        if (disposed) return;
                        int remaining = MaxCharacters - stdout.Length - stderr.Length;
                        if (remaining < count) overflow = true;
                        if (remaining > 0) target.Append(buffer, 0, Math.Min(remaining, count));
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        internal ClientProcessResult Poll()
        {
            if (result != null) return result;
            if (process == null || disposed) return null;
            bool tooLarge;
            lock (gate) tooLarge = overflow;
            if (tooLarge) return Finish("output_limit", -1, true);
            if (timer.ElapsedMilliseconds >= timeoutMilliseconds) return Finish("timeout", -1, true);
            if (!process.HasExited || !outputReader.IsCompleted || !errorReader.IsCompleted) return null;
            return Finish(process.ExitCode == 0 ? "completed" : "failed", process.ExitCode, false);
        }

        internal ClientProcessResult Cancel() { return result ?? Finish("cancelled", -1, true); }

        private ClientProcessResult Finish(string state, int exitCode, bool kill)
        {
            if (kill) KillOwnedProcess();
            timer.Stop();
            lock (gate) result = new ClientProcessResult { state = state, exitCode = exitCode, stdout = stdout.ToString(), stderr = stderr.ToString() };
            return result;
        }

        private void KillOwnedProcess()
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            KillOwnedProcess();
            lock (gate) disposed = true;
            if (process != null) process.Dispose();
        }

        internal static string Arguments(IEnumerable<string> arguments)
        {
            var result = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (result.Length > 0) result.Append(' ');
                result.Append('"');
                int slashes = 0;
                foreach (char c in argument ?? "")
                {
                    if (c == '\\') { slashes++; continue; }
                    if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                    else result.Append('\\', slashes).Append(c);
                    slashes = 0;
                }
                result.Append('\\', slashes * 2).Append('"');
            }
            return result.ToString();
        }
    }
}
