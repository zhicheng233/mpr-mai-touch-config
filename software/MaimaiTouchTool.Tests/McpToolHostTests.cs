using System.Text.Json.Nodes;
using MaimaiTouchTool.Mcp;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

/// <summary>
/// 工具层的独立测试：用假会话驱动，不碰串口、不起进程。
/// 重点覆盖两件事 —— 参数校验（错了要报清楚），以及映射对调语义（34 键永远一一对应）。
/// </summary>
public sealed class McpToolHostTests
{
    private sealed class FakeSession : IMcpSession
    {
        public bool IsConnected { get; private set; }
        public string PortName { get; private set; } = string.Empty;
        public ConfigBlob AppliedConfig { get; private set; } = ConfigBlob.Defaults();
        public ConfigBlob StagedConfig { get; set; } = ConfigBlob.Defaults();
        public List<string> Calls { get; } = [];
        public bool ApplyThrows { get; set; }
        public TimeSpan ApplyDelay { get; set; } = TimeSpan.Zero;
        public int MaxConcurrent { get; private set; }
        private int concurrent;

        private async Task<T> TrackAsync<T>(Func<Task<T>> work)
        {
            var now = Interlocked.Increment(ref concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            try { return await work(); }
            finally { Interlocked.Decrement(ref concurrent); }
        }

        public Task<string> ConnectAsync(string port, CancellationToken cancellationToken)
        {
            IsConnected = true; PortName = port; Calls.Add("connect");
            return Task.FromResult($"connected to {port}");
        }

        public Task<string> DisconnectAsync(CancellationToken cancellationToken)
        {
            IsConnected = false; PortName = string.Empty; Calls.Add("disconnect");
            return Task.FromResult("disconnected");
        }

        public Task<string> ApplyAsync(CancellationToken cancellationToken) => TrackAsync(async () =>
        {
            Calls.Add("apply");
            if (ApplyDelay > TimeSpan.Zero) await Task.Delay(ApplyDelay, cancellationToken);
            if (ApplyThrows) throw new IOException("device went away");
            AppliedConfig = StagedConfig;
            return "applied";
        });

        public Task<string> SaveAsync(CancellationToken cancellationToken) => TrackAsync(() =>
        {
            Calls.Add("save");
            return Task.FromResult("saved");
        });

        public Task<string> CalibrateAsync(CancellationToken cancellationToken) { Calls.Add("calibrate"); return Task.FromResult("calibrated"); }

        public Task<string> ReadTelemetryAsync(TelemetryRequest request, CancellationToken cancellationToken)
        {
            LastTelemetry = request;
            return Task.FromResult($"telemetry {request.DurationMs}");
        }

        public TelemetryRequest LastTelemetry { get; private set; }

        public void StageBoard(int boardIndex, BoardEdit edit)
        {
            var boards = StagedConfig.Boards.ToArray();
            var b = boards[boardIndex];
            boards[boardIndex] = b with
            {
                BaseThreshold = edit.BaseThreshold ?? b.BaseThreshold,
                SensOffset = edit.SensOffset ?? b.SensOffset,
                TouchDebounce = edit.TouchDebounce ?? b.TouchDebounce,
                ReleaseDebounce = edit.ReleaseDebounce ?? b.ReleaseDebounce,
                Sfi = edit.Sfi ?? b.Sfi,
                Esi = edit.Esi ?? b.Esi,
            };
            StagedConfig = StagedConfig with { Boards = boards };
        }

        public void StageElectrode(int keyIndex, int offset)
        {
            var offsets = StagedConfig.ElectrodeOffsets.ToArray();
            offsets[keyIndex] = (sbyte)offset;
            StagedConfig = StagedConfig with { ElectrodeOffsets = offsets };
        }

        public void StageMapping(int keyIndex, int boardIndex, int channel, bool swap) =>
            StagedConfig = StagedConfig with { KeyMap = McpMapping.Change(StagedConfig.Mapping, keyIndex, boardIndex, channel, swap) };
    }

    private static (McpToolHost Host, FakeSession Session) NewHost()
    {
        var session = new FakeSession();
        return (new McpToolHost(session), session);
    }

    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in pairs) obj[key] = value;
        return obj;
    }

    [Fact]
    public async Task UnknownToolIsReportedNotThrown()
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync("nope", new JsonObject(), CancellationToken.None);

        Assert.True(isError);
        Assert.StartsWith("ERROR:", text);
        Assert.Contains("unknown tool nope", text);
    }

    [Fact]
    public async Task GetConfigRefusesBeforeConnecting()
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync("get_config", new JsonObject(), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("not connected", text);
    }

    [Fact]
    public async Task ConnectRequiresAPort()
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync("connect", new JsonObject(), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("port is required", text);
    }

    [Fact]
    public async Task ConnectTrimsThePortName()
    {
        var (host, session) = NewHost();
        var (_, isError) = await host.CallAsync("connect", Args(("port", "  COM7 ")), CancellationToken.None);

        Assert.False(isError);
        Assert.Equal("COM7", session.PortName);
    }

    [Fact]
    public async Task SetBoardLeavesUnspecifiedFieldsAlone()
    {
        var (host, session) = NewHost();
        var before = session.StagedConfig.Boards[1];

        var (_, isError) = await host.CallAsync("set_board", Args(("board", 2), ("baseThreshold", 24)), CancellationToken.None);

        Assert.False(isError);
        var after = session.StagedConfig.Boards[1];
        Assert.Equal(24, after.BaseThreshold);
        Assert.Equal(before.SensOffset, after.SensOffset);
        Assert.Equal(before.TouchDebounce, after.TouchDebounce);
        Assert.Equal(before.Sfi, after.Sfi);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task SetBoardRejectsOutOfRangeBoardNumbers(int board)
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync("set_board", Args(("board", board)), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("board must be 1-4", text);
    }

    [Fact]
    public async Task SetBoardClampsInsteadOfWrapping()
    {
        var (host, session) = NewHost();
        // 超出协议范围的输入不应回绕成垃圾值（byte 回绕会把 300 变成 44）。
        await host.CallAsync("set_board", Args(("board", 1), ("baseThreshold", 300), ("sensOffset", -900)), CancellationToken.None);

        Assert.Equal(255, session.StagedConfig.Boards[0].BaseThreshold);
        Assert.Equal(-127, session.StagedConfig.Boards[0].SensOffset);
    }

    [Fact]
    public async Task SetElectrodeAcceptsLowerCaseKeyNames()
    {
        var (host, session) = NewHost();
        var (_, isError) = await host.CallAsync("set_electrode", Args(("key", "c2"), ("offset", -8)), CancellationToken.None);

        Assert.False(isError);
        Assert.Equal(-8, session.StagedConfig.ElectrodeOffsets[17]);
    }

    [Fact]
    public async Task SetElectrodeRejectsUnknownKeys()
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync("set_electrode", Args(("key", "Z9"), ("offset", 1)), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("unknown key Z9", text);
    }

    [Fact]
    public async Task SetMappingToABusyChannelFailsWithoutSwap()
    {
        var (host, session) = NewHost();
        // A1 默认在 4:04；把 A2 也指到同一个通道必须被拒绝。
        var a1 = KeyMapping.Default[0];
        var board = KeyMapping.BoardOf(a1) + 1;
        var channel = KeyMapping.ChannelOf(a1);

        var (text, isError) = await host.CallAsync(
            "set_mapping", Args(("key", "A2"), ("board", board), ("channel", channel)), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("swap=true", text);
        Assert.Equal(KeyMapping.Default, session.StagedConfig.Mapping);
    }

    [Fact]
    public async Task SetMappingWithSwapTradesTheTwoKeys()
    {
        var (host, session) = NewHost();
        var a1 = KeyMapping.Default[0];
        var a2 = KeyMapping.Default[1];
        var (board, channel) = (KeyMapping.BoardOf(a1), KeyMapping.ChannelOf(a1));

        var (_, isError) = await host.CallAsync(
            "set_mapping", Args(("key", "A2"), ("board", board + 1), ("channel", channel), ("swap", true)), CancellationToken.None);

        Assert.False(isError);
        var mapping = session.StagedConfig.Mapping;
        Assert.Equal(KeyMapping.Pack(board, channel), mapping[1]);   // A2 拿到 A1 原来的位置
        Assert.Equal(a2, mapping[0]);                               // A1 被换到 A2 原来的位置
        Assert.True(KeyMapping.TryValidate(mapping, out _));
    }

    [Fact]
    public async Task SetMappingToAFreeChannelKeepsEveryoneElse()
    {
        var (host, session) = NewHost();
        var used = KeyMapping.Default.ToHashSet();
        var free = Enumerable.Range(0, KeyMapping.ChannelCount)
            .Select(i => KeyMapping.Pack(i / KeyMapping.PortCount, i % KeyMapping.PortCount))
            .First(p => !used.Contains(p));

        var (_, isError) = await host.CallAsync(
            "set_mapping",
            Args(("key", "C1"), ("board", KeyMapping.BoardOf(free) + 1), ("channel", KeyMapping.ChannelOf(free))),
            CancellationToken.None);

        Assert.False(isError);
        var mapping = session.StagedConfig.Mapping;
        Assert.Equal(free, mapping[16]);
        for (var i = 0; i < mapping.Length; i++)
            if (i != 16) Assert.Equal(KeyMapping.Default[i], mapping[i]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task SetMappingRejectsOutOfRangeBoards(int board)
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync(
            "set_mapping", Args(("key", "A1"), ("board", board), ("channel", 0)), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("board must be 1-4", text);
    }

    [Fact]
    public async Task SetMappingRejectsOutOfRangeChannels()
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync(
            "set_mapping", Args(("key", "A1"), ("board", 1), ("channel", 12)), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("channel must be 0-11", text);
    }

    [Fact]
    public async Task ReadTelemetryClampsTheDuration()
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync("read_telemetry", Args(("durationMs", 1)), CancellationToken.None);

        Assert.False(isError);
        Assert.Equal("telemetry 100", text);
    }

    [Fact]
    public async Task ReadTelemetryDefaultsToASecond()
    {
        var (host, _) = NewHost();
        var (text, _) = await host.CallAsync("read_telemetry", new JsonObject(), CancellationToken.None);

        Assert.Equal("telemetry 1000", text);
    }

    // 模型需要能直接问「某个键/某个通道这一段时间怎么变的」，而不是自己扫 48 个数。
    [Fact]
    public async Task ReadTelemetryAcceptsAKeyToFocus()
    {
        var (host, session) = NewHost();
        var (_, isError) = await host.CallAsync("read_telemetry", Args(("key", "B3")), CancellationToken.None);

        Assert.False(isError);
        Assert.Equal(10, session.LastTelemetry.KeyIndex);   // B3 是第 11 个键（0 基下标 10）
        Assert.Null(session.LastTelemetry.Board);
    }

    [Fact]
    public async Task ReadTelemetryAcceptsABoardAndChannel()
    {
        var (host, session) = NewHost();
        var (_, isError) = await host.CallAsync(
            "read_telemetry", Args(("board", 2), ("channel", 7)), CancellationToken.None);

        Assert.False(isError);
        Assert.Equal(2, session.LastTelemetry.Board);
        Assert.Equal(7, session.LastTelemetry.Channel);
        Assert.Null(session.LastTelemetry.KeyIndex);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    public async Task ReadTelemetryRejectsOutOfRangeBoards(int board, int channel)
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync(
            "read_telemetry", Args(("board", board), ("channel", channel)), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("board must be 1-4", text);
    }

    [Fact]
    public async Task ReadTelemetryRejectsAChannelWithoutABoard()
    {
        var (host, _) = NewHost();
        var (text, isError) = await host.CallAsync("read_telemetry", Args(("channel", 3)), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("channel needs board", text);
    }

    // get_config 的 keyMap 必须自带键名和通道下标，否则模型得自己算 board*12+channel。
    [Fact]
    public void ConfigJsonNamesEveryKeyAndChannel()
    {
        var json = McpToolHost.ConfigJson(ConfigBlob.Defaults());

        var keyMap = json["keyMap"]!.AsArray();
        Assert.Equal(34, keyMap.Count);
        Assert.Equal("A1", keyMap[0]!["key"]!.GetValue<string>());
        // A1 默认打包值 52 → board 4 (0x5D)、通道 4、通道下标 3*12+4 = 40。
        Assert.Equal(4, keyMap[0]!["board"]!.GetValue<int>());
        Assert.Equal(4, keyMap[0]!["channel"]!.GetValue<int>());
        Assert.Equal(40, keyMap[0]!["channelIndex"]!.GetValue<int>());

        var channels = json["channels"]!.AsArray();
        Assert.Equal(48, channels.Count);
        Assert.Equal("A8", channels[0]!.GetValue<string>());
        Assert.Equal("A1", channels[40]!.GetValue<string>());
        Assert.Equal("unmapped", channels[8]!.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentCallsAreSerializedSoTheyCannotInterleaveOnThePort()
    {
        var (host, session) = NewHost();
        session.ApplyDelay = TimeSpan.FromMilliseconds(150);

        var first = host.CallAsync("apply", new JsonObject(), CancellationToken.None);
        var second = host.CallAsync("save", new JsonObject(), CancellationToken.None);
        await Task.WhenAll(first, second);

        // save 必须在 apply 完全结束之后才开始，否则两条命令会交错读写同一个串口。
        Assert.Equal(["apply", "save"], session.Calls);
        Assert.Equal(1, session.MaxConcurrent);
    }

    [Fact]
    public async Task DeviceFailuresSurfaceAsToolErrors()
    {
        var (host, session) = NewHost();
        session.ApplyThrows = true;

        var (text, isError) = await host.CallAsync("apply", new JsonObject(), CancellationToken.None);

        Assert.True(isError);
        Assert.Contains("device went away", text);
    }

    [Fact]
    public async Task ToolDefinitionsDeclareRequiredAndOptionalParameters()
    {
        var tools = McpToolHost.ToolDefinitions();
        var byName = tools.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);

        var connect = byName["connect"]["inputSchema"]!;
        Assert.Contains("port", connect["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("port", connect["properties"]!.AsObject().Select(p => p.Key));

        // board 是必填，其余都可选 —— 否则模型得先 get_config 抄一遍才能改一项。
        var setBoard = byName["set_board"]["inputSchema"]!;
        var required = setBoard["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["board"], required);

        var telemetry = byName["read_telemetry"]["inputSchema"]!;
        Assert.Empty(telemetry["required"]!.AsArray());
        // 可选参数里必须能指定聚焦通道，否则模型拿不到「某个通道这一段时间的变化」。
        var telemetryParams = telemetry["properties"]!.AsObject().Select(p => p.Key).ToArray();
        Assert.Contains("durationMs", telemetryParams);
        Assert.Contains("key", telemetryParams);
        Assert.Contains("board", telemetryParams);
        Assert.Contains("channel", telemetryParams);
    }

    [Fact]
    public async Task ServerAnswersInitializeAndToolsList()
    {
        var (host, _) = NewHost();
        var server = new McpServer(host);

        var init = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize" }, CancellationToken.None);
        Assert.NotNull(init);
        Assert.Equal("maimai-touch", init!["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var list = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }, CancellationToken.None);
        Assert.NotEmpty(list!["result"]!["tools"]!.AsArray());
    }

    [Fact]
    public async Task NotificationsGetNoResponse()
    {
        var (host, _) = NewHost();
        var server = new McpServer(host);

        var response = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task UnknownMethodsReturnJsonRpcError()
    {
        var (host, _) = NewHost();
        var server = new McpServer(host);

        var response = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 9, ["method"] = "tools/nope" }, CancellationToken.None);

        Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
    }
}
