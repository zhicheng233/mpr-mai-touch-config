using System.Buffers.Binary;

namespace MaimaiTouchTool.Protocol;

public sealed record BoardConfig(byte BaseThreshold = 20, sbyte SensOffset = 0, byte TouchDebounce = 1, byte ReleaseDebounce = 1, byte Sfi = 2, byte Esi = 0);
public sealed record ConfigBlob(BoardConfig[] Boards, sbyte[] ElectrodeOffsets, byte[]? KeyMap = null)
{
    public const byte Schema = 2;

    /// <summary>实际生效的 keyMap：未显式提供时使用默认焊接顺序。</summary>
    public byte[] Mapping => KeyMap ?? KeyMapping.Default;

    public byte[] Encode()
    {
        if (Boards.Length != 4 || ElectrodeOffsets.Length != 34 || Boards.Any(b => b.BaseThreshold is < 1 or > 255 || b.SensOffset is < -127 or > 127 || b.TouchDebounce > 7 || b.ReleaseDebounce > 7 || b.Sfi > 3 || b.Esi > 7) || ElectrodeOffsets.Any(x => x is < -127 or > 127)) throw new ArgumentException("Config is outside protocol ranges.");
        if (!KeyMapping.TryValidate(Mapping, out var mappingError)) throw new ArgumentException($"Config key mapping is invalid: {mappingError}.");
        var bytes = new byte[ProtocolConstants.ConfigBlobLength]; bytes[0] = Schema;
        for (var i = 0; i < 4; i++) { var b = Boards[i]; var offset = 1 + i * 6; bytes[offset] = b.BaseThreshold; bytes[offset + 1] = unchecked((byte)b.SensOffset); bytes[offset + 2] = b.TouchDebounce; bytes[offset + 3] = b.ReleaseDebounce; bytes[offset + 4] = b.Sfi; bytes[offset + 5] = b.Esi; }
        for (var i = 0; i < 34; i++) bytes[25 + i] = unchecked((byte)ElectrodeOffsets[i]);
        Mapping.CopyTo(bytes, 59);
        return bytes;
    }
    public static ConfigBlob Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ProtocolConstants.ConfigBlobLength || bytes[0] != Schema) throw new FormatException("Invalid config blob.");
        var boards = new BoardConfig[4]; for (var i = 0; i < 4; i++) { var o = 1 + i * 6; boards[i] = new(bytes[o], unchecked((sbyte)bytes[o + 1]), bytes[o + 2], bytes[o + 3], bytes[o + 4], bytes[o + 5]); }
        var electrodes = new sbyte[34]; for (var i = 0; i < electrodes.Length; i++) electrodes[i] = unchecked((sbyte)bytes[25 + i]);
        var mapping = bytes.Slice(59, 34).ToArray();
        if (!KeyMapping.TryValidate(mapping, out _)) throw new FormatException("Invalid config key mapping.");
        return new ConfigBlob(boards, electrodes, mapping);
    }
    public static ConfigBlob Defaults() => new(Enumerable.Repeat(new BoardConfig(), 4).ToArray(), new sbyte[] { -3, -3, -3, -3, 0, -3, -3, -3, -17, -18, 0, 0, 2, 0, 2, 3, -17, -19, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -18, -2, 0, 0 }, (byte[])KeyMapping.Default.Clone());
}

public readonly record struct AckPayload(byte RequestType, byte Status, byte Mode, byte Flags, byte[] Result)
{
    public byte[] Encode() => new[] { RequestType, Status, Mode, Flags }.Concat(Result).ToArray();
    public static AckPayload Decode(ReadOnlySpan<byte> bytes) { if (bytes.Length < 4) throw new FormatException("ACK payload too short."); return new(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4..].ToArray()); }
}
public readonly record struct ErrorPayload(byte RequestType, byte Status, ushort Detail)
{
    public byte[] Encode() { var b = new byte[4]; b[0] = RequestType; b[1] = Status; BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), Detail); return b; }
    public static ErrorPayload Decode(ReadOnlySpan<byte> b) { if (b.Length != 4) throw new FormatException("ERROR payload length."); return new(b[0], b[1], BinaryPrimitives.ReadUInt16LittleEndian(b[2..])); }
}

public readonly record struct StreamStartResult(ushort AcceptedRateHz, ushort ActualRateHz)
{
    public static StreamStartResult Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 4) throw new FormatException("STREAM_START result length.");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]));
    }
}

public sealed record DebugFrame(uint TimestampMs, ulong TouchBits, ushort[] Filtered, ushort[] Baseline, byte[] ChipStatus, ushort I2cErrors)
{
    public byte[] Encode() { if (Filtered.Length != 48 || Baseline.Length != 48 || ChipStatus.Length != 4 || TouchBits > 0x3FFFFFFFF) throw new ArgumentException("Invalid debug frame."); var b = new byte[207]; BinaryPrimitives.WriteUInt32LittleEndian(b, TimestampMs); for (var i = 0; i < 5; i++) b[4 + i] = (byte)(TouchBits >> (8 * i)); for (var i = 0; i < 48; i++) { BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(9 + i * 2), Filtered[i]); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(105 + i * 2), Baseline[i]); } ChipStatus.CopyTo(b, 201); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(205), I2cErrors); return b; }
    public static DebugFrame Decode(ReadOnlySpan<byte> b) { if (b.Length != 207) throw new FormatException("DEBUG_FRAME payload length."); var filtered = new ushort[48]; var baseline = new ushort[48]; for (var i = 0; i < 48; i++) { filtered[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(9 + i * 2)..]); baseline[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(105 + i * 2)..]); } ulong bits = 0; for (var i = 0; i < 5; i++) bits |= (ulong)b[4 + i] << (8 * i); return new(BinaryPrimitives.ReadUInt32LittleEndian(b), bits & 0x3FFFFFFFF, filtered, baseline, b[201..205].ToArray(), BinaryPrimitives.ReadUInt16LittleEndian(b[205..])); }
}

public static class LegacyCodec
{
    public static bool IsCommand(ReadOnlySpan<byte> bytes) => bytes.Length == 6 && bytes[0] == (byte)'{' && bytes[5] == (byte)'}';
    /// <summary>
    /// 编码固件 GAME 模式的 9 字节传统游戏帧：'(' + 7×5-bit chunk + ')'。
    /// 位序与参考固件 maimaiTouchControl.ino 的 TouchSend 完全一致：它从 i=33 倒着左移，
    /// 等价于 key i 占 34 位值的第 i 位（A1 在 bit 0、E8 在 bit 33）。
    /// </summary>
    public static byte[] EncodeGameFrame(ReadOnlySpan<bool> keys)
    {
        if (keys.Length != 34) throw new ArgumentException("Exactly 34 keys required.");
        ulong value = 0;
        for (var i = 0; i < 34; i++) if (keys[i]) value |= 1UL << i;
        var b = new byte[9];
        b[0] = (byte)'(';
        for (var i = 0; i < 7; i++) b[1 + i] = (byte)((value >> (5 * i)) & 0x1F);
        b[8] = (byte)')';
        return b;
    }
}
