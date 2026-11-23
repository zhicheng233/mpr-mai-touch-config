namespace MaimaiTouchTool.Protocol;

public static class ProtocolConstants
{
    // 帧头第 2 字节 = major << 4 | minor，与 PROTOCOL.md 第 47 行一致（1.3 → 0x13）。
    // 收发双方都只比较高 4 位，所以这里是「按文档对齐」而非兼容性修复。
    public const byte Sync0 = 0xAA, Sync1 = 0x55, Version = 0x13;
    public const int HeaderLength = 8, CrcLength = 2, MaxPayload = 256;
    public const int ConfigBlobLength = 99, LegacyConfigBlobLength = 93, DebugFrameLength = 207, ExtendedDebugFrameLength = 251, GameFrameLength = 9, LegacyCommandLength = 6;
    public const uint CapabilityCompensation = 0x20;
    /// <summary>VERSION 能力位：bit 4 表示固件支持可配置按键映射（协议 1.1 / schema 2）。</summary>
    public const uint CapabilityKeyMapping = 0x10;
    /// <summary>VERSION 能力位：bit 6 表示固件支持掩码遥测（协议 1.3：10B STREAM_START + 0x91 可变长帧）。</summary>
    public const uint CapabilityMaskedStream = 0x40;
    public const ushort DefaultStreamRateHz = 100;
    /// <summary>掩码遥测的 field_mask 位定义（协议 §5.6，与 .mtr 存储掩码一一对应）。</summary>
    public const byte FieldMaskTouch = 0x01, FieldMaskFiltered = 0x02, FieldMaskBaseline = 0x04, FieldMaskStatus = 0x08, FieldMaskExtended = 0x10;
    /// <summary>channel_mask 全开（48 通道，协议 §5.6 约定全 0 亦为全开；工具显式发全 1 以免歧义）。</summary>
    public const ulong AllChannelsMask = (1UL << 48) - 1;
    public enum FrameType : byte { Version = 0x01, EnterConfig = 0x02, ExitConfig = 0x03, GetConfig = 0x10, SetConfig = 0x11, SaveConfig = 0x12, ApplyConfig = 0x13, Calibrate = 0x20, CalDone = 0x21, StreamStart = 0x30, StreamStop = 0x31, Error = 0x7F, Ack = 0x80, DebugFrame = 0x90, DebugFrameMasked = 0x91 }
    public enum DeviceMode : byte { Game, ConfigIdle, Streaming, Calibrating }
    public enum ErrorStatus : byte { BadFrame = 1, UnknownType, BadLength, InvalidValue, IllegalState, Busy, SequenceConflict, AlreadyConfigured, Hardware, Storage, Timeout }
}
