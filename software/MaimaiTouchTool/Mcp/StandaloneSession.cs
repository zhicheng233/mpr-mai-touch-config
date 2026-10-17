using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.ViewModels;

namespace MaimaiTouchTool.Mcp;

/// <summary>
/// stdio 模式下的会话：自己打开串口，暂存态放在本进程内存里。
/// 与 GUI 完全独立 —— GUI 若已连着同一个口，这里连不上（COM 口本来就是独占的）。
/// </summary>
public sealed class StandaloneSession : IMcpSession, IAsyncDisposable
{
    private SerialTransport? transport;
    private ConfigurationWorkflow? workflow;
    private ConfigBlob staged = ConfigBlob.Defaults();
    private ConfigBlob applied = ConfigBlob.Defaults();

    public bool IsConnected => workflow is not null && transport?.State == TransportState.Config;
    public string PortName { get; private set; } = string.Empty;
    public ConfigBlob AppliedConfig => applied;
    public ConfigBlob StagedConfig => staged;

    public async Task<string> ConnectAsync(string port, CancellationToken cancellationToken)
    {
        // 重复 connect 时先释放上一个串口，避免句柄泄漏。
        await DisconnectAsync(cancellationToken);

        var serial = new SerialTransport(new SerialByteStream(port));
        transport = serial;
        workflow = new ConfigurationWorkflow(serial);
        PortName = port;
        try
        {
            await serial.ConnectAsync(cancellationToken);
            await serial.SendRequestAsync((byte)ProtocolConstants.FrameType.Version, [], cancellationToken);
            // ENTER_CFG：容忍 AlreadyConfigured（板子已在配置模式）。
            var enter = await serial.SendRequestAsync((byte)ProtocolConstants.FrameType.EnterConfig, [], cancellationToken);
            if (enter.Type == (byte)ProtocolConstants.FrameType.Error)
            {
                var error = ErrorPayload.Decode(enter.Payload);
                if ((ProtocolConstants.ErrorStatus)error.Status != ProtocolConstants.ErrorStatus.AlreadyConfigured)
                    throw new InvalidOperationException($"ENTER_CFG rejected: {Enum.GetName((ProtocolConstants.ErrorStatus)error.Status)}");
            }
            applied = staged = await ReadConfigAsync(cancellationToken);
        }
        catch
        {
            transport = null; workflow = null; PortName = string.Empty;
            try { await serial.DisposeAsync(); } catch { }
            throw;
        }
        return $"connected to {port}, mode=config, boards=4, electrodes=34";
    }

    public async Task<string> DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (workflow is not null)
            {
                try { await workflow.RequestAsync(ProtocolConstants.FrameType.ExitConfig, [], cancellationToken); } catch { }
            }
        }
        finally
        {
            await DisposeTransportAsync();
            PortName = string.Empty;
        }
        return "disconnected";
    }

    private async Task DisposeTransportAsync()
    {
        if (transport is not null) { try { await transport.DisconnectAsync(); } catch { } }
        if (transport is IAsyncDisposable disposable) { try { await disposable.DisposeAsync(); } catch { } }
        transport = null; workflow = null;
    }

    private async Task<ConfigBlob> ReadConfigAsync(CancellationToken cancellationToken)
    {
        var frame = await workflow!.RequestAsync(ProtocolConstants.FrameType.GetConfig, [], cancellationToken);
        if (frame.Type == (byte)ProtocolConstants.FrameType.Error) throw new InvalidOperationException("GET_CFG rejected (streaming? send disconnect then reconnect)");
        var ack = AckPayload.Decode(frame.Payload);
        if (ack.Result.Length != ProtocolConstants.ConfigBlobLength) throw new FormatException("GET_CFG malformed.");
        return ConfigBlob.Decode(ack.Result);
    }

    public async Task<string> ApplyAsync(CancellationToken cancellationToken)
    {
        RequireWorkflow("apply");
        applied = await workflow!.ApplyAsync(staged, cancellationToken);
        staged = applied;
        return $"applied & verified: {System.Text.Json.JsonSerializer.Serialize(McpToolHost.ConfigJson(applied))}";
    }

    public async Task<string> SaveAsync(CancellationToken cancellationToken)
    {
        RequireWorkflow("save");
        await workflow!.SaveAsync(cancellationToken);
        return "saved to EEPROM";
    }

    public async Task<string> CalibrateAsync(CancellationToken cancellationToken)
    {
        if (transport is not IEventRequestClient events) throw new InvalidOperationException("transport does not support events");
        var result = await events.SendRequestAndWaitForEventAsync(
            (byte)ProtocolConstants.FrameType.Calibrate, new CalibratePayload(0, 0).Encode(),
            (byte)ProtocolConstants.FrameType.CalDone, TimeSpan.FromSeconds(61), cancellationToken);
        if (result.Ack.Type == (byte)ProtocolConstants.FrameType.Error)
            throw new InvalidOperationException($"CALIBRATE rejected: {ErrorPayload.Decode(result.Ack.Payload).Status}");
        var done = CalDonePayload.Decode(result.Event.Payload);
        // 校准只改 RAM 里已应用的阈值，读回来让暂存态跟上，否则后续 apply 会覆盖掉校准结果。
        try { applied = staged = await ReadConfigAsync(cancellationToken); } catch { }
        return done.Status == CalibrationStatus.Success
            ? $"calibration succeeded (reason={done.Reason})"
            : $"calibration failed (reason={done.Reason})";
    }

    public async Task<string> ReadTelemetryAsync(TelemetryRequest request, CancellationToken cancellationToken)
    {
        if (transport is null) throw new InvalidOperationException("not connected");
        var report = new TelemetryAccumulator(applied, request);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.DurationMs);
        try
        {
            await transport.StreamDebugEventsAsync(100, sample => report.Add(sample.Frame), _ => { }, cts.Token, _ => { });
        }
        catch (OperationCanceledException) { }
        return System.Text.Json.JsonSerializer.Serialize(report.ToJson(request.DurationMs));
    }

    public void StageBoard(int boardIndex, BoardEdit edit)
    {
        var boards = staged.Boards.ToArray();
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
        staged = staged with { Boards = boards };
    }

    public void StageElectrode(int keyIndex, int offset)
    {
        var offsets = staged.ElectrodeOffsets.ToArray();
        offsets[keyIndex] = (sbyte)offset;
        staged = staged with { ElectrodeOffsets = offsets };
    }

    public void StageMapping(int keyIndex, int boardIndex, int channel, bool swap) =>
        staged = staged with { KeyMap = McpMapping.Change(staged.Mapping, keyIndex, boardIndex, channel, swap) };

    private void RequireWorkflow(string operation)
    {
        if (workflow is null) throw new InvalidOperationException($"not connected; call connect first (needed for {operation})");
    }

    public async ValueTask DisposeAsync() => await DisposeTransportAsync();
}
