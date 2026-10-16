using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

public enum GuidedPhase
{
    Idle,
    Baseline,
    Measuring,
    Complete
}

/// <summary>当前键的逐次按压进度。</summary>
public enum GuidedRepeatState
{
    WaitingForPress,
    Pressed,
    Done
}

/// <summary>
/// 引导式采集状态机：先测静止基线（噪声跨度），再逐键引导"按下→保持→松开"，
/// 记录每次按压的峰值深度与上升/保持/回落耗时。无 UI 依赖，可直接单测。
/// 时间单位为毫秒；与固件/工具界面无关，调用方每次收到调试帧就 PushFrame。
/// </summary>
public sealed class GuidedCalibrationSession
{
    /// <summary>计划引导的键（按引导顺序）。</summary>
    public sealed record TargetKey(int KeyIndex, string Label, int Channel, int Board);

    private const double StartThresholdMin = 12;
    private const double StartThresholdNoiseFactor = 3.0;
    private const int RequiredRepeats = 3;
    private const int MinRepeatsBeforeEarlyFinish = 2;

    private readonly List<TargetKey> targets;
    private readonly double[] minFiltered = new double[48];
    private readonly double[] maxFiltered = new double[48];
    private readonly double[] noiseSpan = new double[48];
    private readonly bool[] noiseSeen = new bool[48];

    private readonly List<GuidedKeyInput> inputs = [];
    private readonly List<double> currentPeaks = [];
    private readonly List<double> currentRises = [];
    private readonly List<double> currentFalls = [];
    private readonly List<double> currentHolds = [];
    private double currentPeak;
    private double pressStartMs;
    private double peakReachedMs;
    private double lastNearPeakMs;
    private double belowThresholdSinceMs = -1;

    private uint baselineStartMs;

    public GuidedCalibrationSession(IEnumerable<TargetKey> keys, int baselineDurationMs = 3000, int perKeyTimeoutMs = 20000)    {
        targets = keys.ToList();
        BaselineDurationMs = baselineDurationMs;
        PerKeyTimeoutMs = perKeyTimeoutMs;
    }

    public int BaselineDurationMs { get; }
    public int PerKeyTimeoutMs { get; }
    public GuidedPhase Phase { get; private set; } = GuidedPhase.Idle;
    public int CurrentIndex { get; private set; }
    public int RepeatsPerKey => RequiredRepeats;
    public int CompletedRepeats => currentPeaks.Count;
    public GuidedRepeatState RepeatState { get; private set; } = GuidedRepeatState.WaitingForPress;
    public double LiveDepth { get; private set; }
    public bool CurrentKeyTimedOut { get; private set; }

    public TargetKey? CurrentTarget => Phase == GuidedPhase.Measuring && CurrentIndex < targets.Count ? targets[CurrentIndex] : null;
    public bool IsComplete => Phase == GuidedPhase.Complete;
    public double BaselineProgress => Phase == GuidedPhase.Baseline
        ? Math.Clamp((lastTimestampMs - baselineStartMs) / (double)BaselineDurationMs, 0, 1)
        : (Phase == GuidedPhase.Idle ? 0 : 1);
    /// <summary>整体进度：基线 0..0.1，逐键均分 0.1..1。</summary>
    public double TotalProgress
    {
        get
        {
            if (Phase == GuidedPhase.Idle) return 0;
            if (Phase == GuidedPhase.Complete) return 1;
            if (targets.Count == 0) return BaselineProgress * 0.1;
            var keyFraction = (CurrentIndex + Math.Clamp(KeyProgress, 0, 1)) / targets.Count;
            return 0.1 + 0.9 * Math.Clamp(keyFraction, 0, 1);
        }
    }
    public double KeyProgress => CompletedRepeats / (double)RequiredRepeats;

    private uint lastTimestampMs;

    /// <summary>最近一帧的时间戳（单调轴），供界面在线程外做"跳过/结束当前键"的推进。</summary>
    public uint CurrentTimestampMs => lastTimestampMs;

    public void Begin(uint timestampMs)
    {
        Phase = GuidedPhase.Baseline;
        baselineStartMs = timestampMs;
        lastTimestampMs = timestampMs;
        Array.Fill(noiseSeen, false);
        for (var channel = 0; channel < 48; channel++) noiseSpan[channel] = 0;
    }

    /// <summary>每次收到调试帧调用。filtered/baseline 为 48 通道原始值。</summary>
    public void PushFrame(uint timestampMs, ushort[] filtered, ushort[] baseline)
    {
        if (Phase is GuidedPhase.Idle or GuidedPhase.Complete) return;
        lastTimestampMs = timestampMs;
        if (Phase == GuidedPhase.Baseline)
        {
            for (var channel = 0; channel < 48 && channel < filtered.Length; channel++)
            {
                if (filtered[channel] == 0 || baseline[channel] == 0) continue;
                var value = (double)filtered[channel];
                if (!noiseSeen[channel]) { minFiltered[channel] = maxFiltered[channel] = value; noiseSeen[channel] = true; }
                else { if (value < minFiltered[channel]) minFiltered[channel] = value; if (value > maxFiltered[channel]) maxFiltered[channel] = value; }
            }
            if (timestampMs - baselineStartMs >= BaselineDurationMs)
            {
                for (var channel = 0; channel < 48; channel++)
                    noiseSpan[channel] = noiseSeen[channel] ? maxFiltered[channel] - minFiltered[channel] : 0;
                Phase = GuidedPhase.Measuring;
                BeginCurrentKey(timestampMs);
            }
            return;
        }
        ObservePress(timestampMs, filtered, baseline);
    }

    private void BeginCurrentKey(uint timestampMs)
    {
        currentPeaks.Clear(); currentRises.Clear(); currentFalls.Clear(); currentHolds.Clear();
        currentPeak = 0; belowThresholdSinceMs = -1;
        RepeatState = GuidedRepeatState.WaitingForPress;
        CurrentKeyTimedOut = false;
        keyStartMs = timestampMs;
    }

    private uint keyStartMs;

    private void ObservePress(uint timestampMs, ushort[] filtered, ushort[] baseline)
    {
        var target = CurrentTarget;
        if (target is null) { Phase = GuidedPhase.Complete; return; }
        var channel = target.Channel;
        if (channel < 0 || channel >= filtered.Length) { AdvanceKey(timestampMs); return; }

        var baselineValue = (double)baseline[channel];
        var filteredValue = (double)filtered[channel];
        LiveDepth = Math.Max(0, baselineValue - filteredValue);

        var startThreshold = Math.Max(StartThresholdMin, noiseSpan[channel] * StartThresholdNoiseFactor);

        if (RepeatState == GuidedRepeatState.WaitingForPress)
        {
            if (LiveDepth >= startThreshold)
            {
                RepeatState = GuidedRepeatState.Pressed;
                pressStartMs = timestampMs;
                peakReachedMs = timestampMs;
                lastNearPeakMs = timestampMs;
                currentPeak = LiveDepth;
                belowThresholdSinceMs = -1;
            }
        }
        else if (RepeatState == GuidedRepeatState.Pressed)
        {
            if (LiveDepth > currentPeak) { currentPeak = LiveDepth; peakReachedMs = timestampMs; }
            // 记录"最后一次接近峰值"的时刻，用于测量回落耗时（不含稳定保持阶段）。
            if (LiveDepth >= currentPeak * 0.9) lastNearPeakMs = timestampMs;
            // 回落判定：低于峰值的 50% 且低于启动阈值，持续一小段时间才算释放，避免抖动误判。
            var releaseThreshold = Math.Min(currentPeak * 0.5, startThreshold);
            if (LiveDepth <= releaseThreshold)
            {
                if (belowThresholdSinceMs < 0) belowThresholdSinceMs = timestampMs;
                else if (timestampMs - belowThresholdSinceMs >= 50)
                {
                    RecordRepeat(timestampMs);
                    if (CompletedRepeats >= RequiredRepeats) { AdvanceKey(timestampMs); return; }
                    RepeatState = GuidedRepeatState.WaitingForPress;
                    currentPeak = 0;
                    belowThresholdSinceMs = -1;
                }
            }
            else belowThresholdSinceMs = -1;
        }

        // 单键整体超时：至少测到 1 次才允许超时结束（否则视为完全跳过）。
        if (!CurrentKeyTimedOut && timestampMs - keyStartMs >= PerKeyTimeoutMs)
        {
            CurrentKeyTimedOut = true;
            if (CompletedRepeats == 0) { SkipCurrentKey(timestampMs); return; }
            AdvanceKey(timestampMs);
        }
    }

    private void RecordRepeat(uint timestampMs)
    {
        currentPeaks.Add(currentPeak);
        currentRises.Add(Math.Max(1, peakReachedMs - pressStartMs));
        // 回落耗时：从最后一次接近峰值到判定释放（不含稳定保持段）。
        currentFalls.Add(Math.Max(1, timestampMs - lastNearPeakMs));
        currentHolds.Add(Math.Max(1, timestampMs - pressStartMs));
    }

    /// <summary>用户点击"跳过此键"。记录为未测量，随后前进。基线阶段调用无效果。</summary>
    public void SkipCurrentKey(uint timestampMs)
    {
        if (Phase != GuidedPhase.Measuring) return;
        var target = CurrentTarget;
        AdvanceKey(timestampMs, target is null
            ? null
            : new GuidedKeyInput(target.KeyIndex, target.Label, target.Channel, target.Board, 0, noiseSpan[target.Channel], 0, 0, 0, Measured: false));
    }

    /// <summary>已测到 ≥2 次时允许提前结束当前键（不足则等同跳过）。</summary>
    public void FinishCurrentKeyEarly(uint timestampMs)
    {
        if (Phase != GuidedPhase.Measuring) return;
        if (CompletedRepeats == 0) { SkipCurrentKey(timestampMs); return; }
        AdvanceKey(timestampMs);
    }

    public bool CanFinishCurrentKeyEarly => CompletedRepeats >= MinRepeatsBeforeEarlyFinish;

    /// <summary>前进到下一键。可传入预先构造好的 input（跳过/超时时用），否则由本键已采到的峰值汇总。</summary>
    private void AdvanceKey(uint timestampMs, GuidedKeyInput? input = null)
    {
        var target = CurrentTarget;
        if (target is not null)
        {
            if (input is not null)
            {
                inputs.Add(input);
            }
            else
            {
                var measured = currentPeaks.Count > 0;
                inputs.Add(new GuidedKeyInput(target.KeyIndex, target.Label, target.Channel, target.Board,
                    measured ? Median(currentPeaks) : 0,
                    noiseSpan[target.Channel],
                    measured ? Median(currentHolds) : 0,
                    measured ? Median(currentRises) : 0,
                    measured ? Median(currentFalls) : 0,
                    measured));
            }
        }
        CurrentIndex++;
        if (CurrentIndex >= targets.Count) { Phase = GuidedPhase.Complete; return; }
        BeginCurrentKey(timestampMs);
    }

    public GuidedCalibrationResult BuildResult(ConfigBlob current, double triggerRatio) =>
        GuidedCalibrationAlgorithm.Compute(inputs, current, triggerRatio);

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
