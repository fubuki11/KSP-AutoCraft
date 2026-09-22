using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace KSPAutoCraft
{
    // HTTP workers never access Unity. Expired pending work is never executed later.
    internal sealed class MainThreadQueue : IDisposable
    {
        private sealed class Work
        {
            internal ApiRequest Request;
            internal ApiResponse Response;
            internal bool Started;
            internal bool Cancelled;
            internal long Deadline;
        }
        private readonly object gate = new object();
        private readonly Queue<Work> pending = new Queue<Work>();
        private bool stopped;
        private static ApiResponse Error(string code)
        {
            return new ApiResponse(503, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"Editor unavailable or busy. Do not automatically retry builds.\"}}");
        }

        internal ApiResponse Send(ApiRequest request)
        {
            var work = new Work { Request = request, Deadline = Stopwatch.GetTimestamp() + 4 * Stopwatch.Frequency };
            var timer = Stopwatch.StartNew();
            lock (gate)
            {
                if (stopped) return Error("editor_closed");
                if (pending.Count >= 8) return Error("queue_full");
                pending.Enqueue(work);
                while (work.Response == null && !stopped)
                {
                    int remaining = 4000 - (int)timer.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        work.Cancelled = true;
                        return Error(work.Started ? "outcome_unknown" : "queue_expired");
                    }
                    Monitor.Wait(gate, remaining);
                }
                return work.Response ?? Error("editor_closed");
            }
        }

        internal void Pump(Func<ApiRequest, ApiResponse> handler)
        {
            Work work = null;
            lock (gate)
            {
                if (stopped) return;
                while (pending.Count > 0)
                {
                    var next = pending.Dequeue();
                    if (next.Cancelled) continue;
                    if (Stopwatch.GetTimestamp() >= next.Deadline)
                    {
                        next.Cancelled = true;
                        next.Response = Error("queue_expired");
                        Monitor.PulseAll(gate);
                        continue;
                    }
                    work = next;
                    work.Started = true;
                    break;
                }
            }
            if (work == null) return;
            ApiResponse response;
            try { response = handler(work.Request); }
            catch { response = Error("handler_failed"); }
            lock (gate)
            {
                work.Response = response;
                Monitor.PulseAll(gate);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                stopped = true;
                pending.Clear();
                Monitor.PulseAll(gate);
            }
        }
    }
}
