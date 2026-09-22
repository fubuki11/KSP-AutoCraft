using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using KSPAutoCraft;

internal static class Program
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const int MaxBodyBytes = 256 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Run("constructor and lifecycle validation", TestConstruction);
        Run("authenticated GET preserves raw target", () =>
        {
            using (var f = new Fixture())
            {
                const string target = "/v1/%2fcraft?name=a+b&name=%E4%BD%A0&empty=";
                Reply reply = Exchange(f, Request(f, target: target));
                Equal(200, reply.Status);
                Equal(target, f.LastRequest.Path);
                Equal("GET", f.LastRequest.Method);
                Equal(string.Empty, f.LastRequest.Body);
                Equal(1, f.Calls);
            }
        });
        Reject("missing auth on unknown route", f => Head(f, target: "/unknown", authorization: null), 401, "unauthorized");
        Reject("wrong bearer token", f => Head(f, authorization: "Bearer " + new string('x', 32)), 401, "unauthorized");
        Reject("token comparison is case sensitive", f => Head(f, authorization: "Bearer " + Token.ToUpperInvariant()), 401, "unauthorized");
        Reject("wrong auth scheme", f => Head(f, authorization: "Basic " + Token), 401, "unauthorized");
        Reject("unauthenticated POST", f => Head(f, method: "POST", authorization: null), 401, "unauthorized");
        Reject("unauthenticated OPTIONS", f => Head(f, method: "OPTIONS", authorization: null), 401, "unauthorized");
        Reject("localhost Host rejected", f => Head(f, host: "localhost:" + f.Port), 400, "invalid_host");
        Reject("wrong Host port", f => Head(f, host: "127.0.0.1:" + (f.Port == 65535 ? 1 : f.Port + 1)), 400, "invalid_host");
        Reject("missing Host", f => Head(f, host: null), 400, "invalid_host");
        Reject("absolute-form target rejected", f => Request(f, target: "http://127.0.0.1:" + f.Port + "/status"), 400, "invalid_request");
        Reject("fragment target rejected", f => Request(f, target: "/status#fragment"), 400, "invalid_request");
        Reject("Origin rejected", f => Request(f, headers: "Origin: https://example.test\r\n"), 403, "origin_forbidden");
        Reject("empty Origin rejected", f => Request(f, headers: "oRiGiN:\r\n"), 403, "origin_forbidden");
        Reject("null Origin rejected", f => Request(f, headers: "Origin: null\r\n"), 403, "origin_forbidden");
        Reject("duplicate Host", f => Request(f, headers: "host: 127.0.0.1:" + f.Port + "\r\n"), 400, "duplicate_header");
        Reject("duplicate authorization", f => Request(f, headers: "Authorization: Bearer " + Token + "\r\n"), 400, "duplicate_header");
        Reject("folded header", f => Request(f, headers: "X-Test: yes\r\n folded\r\n"), 400, "invalid_headers");
        Reject("space before header colon", f => Request(f, headers: "X-Test : yes\r\n"), 400, "invalid_headers");
        Reject("bare LF in header", f => Request(f, headers: "X-Test: yes\nInjected: yes\r\n"), 400, "invalid_headers");
        Reject("non-ASCII header", f => Request(f, headers: "X-Test: \u00e9\r\n"), 400, "invalid_headers");
        Reject("HTTP/1.0 rejected", f => Request(f).Replace("HTTP/1.1", "HTTP/1.0"), 400, "invalid_request");

        foreach (string length in new[] { "-1", "+1", "1.0", "1, 1", "1 0", "", "abc", "9223372036854775808", "1\0" })
        {
            string value = length;
            Reject("invalid Content-Length " + JsonSerializer.Serialize(value), f => Request(f, headers: "Content-Length: " + value + "\r\n"),
                400, value.IndexOf('\0') >= 0 ? "invalid_headers" : "invalid_content_length");
        }
        Reject("identical duplicate lengths", f => Request(f, headers: "Content-Length: 0\r\ncontent-length: 0\r\n"), 400, "duplicate_header");
        Reject("conflicting duplicate lengths", f => Request(f, headers: "Content-Length: 0\r\nContent-Length: 1\r\n"), 400, "duplicate_header");
        Reject("oversized body rejected before reading", f => Request(f, method: "POST", headers: "Content-Type: application/json\r\nContent-Length: 262145\r\n"), 413, "body_too_large");
        Reject("very large body length", f => Request(f, headers: "Content-Length: 9223372036854775807\r\n"), 413, "body_too_large");
        Reject("oversized headers", f => PaddedHeaders(f, 8193), 431, "headers_too_large");
        Reject("Transfer-Encoding chunked", f => Request(f, headers: "Transfer-Encoding: chunked\r\n"), 400, "transfer_encoding_forbidden");
        Reject("Transfer-Encoding identity with length", f => Request(f, headers: "Transfer-Encoding: identity\r\nContent-Length: 0\r\n"), 400, "transfer_encoding_forbidden");
        Reject("empty Transfer-Encoding", f => Request(f, headers: "transfer-encoding:\r\n"), 400, "transfer_encoding_forbidden");
        Reject("POST missing length", f => Request(f, method: "POST", headers: "Content-Type: application/json\r\n"), 411, "length_required");
        Reject("POST missing content type", f => Request(f, method: "POST", headers: "Content-Length: 0\r\n"), 415, "unsupported_media_type");
        Reject("POST wrong content type", f => Request(f, method: "POST", headers: "Content-Length: 0\r\nContent-Type: text/plain\r\n"), 415, "unsupported_media_type");
        Reject("POST JSON prefix is not JSON", f => Request(f, method: "POST", headers: "Content-Length: 0\r\nContent-Type: application/jsonx\r\n"), 415, "unsupported_media_type");
        Reject("duplicate content type", f => Request(f, method: "POST", headers: "Content-Length: 0\r\nContent-Type: application/json\r\nContent-Type: text/plain\r\n"), 400, "duplicate_header");
        Reject("Expect rejected promptly", f => Request(f, method: "POST", headers: "Content-Length: 10\r\nContent-Type: application/json\r\nExpect: 100-continue\r\n"), 417, "expectation_failed");
        foreach (string method in new[] { "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "CONNECT", "get" })
        {
            string value = method;
            Reject("method " + value, f => Request(f, method: value), 405, "method_not_allowed");
        }

        Run("case-insensitive header names and auth scheme", () =>
        {
            using (var f = new Fixture())
            {
                string request = Request(f).Replace("Host:", "hOsT:").Replace("Authorization: Bearer", "aUtHoRiZaTiOn: bEaReR");
                Equal(200, Exchange(f, request).Status);
            }
        });
        Run("fragmented UTF-8 and byte Content-Length", TestFragmentedUtf8);
        Run("invalid UTF-8 rejected", TestInvalidUtf8);
        Run("truncated body rejected", TestTruncatedBody);
        Run("header boundary exactly 8 KiB", () =>
        {
            using (var f = new Fixture())
                Equal(200, Exchange(f, PaddedHeaders(f, 8192)).Status);
        });
        Run("body boundary exactly 256 KiB", () =>
        {
            using (var f = new Fixture())
            {
                string body = "\"" + new string('a', MaxBodyBytes - 2) + "\"";
                Equal(200, Exchange(f, Request(f, "POST", headers: "Content-Length: 262144\r\nContent-Type: application/json\r\n", body: body)).Status);
                Equal(body, f.LastRequest.Body);
            }
        });
        Run("pipelined requests do not dispatch twice", () =>
        {
            using (var f = new Fixture())
            {
                Equal(200, Exchange(f, Request(f) + Request(f)).Status);
                Equal(1, f.Calls);
            }
        });
        Run("dispatch exception is isolated", () =>
        {
            using (var f = new Fixture(r =>
            {
                if (r.Path == "/boom")
                    throw new InvalidOperationException("Private exception: " + Token);
                return new ApiResponse(200, "{}");
            }))
            {
                CheckError(Exchange(f, Request(f, target: "/boom")), 500, "internal_error");
                Equal(200, Exchange(f, Request(f)).Status);
            }
        });
        foreach (ApiResponse response in new[] { null, new ApiResponse(99, "{}"), new ApiResponse(600, "{}"),
            new ApiResponse(200, new string('a', MaxBodyBytes + 1)), new ApiResponse(200, new string('\u00e9', MaxBodyBytes)),
            new ApiResponse(200, "\ud800"), new ApiResponse(204, "{}") })
        {
            ApiResponse value = response;
            Run("invalid callback response " + (value == null ? "null" : value.StatusCode + "/" + value.Body.Length), () =>
            {
                using (var f = new Fixture(r => value))
                    CheckError(Exchange(f, Request(f)), 500, "invalid_response");
            });
        }
        Run("bodyless 204 response", () =>
        {
            using (var f = new Fixture(r => new ApiResponse(204, null)))
            {
                Reply reply = Exchange(f, Request(f));
                Equal(204, reply.Status);
                Equal(string.Empty, reply.Body);
            }
        });
        Run("four connections and bounded overload", TestOverload);
        Run("absolute slow-header deadline", () => TestSlowClient(false));
        Run("absolute slow-body deadline", () => TestSlowClient(true));
        Run("Dispose aborts clients and allows port reuse", TestDispose);
        Run("Dispose does not wait for callback", TestDisposeDuringDispatch);
        Run("listener is IPv4 only", TestIpv4Only);

        Console.WriteLine("Transport tests: " + passed + " passed, " + failed + " failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void TestConstruction()
    {
        Func<ApiRequest, ApiResponse> handler = r => new ApiResponse(200, "{}");
        Throws<ArgumentOutOfRangeException>(() => new LoopbackServer(0, Token, handler));
        Throws<ArgumentOutOfRangeException>(() => new LoopbackServer(65536, Token, handler));
        foreach (string value in new[] { null, "", new string('a', 31), new string(' ', 32), Token + "\r\n" })
            Throws<ArgumentException>(() => new LoopbackServer(12345, value, handler));
        Throws<ArgumentNullException>(() => new LoopbackServer(12345, Token, null));
        using (var server = new LoopbackServer(FreePort(), Token, handler))
        {
            server.Dispose();
            server.Dispose();
            Throws<ObjectDisposedException>(server.Start);
        }
        using (var f = new Fixture())
        {
            f.Server.Start();
            using (var conflicting = new LoopbackServer(f.Port, Token, handler))
            {
                Throws<SocketException>(conflicting.Start);
                f.Server.Dispose();
                conflicting.Start();
                Equal(200, Exchange(f, Request(f)).Status);
            }
        }
    }

    private static void TestFragmentedUtf8()
    {
        using (var f = new Fixture(r => new ApiResponse(200, r.Body)))
        using (TcpClient client = Connect(f.Port))
        {
            string body = "{\"name\":\"\u4f60\u597d\ud83d\ude80\"}";
            string head = Request(f, "POST", target: "/craft?raw=%2f+", headers: "Content-Length: " + Utf8.GetByteCount(body) + "\r\nContent-Type: Application/JSON; charset=utf-8\r\n");
            byte[] bytes = Utf8.GetBytes(head + body);
            foreach (byte value in bytes)
            {
                client.GetStream().WriteByte(value);
                Thread.Sleep(1);
            }
            Reply reply = ReadReply(client);
            Equal(200, reply.Status);
            Equal(body, reply.Body);
            Equal(body, f.LastRequest.Body);
            Equal("/craft?raw=%2f+", f.LastRequest.Path);
            Equal("POST", f.LastRequest.Method);
        }
    }

    private static void TestInvalidUtf8()
    {
        using (var f = new Fixture())
        using (TcpClient client = Connect(f.Port))
        {
            Send(client, Request(f, "POST", headers: "Content-Length: 2\r\nContent-Type: application/json\r\n"));
            client.GetStream().Write(new byte[] { 0xc3, 0x28 }, 0, 2);
            CheckError(ReadReply(client), 400, "invalid_utf8");
            Equal(0, f.Calls);
        }
    }

    private static void TestTruncatedBody()
    {
        using (var f = new Fixture())
        using (TcpClient client = Connect(f.Port))
        {
            Send(client, Request(f, "POST", headers: "Content-Length: 10\r\nContent-Type: application/json\r\n", body: "{}"));
            NetworkStream stream = client.GetStream();
            client.Client.Shutdown(SocketShutdown.Send);
            CheckError(ReadReply(stream), 400, "invalid_body");
            Equal(0, f.Calls);
        }
    }

    private static void TestOverload()
    {
        using (var entered = new CountdownEvent(4))
        using (var release = new ManualResetEventSlim(false))
        {
            int count = 0;
            using (var f = new Fixture(r =>
            {
                if (Interlocked.Increment(ref count) <= 4)
                {
                    entered.Signal();
                    if (!release.Wait(6000))
                        throw new TimeoutException();
                }
                return new ApiResponse(200, "{}");
            }))
            {
                var clients = new List<TcpClient>();
                try
                {
                    for (int i = 0; i < 4; i++)
                    {
                        TcpClient client = Connect(f.Port);
                        clients.Add(client);
                        Send(client, Request(f));
                    }
                    Check(entered.Wait(4000), "Four callbacks did not enter.");
                    Stopwatch watch = Stopwatch.StartNew();
                    CheckError(Exchange(f, Request(f)), 503, "server_busy");
                    Check(watch.ElapsedMilliseconds < 1500, "Overload rejection was not bounded.");
                    Equal(4, f.Calls);
                    release.Set();
                    foreach (TcpClient client in clients)
                        Equal(200, ReadReply(client).Status);
                    Thread.Sleep(50);
                    Equal(200, Exchange(f, Request(f)).Status);
                }
                finally
                {
                    release.Set();
                    foreach (TcpClient client in clients)
                        client.Close();
                }
            }
        }
    }

    private static void TestSlowClient(bool body)
    {
        using (var f = new Fixture())
        using (TcpClient client = Connect(f.Port))
        using (var stop = new ManualResetEventSlim(false))
        {
            Stopwatch watch = Stopwatch.StartNew();
            if (body)
            {
                string head = Request(f, "POST", headers: "Content-Length: 100\r\nContent-Type: application/json\r\n");
                Send(client, head.Substring(0, head.Length - 2));
                Thread.Sleep(2000);
                Send(client, "\r\n");
            }
            else
                Send(client, "GET /status HTTP/1.1\r\nHost: ");
            var sender = new Thread(() =>
            {
                try
                {
                    while (!stop.Wait(200))
                        client.GetStream().WriteByte((byte)'a');
                }
                catch (Exception) { }
            });
            sender.IsBackground = true;
            sender.Start();
            try
            {
                CheckError(ReadReply(client), 408, "request_timeout");
                Check(watch.ElapsedMilliseconds >= 3500 && watch.ElapsedMilliseconds < 6500, "Deadline reset by trickled bytes or the header/body transition, or fired too early.");
                Equal(0, f.Calls);
            }
            finally
            {
                stop.Set();
                sender.Join(1000);
            }
        }
    }

    private static void TestDispose()
    {
        using (var f = new Fixture())
        {
            var clients = new List<TcpClient>();
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    TcpClient client = Connect(f.Port);
                    clients.Add(client);
                    Send(client, "GET / HTTP/1.1\r\n");
                }
                Thread.Sleep(100);
                Stopwatch watch = Stopwatch.StartNew();
                f.Server.Dispose();
                Check(watch.ElapsedMilliseconds < 1500, "Dispose did not return promptly.");
                foreach (TcpClient client in clients)
                    AssertClosed(client);
                Equal(0, f.Calls);
                using (var replacement = new LoopbackServer(f.Port, Token, r => new ApiResponse(200, "{}")))
                {
                    replacement.Start();
                    Equal(200, Exchange(f, Request(f)).Status);
                }
            }
            finally
            {
                foreach (TcpClient client in clients)
                    client.Close();
            }
        }
    }

    private static void TestDisposeDuringDispatch()
    {
        using (var entered = new ManualResetEventSlim(false))
        using (var release = new ManualResetEventSlim(false))
        using (var finished = new ManualResetEventSlim(false))
        using (var f = new Fixture(r =>
        {
            entered.Set();
            release.Wait(6000);
            finished.Set();
            return new ApiResponse(200, "{}");
        }))
        using (TcpClient client = Connect(f.Port))
        {
            try
            {
                Send(client, Request(f));
                Check(entered.Wait(3000), "Callback did not enter.");
                Stopwatch watch = Stopwatch.StartNew();
                f.Server.Dispose();
                Check(watch.ElapsedMilliseconds < 1500, "Dispose waited for dispatch.");
                AssertClosed(client);
            }
            finally
            {
                release.Set();
                Check(finished.Wait(3000), "Callback did not finish.");
            }
        }
    }

    private static void TestIpv4Only()
    {
        using (var f = new Fixture())
        {
            Equal(200, Exchange(f, Request(f)).Status);
            if (Socket.OSSupportsIPv6)
            {
                using (var client = new TcpClient(AddressFamily.InterNetworkV6))
                    Throws<SocketException>(() => client.Connect(IPAddress.IPv6Loopback, f.Port));
            }
        }
    }

    private static void Reject(string name, Func<Fixture, string> request, int status, string code)
    {
        Run(name, () =>
        {
            using (var f = new Fixture())
            {
                CheckError(Exchange(f, request(f)), status, code);
                Equal(0, f.Calls);
            }
        });
    }

    private static void CheckError(Reply reply, int status, string code)
    {
        Equal(status, reply.Status);
        using (JsonDocument document = JsonDocument.Parse(reply.Body))
        {
            JsonElement error = document.RootElement.GetProperty("error");
            Equal(code, error.GetProperty("code").GetString());
            Check(!string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()), "Missing error message.");
        }
        Check(reply.Body.IndexOf(Token, StringComparison.Ordinal) < 0, "Token leaked in response.");
        if (status == 401)
            Equal("Bearer", reply.Headers["WWW-Authenticate"]);
        if (status == 405)
            Equal("GET, POST", reply.Headers["Allow"]);
    }

    private static string Request(Fixture f, string method = "GET", string target = "/status", string headers = "", string body = "")
    {
        return Head(f, method, target, "127.0.0.1:" + f.Port, "Bearer " + Token, headers) + body;
    }

    private static string Head(Fixture f, string method = "GET", string target = "/status", string host = "default", string authorization = "default", string headers = "")
    {
        if (host == "default")
            host = "127.0.0.1:" + f.Port;
        if (authorization == "default")
            authorization = "Bearer " + Token;
        return method + " " + target + " HTTP/1.1\r\n" +
            (host == null ? "" : "Host: " + host + "\r\n") +
            (authorization == null ? "" : "Authorization: " + authorization + "\r\n") + headers + "\r\n";
    }

    private static string PaddedHeaders(Fixture f, int bytes)
    {
        string empty = Request(f, headers: "X-Fill: \r\n");
        return Request(f, headers: "X-Fill: " + new string('a', bytes - Utf8.GetByteCount(empty)) + "\r\n");
    }

    private static Reply Exchange(Fixture f, string request)
    {
        using (TcpClient client = Connect(f.Port))
        {
            Send(client, request);
            return ReadReply(client);
        }
    }

    private static TcpClient Connect(int port)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        client.NoDelay = true;
        client.SendTimeout = 3000;
        client.ReceiveTimeout = 9000;
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return client;
        }
        catch
        {
            client.Close();
            throw;
        }
    }

    private static void Send(TcpClient client, string text)
    {
        byte[] bytes = Utf8.GetBytes(text);
        client.GetStream().Write(bytes, 0, bytes.Length);
    }

    private static Reply ReadReply(TcpClient client)
    {
        return ReadReply(client.GetStream());
    }

    private static Reply ReadReply(NetworkStream stream)
    {
        using (var result = new MemoryStream())
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count;
                try { count = stream.Read(buffer, 0, buffer.Length); }
                catch (IOException error)
                {
                    var socketError = error.InnerException as SocketException;
                    if (socketError != null && socketError.SocketErrorCode == SocketError.ConnectionReset)
                        break;
                    throw;
                }
                if (count == 0)
                    break;
                result.Write(buffer, 0, count);
                Check(result.Length <= MaxBodyBytes + 8192, "Unbounded response.");
            }
            byte[] bytes = result.ToArray();
            string text = Utf8.GetString(bytes);
            int separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Check(separator >= 0, "No complete HTTP response.");
            string[] lines = text.Substring(0, separator).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] status = lines[0].Split(' ');
            Equal("HTTP/1.1", status[0]);
            var reply = new Reply();
            reply.Status = int.Parse(status[1], CultureInfo.InvariantCulture);
            reply.Body = text.Substring(separator + 4);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                Check(colon > 0, "Malformed response header.");
                reply.Headers.Add(lines[i].Substring(0, colon), lines[i].Substring(colon + 1).Trim());
            }
            Equal("close", reply.Headers["Connection"]);
            Check(reply.Headers["Content-Type"].StartsWith("application/json", StringComparison.Ordinal), "Response is not JSON content type.");
            Check(!reply.Headers.ContainsKey("Transfer-Encoding"), "Chunked response.");
            foreach (string header in reply.Headers.Keys)
                Check(!header.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase), "CORS header present.");
            if (reply.Status == 204 || reply.Status == 304)
            {
                Check(!reply.Headers.ContainsKey("Content-Length"), "Unexpected Content-Length for bodyless status.");
                Equal(string.Empty, reply.Body);
            }
            else
                Equal(Utf8.GetByteCount(reply.Body), int.Parse(reply.Headers["Content-Length"], CultureInfo.InvariantCulture));
            return reply;
        }
    }

    private static void AssertClosed(TcpClient client)
    {
        client.ReceiveTimeout = 1500;
        try { Equal(-1, client.GetStream().ReadByte()); }
        catch (IOException error)
        {
            var socketError = error.InnerException as SocketException;
            Check(socketError != null && (socketError.SocketErrorCode == SocketError.ConnectionReset || socketError.SocketErrorCode == SocketError.ConnectionAborted), "Socket was not aborted promptly.");
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            failed++;
            Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException("Expected " + expected + ", got " + actual + ".");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private sealed class Reply
    {
        internal int Status;
        internal string Body;
        internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly int Port;
        internal readonly LoopbackServer Server;
        internal int Calls;
        internal ApiRequest LastRequest;

        internal Fixture(Func<ApiRequest, ApiResponse> handler = null)
        {
            Port = FreePort();
            Server = new LoopbackServer(Port, Token, request =>
            {
                LastRequest = request;
                Interlocked.Increment(ref Calls);
                return handler == null ? new ApiResponse(200, "{\"ok\":true}") : handler(request);
            });
            Server.Start();
        }

        public void Dispose()
        {
            Server.Dispose();
        }
    }
}
