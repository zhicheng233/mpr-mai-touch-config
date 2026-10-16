using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class ThresholdComputationTests
{
    // 通道索引 = board*12 + port；映射关系见 TelemetryModels.KeyChannels。
    // board0 使用的端口为 {4,0,5,1,6,2,7,3}，端口 8..11 未映射。
    private const int UnmappedBoard0Channel = 8;   // board0 / port8 → 未映射，offset 取 0
    private const int MappedA8Channel = 0;          // board0 / port0 = key A8，ElectrodeOffsets[7] = -3

    [Fact]
    public void ApplyThresholds_UnmappedChannelEqualsBaseThreshold()
    {
        var telemetry = new TelemetrySessionModel();
        telemetry.ApplyThresholds(ConfigBlob.Defaults());

        // 默认 baseThreshold = 20，sensOffset = 0，未映射 offset = 0 → 20
        Assert.Equal((ushort)20, telemetry.Channels[UnmappedBoard0Channel].Threshold);
        Assert.All(telemetry.Channels, channel => Assert.True(channel.Threshold >= 1));
    }

    [Fact]
    public void ApplyThresholds_MappedChannelAddsElectrodeOffset()
    {
        var telemetry = new TelemetrySessionModel();
        telemetry.ApplyThresholds(ConfigBlob.Defaults());

        // A8 电极偏移默认 -3 → 20 + 0 + (-3) = 17
        Assert.Equal((ushort)17, telemetry.Channels[MappedA8Channel].Threshold);
    }

    [Fact]
    public void ApplyThresholds_ClampsToLowerBound()
    {
        var telemetry = new TelemetrySessionModel();
        var offsets = new sbyte[34];
        for (var i = 0; i < offsets.Length; i++) offsets[i] = -127;
        var config = new ConfigBlob(
            new[]
            {
                new BoardConfig(1, -127, 1, 1, 2, 0),
                new BoardConfig(1, -127, 1, 1, 2, 0),
                new BoardConfig(1, -127, 1, 1, 2, 0),
                new BoardConfig(1, -127, 1, 1, 2, 0),
            },
            offsets);

        telemetry.ApplyThresholds(config);

        // 1 + (-127) + (-127) = -253 → clamp 到 1
        Assert.Equal((ushort)1, telemetry.Channels[MappedA8Channel].Threshold);
        Assert.All(telemetry.Channels, channel => Assert.True(channel.Threshold >= 1));
    }

    [Fact]
    public void ThresholdValue_TracksBaselineMinusThreshold()
    {
        var telemetry = new TelemetrySessionModel();
        telemetry.ApplyThresholds(ConfigBlob.Defaults());
        var channel = telemetry.Channels[MappedA8Channel]; // threshold = 17

        channel.Apply(500, 700, false, 0x07); // baseline 700, filtered 500
        Assert.Equal(700 - 17, channel.ThresholdValue);
    }

    [Fact]
    public void ThresholdValue_ClampsToValidRange()
    {
        var telemetry = new TelemetrySessionModel();
        telemetry.ApplyThresholds(ConfigBlob.Defaults());
        var channel = telemetry.Channels[MappedA8Channel]; // threshold = 17

        channel.Apply(0, 5, false, 0x07); // baseline 5 - 17 = -12 → clamp 0
        Assert.Equal(0, channel.ThresholdValue);
    }
}
