using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class SignalChartTests
{
    private static DebugFrame Frame(uint time, ulong bits = 0, byte[]? status = null) => new(time, bits,
        Enumerable.Repeat((ushort)380, 48).ToArray(), Enumerable.Repeat((ushort)400, 48).ToArray(),
        status ?? [7, 7, 7, 7], 0);

    [Fact]
    public void ChartUsesAppliedMappingAndFirmwareQuantizedThresholdsForEveryKey()
    {
        var config = ConfigBlob.Defaults();
        var model = new SignalChartModel();
        var frame = Frame(100, 1UL << 16);
        frame.Filtered[KeyMapping.ChannelIndexOf(config.Mapping[16])] = 360;
        model.Add(frame, config);
        model.SelectedKeyIndex = 16;
        var point = Assert.Single(model.Points());
        Assert.Equal(360, point.Filtered);
        Assert.Equal(40, point.Drop);
        Assert.True(point.Touched);
        var board = config.Boards[KeyMapping.BoardOf(config.Mapping[16])];
        var threshold = Math.Clamp(board.BaseThreshold + board.SensOffset + config.ElectrodeOffsets[16], 1, 1023);
        Assert.Equal((threshold + 3) / 4 * 4, point.TouchThreshold);
        Assert.Equal(GuidedCalibrationAlgorithm.HardwareReleaseThreshold(threshold) * 4, point.ReleaseThreshold);
        model.SelectedKeyIndex = 0;
        Assert.Equal(380, Assert.Single(model.Points()).Filtered);
        Assert.False(Assert.Single(model.Points()).Touched);
    }

    [Fact]
    public void OldSamplesOwnTheirDataAndMappingAndDoNotBridgeMappingChanges()
    {
        var config = ConfigBlob.Defaults();
        var frame = Frame(0);
        var channel = KeyMapping.ChannelIndexOf(config.Mapping[0]);
        frame.Filtered[channel] = 350;
        var model = new SignalChartModel();
        model.Add(frame, config);
        frame.Filtered[channel] = 1;
        (config.Mapping[0], config.Mapping[1]) = (config.Mapping[1], config.Mapping[0]);
        model.Add(Frame(30), config);
        var points = model.Points();
        Assert.Equal(350, points[0].Filtered);
        Assert.Equal(380, points[1].Filtered);
        Assert.False(points[1].Connect);
    }

    [Fact]
    public void GapsInvalidChipsAndInvalidValuesBreakCurves()
    {
        var model = new SignalChartModel();
        var config = ConfigBlob.Defaults();
        model.Add(Frame(0), config);
        model.Add(Frame(30), config);
        model.Add(Frame(400), config);
        model.Add(Frame(430, status: [7, 7, 7, 15]), config); // A1 on board 4.
        model.Add(Frame(460), config);
        var invalid = Frame(490);
        invalid.Baseline[KeyMapping.ChannelIndexOf(config.Mapping[0])] = 0;
        model.Add(invalid, config);
        var points = model.Points();
        Assert.False(points[0].Connect);
        Assert.True(points[1].Connect);
        Assert.False(points[2].Connect);
        Assert.False(points[3].Valid);
        Assert.False(points[4].Connect);
        Assert.False(points[5].Valid);
    }

    [Fact]
    public void TimestampWrapIsContinuousButRestartClearsOldHistory()
    {
        var model = new SignalChartModel();
        var config = ConfigBlob.Defaults();
        model.Add(Frame(uint.MaxValue - 10), config);
        model.Add(Frame(19), config);
        Assert.Equal(30UL, model.Points()[1].TimeMs);
        Assert.True(model.Points()[1].Connect);
        model.Add(Frame(0), config);
        Assert.Equal(0UL, Assert.Single(model.Points()).TimeMs);
    }

    [Fact]
    public void HistoryIsBoundedByBothTimeAndSampleCount()
    {
        var model = new SignalChartModel();
        var config = ConfigBlob.Defaults();
        for (uint i = 0; i < 1400; i++) model.Add(Frame(i * 10), config);
        Assert.Equal(SignalChartModel.Capacity, model.Count);
        model.Clear();
        for (uint i = 0; i < 100; i++) model.Add(Frame(i * 1000), config);
        var points = model.Points();
        Assert.True(points[^1].TimeMs - points[0].TimeMs <= SignalChartModel.WindowMs);
        Assert.Equal(31, model.Count);
    }

    [Fact]
    public void RepeatedRendersReuseHistoryAndSelectionOrNewFramesInvalidateIt()
    {
        var model = new SignalChartModel();
        var config = ConfigBlob.Defaults();
        model.Add(Frame(0), config);
        var first = model.Points();
        Assert.Same(first, model.Points());
        _ = model.CurrentValues;
        Assert.Same(first, model.Points());
        model.Add(Frame(30), config);
        var next = model.Points();
        Assert.NotSame(first, next);
        Assert.Single(first); // Previously rendered snapshots remain immutable.
        Assert.Equal(2, next.Count);
        model.SelectedKeyIndex = 16;
        Assert.NotSame(next, model.Points());
        Assert.Contains("C1", model.CurrentValues);
        model.Clear();
        Assert.Empty(model.Points());
        Assert.Equal(MaimaiTouchTool.Localization.Localizer.Instance["Chart.Empty"], model.CurrentValues);
    }

    [Fact]
    public void FreezeFollowAndClearAreDisplayOnlyOperations()
    {
        var model = new SignalChartModel();
        var config = ConfigBlob.Defaults();
        model.Add(Frame(0), config);
        model.IsPaused = true;
        model.Add(Frame(30), config);
        Assert.Single(model.Points());
        model.Follow(16);
        Assert.Equal(16, model.SelectedKeyIndex);
        model.FollowGuided = false;
        model.Follow(0);
        Assert.Equal(16, model.SelectedKeyIndex);
        model.IsPaused = false;
        model.Add(Frame(60), config);
        Assert.Equal(2, model.Count);
        model.ClearCommand.Execute(null);
        Assert.Empty(model.Points());
    }
}
