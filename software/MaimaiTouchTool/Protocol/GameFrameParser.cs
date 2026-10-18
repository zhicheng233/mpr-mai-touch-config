namespace MaimaiTouchTool.Protocol;

/// <summary>
/// 解析固件在 GAME 模式输出的 9 字节传统游戏帧：'(' + 7×5-bit chunk + ')'。
/// 34 位触摸位按 LSB-first 打包（chunk[i] 占位 5*i..5*i+4），
/// 其中 key k 占 34 位值的第 k 位（A1=bit 0、E8=bit 33），与参考固件 TouchSend 一致。
/// 与 <see cref="LegacyCodec.EncodeGameFrame"/> 互逆。
/// 扫描式解析：遇到噪声/不完整帧不产出、不抛异常。
/// </summary>
public sealed class GameFrameParser
{
    private const byte Open = (byte)'(';
    private const byte Close = (byte)')';
    private const int ChunkCount = 7;
    private const int FrameLength = 9;
    private const ulong Mask34 = 0x3FFFFFFFFUL;

    private readonly byte[] frame = new byte[FrameLength];
    private int filled;

    /// <summary>解析整块字节。</summary>
    public List<ulong> Push(byte[] bytes) => Push(bytes, bytes.Length);

    /// <summary>解析一块字节，返回其中完整游戏帧解出的 34 位触摸值（按出现顺序）。</summary>
    public List<ulong> Push(byte[] bytes, int count)
    {
        var results = new List<ulong>();
        for (var index = 0; index < count; index++)
        {
            var b = bytes[index];
            if (filled == 0)
            {
                if (b != Open) continue; // 等待帧起始 '('；其余字节（噪声/半帧残料）直接丢弃。
                frame[0] = b;
                filled = 1;
                continue;
            }

            if (filled == FrameLength - 1)
            {
                if (b == Close)
                {
                    frame[filled] = b;
                    filled = 0;
                    results.Add(Decode());
                }
                else
                {
                    filled = b == Open ? 1 : 0; // 不是闭合：把当前字节当作新的潜在起始重试。
                }
                continue;
            }

            // 负载区遇到 '('：视为新帧起点（丢弃这个不完整帧）；遇到 ')'：本帧作废。
            if (b == Open) { frame[0] = b; filled = 1; continue; }
            if (b == Close) { filled = 0; continue; }

            frame[filled++] = b;
        }
        return results;
    }

    private ulong Decode()
    {
        ulong value = 0;
        for (var i = 0; i < ChunkCount; i++) value |= (ulong)(frame[1 + i] & 0x1F) << (5 * i);
        return value & Mask34;
    }
}
