using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.Mcp;

/// <summary>
/// 「把某个键改到某个板/通道」的唯一实现，stdio 会话和 GUI 会话都走这里，
/// 保证两边的对调语义、错误文案完全一致。
/// </summary>
public static class McpMapping
{
    /// <summary>
    /// 返回改动后的映射表（不改入参）。目标通道已被别的键占用时：
    /// <paramref name="swap"/> 为 false 抛错并提示可以用 swap；为 true 则把两个键对调。
    /// </summary>
    public static byte[] Change(byte[] mapping, int keyIndex, int boardIndex, int channel, bool swap)
    {
        if (keyIndex < 0 || keyIndex >= mapping.Length) throw new ArgumentOutOfRangeException(nameof(keyIndex));
        if (boardIndex is < 0 or >= KeyMapping.BoardCount) throw new ArgumentOutOfRangeException(nameof(boardIndex));
        if (channel is < 0 or >= KeyMapping.PortCount) throw new ArgumentOutOfRangeException(nameof(channel));

        var result = (byte[])mapping.Clone();
        var target = KeyMapping.Pack(boardIndex, channel);
        if (result[keyIndex] == target) return result;

        // 占用者必须在赋值之前找：赋值之后再搜会先搜到刚被改掉的那个键自己，
        // 于是「对调」变成「和自己对调」，重复通道一个都没消掉。
        var occupant = -1;
        for (var index = 0; index < result.Length; index++)
            if (index != keyIndex && result[index] == target) { occupant = index; break; }

        if (occupant >= 0 && !swap)
            throw new InvalidOperationException(
                $"channel {boardIndex + 1}:{channel:00} is already used by {KeyMapping.KeyLabels[occupant]}; " +
                "pass swap=true to trade the two keys");

        var previous = result[keyIndex];
        result[keyIndex] = target;
        if (occupant >= 0) result[occupant] = previous;

        if (!KeyMapping.TryValidate(result, out var error))
            throw new InvalidOperationException($"mapping conflict: {error}");
        return result;
    }
}
