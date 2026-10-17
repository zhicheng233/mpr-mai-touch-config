using System.Text.Json;
using System.Text.Json.Nodes;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.Mcp;

/// <summary>一次板级参数修改；<c>null</c> 表示该字段不变。</summary>
public readonly record struct BoardEdit(
    byte? BaseThreshold, sbyte? SensOffset, byte? TouchDebounce, byte? ReleaseDebounce, byte? Sfi, byte? Esi);

/// <summary>
/// MCP 工具层对「一个可配置会话」的唯一依赖。两个实现：
/// <list type="bullet">
/// <item><see cref="StandaloneSession"/>：stdio 模式，自建串口，暂存态在服务器进程里。</item>
/// <item><see cref="GuiSession"/>：GUI 内嵌模式，直接改运行中的编辑器，界面上能看到 AI 正在改什么。</item>
/// </list>
/// </summary>
public interface IMcpSession
{
    bool IsConnected { get; }
    string PortName { get; }

    /// <summary>设备上当前生效（已 apply）的配置。</summary>
    ConfigBlob AppliedConfig { get; }

    /// <summary>暂存区里待应用的配置。</summary>
    ConfigBlob StagedConfig { get; }

    Task<string> ConnectAsync(string port, CancellationToken cancellationToken);
    Task<string> DisconnectAsync(CancellationToken cancellationToken);
    Task<string> ApplyAsync(CancellationToken cancellationToken);
    Task<string> SaveAsync(CancellationToken cancellationToken);
    Task<string> CalibrateAsync(CancellationToken cancellationToken);
    Task<string> ReadTelemetryAsync(TelemetryRequest request, CancellationToken cancellationToken);

    void StageBoard(int boardIndex, BoardEdit edit);
    void StageElectrode(int keyIndex, int offset);
    void StageMapping(int keyIndex, int boardIndex, int channel, bool swap);
}

/// <summary>
/// MCP 工具定义与分发。stdio 服务器和 GUI 内嵌服务器共用这一份，
/// 所以「模型看到的工具」在任何入口都完全一致。
/// </summary>
public sealed class McpToolHost
{
    public McpToolHost(IMcpSession session) => Session = session;

    public IMcpSession Session { get; }

    /// <summary>
    /// 串口是独占资源：两个并发工具调用（HTTP 端点会并发，stdio 不会）不能交错着读同一个口。
    /// 这里串行化，代价是「一个慢调用（如 read_telemetry）会把后面的排队」—— 这正是想要的语义。
    /// </summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    private static readonly JsonObject[] Definitions =
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
        Tool("set_mapping", "暂存某个游戏键的板/通道映射（板号 1-4，通道 0-11；需 apply 才生效）",
            Req("key", ("string", "键名，如 A1 或 E8")),
            Req("board", ("integer", "板号 1-4")),
            Req("channel", ("integer", "通道 0-11")),
            Opt("swap", ("boolean", "目标通道已有别的键时，两个键对调而不是报错"))),
        Tool("apply", "SET_CFG + APPLY_CFG + 读回校验，使暂存配置生效"),
        Tool("save", "SAVE_CFG，把已应用配置写入 EEPROM"),
        Tool("calibrate", "触发自动校准并等待结果"),
        Tool("read_telemetry", "流式读取调试遥测若干毫秒，返回 48 通道 filtered/baseline 的变化范围、最大下降量、触摸帧数，以及通道→键名对照表",
            Opt("durationMs", ("integer", "采样时长（毫秒），默认 1000，上限 60000")),
            Opt("key", ("string", "只看这个键的通道逐次变化，如 A1")),
            Opt("board", ("integer", "只看这块板（1-4）的某个通道，需与 channel 一起用")),
            Opt("channel", ("integer", "通道号 0-11，需与 board 一起用"))),
    ];

    public static IReadOnlyList<(string Name, string Description)> Describe() =>
        Definitions.Select(t => (t["name"]!.GetValue<string>(), t["description"]!.GetValue<string>())).ToArray();

    /// <summary>tools/list 的返回体；每次深拷贝，避免调用方改到定义本身。</summary>
    public static JsonArray ToolDefinitions() => new(Definitions.Select(t => t.DeepClone()).ToArray());

    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonObject args, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var text = name switch
            {
                "connect" => await ConnectAsync(args, cancellationToken),
                "disconnect" => await Session.DisconnectAsync(cancellationToken),
                "get_config" => GetConfig(),
                "set_board" => SetBoard(args),
                "set_electrode" => SetElectrode(args),
                "set_mapping" => SetMapping(args),
                "apply" => await Session.ApplyAsync(cancellationToken),
                "save" => await Session.SaveAsync(cancellationToken),
                "calibrate" => await Session.CalibrateAsync(cancellationToken),
                "read_telemetry" => await ReadTelemetryAsync(args, cancellationToken),
                _ => throw new InvalidOperationException($"unknown tool {name}"),
            };
            return (text, false);
        }
        catch (Exception ex)
        {
            return ($"ERROR: {ex.Message}", true);
        }
        finally { gate.Release(); }
    }

    private async Task<string> ConnectAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var port = args["port"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(port)) throw new InvalidOperationException("port is required");
        return await Session.ConnectAsync(port.Trim(), cancellationToken);
    }

    private string GetConfig()
    {
        if (!Session.IsConnected) throw new InvalidOperationException("not connected; call connect first");
        return JsonSerializer.Serialize(ConfigJson(Session.AppliedConfig));
    }

    private string SetBoard(JsonObject args)
    {
        var board = args["board"]?.GetValue<int>() ?? throw new InvalidOperationException("board required (1-4)");
        if (board is < 1 or > 4) throw new InvalidOperationException("board must be 1-4");
        var edit = new BoardEdit(
            Optional(args, "baseThreshold", 1, 255, (byte)0),
            Optional(args, "sensOffset", -127, 127, (sbyte)0),
            Optional(args, "touchDebounce", 0, 7, (byte)0),
            Optional(args, "releaseDebounce", 0, 7, (byte)0),
            Optional(args, "sfi", 0, 3, (byte)0),
            Optional(args, "esi", 0, 7, (byte)0));
        Session.StageBoard(board - 1, edit);
        return $"board {board} staged: {JsonSerializer.Serialize(Session.StagedConfig.Boards[board - 1])}";
    }

    private string SetElectrode(JsonObject args)
    {
        var key = args["key"]?.GetValue<string>() ?? throw new InvalidOperationException("key required");
        var offset = args["offset"]?.GetValue<int>() ?? throw new InvalidOperationException("offset required");
        var index = KeyIndex(key);
        Session.StageElectrode(index, Math.Clamp(offset, -127, 127));
        return $"electrode {key} staged: offset={Session.StagedConfig.ElectrodeOffsets[index]}";
    }

    private string SetMapping(JsonObject args)
    {
        var key = args["key"]?.GetValue<string>() ?? throw new InvalidOperationException("key required");
        var board = args["board"]?.GetValue<int>() ?? throw new InvalidOperationException("board required (1-4)");
        var channel = args["channel"]?.GetValue<int>() ?? throw new InvalidOperationException("channel required (0-11)");
        if (board is < 1 or > 4) throw new InvalidOperationException("board must be 1-4");
        if (channel is < 0 or > 11) throw new InvalidOperationException("channel must be 0-11");
        var swap = args["swap"]?.GetValue<bool>() ?? false;
        var index = KeyIndex(key);
        Session.StageMapping(index, board - 1, channel, swap);
        return $"mapping {key} staged: board={board} channel={channel}";
    }

    private async Task<string> ReadTelemetryAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var durationMs = args.ContainsKey("durationMs") ? args["durationMs"]!.GetValue<int>() : 1000;
        // 聚焦通道二选一：给键名，或给板号+通道号。两者都给时键名优先（键才是有意义的标识）。
        int? keyIndex = args.ContainsKey("key") ? KeyIndex(args["key"]!.GetValue<string>()) : null;
        int? board = args.ContainsKey("board") ? args["board"]!.GetValue<int>() : null;
        int? channel = args.ContainsKey("channel") ? args["channel"]!.GetValue<int>() : null;
        if (board is < 1 or > 4) throw new InvalidOperationException("board must be 1-4");
        if (channel is < 0 or > 11) throw new InvalidOperationException("channel must be 0-11");
        if (channel is not null && board is null) throw new InvalidOperationException("channel needs board too (or pass key instead)");
        var request = new TelemetryRequest(Math.Clamp(durationMs, 100, 60000), keyIndex, board, channel);
        return await Session.ReadTelemetryAsync(request, cancellationToken);
    }

    /// <summary>键名 → 键序（A1…E8），大小写不敏感。</summary>
    public static int KeyIndex(string key)
    {
        var normalized = key.Trim().ToUpperInvariant();
        var index = Array.IndexOf(KeyMapping.KeyLabels, normalized);
        if (index < 0) throw new InvalidOperationException($"unknown key {key}");
        return index;
    }

    public static JsonObject ConfigJson(ConfigBlob config) => new()
    {
        ["boards"] = new JsonArray(config.Boards.Select(b => (JsonNode)new JsonObject {
            ["baseThreshold"] = b.BaseThreshold, ["sensOffset"] = b.SensOffset,
            ["touchDebounce"] = b.TouchDebounce, ["releaseDebounce"] = b.ReleaseDebounce,
            ["sfi"] = b.Sfi, ["esi"] = b.Esi }).ToArray()),
        ["electrodeOffsets"] = new JsonArray(config.ElectrodeOffsets.Select(o => (JsonNode)o).ToArray()),
        // keyMap 按键序（A1…E8）给，同时带上 key 名和它在 48 通道里的下标，
        // 这样模型不用自己算 board*12+channel 就能把遥测通道对上键。
        ["keyMap"] = new JsonArray(config.Mapping.Select((packed, index) => (JsonNode)new JsonObject {
            ["key"] = KeyMapping.KeyLabels[index],
            ["board"] = KeyMapping.BoardOf(packed) + 1,
            ["channel"] = KeyMapping.ChannelOf(packed),
            ["channelIndex"] = KeyMapping.ChannelIndexOf(packed),
        }).ToArray()),
        // 48 通道 → 键名（未映射为 "unmapped"），与 read_telemetry 的 keys 表同构。
        ["channels"] = new JsonArray(KeyMapping.ChannelLabels(KeyMapping.Clone(config.Mapping))
            .Select(label => (JsonNode)label).ToArray()),
    };

    /// <summary>
    /// 读一个可选整型参数并夹到协议范围内；未提供返回 null（表示「保持原值」）。
    /// 注意别写成三元表达式再装进 object：<c>cond ? (sbyte)raw : raw</c> 会被提升成 int，
    /// 之后再 <c>(T)boxed</c> 拆箱就抛 InvalidCastException。
    /// </summary>
    private static T? Optional<T>(JsonObject args, string name, int min, int max, T typeHint) where T : struct
    {
        _ = typeHint; // 仅用于推断 T；实际值不参与计算
        if (!args.ContainsKey(name)) return null;
        var raw = Math.Clamp(args[name]!.GetValue<int>(), min, max);
        if (typeof(T) == typeof(byte)) return (T)(object)Convert.ToByte(raw);
        if (typeof(T) == typeof(sbyte)) return (T)(object)Convert.ToSByte(raw);
        throw new NotSupportedException($"unsupported optional type {typeof(T).Name}");
    }

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
}
