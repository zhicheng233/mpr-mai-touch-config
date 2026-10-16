using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class GuidedCalibrationTests
{
    private static ConfigBlob Config(int baseThreshold = 20, int sensOffset = 0, int[]? offsets = null) => new(
        [
            new BoardConfig((byte)baseThreshold, (sbyte)sensOffset),
            new BoardConfig((byte)baseThreshold, (sbyte)sensOffset),
            new BoardConfig((byte)baseThreshold, (sbyte)sensOffset),
            new BoardConfig((byte)baseThreshold, (sbyte)sensOffset)
        ],
        (offsets ?? Enumerable.Repeat(0, 34).ToArray()).Select(value => (sbyte)value).ToArray());

    // 构造一个已测到的键；KeyIndex 决定写入 ElectrodeOffsets 的位置。
    private static GuidedKeyInput Key(int keyIndex, int board, double pressDepth, double noiseSpan = 4,
        double riseMs = 20, double fallMs = 40, bool measured = true) =>
        new(keyIndex, $"K{keyIndex}", board * 12, board, pressDepth, noiseSpan, 200, riseMs, fallMs, measured);

    [Fact]
    public void Compute_UsesHalfDepthWhenNoiseIsLow()
    {
        // pressDepth=400 → 目标 200；噪声底线 4*2=8 → 取 200。
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, 0, 400)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);

        Assert.All(result.Keys, key => Assert.Equal(200, key.KeyThreshold));
        Assert.All(result.Keys, key => Assert.Equal(GuidedKeyStatus.Measured, key.Status));
    }

    [Fact]
    public void Compute_NoiseFloorDominatesWhenNoiseIsHigh()
    {
        // 噪声跨度 60 → 底线 120 > 半按 200? 不：200>120，仍取 200。用低深度让噪声主导。
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, 0, 160, noiseSpan: 60)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5); // ideal=80, noiseFloor=120
        Assert.All(result.Keys, key => Assert.Equal(120, key.KeyThreshold));
    }

    [Fact]
    public void Compute_FlagsInsufficientDepth()
    {
        // 目标阈值 >= pressDepth：无法既防误触又按得动。
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, 0, 30, noiseSpan: 40)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);

        Assert.All(result.Keys, key => Assert.Equal(GuidedKeyStatus.InsufficientDepth, key.Status));
        Assert.True(result.HasWarning);
    }

    [Fact]
    public void Compute_SplitsBoardBaseIntoBaseThresholdAndSensOffset()
    {
        // 目标阈值 350：baseThreshold 上限 255，其余 95 交给 sensOffset（≤127，可容纳）。
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, 0, 700)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);

        var board = result.Boards[0];
        Assert.Equal(255, board.NewBaseThreshold);
        Assert.Equal(95, board.NewSensOffset);
        Assert.All(result.Keys, key => Assert.Equal(0, key.NewOffset)); // 全部同值 → 相对偏移 0
    }

    [Fact]
    public void Compute_ClampsSensOffsetAndRebasesKeysWhenBoardBaseIsHuge()
    {
        // 目标阈值 400 > 255+127=382 的可表达上限 → sensOffset 顶到 127，剩余 18 落到逐键偏移。
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, 0, 800)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);

        var board = result.Boards[0];
        Assert.Equal(255, board.NewBaseThreshold);
        Assert.Equal(127, board.NewSensOffset);
        // 生效基 382；400-382=18 → 对齐到 20。
        Assert.All(result.Keys, key => Assert.Equal(20, key.NewOffset));
    }

    [Fact]
    public void Compute_ProducesPerKeyOffsetsRelativeToBoardMedian()
    {
        // 12 键中 11 个目标 100，1 个目标 200 → 中位数 100；异常键相对 +100（对齐到 4）。
        var inputs = Enumerable.Range(0, 12)
            .Select(i => Key(i, 0, i == 5 ? 400 : 200))
            .ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);

        var outlier = result.Keys.Single(key => key.KeyIndex == 5);
        Assert.Equal(200, outlier.KeyThreshold);
        Assert.Equal(100, outlier.NewOffset); // 200 - 100(中位) = 100
        Assert.Equal(0, result.Keys.Single(key => key.KeyIndex == 0).NewOffset);
    }

    [Fact]
    public void Compute_MarksOverflowWhenOffsetExceedsRange()
    {
        // 板内 3 个键目标 100，1 个目标 500 → 中位数 100，异常键相对 +400 → 溢出。
        var inputs = new[]
        {
            Key(0, 0, 200), Key(1, 0, 200), Key(2, 0, 200), Key(3, 0, 1000)
        };
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);

        var outlier = result.Keys.Single(key => key.KeyIndex == 3);
        Assert.Equal(GuidedKeyStatus.Overflow, outlier.Status);
        Assert.Equal(500, outlier.KeyThreshold);
        Assert.Equal(0, outlier.NewOffset); // 溢出时保留原值
    }

    [Fact]
    public void Compute_SkippedKeysKeepOriginalOffset()
    {
        var offsets = Enumerable.Repeat(0, 34).ToArray();
        offsets[3] = 24;
        var inputs = new[] { Key(0, 0, 400), Key(1, 0, 400), Key(2, 0, 400), Key(3, 0, 0, measured: false) };
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(offsets: offsets), 0.5);

        var skipped = result.Keys.Single(key => key.KeyIndex == 3);
        Assert.Equal(GuidedKeyStatus.Skipped, skipped.Status);
        Assert.Equal(24, skipped.NewOffset);
        Assert.Equal(24, skipped.OldOffset);
    }

    [Fact]
    public void Compute_SuggestsDebounceFromTiming()
    {
        // 上升 30ms → DT=1；回落 200ms → DR=3（按阈值表 40/80/160/320）。
        var inputs = Enumerable.Range(0, 12)
            .Select(i => Key(i, 0, 400, riseMs: 30, fallMs: 200))
            .ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);

        var board = result.Boards[0];
        Assert.Equal(1, board.NewTouchDebounce);
        Assert.Equal(3, board.NewReleaseDebounce);
    }

    [Fact]
    public void Compute_ClampsTriggerRatioIntoRange()
    {
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, 0, 400)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 5.0); // 越界
        Assert.Equal(GuidedCalibrationAlgorithm.MaxTriggerRatio, result.TriggerRatio);
    }

    [Fact]
    public void Apply_OnlyAdoptsSelectedKeysAndBoards()
    {
        // 键 1..11 深度 400 → 目标 200（板基）；键 0 深度 300 → 目标 150，相对板基 -50 → 对齐到 -52。
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, 0, i == 0 ? 300 : 400)).ToArray();
        var start = Config(offsets: Enumerable.Range(0, 34).Select(i => i + 1).ToArray()); // 1,2,3,...
        var result = GuidedCalibrationAlgorithm.Compute(inputs, start, 0.5);

        Assert.Equal(-52, result.Keys.Single(key => key.KeyIndex == 0).NewOffset);

        // 只采纳键 0，不采纳任何板级项。
        var applied = GuidedCalibrationAlgorithm.Apply(result, start,
            key => key.KeyIndex == 0, board => false);

        Assert.Equal(-52, applied.ElectrodeOffsets[0]);      // 被采纳
        Assert.Equal(2, applied.ElectrodeOffsets[1]);        // 未采纳 → 保持原值
        Assert.Equal(20, applied.Boards[0].BaseThreshold);   // 板级未被采纳
        Assert.Equal(0, applied.Boards[0].SensOffset);
    }

    [Fact]
    public void Apply_AdoptsBoardWhenSelected()
    {
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, 0, 700)).ToArray(); // 目标 350
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);

        var applied = GuidedCalibrationAlgorithm.Apply(result, Config(), _ => true, _ => true);
        Assert.Equal(255, applied.Boards[0].BaseThreshold);
        Assert.Equal(95, applied.Boards[0].SensOffset);
        Assert.Equal(0, applied.ElectrodeOffsets[0]); // 相对生效基（255+95=350）→ 0
    }

    [Fact]
    public void RoundToQuantum_AlignsToChipResolution()
    {
        Assert.Equal(0, GuidedCalibrationAlgorithm.RoundToQuantum(1));
        Assert.Equal(4, GuidedCalibrationAlgorithm.RoundToQuantum(3));
        Assert.Equal(100, GuidedCalibrationAlgorithm.RoundToQuantum(99));
        Assert.Equal(-8, GuidedCalibrationAlgorithm.RoundToQuantum(-7));
    }

    // ---- 采集状态机 ----

    private static GuidedCalibrationSession Session(params int[] channels)
    {
        var targets = channels.Select((channel, index) => new GuidedCalibrationSession.TargetKey(index, $"K{index}", channel, channel / 12));
        return new GuidedCalibrationSession(targets, baselineDurationMs: 100);
    }

    private static ushort[] Fill(ushort value) { var array = new ushort[48]; Array.Fill(array, value); return array; }

    [Fact]
    public void Session_CapturesBaselineThenMeasuresPress()
    {
        var session = Session(0);
        var filtered = Fill(400);
        var baseline = Fill(400);

        session.Begin(0);
        session.PushFrame(50, filtered, baseline);
        session.PushFrame(120, filtered, baseline); // 超出基线窗口 → 进入测量
        Assert.Equal(GuidedPhase.Measuring, session.Phase);

        // 三次按下→松开。松开判定要求持续低于阈值 ≈50ms，故每轮给 3 个释放帧。
        uint t = 200;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            filtered[0] = 200; // delta = 200
            session.PushFrame(t, filtered, baseline); t += 40;
            filtered[0] = 400; // 释放
            session.PushFrame(t, filtered, baseline); t += 40;
            session.PushFrame(t, filtered, baseline); t += 40;
            session.PushFrame(t, filtered, baseline); t += 40;
        }

        Assert.True(session.IsComplete);
        var result = session.BuildResult(Config(), 0.5);
        var key = result.Keys.Single();
        Assert.Equal(GuidedKeyStatus.Measured, key.Status);
        Assert.True(key.PressDepth >= 190, $"press depth {key.PressDepth} should be near 200");
    }

    [Fact]
    public void Session_SkipRecordsUnmeasuredKey()
    {
        var session = Session(0, 12);
        session.Begin(0);
        session.PushFrame(200, Fill(400), Fill(400));
        Assert.Equal(GuidedPhase.Measuring, session.Phase);

        session.SkipCurrentKey(300);
        Assert.Equal(1, session.CurrentIndex);
        session.SkipCurrentKey(400);
        Assert.True(session.IsComplete);

        var result = session.BuildResult(Config(), 0.5);
        Assert.All(result.Keys, key => Assert.Equal(GuidedKeyStatus.Skipped, key.Status));
        Assert.Equal(0, result.MeasuredCount);
    }

    [Fact]
    public void Session_TimeoutWithoutPressSkipsKey()
    {
        var targets = new[] { new GuidedCalibrationSession.TargetKey(0, "K0", 0, 0) };
        var session = new GuidedCalibrationSession(targets, baselineDurationMs: 100, perKeyTimeoutMs: 500);
        session.Begin(0);
        session.PushFrame(200, Fill(400), Fill(400));

        session.PushFrame(1000, Fill(400), Fill(400)); // 超过单键超时，且无按压
        Assert.True(session.IsComplete);
        var result = session.BuildResult(Config(), 0.5);
        Assert.Equal(GuidedKeyStatus.Skipped, result.Keys.Single().Status);
    }

    [Fact]
    public void Session_TotalProgressReachesOne()
    {
        var session = Session(0);
        session.Begin(0);
        Assert.Equal(GuidedPhase.Baseline, session.Phase);
        session.PushFrame(200, Fill(400), Fill(400));
        session.SkipCurrentKey(300);
        Assert.Equal(1.0, session.TotalProgress);
    }
}
