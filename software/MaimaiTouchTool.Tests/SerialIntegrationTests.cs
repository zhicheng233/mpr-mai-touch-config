using System.Buffers.Binary;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class SerialIntegrationTests
{
    [Fact]
    public async Task ConnectVersionEnterGetAndApplySaveUseTheFrozenWireProtocol()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = new SerialTransport(endpoint);
        await transport.ConnectAsync();

        var version = await transport.SendRequestAsync((byte)ProtocolConstants.FrameType.Version, []);
        Assert.Equal((byte)ProtocolConstants.FrameType.Ack, version.Type);
        Assert.Equal(16, AckPayload.Decode(version.Payload).Result.Length);

        var enter = await transport.SendRequestAsync((byte)ProtocolConstants.FrameType.EnterConfig, []);
        Assert.Equal(ProtocolConstants.DeviceMode.ConfigIdle, endpoint.Mode);
        Assert.Equal((byte)ProtocolConstants.FrameType.EnterConfig, AckPayload.Decode(enter.Payload).RequestType);

        var workflow = new ConfigurationWorkflow(transport);
        var original = await workflow.RequestAsync(ProtocolConstants.FrameType.GetConfig, []);
        Assert.Equal(ConfigBlob.Defaults().Encode(), AckPayload.Decode(original.Payload).Result);

        var changed = ConfigBlob.Defaults() with
        {
            Boards = [new BoardConfig(31, -2, 2, 3, 1, 4), ..ConfigBlob.Defaults().Boards[1..]],
            ElectrodeOffsets = ConfigBlob.Defaults().ElectrodeOffsets.Select((value, index) => index == 0 ? (sbyte)7 : value).ToArray()
        };
        var applied = await workflow.ApplyAsync(changed);
        await workflow.SaveAsync();

        Assert.Equal(changed.Encode(), applied.Encode());
        Assert.Equal(changed.Encode(), endpoint.AppliedConfig.Encode());
        Assert.Equal(changed.Encode(), endpoint.PersistedConfig.Encode());
        Assert.Equal(new byte[] { 1, 2, 16, 17, 19, 16, 18 }, endpoint.Requests.Select(request => request.Type).ToArray());
        Assert.All(endpoint.Requests, request =>
        {
            var decoded = ProtocolFrame.Decode(request.Encode());
            Assert.Equal(request.Type, decoded.Type);
            Assert.Equal(request.Sequence, decoded.Sequence);
            Assert.Equal(request.Payload, decoded.Payload);
        });
        Assert.True(endpoint.ResponseHistory.Count <= FakeSerialEndpoint.MaxHistory);
    }

    [Fact]
    public async Task CalibrationCorrelatesAckAndCalDoneAndPreservesState()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = await EnterConfigAsync(endpoint);

        var result = await transport.SendRequestAndWaitForEventAsync(
            (byte)ProtocolConstants.FrameType.Calibrate,
            new CalibratePayload(0, 0).Encode(),
            (byte)ProtocolConstants.FrameType.CalDone,
            TimeSpan.FromSeconds(1));

        Assert.Equal((byte)ProtocolConstants.FrameType.Ack, result.Ack.Type);
        var done = CalDonePayload.Decode(result.Event.Payload);
        Assert.Equal(CalibrationStatus.Success, done.Status);
        Assert.Equal(48, done.Baseline.Length);
        Assert.Equal(ProtocolConstants.DeviceMode.ConfigIdle, endpoint.Mode);
        Assert.Equal(1, endpoint.Requests.Count(request => request.Type == (byte)ProtocolConstants.FrameType.Calibrate));
        // 校准结束后传输必须回到 Config：否则后续 GET_CFG / APPLY_CFG 会被状态守卫拒绝，
        // 表现为「第一次校准后工具就不正常了」。
        Assert.Equal(TransportState.Config, transport.State);
        // 回到 Config 后仍能继续发命令（用 GET_CFG 验证端口仍可用）。
        var readback = await transport.SendRequestAsync((byte)ProtocolConstants.FrameType.GetConfig, []);
        Assert.Equal((byte)ProtocolConstants.FrameType.Ack, readback.Type);
    }

    [Fact]
    public async Task DebugStreamUpdatesTelemetryMeasuresRateAndReportsMalformedFrames()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = await EnterConfigAsync(endpoint);
        var telemetry = new TelemetrySessionModel();
        telemetry.Begin(100);
        var cancellation = new CancellationTokenSource();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCount = 0;
        var frames = new[]
        {
            NewDebug(1000, 1UL << 33),
            NewDebug(1010, 0),
            NewDebug(1020, 1UL << 27)
        };

        var stream = transport.StreamDebugEventsAsync(0,
            sample =>
            {
                telemetry.AddFrame(sample.Frame, sample.Sequence, DateTimeOffset.UnixEpoch.AddMilliseconds(sample.Frame.TimestampMs));
                if (Interlocked.Increment(ref receivedCount) == 3) received.TrySetResult(true);
            },
            error => telemetry.MarkMalformed(error.Message),
            cancellation.Token,
            info => telemetry.SetAcceptedRate(info.AcceptedRateHz, info.ActualRateHz));
        await endpoint.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        endpoint.InjectResponse(new ProtocolFrame((byte)ProtocolConstants.FrameType.DebugFrame, 0, new byte[1]));
        endpoint.InjectDebug(1, frames[0]);
        endpoint.InjectDebug(3, frames[1]);
        endpoint.InjectDebug(4, frames[2]);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await stream;

        Assert.Equal(TelemetryHealth.Error, telemetry.Health);
        Assert.Equal(1, telemetry.SequenceGaps);
        Assert.Equal(100, telemetry.ActualRateHz, 5);
        Assert.Equal((ushort)100, telemetry.AcceptedRateHz);
        Assert.Equal(1, telemetry.Channels.Count(channel => channel.IsTouched));
        Assert.Contains(telemetry.Log, entry => entry.Level == "ERROR");
    }

    [Fact]
    public async Task FakeEndpointRejectsMalformedFramesReplaysDuplicatesAndRejectsConflicts()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = await EnterConfigAsync(endpoint);
        var request = new ProtocolFrame((byte)ProtocolConstants.FrameType.GetConfig, 500, []);
        await endpoint.WriteAsync(request.Encode(), CancellationToken.None);
        await endpoint.WriteAsync(request.Encode(), CancellationToken.None);
        var first = await ReadFrameAsync(endpoint);
        var replay = await ReadFrameAsync(endpoint);
        Assert.Equal(first.Encode(), replay.Encode());

        var conflict = new ProtocolFrame((byte)ProtocolConstants.FrameType.Version, request.Sequence, []);
        await endpoint.WriteAsync(conflict.Encode(), CancellationToken.None);
        var conflictResponse = await ReadFrameAsync(endpoint);
        var conflictError = ErrorPayload.Decode(conflictResponse.Payload);
        Assert.Equal(ProtocolConstants.ErrorStatus.SequenceConflict, (ProtocolConstants.ErrorStatus)conflictError.Status);

        var malformed = request.Encode();
        malformed[^1] ^= 0xFF;
        await endpoint.WriteAsync(malformed, CancellationToken.None);
        var malformedResponse = await ReadFrameAsync(endpoint);
        Assert.Equal(ProtocolConstants.ErrorStatus.BadFrame, (ProtocolConstants.ErrorStatus)ErrorPayload.Decode(malformedResponse.Payload).Status);
        Assert.Single(endpoint.RejectedRawFrames);
        Assert.Equal(ProtocolConstants.DeviceMode.ConfigIdle, endpoint.Mode);
        Assert.Equal(3, endpoint.Requests.Count(requestFrame => requestFrame.Sequence == 500));
        await transport.DisconnectAsync();
    }

    [Fact]
    public async Task TimeoutAndDisconnectLeaveTransportDisconnectedAndIgnoreStaleResponses()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = await EnterConfigAsync(endpoint);
        endpoint.InjectResponse(new ProtocolFrame((byte)ProtocolConstants.FrameType.Ack, 999, new AckPayload((byte)ProtocolConstants.FrameType.Version, 0, 1, 0, new byte[16]).Encode()));
        var version = await transport.SendRequestAsync((byte)ProtocolConstants.FrameType.Version, []);
        Assert.Equal((ushort)3, version.Sequence);
        Assert.Equal(ProtocolConstants.DeviceMode.ConfigIdle, endpoint.Mode);

        await transport.DisconnectAsync();
        Assert.Equal(TransportState.Disconnected, transport.State);

        // 命令超时：抛 TimeoutException，但“不拆端口/不进入 Disconnected”，以便用户随后重试。
        await using var timeoutEndpoint = new FakeSerialEndpoint();
        var timeoutTransport = await EnterConfigAsync(timeoutEndpoint, TimeSpan.FromMilliseconds(20));
        timeoutEndpoint.SuppressResponses = true;
        await Assert.ThrowsAnyAsync<TimeoutException>(() => timeoutTransport.SendRequestAsync((byte)ProtocolConstants.FrameType.Version, []));
        Assert.Equal(TransportState.Config, timeoutTransport.State);

        // 用户取消（token）：中断端口并置为 Disconnected，抛 OperationCanceledException。
        await using var cancelEndpoint = new FakeSerialEndpoint();
        var cancelTransport = await EnterConfigAsync(cancelEndpoint);
        cancelEndpoint.SuppressResponses = true;
        using var cancellation = new CancellationTokenSource();
        var pendingCancel = cancelTransport.SendRequestAsync((byte)ProtocolConstants.FrameType.Version, [], cancellation.Token);
        await Task.Delay(10);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingCancel);
        Assert.Equal(TransportState.Disconnected, cancelTransport.State);

        // 设备断开：抛 IOException 并置为 Disconnected。
        await using var disconnectEndpoint = new FakeSerialEndpoint();
        var disconnectTransport = await EnterConfigAsync(disconnectEndpoint);
        disconnectEndpoint.SuppressResponses = true;
        var pending = disconnectTransport.SendRequestAsync((byte)ProtocolConstants.FrameType.Version, []);
        await Task.Delay(10);
        disconnectEndpoint.Disconnect();
        await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.Equal(TransportState.Disconnected, disconnectTransport.State);
    }

    private static async Task<SerialTransport> EnterConfigAsync(FakeSerialEndpoint endpoint, TimeSpan? commandTimeout = null)
    {
        var transport = new SerialTransport(endpoint) { CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(1) };
        await transport.ConnectAsync();
        await transport.SendRequestAsync((byte)ProtocolConstants.FrameType.Version, []);
        await transport.SendRequestAsync((byte)ProtocolConstants.FrameType.EnterConfig, []);
        return transport;
    }

    private static DebugFrame NewDebug(uint timestamp, ulong touchBits) => new(timestamp, touchBits, Enumerable.Range(0, 48).Select(index => (ushort)(500 + index)).ToArray(), Enumerable.Range(0, 48).Select(index => (ushort)(700 + index)).ToArray(), [7, 7, 7, 7], 0);

    private static async Task<ProtocolFrame> ReadFrameAsync(FakeSerialEndpoint endpoint)
    {
        var bytes = new byte[266];
        var read = 0;
        while (read < ProtocolConstants.HeaderLength)
            read += await endpoint.ReadAsync(bytes.AsMemory(read), CancellationToken.None);
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        var total = ProtocolConstants.HeaderLength + payloadLength + ProtocolConstants.CrcLength;
        while (read < total)
            read += await endpoint.ReadAsync(bytes.AsMemory(read), CancellationToken.None);
        return ProtocolFrame.Decode(bytes.AsSpan(0, total));
    }

    // 回归：连接失败后“连接”按钮必须重新可用（此前 CanExecute 要求 Status==Disconnected，失败后 Status=Error 导致永久禁用）。
    [Fact]
    public async Task ConnectButtonStaysRetryableAfterAFailedOpen()
    {
        var attempts = 0;
        var viewModel = new MainViewModel(_ =>
        {
            attempts++;
            var transport = new SerialTransport(new ThrowingByteStreamEndpoint());
            return (transport, transport);
        });
        viewModel.SelectedPort = "COM_TEST";

        Assert.True(viewModel.ConnectCommand.CanExecute(null));
        await viewModel.RunConnectForTestAsync();
        Assert.Equal(ConnectionStatus.Error, viewModel.Status);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsConnecting);
        Assert.True(viewModel.ConnectCommand.CanExecute(null), "Connect must stay retryable after a failure.");

        await viewModel.RunConnectForTestAsync();
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ConnectFailureDoesNotLeaveTheUiBusy()
    {
        var viewModel = new MainViewModel(_ =>
        {
            var transport = new SerialTransport(new ThrowingByteStreamEndpoint());
            return (transport, transport);
        });
        viewModel.SelectedPort = "COM_TEST";

        await viewModel.RunConnectForTestAsync();

        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsConnecting);
        Assert.NotEqual(ConnectionStatus.Connecting, viewModel.Status);
        Assert.True(viewModel.ConnectCommand.CanExecute(null));
    }

    private sealed class ThrowingByteStreamEndpoint : IByteStream
    {
        public Task OpenAsync(CancellationToken cancellationToken) => throw new IOException("The port is already in use.");
        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => ValueTask.FromResult(0);
        public void Abort() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // 回归：上次工具被强杀后板子可能残留 CONFIG_IDLE，此时 ENTER_CFG 会回 AlreadyConfigured——
    // 工具必须把它当作“已可配置”继续连接，而不是连接失败。
    [Fact]
    public async Task ConnectSucceedsWhenDeviceIsAlreadyInConfigMode()
    {
        var endpoint = new FakeSerialEndpoint();
        endpoint.ForceMode(ProtocolConstants.DeviceMode.ConfigIdle);
        var viewModel = new MainViewModel(_ => { var t = new SerialTransport(endpoint); return (t, t); });
        viewModel.SelectedPort = "COM_TEST";

        await viewModel.RunConnectForTestAsync();

        Assert.Equal(ConnectionStatus.Connected, viewModel.Status);
        Assert.True(viewModel.IsConnected);
    }

    // 回归：板子残留 STREAMING 时 GET_CFG 会被拒（Busy），工具应自动 STREAM_STOP 再重试读取配置并连接成功。
    [Fact]
    public async Task ConnectRecoversWhenDeviceIsStuckStreaming()
    {
        var endpoint = new FakeSerialEndpoint();
        endpoint.ForceMode(ProtocolConstants.DeviceMode.Streaming);
        var viewModel = new MainViewModel(_ => { var t = new SerialTransport(endpoint); return (t, t); });
        viewModel.SelectedPort = "COM_TEST";

        await viewModel.RunConnectForTestAsync();

        Assert.True(viewModel.Status == ConnectionStatus.Connected, $"status={viewModel.Status} msg={viewModel.StatusMessage}");
        Assert.Contains(endpoint.Requests, request => request.Type == (byte)ProtocolConstants.FrameType.StreamStop);
    }

    // 回归：固件在 CONFIG_IDLE 空闲 30 s 后会静默退回 GAME（配置帧被丢弃且不回报错），
    // 用户「改完映射过一会儿再点保存」就会看到 1 s 超时。传输层必须自动补发 ENTER_CFG 再重试，
    // 让调用方完全感知不到这次掉线。
    [Fact]
    public async Task ConfigRequestsRecoverAfterTheFirmwareSilentlyReturnsToGameMode()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = await EnterConfigAsync(endpoint, TimeSpan.FromMilliseconds(150));
        var workflow = new ConfigurationWorkflow(transport);

        // 模拟空闲超时：板子已经回到 GAME，工具这边毫不知情。
        endpoint.ForceMode(ProtocolConstants.DeviceMode.Game);

        var changed = ConfigBlob.Defaults() with { ElectrodeOffsets = ConfigBlob.Defaults().ElectrodeOffsets.Select((value, index) => index == 0 ? (sbyte)9 : value).ToArray() };
        await workflow.ApplyAsync(changed);
        await workflow.SaveAsync();

        Assert.Equal(ProtocolConstants.DeviceMode.ConfigIdle, endpoint.Mode);
        Assert.Equal(changed.Encode(), endpoint.PersistedConfig.Encode());
        Assert.Contains(endpoint.Requests, request => request.Type == (byte)ProtocolConstants.FrameType.EnterConfig);
    }

    // 回归：单条命令失败但端口还在时，界面不能进入「错误」态（等同断开、按钮全禁用却仍占着 COM 口）。
    [Fact]
    public async Task FailedSaveKeepsTheSessionUsableInsteadOfGoingToError()
    {
        var endpoint = new FakeSerialEndpoint();
        endpoint.ForceMode(ProtocolConstants.DeviceMode.ConfigIdle);
        var viewModel = new MainViewModel(_ => { var t = new SerialTransport(endpoint) { CommandTimeout = TimeSpan.FromMilliseconds(150) }; return (t, t); });
        viewModel.SelectedPort = "COM_TEST";
        await viewModel.RunConnectForTestAsync();
        Assert.True(viewModel.IsConnected);

        // 让 SAVE_CFG 既超时、重试也失败：ENTER_CFG 之后设备装死不响应。
        endpoint.SuppressResponses = true;
        await viewModel.RunSaveForTestAsync();

        Assert.Equal(ConnectionStatus.Connected, viewModel.Status);
        Assert.True(viewModel.DisconnectCommand.CanExecute(null));
        Assert.True(viewModel.ReadbackCommand.CanExecute(null));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusMessage));
    }

    // 回归：映射页的真实顺序是「停流（只 Cancel，不等任务结束）→ 立刻 Apply」。
    // 如果流循环和随后的配置请求各自读同一个端口，APPLY_CFG 的 ACK 会被 STREAM_STOP 的读循环吞掉，
    // 表现为随机 1 s 超时。传输层必须让整段流（含收尾 STREAM_STOP）独占端口。
    [Fact]
    public async Task ApplyRightAfterStoppingTheStreamDoesNotLoseTheAck()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = await EnterConfigAsync(endpoint);
        var workflow = new ConfigurationWorkflow(transport);
        var config = ConfigBlob.Defaults();

        for (var round = 0; round < 3; round++)
        {
            using var streaming = new CancellationTokenSource();
            var streamTask = Task.Run(async () =>
            {
                try { await transport.StreamDebugEventsAsync(100, _ => { }, _ => { }, streaming.Token, _ => { }); }
                catch (OperationCanceledException) { }
            });
            await Task.Delay(30);
            streaming.Cancel(); // 故意不等 streamTask，与 EndMappingStream 一致
            await workflow.ApplyAsync(config);
            await workflow.SaveAsync();
            await streamTask;
        }

        Assert.Equal(config.Encode(), endpoint.PersistedConfig.Encode());
    }

    // 游戏模式：进入后固件以 9 字节游戏帧流式输出；工具解析这些帧驱动 34 键触摸反馈。
    [Fact]
    public async Task GameModeStreamsRawFramesAndDrivesTouchKeys()
    {
        var endpoint = new FakeSerialEndpoint();
        endpoint.ForceMode(ProtocolConstants.DeviceMode.ConfigIdle);
        var viewModel = new MainViewModel(_ => { var t = new SerialTransport(endpoint); return (t, t); });
        viewModel.SelectedPort = "COM_TEST";
        await viewModel.RunConnectForTestAsync();
        Assert.True(viewModel.IsConnected);

        // 进入游戏模式：发 EXIT_CFG，板子切到 GAME 并开始输出 9 字节帧。
        var gameTask = viewModel.RunGameModeForTestAsync();
        await WaitFor(() => endpoint.Requests.Exists(r => r.Type == (byte)ProtocolConstants.FrameType.ExitConfig));
        await WaitFor(() => endpoint.Mode == ProtocolConstants.DeviceMode.Game);

        // 固件约定：key k -> 34 位值的 bit 33-k。按下 A1（key 0）与 C1（key 24，bit 9）。
        endpoint.InjectRaw(BuildGameFrame(1UL << 33 | 1UL << 9));
        await WaitFor(() => viewModel.TouchKeys[0].IsPressed && viewModel.TouchKeys[24].IsPressed);

        Assert.False(viewModel.TouchKeys[1].IsPressed);
        // 游戏模式/调试流期间不允许 Apply/Save/Readback：端口被流循环独占，另外发请求只会超时。
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        Assert.False(viewModel.ReadbackCommand.CanExecute(null));

        await viewModel.RunStopGameModeForTestAsync();
        await gameTask;
        Assert.False(viewModel.IsGameMode);
        // 停止游戏模式后板子应回到配置态（ENTER_CFG）。
        Assert.Contains(endpoint.Requests, request => request.Type == (byte)ProtocolConstants.FrameType.EnterConfig);
    }

    private static byte[] BuildGameFrame(ulong value)
    {
        var frame = new byte[9];
        frame[0] = (byte)'(';
        for (var chunk = 0; chunk < 7; chunk++) frame[1 + chunk] = (byte)((value >> (5 * chunk)) & 0x1F);
        frame[8] = (byte)')';
        return frame;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition was not met within the timeout.");
    }
}