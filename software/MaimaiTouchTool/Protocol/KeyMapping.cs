namespace MaimaiTouchTool.Protocol;

/// <summary>
/// 34 键 → MPR121 板/通道 的映射工具：打包格式为每键一个字节 <c>(board &lt;&lt; 4) | channel</c>，
/// board 0..3、channel 0..11（合法打包值 0..47）。默认映射与固件 <c>touchmap::DefaultKeyMap</c> 一致，
/// 配置未改动时游戏输出与参考固件逐位相同。
/// </summary>
public static class KeyMapping
{
    public const int KeyCount = 34;
    public const int BoardCount = 4;
    public const int PortCount = 12;
    public const int ChannelCount = BoardCount * PortCount;

    public static readonly string[] KeyLabels =
        ["A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8",
         "C1", "C2", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8"];

    /// <summary>默认焊接顺序（与参考固件 touchmap::Keys 相同）。</summary>
    public static readonly byte[] Default =
    [
        52, 48, 36, 32, 21, 16, 4, 0, 53, 49, 37, 33, 22, 17, 5, 1, 40,
        18, 54, 50, 38, 34, 23, 19, 6, 2, 55, 51, 39, 35, 24, 20, 7, 3
    ];

    public static byte Pack(int board, int channel) => (byte)((board << 4) | (channel & 0x0F));
    public static int BoardOf(byte packed) => packed >> 4;
    public static int ChannelOf(byte packed) => packed & 0x0F;
    public static int ChannelIndexOf(byte packed) => BoardOf(packed) * PortCount + ChannelOf(packed);

    /// <summary>单个打包字节是否合法：board 0..3、channel 0..11。</summary>
    public static bool IsValidPacked(byte packed) => BoardOf(packed) < BoardCount && ChannelOf(packed) < PortCount;

    /// <summary>映射是否合法：长度、取值范围、34 键通道互不重复。错误信息写入 <paramref name="error"/>。</summary>
    public static bool TryValidate(byte[]? map, out string? error)
    {
        if (map is null || map.Length != KeyCount) { error = "mapping must contain 34 entries"; return false; }
        var used = new bool[ChannelCount];
        foreach (var packed in map)
        {
            var board = BoardOf(packed);
            var channel = ChannelOf(packed);
            if (board >= BoardCount || channel >= PortCount) { error = $"invalid board/channel {board}/{channel}"; return false; }
            var index = board * PortCount + channel;
            if (used[index]) { error = $"duplicate channel {board + 1}:{channel:00}"; return false; }
            used[index] = true;
        }
        error = null;
        return true;
    }

    /// <summary>通道（board*12 + port）对应的游戏按键索引；未被映射返回 -1。</summary>
    public static int KeyIndexForChannel(byte[] map, int channel)
    {
        for (var index = 0; index < map.Length && index < KeyCount; index++)
            if (ChannelIndexOf(map[index]) == channel) return index;
        return -1;
    }

    /// <summary>48 个通道的按键名标签（未映射为 "unmapped"）。</summary>
    public static string[] ChannelLabels(byte[] map)
    {
        var labels = Enumerable.Repeat("unmapped", ChannelCount).ToArray();
        for (var index = 0; index < map.Length && index < KeyCount; index++)
        {
            var channel = ChannelIndexOf(map[index]);
            if (channel is >= 0 and < ChannelCount) labels[channel] = KeyLabels[index];
        }
        return labels;
    }

    public static bool IsValid(byte[]? map) => TryValidate(map, out _);

    public static byte[] Clone(byte[]? map) => (byte[]?)map?.Clone() ?? (byte[])Default.Clone();
}
