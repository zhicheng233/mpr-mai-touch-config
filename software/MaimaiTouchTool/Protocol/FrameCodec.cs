using System.Buffers.Binary;

namespace MaimaiTouchTool.Protocol;

public readonly record struct ProtocolFrame(byte Type, ushort Sequence, byte[] Payload)
{
    public int EncodedLength => ProtocolConstants.HeaderLength + Payload.Length + ProtocolConstants.CrcLength;

    public byte[] Encode()
    {
        if (Payload.Length > ProtocolConstants.MaxPayload) throw new ArgumentOutOfRangeException(nameof(Payload));
        var output = new byte[EncodedLength];
        output[0] = ProtocolConstants.Sync0; output[1] = ProtocolConstants.Sync1; output[2] = ProtocolConstants.Version; output[3] = Type;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), (ushort)Payload.Length);
        Payload.CopyTo(output, ProtocolConstants.HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(ProtocolConstants.HeaderLength + Payload.Length), Crc16Ccitt.Compute(output.AsSpan(2, 6 + Payload.Length)));
        return output;
    }

    public static ProtocolFrame Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ProtocolConstants.HeaderLength + ProtocolConstants.CrcLength || bytes[0] != ProtocolConstants.Sync0 || bytes[1] != ProtocolConstants.Sync1 || (bytes[2] >> 4) != (ProtocolConstants.Version >> 4))
            throw new FormatException("Invalid frame header.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (length > ProtocolConstants.MaxPayload || bytes.Length != ProtocolConstants.HeaderLength + length + ProtocolConstants.CrcLength)
            throw new FormatException("Invalid frame length.");
        var expected = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(8 + length)..]);
        if (expected != Crc16Ccitt.Compute(bytes.Slice(2, 6 + length))) throw new FormatException("CRC mismatch.");
        return new ProtocolFrame(bytes[3], BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]), bytes.Slice(8, length).ToArray());
    }
}

public sealed class FrameParser
{
    private readonly List<byte> buffer = new();
    private DateTimeOffset? started;
    public TimeSpan InterByteTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    public IEnumerable<ProtocolFrame> Push(ReadOnlyMemory<byte> bytes, DateTimeOffset now)
    {
        var frames = new List<ProtocolFrame>();
        if (started is { } start && now - start > InterByteTimeout) { buffer.Clear(); started = null; }
        foreach (var value in bytes.ToArray())
        {
            if (buffer.Count == 0) { if (value == ProtocolConstants.Sync0) { buffer.Add(value); started = now; } continue; }
            if (buffer.Count == 1 && value != ProtocolConstants.Sync1) { buffer.Clear(); started = value == ProtocolConstants.Sync0 ? now : null; if (value == ProtocolConstants.Sync0) buffer.Add(value); continue; }
            buffer.Add(value);
            if (buffer.Count < ProtocolConstants.HeaderLength) continue;
            var length = buffer[6] | (buffer[7] << 8);
            if (length > ProtocolConstants.MaxPayload) { buffer.RemoveAt(0); started = buffer.Count > 0 ? now : null; continue; }
            var total = ProtocolConstants.HeaderLength + length + ProtocolConstants.CrcLength;
            if (buffer.Count < total) continue;
            var candidate = buffer.ToArray(); buffer.Clear(); started = null;
            try { frames.Add(ProtocolFrame.Decode(candidate)); }
            catch (FormatException) { for (var index = 1; index < candidate.Length; index++) if (candidate[index] == ProtocolConstants.Sync0) { buffer.AddRange(candidate[index..]); started = now; break; } }
        }
        foreach (var frame in frames) yield return frame;
    }
}
