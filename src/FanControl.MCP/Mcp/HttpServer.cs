using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FanControl.MCP.Mcp;

/// <summary>
/// Minimal HTTP/1.1 server implementing the MCP "Streamable HTTP" transport (POST-only, JSON responses).
/// Built on <see cref="TcpListener"/> instead of HttpListener/Kestrel so the plugin needs neither
/// URL ACLs nor the ASP.NET Core shared framework, which FanControl does not load.
/// </summary>
internal sealed class HttpServer : IDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly string _authToken;
    private readonly McpProtocol _protocol;
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public HttpServer(string bindAddress, int port, string authToken, McpProtocol protocol, Action<string> log)
    {
        _address = bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(bindAddress);
        _port = port;
        _authToken = authToken ?? "";
        _protocol = protocol;
        _log = log;
    }

    public string Endpoint => $"http://{(_address.Equals(IPAddress.Any) ? "127.0.0.1" : _address.ToString())}:{BoundPort}/mcp";

    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(_address, _port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoop(_listener, _cts.Token));
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _listener = null;
        _cts = null;
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) { _log($"Accept failed: {e.Message}"); continue; }

            _ = Task.Run(() => HandleConnection(client, token), token);
        }
    }

    private async Task HandleConnection(TcpClient client, CancellationToken serverToken)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        var reader = new BufferedReader(stream);
        try
        {
            while (!serverToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                idle.CancelAfter(IdleTimeout);
                var request = await HttpRequest.ReadAsync(reader, idle.Token).ConfigureAwait(false);
                if (request is null) return; // connection closed

                var response = Handle(request, stream);
                if (response is null) return; // answered as an SSE stream, which ends with the connection
                await response.WriteAsync(stream, serverToken).ConfigureAwait(false);
                if (!request.KeepAlive || response.Close) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (HttpBadRequest e)
        {
            try { await HttpResponse.Text(400, e.Message, close: true).WriteAsync(stream, serverToken).ConfigureAwait(false); } catch { }
        }
        catch (Exception e)
        {
            _log($"HTTP connection error: {e}");
        }
    }

    private HttpResponse? Handle(HttpRequest request, Stream stream)
    {
        // DNS rebinding protection required by the MCP spec: browsers always send Origin, local clients usually don't.
        if (request.Headers.TryGetValue("origin", out var origin) && !IsLocalOrigin(origin))
            return HttpResponse.Text(403, "Forbidden origin");

        if (_authToken.Length > 0)
        {
            request.Headers.TryGetValue("authorization", out var auth);
            if (!FixedTimeEquals(auth ?? "", "Bearer " + _authToken))
                return HttpResponse.Text(401, "Unauthorized", extraHeaders: [("WWW-Authenticate", "Bearer")]);
        }

        var path = request.Path.Split('?')[0].TrimEnd('/');
        if (path is "" && request.Method == "GET")
            return HttpResponse.Text(200, $"FanControl MCP server. Endpoint: POST {Endpoint}");
        if (path != "/mcp")
            return HttpResponse.Text(404, "Not found. The MCP endpoint is /mcp");

        switch (request.Method)
        {
            case "POST":
                break;
            case "GET":
                // Server-to-client messages only travel on the response stream of a POST, so no standalone SSE stream.
                return HttpResponse.Text(405, "SSE stream not supported", extraHeaders: [("Allow", "POST")]);
            case "DELETE":
                if (request.Headers.TryGetValue("mcp-session-id", out var ended)) _protocol.EndSession(ended);
                return new HttpResponse(200, "text/plain", []);
            case "OPTIONS":
                return new HttpResponse(204, "text/plain", [], [("Allow", "POST, DELETE")]);
            default:
                return HttpResponse.Text(405, "Method not allowed", extraHeaders: [("Allow", "POST")]);
        }

        var body = Encoding.UTF8.GetString(request.Body);
        request.Headers.TryGetValue("mcp-session-id", out var sessionId);
        var isInitialize = body.Contains("\"initialize\"", StringComparison.Ordinal);
        if (sessionId is not null && !isInitialize && !_protocol.KnowsSession(sessionId))
            return HttpResponse.Text(404, "Unknown or expired MCP session; please re-initialize."); // e.g. after a FanControl restart

        // Server-to-client requests (elicitation) switch this response to an SSE stream.
        var sse = new SseWriter(stream);
        var context = new McpCallContext { Session = _protocol.GetSession(sessionId), SendToClient = sse.Send };
        var reply = _protocol.HandleRaw(body, context, out var protocolVersion, out var newSessionId);

        if (sse.Started)
        {
            if (reply is not null) sse.Send(reply);
            return null;
        }
        var headers = new List<(string, string)>();
        if (protocolVersion is not null) headers.Add(("MCP-Protocol-Version", protocolVersion));
        if (newSessionId is not null) headers.Add(("MCP-Session-Id", newSessionId));
        if (reply is null)
            return new HttpResponse(202, "text/plain", [], headers);
        return new HttpResponse(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(reply), headers);
    }

    /// <summary>Lazily turns the current response into "text/event-stream" (closed with the connection).</summary>
    private sealed class SseWriter(Stream stream)
    {
        private readonly object _lock = new();
        public bool Started { get; private set; }

        public void Send(string json)
        {
            lock (_lock)
            {
                if (!Started)
                {
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nX-Accel-Buffering: no\r\nConnection: close\r\n\r\n";
                    stream.Write(Encoding.ASCII.GetBytes(head));
                    Started = true;
                }
                stream.Write(Encoding.UTF8.GetBytes($"event: message\ndata: {json}\n\n"));
                stream.Flush();
            }
        }
    }

    private static bool IsLocalOrigin(string origin)
    {
        if (origin == "null") return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
    }

    // ---------------------------------------------------------------- HTTP plumbing

    private sealed class HttpBadRequest(string message) : Exception(message);

    private sealed class BufferedReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _pos;
        private int _len;

        private async ValueTask<bool> FillAsync(CancellationToken token)
        {
            _pos = 0;
            _len = await stream.ReadAsync(_buffer, token).ConfigureAwait(false);
            return _len > 0;
        }

        /// <summary>Reads a CRLF (or LF) terminated line; returns null at end of stream.</summary>
        public async ValueTask<string?> ReadLineAsync(int maxBytes, CancellationToken token)
        {
            var line = new List<byte>(128);
            while (true)
            {
                if (_pos >= _len && !await FillAsync(token).ConfigureAwait(false))
                    return line.Count == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
                var b = _buffer[_pos++];
                if (b == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
                    return Encoding.Latin1.GetString(line.ToArray());
                }
                line.Add(b);
                if (line.Count > maxBytes) throw new HttpBadRequest("Header line too long");
            }
        }

        public async ValueTask ReadExactAsync(byte[] target, int offset, int count, CancellationToken token)
        {
            while (count > 0)
            {
                if (_pos >= _len && !await FillAsync(token).ConfigureAwait(false))
                    throw new IOException("Unexpected end of stream");
                var n = Math.Min(count, _len - _pos);
                Buffer.BlockCopy(_buffer, _pos, target, offset, n);
                _pos += n; offset += n; count -= n;
            }
        }
    }

    private sealed class HttpRequest
    {
        public required string Method { get; init; }
        public required string Path { get; init; }
        public required Dictionary<string, string> Headers { get; init; }
        public required byte[] Body { get; init; }
        public bool KeepAlive { get; init; }

        public static async Task<HttpRequest?> ReadAsync(BufferedReader reader, CancellationToken token)
        {
            string? requestLine;
            do
            {
                requestLine = await reader.ReadLineAsync(MaxHeaderBytes, token).ConfigureAwait(false);
                if (requestLine is null) return null;
            } while (requestLine.Length == 0); // tolerate stray CRLF between requests

            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                throw new HttpBadRequest("Malformed request line");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var headerBytes = 0;
            while (true)
            {
                var line = await reader.ReadLineAsync(MaxHeaderBytes, token).ConfigureAwait(false)
                           ?? throw new IOException("Connection closed in headers");
                if (line.Length == 0) break;
                headerBytes += line.Length;
                if (headerBytes > MaxHeaderBytes) throw new HttpBadRequest("Headers too large");
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new HttpBadRequest("Malformed header");
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
            }

            byte[] body;
            if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                body = await ReadChunkedAsync(reader, token).ConfigureAwait(false);
            }
            else if (headers.TryGetValue("content-length", out var cl))
            {
                if (!int.TryParse(cl, out var length) || length < 0) throw new HttpBadRequest("Invalid Content-Length");
                if (length > MaxBodyBytes) throw new HttpBadRequest("Body too large");
                body = new byte[length];
                await reader.ReadExactAsync(body, 0, length, token).ConfigureAwait(false);
            }
            else
            {
                body = [];
            }

            headers.TryGetValue("connection", out var connection);
            var keepAlive = parts[2] == "HTTP/1.1"
                ? !string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase)
                : string.Equals(connection, "keep-alive", StringComparison.OrdinalIgnoreCase);

            return new HttpRequest { Method = parts[0].ToUpperInvariant(), Path = parts[1], Headers = headers, Body = body, KeepAlive = keepAlive };
        }

        private static async Task<byte[]> ReadChunkedAsync(BufferedReader reader, CancellationToken token)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = await reader.ReadLineAsync(1024, token).ConfigureAwait(false) ?? throw new IOException("Unexpected end of chunked body");
                var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                if (size == 0)
                {
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(MaxHeaderBytes, token).ConfigureAwait(false))) { } // trailers
                    return ms.ToArray();
                }
                if (ms.Length + size > MaxBodyBytes) throw new HttpBadRequest("Body too large");
                var chunk = new byte[size];
                await reader.ReadExactAsync(chunk, 0, size, token).ConfigureAwait(false);
                ms.Write(chunk);
                await reader.ReadLineAsync(16, token).ConfigureAwait(false); // CRLF after chunk
            }
        }
    }

    private sealed class HttpResponse(int status, string contentType, byte[] body, IReadOnlyList<(string Name, string Value)>? extraHeaders = null, bool close = false)
    {
        public bool Close { get; } = close;

        public static HttpResponse Text(int status, string text, bool close = false, IReadOnlyList<(string, string)>? extraHeaders = null)
            => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), extraHeaders, close);

        public async Task WriteAsync(Stream stream, CancellationToken token)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (Close) sb.Append("Connection: close\r\n");
            foreach (var (name, value) in extraHeaders ?? []) sb.Append(name).Append(": ").Append(value).Append("\r\n");
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, token).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized",
            403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error",
        };
    }
}
