using System.Text.Json;
using System.Text.Json.Nodes;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.ViewModels;

namespace MaimaiTouchTool.Mcp;

/// <summary>
/// 极简 MCP (Model Context Protocol) stdio 服务器：让 AI 模型能接入本工具，
/// 全自动完成「连接 → 读配置 → 调阈值 → 应用 → 保存 → 校准 → 读遥测」的调试流程。
/// 走 newline-delimited JSON-RPC 2.0（stdio transport），复用现有 SerialTransport + 协议层。
/// </summary>
public static class McpServer
{
    private static SerialTransport? transport;
    private static ConfigurationWorkflow? workflow;
    private static ConfigBlob staged = ConfigBlob.Defaults();
    private static ConfigBlob applied = ConfigBlob.Defaults();

    private static readonly JsonObject[] ToolDefinitions =
    [
        Tool("connect", "打开串口并进入配置模式（容忍已处于配置模式）", Req("port", ("string", "串口名，如 COM3"))),
        Tool("disconnect", "退出配置模式并断开串口"),
        Tool("get_config", "读取当前已应用配置"),
        Tool("set_board", "暂存某个 MPR121 板的响应参数（需 apply 才生效；未传的字段保持原值）",
            Req("board", ("integer", "板号 1-4")),
            Opt("baseThreshold", ("integer", "基础阈值 1-255")),
            Opt("sensOffset", ("integer", "灵敏度偏移 -127..127")),
            Opt("touchDebounce", ("integer", "触摸去抖 0-7")),
            Opt("releaseDebounce", ("integer", "释放去抖 0-7")),
            Opt("sfi", ("integer", "SFI 0-3")),
            Opt("esi", ("integer", "ESI 0-7"))),
        Tool("set_electrode", "暂存某个游戏键的电极偏移（需 apply 才生效）",
            Req("key", ("string", "键名，如 A1 或 E8")),
            Req("offset", ("integer", "电极偏移 -127..127"))),
        Tool("set_mapping", "暂存某个游戏键的板/通道映射（板号 1-4，通道 0-11；需 apply 才生效；34 键通道必须互不重复）",
            Req("key", ("string", "键名，如 A1 或 E8")),
            Req("board", ("integer", "板号 1-4")),
            Req("channel", ("integer", "通道 0-11"))),
        Tool("apply", "SET_CFG + APPLY_CFG + 读回校验，使暂存配置生效"),
        Tool("save", "SAVE_CFG，把已应用配置写入 EEPROM"),
        Tool("calibrate", "触发自动校准并等待结果"),
        Tool("read_telemetry", "流式读取调试遥测若干毫秒，返回 48 通道 filtered/baseline 与触摸位图摘要",
            Opt("durationMs", ("integer", "采样时长（毫秒），默认 1000"))),
    ];

    /// <summary>供 UI 的 MCP 引导页展示工具清单，避免文档与实际定义漂移。</summary>
    public static IReadOnlyList<(string Name, string Description)> DescribeTools() =>
        ToolDefinitions
            .Select(t => (t["name"]!.GetValue<string>(), t["description"]!.GetValue<string>()))
            .ToArray();

    public static async Task RunAsync()
    {
        using var reader = new StreamReader(Console.OpenStandardInput());
        await using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        writer.NewLine = "\n";

        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? request;
            try { request = JsonNode.Parse(line) as JsonObject; }
            catch { continue; }
            if (request is null) continue;

            var method = request["method"]?.GetValue<string>();
            var id = request["id"]?.DeepClone();
            switch (method)
            {
                case "initialize":
                    await WriteAsync(writer, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "maimai-touch", ["version"] = "0.1.0" } } });
                    break;
                case "notifications/initialized":
                    break; // 无响应
                case "tools/list":
                    await WriteAsync(writer, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["tools"] = new JsonArray(ToolDefinitions.Select(t => t.DeepClone()).ToArray()) } });
                    break;
                case "tools/call":
                {
                    var name = request["params"]?["name"]?.GetValue<string>();
                    var arguments = request["params"]?["arguments"] as JsonObject ?? new JsonObject();
                    var (text, isError) = await CallToolAsync(name ?? "", arguments);
                    await WriteAsync(writer, new JsonObject {
                        ["jsonrpc"] = "2.0", ["id"] = id,
                        ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError } });
                    break;
                }
                default:
                    await WriteAsync(writer, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"unknown method {method}" } });
                    break;
            }
        }

        await TryDisconnectAsync();
    }

    private static async Task<(string Text, bool IsError)> CallToolAsync(string name, JsonObject args)
    {
        try
        {
            var text = name switch
            {
                "connect" => await ConnectAsync(args),
                "disconnect" => await DisconnectAsync(),
                "get_config" => GetConfig(),
                "set_board" => SetBoard(args),
                "set_electrode" => SetElectrode(args),
                "set_mapping" => SetMapping(args),
                "apply" => await ApplyAsync(),
                "save" => await SaveAsync(),
                "calibrate" => await CalibrateAsync(),
                "read_telemetry" => await ReadTelemetryAsync(args),
                _ => throw new InvalidOperationException($"unknown tool {name}"),
            };
            return (text, false);
        }
        catch (Exception ex)
        {
            return ($"ERROR: {ex.Message}", true);
        }
    }

    private static async Task<string> ConnectAsync(JsonObject args)
    {
        var port = args["port"]?.GetValue<string>() ?? throw new InvalidOperationException("port is required");
        await TryDisconnectAsync(); // 重复 connect 时先释放上一个串口，避免句柄泄漏
        var serial = new SerialTransport(new SerialByteStream(port));
        transport = serial;
        workflow = new ConfigurationWorkflow(serial);
        await serial.ConnectAsync();
        await serial.SendRequestAsync((byte)ProtocolConstants.FrameType.Version, []);
        // ENTER_CFG：容忍 AlreadyConfigured（板子已在配置模式）。
        var enter = await serial.SendRequestAsync((byte)ProtocolConstants.FrameType.EnterConfig, []);
        if (enter.Type == (byte)ProtocolConstants.FrameType.Error)
        {
            var error = ErrorPayload.Decode(enter.Payload);
            if ((ProtocolConstants.ErrorStatus)error.Status != ProtocolConstants.ErrorStatus.AlreadyConfigured)
                throw new InvalidOperationException($"ENTER_CFG rejected: {Enum.GetName((ProtocolConstants.ErrorStatus)error.Status)}");
        }
        applied = staged = await ReadConfigAsync();
        return $"connected to {port}, mode=config, boards=4, electrodes=34";
    }

    private static async Task<string> DisconnectAsync()
    {
        await TryDisconnectAsync();
        return "disconnected";
    }

    private static async Task TryDisconnectAsync()
    {
        if (workflow is not null)
        {
            try { await workflow.RequestAsync(ProtocolConstants.FrameType.ExitConfig, []); } catch { }
        }
        if (transport is not null)
        {
            try { await transport.DisconnectAsync(); } catch { }
        }
        transport = null; workflow = null;
    }

    private static async Task<ConfigBlob> ReadConfigAsync()
    {
        var frame = await workflow!.RequestAsync(ProtocolConstants.FrameType.GetConfig, []);
        if (frame.Type == (byte)ProtocolConstants.FrameType.Error) throw new InvalidOperationException("GET_CFG rejected (streaming? send disconnect then reconnect)");
        var ack = AckPayload.Decode(frame.Payload);
        if (ack.Result.Length != ProtocolConstants.ConfigBlobLength) throw new FormatException("GET_CFG malformed.");
        return ConfigBlob.Decode(ack.Result);
    }

    private static string GetConfig()
    {
        if (workflow is null) throw new InvalidOperationException("not connected; call connect first");
        return JsonSerializer.Serialize(ConfigToJson(applied));
    }

    private static string SetBoard(JsonObject args)
    {
        var board = args["board"]?.GetValue<int>() ?? throw new InvalidOperationException("board required (1-4)");
        if (board is < 1 or > 4) throw new InvalidOperationException("board must be 1-4");
        var boards = staged.Boards.ToArray();
        var b = boards[board - 1];
        boards[board - 1] = b with
        {
            BaseThreshold = args.ContainsKey("baseThreshold") ? (byte)Math.Clamp(args["baseThreshold"]!.GetValue<int>(), 1, 255) : b.BaseThreshold,
            SensOffset = args.ContainsKey("sensOffset") ? (sbyte)Math.Clamp(args["sensOffset"]!.GetValue<int>(), -127, 127) : b.SensOffset,
            TouchDebounce = args.ContainsKey("touchDebounce") ? (byte)Math.Clamp(args["touchDebounce"]!.GetValue<int>(), 0, 7) : b.TouchDebounce,
            ReleaseDebounce = args.ContainsKey("releaseDebounce") ? (byte)Math.Clamp(args["releaseDebounce"]!.GetValue<int>(), 0, 7) : b.ReleaseDebounce,
            Sfi = args.ContainsKey("sfi") ? (byte)Math.Clamp(args["sfi"]!.GetValue<int>(), 0, 3) : b.Sfi,
            Esi = args.ContainsKey("esi") ? (byte)Math.Clamp(args["esi"]!.GetValue<int>(), 0, 7) : b.Esi,
        };
        staged = staged with { Boards = boards };
        return $"board {board} staged: {JsonSerializer.Serialize(staged.Boards[board - 1])}";
    }

    private static string SetElectrode(JsonObject args)
    {
        var key = args["key"]?.GetValue<string>() ?? throw new InvalidOperationException("key required");
        var offset = args["offset"]?.GetValue<int>() ?? throw new InvalidOperationException("offset required");
        var index = KeyIndex(key);
        var offsets = staged.ElectrodeOffsets.ToArray();
        offsets[index] = (sbyte)Math.Clamp(offset, -127, 127);
        staged = staged with { ElectrodeOffsets = offsets };
        return $"electrode {key} staged: offset={offsets[index]}";
    }

    private static string SetMapping(JsonObject args)
    {
        var key = args["key"]?.GetValue<string>() ?? throw new InvalidOperationException("key required");
        var board = args["board"]?.GetValue<int>() ?? throw new InvalidOperationException("board required (1-4)");
        var channel = args["channel"]?.GetValue<int>() ?? throw new InvalidOperationException("channel required (0-11)");
        if (board is < 1 or > 4) throw new InvalidOperationException("board must be 1-4");
        if (channel is < 0 or > 11) throw new InvalidOperationException("channel must be 0-11");
        var index = KeyIndex(key);
        var mapping = KeyMapping.Clone(staged.Mapping);
        mapping[index] = KeyMapping.Pack(board - 1, channel);
        if (!KeyMapping.TryValidate(mapping, out var error)) throw new InvalidOperationException($"mapping conflict: {error}; each channel must be used by exactly one key");
        staged = staged with { KeyMap = mapping };
        return $"mapping {key} staged: board={board} channel={channel}";
    }

    private static int KeyIndex(string key)
    {
        var normalized = key.Trim().ToUpperInvariant();
        var index = Array.IndexOf(KeyMapping.KeyLabels, normalized);
        if (index < 0) throw new InvalidOperationException($"unknown key {key}");
        return index;
    }

    private static async Task<string> ApplyAsync()
    {
        applied = await workflow!.ApplyAsync(staged);
        return $"applied & verified: {JsonSerializer.Serialize(ConfigToJson(applied))}";
    }

    private static async Task<string> SaveAsync()
    {
        await workflow!.SaveAsync();
        return "saved to EEPROM";
    }

    private static async Task<string> CalibrateAsync()
    {
        if (transport is not IEventRequestClient events) throw new InvalidOperationException("transport does not support events");
        var result = await events.SendRequestAndWaitForEventAsync(
            (byte)ProtocolConstants.FrameType.Calibrate, new CalibratePayload(0, 0).Encode(),
            (byte)ProtocolConstants.FrameType.CalDone, TimeSpan.FromSeconds(61), CancellationToken.None);
        if (result.Ack.Type == (byte)ProtocolConstants.FrameType.Error)
            throw new InvalidOperationException($"CALIBRATE rejected: {ErrorPayload.Decode(result.Ack.Payload).Status}");
        var done = CalDonePayload.Decode(result.Event.Payload);
        return done.Status == CalibrationStatus.Success
            ? $"calibration succeeded (reason={done.Reason})"
            : $"calibration failed (reason={done.Reason})";
    }

    private static async Task<string> ReadTelemetryAsync(JsonObject args)
    {
        var durationMs = args.ContainsKey("durationMs") ? args["durationMs"]!.GetValue<int>() : 1000;
        if (transport is null) throw new InvalidOperationException("not connected");
        var last = default(DebugFrame?);
        var count = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(durationMs);
        try
        {
            await transport.StreamDebugEventsAsync(100, sample =>
            {
                last = sample.Frame; count++;
            }, _ => { }, cts.Token, info => { });
        }
        catch (OperationCanceledException) { }
        if (last is not { } frame) return "no telemetry frames received";
        return JsonSerializer.Serialize(new
        {
            frames = count,
            durationMs,
            touchBits = frame.TouchBits,
            filtered = frame.Filtered,
            baseline = frame.Baseline,
            chipStatus = frame.ChipStatus,
            i2cErrors = frame.I2cErrors,
        });
    }

    private static JsonObject ConfigToJson(ConfigBlob config) => new()
    {
        ["boards"] = new JsonArray(config.Boards.Select(b => new JsonObject {
            ["baseThreshold"] = b.BaseThreshold, ["sensOffset"] = b.SensOffset,
            ["touchDebounce"] = b.TouchDebounce, ["releaseDebounce"] = b.ReleaseDebounce,
            ["sfi"] = b.Sfi, ["esi"] = b.Esi }).ToArray()),
        ["electrodeOffsets"] = new JsonArray(config.ElectrodeOffsets.Select(o => (JsonNode)o).ToArray()),
        ["keyMap"] = new JsonArray(config.Mapping.Select(p => (JsonNode)new JsonObject {
            ["board"] = KeyMapping.BoardOf(p) + 1, ["channel"] = KeyMapping.ChannelOf(p) }).ToArray()),
    };

    private static JsonObject Tool(string name, string description, params (string Name, (string Type, string Description) Spec, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (pname, (ptype, pdesc), isRequired) in parameters)
        {
            properties[pname] = new JsonObject { ["type"] = ptype, ["description"] = pdesc };
            if (isRequired) required.Add(pname);
        }
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required },
        };
    }

    private static (string, (string, string), bool) Req(string name, (string Type, string Description) spec) => (name, spec, true);

    private static (string, (string, string), bool) Opt(string name, (string Type, string Description) spec) => (name, spec, false);

    private static Task WriteAsync(StreamWriter writer, JsonObject message) => writer.WriteLineAsync(message.ToJsonString());
}
