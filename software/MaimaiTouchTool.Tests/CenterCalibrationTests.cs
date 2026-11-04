using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class CenterCalibrationTests
{
    private static GuidedKeyInput Input(int key = 17) => new(key, KeyMapping.KeyLabels[key],
        KeyMapping.ChannelIndexOf(KeyMapping.Default[key]), KeyMapping.BoardOf(KeyMapping.Default[key]),
        11, 8, 0, 0, 0, true) { FilteredNoiseSpan = 0 };

    [Theory]
    [InlineData(16, 0)]
    [InlineData(17, 0)]
    [InlineData(16, 2)]
    [InlineData(17, 4)]
    [InlineData(17, 6)]
    public void WeakCenterSignalUsesActualFilteredNoiseAndKeepsANonzeroRelease(int key, double filteredNoise)
    {
        var config = ConfigBlob.Defaults();
        var result = GuidedCalibrationAlgorithm.Compute([Input(key) with { FilteredNoiseSpan = filteredNoise }], config, 0.5);
        var outcome = Assert.Single(result.Keys);
        Assert.Equal(GuidedKeyStatus.Measured, outcome.Status);
        Assert.Equal(8, outcome.SafeLower);
        Assert.Equal(8, outcome.SafeUpper);
        Assert.Equal(8, outcome.KeyThreshold);
        Assert.Equal(1, GuidedCalibrationAlgorithm.HardwareReleaseThreshold(outcome.KeyThreshold));
        Assert.Equal(filteredNoise, outcome.CenterFilteredNoiseSpan);
        var changed = GuidedCalibrationAlgorithm.Apply(result, config);
        Assert.Equal(config.Boards, changed.Boards);
        Assert.Equal(config.Mapping, changed.Mapping);
        for (var i = 0; i < 34; i++)
            Assert.Equal(i == key ? 8 : Threshold(config, i), Threshold(changed, i));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(18)]
    [InlineData(33)]
    public void NonCenterKeysKeepTheOriginalNoisePolicy(int key)
    {
        var outcome = GuidedCalibrationAlgorithm.Compute([Input(key)], ConfigBlob.Defaults(), 0.5).Keys.Single();
        Assert.Equal(GuidedKeyStatus.InsufficientDepth, outcome.Status);
        Assert.Equal(24, outcome.SafeLower);
        Assert.Equal(0, outcome.SafeUpper);
        Assert.Null(outcome.CenterFilteredNoiseSpan);
    }

    [Fact]
    public void CenterProfileRequiresIndependentNoiseEvidence()
    {
        var input = Input() with { FilteredNoiseSpan = null };
        Assert.False(input.UsesCenterProfile);
        var outcome = GuidedCalibrationAlgorithm.Compute([input], ConfigBlob.Defaults(), 0.5).Keys.Single();
        Assert.Equal(GuidedKeyStatus.InsufficientDepth, outcome.Status);
        Assert.Equal(24, outcome.SafeLower);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("cross-talk")]
    [InlineData("release")]
    [InlineData("verification lower")]
    [InlineData("verification upper")]
    [InlineData("real noise")]
    [InlineData("too weak")]
    public void WeakProfileDoesNotBypassPhysicalOrVerificationLimits(string obstacle)
    {
        var input = obstacle switch
        {
            "idle" => Input() with { IdleUpper = 4 },
            "cross-talk" => Input() with { CrossTalk = 8 },
            "release" => Input() with { ReleaseUpper = 4 },
            "verification lower" => Input() with { MinimumThreshold = 12 },
            "verification upper" => Input() with { MaximumThreshold = 4 },
            "real noise" => Input() with { FilteredNoiseSpan = 8, IdleUpper = 4 },
            _ => Input() with { PressDepth = 7 }
        };
        var config = ConfigBlob.Defaults();
        var result = GuidedCalibrationAlgorithm.Compute([input], config, 0.5);
        Assert.Equal(GuidedKeyStatus.InsufficientDepth, result.Keys.Single().Status);
        Assert.Equal(config.Encode(), GuidedCalibrationAlgorithm.Apply(result, config).Encode());
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void AcquisitionSeparatesBaselineStepsFromNoiseAndCountsWeakRawCycles(int key)
    {
        var capture = new Capture(key);
        capture.Baseline();
        for (var i = 0; i < 3; i++) capture.Press(hardware: false);
        Assert.True(capture.Session.IsComplete);
        var input = Assert.Single(capture.Session.Measurements());
        Assert.Equal(8, input.NoiseSpan);
        Assert.Equal(0d, input.FilteredNoiseSpan);
        Assert.Equal(11, input.PressDepth);
        Assert.Equal(3, input.Samples.Count);
        Assert.All(input.Samples, p => Assert.False(p.HardwareSeen));
        Assert.Equal(GuidedKeyStatus.Measured, capture.Session.BuildResult(capture.Config, 0.5).Keys.Single().Status);
        Assert.Equal(8, capture.Session.BuildResult(capture.Config, 0.5).Keys.Single().KeyThreshold);
    }

    [Fact]
    public void ProfileFollowsCenterKeyAcrossCustomMappingNotTheOriginalChannel()
    {
        var config = ConfigBlob.Defaults();
        (config.Mapping[0], config.Mapping[17]) = (config.Mapping[17], config.Mapping[0]);
        var capture = new Capture(17, config: config);
        capture.Baseline();
        for (var i = 0; i < 3; i++) capture.Press(hardware: false);
        var outcome = capture.Session.BuildResult(config, 0.5).Keys.Single();
        Assert.Equal(KeyMapping.ChannelIndexOf(config.Mapping[17]), outcome.Channel);
        Assert.Equal(GuidedKeyStatus.Measured, outcome.Status);
        Assert.Equal(8, outcome.KeyThreshold);
    }

    [Fact]
    public void BaselineStepsWithoutAFilteredExcursionCannotBecomeCenterPresses()
    {
        var capture = new Capture();
        capture.Baseline(); capture.Frames(5);
        for (var i = 0; i < 5; i++)
        {
            capture.Frames(8, baseline: 408);
            capture.Frames(5);
        }
        Assert.Equal(0, capture.Session.CompletedRepeats);
        Assert.Equal(GuidedRepeatState.WaitingForPress, capture.Session.RepeatState);
    }

    [Fact]
    public void BaselineFollowingAHeldCenterTouchCannotSplitItIntoRawRepeats()
    {
        var capture = new Capture();
        capture.Baseline(); capture.Frames(5);
        capture.Frames(10, filtered: 389);
        capture.Frames(30, filtered: 389, baseline: 388);
        Assert.Equal(0, capture.Session.CompletedRepeats);
        Assert.Equal(GuidedRepeatState.Pressed, capture.Session.RepeatState);
        capture.Frames(5);
        Assert.Equal(1, capture.Session.CompletedRepeats);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CenterTrialStillRequiresEightRealHardwareTouchesAndReleases(bool hardware)
    {
        var capture = new Capture(repeats: 8, verification: true);
        capture.Baseline();
        for (var i = 0; i < 8; i++) capture.Press(hardware);
        capture.Frames(40);
        Assert.True(capture.Session.IsComplete);
        var report = GuidedVerificationReport.From([capture.Target], capture.Session);
        Assert.Equal(hardware, report.Passed);
        Assert.Equal(hardware ? "Guided.VerifyPassed" : "Guided.VerifyMissed", report.Keys.Single().ReasonKey);
    }

    [Fact]
    public void CenterNoiseEvidenceOwnsItsSnapshotsRatherThanReusedSerialBuffers()
    {
        var capture = new Capture();
        capture.Baseline(filteredNoise: true);
        for (var i = 0; i < 3; i++) capture.Press(hardware: false);
        // Capture.Frame reuses the same arrays throughout the entire session.
        Assert.Equal(2d, capture.Session.Measurements().Single().FilteredNoiseSpan);
    }

    [Fact]
    public void NegativeIdleBiasDoesNotPreventCenterArmingAfterBaselineSettles()
    {
        var capture = new Capture();
        for (var i = 0; i < 20; i++) capture.Frame(baseline: 392 + 4 * (i % 2));
        for (var i = 0; i < 3; i++) capture.Press(hardware: false);
        Assert.True(capture.Session.IsComplete);
        var input = Assert.Single(capture.Session.Measurements());
        Assert.Equal(-4, input.IdleUpper);
        Assert.Equal(11, input.PressDepth);
        Assert.Equal(8, capture.Session.BuildResult(capture.Config, 0.5).Keys.Single().KeyThreshold);
    }

    [Fact]
    public void CenterVerificationStillRejectsNeighborTouches()
    {
        var capture = new Capture(repeats: 3, verification: true);
        capture.Baseline();
        for (var i = 0; i < 3; i++)
        {
            capture.Frames(5);
            capture.Frames(7, filtered: 389, hardware: true, extraBits: 1UL << 18);
            capture.Frames(5);
        }
        capture.Frames(40);
        Assert.True(capture.Session.IsComplete);
        Assert.Equal("Guided.VerifyCrossTalk", GuidedVerificationReport.From([capture.Target], capture.Session).Keys.Single().ReasonKey);
    }

    [Fact]
    public void CenterProfileIsClearlyLabelledInBothLanguages()
    {
        var outcome = GuidedCalibrationAlgorithm.Compute([Input()], ConfigBlob.Defaults(), 0.5).Keys.Single();
        var preview = new GuidedKeyPreview(outcome);
        var changed = new List<string?>();
        preview.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        try
        {
            Localizer.Instance.SetLanguage("zh-Hans");
            Assert.Contains("C 区弱信号", preview.SafetyDetail);
            Assert.Contains("须硬件验证", preview.SafetyDetail);
            Assert.Contains(nameof(GuidedKeyPreview.SafetyDetail), changed);
            Localizer.Instance.SetLanguage("en");
            Assert.Contains("C-area weak signal", preview.SafetyDetail);
        }
        finally { Localizer.Instance.SetLanguage("en"); }
    }

    private static int Threshold(ConfigBlob config, int key)
    {
        var board = config.Boards[KeyMapping.BoardOf(config.Mapping[key])];
        return board.BaseThreshold + board.SensOffset + config.ElectrodeOffsets[key];
    }

    private sealed class Capture
    {
        public ConfigBlob Config { get; }
        public GuidedCalibrationSession.TargetKey Target { get; }
        public GuidedCalibrationSession Session { get; }
        private uint time;
        private readonly ushort[] filteredBuffer = Enumerable.Repeat((ushort)400, 48).ToArray();
        private readonly ushort[] baselineBuffer = Enumerable.Repeat((ushort)400, 48).ToArray();

        public Capture(int key = 17, int repeats = 3, bool verification = false, ConfigBlob? config = null)
        {
            Config = config ?? ConfigBlob.Defaults();
            Target = new(key, KeyMapping.KeyLabels[key], KeyMapping.ChannelIndexOf(Config.Mapping[key]), KeyMapping.BoardOf(Config.Mapping[key]));
            Session = new([Target], baselineDurationMs: 600, warmupDurationMs: 0,
                requiredRepeats: repeats, allowRawRecovery: !verification);
            Session.Begin(0);
        }

        public void Frame(int filtered = 400, int baseline = 400, bool hardware = false, ulong extraBits = 0)
        {
            time += 30;
            Array.Fill(filteredBuffer, (ushort)400);
            Array.Fill(baselineBuffer, (ushort)400);
            filteredBuffer[Target.Channel] = (ushort)filtered;
            baselineBuffer[Target.Channel] = (ushort)baseline;
            Session.PushFrame(time, filteredBuffer, baselineBuffer, (hardware ? 1UL << Target.KeyIndex : 0) | extraBits, [7, 7, 7, 7]);
        }

        public void Frames(int count, int filtered = 400, int baseline = 400, bool hardware = false, ulong extraBits = 0)
        {
            for (var i = 0; i < count; i++) Frame(filtered, baseline, hardware, extraBits);
        }

        public void Baseline(bool filteredNoise = false)
        {
            for (var i = 0; i < 20; i++) Frame(filtered: 400 + (filteredNoise ? 2 * (i % 2) : 0), baseline: 392 + 4 * (i % 3));
            Assert.Equal(GuidedPhase.Measuring, Session.Phase);
        }

        public void Press(bool hardware)
        {
            Frames(5);
            Frames(7, filtered: 389, hardware: hardware);
            Frames(5);
        }
    }
}
