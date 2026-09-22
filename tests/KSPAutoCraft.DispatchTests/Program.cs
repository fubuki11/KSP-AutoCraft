using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using KSPAutoCraft;

internal static class Program
{
    private static int mainThreadId;
    private static int passed;
    private static int failed;
    private static string currentTest;

    private static int Main()
    {
        mainThreadId = Environment.CurrentManagedThreadId;
        var elapsed = Stopwatch.StartNew();
        using var watchdog = new Timer(_ =>
        {
            Console.Error.WriteLine("FAIL suite exceeded 15 seconds during: " + Volatile.Read(ref currentTest));
            Environment.Exit(1);
        }, null, 15000, Timeout.Infinite);

        Run("empty Pump does not dispatch", TestEmpty);
        Run("Send waits for Pump and handler response on the main thread", TestSendWaits);
        Run("FIFO order and exactly one request per Pump", TestOrder);
        Run("eight pending requests bound the queue; rejected work never dispatches", TestFull);
        Run("Dispose cancels pending waiters, future sends and future pumps", TestDispose);
        Run("Dispose releases an in-progress waiter", TestDisposeInProgress);
        Run("handler exception returns handler_failed and the queue recovers", TestHandlerException);
        Run("four-second pending expiry prevents latent writes in future pumps", TestPendingExpiry);
        Run("in-progress timeout returns outcome_unknown without retry dispatch", TestInProgressTimeout);

        Console.WriteLine("Dispatch tests: {0} passed, {1} failed ({2:F2}s).", passed, failed, elapsed.Elapsed.TotalSeconds);
        Console.WriteLine("Real 4s timeouts; Pump runs on this process's main thread. No Unity or HTTP integration is exercised.");
        return failed == 0 ? 0 : 1;
    }

    private static void TestEmpty()
    {
        using var f = new Fixture();
        int calls = 0;
        f.Queue.Pump(_ => { calls++; return new ApiResponse(200, "{}"); });
        Check(calls == 0, "An empty Pump invoked the handler.");
    }

    private static void TestSendWaits()
    {
        using var f = new Fixture();
        Sender sender = f.Send("/build", 1);
        Check(!sender.Thread.Join(75), "Send returned before Pump ran.");
        var expected = new ApiResponse(201, "{\"built\":true}");
        ApiRequest seen = null;
        int handlerThread = 0;
        bool returnedInsideHandler = false;
        int writes = 0;
        f.Queue.Pump(request =>
        {
            seen = request;
            handlerThread = Environment.CurrentManagedThreadId;
            returnedInsideHandler = sender.Thread.Join(75);
            writes++;
            return expected;
        });

        Check(ReferenceEquals(expected, sender.Finish()), "Send did not receive the exact handler response.");
        Check(ReferenceEquals(seen, sender.Request), "Pump changed the request.");
        Check(handlerThread == mainThreadId && handlerThread != sender.Thread.ManagedThreadId,
            "Work did not run exclusively on the main/Pump thread.");
        Check(!returnedInsideHandler, "Send returned before the handler produced its response.");
        Check(writes == 1, "The handler did not write exactly once.");
        f.Queue.Pump(_ => { writes++; return expected; });
        Check(writes == 1, "Completed work dispatched again.");
    }

    private static void TestOrder()
    {
        using var f = new Fixture();
        var senders = new Sender[3];
        for (int i = 0; i < senders.Length; i++)
            senders[i] = f.Send("/ordered/" + i, i + 1);

        var seen = new List<ApiRequest>();
        for (int i = 0; i < senders.Length; i++)
        {
            var expected = new ApiResponse(200, "{\"order\":" + i + "}");
            int handlerThread = 0;
            f.Queue.Pump(request =>
            {
                handlerThread = Environment.CurrentManagedThreadId;
                seen.Add(request);
                return expected;
            });
            Check(seen.Count == i + 1, "Pump must dispatch exactly one live request.");
            Check(ReferenceEquals(seen[i], senders[i].Request), "FIFO order changed at request " + i + ".");
            Check(handlerThread == mainThreadId, "Ordered work left the main thread.");
            Check(ReferenceEquals(expected, senders[i].Finish()), "Response delivered to the wrong sender.");
            Check(f.PendingCount == senders.Length - i - 1, "Pump removed more than one live request.");
            for (int j = i + 1; j < senders.Length; j++)
                Check(senders[j].Thread.IsAlive, "An unpumped sender returned early.");
        }
        f.Queue.Pump(request => { seen.Add(request); return new ApiResponse(200, "{}"); });
        Check(seen.Count == senders.Length, "An extra Pump repeated completed work.");
    }

    private static void TestFull()
    {
        using var f = new Fixture();
        var accepted = new List<Sender>();
        for (int i = 0; i < 8; i++)
            accepted.Add(f.Send("/capacity/" + i, i + 1));
        Sender rejected = f.Send("/capacity/rejected");
        CheckError(rejected.Finish(), "queue_full");
        Check(f.PendingCount == 8, "The full queue accepted a ninth pending request.");
        foreach (Sender sender in accepted)
            Check(sender.Thread.IsAlive, "A request within capacity was rejected.");

        var seen = new List<ApiRequest>();
        var expected = new ApiResponse(200, "{}");
        Func<ApiRequest, ApiResponse> handler = request => { seen.Add(request); return expected; };
        f.Queue.Pump(handler);
        Check(ReferenceEquals(expected, accepted[0].Finish()), "The first queued request failed.");
        accepted.Add(f.Send("/capacity/replacement", 8));
        for (int i = 1; i < accepted.Count; i++)
        {
            f.Queue.Pump(handler);
            Check(ReferenceEquals(expected, accepted[i].Finish()), "A freed slot was not reusable.");
        }
        f.Queue.Pump(handler);
        Check(seen.Count == accepted.Count, "Unexpected dispatch count after draining a full queue.");
        for (int i = 0; i < accepted.Count; i++)
            Check(ReferenceEquals(seen[i], accepted[i].Request), "Full/rejected work corrupted FIFO order.");
        Check(!seen.Contains(rejected.Request), "Rejected work produced a latent dispatch.");
    }

    private static void TestDispose()
    {
        using var f = new Fixture();
        var waiting = new Sender[3];
        for (int i = 0; i < waiting.Length; i++)
            waiting[i] = f.Send("/closing/" + i, i + 1);
        f.Queue.Dispose();
        foreach (Sender sender in waiting)
            CheckError(sender.Finish(), "editor_closed");
        Check(f.PendingCount == 0, "Dispose did not clear pending work.");
        f.Queue.Dispose();
        CheckError(f.Send("/after-close").Finish(), "editor_closed");
        int writes = 0;
        for (int i = 0; i < 3; i++)
            f.Queue.Pump(_ => { writes++; return new ApiResponse(200, "{}"); });
        Check(writes == 0, "Disposed work produced a latent write.");
        Check(f.PendingCount == 0, "A future Send enqueued work after Dispose.");
    }

    private static void TestDisposeInProgress()
    {
        using var f = new Fixture();
        Sender sender = f.Send("/closing-in-progress", 1);
        bool waiterReleased = false;
        int calls = 0;
        f.Queue.Pump(_ =>
        {
            calls++;
            f.Queue.Dispose();
            waiterReleased = sender.Thread.Join(1000);
            return new ApiResponse(201, "{}");
        });
        Check(waiterReleased, "Dispose did not release the sender while its handler was still running.");
        CheckError(sender.Finish(), "editor_closed");
        f.Queue.Pump(_ => { calls++; return new ApiResponse(200, "{}"); });
        Check(calls == 1, "Disposed in-progress work dispatched again.");
    }

    private static void TestHandlerException()
    {
        using var f = new Fixture();
        Sender broken = f.Send("/throw", 1);
        Sender healthy = f.Send("/after-throw", 2);
        int calls = 0;
        int handlerThread = 0;
        f.Queue.Pump(_ =>
        {
            handlerThread = Environment.CurrentManagedThreadId;
            calls++;
            throw new InvalidOperationException("private handler detail");
        });
        ApiResponse response = broken.Finish();
        CheckError(response, "handler_failed");
        Check(!response.Body.Contains("private handler detail"), "The exception leaked into the response.");
        Check(handlerThread == mainThreadId && calls == 1, "Throwing handler dispatched on the wrong thread or twice.");
        Check(f.PendingCount == 1 && healthy.Thread.IsAlive, "A failed handler consumed the next request.");
        var expected = new ApiResponse(200, "{\"recovered\":true}");
        f.Queue.Pump(_ => expected);
        Check(ReferenceEquals(expected, healthy.Finish()), "Handler failure prevented the next response.");
    }

    private static void TestPendingExpiry()
    {
        using var f = new Fixture();
        Sender expired = f.Send("/expired/0", 1);
        Sender alsoExpired = f.Send("/expired/1", 2);
        CheckError(expired.Finish(5000), "queue_expired");
        CheckError(alsoExpired.Finish(), "queue_expired");
        CheckFourSecondTimeout(expired);
        CheckFourSecondTimeout(alsoExpired);

        Sender fresh = f.Send("/fresh", 3);
        int staleWrites = 0;
        int freshWrites = 0;
        var expected = new ApiResponse(201, "{\"fresh\":true}");
        Func<ApiRequest, ApiResponse> handler = request =>
        {
            if (ReferenceEquals(request, fresh.Request)) freshWrites++;
            else staleWrites++;
            return expected;
        };
        f.Queue.Pump(handler);
        Check(ReferenceEquals(expected, fresh.Finish()), "Cancelled entries prevented fresh work in the same Pump.");
        for (int i = 0; i < 3; i++)
            f.Queue.Pump(handler);
        Check(staleWrites == 0, "Expired pending work produced a latent write.");
        Check(freshWrites == 1 && f.PendingCount == 0, "Fresh work was lost, duplicated or left pending.");
    }

    private static void TestInProgressTimeout()
    {
        using var f = new Fixture();
        Sender sender = f.Send("/slow-build", 1);
        var success = new ApiResponse(201, "{\"built\":true}");
        bool timedOutInsideHandler = false;
        int calls = 0;
        int writes = 0;
        int handlerThread = 0;
        Func<ApiRequest, ApiResponse> handler = _ =>
        {
            calls++;
            handlerThread = Environment.CurrentManagedThreadId;
            // Keep Pump on the main thread and let the real Send deadline elapse mid-handler.
            timedOutInsideHandler = sender.Thread.Join(5000);
            writes++;
            return success;
        };
        f.Queue.Pump(handler);
        Check(timedOutInsideHandler, "Send did not time out while its handler remained in progress.");
        ApiResponse response = sender.Finish();
        CheckError(response, "outcome_unknown");
        CheckFourSecondTimeout(sender);
        Check(handlerThread == mainThreadId, "Slow work left the main/Pump thread.");
        Check(writes == 1, "The already-started handler did not finish its single write.");
        for (int i = 0; i < 3; i++)
            f.Queue.Pump(handler);
        Check(calls == 1 && writes == 1 && f.PendingCount == 0, "Timed-out in-progress work was retried.");
        Check(ReferenceEquals(response, sender.Response), "Late success replaced the caller's outcome_unknown response.");

        Sender fresh = f.Send("/after-slow-build", 1);
        ApiRequest seen = null;
        f.Queue.Pump(request => { seen = request; return success; });
        Check(ReferenceEquals(success, fresh.Finish()) && ReferenceEquals(seen, fresh.Request),
            "A timeout retry displaced fresh work or left the queue unusable.");
    }

    private static void CheckFourSecondTimeout(Sender sender)
    {
        Check(sender.ElapsedMilliseconds >= 3950 && sender.ElapsedMilliseconds < 5000,
            "Expected the real 4000ms timeout (50ms early/1000ms late tolerance); observed " +
            sender.ElapsedMilliseconds + "ms for " + sender.Request.Path + ".");
    }

    private static void CheckError(ApiResponse response, string code)
    {
        Check(response != null && response.StatusCode == 503, "Expected HTTP 503 for " + code + ".");
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement error = json.RootElement.GetProperty("error");
        string actual = error.GetProperty("code").GetString();
        Check(actual == code, "Expected " + code + ", got " + actual + ".");
        Check(error.GetProperty("message").GetString().Contains("Do not automatically retry builds."),
            "Error response lost its no-automatic-retry warning.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Run(string name, Action test)
    {
        Volatile.Write(ref currentTest, name);
        var elapsed = Stopwatch.StartNew();
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS {0} ({1:F3}s)", name, elapsed.Elapsed.TotalSeconds);
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine("FAIL {0}: {1}", name, error);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly MainThreadQueue Queue = new MainThreadQueue();
        private readonly List<Sender> senders = new List<Sender>();
        private readonly object gate;
        private readonly ICollection pending;

        internal Fixture()
        {
            // Read-only inspection under the production lock establishes admission/FIFO without sleeps or timeout hacks.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            gate = typeof(MainThreadQueue).GetField("gate", flags).GetValue(Queue);
            pending = (ICollection)typeof(MainThreadQueue).GetField("pending", flags).GetValue(Queue);
        }

        internal int PendingCount
        {
            get { lock (gate) return pending.Count; }
        }

        internal Sender Send(string path, int expectedPending = -1)
        {
            var sender = new Sender(Queue, path);
            senders.Add(sender);
            sender.Thread.Start();
            if (expectedPending >= 0)
                Check(SpinWait.SpinUntil(() => PendingCount == expectedPending, 1000),
                    "Request " + path + " was not admitted; expected " + expectedPending + " pending, got " + PendingCount + ".");
            return sender;
        }

        public void Dispose()
        {
            Queue.Dispose();
            var stuck = new List<string>();
            foreach (Sender sender in senders)
                if (!sender.Thread.Join(1000)) stuck.Add(sender.Request.Path);
            Check(stuck.Count == 0, "Sender threads survived cleanup: " + string.Join(", ", stuck));
        }
    }

    private sealed class Sender
    {
        internal readonly ApiRequest Request;
        internal readonly Thread Thread;
        internal ApiResponse Response;
        internal long ElapsedMilliseconds;
        private Exception failure;

        internal Sender(MainThreadQueue queue, string path)
        {
            Request = new ApiRequest { Method = "POST", Path = path, Body = "{\"write\":true}" };
            Thread = new Thread(() =>
            {
                var elapsed = Stopwatch.StartNew();
                try { Response = queue.Send(Request); }
                catch (Exception error) { failure = error; }
                finally { ElapsedMilliseconds = elapsed.ElapsedMilliseconds; }
            }) { IsBackground = true, Name = "Dispatch test " + path };
        }

        internal ApiResponse Finish(int timeoutMilliseconds = 1000)
        {
            Check(Thread.Join(timeoutMilliseconds), "Send did not finish within " + timeoutMilliseconds + "ms: " + Request.Path);
            if (failure != null) throw new InvalidOperationException("Send threw for " + Request.Path, failure);
            Check(Response != null, "Send returned null for " + Request.Path);
            return Response;
        }
    }
}
