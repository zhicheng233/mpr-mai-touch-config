using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class GuidedIdleWindowTests
{
    [Fact]
    public void AnIsolatedSpikeDoesNotInvalidateStableNoiseStatistics()
    {
        var values = Enumerable.Repeat(0.0, 100).ToArray();
        values[70] = 300;
        var statistics = GuidedIdleStatistics.From(values);
        Assert.False(statistics.Unstable);
        Assert.True(statistics.Transient);
        Assert.Equal(0, statistics.Upper);
        Assert.Equal(0, statistics.Noise);
    }

    [Fact]
    public void SustainedNoiseIsIncludedInSafetyRangeRatherThanRejectedAsOneSpike()
    {
        var values = Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 12.0 : -12.0).ToArray();
        var statistics = GuidedIdleStatistics.From(values);
        Assert.False(statistics.Unstable);
        Assert.Equal(24, statistics.Noise);
        var input = new GuidedKeyInput(0, "A1", 40, 3, 20, statistics.Noise, 0, 0, 0, true)
            { IdleUpper = statistics.Upper };
        Assert.Equal(GuidedKeyStatus.InsufficientDepth,
            GuidedCalibrationAlgorithm.Compute([input], ConfigBlob.Defaults(), 0.5).Keys.Single().Status);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, true)]
    public void IdleTouchRequiresPersistenceNotOneFlicker(int touchedFrames, bool expected)
    {
        var frames = Enumerable.Range(0, 100).Select(i => new GuidedIdleObservation((ulong)(i * 30), [],
            i >= 20 && i < 20 + touchedFrames ? 1UL : 0)).ToArray();
        Assert.Equal(expected, GuidedIdleStatistics.HasIdleTouch(frames, 0));
    }

    [Fact]
    public void RepeatedBriefFalseTouchesStillFail()
    {
        var frames = Enumerable.Range(0, 100).Select(i => new GuidedIdleObservation((ulong)(i * 30), [],
            i % 5 == 0 ? 1UL : 0)).ToArray();
        Assert.True(GuidedIdleStatistics.HasIdleTouch(frames, 0));
    }

    private static GuidedCalibrationSession Session(int duration = 600) => new([new(0, "A1", 0, 0)],
        baselineDurationMs: duration, warmupDurationMs: 0, requiredRepeats: 3);
    private static void Push(GuidedCalibrationSession session, ref uint time, int drop = 0, ulong bits = 0, int step = 30)
    {
        time += (uint)step;
        var filtered = Enumerable.Repeat((ushort)400, 48).ToArray();
        filtered[0] = (ushort)(400 - drop);
        session.PushFrame(time, filtered, Enumerable.Repeat((ushort)400, 48).ToArray(), bits, [7, 7, 7, 7]);
    }

    [Fact]
    public void RecoveredDriftReplacesTheBadWindowWithoutRestartingTheWholeRound()
    {
        var session = Session();
        uint time = 0;
        session.Begin(time);
        for (var i = 0; i < 20; i++) Push(session, ref time, i);
        Assert.Equal(GuidedPhase.Baseline, session.Phase);
        Assert.True(session.BaselineRetrying);
        Assert.Contains("A1", session.BaselineProblemKeys);
        for (var i = 0; i < 40; i++) Push(session, ref time);
        Assert.Equal(GuidedPhase.Measuring, session.Phase);
        session.SkipCurrentKey(time);
        var input = Assert.Single(session.Measurements());
        Assert.False(input.IdleUnstable);
        Assert.Equal(0, input.IdleDrift);
        Assert.Equal(0, input.NoiseSpan);
    }

    [Fact]
    public void PersistentDriftHasABoundedWaitAndDoesNotHoldOtherKeysForever()
    {
        var session = Session();
        uint time = 0;
        session.Begin(time);
        for (var i = 0; i < 240 && session.Phase == GuidedPhase.Baseline; i++) Push(session, ref time, i);
        Assert.Equal(GuidedPhase.Measuring, session.Phase);
        Assert.InRange(time, 6600U, 7000U);
        session.SkipCurrentKey(time);
        Assert.True(Assert.Single(session.Measurements()).IdleUnstable);
    }

    [Fact]
    public void StableWindowWithOneRawSpikeAndOneHardwareFlickerCanProceed()
    {
        var session = Session(3000);
        uint time = 0;
        session.Begin(time);
        for (var i = 0; i < 100; i++) Push(session, ref time, i == 60 ? 200 : 0, i == 60 ? 1UL : 0);
        Assert.Equal(GuidedPhase.Measuring, session.Phase);
        session.SkipCurrentKey(time);
        var input = Assert.Single(session.Measurements());
        Assert.False(input.IdleTouched);
        Assert.False(input.IdleUnstable);
        Assert.True(input.IdleTransient);
    }

    [Fact]
    public void StrictVerificationCanRecoverFromAnEarlyIdleTouchAfterAFreshCleanWindow()
    {
        var session = new GuidedCalibrationSession([new(0, "A1", 0, 0)], baselineDurationMs: 600,
            warmupDurationMs: 0, requiredRepeats: 3, allowRawRecovery: false);
        uint time = 0;
        session.Begin(time);
        for (var i = 0; i < 20; i++) Push(session, ref time, bits: 1);
        Assert.True(session.BaselineRetrying);
        for (var i = 0; i < 40; i++) Push(session, ref time);
        Assert.Equal(GuidedPhase.Measuring, session.Phase);
        Assert.Equal(0UL, session.IdleTouchedBits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalIdleIgnoresOneFlickerButStillRejectsRepeatedFalseTouches(bool repeated)
    {
        var session = new GuidedCalibrationSession([new(0, "A1", 0, 0)], baselineDurationMs: 600,
            warmupDurationMs: 0, requiredRepeats: 3, allowRawRecovery: false);
        uint time = 0;
        session.Begin(time);
        for (var i = 0; i < 20; i++) Push(session, ref time, bits: i == 10 ? 1UL : 0);
        Assert.Equal(GuidedPhase.Measuring, session.Phase);
        for (var repeat = 0; repeat < 3; repeat++)
        {
            for (var i = 0; i < 5; i++) Push(session, ref time);
            for (var i = 0; i < 7; i++) Push(session, ref time, 40, 1);
            for (var i = 0; i < 5; i++) Push(session, ref time);
        }
        Assert.Equal(GuidedPhase.FinalIdle, session.Phase);
        for (var i = 0; i < 40; i++) Push(session, ref time, bits: (repeated ? i % 5 == 0 : i == 10) ? 1UL : 0);
        var report = GuidedVerificationReport.From([new(0, "A1", 0, 0)], session);
        Assert.Equal(!repeated, report.Passed);
        Assert.Equal(repeated ? "Guided.StatusIdleHardware" : "Guided.VerifyPassed", report.Keys.Single().ReasonKey);
    }
}
