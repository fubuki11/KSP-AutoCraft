using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace KSPAutoCraft
{
    public sealed class ApiRequest
    {
        public string Method;
        public string Path;
        public string Body;
    }

    public sealed class ApiResponse
    {
        public readonly int StatusCode;
        public readonly string Body;

        public ApiResponse(int statusCode, string body)
        {
            StatusCode = statusCode;
            Body = body ?? string.Empty;
        }
    }

    // The callback runs on a bounded worker, not the game thread, and must return in bounded time.
    public sealed class LoopbackServer : IDisposable
    {
        private const int MaxConnections = 4;
        private const int MaxHeaderBytes = 8 * 1024;
        private const int MaxBodyBytes = 256 * 1024;
        private const int RequestTimeoutMilliseconds = 5000;
        private const int WriteTimeoutMilliseconds = 2000;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly object gate = new object();
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly int port;
        private readonly string host;
        private readonly string token;
        private readonly Func<ApiRequest, ApiResponse> dispatch;
        private TcpListener listener;
        private Thread acceptThread;
        private TcpClient rejectingClient;
        private bool started;
        private bool disposed;

        public LoopbackServer(int port, string token, Func<ApiRequest, ApiResponse> dispatch)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (token == null || token.Length < 32)
                throw new ArgumentException("Token must contain at least 32 printable ASCII characters without spaces.", nameof(token));
            foreach (char c in token)
            {
                if (c < 33 || c > 126)
                    throw new ArgumentException("Token must contain at least 32 printable ASCII characters without spaces.", nameof(token));
            }
            if (dispatch == null)
                throw new ArgumentNullException(nameof(dispatch));

            this.port = port;
            this.host = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            this.token = token;
            this.dispatch = dispatch;
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(LoopbackServer));
                if (started)
                    return;

                var nextListener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    nextListener.ExclusiveAddressUse = true;
                    nextListener.Start(MaxConnections);
                    var nextThread = new Thread(() => AcceptLoop(nextListener));
                    nextThread.IsBackground = true;
                    nextThread.Name = "KSPAutoCraft loopback accept";
                    listener = nextListener;
                    acceptThread = nextThread;
                    started = true;
                    nextThread.Start();
                }
                catch
                {
                    nextListener.Stop();
                    listener = null;
                    acceptThread = null;
                    started = false;
                    throw;
                }
            }
        }

        public void Dispose()
        {
            TcpClient[] pending;
            TcpListener toStop;
            Thread toJoin;
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                pending = new TcpClient[clients.Count + (rejectingClient == null ? 0 : 1)];
                clients.CopyTo(pending);
                if (rejectingClient != null)
                    pending[pending.Length - 1] = rejectingClient;
                toStop = listener;
                toJoin = acceptThread;
            }

            if (toStop != null)
                toStop.Stop();
            foreach (TcpClient client in pending)
                Close(client);
            if (toJoin != null && toJoin != Thread.CurrentThread)
                toJoin.Join(1000);
            // In-flight callbacks cannot safely be terminated; their sockets are already closed.
        }

        private void AcceptLoop(TcpListener source)
        {
            while (true)
            {
                TcpClient client = null;
                bool handedOff = false;
                try
                {
                    client = source.AcceptTcpClient();
                    long deadline = Deadline(RequestTimeoutMilliseconds);
                    client.NoDelay = true;
                    var remote = client.Client.RemoteEndPoint as IPEndPoint;
                    if (remote == null || remote.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(remote.Address))
                        continue;

                    bool admitted;
                    lock (gate)
                    {
                        if (disposed)
                            return;
                        admitted = clients.Count < MaxConnections;
                        if (admitted)
                            clients.Add(client);
                        else
                            rejectingClient = client;
                    }

                    if (admitted)
                    {
                        // Reserve the slot before queueing: queued plus running work never exceeds four.
                        if (!ThreadPool.QueueUserWorkItem(HandleClient, new ClientWork(client, deadline)))
                            throw new InvalidOperationException("Unable to queue connection.");
                        handedOff = true;
                    }
                    else
                    {
                        WriteResponse(client.GetStream(), Error(503, "server_busy", "Too many connections."), Deadline(100));
                    }
                }
                catch (Exception)
                {
                    lock (gate)
                    {
                        if (disposed)
                            return;
                    }
                    Thread.Sleep(10);
                }
                finally
                {
                    if (client != null && !handedOff)
                    {
                        Close(client);
                        lock (gate)
                        {
                            clients.Remove(client);
                            if (rejectingClient == client)
                                rejectingClient = null;
                        }
                    }
                }
            }
        }

        private void HandleClient(object state)
        {
            var work = (ClientWork)state;
            try
            {
                NetworkStream stream = work.Client.GetStream();
                ApiRequest request = null;
                ApiResponse response;
                try
                {
                    request = ReadRequest(stream, work.Deadline);
                    response = null;
                }
                catch (RequestError error)
                {
                    response = error.Response;
                }
                catch (TimeoutException)
                {
                    response = Error(408, "request_timeout", "Request deadline exceeded.");
                }
                catch (IOException error)
                {
                    var socketError = error.InnerException as SocketException;
                    if (socketError == null || (socketError.SocketErrorCode != SocketError.TimedOut && socketError.SocketErrorCode != SocketError.WouldBlock))
                        return;
                    response = Error(408, "request_timeout", "Request deadline exceeded.");
                }

                if (request != null)
                {
                    lock (gate)
                    {
                        if (disposed)
                            return;
                    }
                    try
                    {
                        response = dispatch(request);
                    }
                    catch (Exception)
                    {
                        response = Error(500, "internal_error", "Request handler failed.");
                    }
                }

                WriteResponse(stream, response, Deadline(WriteTimeoutMilliseconds));
            }
            catch (Exception)
            {
                // Disconnects, shutdown races and failed writes must not escape a background worker.
            }
            finally
            {
                Close(work.Client);
                lock (gate)
                    clients.Remove(work.Client);
            }
        }

        private ApiRequest ReadRequest(NetworkStream stream, long deadline)
        {
            byte[] buffer = new byte[MaxHeaderBytes];
            int count = 0;
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                if (count == buffer.Length)
                    throw new RequestError(431, "headers_too_large", "Headers exceed 8192 bytes.");
                stream.ReadTimeout = RemainingMilliseconds(deadline);
                int received = stream.Read(buffer, count, buffer.Length - count);
                RemainingMilliseconds(deadline);
                if (received == 0)
                    throw new RequestError(400, "invalid_request", "Incomplete request headers.");
                int scanFrom = Math.Max(3, count);
                count += received;
                for (int i = scanFrom; i < count; i++)
                {
                    if (buffer[i - 3] == 13 && buffer[i - 2] == 10 && buffer[i - 1] == 13 && buffer[i] == 10)
                    {
                        headerEnd = i + 1;
                        break;
                    }
                }
            }

            for (int i = 0; i < headerEnd; i++)
            {
                byte c = buffer[i];
                if (c > 126 || (c < 32 && c != 9 && c != 10 && c != 13))
                    throw new RequestError(400, "invalid_headers", "Headers must contain valid ASCII.");
            }

            string[] lines = Encoding.ASCII.GetString(buffer, 0, headerEnd - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || !IsToken(first[0]) || first[1].Length == 0 || first[1][0] != '/' || first[2] != "HTTP/1.1")
                throw new RequestError(400, "invalid_request", "Expected an HTTP/1.1 origin-form request.");
            foreach (char c in first[1])
            {
                if (c < 33 || c > 126 || c == '#')
                    throw new RequestError(400, "invalid_request", "Invalid request target.");
            }

            string requestHost = null;
            string authorization = null;
            string contentLength = null;
            string contentType = null;
            bool hasOrigin = false;
            bool hasTransferEncoding = false;
            bool hasExpectation = false;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0 || !IsToken(lines[i].Substring(0, colon)))
                    throw new RequestError(400, "invalid_headers", "Invalid header name or folded header.");
                string name = lines[i].Substring(0, colon);
                string value = lines[i].Substring(colon + 1);
                foreach (char c in value)
                {
                    if (c < 32 && c != '\t')
                        throw new RequestError(400, "invalid_headers", "Invalid header value.");
                }
                value = value.Trim(' ', '\t');
                switch (name.ToLowerInvariant())
                {
                    case "host": SetSingleHeader(ref requestHost, value); break;
                    case "authorization": SetSingleHeader(ref authorization, value); break;
                    case "content-length": SetSingleHeader(ref contentLength, value); break;
                    case "content-type": SetSingleHeader(ref contentType, value); break;
                    case "origin": hasOrigin = true; break;
                    case "transfer-encoding": hasTransferEncoding = true; break;
                    case "expect": hasExpectation = true; break;
                }
            }

            if (!string.Equals(requestHost, host, StringComparison.Ordinal))
                throw new RequestError(400, "invalid_host", "Host must match the configured IPv4 loopback endpoint.");
            if (hasOrigin)
                throw new RequestError(403, "origin_forbidden", "Browser Origin requests are not allowed.");
            if (!IsAuthorized(authorization))
                throw new RequestError(401, "unauthorized", "A valid bearer token is required.");
            if (hasTransferEncoding)
                throw new RequestError(400, "transfer_encoding_forbidden", "Transfer-Encoding is not supported.");
            if (first[0] != "GET" && first[0] != "POST")
                throw new RequestError(405, "method_not_allowed", "Only GET and POST are supported.");
            if (hasExpectation)
                throw new RequestError(417, "expectation_failed", "Expect is not supported.");

            long length = 0;
            if (contentLength != null && !long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out length))
                throw new RequestError(400, "invalid_content_length", "Content-Length must be a nonnegative decimal integer.");
            if (length > MaxBodyBytes)
                throw new RequestError(413, "body_too_large", "Body exceeds 262144 bytes.");
            if (first[0] == "POST")
            {
                if (contentLength == null)
                    throw new RequestError(411, "length_required", "POST requires Content-Length.");
                if (contentType == null || !string.Equals(contentType.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                    throw new RequestError(415, "unsupported_media_type", "POST requires application/json encoded as UTF-8.");
            }

            byte[] body = new byte[(int)length];
            int copied = Math.Min(count - headerEnd, body.Length);
            Buffer.BlockCopy(buffer, headerEnd, body, 0, copied);
            while (copied < body.Length)
            {
                stream.ReadTimeout = RemainingMilliseconds(deadline);
                int received = stream.Read(body, copied, body.Length - copied);
                RemainingMilliseconds(deadline);
                if (received == 0)
                    throw new RequestError(400, "invalid_body", "Incomplete request body.");
                copied += received;
            }

            string decoded;
            try
            {
                decoded = Utf8.GetString(body);
            }
            catch (DecoderFallbackException)
            {
                throw new RequestError(400, "invalid_utf8", "Body must be valid UTF-8.");
            }
            RemainingMilliseconds(deadline);
            return new ApiRequest { Method = first[0], Path = first[1], Body = decoded };
        }

        private bool IsAuthorized(string authorization)
        {
            if (authorization == null || authorization.Length != token.Length + 7 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return false;
            int difference = 0;
            for (int i = 0; i < token.Length; i++)
                difference |= authorization[i + 7] ^ token[i];
            return difference == 0;
        }

        private static bool IsToken(string value)
        {
            if (value.Length == 0)
                return false;
            foreach (char c in value)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0))
                    return false;
            }
            return true;
        }

        private static void SetSingleHeader(ref string destination, string value)
        {
            if (destination != null)
                throw new RequestError(400, "duplicate_header", "Duplicate framing or security header.");
            destination = value;
        }

        private static void WriteResponse(NetworkStream stream, ApiResponse response, long deadline)
        {
            bool valid = response != null && response.StatusCode >= 200 && response.StatusCode <= 599 && response.Body.Length <= MaxBodyBytes;
            if (valid)
            {
                try { valid = Utf8.GetByteCount(response.Body) <= MaxBodyBytes; }
                catch (EncoderFallbackException) { valid = false; }
                // These status codes forbid a message body.
                if ((response.StatusCode == 204 || response.StatusCode == 205 || response.StatusCode == 304) && response.Body.Length != 0)
                    valid = false;
            }
            if (!valid)
                response = Error(500, "invalid_response", "Request handler returned an invalid response.");

            byte[] body = Utf8.GetBytes(response.Body);
            string headers = "HTTP/1.1 " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + " " + Reason(response.StatusCode) + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\nConnection: close\r\n";
            if (response.StatusCode != 204 && response.StatusCode != 304)
                headers += "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n";
            if (response.StatusCode == 401)
                headers += "WWW-Authenticate: Bearer\r\n";
            if (response.StatusCode == 405)
                headers += "Allow: GET, POST\r\n";
            headers += "\r\n";
            WriteBytes(stream, Encoding.ASCII.GetBytes(headers), deadline);
            WriteBytes(stream, body, deadline);
        }

        private static void WriteBytes(NetworkStream stream, byte[] bytes, long deadline)
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                stream.WriteTimeout = RemainingMilliseconds(deadline);
                int count = Math.Min(4096, bytes.Length - offset);
                stream.Write(bytes, offset, count);
                offset += count;
            }
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 415: return "Unsupported Media Type";
                case 417: return "Expectation Failed";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Response";
            }
        }

        private static ApiResponse Error(int status, string code, string message)
        {
            // Only fixed, internal strings reach this helper; no request data or exception details.
            return new ApiResponse(status, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}");
        }

        private static long Deadline(int milliseconds)
        {
            return Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000;
        }

        private static int RemainingMilliseconds(long deadline)
        {
            long ticks = deadline - Stopwatch.GetTimestamp();
            if (ticks <= 0)
                throw new TimeoutException();
            return (int)Math.Max(1, (ticks * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
        }

        private static void Close(TcpClient client)
        {
            try { client.Close(); }
            catch (Exception) { }
        }

        private sealed class ClientWork
        {
            internal readonly TcpClient Client;
            internal readonly long Deadline;

            internal ClientWork(TcpClient client, long deadline)
            {
                Client = client;
                Deadline = deadline;
            }
        }

        private sealed class RequestError : Exception
        {
            internal readonly ApiResponse Response;

            internal RequestError(int status, string code, string message)
            {
                Response = Error(status, code, message);
            }
        }
    }
}
