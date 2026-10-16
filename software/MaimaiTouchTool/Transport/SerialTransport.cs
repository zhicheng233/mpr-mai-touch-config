using System.IO.Ports;
using System.Buffers.Binary;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.Transport;

public enum TransportState { Disconnected, Connecting, Config, Streaming, Calibrating }
public readonly record struct DebugFrameEvent(ushort Sequence, DebugFrame Frame, DateTimeOffset ReceivedAt);
public readonly record struct StreamStartInfo(ushort AcceptedRateHz, ushort ActualRateHz);
public interface IByteStream : IAsyncDisposable
{
    Task OpenAsync(CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    // 强制中断进行中的读写（用于“取消连接”）：关闭端口即可让阻塞的读/写立即抛异常返回。
    void Abort();
}
public sealed class SerialByteStream(string portName, int baudRate = 115200) : IByteStream
{
    private SerialPort? port;
    private readonly object gate = new();
    // 可选诊断：设置环境变量 MAIMAI_SERIAL_LOG=1 可把串口收发与请求/响应写入 %TEMP%\maimai_serial.log。
    private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("MAIMAI_SERIAL_LOG") == "1";
    private static readonly object TraceGate = new();
    internal static void Trace(string message)
    {
        if (!TraceEnabled) return;
        try { lock (TraceGate) File.AppendAllText(Path.Combine(Path.GetTempPath(), "maimai_serial.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); } catch { }
    }
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        Trace($"Open {portName} start");
        // DTR/RTS：部分板子（如 SAMD21）在开串口时会因 DTR 变化而复位。保持稳定电平避免误复位。
        var created = new SerialPort(portName, baudRate) { DtrEnable = true, RtsEnable = true, ReadTimeout = 250, WriteTimeout = 250 };
        // SerialPort.Open() 是同步阻塞调用，放到线程池避免卡死 UI 线程。
        try { await Task.Run(created.Open, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { Trace($"Open {portName} FAILED {ex.GetType().Name}: {ex.Message}"); created.Dispose(); throw; }
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) port = created;
        Trace($"Open {portName} ok");
    }
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        SerialPort? local;
        lock (gate) { local = port; port = null; }
        // Close() 在后台阻塞读线程仍卡住时不会立即归还句柄，必须 Dispose() 才能真正释放 COM 口，
        // 否则下一次 Open 会报 “Access to the path 'COMx' is denied”。
        try { local?.Close(); } catch { }
        try { local?.Dispose(); } catch { }
        Trace("Close");
        return Task.CompletedTask;
    }
    // .NET 的 SerialPort.BaseStream.ReadAsync/WriteAsync 不保证响应 CancellationToken（实测在安静的口上永久挂起），
    // 因此改用同步 Read（受 ReadTimeout 约束）在线程池执行：
    //   - ReadTimeout 到期（无数据）视为“暂时空闲”，继续等待，直到收到字节或 token 取消；
    //   - 端口被关闭/断开 → 返回 0（真正的“已断开”）；
    //   - token 取消 → 抛 OperationCanceledException。
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Length == 0) return 0;
        var scratch = new byte[buffer.Length];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = Current();
            if (local is null || !local.IsOpen) return 0;
            int count;
            try { count = await Task.Run(() => local.Read(scratch, 0, scratch.Length), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { continue; }                 // 本轮无数据：继续等待，不算断开
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException) { return 0; }        // 端口已被关闭/Abort
            catch (IOException) { return 0; }                      // 设备断开
            if (count <= 0) return 0;
            scratch.AsSpan(0, count).CopyTo(buffer.Span);
            return count;
        }
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var local = Current();
        if (local is null || !local.IsOpen) throw new IOException("Serial port is not open.");
        var array = bytes.ToArray();
        try { await Task.Run(() => local.Write(array, 0, array.Length), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw new IOException("Serial port was aborted."); }
    }
    public void Abort()
    {
        SerialPort? local;
        lock (gate) local = port;
        // 仅 Close 即可让阻塞中的 Read 立即抛 InvalidOperationException 返回；
        // 真正的句柄释放交给后续的 CloseAsync()（它会 Dispose）。
        try { local?.Close(); } catch { }
    }
    private SerialPort? Current() { lock (gate) return port; }
    public ValueTask DisposeAsync() { SerialPort? local; lock (gate) { local = port; port = null; } try { local?.Dispose(); } catch { } return ValueTask.CompletedTask; }
}
public sealed class SerialTransport(IByteStream stream) : IEventRequestClient, IAsyncDisposable
{
    public TransportState State { get; private set; } = TransportState.Disconnected;
    // 释放底层字节流（真正归还 COM 句柄）。CloseAsync 已 Dispose，这里再兜底一次。
    public async ValueTask DisposeAsync() { try { await stream.DisposeAsync(); } catch { } }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public async Task ConnectAsync(CancellationToken cancellationToken = default) { if (State != TransportState.Disconnected) throw new InvalidOperationException("Already connected."); State = TransportState.Connecting; try { await stream.OpenAsync(cancellationToken); await SettleAsync(cancellationToken); State = TransportState.Config; } catch { State = TransportState.Disconnected; throw; } }
    // 开串口后板子可能要复位/做 ~1s 开机校准，先排空这段时间的（游戏）输出，避免后续命令被丢弃或与游戏帧混淆。
    // 按“时间窗口”排空，而不是遇到空闲就停（空闲由 ReadTimeout 的 250ms 体现）。
    private async Task SettleAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1200);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(remaining);
            try
            {
                var count = await stream.ReadAsync(buffer, window.Token);
                if (count == 0 && !cancellationToken.IsCancellationRequested) return; // 真正断开
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; } // 排空窗口到期
        }
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default) { stream.Abort(); await stream.CloseAsync(cancellationToken); State = TransportState.Disconnected; }
    // 命令写入：受 CommandTimeout 约束。超时不拆端口；仅用户取消才 Abort。
    public async Task SendAsync(ProtocolFrame frame, CancellationToken cancellationToken = default)
    {
        if (State is TransportState.Disconnected or TransportState.Connecting) throw new InvalidOperationException("Transport is not ready.");
        await WriteWithTimeoutAsync(frame.Encode(), cancellationToken);
    }
    private async Task WriteWithTimeoutAsync(byte[] encoded, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        using var abortOnUserCancel = cancellationToken.Register(static state => ((IByteStream)state!).Abort(), stream);
        try { await stream.WriteAsync(encoded, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Serial write timed out."); }
    }
    public async Task<ProtocolFrame> SendRequestAsync(byte type, byte[] payload, CancellationToken cancellationToken = default)
    {
        if (State is TransportState.Disconnected or TransportState.Connecting) throw new InvalidOperationException("Transport is not ready.");
        var sequence = NextSequence();
        await SendAsync(new ProtocolFrame(type, sequence, payload), cancellationToken);
        SerialByteStream.Trace($"-> request type=0x{type:X2} seq={sequence}");
        var parser = new FrameParser();
        var buffer = new byte[512];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        using var abortOnUserCancel = cancellationToken.Register(static state => ((IByteStream)state!).Abort(), stream);
        try
        {
            while (true)
            {
                int count;
                try { count = await stream.ReadAsync(buffer, timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException($"No response to frame type {type} within {CommandTimeout.TotalSeconds:0.#}s."); }
                // Abort() 会让读返回 0；若其实是用户取消，统一以 OperationCanceledException 呈现。
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (count == 0) throw new IOException("The serial device disconnected.");
                foreach (var response in parser.Push(buffer.AsMemory(0, count), DateTimeOffset.UtcNow))
                    if (response.Sequence == sequence && (response.Type == (byte)ProtocolConstants.FrameType.Ack || response.Type == (byte)ProtocolConstants.FrameType.Error))
                    { SerialByteStream.Trace($"<- response type=0x{response.Type:X2} seq={response.Sequence} payload={Convert.ToHexString(response.Payload.AsSpan(0, Math.Min(response.Payload.Length, 8)))}"); return response; }
            }
        }
        catch (TimeoutException) { SerialByteStream.Trace($"<- TIMEOUT type=0x{type:X2}"); throw; }          // 超时不拆端口，保持可继续使用
        catch (OperationCanceledException) { SerialByteStream.Trace($"<- CANCELLED type=0x{type:X2}"); State = TransportState.Disconnected; throw; }
        catch (Exception ex) { SerialByteStream.Trace($"<- FAILED type=0x{type:X2} {ex.GetType().Name}: {ex.Message}"); State = TransportState.Disconnected; throw; }
    }
    public async Task<(ProtocolFrame Ack, ProtocolFrame Event)> SendRequestAndWaitForEventAsync(byte type, byte[] payload, byte eventType, TimeSpan eventTimeout, CancellationToken cancellationToken = default)
    {
        if (State is TransportState.Disconnected or TransportState.Connecting) throw new InvalidOperationException("Transport is not ready.");
        var requestSequence = NextSequence(); SetOperationState(TransportState.Calibrating); await SendAsync(new ProtocolFrame(type, requestSequence, payload), cancellationToken);
        var parser = new FrameParser(); var buffer = new byte[512]; ProtocolFrame? ack = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(eventTimeout);
        using var abortOnUserCancel = cancellationToken.Register(static state => ((IByteStream)state!).Abort(), stream);
        try
        {
            while (true)
            {
                int count;
                try { count = await stream.ReadAsync(buffer, timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException($"No completion event within {eventTimeout.TotalSeconds:0.#}s."); }
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (count == 0) throw new IOException("The serial device disconnected.");
                foreach (var response in parser.Push(buffer.AsMemory(0, count), DateTimeOffset.UtcNow))
                {
                    if (response.Sequence == requestSequence && response.Type is (byte)ProtocolConstants.FrameType.Ack or (byte)ProtocolConstants.FrameType.Error)
                    {
                        if (response.Type == (byte)ProtocolConstants.FrameType.Error) return (response, response);
                        ack = response;
                    }
                    if (ack is { } accepted && response.Sequence == requestSequence && response.Type == eventType) return (accepted, response);
                }
            }
        }
        catch (TimeoutException) { throw; }
        catch { State = TransportState.Disconnected; throw; }
        finally { if (State == TransportState.Calibrating) SetOperationState(TransportState.Config); }
    }
    public async Task StreamDebugAsync(ushort requestedRateHz, Action<DebugFrame> onFrame, CancellationToken cancellationToken = default)
    {
        await StreamDebugEventsAsync(requestedRateHz, sample => onFrame(sample.Frame), _ => { }, cancellationToken);
    }
    public async Task<StreamStartInfo> StreamDebugEventsAsync(ushort requestedRateHz, Action<DebugFrameEvent> onFrame, Action<Exception> onError, CancellationToken cancellationToken = default, Action<StreamStartInfo>? onStarted = null)
    {
        var startPayload = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(startPayload, requestedRateHz);
        var ackFrame = await SendRequestAsync((byte)ProtocolConstants.FrameType.StreamStart, startPayload, cancellationToken);
        if (ackFrame.Type == (byte)ProtocolConstants.FrameType.Error) { var e = ErrorPayload.Decode(ackFrame.Payload); throw new InvalidOperationException($"STREAM_START was rejected: {Enum.GetName((ProtocolConstants.ErrorStatus)e.Status) ?? $"status {e.Status}"}."); }
        var ack = AckPayload.Decode(ackFrame.Payload); if (ack.Status != 0) throw new InvalidOperationException($"STREAM_START was rejected: {Enum.GetName((ProtocolConstants.ErrorStatus)ack.Status) ?? $"status {ack.Status}"}.");
        var rate = StreamStartResult.Decode(ack.Result);
        if (rate.AcceptedRateHz == 0 || rate.ActualRateHz == 0) throw new FormatException("STREAM_START returned no measurable rate.");
        var startInfo = new StreamStartInfo(rate.AcceptedRateHz, rate.ActualRateHz);
        onStarted?.Invoke(startInfo);
        SetOperationState(TransportState.Streaming); var parser = new FrameParser(); var buffer = new byte[512];
        // 注意：这里不注册 abortOnUserCancel —— “停止流”是优雅停止：取消读循环后仍需在同一个（未关闭的）端口上发 STREAM_STOP。
        // ReadAsync 会在两次 ReadTimeout（≤250ms）之间检查 token，因此取消延迟很小。
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken); if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken); if (count == 0) throw new IOException("The serial device disconnected.");
                foreach (var response in parser.Push(buffer.AsMemory(0, count), DateTimeOffset.UtcNow))
                {
                    if (response.Type != (byte)ProtocolConstants.FrameType.DebugFrame) continue;
                    try { onFrame(new DebugFrameEvent(response.Sequence, DebugFrame.Decode(response.Payload), DateTimeOffset.UtcNow)); }
                    catch (FormatException error) { onError(error); }
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetOperationState(TransportState.Config);
            try { await SendRequestAsync((byte)ProtocolConstants.FrameType.StreamStop, [], CancellationToken.None); } catch { }
        }
        catch { State = TransportState.Disconnected; throw; }
        return startInfo;
    }
    public void SetOperationState(TransportState state) { if (state is TransportState.Disconnected or TransportState.Connecting) throw new ArgumentOutOfRangeException(nameof(state)); State = state; }
    // GAME 模式：固件在退出配置模式后直接输出 9 字节传统游戏帧（'('+7+')'），约几百 Hz。
    // 只读解析，不发命令；取消时不拆端口，调用方负责用 ENTER_CFG 把板子拉回配置模式。
    public async Task StreamGameFramesAsync(Action<ulong> onTouchBits, CancellationToken cancellationToken = default)
    {
        if (State is TransportState.Disconnected or TransportState.Connecting) throw new InvalidOperationException("Transport is not ready.");
        SetOperationState(TransportState.Streaming);
        var parser = new GameFrameParser();
        var buffer = new byte[512];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (count == 0) throw new IOException("The serial device disconnected.");
                foreach (var bits in parser.Push(buffer, count)) onTouchBits(bits);
            }
        }
        catch (OperationCanceledException) { SetOperationState(TransportState.Config); }
        catch { State = TransportState.Disconnected; throw; }
    }
    private ushort sequence;
    private ushort NextSequence() => ++sequence;
}
public interface IRequestClient { Task<ProtocolFrame> SendRequestAsync(byte type, byte[] payload, CancellationToken cancellationToken = default); }
public interface IEventRequestClient : IRequestClient
{
    Task<(ProtocolFrame Ack, ProtocolFrame Event)> SendRequestAndWaitForEventAsync(byte type, byte[] payload, byte eventType, TimeSpan eventTimeout, CancellationToken cancellationToken = default);
    Task StreamDebugAsync(ushort requestedRateHz, Action<DebugFrame> onFrame, CancellationToken cancellationToken = default);
    Task StreamGameFramesAsync(Action<ulong> onTouchBits, CancellationToken cancellationToken = default);
}
public sealed class ConfigurationWorkflow(IRequestClient client)
{
    public async Task<ConfigBlob> ApplyAsync(ConfigBlob config, CancellationToken cancellationToken = default)
    {
        await ExpectAckAsync(ProtocolConstants.FrameType.SetConfig, config.Encode(), cancellationToken);
        await ExpectAckAsync(ProtocolConstants.FrameType.ApplyConfig, [], cancellationToken);
        var readback = await ExpectAckAsync(ProtocolConstants.FrameType.GetConfig, [], cancellationToken);
        if (readback.Result.Length != ProtocolConstants.ConfigBlobLength) throw new FormatException("GET_CFG returned an invalid configuration length.");
        var applied = ConfigBlob.Decode(readback.Result);
        if (!applied.Encode().AsSpan().SequenceEqual(config.Encode())) throw new InvalidDataException("Device readback differs from the applied configuration.");
        return applied;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default) => await ExpectAckAsync(ProtocolConstants.FrameType.SaveConfig, [], cancellationToken);

    public async Task<ProtocolFrame> RequestAsync(ProtocolConstants.FrameType type, byte[] payload, CancellationToken cancellationToken = default) => await client.SendRequestAsync((byte)type, payload, cancellationToken);

    private async Task<AckPayload> ExpectAckAsync(ProtocolConstants.FrameType type, byte[] payload, CancellationToken cancellationToken)
    {
        var response = await client.SendRequestAsync((byte)type, payload, cancellationToken);
        if (response.Type == (byte)ProtocolConstants.FrameType.Error)
        {
            var error = ErrorPayload.Decode(response.Payload);
            throw new InvalidOperationException($"{type} failed: {Enum.GetName((ProtocolConstants.ErrorStatus)error.Status) ?? $"status {error.Status}"}.");
        }
        if (response.Type != (byte)ProtocolConstants.FrameType.Ack) throw new FormatException($"{type} returned an unexpected response type.");
        var ack = AckPayload.Decode(response.Payload);
        if (ack.RequestType != (byte)type) throw new FormatException($"Response request type does not match {type}.");
        if (ack.Status != 0) throw new InvalidOperationException($"{type} was rejected with status {ack.Status}.");
        return ack;
    }
}
