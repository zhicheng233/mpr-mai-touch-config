using System.Text.Json.Nodes;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.Mcp;

/// <summary>
/// read_telemetry 的请求：采样时长，外加一个可选的聚焦通道
/// （按游戏键名 A1…E8，或按板号 1-4 + 通道 0-11）。
/// </summary>
public readonly record struct TelemetryRequest(int DurationMs, int? KeyIndex, int? Board, int? Channel);

/// <summary>
/// 把一段时间内的 DEBUG_FRAME 汇总成模型能直接判断的变化报告。
/// 之所以不是「原样返回每帧」：33 Hz × 几秒的原始数据塞不进上下文，
/// 而模型读不了图，只能读数字。所以这里给的是每通道的
/// 阈值 / 最大下降量 / 触摸帧数，外加（聚焦某个通道时）该通道逐次变化的序列。
/// </summary>
public sealed class TelemetryAccumulator
{
    /// <summary>聚焦通道的序列上限：噪声大的通道一次能变化上千次，不能全塞回去。</summary>
    private const int MaxSeries = 400;

    private const int ChannelCount = KeyMapping.ChannelCount;

    private readonly string[] labels;
    private readonly int[] channelToKey;
    private readonly int[] thresholds = new int[ChannelCount];
    private readonly int focusChannel;

    private readonly ushort[] filteredLast = new ushort[ChannelCount];
    private readonly ushort[] baselineLast = new ushort[ChannelCount];
    private readonly ushort[] maxDrop = new ushort[ChannelCount];
    private readonly ushort[] previousFiltered = new ushort[ChannelCount];
    private readonly ushort[] previousBaseline = new ushort[ChannelCount];
    private readonly bool[] previousTouched = new bool[ChannelCount];
    private readonly int[] touchedFrames = new int[ChannelCount];
    private readonly int[] touchedByKey = new int[KeyMapping.KeyCount];
    private readonly List<(uint Timestamp, ushort Filtered, ushort Baseline, bool Touched)> series = [];

    private byte[] chipStatus = new byte[KeyMapping.BoardCount];
    private ushort i2cErrors;
    private int frames;
    private bool hasPrevious;
    private uint firstTimestamp;
    private uint lastTimestamp;

    public TelemetryAccumulator(ConfigBlob config, TelemetryRequest request)
    {
        var keyMap = KeyMapping.Clone(config.Mapping);
        labels = KeyMapping.ChannelLabels(keyMap);
        channelToKey = new int[ChannelCount];
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            channelToKey[channel] = KeyMapping.KeyIndexForChannel(keyMap, channel);
            thresholds[channel] = ThresholdFor(config, channel, channelToKey[channel]);
        }

        // 键名优先：键当前映射到哪个通道，就聚焦那个通道（映射可改，所以不能写死）。
        var focusKey = request.KeyIndex is { } key && key is >= 0 and < KeyMapping.KeyCount ? key : -1;
        focusChannel = focusKey >= 0
            ? KeyMapping.ChannelIndexOf(keyMap[focusKey])
            : request is { Board: { } board, Channel: { } port } && board is >= 1 and <= KeyMapping.BoardCount
                ? (board - 1) * KeyMapping.PortCount + port
                : -1;
        if (focusChannel is < 0 or >= ChannelCount) focusChannel = -1;
    }

    public int Frames => frames;

    /// <summary>聚焦通道（board*12 + port）；没有指定时为 -1。</summary>
    public int FocusChannel => focusChannel;

    public void Add(DebugFrame frame)
    {
        if (!hasPrevious) firstTimestamp = frame.TimestampMs;
        lastTimestamp = frame.TimestampMs;
        frames++;

        for (var channel = 0; channel < ChannelCount; channel++)
        {
            var filtered = frame.Filtered[channel];
            var baseline = frame.Baseline[channel];
            filteredLast[channel] = filtered;
            baselineLast[channel] = baseline;

            // 触摸是 filtered 相对 baseline 的下降量，负值（滤波噪声偏高）按 0 算。
            var drop = baseline > filtered ? (ushort)(baseline - filtered) : (ushort)0;
            if (drop > maxDrop[channel]) maxDrop[channel] = drop;

            var keyIndex = channelToKey[channel];
            var touched = keyIndex >= 0 && (frame.TouchBits & (1UL << keyIndex)) != 0;
            if (touched)
            {
                touchedFrames[channel]++;
                if (keyIndex >= 0) touchedByKey[keyIndex]++;
            }

            if (channel == focusChannel &&
                (series.Count == 0 || !hasPrevious ||
                 filtered != previousFiltered[channel] ||
                 baseline != previousBaseline[channel] ||
                 touched != previousTouched[channel]))
            {
                if (series.Count < MaxSeries) series.Add((frame.TimestampMs, filtered, baseline, touched));
            }

            previousFiltered[channel] = filtered;
            previousBaseline[channel] = baseline;
            previousTouched[channel] = touched;
        }

        chipStatus = [.. frame.ChipStatus];
        i2cErrors = frame.I2cErrors;
        hasPrevious = true;
    }

    public JsonObject ToJson(int durationMs)
    {
        var report = new JsonObject
        {
            [nameof(durationMs)] = durationMs,
            ["frames"] = frames,
            // 首帧与末帧之间的实际帧率；0 样本时没有时间跨度，别看成分频为 0。
            ["rateHz"] = frames > 1 && lastTimestamp > firstTimestamp
                ? Math.Round((frames - 1) * 1000d / (lastTimestamp - firstTimestamp), 1)
                : 0d,
            ["i2cErrors"] = i2cErrors,
            ["chipStatus"] = Numbers(chipStatus),
            // 通道表：下标是 board*12+channel。有这张表，模型不用先算一遍归属。
            ["keys"] = new JsonArray(labels.Select(label => (JsonNode)label).ToArray()),
            // 每通道的判定阈值（与固件 thresholdForChannel 同一套算法），
            // 拿 maxDrop 跟它比就知道这个键「够不够灵敏」。
            ["threshold"] = Numbers(thresholds),
        };

        if (frames == 0)
        {
            // 没样本时也要说清「看的是哪个通道」，否则模型分不清是采样失败还是通道选错。
            if (focusChannel >= 0) report["focus"] = FocusJson();
            report["note"] = "no debug frames received";
            return report;
        }

        report["filtered"] = Numbers(filteredLast);
        report["baseline"] = Numbers(baselineLast);
        report["maxDrop"] = Numbers(maxDrop);
        report["touchedFrames"] = Numbers(touchedFrames);

        var touchedKeys = new JsonArray();
        for (var key = 0; key < touchedByKey.Length; key++)
            if (touchedByKey[key] > 0)
                touchedKeys.Add(new JsonObject { ["key"] = KeyMapping.KeyLabels[key], ["frames"] = touchedByKey[key] });
        report["touchedKeys"] = touchedKeys;

        if (focusChannel >= 0) report["focus"] = FocusJson();

        return report;
    }

    private JsonObject FocusJson() => new()
    {
        ["key"] = labels[focusChannel],
        ["board"] = (focusChannel / KeyMapping.PortCount) + 1,
        ["channel"] = focusChannel % KeyMapping.PortCount,
        ["samples"] = frames,
        // 每一项是 [设备毫秒时间戳, filtered, baseline, 触摸 0/1]，
        // 只在值真的变了才追加一条（第一帧总是有），这样序列长度反映的是变化次数。
        ["series"] = new JsonArray(series.Select(entry => (JsonNode)new JsonArray
        {
            (int)entry.Timestamp, (int)entry.Filtered, (int)entry.Baseline, entry.Touched ? 1 : 0,
        }).ToArray()),
        ["truncated"] = series.Count >= MaxSeries,
    };

    /// <summary>与固件 thresholdForChannel 一致：clamp(baseThreshold + sensOffset + electrodeOffset, 1, 1023)。</summary>
    private static int ThresholdFor(ConfigBlob config, int channel, int keyIndex)
    {
        var board = channel / KeyMapping.PortCount;
        var offset = keyIndex >= 0 && keyIndex < config.ElectrodeOffsets.Length ? config.ElectrodeOffsets[keyIndex] : 0;
        return Math.Clamp(config.Boards[board].BaseThreshold + config.Boards[board].SensOffset + offset, 1, 1023);
    }

    // 显式转 int：JsonNode 的隐式转换会按 ushort/byte/uint 装箱，读回来时 GetValue<int>() 会抛。
    private static JsonArray Numbers(IEnumerable<ushort> values) =>
        new(values.Select(value => (JsonNode)(int)value).ToArray());

    private static JsonArray Numbers(IEnumerable<byte> values) =>
        new(values.Select(value => (JsonNode)(int)value).ToArray());

    private static JsonArray Numbers(IEnumerable<int> values) =>
        new(values.Select(value => (JsonNode)value).ToArray());
}
