using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

public enum GuidedPhase { Idle, Warmup, Baseline, Measuring, FinalIdle, Complete }
public enum GuidedRepeatState { WaitingForPress, Pressed, Done }

public sealed record GuidedSignalSample(ulong TimestampMs, double Drop, bool HardwareTouched, ushort Filtered, ushort Baseline);
public sealed record GuidedPressSample(double Peak, bool HardwareSeen, IReadOnlyList<GuidedSignalSample> Series)
{
    public bool HardwareReleased { get; init; } = true;
    public double ReleaseUpper { get; init; }
}

/// <summary>
/// Captures actions independently of the thresholds being calibrated. Hardware touch is sufficient
/// evidence; raw-signal detection also works when the old hardware threshold is too high.
/// Owned by one thread (the UI dispatcher in the application).
/// </summary>
public sealed class GuidedCalibrationSession
{
    public sealed record TargetKey(int KeyIndex, string Label, int Channel, int Board);
    private readonly List<TargetKey> targets;
    private readonly List<double>[] idle = Enumerable.Range(0, 48).Select(_ => new List<double>()).ToArray();
    private readonly double[] idleMedian = new double[48];
    private readonly double[] idleUpper = new double[48];
    private readonly double[] idleNoise = new double[48];
    private readonly double[] idleFilteredNoise = new double[48];
    private readonly double[] idleFilteredMedian = new double[48];
    private readonly Queue<(ulong Time, ushort Value)> centerQuiet = new();
    private double centerReference;
    private readonly GuidedIdleStatistics[] idleStatistics = Enumerable.Range(0, 48)
        .Select(_ => new GuidedIdleStatistics(0, 0, 0, 0, false)).ToArray();
    private readonly Queue<GuidedIdleObservation> idleWindow = new();
    private readonly List<GuidedIdleObservation> finalIdleWindow = [];
    private ulong lastIdleAnalysis;
    private bool baselineHadProblem;
    private double baselineStableSince = -1;
    private readonly double[] crossTalk = new double[48];
    private readonly List<GuidedKeyInput> inputs = [];
    private readonly List<GuidedPressSample> presses = [];
    private readonly List<GuidedSignalSample> series = [];
    private readonly Queue<(ulong Time, double[] Values)> settling = new();
    private readonly int warmupDurationMs;
    private readonly int requiredRepeats;
    private readonly bool allowRawRecovery;
    private double stuckSince = -1;
    private bool rawOnlyArmed;
    private bool releaseStuck;
    private bool rawRecoveryUsed;
    private double releaseUpper;
    private const int StuckConfirmationMs = 600;
    private readonly double[] finalStuckSince = Enumerable.Repeat(-1.0, 34).ToArray();
    private ulong phaseStartMs;
    private ulong keyStartMs;
    private uint lastTimestampMs;
    private ulong monotonicMs;
    private double quietSince = -1;
    private double rawSince = -1;
    private double releasedSince = -1;
    private bool armed;
    private bool hardwareSeen;
    private double currentPeak;
    private ulong idleTouchedBits;
    private bool unexpectedTouch;
    private ulong unexpectedTouchBits;
    private double invalidSince = -1;
    private ulong baselineWindowStart;

    public GuidedCalibrationSession(IEnumerable<TargetKey> keys, int baselineDurationMs = 3000,
        int perKeyTimeoutMs = 45000, int warmupDurationMs = 1000, int requiredRepeats = 5,
        bool allowRawRecovery = true)
    {
        targets = keys.ToList();
        if (baselineDurationMs <= 0 || perKeyTimeoutMs <= 0 || warmupDurationMs < 0 || requiredRepeats < 3)
            throw new ArgumentOutOfRangeException(nameof(baselineDurationMs));
        if (targets.Any(k => k.Channel is < 0 or >= 48 || k.Board != k.Channel / 12 || k.KeyIndex is < 0 or >= 34)
            || targets.Select(k => k.KeyIndex).Distinct().Count() != targets.Count)
            throw new ArgumentException("Invalid calibration targets.", nameof(keys));
        BaselineDurationMs = baselineDurationMs;
        PerKeyTimeoutMs = perKeyTimeoutMs;
        this.warmupDurationMs = warmupDurationMs;
        this.requiredRepeats = requiredRepeats;
        this.allowRawRecovery = allowRawRecovery;
    }

    public int BaselineDurationMs { get; }
    public int PerKeyTimeoutMs { get; }
    public GuidedPhase Phase { get; private set; }
    public int CurrentIndex { get; private set; }
    public int RepeatsPerKey => requiredRepeats;
    public int CompletedRepeats => presses.Count;
    public GuidedRepeatState RepeatState { get; private set; }
    public double LiveDepth { get; private set; }
    public bool CurrentKeyTimedOut { get; private set; }
    public string? Fault { get; private set; }
    public bool CurrentHardwareStuck => stuckSince >= 0 && monotonicMs - stuckSince >= 300;
    public TargetKey? CurrentTarget => Phase == GuidedPhase.Measuring && CurrentIndex < targets.Count ? targets[CurrentIndex] : null;
    public bool IsComplete => Phase == GuidedPhase.Complete;
    public uint CurrentTimestampMs => lastTimestampMs;
    public bool CanFinishCurrentKeyEarly => allowRawRecovery && Phase == GuidedPhase.Measuring &&
        CompletedRepeats >= 3 && RepeatState != GuidedRepeatState.Pressed;
    public IReadOnlyList<GuidedKeyInput> Inputs => inputs;
    public ulong IdleTouchedBits => idleTouchedBits;
    public bool BaselineRetrying { get; private set; }
    public string BaselineProblemKeys { get; private set; } = "";
    public double BaselineProgress => Phase == GuidedPhase.Baseline
        ? Math.Clamp((monotonicMs - phaseStartMs) / (double)BaselineDurationMs, 0, 1) : 0;
    public double KeyProgress => CompletedRepeats / (double)requiredRepeats;
    public double TotalProgress => Phase switch
    {
        GuidedPhase.Idle => 0,
        GuidedPhase.Warmup => 0.05,
        GuidedPhase.Baseline => 0.05 + 0.1 * BaselineProgress,
        GuidedPhase.Complete => 1,
        GuidedPhase.FinalIdle => 0.99,
        _ => 0.15 + 0.85 * (CurrentIndex + KeyProgress) / Math.Max(1, targets.Count)
    };

    public void Begin(uint timestampMs)
    {
        if (Phase != GuidedPhase.Idle) throw new InvalidOperationException("A calibration session cannot be restarted.");
        Phase = warmupDurationMs > 0 ? GuidedPhase.Warmup : GuidedPhase.Baseline;
        lastTimestampMs = timestampMs;
    }

    public void PushFrame(uint rawTimestampMs, ushort[] filtered, ushort[] baseline, ulong? touchBits = null, byte[]? boardStatus = null)
    {
        if (Phase is GuidedPhase.Idle or GuidedPhase.Complete) return;
        var gap = unchecked(rawTimestampMs - lastTimestampMs);
        lastTimestampMs = rawTimestampMs;
        var timestampMs = monotonicMs += gap;
        if (gap > 5000)
        {
            Fault = "Guided.NoFrames";
            Phase = GuidedPhase.Complete;
            return;
        }
        var valid = filtered.Length >= 48 && baseline.Length >= 48 && targets.All(k =>
            filtered[k.Channel] is > 0 and <= 1023 && baseline[k.Channel] is > 0 and <= 1023 &&
            (boardStatus is null || (k.Board < boardStatus.Length && (boardStatus[k.Board] & 15) == 7)));
        if (!valid || gap > 250)
        {
            if (invalidSince < 0) invalidSince = timestampMs;
            if (timestampMs - invalidSince >= 5000)
            {
                Fault = "Guided.NoFrames";
                Phase = GuidedPhase.Complete;
                return;
            }
            ResetAction();
            settling.Clear();
            if (Phase == GuidedPhase.FinalIdle)
            {
                phaseStartMs = timestampMs;
                Array.Fill(finalStuckSince, -1.0);
                finalIdleWindow.Clear();
            }
            if (Phase == GuidedPhase.Baseline)
            {
                foreach (var values in idle) values.Clear();
                idleWindow.Clear();
                baselineStableSince = -1;
                phaseStartMs = timestampMs;
                idleTouchedBits = 0;
            }
            if (Phase == GuidedPhase.Measuring && timestampMs - keyStartMs >= (ulong)PerKeyTimeoutMs) AdvanceKey(true);
            if (!valid) return;
        }
        else invalidSince = -1;

        if (Phase == GuidedPhase.Warmup)
        {
            settling.Enqueue((timestampMs, targets.Select(k => (double)filtered[k.Channel]).ToArray()));
            while (settling.Count > 1 && timestampMs - settling.Peek().Time > 600) settling.Dequeue();
            var stable = settling.Count >= 2 && timestampMs - settling.Peek().Time >= 400 &&
                Enumerable.Range(0, targets.Count).All(i => GuidedStatistics.Quantile(settling.Select(s => s.Values[i]), 0.9) -
                    GuidedStatistics.Quantile(settling.Select(s => s.Values[i]), 0.1) <= 12);
            if (timestampMs - phaseStartMs >= (ulong)warmupDurationMs && stable)
            {
                Phase = GuidedPhase.Baseline;
                phaseStartMs = baselineWindowStart = timestampMs;
            }
            else if (timestampMs - phaseStartMs >= 10000) { Fault = "Guided.Unstable"; Phase = GuidedPhase.Complete; }
            return;
        }

        if (Phase == GuidedPhase.Baseline)
        {
            if (timestampMs - baselineWindowStart > (ulong)BaselineDurationMs + 10000)
            {
                Fault = "Guided.NoFrames";
                Phase = GuidedPhase.Complete;
                return;
            }
            var drops = Enumerable.Range(0, 48).Select(ch => filtered[ch] > 0 && baseline[ch] > 0
                ? (double)baseline[ch] - filtered[ch] : double.NaN).ToArray();
            idleWindow.Enqueue(new(timestampMs, drops, touchBits ?? 0, (ushort[])filtered.Clone()));
            while (idleWindow.Count > 2000 || idleWindow.Count > 1 && timestampMs - idleWindow.Peek().TimeMs > (ulong)BaselineDurationMs)
                idleWindow.Dequeue();
            if (timestampMs - phaseStartMs < (ulong)BaselineDurationMs) return;
            if (lastIdleAnalysis > 0 && timestampMs - lastIdleAnalysis < 100) return;
            lastIdleAnalysis = timestampMs;
            var window = idleWindow.ToArray();
            idleTouchedBits = 0;
            for (var ch = 0; ch < 48; ch++)
            {
                idle[ch].Clear();
                idle[ch].AddRange(window.Select(f => f.Drops[ch]).Where(double.IsFinite));
                idleStatistics[ch] = GuidedIdleStatistics.From(idle[ch]);
            }
            if (targets.Any(k => idle[k.Channel].Count < Math.Min(20, Math.Max(3, BaselineDurationMs / 100)))) return;
            foreach (var target in targets)
                if (GuidedIdleStatistics.HasIdleTouch(window, target.KeyIndex)) idleTouchedBits |= 1UL << target.KeyIndex;
            var problems = targets.Where(k => idleStatistics[k.Channel].Unstable ||
                !allowRawRecovery && (idleTouchedBits & (1UL << k.KeyIndex)) != 0).ToArray();
            if (problems.Length > 0)
            {
                baselineHadProblem = true;
                baselineStableSince = -1;
                BaselineProblemKeys = string.Join(", ", problems.Select(k => k.Label));
            }
            else if (baselineStableSince < 0) baselineStableSince = timestampMs;
            BaselineRetrying = problems.Length > 0 || baselineHadProblem &&
                timestampMs - baselineStableSince < (ulong)Math.Min(600, BaselineDurationMs);
            // Retry with a recent rolling window, rather than permanently poisoning the round
            // with an early spike/drift. A persistently bad key must not stall all other boards.
            if (BaselineRetrying && timestampMs - phaseStartMs < (ulong)BaselineDurationMs + 6000) return;
            for (var ch = 0; ch < 48; ch++)
            {
                idleMedian[ch] = idleStatistics[ch].Median;
                idleUpper[ch] = idleStatistics[ch].Upper;
                idleNoise[ch] = idleStatistics[ch].Noise;
                var filteredValues = window.Where(f => f.Filtered![ch] is > 0 and <= 1023)
                    .Select(f => (double)f.Filtered![ch]).ToArray();
                idleFilteredNoise[ch] = GuidedStatistics.Quantile(filteredValues, 0.95) - GuidedStatistics.Quantile(filteredValues, 0.05);
                idleFilteredMedian[ch] = GuidedStatistics.Quantile(filteredValues, 0.5);
            }
            Phase = targets.Count == 0 ? GuidedPhase.Complete : GuidedPhase.Measuring;
            BeginCurrentKey();
            return;
        }
        if (Phase == GuidedPhase.FinalIdle)
        {
            finalIdleWindow.Add(new(timestampMs, [], touchBits ?? 0));
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var drop = (double)baseline[input.Channel] - filtered[input.Channel];
                var touched = (touchBits.GetValueOrDefault() & (1UL << input.KeyIndex)) != 0;
                if (touched && drop <= RawIdleCeiling(input.KeyIndex, input.Channel) + RawNoiseMargin(input.KeyIndex, input.Channel) * 0.5)
                {
                    if (finalStuckSince[input.KeyIndex] < 0) finalStuckSince[input.KeyIndex] = timestampMs;
                    if (timestampMs - finalStuckSince[input.KeyIndex] >= StuckConfirmationMs)
                        inputs[i] = input with { ReleaseStuck = true, ReleaseUpper = Math.Max(input.ReleaseUpper, Math.Max(0, drop)) };
                }
                else finalStuckSince[input.KeyIndex] = -1;
            }
            if (timestampMs - phaseStartMs >= 1000)
            {
                foreach (var target in targets)
                    if (GuidedIdleStatistics.HasIdleTouch(finalIdleWindow, target.KeyIndex)) idleTouchedBits |= 1UL << target.KeyIndex;
                Phase = GuidedPhase.Complete;
            }
            return;
        }
        ObservePress(timestampMs, filtered, baseline, touchBits);
    }

    private void BeginCurrentKey()
    {
        presses.Clear();
        ResetAction();
        CurrentKeyTimedOut = false;
        unexpectedTouch = false;
        unexpectedTouchBits = 0;
        releaseStuck = rawRecoveryUsed = false;
        releaseUpper = 0;
        LiveDepth = 0;
        centerReference = CurrentTarget is { } target ? idleFilteredMedian[target.Channel] : 0;
        keyStartMs = monotonicMs;
    }

    private void ResetAction()
    {
        series.Clear();
        currentPeak = 0;
        hardwareSeen = armed = rawOnlyArmed = false;
        stuckSince = -1;
        quietSince = rawSince = releasedSince = -1;
        centerQuiet.Clear();
        RepeatState = GuidedRepeatState.WaitingForPress;
    }

    private void ObservePress(ulong timestampMs, ushort[] filtered, ushort[] baseline, ulong? bits)
    {
        var target = CurrentTarget;
        if (target is null) return;
        var ch = target.Channel;
        var drop = (double)baseline[ch] - filtered[ch];
        LiveDepth = drop;
        var touched = (bits.GetValueOrDefault() & (1UL << target.KeyIndex)) != 0;
        var center = GuidedCalibrationAlgorithm.IsCenterKey(target.KeyIndex);
        var margin = RawNoiseMargin(target.KeyIndex, ch);
        var idleCeiling = RawIdleCeiling(target.KeyIndex, ch);
        var rawOn = drop > idleCeiling + margin;
        var rawOff = drop <= idleCeiling + margin * 0.5;
        if (center)
        {
            // A baseline step alone is not a touch. Require a real filtered excursion from
            // the immediately preceding quiet interval, even if the old hardware threshold misses it.
            rawOn &= centerReference - filtered[ch] >= Math.Max(4, margin + 1);
            if (RepeatState == GuidedRepeatState.Pressed)
                rawOff &= centerReference - filtered[ch] <= Math.Max(2, margin + 1);
        }

        // A returned raw signal with a latched hardware bit is different from a long held touch.
        // Only acquisition may recover using raw transitions; verification must fail this key.
        if (touched && rawOff)
        {
            if (stuckSince < 0) stuckSince = timestampMs;
            if (timestampMs - stuckSince >= StuckConfirmationMs)
            {
                releaseStuck = true;
                releaseUpper = Math.Max(releaseUpper, Math.Max(0, drop));
                if (!allowRawRecovery) { AdvanceKey(false); return; }
            }
        }
        else stuckSince = -1;

        if (RepeatState == GuidedRepeatState.WaitingForPress)
        {
            // Require a released interval before every action: a held key cannot become the next repeat.
            if (!armed)
            {
                if (rawOff && (!touched || allowRawRecovery && releaseStuck))
                {
                    if (quietSince < 0) quietSince = timestampMs;
                    if (center)
                    {
                        centerQuiet.Enqueue((timestampMs, filtered[ch]));
                        while (centerQuiet.Count > 1 && timestampMs - centerQuiet.Peek().Time > 180) centerQuiet.Dequeue();
                    }
                    if (timestampMs - quietSince >= 90)
                    {
                        armed = true; rawOnlyArmed = touched;
                        if (center) centerReference = GuidedStatistics.Quantile(centerQuiet.Select(s => (double)s.Value), 0.5);
                    }
                }
                else { quietSince = -1; centerQuiet.Clear(); }
            }
            else
            {
                if (rawOn) { if (rawSince < 0) rawSince = timestampMs; }
                else rawSince = -1;
                if ((touched && !rawOnlyArmed) || (rawSince >= 0 && timestampMs - rawSince >= (center ? 60 : 30)))
                {
                    RepeatState = GuidedRepeatState.Pressed;
                    currentPeak = drop;
                    hardwareSeen = touched;
                    unexpectedTouchBits |= bits.GetValueOrDefault() & ~(1UL << target.KeyIndex);
                    series.Clear();
                    series.Add(new(timestampMs, drop, touched, filtered[ch], baseline[ch]));
                }
            }
        }
        else if (RepeatState == GuidedRepeatState.Pressed)
        {
            unexpectedTouch |= (bits.GetValueOrDefault() & ~(1UL << target.KeyIndex)) != 0;
            unexpectedTouchBits |= bits.GetValueOrDefault() & ~(1UL << target.KeyIndex);
            currentPeak = Math.Max(currentPeak, drop);
            hardwareSeen |= touched;
            if (series.Count >= 400) { AdvanceKey(true); return; }
            series.Add(new(timestampMs, drop, touched, filtered[ch], baseline[ch]));
            // Track other channels while this key is held, not while the user moves between keys.
            if (rawOn || touched)
                for (var other = 0; other < 48; other++)
                    if (other != ch && filtered[other] > 0 && baseline[other] > 0)
                        crossTalk[other] = Math.Max(crossTalk[other], (double)baseline[other] - filtered[other]);
            var rawRecovered = allowRawRecovery && touched && rawOff && stuckSince >= 0 &&
                timestampMs - stuckSince >= StuckConfirmationMs;
            var released = !touched && (rawOff || (bits.HasValue && hardwareSeen)) || rawRecovered;
            if (released)
            {
                if (releasedSince < 0) releasedSince = timestampMs;
                if (timestampMs - releasedSince >= 90)
                {
                    var active = series.Where(s => (s.Drop > idleCeiling + margin &&
                            (!center || centerReference - s.Filtered >= Math.Max(4, margin + 1))) ||
                        s.HardwareTouched && !rawOnlyArmed && !rawRecovered).Select(s => s.Drop).ToArray();
                    var response = active.Length > 0 ? GuidedStatistics.Quantile(active, 0.75) : currentPeak;
                    var residual = GuidedStatistics.Quantile(series.Where(s => s.TimestampMs >= releasedSince).Select(s => s.Drop), 0.95);
                    releaseUpper = Math.Max(releaseUpper, Math.Max(0, residual));
                    rawRecoveryUsed |= rawOnlyArmed || rawRecovered;
                    presses.Add(new(response, hardwareSeen, series.ToArray())
                    { HardwareReleased = !touched && !rawOnlyArmed, ReleaseUpper = Math.Max(0, residual) });
                    ResetAction();
                    if (CompletedRepeats >= requiredRepeats) { AdvanceKey(false); return; }
                }
            }
            else releasedSince = -1;
        }
        if (timestampMs - keyStartMs >= (ulong)PerKeyTimeoutMs) AdvanceKey(true);
    }

    public void SkipCurrentKey(uint timestampMs)
    {
        if (Phase != GuidedPhase.Measuring) return;
        lastTimestampMs = timestampMs;
        AdvanceKey(false, skip: true);
    }

    public void FinishCurrentKeyEarly(uint timestampMs)
    {
        if (!CanFinishCurrentKeyEarly || Phase != GuidedPhase.Measuring) return;
        lastTimestampMs = timestampMs;
        AdvanceKey(false);
    }

    private void AdvanceKey(bool timedOut, bool skip = false)
    {
        var target = CurrentTarget;
        if (target is null) return;
        CurrentKeyTimedOut = timedOut;
        var measured = !skip && presses.Count >= Math.Min(3, requiredRepeats);
        var ch = target.Channel;
        inputs.Add(new GuidedKeyInput(target.KeyIndex, target.Label, ch, target.Board,
            measured ? presses.Min(p => p.Peak) : 0, idleNoise[ch], 0, 0, 0, measured)
        {
            IdleUpper = idleUpper[ch], IdleMedian = idleMedian[ch],
            IdleTouched = (idleTouchedBits & (1UL << target.KeyIndex)) != 0,
            IdleUnstable = idleStatistics[ch].Unstable,
            IdleDrift = idleStatistics[ch].Drift, IdleTransient = idleStatistics[ch].Transient,
            TimedOut = timedOut, Samples = presses.ToArray(), UnexpectedTouch = unexpectedTouch || unexpectedTouchBits != 0,
            ReleaseStuck = releaseStuck, RawRecoveryUsed = rawRecoveryUsed, ReleaseUpper = releaseUpper,
            FilteredNoiseSpan = idleFilteredNoise[ch],
            UnexpectedTouchBits = unexpectedTouchBits
        });
        CurrentIndex++;
        if (CurrentIndex >= targets.Count)
        {
            Phase = allowRawRecovery ? GuidedPhase.Complete : GuidedPhase.FinalIdle;
            phaseStartMs = monotonicMs;
            return;
        }
        BeginCurrentKey();
    }

    /// <summary>Read after completion: cross-talk measured on later keys also applies to earlier keys.</summary>
    public IReadOnlyList<GuidedKeyInput> Measurements() => inputs.Select(i => i with { CrossTalk = crossTalk[i.Channel] }).ToArray();
    public double CrossTalkFor(int channel) => crossTalk[channel];
    private double RawIdleCeiling(int keyIndex, int channel) => GuidedCalibrationAlgorithm.IsCenterKey(keyIndex)
        ? Math.Max(0, idleUpper[channel]) : idleUpper[channel];
    private double RawNoiseMargin(int keyIndex, int channel) => Math.Max(1,
        GuidedCalibrationAlgorithm.IsCenterKey(keyIndex)
            ? idleFilteredNoise[channel] * GuidedCalibrationAlgorithm.CenterNoiseFactor : idleNoise[channel]);
    public GuidedCalibrationResult BuildResult(ConfigBlob current, double triggerRatio) =>
        GuidedCalibrationAlgorithm.Compute(Measurements(), current, triggerRatio);
}

public static class GuidedStatistics
{
    public static double Quantile(IEnumerable<double> source, double fraction)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0) return 0;
        var position = Math.Clamp(fraction, 0, 1) * (values.Length - 1);
        var lower = (int)position;
        return values[lower] + (values[Math.Min(lower + 1, values.Length - 1)] - values[lower]) * (position - lower);
    }
}
