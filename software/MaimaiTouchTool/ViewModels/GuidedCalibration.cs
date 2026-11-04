using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

/// <summary>单个按键的引导测量状态。预览表逐键如实呈现。</summary>
public enum GuidedKeyStatus
{
    Measured,
    Skipped,
    Timeout,
    /// <summary>按下深度不足：无论怎么设阈值都无法同时既不误触又按得动。</summary>
    InsufficientDepth,
    /// <summary>算出的阈值超出现有配置能表达的范围（&gt;509 或与板级基准差 &gt;127）。</summary>
    Overflow,
    UnstableIdle
}

/// <summary>工具侧逐键采集到的原始测量（不含算法决策）。</summary>
public sealed record GuidedKeyInput(
    int KeyIndex,
    string Label,
    int Channel,
    int Board,
    double PressDepth,
    double NoiseSpan,
    double HoldMs,
    double RiseMs,
    double FallMs,
    bool Measured)
{
    public double IdleUpper { get; init; }
    public double IdleMedian { get; init; }
    public double CrossTalk { get; init; }
    public bool IdleTouched { get; init; }
    public bool IdleUnstable { get; init; }
    public bool IdleTransient { get; init; }
    public double IdleDrift { get; init; }
    public bool TimedOut { get; init; }
    public bool UnexpectedTouch { get; init; }
    public bool ReleaseStuck { get; init; }
    public bool RawRecoveryUsed { get; init; }
    public double ReleaseUpper { get; init; }
    public double? FilteredNoiseSpan { get; init; }
    public bool UsesCenterProfile => GuidedCalibrationAlgorithm.IsCenterKey(KeyIndex) &&
        FilteredNoiseSpan is { } noise && double.IsFinite(noise) && noise >= 0;
    public ulong UnexpectedTouchBits { get; init; }
    public int MinimumThreshold { get; init; } = 1;
    public int MaximumThreshold { get; init; } = 508;
    public IReadOnlyList<GuidedPressSample> Samples { get; init; } = [];
}

/// <summary>逐键的算法结论，供预览表展示。</summary>
public sealed record GuidedKeyOutcome(
    int KeyIndex,
    string Label,
    int Channel,
    int Board,
    double PressDepth,
    double NoiseSpan,
    int OldOffset,
    int NewOffset,
    int KeyThreshold,
    GuidedKeyStatus Status)
{
    public int SafeLower { get; init; }
    public int SafeUpper { get; init; }
    public int SampleCount { get; init; }
    public bool HadStuckRelease { get; init; }
    public bool IdleTouched { get; init; }
    public bool IdleTransient { get; init; }
    public double IdleDrift { get; init; }
    public GuidedSafetyLimits? SafetyLimits { get; init; }
    public double? CenterFilteredNoiseSpan { get; init; }
}

/// <summary>Independent, quantized constraints so a failed interval is explainable.</summary>
public sealed record GuidedSafetyLimits(int IdleLower, int CrossTalkLower, int ReleaseLower,
    int VerificationLower, int PressUpper, int VerificationUpper)
{
    public int Lower => Math.Max(GuidedCalibrationAlgorithm.MinimumSafeThreshold,
        Math.Max(Math.Max(IdleLower, CrossTalkLower), Math.Max(ReleaseLower, VerificationLower)));
    public int Upper => Math.Min(PressUpper, VerificationUpper);
}

/// <summary>板级（含 baseThreshold / sensOffset / DT / DR）的旧值→新值。</summary>
public sealed record GuidedBoardOutcome(
    int BoardIndex,
    int OldBaseThreshold,
    int NewBaseThreshold,
    int OldSensOffset,
    int NewSensOffset,
    int OldTouchDebounce,
    int NewTouchDebounce,
    int OldReleaseDebounce,
    int NewReleaseDebounce,
    int MeasuredKeys)
{
    public bool ThresholdChanged => NewBaseThreshold != OldBaseThreshold || NewSensOffset != OldSensOffset;
    public bool DebounceChanged => NewTouchDebounce != OldTouchDebounce || NewReleaseDebounce != OldReleaseDebounce;
}

/// <summary>一次引导式强力调整的完整结果。</summary>
public sealed record GuidedCalibrationResult(
    IReadOnlyList<GuidedKeyOutcome> Keys,
    IReadOnlyList<GuidedBoardOutcome> Boards,
    double TriggerRatio,
    bool HasWarning)
{
    public int MeasuredCount => Keys.Count(key => key.Status == GuidedKeyStatus.Measured);
    public int SkippedCount => Keys.Count(key => key.Status == GuidedKeyStatus.Skipped);
    public int FailedCount => Keys.Count(key => key.Status != GuidedKeyStatus.Measured);
}

/// <summary>
/// Pure board-wise constrained solver. Targets are absolute, quantized thresholds within each
/// key's measured safety interval. DT/DR/SFI/ESI and the firmware hardware settings are unchanged.
/// </summary>
public static class GuidedCalibrationAlgorithm
{
    public const double DefaultTriggerRatio = 0.5;
    /// <summary>Noise margin, in the same units as the streamed baseline-filtered signal.</summary>
    public const double NoiseFactor = 2.0;
    /// <summary>C1/C2 use one-sided filtered noise; absolute idle/residual/cross-talk bounds still apply.</summary>
    public const double CenterNoiseFactor = 0.5;
    public static bool IsCenterKey(int keyIndex) => keyIndex is 16 or 17;
    /// <summary>Match the current firmware conversion ceil(touchDelta/4); quantize ABSOLUTE targets.</summary>
    public const int StepQuantum = 4;
    public const int ReleaseHysteresis = 4;
    public const int MinimumSafeThreshold = 8;

    /// <summary>Mirror firmware clamp + ceil conversion, including its fixed release hysteresis.</summary>
    public static int HardwareReleaseThreshold(int threshold) =>
        (Math.Max(0, Math.Clamp(threshold, 1, 1023) - ReleaseHysteresis) + StepQuantum - 1) / StepQuantum;
    /// <summary>baseThreshold(255) + sensOffset(127) + electrodeOffset(127) 的可表达上限。</summary>
    public const int MaxRepresentableThreshold = 255 + 127 + 127;
    public const int MinElectrodeOffset = -127;
    public const int MaxElectrodeOffset = 127;
    public const int MaxBaseThreshold = 255;
    public const int MaxSensOffset = 127;
    public const int MinSensOffset = -127;
    public const int MaxDebounce = 7;
    /// <summary>触发比例的可调范围。</summary>
    public const double MinTriggerRatio = 0.2;
    public const double MaxTriggerRatio = 0.9;

    public static GuidedSafetyLimits SafetyLimitsFor(GuidedKeyInput input)
    {
        // The usual difference span includes baseline register steps. On weak center electrodes,
        // do not add that whole span twice on top of the already measured absolute idle envelope.
        // Only use this profile when separate filtered-noise evidence was actually collected.
        var margin = input.UsesCenterProfile
            ? Math.Max(1, input.FilteredNoiseSpan!.Value * CenterNoiseFactor)
            : Math.Max(1, input.NoiseSpan * NoiseFactor);
        var pressMargin = input.UsesCenterProfile ? margin : Math.Max(1, input.NoiseSpan);
        return new(
            QuantizeUp(Math.Max(0, input.IdleUpper) + ReleaseHysteresis + margin + 1),
            QuantizeUp(input.CrossTalk + margin + 1),
            QuantizeUp(Math.Max(0, input.ReleaseUpper) + ReleaseHysteresis + margin + 1),
            QuantizeUp(input.MinimumThreshold),
            (int)Math.Floor((input.PressDepth - pressMargin) / StepQuantum) * StepQuantum,
            (int)Math.Floor(input.MaximumThreshold / (double)StepQuantum) * StepQuantum);
    }

    public static GuidedCalibrationResult Compute(
        IReadOnlyList<GuidedKeyInput> inputs,
        ConfigBlob current,
        double triggerRatio)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(current);
        var ratio = Math.Clamp(triggerRatio, MinTriggerRatio, MaxTriggerRatio);

        if (inputs.Any(i => i.KeyIndex < 0 || i.KeyIndex >= 34 || i.Board < 0 || i.Board >= current.Boards.Length ||
                i.Channel != KeyMapping.ChannelIndexOf(current.Mapping[i.KeyIndex]) || i.Board != i.Channel / 12))
            throw new ArgumentException("Measurements do not match the configuration mapping.", nameof(inputs));
        if (inputs.Select(i => i.KeyIndex).Distinct().Count() != inputs.Count)
            throw new ArgumentException("Duplicate calibration measurements.", nameof(inputs));
        var lower = new int[inputs.Count];
        var upper = new int[inputs.Count];
        var ideal = new double[inputs.Count];
        var thresholds = new int[inputs.Count];
        var statuses = new GuidedKeyStatus[inputs.Count];
        var limits = new GuidedSafetyLimits[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            // Release must clear both idle noise and the measured post-touch residual, not merely
            // be nonzero. A negative idle bias must never permit a zero hardware release threshold.
            limits[i] = SafetyLimitsFor(input);
            lower[i] = limits[i].Lower;
            upper[i] = limits[i].Upper;
            ideal[i] = Math.Clamp(input.IdleMedian + (input.PressDepth - input.IdleMedian) * ratio,
                lower[i], Math.Max(lower[i], upper[i]));
            statuses[i] = input.TimedOut ? GuidedKeyStatus.Timeout : !input.Measured ? GuidedKeyStatus.Skipped
                : ((input.IdleTouched || input.ReleaseStuck) && !input.RawRecoveryUsed) || input.IdleUnstable ? GuidedKeyStatus.UnstableIdle
                : lower[i] > upper[i] ? GuidedKeyStatus.InsufficientDepth : GuidedKeyStatus.Measured;
        }

        var boardOutcomes = new List<GuidedBoardOutcome>();
        var effectiveBase = new int[current.Boards.Length];
        for (var board = 0; board < current.Boards.Length; board++)
        {
            var old = current.Boards[board];
            var indices = Enumerable.Range(0, inputs.Count).Where(i => inputs[i].Board == board && statuses[i] == GuidedKeyStatus.Measured).ToArray();
            var mappedCount = current.Mapping.Count(p => KeyMapping.BoardOf(p) == board);
            var oldBase = old.BaseThreshold + old.SensOffset;
            var chosenBase = oldBase;
            // Rebalance only complete boards. A subset must not move unmeasured/failed keys.
            var complete = indices.Length > 0 && indices.Length == mappedCount;
            var bases = complete ? Enumerable.Range(1 + MinSensOffset, 255 + MaxSensOffset - (1 + MinSensOffset) + 1) : [oldBase];
            var bestCost = double.PositiveInfinity;
            var median = GuidedStatistics.Quantile(indices.Select(i => ideal[i]), 0.5);
            foreach (var candidateBase in bases)
            {
                var cost = 0.01 * Math.Pow(candidateBase - median, 2);
                foreach (var i in indices)
                {
                    var lo = Math.Max(lower[i], QuantizeUp(candidateBase + MinElectrodeOffset));
                    var hi = Math.Min(upper[i], (int)Math.Floor((candidateBase + MaxElectrodeOffset) / 4.0) * 4);
                    if (lo > hi) { cost = double.PositiveInfinity; break; }
                    var target = Math.Clamp(RoundToQuantum((int)Math.Round(ideal[i])), lo, hi);
                    cost += Math.Pow(target - ideal[i], 2);
                }
                if (cost < bestCost) { bestCost = cost; chosenBase = candidateBase; }
            }
            effectiveBase[board] = chosenBase;
            foreach (var i in indices)
            {
                var lo = Math.Max(lower[i], QuantizeUp(chosenBase + MinElectrodeOffset));
                var hi = Math.Min(upper[i], (int)Math.Floor((chosenBase + MaxElectrodeOffset) / 4.0) * 4);
                if (lo > hi || (complete && double.IsPositiveInfinity(bestCost))) statuses[i] = GuidedKeyStatus.Overflow;
                else thresholds[i] = Math.Clamp(RoundToQuantum((int)Math.Round(ideal[i])), lo, hi);
            }
            var newBase = chosenBase == oldBase ? old.BaseThreshold : Math.Clamp(chosenBase, 1, 255);
            var newSens = chosenBase == oldBase ? old.SensOffset : chosenBase - newBase;
            boardOutcomes.Add(new(board + 1, old.BaseThreshold, newBase, old.SensOffset, newSens,
                old.TouchDebounce, old.TouchDebounce, old.ReleaseDebounce, old.ReleaseDebounce,
                indices.Count(i => statuses[i] == GuidedKeyStatus.Measured)));
        }

        var keyOutcomes = new List<GuidedKeyOutcome>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var oldOffset = KeyOffsetFor(current, input.KeyIndex);
            var measured = statuses[i] == GuidedKeyStatus.Measured;
            keyOutcomes.Add(new(input.KeyIndex, input.Label, input.Channel, input.Board,
                input.PressDepth, input.NoiseSpan, oldOffset,
                measured ? thresholds[i] - effectiveBase[input.Board] : oldOffset,
                measured ? thresholds[i] : Math.Max(1, current.Boards[input.Board].BaseThreshold + current.Boards[input.Board].SensOffset + oldOffset), statuses[i])
            { SafeLower = lower[i], SafeUpper = upper[i], SampleCount = input.Samples.Count,
             HadStuckRelease = input.IdleTouched || input.ReleaseStuck,
                IdleTouched = input.IdleTouched, IdleTransient = input.IdleTransient, IdleDrift = input.IdleDrift,
                SafetyLimits = limits[i],
                CenterFilteredNoiseSpan = input.UsesCenterProfile ? input.FilteredNoiseSpan : null });
        }
        return new(keyOutcomes, boardOutcomes, ratio, keyOutcomes.Any(k => k.Status != GuidedKeyStatus.Measured));
    }

    private static int QuantizeUp(double value) => (int)Math.Ceiling(value / StepQuantum) * StepQuantum;

    /// <summary>Feedback constrains the NEXT measurement round, never silently changes an active trial.</summary>
    public static GuidedKeyInput Refine(GuidedKeyInput input, int triedThreshold, bool missedTouch, bool falseTouch,
        bool stuckRelease = false, double releaseUpper = 0) => input with
    {
        MinimumThreshold = falseTouch || stuckRelease ? Math.Max(input.MinimumThreshold, triedThreshold + StepQuantum) : input.MinimumThreshold,
        // A stuck bit is not evidence that the touch threshold was too high.
        MaximumThreshold = missedTouch && !stuckRelease ? Math.Min(input.MaximumThreshold, triedThreshold - StepQuantum) : input.MaximumThreshold,
        ReleaseUpper = Math.Max(input.ReleaseUpper, releaseUpper)
    };

    /// <summary>把实际配置写回用的新 ConfigBlob 由结果生成（仅含被采纳的项）。</summary>
    public static ConfigBlob Apply(GuidedCalibrationResult result, ConfigBlob current, bool applyThresholds = true, bool applyDebounce = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(current);
        return Apply(result, current, _ => true, _ => true, applyThresholds, applyDebounce);
    }

    /// <summary>按逐键/逐板的"采纳"谓词生成新配置。</summary>
    public static ConfigBlob Apply(
        GuidedCalibrationResult result,
        ConfigBlob current,
        Func<GuidedKeyOutcome, bool> adoptKey,
        Func<GuidedBoardOutcome, bool> adoptBoard,
        bool applyThresholds = true,
        bool applyDebounce = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(adoptKey);
        ArgumentNullException.ThrowIfNull(adoptBoard);
        var offsets = (sbyte[])current.ElectrodeOffsets.Clone();
        var boards = new BoardConfig[current.Boards.Length];
        for (var board = 0; board < current.Boards.Length; board++)
        {
            var source = current.Boards[board];
            var outcome = result.Boards.FirstOrDefault(item => item.BoardIndex == board + 1);
            var take = applyThresholds && outcome is not null && outcome.MeasuredKeys > 0 && adoptBoard(outcome);
            // Timing cannot be inferred reliably from the ~33 Hz debug stream. Preserve DT/DR.
            var baseThreshold = take ? (byte)Math.Clamp(outcome!.NewBaseThreshold, 1, MaxBaseThreshold) : source.BaseThreshold;
            var sensOffset = take ? (sbyte)Math.Clamp(outcome!.NewSensOffset, MinSensOffset, MaxSensOffset) : source.SensOffset;
            boards[board] = source with { BaseThreshold = baseThreshold, SensOffset = sensOffset };
        }
        // Resolve absolute targets against the ACTUALLY adopted board base. Also compensate
        // unadopted keys if a board base moves; preserving an offset alone does not preserve sensitivity.
        if (applyThresholds)
            for (var keyIndex = 0; keyIndex < offsets.Length; keyIndex++)
            {
                var board = KeyMapping.BoardOf(current.Mapping[keyIndex]);
                var outcome = result.Keys.FirstOrDefault(k => k.KeyIndex == keyIndex);
                var selected = outcome is not null && outcome.Status == GuidedKeyStatus.Measured && adoptKey(outcome);
                var source = current.Boards[board];
                var target = selected ? outcome!.KeyThreshold : source.BaseThreshold + source.SensOffset + current.ElectrodeOffsets[keyIndex];
                if (selected && (target < MinimumSafeThreshold || HardwareReleaseThreshold(target) == 0))
                    throw new InvalidOperationException(Localizer.Instance["Guided.UnsafeRelease"]);
                var relative = target - boards[board].BaseThreshold - boards[board].SensOffset;
                if (relative is < MinElectrodeOffset or > MaxElectrodeOffset)
                    throw new InvalidOperationException(Localizer.Instance["Guided.SelectionRange"]);
                offsets[keyIndex] = (sbyte)relative;
            }
        return new ConfigBlob(boards, offsets, KeyMapping.Clone(current.Mapping), current.CompensationMode, current.CompensationKeys);
    }

    /// <summary>Round an absolute target to the firmware's four-count conversion quantum.</summary>
    public static int RoundToQuantum(int value)
    {
        var quotient = (int)Math.Round(value / (double)StepQuantum, MidpointRounding.AwayFromZero);
        return quotient * StepQuantum;
    }

    private static int KeyOffsetFor(ConfigBlob config, int keyIndex) =>
        keyIndex >= 0 && keyIndex < config.ElectrodeOffsets.Length ? config.ElectrodeOffsets[keyIndex] : 0;
}

/// <summary>预览表中一行板级结果（带"采纳"勾选）。</summary>
public sealed class GuidedBoardPreview : NotifyModel
{
    private bool adopt = true;
    public GuidedBoardPreview(GuidedBoardOutcome outcome)
    {
        Outcome = outcome;
        Index = outcome.BoardIndex;
        Localizer.Instance.LanguageChanged += (_, _) => { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Detail)); };
    }
    public GuidedBoardOutcome Outcome { get; }
    public int Index { get; }
    public bool Adopt { get => adopt; set { adopt = value; OnPropertyChanged(); } }
    public string Label => string.Format(Localizer.Instance["Config.BoardN"], Index);
    public string Detail
    {
        get
        {
            if (Outcome.MeasuredKeys == 0) return Localizer.Instance["Guided.NoKeysMeasured"];
            var threshold = Localizer.Instance["Guided.BoardThresholdDetail"];
            var debounce = Localizer.Instance["Guided.BoardDebounceDetail"];
            return string.Format(threshold, Outcome.OldBaseThreshold, Outcome.NewBaseThreshold, Outcome.OldSensOffset, Outcome.NewSensOffset)
                   + " · " + string.Format(debounce, Outcome.OldTouchDebounce, Outcome.NewTouchDebounce, Outcome.OldReleaseDebounce, Outcome.NewReleaseDebounce);
        }
    }
}

/// <summary>预览表中一行逐键结果（带"采纳"勾选）。</summary>
public sealed class GuidedKeyPreview : NotifyModel
{
    private bool adopt = true;
    public string? VerificationReason { get; set; }
    public GuidedKeyPreview(GuidedKeyOutcome outcome)
    {
        Outcome = outcome;
        if (outcome.Status != GuidedKeyStatus.Measured) adopt = false;
        Localizer.Instance.LanguageChanged += (_, _) => { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(SafetyDetail)); OnPropertyChanged(nameof(StatusLabel)); };
    }
    public GuidedKeyOutcome Outcome { get; }
    public bool Adopt
    {
        get => adopt;
        set { adopt = value; OnPropertyChanged(); }
    }
    public string Label => Outcome.Label;
    public string DepthLabel => Outcome.PressDepth > 0 ? $"{Outcome.PressDepth:0} / {Outcome.NoiseSpan:0}" : "—";
    public string Detail => string.Format(Localizer.Instance["Guided.KeyDetail"], Outcome.OldOffset, Outcome.NewOffset, Outcome.KeyThreshold)
        + " · " + string.Format(Localizer.Instance["Guided.SafeRange"], Outcome.SafeLower, Outcome.SafeUpper, Outcome.SampleCount)
        + " · " + string.Format(Localizer.Instance["Guided.IdleDriftDetail"], Outcome.IdleDrift)
        + (Outcome.IdleTransient ? " · " + Localizer.Instance["Guided.TransientWarning"] : "")
         + (Outcome.HadStuckRelease ? " · " + Localizer.Instance["Guided.RecoveredStuck"] : "");
    public string SafetyDetail => (Outcome.CenterFilteredNoiseSpan is { } noise
        ? string.Format(Localizer.Instance["Guided.CenterProfileDetail"], noise, Outcome.NoiseSpan) + " · " : "")
        + (Outcome.SafetyLimits is not { } limits ? "" :
            string.Format(Localizer.Instance["Guided.SafetyLimitsDetail"], limits.IdleLower, limits.CrossTalkLower,
                limits.ReleaseLower, limits.VerificationLower, limits.PressUpper, limits.VerificationUpper));
    public string StatusLabel => VerificationReason is not null ? Localizer.Instance[VerificationReason] : Outcome.Status switch
    {
        GuidedKeyStatus.Measured => Localizer.Instance["Guided.StatusMeasured"],
        GuidedKeyStatus.Skipped => Localizer.Instance["Guided.StatusSkipped"],
        GuidedKeyStatus.Timeout => Localizer.Instance["Guided.StatusTimeout"],
        GuidedKeyStatus.InsufficientDepth => Localizer.Instance["Guided.StatusInsufficient"],
        GuidedKeyStatus.Overflow => Localizer.Instance["Guided.StatusOverflow"],
        GuidedKeyStatus.UnstableIdle => Localizer.Instance[Outcome.IdleTouched ? "Guided.StatusIdleHardware" : "Guided.StatusIdleDrift"],
        _ => string.Empty
    };
}
