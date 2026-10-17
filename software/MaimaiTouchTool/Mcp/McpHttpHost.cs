using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MaimaiTouchTool.Mcp;

/// <summary>
/// GUI 内嵌的 MCP 端点：在 127.0.0.1 上跑一个最小的 HTTP/1.1 服务，按 MCP Streamable HTTP 收发 JSON-RPC。
///
/// 为什么手写 HTTP 而不用 <c>HttpListener</c>：后者在 Windows 上需要 URL ACL 预留，
/// 非管理员身份会直接抛「拒绝访问」。这里只服务本机、一次一个请求，手写反而更省事。
///
/// 安全考虑（这是能驱动硬件的接口）：
/// <list type="bullet">
/// <item>只绑 <see cref="IPAddress.Loopback"/>，外网访问不到。</item>
/// <item>拒绝带 <c>Origin</c> 的请求 —— 浏览器跨域请求必带，所以网页没法偷偷 POST 过来。</item>
/// <item>只接受 <c>Content-Type: application/json</c>；浏览器发这个类型会先触发预检，
/// 而预检（OPTIONS）这里直接拒，等于再上一层保险。</item>
/// </list>
/// </summary>
public sealed class McpHttpHost : IAsyncDisposable
{
    /// <summary>默认端口。固定端口是为了让客户端配置一次就不用再改。</summary>
    public const int DefaultPort = 8477;

    /// <summary>默认端口被占时向后顺延的尝试次数。</summary>
    private const int PortScanAttempts = 16;

    private TcpListener? listener;
    private CancellationTokenSource? cancellation;
    private Task? acceptLoop;

    public bool IsRunning => listener is not null;

    /// <summary>实际绑定的端口（默认端口被占时会顺延，所以不一定等于 <see cref="DefaultPort"/>）。</summary>
    public int Port { get; private set; }

    public string Endpoint => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>启动；返回实际使用的端口。已在运行则直接返回当前端口。</summary>
    public int Start(McpServer server)
    {
        if (listener is not null) return Port;

        TcpListener? bound = null;
        var port = 0;
        for (var attempt = 0; attempt < PortScanAttempts; attempt++)
        {
            var candidate = DefaultPort + attempt;
            var probe = new TcpListener(IPAddress.Loopback, candidate);
            try { probe.Start(); bound = probe; port = candidate; break; }
            catch (SocketException) { try { probe.Stop(); } catch { } }
        }
        if (bound is null) throw new InvalidOperationException(
            $"could not bind a local port in {DefaultPort}..{DefaultPort + PortScanAttempts - 1}; stop whatever is using them or restart the app");

        listener = bound;
        Port = port;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        acceptLoop = Task.Run(() => AcceptLoopAsync(server, token), CancellationToken.None);
        return port;
    }

    public async Task StopAsync()
    {
        cancellation?.Cancel();
        try { listener?.Stop(); } catch { }
        if (acceptLoop is not null) { try { await acceptLoop; } catch { } }
        acceptLoop = null;
        listener = null;
        cancellation?.Dispose();
        cancellation = null;
    }

    private async Task AcceptLoopAsync(McpServer server, CancellationToken token)
    {
        var active = listener;
        while (!token.IsCancellationRequested && active is not null)
        {
            TcpClient client;
            try { client = await active.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            _ = Task.Run(() => HandleConnectionAsync(server, client, token), CancellationToken.None);
        }
    }

    private static async Task HandleConnectionAsync(McpServer server, TcpClient client, CancellationToken token)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        try
        {
            // 只给「读请求头」设超时：一个连上就不说话的客户端会一直占着这个任务。
            // 工具调用本身可能很长（calibrate 最长 ~61 s、read_telemetry 最长 60 s），不能一起限时。
            using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var head = await ReadHeadAsync(stream, headTimeout.Token);
            if (head is null) return;

            var (method, headers, body) = head.Value;
            if (method == "OPTIONS") { await WriteAsync(stream, 403, "Forbidden", null, token); return; }
            // 浏览器跨域请求一定带 Origin；带上了就说明不是本机客户端。
            if (headers.ContainsKey("origin")) { await WriteAsync(stream, 403, "Forbidden", null, token); return; }
            if (method != "POST") { await WriteAsync(stream, 405, "Method Not Allowed", null, token); return; }
            if (!headers.TryGetValue("content-type", out var contentType) || !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(stream, 415, "Unsupported Media Type", null, token);
                return;
            }

            JsonObject? request;
            try { request = JsonNode.Parse(body) as JsonObject; }
            catch { request = null; }
            if (request is null)
            {
                await WriteAsync(stream, 400, "Bad Request", new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "parse error" },
                }, token);
                return;
            }

            var response = await server.HandleAsync(request, token);
            if (response is null)
            {
                // 通知没有回包；MCP 规范要求 202 Accepted。
                await WriteAsync(stream, 202, "Accepted", null, token);
                return;
            }
            await WriteAsync(stream, 200, "OK", response, token);
        }
        catch (Exception)
        {
            // 浏览器/客户端都在等一个回包；直接断开会让它挂到超时。
            try
            {
                await WriteAsync(stream, 500, "Internal Server Error", new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "internal error" },
                }, CancellationToken.None);
            }
            catch { /* 连接已经没了 */ }
        }
    }

    private static async Task<(string Method, Dictionary<string, string> Headers, string Body)?> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var pending = new List<byte>(8192);
        var headEnd = -1;

        while (headEnd < 0)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) return null;
            pending.AddRange(buffer.AsSpan(0, count).ToArray());
            if (pending.Count > 64 * 1024) return null;
            headEnd = IndexOfHeadEnd(pending);
        }

        var headText = Encoding.ASCII.GetString(pending.GetRange(0, headEnd).ToArray());
        var bodyStart = headEnd + 4;
        var lines = headText.Split("\r\n");
        var method = lines[0].Split(' ')[0].ToUpperInvariant();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var length = headers.TryGetValue("content-length", out var raw) && int.TryParse(raw, out var parsed) ? parsed : 0;
        if (length <= 0) return (method, headers, string.Empty);
        if (length > 4 * 1024 * 1024) return null;

        var body = new List<byte>(length);
        body.AddRange(pending.Skip(bodyStart));
        while (body.Count < length)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) break;
            body.AddRange(buffer.AsSpan(0, count).ToArray());
        }
        return (method, headers, Encoding.UTF8.GetString(body.ToArray(), 0, Math.Min(body.Count, length)));
    }

    private static int IndexOfHeadEnd(List<byte> bytes)
    {
        for (var index = 0; index + 3 < bytes.Count; index++)
            if (bytes[index] == (byte)'\r' && bytes[index + 1] == (byte)'\n' && bytes[index + 2] == (byte)'\r' && bytes[index + 3] == (byte)'\n')
                return index;
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string reason, JsonObject? payload, CancellationToken token)
    {
        var body = payload is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(payload.ToJsonString());
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes, token);
        if (body.Length > 0) await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
