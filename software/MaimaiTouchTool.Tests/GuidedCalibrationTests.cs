using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class GuidedCalibrationTests
{
    private static ConfigBlob Config(int baseThreshold = 20, int sensOffset = 0) => new(
        Enumerable.Range(0, 4).Select(_ => new BoardConfig((byte)baseThreshold, (sbyte)sensOffset)).ToArray(),
        new sbyte[34], Enumerable.Range(0, 34).Select(i => KeyMapping.Pack(i / 12, i % 12)).ToArray());

    private static GuidedKeyInput Key(int index, double depth = 200, double noise = 2) =>
        new(index, KeyMapping.KeyLabels[index], index, index / 12, depth, noise, 0, 0, 0, true);
    private static int Threshold(ConfigBlob config, int key) => config.Boards[KeyMapping.BoardOf(config.Mapping[key])].BaseThreshold
        + config.Boards[KeyMapping.BoardOf(config.Mapping[key])].SensOffset + config.ElectrodeOffsets[key];

    [Fact]
    public void CompleteBoardUsesEachKeysOwnSignalAndPreservesOtherBoards()
    {
        var config = Config();
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, i == 0 ? 24 : i == 1 ? 80 : 240)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, config, 0.5);
        var changed = GuidedCalibrationAlgorithm.Apply(result, config);
        Assert.All(result.Keys, k =>
        {
            Assert.Equal(GuidedKeyStatus.Measured, k.Status);
            Assert.InRange(k.KeyThreshold, k.SafeLower, k.SafeUpper);
            Assert.Equal(0, k.KeyThreshold % 4);
            Assert.Equal(k.KeyThreshold, Threshold(changed, k.KeyIndex));
        });
        Assert.Equal(12, Threshold(changed, 0));
        Assert.Equal(40, Threshold(changed, 1));
        Assert.Equal(120, Threshold(changed, 2));
        for (var board = 1; board < 4; board++) Assert.Equal(config.Boards[board], changed.Boards[board]);
        for (var key = 12; key < 34; key++) Assert.Equal(config.ElectrodeOffsets[key], changed.ElectrodeOffsets[key]);
        Assert.Equal(config.Mapping, changed.Mapping);
    }

    [Fact]
    public void SubsetKeepsBoardBaseAndUnmeasuredThresholds()
    {
        var config = Config();
        var result = GuidedCalibrationAlgorithm.Compute([Key(0, 120)], config, 0.5);
        var changed = GuidedCalibrationAlgorithm.Apply(result, config);
        Assert.Equal(config.Boards, changed.Boards);
        Assert.Equal(60, Threshold(changed, 0));
        for (var key = 1; key < 34; key++) Assert.Equal(Threshold(config, key), Threshold(changed, key));
    }

    [Fact]
    public void IdleBiasAndCrossTalkConstrainThresholds()
    {
        var input = Key(0, 100) with { IdleMedian = 10, IdleUpper = 12, CrossTalk = 48 };
        var key = GuidedCalibrationAlgorithm.Compute([input], Config(), 0.2).Keys.Single();
        Assert.Equal(GuidedKeyStatus.Measured, key.Status);
        Assert.True(key.KeyThreshold > 48);
        Assert.True(key.KeyThreshold - 4 > input.IdleUpper);
        Assert.InRange(key.KeyThreshold, key.SafeLower, key.SafeUpper);
    }

    [Fact]
    public void ReportsNoSafeRangeWithoutInventingASuccess()
    {
        var result = GuidedCalibrationAlgorithm.Compute([Key(0, 20, 12)], Config(), 0.5);
        Assert.Equal(GuidedKeyStatus.InsufficientDepth, result.Keys.Single().Status);
        Assert.True(result.HasWarning);
        Assert.Equal(Config().Encode(), GuidedCalibrationAlgorithm.Apply(result, Config()).Encode());
    }

    [Theory]
    [InlineData(41, 0, 0, 1)]
    [InlineData(0, 45, 0, 1)]
    [InlineData(0, 0, 41, 1)]
    [InlineData(0, 0, 0, 64)]
    public void E8ScreenshotRangeCanIdentifyEachPossibleLowerBound(double idle, double crossTalk, double release, int minimum)
    {
        var input = Key(33, 62, 9) with
        {
            IdleUpper = idle, CrossTalk = crossTalk, ReleaseUpper = release, MinimumThreshold = minimum
        };
        var result = GuidedCalibrationAlgorithm.Compute([input], Config(25), 0.5);
        var key = Assert.Single(result.Keys);
        Assert.Equal(GuidedKeyStatus.InsufficientDepth, key.Status);
        Assert.Equal(64, key.SafeLower);
        Assert.Equal(52, key.SafeUpper);
        var limits = Assert.IsType<GuidedSafetyLimits>(key.SafetyLimits);
        Assert.Equal(64, limits.Lower);
        Assert.Equal(52, limits.PressUpper);
        Assert.Equal(64, idle > 0 ? limits.IdleLower : crossTalk > 0 ? limits.CrossTalkLower
            : release > 0 ? limits.ReleaseLower : limits.VerificationLower);
        Assert.Contains("64", new GuidedKeyPreview(key).SafetyDetail);
        Assert.Equal(Config(25).Encode(), GuidedCalibrationAlgorithm.Apply(result, Config(25)).Encode());
    }

    [Fact]
    public void SafetyBreakdownPreservesExistingBoundsWithoutRelaxingAnyConstraint()
    {
        var random = new Random(348);
        for (var i = 0; i < 2000; i++)
        {
            var input = Key(0, random.NextDouble() * 300, random.NextDouble() * 30) with
            {
                IdleUpper = random.NextDouble() * 80 - 40,
                CrossTalk = random.NextDouble() * 100,
                ReleaseUpper = random.NextDouble() * 80,
                MinimumThreshold = random.Next(1, 100), MaximumThreshold = random.Next(100, 509)
            };
            var margin = Math.Max(1, input.NoiseSpan * 2);
            var floor = Math.Max(0, Math.Max(input.IdleUpper, input.ReleaseUpper));
            var lower = Math.Max(8, (int)Math.Ceiling((Math.Max(floor + 4 + margin,
                Math.Max(input.IdleUpper, input.CrossTalk) + margin) + 1) / 4) * 4);
            lower = Math.Max(lower, (int)Math.Ceiling(input.MinimumThreshold / 4.0) * 4);
            var upper = Math.Min((int)Math.Floor((input.PressDepth - Math.Max(1, input.NoiseSpan)) / 4) * 4,
                (int)Math.Floor(input.MaximumThreshold / 4.0) * 4);
            var limits = GuidedCalibrationAlgorithm.SafetyLimitsFor(input);
            Assert.Equal(lower, limits.Lower);
            Assert.Equal(upper, limits.Upper);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RejectsIdleTouchesAndUnstableIdle(bool touched, bool unstable)
    {
        var result = GuidedCalibrationAlgorithm.Compute([Key(0) with { IdleTouched = touched, IdleUnstable = unstable }], Config(), 0.5);
        Assert.Equal(GuidedKeyStatus.UnstableIdle, result.Keys.Single().Status);
    }

    [Fact]
    public void FailedOrSkippedKeysFreezeBoardRebalancing()
    {
        var config = Config();
        var inputs = Enumerable.Range(0, 12).Select(i => i == 11 ? Key(i) with { Measured = false } : Key(i)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, config, 0.5);
        var changed = GuidedCalibrationAlgorithm.Apply(result, config);
        Assert.Equal(config.Boards, changed.Boards);
        Assert.Equal(Threshold(config, 11), Threshold(changed, 11));
        Assert.Equal(GuidedKeyStatus.Skipped, result.Keys[11].Status);
    }

    [Fact]
    public void SolverUsesFeasibleIntervalsInsteadOfRejectingAnOutlierFromMedianAlone()
    {
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, i == 0 ? 1000 : 200)).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);
        Assert.All(result.Keys, k => Assert.Equal(GuidedKeyStatus.Measured, k.Status));
        Assert.All(result.Keys, k => Assert.InRange(k.NewOffset, -127, 127));
        Assert.Equal(result.Keys[0].KeyThreshold, Threshold(GuidedCalibrationAlgorithm.Apply(result, Config()), 0));
    }

    [Fact]
    public void ReportsAnActuallyInfeasibleWholeBoardWithoutChangingIt()
    {
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i, i == 0 ? 24 : 1000) with { IdleUpper = i == 0 ? 0 : 400 }).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, Config(), 0.5);
        Assert.All(result.Keys, k => Assert.Equal(GuidedKeyStatus.Overflow, k.Status));
        Assert.Equal(Config().Encode(), GuidedCalibrationAlgorithm.Apply(result, Config()).Encode());
    }

    [Fact]
    public void AdoptionUsesAbsoluteTargetsEvenWhenBoardIsNotAdopted()
    {
        var config = Config(60);
        var result = GuidedCalibrationAlgorithm.Compute(Enumerable.Range(0, 12).Select(i => Key(i)).ToArray(), config, 0.5);
        var changed = GuidedCalibrationAlgorithm.Apply(result, config, k => k.KeyIndex == 0, _ => false);
        Assert.Equal(100, Threshold(changed, 0));
        Assert.Equal(40, changed.ElectrodeOffsets[0]);
        Assert.Equal(config.Boards, changed.Boards);
        Assert.Equal(60, Threshold(changed, 1));
    }

    [Fact]
    public void UnadoptedKeysAreCompensatedWhenBoardBaseMoves()
    {
        var config = Config(60);
        var result = GuidedCalibrationAlgorithm.Compute(Enumerable.Range(0, 12).Select(i => Key(i)).ToArray(), config, 0.5);
        var changed = GuidedCalibrationAlgorithm.Apply(result, config, k => k.KeyIndex != 0, _ => true);
        Assert.Equal(60, Threshold(changed, 0));
        Assert.Equal(100, Threshold(changed, 1));
    }

    [Fact]
    public void UnrepresentableAdoptionIsRejectedRatherThanClamped()
    {
        var config = Config();
        var result = GuidedCalibrationAlgorithm.Compute(Enumerable.Range(0, 12).Select(i => Key(i, 700)).ToArray(), config, 0.5);
        Assert.Throws<InvalidOperationException>(() => GuidedCalibrationAlgorithm.Apply(result, config, k => k.KeyIndex == 0, _ => true));
    }

    [Fact]
    public void AbsoluteThresholdIsQuantizedNotRelativeOffset()
    {
        var config = Config(21);
        var result = GuidedCalibrationAlgorithm.Compute([Key(0, 80)], config, 0.5);
        Assert.Equal(40, result.Keys.Single().KeyThreshold);
        Assert.Equal(19, result.Keys.Single().NewOffset);
        Assert.Equal(40, Threshold(GuidedCalibrationAlgorithm.Apply(result, config), 0));
    }

    [Fact]
    public void NeverInfersDebounceOrSamplingFromTelemetryTiming()
    {
        var config = Config();
        config.Boards[0] = config.Boards[0] with { TouchDebounce = 5, ReleaseDebounce = 3, Sfi = 1, Esi = 2 };
        var inputs = Enumerable.Range(0, 12).Select(i => Key(i) with { RiseMs = 500, FallMs = 900 }).ToArray();
        var result = GuidedCalibrationAlgorithm.Compute(inputs, config, 0.5);
        var changed = GuidedCalibrationAlgorithm.Apply(result, config);
        Assert.False(result.Boards[0].DebounceChanged);
        Assert.Equal(5, changed.Boards[0].TouchDebounce);
        Assert.Equal(3, changed.Boards[0].ReleaseDebounce);
        Assert.Equal(1, changed.Boards[0].Sfi);
        Assert.Equal(2, changed.Boards[0].Esi);
        Assert.Equal(config.Encode(), GuidedCalibrationAlgorithm.Apply(result, config, applyThresholds: false).Encode());
    }

    [Fact]
    public void RejectsStaleMappingMeasurements()
    {
        Assert.Throws<ArgumentException>(() => GuidedCalibrationAlgorithm.Compute([Key(0) with { Channel = 1 }], Config(), 0.5));
    }

    [Fact]
    public void VerificationFeedbackMovesTheNextRoundInTheCorrectDirection()
    {
        var input = Key(0, 120);
        var missed = GuidedCalibrationAlgorithm.Refine(input, 60, missedTouch: true, falseTouch: false);
        var falseTouch = GuidedCalibrationAlgorithm.Refine(input, 60, missedTouch: false, falseTouch: true);
        var lower = GuidedCalibrationAlgorithm.Compute([missed], Config(), 0.5).Keys.Single();
        var higher = GuidedCalibrationAlgorithm.Compute([falseTouch], Config(), 0.5).Keys.Single();
        Assert.Equal(56, lower.KeyThreshold);
        Assert.Equal(64, higher.KeyThreshold);
        Assert.InRange(lower.KeyThreshold, lower.SafeLower, lower.SafeUpper);
        Assert.InRange(higher.KeyThreshold, higher.SafeLower, higher.SafeUpper);
        // Contradictory evidence must not be hidden by clamping.
        var impossible = GuidedCalibrationAlgorithm.Refine(missed, 60, missedTouch: false, falseTouch: true);
        Assert.Equal(GuidedKeyStatus.InsufficientDepth,
            GuidedCalibrationAlgorithm.Compute([impossible], Config(), 0.5).Keys.Single().Status);
    }

    [Theory]
    [InlineData(-100, -90)]
    [InlineData(0, 0)]
    public void NegativeIdleBiasCannotProduceAZeroReleaseThreshold(double median, double upper)
    {
        var result = GuidedCalibrationAlgorithm.Compute([Key(0, 10, 0) with
            { IdleMedian = median, IdleUpper = upper }], Config(), 0.2);
        var key = result.Keys.Single();
        Assert.Equal(GuidedKeyStatus.Measured, key.Status);
        Assert.Equal(8, key.KeyThreshold);
        Assert.True(GuidedCalibrationAlgorithm.HardwareReleaseThreshold(key.KeyThreshold) > 0);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(8, 1)]
    [InlineData(12, 2)]
    public void HardwareReleaseConversionMatchesFirmware(int threshold, int expected) =>
        Assert.Equal(expected, GuidedCalibrationAlgorithm.HardwareReleaseThreshold(threshold));

    [Fact]
    public void PostTouchResidualReservesReleaseHeadroom()
    {
        var input = Key(0, 100) with { ReleaseUpper = 40 };
        var key = GuidedCalibrationAlgorithm.Compute([input], Config(), 0.2).Keys.Single();
        Assert.Equal(GuidedKeyStatus.Measured, key.Status);
        Assert.Equal(52, key.KeyThreshold);
        Assert.True(key.KeyThreshold - GuidedCalibrationAlgorithm.ReleaseHysteresis > input.ReleaseUpper + input.NoiseSpan * 2);
        var impossible = input with { PressDepth = 48 };
        Assert.Equal(GuidedKeyStatus.InsufficientDepth,
            GuidedCalibrationAlgorithm.Compute([impossible], Config(), 0.2).Keys.Single().Status);
    }

    [Fact]
    public void UnsafeAdoptedOutcomeIsRejectedRatherThanApplied()
    {
        var result = GuidedCalibrationAlgorithm.Compute([Key(0)], Config(), 0.5);
        result = result with { Keys = [result.Keys.Single() with { KeyThreshold = 4 }] };
        Assert.Throws<InvalidOperationException>(() => GuidedCalibrationAlgorithm.Apply(result, Config()));
    }

    [Fact]
    public void StuckFeedbackNeverLowersTouchThresholdAndCarriesReleaseResidual()
    {
        var refined = GuidedCalibrationAlgorithm.Refine(Key(0, 120), 60,
            missedTouch: true, falseTouch: false, stuckRelease: true, releaseUpper: 64);
        Assert.Equal(508, refined.MaximumThreshold);
        var result = GuidedCalibrationAlgorithm.Compute([refined], Config(), 0.5).Keys.Single();
        Assert.Equal(GuidedKeyStatus.Measured, result.Status);
        Assert.True(result.KeyThreshold >= 76);
        Assert.Equal(64, refined.ReleaseUpper);
    }

    [Fact]
    public void LatchedOldHardwareCanBeMeasuredUsingRealRawCyclesButCannotBeVerified()
    {
        var capture = new Capture();
        capture.Frames(4, bits: 1);
        capture.Frames(32, bits: 1);
        Assert.Equal(0, capture.Session.CompletedRepeats); // Idle latch is never a press.
        for (var i = 0; i < 5; i++)
        {
            capture.Frames(7, 40, bits: 1);
            capture.Frames(32, bits: 1);
        }
        Assert.True(capture.Session.IsComplete);
        var input = capture.Session.Measurements().Single();
        Assert.True(input.IdleTouched);
        Assert.True(input.ReleaseStuck);
        Assert.True(input.RawRecoveryUsed);
        Assert.True(input.Measured);
        Assert.Equal(40, input.PressDepth);
        Assert.Equal(5, input.Samples.Count);
        Assert.All(input.Samples, p => Assert.False(p.HardwareReleased));
        var key = capture.Session.BuildResult(Config(), 0.5).Keys.Single();
        Assert.Equal(GuidedKeyStatus.Measured, key.Status);
        Assert.True(key.HadStuckRelease);
        Assert.Equal("Guided.VerifyStuck",
            GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session).Keys.Single().ReasonKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerificationStopsLatchedHardwarePromptlyInsteadOfWaitingForGenericTimeout(bool idleLatch)
    {
        var capture = new Capture(repeats: 3, allowRawRecovery: false);
        capture.Baseline();
        if (!idleLatch) { capture.Frames(5); capture.Frames(7, 40, 1); }
        capture.Frames(12, bits: 1);
        Assert.True(capture.Session.CurrentHardwareStuck);
        Assert.False(capture.Session.IsComplete);
        capture.Frames(12, bits: 1);
        Assert.Equal(GuidedPhase.FinalIdle, capture.Session.Phase);
        capture.Frames(40, bits: 1);
        Assert.True(capture.Session.IsComplete);
        Assert.False(capture.Session.Measurements().Single().TimedOut);
        var report = GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session);
        Assert.False(report.Passed);
        Assert.Equal("Guided.VerifyStuck", report.Keys.Single().ReasonKey);
    }

    [Fact]
    public void SlowButRealHardwareReleaseIsNotReportedAsStuck()
    {
        var capture = new Capture(repeats: 3, allowRawRecovery: false);
        capture.Baseline();
        for (var i = 0; i < 3; i++)
        {
            capture.Frames(5); capture.Frames(7, 40, 1);
            capture.Frames(10, bits: 1); // 300 ms < stuck confirmation.
            capture.Frames(5);
        }
        capture.Frames(40);
        Assert.True(capture.Session.IsComplete);
        Assert.False(capture.Session.Measurements().Single().ReleaseStuck);
        Assert.True(GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session).Passed);
    }

    [Fact]
    public void ReleaseResidualIsMeasuredAndFedIntoSolver()
    {
        var capture = new Capture(repeats: 3);
        capture.Baseline();
        for (var i = 0; i < 3; i++)
        {
            capture.Frames(5); capture.Frames(7, 100, 1);
            capture.Frames(5, 24, 0); // Hardware released, residual signal remains.
            capture.Frames(5);
        }
        var input = capture.Session.Measurements().Single();
        Assert.Equal(24, input.ReleaseUpper);
        Assert.All(input.Samples, p => Assert.Equal(24, p.ReleaseUpper));
        var key = capture.Session.BuildResult(Config(), 0.2).Keys.Single();
        Assert.True(key.KeyThreshold - 4 > 24);
    }

    private sealed class Capture
    {
        public GuidedCalibrationSession Session { get; }
        public uint Time { get; private set; }
        private readonly ushort[] filtered = Enumerable.Repeat((ushort)400, 48).ToArray();
        private readonly ushort[] baseline = Enumerable.Repeat((ushort)400, 48).ToArray();
        public Capture(int repeats = 5, int warmup = 0, int timeout = 45000, bool allowRawRecovery = true, params int[] keys)
        {
            if (keys.Length == 0) keys = [0];
            Session = new(keys.Select(i => new GuidedCalibrationSession.TargetKey(i, KeyMapping.KeyLabels[i], i, i / 12)),
                baselineDurationMs: 120, perKeyTimeoutMs: timeout, warmupDurationMs: warmup, requiredRepeats: repeats,
                allowRawRecovery: allowRawRecovery);
            Session.Begin(0);
        }
        public void Frame(int drop = 0, ulong? bits = 0, int key = 0, int step = 30, byte[]? status = null, int otherDrop = 0)
        {
            Time += (uint)step;
            Array.Fill(filtered, (ushort)400);
            filtered[key] = (ushort)(400 - drop);
            if (otherDrop > 0) filtered[1] = (ushort)(400 - otherDrop);
            Session.PushFrame(Time, filtered, baseline, bits, status);
        }
        public void Frames(int count, int drop = 0, ulong? bits = 0, int key = 0)
        {
            for (var i = 0; i < count; i++) Frame(drop, bits, key);
        }
        public void Baseline() => Frames(4);
        public void Press(int drop = 40, ulong? bits = 1, int key = 0)
        {
            Frames(5, key: key);
            Frames(7, drop, bits, key);
            Frames(5, key: key);
        }
    }

    [Fact]
    public void HardwareTouchCountsEvenBelowTheOldTwelveCountGate()
    {
        var capture = new Capture();
        capture.Baseline();
        for (var i = 0; i < 5; i++) capture.Press(4);
        Assert.True(capture.Session.IsComplete);
        var input = capture.Session.Measurements().Single();
        Assert.True(input.Measured);
        Assert.Equal(5, input.Samples.Count);
        Assert.All(input.Samples, p => Assert.True(p.HardwareSeen));
        Assert.Equal(4, input.PressDepth);
        // Counting an action does not assert that a safe hardware threshold exists.
        Assert.Equal(GuidedKeyStatus.InsufficientDepth, capture.Session.BuildResult(Config(), 0.5).Keys.Single().Status);
    }

    [Fact]
    public void RawSignalCountsWhenOldHardwareThresholdNeverTriggers()
    {
        var capture = new Capture();
        capture.Baseline();
        for (var i = 0; i < 5; i++) capture.Press(20, bits: 0);
        Assert.True(capture.Session.IsComplete);
        Assert.All(capture.Session.Measurements().Single().Samples, p => Assert.False(p.HardwareSeen));
        Assert.Equal(GuidedKeyStatus.Measured, capture.Session.BuildResult(Config(), 0.5).Keys.Single().Status);
    }

    [Fact]
    public void AHoldDoesNotBecomeSeveralRepeatsAndRequiresReleaseToCount()
    {
        var capture = new Capture();
        capture.Baseline(); capture.Frames(5);
        capture.Frames(30, 40, 1);
        Assert.Equal(0, capture.Session.CompletedRepeats);
        Assert.Equal(GuidedRepeatState.Pressed, capture.Session.RepeatState);
        capture.Frames(5);
        Assert.Equal(1, capture.Session.CompletedRepeats);
    }

    [Fact]
    public void RawNoiseSpikeDoesNotCount()
    {
        var capture = new Capture();
        capture.Baseline(); capture.Frames(5);
        capture.Frame(30, bits: 0);
        capture.Frames(8);
        Assert.Equal(0, capture.Session.CompletedRepeats);
    }

    [Fact]
    public void WarmupRampIsNotAddedToIdleNoise()
    {
        var capture = new Capture(warmup: 1000);
        for (var i = 0; i < 10; i++) capture.Frame(80 - i * 8);
        capture.Frames(30);
        capture.Frames(4);
        Assert.Equal(GuidedPhase.Measuring, capture.Session.Phase);
        for (var i = 0; i < 5; i++) capture.Press(20, bits: 0);
        Assert.Equal(0, capture.Session.Measurements().Single().NoiseSpan);
    }

    [Fact]
    public void BadChipStatusAndLongGapsCannotFinishAPress()
    {
        var capture = new Capture();
        capture.Baseline(); capture.Frames(5); capture.Frames(4, 40, 1);
        capture.Frame(status: [15, 7, 7, 7]);
        capture.Frames(5);
        Assert.Equal(0, capture.Session.CompletedRepeats);
        capture.Frames(4, 40, 1);
        capture.Frame(step: 400);
        capture.Frames(5);
        Assert.Equal(0, capture.Session.CompletedRepeats);
    }

    [Fact]
    public void PersistentInvalidDataStopsInsteadOfWaitingForever()
    {
        var capture = new Capture(warmup: 1000);
        for (var i = 0; i < 180; i++) capture.Frame(status: [0, 7, 7, 7]);
        Assert.True(capture.Session.IsComplete);
        Assert.Equal("Guided.NoFrames", capture.Session.Fault);
    }

    [Fact]
    public void DeviceMillisecondWrapDoesNotBreakPressReleaseTiming()
    {
        var session = new GuidedCalibrationSession([new(0, "A1", 0, 0)], baselineDurationMs: 120,
            warmupDurationMs: 0, requiredRepeats: 3);
        var time = uint.MaxValue - 200;
        session.Begin(time);
        void Feed(int count, bool pressed)
        {
            for (var i = 0; i < count; i++)
            {
                time = unchecked(time + 30);
                var filtered = Enumerable.Repeat((ushort)400, 48).ToArray();
                if (pressed) filtered[0] = 360;
                session.PushFrame(time, filtered, Enumerable.Repeat((ushort)400, 48).ToArray(), pressed ? 1UL : 0);
            }
        }
        Feed(4, false);
        for (var i = 0; i < 3; i++) { Feed(5, false); Feed(7, true); Feed(5, false); }
        Assert.True(session.IsComplete);
        Assert.Null(session.Fault);
        Assert.Equal(3, session.Measurements().Single().Samples.Count);
    }

    [Fact]
    public void AVeryLongHoldIsReportedInsteadOfSilentlyTruncatingItsTrace()
    {
        var capture = new Capture();
        capture.Baseline(); capture.Frames(5);
        capture.Frames(410, 40, 1);
        Assert.True(capture.Session.IsComplete);
        Assert.Equal(GuidedKeyStatus.Timeout, capture.Session.BuildResult(Config(), 0.5).Keys.Single().Status);
    }

    [Fact]
    public void SkipAndTimeoutRemainDifferentFromSuccessfulMeasurement()
    {
        var capture = new Capture(timeout: 500, keys: [0, 12]);
        capture.Baseline();
        capture.Session.SkipCurrentKey(capture.Time);
        capture.Frames(20, key: 12);
        var result = capture.Session.BuildResult(Config(), 0.5);
        Assert.Equal(GuidedKeyStatus.Skipped, result.Keys[0].Status);
        Assert.Equal(GuidedKeyStatus.Timeout, result.Keys[1].Status);
        Assert.Equal(1, capture.Session.TotalProgress);
    }

    [Fact]
    public void EarlyFinishRequiresThreeCompletedReleases()
    {
        var capture = new Capture();
        capture.Baseline(); capture.Press(); capture.Press();
        Assert.False(capture.Session.CanFinishCurrentKeyEarly);
        capture.Session.FinishCurrentKeyEarly(capture.Time);
        Assert.False(capture.Session.IsComplete);
        capture.Press();
        Assert.True(capture.Session.CanFinishCurrentKeyEarly);
        capture.Session.FinishCurrentKeyEarly(capture.Time);
        Assert.True(capture.Session.IsComplete);
        Assert.Equal(3, capture.Session.Measurements().Single().Samples.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerificationRequiresActualHardwareTouches(bool hardware)
    {
        var capture = new Capture(repeats: 3);
        capture.Baseline();
        for (var i = 0; i < 3; i++) capture.Press(40, hardware ? 1UL : 0);
        var report = GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session);
        Assert.Equal(hardware, report.Passed);
        Assert.Equal(hardware ? "Guided.VerifyPassed" : "Guided.VerifyMissed", report.Keys.Single().ReasonKey);
    }

    [Fact]
    public void VerificationRejectsUnexpectedNeighborTouches()
    {
        var capture = new Capture(repeats: 3);
        capture.Baseline();
        for (var i = 0; i < 3; i++) capture.Press(40, bits: 3);
        var report = GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session);
        Assert.False(report.Passed);
        Assert.Equal("Guided.VerifyCrossTalk", report.Keys.Single().ReasonKey);
    }

    [Fact]
    public void CrossTalkOnLaterKeysIsIncludedInEarlierKeysResults()
    {
        var capture = new Capture(repeats: 3, keys: [0, 1]);
        capture.Baseline();
        for (var i = 0; i < 3; i++) capture.Press(40, key: 0);
        capture.Frames(5, key: 1);
        for (var i = 0; i < 7; i++) capture.Frame(10, bits: 2, key: 0, otherDrop: 60);
        capture.Frames(5, key: 1);
        capture.Session.SkipCurrentKey(capture.Time);
        Assert.Equal(10, capture.Session.Measurements()[0].CrossTalk);
    }

    [Fact]
    public async Task TrialNeverSavesAndRestoresTheExactOriginalBlob()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = new SerialTransport(endpoint);
        await transport.ConnectAsync();
        await transport.SendRequestAsync((byte)ProtocolConstants.FrameType.EnterConfig, []);
        var original = endpoint.AppliedConfig;
        var candidate = Config(100);
        var trial = new GuidedCalibrationTrial(original, new ConfigurationWorkflow(transport));
        await trial.ApplyAsync(candidate);
        Assert.True(trial.NeedsRestore);
        Assert.Equal(candidate.Encode(), endpoint.AppliedConfig.Encode());
        Assert.Equal(original.Encode(), endpoint.PersistedConfig.Encode());
        await trial.RestoreAsync();
        Assert.False(trial.NeedsRestore);
        Assert.Equal(original.Encode(), endpoint.AppliedConfig.Encode());
        Assert.DoesNotContain(endpoint.Requests, r => r.Type == (byte)ProtocolConstants.FrameType.SaveConfig);
    }

    [Fact]
    public async Task AmbiguousApplyAndFailedRollbackKeepTheRecoveryHandle()
    {
        await using var endpoint = new FakeSerialEndpoint();
        var transport = new SerialTransport(endpoint) { CommandTimeout = TimeSpan.FromMilliseconds(30) };
        await transport.ConnectAsync();
        await transport.SendRequestAsync((byte)ProtocolConstants.FrameType.EnterConfig, []);
        var original = endpoint.AppliedConfig;
        var trial = new GuidedCalibrationTrial(original, new ConfigurationWorkflow(transport));
        endpoint.SuppressResponses = true;
        await Assert.ThrowsAsync<TimeoutException>(() => trial.ApplyAsync(Config(80)));
        Assert.True(trial.NeedsRestore);
        await Assert.ThrowsAsync<TimeoutException>(() => trial.RestoreAsync());
        Assert.True(trial.NeedsRestore);
        endpoint.SuppressResponses = false;
        await trial.RestoreAsync();
        Assert.Equal(original.Encode(), endpoint.AppliedConfig.Encode());
        Assert.False(trial.NeedsRestore);
    }

    [Fact]
    public async Task ViewModelCalibratesOnlySelectedBoardAndLocksSavingUntilAccepted()
    {
        var endpoint = new FakeSerialEndpoint();
        await using var vm = new MainViewModel(_ => { var transport = new SerialTransport(endpoint); return (transport, transport); });
        vm.SelectedPort = "COM_GUIDED_TEST";
        await vm.RunConnectForTestAsync();
        Assert.True(vm.IsConnected, vm.StatusMessage);
        foreach (var board in vm.GuidedBoardChoices) board.Selected = board.Board == 0;
        var original = endpoint.AppliedConfig;
        var keys = Enumerable.Range(0, 34).Where(k => KeyMapping.BoardOf(original.Mapping[k]) == 0).ToArray();
        await vm.RunStartGuidedForTestAsync();
        await endpoint.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(vm.IsGuidedRunning);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.ReadbackCommand.CanExecute(null));
        Assert.False(vm.CalibrateCommand.CanExecute(null));
        uint time = 0;
        FeedMeasurement(vm, original, keys, ref time, repeats: 5);
        await WaitFor(() => !vm.IsGuidedRunning && !vm.IsBusy);
        Assert.Equal(keys.Length, vm.GuidedKeys.Count);
        Assert.All(vm.GuidedKeys, key => Assert.Equal(0, key.Outcome.Board));
        Assert.Equal(original.Encode(), endpoint.AppliedConfig.Encode());
        Assert.False(vm.ApplyGuidedResultCommand.CanExecute(null));

        await vm.RunVerifyGuidedForTestAsync();
        Assert.True(vm.IsGuidedRunning, vm.StatusMessage);
        Assert.False(vm.SaveCommand.CanExecute(null));
        time = 0; // Each new stream has its own time axis.
        FeedMeasurement(vm, original, keys, ref time, repeats: 8);
        await WaitFor(() => !vm.IsGuidedRunning && !vm.IsBusy);
        Assert.True(vm.ApplyGuidedResultCommand.CanExecute(null), vm.GuidedVerificationLabel);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(original.Encode(), endpoint.PersistedConfig.Encode());
        for (var board = 1; board < 4; board++) Assert.Equal(original.Boards[board], endpoint.AppliedConfig.Boards[board]);
        vm.ApplyGuidedResultCommand.Execute(null);
        Assert.False(vm.IsGuidedLocked);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.DoesNotContain(endpoint.Requests, r => r.Type == (byte)ProtocolConstants.FrameType.SaveConfig);
    }

    [Fact]
    public async Task CancellingHardwareVerificationRestoresOriginalWithoutSaving()
    {
        var endpoint = new FakeSerialEndpoint();
        await using var vm = new MainViewModel(_ => { var transport = new SerialTransport(endpoint); return (transport, transport); });
        vm.SelectedPort = "COM_GUIDED_CANCEL_TEST";
        await vm.RunConnectForTestAsync();
        foreach (var board in vm.GuidedBoardChoices) board.Selected = board.Board == 0;
        var original = endpoint.AppliedConfig;
        var keys = Enumerable.Range(0, 34).Where(k => KeyMapping.BoardOf(original.Mapping[k]) == 0).ToArray();
        await vm.RunStartGuidedForTestAsync();
        await endpoint.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        uint time = 0;
        FeedMeasurement(vm, original, keys, ref time, repeats: 5);
        await WaitFor(() => !vm.IsGuidedRunning && !vm.IsBusy);
        await vm.RunVerifyGuidedForTestAsync();
        Assert.NotEqual(original.Encode(), endpoint.AppliedConfig.Encode());
        await vm.RunCancelGuidedForTestAsync();
        Assert.False(vm.IsGuidedLocked);
        Assert.Equal(original.Encode(), endpoint.AppliedConfig.Encode());
        Assert.Equal(original.Encode(), endpoint.PersistedConfig.Encode());
        Assert.True(vm.IsConnected);
        Assert.DoesNotContain(endpoint.Requests, r => r.Type == (byte)ProtocolConstants.FrameType.SaveConfig);
    }

    [Fact]
    public void VerificationCannotFinishAfterThreeTouchesOrBeforeFinalIdleCheck()
    {
        var capture = new Capture(repeats: 8, allowRawRecovery: false);
        capture.Baseline();
        for (var i = 0; i < 3; i++) capture.Press();
        Assert.False(capture.Session.CanFinishCurrentKeyEarly);
        capture.Session.FinishCurrentKeyEarly(capture.Time);
        Assert.Equal(GuidedPhase.Measuring, capture.Session.Phase);
        for (var i = 3; i < 8; i++) capture.Press();
        Assert.Equal(GuidedPhase.FinalIdle, capture.Session.Phase);
        Assert.False(GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session).Passed);
        capture.Frames(40);
        Assert.True(GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session).Passed);
    }

    [Fact]
    public void DelayedStuckTouchDuringFinalIdleCannotPassVerification()
    {
        var capture = new Capture(repeats: 3, allowRawRecovery: false);
        capture.Baseline();
        for (var i = 0; i < 3; i++) capture.Press();
        Assert.Equal(GuidedPhase.FinalIdle, capture.Session.Phase);
        capture.Frames(40, bits: 1);
        var report = GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session);
        Assert.True(capture.Session.IsComplete);
        Assert.Equal("Guided.VerifyStuck", report.Keys.Single().ReasonKey);
    }

    [Fact]
    public void FinalIdleRequiresAContinuousValidTelemetryWindow()
    {
        var capture = new Capture(repeats: 3, allowRawRecovery: false);
        capture.Baseline();
        for (var i = 0; i < 3; i++) capture.Press();
        capture.Frames(15);
        capture.Frame(step: 400);
        capture.Frames(20);
        Assert.False(capture.Session.IsComplete);
        capture.Frames(20);
        Assert.True(capture.Session.IsComplete);
        Assert.True(GuidedVerificationReport.From([new(0, "A1", 0, 0)], capture.Session).Passed);
    }

    [Fact]
    public async Task StuckHardwareVerificationBlocksAcceptanceAndCanRestoreOriginal()
    {
        var endpoint = new FakeSerialEndpoint();
        await using var vm = new MainViewModel(_ => { var transport = new SerialTransport(endpoint); return (transport, transport); });
        vm.SelectedPort = "COM_GUIDED_STUCK_TEST";
        await vm.RunConnectForTestAsync();
        foreach (var board in vm.GuidedBoardChoices) board.Selected = board.Board == 0;
        var original = endpoint.AppliedConfig;
        var keys = Enumerable.Range(0, 34).Where(k => KeyMapping.BoardOf(original.Mapping[k]) == 0).ToArray();
        await vm.RunStartGuidedForTestAsync();
        await endpoint.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        uint time = 0;
        FeedMeasurement(vm, original, keys, ref time, repeats: 5);
        await WaitFor(() => !vm.IsGuidedRunning && !vm.IsBusy);
        await vm.RunVerifyGuidedForTestAsync();
        time = 0;
        FeedMeasurement(vm, original, keys, ref time, repeats: 8, stuckKey: keys[0]);
        await WaitFor(() => !vm.IsGuidedRunning && !vm.IsBusy);
        Assert.Contains(vm.GuidedKeys, k => k.Outcome.KeyIndex == keys[0] && k.VerificationReason == "Guided.VerifyStuck");
        Assert.False(vm.ApplyGuidedResultCommand.CanExecute(null));
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.RetryGuidedCommand.CanExecute(null));
        await vm.RunDiscardGuidedForTestAsync();
        Assert.Equal(original.Encode(), endpoint.AppliedConfig.Encode());
        Assert.Equal(original.Encode(), endpoint.PersistedConfig.Encode());
        Assert.DoesNotContain(endpoint.Requests, r => r.Type == (byte)ProtocolConstants.FrameType.SaveConfig);
    }

    private static void FeedMeasurement(MainViewModel vm, ConfigBlob config, int[] keys, ref uint time, int repeats, int stuckKey = -1)
    {
        var timestamp = time;
        ulong latchedBits = 0;
        void Feed(int count, int key = -1)
        {
            for (var i = 0; i < count; i++)
            {
                timestamp += 30;
                var filtered = Enumerable.Repeat((ushort)400, 48).ToArray();
                if (key >= 0) filtered[KeyMapping.ChannelIndexOf(config.Mapping[key])] = 360;
                if (key >= 0 && key == stuckKey) latchedBits |= 1UL << key;
                vm.PushGuidedFrameForTest(new DebugFrame(timestamp, (key >= 0 ? 1UL << key : 0) | latchedBits, filtered,
                    Enumerable.Repeat((ushort)400, 48).ToArray(), [7, 7, 7, 7], 0));
            }
        }
        Feed(150); // Warmup, clean baseline, then arm the first key.
        foreach (var key in keys)
        {
            for (var repeat = 0; repeat < repeats; repeat++)
            {
                Feed(5);
                Feed(7, key);
                Feed(5);
            }
            if (key == stuckKey) Feed(30); // Follow the release prompt until the stuck key is diagnosed.
        }
        Feed(40); // Strict verification also requires a final continuous idle window.
        time = timestamp;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Calibration did not complete.");
            await Task.Delay(10);
        }
    }
}
