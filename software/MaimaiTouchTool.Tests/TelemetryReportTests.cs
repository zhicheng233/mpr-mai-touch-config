using System.Text.Json.Nodes;
using MaimaiTouchTool.Mcp;
using MaimaiTouchTool.Protocol;
using Xunit;

namespace MaimaiTouchTool.Tests;

/// <summary>
/// 遥测汇总的测试：模型只能读数字，所以报告必须回答「哪个通道这段时间动过、动了多少、按了几次」，
/// 而不是把一秒钟几十帧的原始数据原样丢回去。
/// </summary>
public sealed class TelemetryReportTests
{
    private const int ChannelCount = 48;

    /// <summary>造一帧：只给关心的通道设值，其余为 0。</summary>
    private static DebugFrame Frame(uint timestamp, int channel, ushort filtered, ushort baseline, int keyIndex = -1, bool touched = false)
    {
        var filteredValues = new ushort[ChannelCount];
        var baselineValues = new ushort[ChannelCount];
        filteredValues[channel] = filtered;
        baselineValues[channel] = baseline;
        var bits = touched && keyIndex >= 0 ? 1UL << keyIndex : 0UL;
        return new DebugFrame(timestamp, bits, filteredValues, baselineValues, new byte[4], 0);
    }

    private static JsonObject Report(ConfigBlob config, TelemetryRequest request, params DebugFrame[] frames)
    {
        var accumulator = new TelemetryAccumulator(config, request);
        foreach (var frame in frames) accumulator.Add(frame);
        return accumulator.ToJson(request.DurationMs);
    }

    [Fact]
    public void EveryChannelGetsAKeyLabelEvenWhenUnmapped()
    {
        var report = Report(ConfigBlob.Defaults(), new TelemetryRequest(1000, null, null, null),
            Frame(0, 40, 300, 310));

        var keys = report["keys"]!.AsArray();
        Assert.Equal(ChannelCount, keys.Count);
        // 通道下标 = board*12 + channel。A1 默认打包值 52 → board 3 通道 4 → 下标 40。
        Assert.Equal("A1", keys[40]!.GetValue<string>());
        Assert.Equal("A8", keys[0]!.GetValue<string>());
        Assert.Equal("unmapped", keys[8]!.GetValue<string>());
    }

    [Fact]
    public void ReportTracksTheLargestDropAgainstTheConfiguredThreshold()
    {
        // 手指按下的典型形状：baseline 稳定，filtered 先掉下去再回来。
        var report = Report(ConfigBlob.Defaults(), new TelemetryRequest(1000, 0, null, null),
            Frame(0, 40, 300, 310),
            Frame(10, 40, 180, 310),
            Frame(20, 40, 120, 310),
            Frame(30, 40, 305, 310));

        Assert.Equal(4, report["frames"]!.GetValue<int>());
        // 310 - 120 = 190，这是判断「这个通道够不够灵敏」的核心数字。
        Assert.Equal(190, report["maxDrop"]!.AsArray()[40]!.GetValue<int>());
        Assert.Equal(310, report["baseline"]!.AsArray()[40]!.GetValue<int>());
        // 阈值必须能跟 maxDrop 直接比较，所以要按固件的算法给出来。
        // 默认板 20/0，A1 的电极偏移 -3 → 17。
        Assert.Equal(17, report["threshold"]!.AsArray()[40]!.GetValue<int>());
    }

    [Fact]
    public void ThresholdFollowsPerElectrodeOffsets()
    {
        // C1 默认偏移 -17，落到 board 3（0x5D）：20 + 0 - 17 = 3。
        var report = Report(ConfigBlob.Defaults(), new TelemetryRequest(1000, null, null, null),
            Frame(0, 32, 300, 310));

        Assert.Equal(3, report["threshold"]!.AsArray()[32]!.GetValue<int>());
        // 没有键占用的通道用板级值，不带电极偏移。
        Assert.Equal(20, report["threshold"]!.AsArray()[8]!.GetValue<int>());
    }

    [Fact]
    public void TouchedFramesAreCountedPerKey()
    {
        var report = Report(ConfigBlob.Defaults(), new TelemetryRequest(1000, null, null, null),
            Frame(0, 40, 150, 310, keyIndex: 0, touched: true),
            Frame(10, 40, 140, 310, keyIndex: 0, touched: true),
            Frame(20, 40, 300, 310, keyIndex: 0, touched: false));

        var touched = report["touchedKeys"]!.AsArray();
        Assert.Single(touched);
        Assert.Equal("A1", touched[0]!["key"]!.GetValue<string>());
        Assert.Equal(2, touched[0]!["frames"]!.GetValue<int>());
        Assert.Equal(2, report["touchedFrames"]!.AsArray()[40]!.GetValue<int>());
    }

    [Fact]
    public void FocusedChannelKeepsOnlyTheSamplesThatChanged()
    {
        // 中间那两帧数值没变，不该占位；序列要能看出「什么时候掉的、掉了多久」。
        var report = Report(ConfigBlob.Defaults(), new TelemetryRequest(1000, 0, null, null),
            Frame(0, 40, 300, 310),
            Frame(10, 40, 300, 310),
            Frame(20, 40, 200, 310, keyIndex: 0, touched: true),
            Frame(30, 40, 200, 310, keyIndex: 0, touched: true),
            Frame(40, 40, 300, 310));

        var focus = report["focus"]!;
        Assert.Equal("A1", focus["key"]!.GetValue<string>());
        Assert.Equal(4, focus["board"]!.GetValue<int>());
        Assert.Equal(4, focus["channel"]!.GetValue<int>());
        Assert.Equal(5, focus["samples"]!.GetValue<int>());

        var series = focus["series"]!.AsArray();
        Assert.Equal(3, series.Count);                       // 0ms、20ms、40ms
        Assert.Equal(0, series[0]![0]!.GetValue<int>());
        Assert.Equal(300, series[0]![1]!.GetValue<int>());
        Assert.Equal(20, series[1]![0]!.GetValue<int>());
        Assert.Equal(200, series[1]![1]!.GetValue<int>());
        Assert.Equal(1, series[1]![3]!.GetValue<int>());     // 这一帧是触摸
        Assert.False(focus["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void FocusByNameFollowsTheCurrentMapping()
    {
        // A1 被挪到 board 1（0x5A）通道 0 后，focus=key:A1 必须看新通道，而不是默认的 40。
        var mapping = (byte[])KeyMapping.Default.Clone();
        var displaced = Array.IndexOf(mapping, KeyMapping.Pack(0, 0));   // 默认占着 0 通道的是 A8
        Assert.Equal(7, displaced);
        mapping[0] = KeyMapping.Pack(0, 0);
        mapping[displaced] = KeyMapping.Default[0];
        Assert.True(KeyMapping.TryValidate(mapping, out _));

        var config = ConfigBlob.Defaults() with { KeyMap = mapping };
        var report = Report(config, new TelemetryRequest(1000, 0, null, null),
            Frame(0, 0, 300, 310));

        Assert.Equal(0, report["focus"]!["channel"]!.GetValue<int>());
        Assert.Equal(1, report["focus"]!["board"]!.GetValue<int>());
        Assert.Equal("A1", report["focus"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public void FocusByBoardAndChannelIgnoresTheKeyName()
    {
        // board 2 / 通道 3 → 下标 15，默认映射里那是 D6。
        var report = Report(ConfigBlob.Defaults(), new TelemetryRequest(1000, null, Board: 2, Channel: 3),
            Frame(0, 15, 300, 310));

        Assert.Equal("D6", report["focus"]!["key"]!.GetValue<string>());
        Assert.Equal(2, report["focus"]!["board"]!.GetValue<int>());
        Assert.Equal(3, report["focus"]!["channel"]!.GetValue<int>());
    }

    [Fact]
    public void NoFramesIsReportedAsSuchInsteadOfAnEmptyShell()
    {
        var report = Report(ConfigBlob.Defaults(), new TelemetryRequest(500, 0, null, null));

        Assert.Equal(0, report["frames"]!.GetValue<int>());
        Assert.Contains("no debug frames", report["note"]!.GetValue<string>());
        Assert.Null(report["filtered"]);   // 没样本就别给 48 个 0 让模型误以为是实测值
        Assert.NotNull(report["focus"]);   // 但还是告诉它看的是哪个通道
    }

    [Fact]
    public void RateIsDerivedFromTheDeviceTimestamps()
    {
        var frames = Enumerable.Range(0, 11)
            .Select(i => Frame((uint)(i * 30), 40, 300, 310))
            .ToArray();
        var report = Report(ConfigBlob.Defaults(), new TelemetryRequest(300, null, null, null), frames);

        // 10 个间隔 × 30 ms = 300 ms，11 帧 → 约 33 Hz（与实测的 USB CDC 上限一致）。
        Assert.Equal(33.3, report["rateHz"]!.GetValue<double>(), 1);
    }
}
