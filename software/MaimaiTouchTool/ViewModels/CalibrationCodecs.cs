using System.Buffers.Binary;

namespace MaimaiTouchTool.ViewModels;

public readonly record struct CalibratePayload(byte Mode, ushort HoldMs)
{
    public byte[] Encode() { if (Mode > 1 || HoldMs > 60000) throw new ArgumentOutOfRangeException(nameof(Mode)); var bytes = new byte[3]; bytes[0] = Mode; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), HoldMs); return bytes; }
    public static CalibratePayload Decode(ReadOnlySpan<byte> bytes) { if (bytes.Length != 3) throw new FormatException("CALIBRATE payload length."); var payload = new CalibratePayload(bytes[0], BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..])); _ = payload.Encode(); return payload; }
}
public enum CalibrationStatus : byte { Success = 0, Failed = 1 }
public enum CalibrationReason : byte { None = 0, Timeout = 1, TouchDetected = 2, Noisy = 3, HardwareMissing = 4, I2cError = 5, InvalidRequest = 6 }
public readonly record struct CalibrationDiagnostic(byte Channel, byte Magnitude);
public sealed record CalDonePayload(CalibrationStatus Status, CalibrationReason Reason, ushort[] Baseline, ushort[] TouchThreshold, CalibrationDiagnostic[] Diagnostics)
{
    public byte[] Encode()
    {
        if (Status == CalibrationStatus.Success) { if (Baseline.Length != 48 || TouchThreshold.Length != 48) throw new ArgumentException("Successful calibration requires 48 values per array."); var b = new byte[196]; b[0] = (byte)Status; b[1] = (byte)Reason; for (var i = 0; i < 48; i++) { BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4 + i * 2), Baseline[i]); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(100 + i * 2), TouchThreshold[i]); } return b; }
        if (Status != CalibrationStatus.Failed || Baseline.Length != 0 || TouchThreshold.Length != 0 || Reason > CalibrationReason.InvalidRequest || Diagnostics.Length > 12) throw new ArgumentException("Invalid calibration result.");
        // 无诊断时保持历史 4 字节帧；有诊断时追加 count:u8 + count×{channel,magnitude}。
        if (Diagnostics.Length == 0) return [(byte)Status, (byte)Reason, 0, 0];
        var bytes = new byte[5 + Diagnostics.Length * 2];
        bytes[0] = (byte)Status; bytes[1] = (byte)Reason; bytes[4] = (byte)Diagnostics.Length;
        for (var i = 0; i < Diagnostics.Length; i++) { bytes[5 + i * 2] = Diagnostics[i].Channel; bytes[6 + i * 2] = Diagnostics[i].Magnitude; }
        return bytes;
    }
    // 失败帧长度是可变的：4（无诊断块）或 5+2*count。诊断块在 reserved:u16 之后。
    public static CalDonePayload Decode(ReadOnlySpan<byte> bytes) { if (bytes.Length != 4 && bytes.Length != 196 && (bytes.Length < 5 || bytes.Length > 5 + 12 * 2 || (bytes.Length - 5) % 2 != 0)) throw new FormatException("CAL_DONE payload length."); var status = (CalibrationStatus)bytes[0]; var reason = (CalibrationReason)bytes[1]; if (status > CalibrationStatus.Failed || reason > CalibrationReason.InvalidRequest || bytes[2] != 0 || bytes[3] != 0) throw new FormatException("Invalid CAL_DONE result."); if (bytes.Length == 196) { if (status != CalibrationStatus.Success) throw new FormatException("Failed CAL_DONE has an invalid length."); var baseline = new ushort[48]; var thresholds = new ushort[48]; for (var i = 0; i < 48; i++) { baseline[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(4 + i * 2)..]); thresholds[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(100 + i * 2)..]); } return new(status, reason, baseline, thresholds, []); } if (status != CalibrationStatus.Failed) throw new FormatException("Successful CAL_DONE is incomplete."); if (bytes.Length == 4) return new(status, reason, [], [], []); var count = bytes[4]; if (bytes.Length != 5 + count * 2) throw new FormatException("CAL_DONE diagnostic block length."); var diagnostics = new CalibrationDiagnostic[count]; for (var i = 0; i < count; i++) diagnostics[i] = new CalibrationDiagnostic(bytes[5 + i * 2], bytes[6 + i * 2]); return new(status, reason, [], [], diagnostics); }
}
