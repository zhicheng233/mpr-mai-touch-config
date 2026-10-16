namespace MaimaiTouchTool.Protocol;

public static class ProtocolConstants
{
    public const byte Sync0 = 0xAA, Sync1 = 0x55, Version = 0x10;
    public const int HeaderLength = 8, CrcLength = 2, MaxPayload = 256;
    public const int ConfigBlobLength = 59, DebugFrameLength = 207, GameFrameLength = 9, LegacyCommandLength = 6;
    public const ushort DefaultStreamRateHz = 100;
    public enum FrameType : byte { Version = 0x01, EnterConfig = 0x02, ExitConfig = 0x03, GetConfig = 0x10, SetConfig = 0x11, SaveConfig = 0x12, ApplyConfig = 0x13, Calibrate = 0x20, CalDone = 0x21, StreamStart = 0x30, StreamStop = 0x31, Error = 0x7F, Ack = 0x80, DebugFrame = 0x90 }
    public enum DeviceMode : byte { Game, ConfigIdle, Streaming, Calibrating }
    public enum ErrorStatus : byte { BadFrame = 1, UnknownType, BadLength, InvalidValue, IllegalState, Busy, SequenceConflict, AlreadyConfigured, Hardware, Storage, Timeout }
}
