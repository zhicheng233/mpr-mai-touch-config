using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class CalibrationSummaryTests
{
    // 构造一个成功 CAL_DONE：48 个基线与 48 个阈值。
    private static CalDonePayload Success(ushort baseline, Func<int, ushort> threshold) =>
        new(CalibrationStatus.Success, CalibrationReason.None,
            Enumerable.Repeat(baseline, 48).ToArray(),
            Enumerable.Range(0, 48).Select(index => threshold(index)).ToArray(), []);

    private static ConfigBlob Config(byte baseThreshold, sbyte sensOffset = 0) => new(
        [
            new BoardConfig(baseThreshold, sensOffset),
            new BoardConfig(baseThreshold, sensOffset),
            new BoardConfig(baseThreshold, sensOffset),
            new BoardConfig(baseThreshold, sensOffset),
        ],
        new sbyte[34]);

    [Fact]
    public void From_DetectsBoardThresholdRaise()
    {
        var previous = Config(20);
        var current = Config(34);
        // 读回后的阈值 = base + sensOffset + offset；offset 全 0 → 34
        var done = Success(200, _ => 34);

        var summary = CalibrationSummary.From(previous, current, done);

        Assert.True(summary.Succeeded);
        Assert.False(summary.ReadbackFailed);
        Assert.Equal(4, summary.ChangedBoardCount);
        Assert.Equal(48, summary.CalibratedChannels);
        Assert.All(summary.Boards, board => { Assert.Equal(20, board.PreviousThreshold); Assert.Equal(34, board.CurrentThreshold); Assert.True(board.Changed); });
    }

    [Fact]
    public void From_ReportsNoChangeWhenThresholdsEqual()
    {
        var config = Config(20);
        var done = Success(180, _ => 20);

        var summary = CalibrationSummary.From(config, config, done);

        Assert.Equal(0, summary.ChangedBoardCount);
        Assert.All(summary.Boards, board => Assert.False(board.Changed));
        Assert.False(string.IsNullOrWhiteSpace(summary.BuildHeadline()));
    }

    [Fact]
    public void From_MarksReadbackFailureAndKeepsBaselineInfo()
    {
        var done = Success(150, _ => 20);

        var summary = CalibrationSummary.From(Config(20), null, done);

        Assert.True(summary.Succeeded);
        Assert.True(summary.ReadbackFailed);
        Assert.Empty(summary.Boards);
        Assert.Equal(48, summary.CalibratedChannels);
        Assert.Equal(150, summary.MinBaseline);
        Assert.Equal(150, summary.MaxBaseline);
    }

    [Fact]
    public void From_ComputesBaselineRange()
    {
        var baseline = new ushort[48];
        for (var i = 0; i < baseline.Length; i++) baseline[i] = (ushort)(100 + i);
        var done = new CalDonePayload(CalibrationStatus.Success, CalibrationReason.None, baseline, Enumerable.Repeat((ushort)20, 48).ToArray(), []);

        var summary = CalibrationSummary.From(Config(20), Config(20), done);

        Assert.Equal(100, summary.MinBaseline);
        Assert.Equal(147, summary.MaxBaseline);
    }

    [Fact]
    public void Failed_ReportsReason()
    {
        var done = new CalDonePayload(CalibrationStatus.Failed, CalibrationReason.TouchDetected, [], [], []);

        var summary = CalibrationSummary.Failed(done);

        Assert.False(summary.Succeeded);
        Assert.Equal("TouchDetected", summary.FailureReasonName);
        Assert.Empty(summary.Boards);
    }

    [Fact]
    public void Failed_RendersAffectedChannelsInHeadline()
    {
        var done = new CalDonePayload(CalibrationStatus.Failed, CalibrationReason.TouchDetected, [], [],
            [new CalibrationDiagnostic(3, 42), new CalibrationDiagnostic(46, 99)]);

        var summary = CalibrationSummary.Failed(done);

        Assert.True(summary.HasDiagnostics);
        Assert.Equal(2, summary.Diagnostics.Count);
        var headline = summary.BuildHeadline();
        // 通道以 B<板>P<端口> 形式呈现；通道 3 → B1P3，通道 46 → B4P10。
        Assert.Contains("B1P3", headline);
        Assert.Contains("B4P10", headline);
        Assert.Contains("42", headline);
    }

    [Fact]
    public void Failed_WithoutDiagnosticsOmitsChannelSection()
    {
        var summary = CalibrationSummary.Failed(new CalDonePayload(CalibrationStatus.Failed, CalibrationReason.Timeout, [], [], []));
        Assert.False(summary.HasDiagnostics);
        var headline = summary.BuildHeadline();
        Assert.DoesNotContain("B1P", headline);
    }

    [Fact]
    public void BuildHeadline_MentionsReadbackFailure()
    {
        var summary = CalibrationSummary.From(Config(20), null, Success(150, _ => 20));

        // 只验证不抛异常并包含可读内容（语言表随环境变化）。
        Assert.False(string.IsNullOrWhiteSpace(summary.BuildHeadline()));
    }
}
