using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.ViewModels;

namespace MaimaiTouchTool.Tests;

internal sealed class FakeSerialEndpoint : IByteStream
{
    private readonly Channel<byte[]> output = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Queue<ProtocolFrame> responseHistory = new();
    private readonly Dictionary<ushort, (byte Type, ushort PayloadCrc, byte[] EncodedResponse)> sequenceCache = [];
    private readonly object gate = new();
    private byte[]? pending;
    private ConfigBlob applied = ConfigBlob.Defaults();
    private ConfigBlob staged = ConfigBlob.Defaults();
    private ConfigBlob persisted = ConfigBlob.Defaults();
    private ProtocolConstants.DeviceMode mode = ProtocolConstants.DeviceMode.Game;
    private bool opened;
    private bool closed;

    public const int MaxHistory = 64;
    public List<ProtocolFrame> Requests { get; } = [];
    public List<byte[]> RejectedRawFrames { get; } = [];
    public bool SuppressResponses { get; set; }
    public bool IsStreaming => mode == ProtocolConstants.DeviceMode.Streaming;
    public ProtocolConstants.DeviceMode Mode => mode;
    // 测试用：模拟“上次工具被强杀后板子残留的模式”（非 Game）。
    public void ForceMode(ProtocolConstants.DeviceMode value) => mode = value;
    public ConfigBlob AppliedConfig => ConfigBlob.Decode(applied.Encode());
    public ConfigBlob PersistedConfig => ConfigBlob.Decode(persisted.Encode());
    public TaskCompletionSource<ProtocolFrame> StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        opened = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Disconnect();
        return Task.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!opened || closed) throw new IOException("Fake serial endpoint is closed.");
        var raw = bytes.ToArray();
        ProtocolFrame request;
        try
        {
            request = ProtocolFrame.Decode(raw);
        }
        catch (FormatException)
        {
            RejectedRawFrames.Add(raw);
            if (raw.Length >= ProtocolConstants.HeaderLength && raw[0] == ProtocolConstants.Sync0 && raw[1] == ProtocolConstants.Sync1)
            {
                var sequence = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(4));
                Enqueue(new ProtocolFrame((byte)ProtocolConstants.FrameType.Error, sequence, new ErrorPayload(raw[3], (byte)ProtocolConstants.ErrorStatus.BadFrame, 0).Encode()));
            }
            return ValueTask.CompletedTask;
        }

        Requests.Add(request);
        Handle(request, raw);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (pending is null || pending.Length == 0)
        {
            if (!await output.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!output.Reader.TryRead(out pending)) continue;
        }

        var available = pending!;
        var count = Math.Min(buffer.Length, available.Length);
        available.AsMemory(0, count).CopyTo(buffer);
        pending = available[count..];
        return count;
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }

    public void Abort() => Disconnect();

    public void Disconnect()
    {
        closed = true;
        output.Writer.TryComplete();
    }

    public void InjectRaw(ReadOnlySpan<byte> raw)
    {
        var bytes = raw.ToArray();
        try { _ = ProtocolFrame.Decode(bytes); }
        catch (FormatException) { RejectedRawFrames.Add(bytes); }
        EnqueueRaw(bytes);
    }

    public void InjectResponse(ProtocolFrame frame) => Enqueue(frame);

    public void InjectDebug(ushort sequence, DebugFrame frame)
    {
        if (!IsStreaming) throw new InvalidOperationException("Debug frames require STREAMING mode.");
        Enqueue(new ProtocolFrame((byte)ProtocolConstants.FrameType.DebugFrame, sequence, frame.Encode()));
    }

    public IReadOnlyList<ProtocolFrame> ResponseHistory
    {
        get { lock (gate) return responseHistory.ToArray(); }
    }

    private void Handle(ProtocolFrame request, byte[] raw)
    {
        var requestCrc = Crc16Ccitt.Compute(request.Payload);
        lock (gate)
        {
            if (sequenceCache.TryGetValue(request.Sequence, out var cached))
            {
                if (cached.Type == request.Type && cached.PayloadCrc == requestCrc) EnqueueRaw(cached.EncodedResponse);
                else Enqueue(new ProtocolFrame((byte)ProtocolConstants.FrameType.Error, request.Sequence, new ErrorPayload(request.Type, (byte)ProtocolConstants.ErrorStatus.SequenceConflict, 0).Encode()));
                return;
            }
        }

        var type = (ProtocolConstants.FrameType)request.Type;
        if (mode == ProtocolConstants.DeviceMode.Game && type is not ProtocolConstants.FrameType.Version and not ProtocolConstants.FrameType.EnterConfig)
            return;
        // 固件顺序：EnterConfig（非 Game 时）先于 STREAMING 的 Busy 守卫处理，返回 AlreadyConfigured。
        if (mode != ProtocolConstants.DeviceMode.Game && type == ProtocolConstants.FrameType.EnterConfig && request.Payload.Length == 0)
        {
            Reply(request, Error(request, ProtocolConstants.ErrorStatus.AlreadyConfigured));
            return;
        }
        if (mode == ProtocolConstants.DeviceMode.Streaming && type is not ProtocolConstants.FrameType.Version and not ProtocolConstants.FrameType.StreamStop and not ProtocolConstants.FrameType.ExitConfig)
        {
            Reply(request, Error(request, ProtocolConstants.ErrorStatus.Busy));
            return;
        }

        ProtocolFrame response;
        switch (type)
        {
            case ProtocolConstants.FrameType.Version when request.Payload.Length == 0:
                response = Ack(request, [1, 0, 1, 2, 3, 0x07, 0, 0, 0, 0, 1, 48, 34, 4, (byte)mode, 1]);
                break;
            case ProtocolConstants.FrameType.EnterConfig when request.Payload.Length == 0 && mode == ProtocolConstants.DeviceMode.Game:
                mode = ProtocolConstants.DeviceMode.ConfigIdle; staged = ConfigBlob.Decode(applied.Encode()); response = Ack(request); break;
            case ProtocolConstants.FrameType.ExitConfig when request.Payload.Length == 0:
                mode = ProtocolConstants.DeviceMode.Game; staged = ConfigBlob.Decode(applied.Encode()); response = Ack(request); break;
            case ProtocolConstants.FrameType.GetConfig when request.Payload.Length == 0 && mode == ProtocolConstants.DeviceMode.ConfigIdle:
                response = Ack(request, applied.Encode()); break;
            case ProtocolConstants.FrameType.SetConfig when request.Payload.Length == ProtocolConstants.ConfigBlobLength && mode == ProtocolConstants.DeviceMode.ConfigIdle:
                staged = ConfigBlob.Decode(request.Payload); response = Ack(request); break;
            case ProtocolConstants.FrameType.ApplyConfig when request.Payload.Length == 0 && mode == ProtocolConstants.DeviceMode.ConfigIdle:
                applied = ConfigBlob.Decode(staged.Encode()); response = Ack(request); break;
            case ProtocolConstants.FrameType.SaveConfig when request.Payload.Length == 0 && mode == ProtocolConstants.DeviceMode.ConfigIdle:
                persisted = ConfigBlob.Decode(applied.Encode()); response = Ack(request); break;
            case ProtocolConstants.FrameType.Calibrate when request.Payload.Length == 3 && mode == ProtocolConstants.DeviceMode.ConfigIdle:
                _ = CalibratePayload.Decode(request.Payload); mode = ProtocolConstants.DeviceMode.Calibrating; response = Ack(request, BitConverter.GetBytes(100u)); break;
            case ProtocolConstants.FrameType.StreamStart when request.Payload.Length == 2 && mode == ProtocolConstants.DeviceMode.ConfigIdle:
                var requested = BinaryPrimitives.ReadUInt16LittleEndian(request.Payload);
                var accepted = requested == 0 ? (ushort)100 : requested;
                mode = ProtocolConstants.DeviceMode.Streaming;
                response = Ack(request, BitConverter.GetBytes(accepted).Concat(BitConverter.GetBytes(accepted)).ToArray());
                break;
            case ProtocolConstants.FrameType.StreamStop when request.Payload.Length == 0 && mode == ProtocolConstants.DeviceMode.Streaming:
                mode = ProtocolConstants.DeviceMode.ConfigIdle; response = Ack(request); break;
            default:
                response = Error(request, request.Payload.Length == 0 ? ProtocolConstants.ErrorStatus.IllegalState : ProtocolConstants.ErrorStatus.BadLength);
                break;
        }

        Reply(request, response);
        if (type == ProtocolConstants.FrameType.Calibrate && response.Type == (byte)ProtocolConstants.FrameType.Ack)
        {
            mode = ProtocolConstants.DeviceMode.ConfigIdle;
            var baseline = Enumerable.Repeat((ushort)700, 48).ToArray();
            var thresholds = Enumerable.Repeat((ushort)20, 48).ToArray();
            Enqueue(new ProtocolFrame((byte)ProtocolConstants.FrameType.CalDone, request.Sequence, new CalDonePayload(CalibrationStatus.Success, CalibrationReason.None, baseline, thresholds, []).Encode()));
        }
        if (type == ProtocolConstants.FrameType.StreamStart) StreamStarted.TrySetResult(response);
    }

    private void Reply(ProtocolFrame request, ProtocolFrame response)
    {
        if (SuppressResponses) return;
        var encoded = response.Encode();
        lock (gate)
        {
            sequenceCache[request.Sequence] = (request.Type, Crc16Ccitt.Compute(request.Payload), encoded);
            responseHistory.Enqueue(response);
            while (responseHistory.Count > MaxHistory) responseHistory.Dequeue();
        }
        EnqueueRaw(encoded);
    }

    private ProtocolFrame Ack(ProtocolFrame request, byte[] result = null!) => new((byte)ProtocolConstants.FrameType.Ack, request.Sequence, new AckPayload(request.Type, 0, (byte)mode, 0, result ?? []).Encode());
    private static ProtocolFrame Error(ProtocolFrame request, ProtocolConstants.ErrorStatus status) => new((byte)ProtocolConstants.FrameType.Error, request.Sequence, new ErrorPayload(request.Type, (byte)status, 0).Encode());
    private void Enqueue(ProtocolFrame frame) => EnqueueRaw(frame.Encode());
    private void EnqueueRaw(byte[] bytes) { if (!closed) output.Writer.TryWrite(bytes); }
}
