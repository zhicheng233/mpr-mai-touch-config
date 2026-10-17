using System.Text.Json.Nodes;
using MaimaiTouchTool.Firmware;

namespace MaimaiTouchTool.Mcp;

/// <summary>
/// 极简 MCP (Model Context Protocol) 服务器：让 AI 模型能接入本工具，
/// 全自动完成「连接 → 读配置 → 调阈值 → 应用 → 保存 → 校准 → 读遥测」的调试流程。
///
/// 这里只负责 JSON-RPC 的分发，工具实现和状态在 <see cref="McpToolHost"/> + <see cref="IMcpSession"/> 里，
/// 这样 stdio 进程（<see cref="StandaloneSession"/>）和 GUI 内嵌（<see cref="GuiSession"/>）跑的是同一套工具。
/// </summary>
public sealed class McpServer(McpToolHost host)
{
    private readonly McpToolHost host = host;

    /// <summary>stdio 入口：<c>MaimaiTouchTool.exe --mcp</c>。</summary>
    public static async Task RunStdioAsync()
    {
        await using var session = new StandaloneSession();
        var server = new McpServer(new McpToolHost(session));
        using var reader = new StreamReader(Console.OpenStandardInput());
        await using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        writer.NewLine = "\n";
        await server.ServeAsync(
            async () => await reader.ReadLineAsync(),
            message => writer.WriteLineAsync(message.ToJsonString()),
            CancellationToken.None);
    }

    /// <summary>处理一条 JSON-RPC 消息；返回 null 表示这是通知，不需要回包。</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var method = request["method"]?.GetValue<string>();
        var id = request["id"]?.DeepClone();
        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "maimai-touch", ["version"] = ToolInfo.Version },
                });
            case "notifications/initialized":
                return null;
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = McpToolHost.ToolDefinitions() });
            case "tools/call":
            {
                var name = request["params"]?["name"]?.GetValue<string>() ?? string.Empty;
                var arguments = request["params"]?["arguments"] as JsonObject ?? new JsonObject();
                var (text, isError) = await host.CallAsync(name, arguments, cancellationToken);
                return Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                });
            }
            default:
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"unknown method {method}" },
                };
        }
    }

    /// <summary>跑一条「读一行 → 处理 → 写一行」的循环，stdio 和 GUI 内嵌（按行分帧的本地连接）共用。</summary>
    public async Task ServeAsync(Func<Task<string?>> readLine, Func<JsonObject, Task> writeLine, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try { line = await readLine(); }
            catch (Exception) { break; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject? request;
            try { request = JsonNode.Parse(line) as JsonObject; }
            catch { continue; }
            if (request is null) continue;

            JsonObject? response;
            try { response = await HandleAsync(request, cancellationToken); }
            catch (Exception ex)
            {
                response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = request["id"]?.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message },
                };
            }
            if (response is not null) await writeLine(response);
        }
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
}
