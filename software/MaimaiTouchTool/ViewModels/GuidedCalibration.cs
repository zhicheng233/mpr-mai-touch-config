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
    Overflow
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
    bool Measured);

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
    GuidedKeyStatus Status);

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
    public int FailedCount => Keys.Count(key => key.Status is GuidedKeyStatus.Timeout or GuidedKeyStatus.InsufficientDepth or GuidedKeyStatus.Overflow);
}

/// <summary>
/// 引导式强力自动调整的纯算法核心：给定逐键测量 + 当前配置 + 触发比例，
/// 算出新的 electrodeOffset 与板级 baseThreshold/sensOffset/DT/DR。无副作用，便于单测。
/// </summary>
public static class GuidedCalibrationAlgorithm
{
    public const double DefaultTriggerRatio = 0.5;
    /// <summary>噪声余量倍数：阈值至少是静止噪声跨度的多少倍，避免误触。</summary>
    public const double NoiseFactor = 2.0;
    /// <summary>芯片阈值分辨率为 4（ceil(touchDelta/4)），电极偏移按此对齐。</summary>
    public const int StepQuantum = 4;
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

    public static GuidedCalibrationResult Compute(
        IReadOnlyList<GuidedKeyInput> inputs,
        ConfigBlob current,
        double triggerRatio)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(current);
        var ratio = Math.Clamp(triggerRatio, MinTriggerRatio, MaxTriggerRatio);

        // 逐键目标阈值：至少高过噪声底线，理想是"按到 ratio 深度即触发"。
        var keyThresholds = new int[inputs.Count];
        var keyStatus = new GuidedKeyStatus[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (!input.Measured)
            {
                keyThresholds[i] = 0;
                keyStatus[i] = GuidedKeyStatus.Skipped;
                continue;
            }
            var noiseFloor = input.NoiseSpan * NoiseFactor;
            var ideal = input.PressDepth * ratio;
            var target = (int)Math.Ceiling(Math.Max(noiseFloor, ideal));
            keyThresholds[i] = target;
            // 按下深度不足以与噪声区分：任何阈值都无法既防误触又按得动。
            keyStatus[i] = target >= input.PressDepth
                ? GuidedKeyStatus.InsufficientDepth
                : GuidedKeyStatus.Measured;
        }

        // 板级公共基准 = 该板所有"可用键"目标阈值的中位数（对异常键鲁棒）。只升不降在这里不适用：
        // 这是"重新平衡"，不是"安全底线累加"，因此允许下调。
        var boardCount = current.Boards.Length;
        var boardBase = new double[boardCount];
        var boardMeasured = new int[boardCount];
        for (var board = 0; board < boardCount; board++)
        {
            var samples = new List<int>();
            for (var i = 0; i < inputs.Count; i++)
                if (inputs[i].Board == board && keyStatus[i] == GuidedKeyStatus.Measured)
                    samples.Add(keyThresholds[i]);
            boardBase[board] = samples.Count > 0 ? Median(samples) : 0;
            boardMeasured[board] = samples.Count;
        }

        // 板级 baseThreshold/sensOffset 拆解：优先用 baseThreshold 装下基准（上限 255），
        // 溢出部分交给 sensOffset（±127）。必须先把板级算完，因为逐键偏移要相对
        // "实际生效的板级基准"（baseThreshold + sensOffset）计算；否则两者被 clamp 后
        // 每个键的最终阈值会整体偏移，与所测深度对不上。
        var boardOutcomes = new List<GuidedBoardOutcome>(boardCount);
        var effectiveBase = new int[boardCount];
        for (var board = 0; board < boardCount; board++)
        {
            var old = current.Boards[board];
            var newBase = (int)old.BaseThreshold;
            var newSens = (int)old.SensOffset;
            var newTouchDt = (int)old.TouchDebounce;
            var newReleaseDr = (int)old.ReleaseDebounce;

            if (boardMeasured[board] > 0)
            {
                var baseRounded = (int)Math.Round(boardBase[board]);
                newBase = Math.Clamp(baseRounded, 0, MaxBaseThreshold);
                newSens = Math.Clamp(baseRounded - newBase, MinSensOffset, MaxSensOffset);

                // DT/DR：由该板所有已测键的上升/回落耗时中位数推断（±1 档，仅供参考）。
                var rises = new List<double>();
                var falls = new List<double>();
                for (var i = 0; i < inputs.Count; i++)
                {
                    if (inputs[i].Board != board || keyStatus[i] != GuidedKeyStatus.Measured) continue;
                    rises.Add(inputs[i].RiseMs);
                    falls.Add(inputs[i].FallMs);
                }
                if (rises.Count > 0) newTouchDt = SuggestDebounce(Median(rises), 25, 50, 100, 200);
                if (falls.Count > 0) newReleaseDr = SuggestDebounce(Median(falls), 40, 80, 160, 320);
            }
            effectiveBase[board] = newBase + newSens;

            boardOutcomes.Add(new GuidedBoardOutcome(board + 1,
                old.BaseThreshold, newBase, old.SensOffset, newSens,
                old.TouchDebounce, newTouchDt, old.ReleaseDebounce, newReleaseDr,
                boardMeasured[board]));
        }

        var keyOutcomes = new List<GuidedKeyOutcome>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var oldOffset = KeyOffsetFor(current, input.KeyIndex);
            if (keyStatus[i] != GuidedKeyStatus.Measured)
            {
                // 未测到：保留原偏移，不参与板级基准。
                keyOutcomes.Add(new GuidedKeyOutcome(input.KeyIndex, input.Label, input.Channel, input.Board,
                    input.PressDepth, input.NoiseSpan, oldOffset, oldOffset, keyThresholds[i], keyStatus[i]));
                continue;
            }

            var relative = keyThresholds[i] - effectiveBase[input.Board];
            var rounded = RoundToQuantum(relative);
            var status = GuidedKeyStatus.Measured;
            if (keyThresholds[i] > MaxRepresentableThreshold ||
                rounded > MaxElectrodeOffset || rounded < MinElectrodeOffset)
            {
                status = GuidedKeyStatus.Overflow;
            }
            var newOffset = status == GuidedKeyStatus.Overflow
                ? oldOffset
                : Math.Clamp(rounded, MinElectrodeOffset, MaxElectrodeOffset);
            keyOutcomes.Add(new GuidedKeyOutcome(input.KeyIndex, input.Label, input.Channel, input.Board,
                input.PressDepth, input.NoiseSpan, oldOffset, newOffset, keyThresholds[i], status));
        }

        var hasWarning = keyOutcomes.Any(key => key.Status is GuidedKeyStatus.Timeout or GuidedKeyStatus.InsufficientDepth or GuidedKeyStatus.Overflow)
                         || boardOutcomes.Any(board => board.MeasuredKeys == 0);
        return new GuidedCalibrationResult(keyOutcomes, boardOutcomes, ratio, hasWarning);
    }

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
        foreach (var key in result.Keys)
            if (key.Status == GuidedKeyStatus.Measured && adoptKey(key) && key.KeyIndex >= 0 && key.KeyIndex < offsets.Length)
                offsets[key.KeyIndex] = (sbyte)Math.Clamp(key.NewOffset, MinElectrodeOffset, MaxElectrodeOffset);

        var boards = new BoardConfig[current.Boards.Length];
        for (var board = 0; board < current.Boards.Length; board++)
        {
            var source = current.Boards[board];
            var outcome = result.Boards.FirstOrDefault(item => item.BoardIndex == board + 1);
            var take = applyThresholds && outcome is not null && outcome.MeasuredKeys > 0 && adoptBoard(outcome);
            var takeDebounce = applyDebounce && outcome is not null && outcome.MeasuredKeys > 0 && adoptBoard(outcome);
            var baseThreshold = take ? (byte)Math.Clamp(outcome!.NewBaseThreshold, 0, MaxBaseThreshold) : source.BaseThreshold;
            var sensOffset = take ? (sbyte)Math.Clamp(outcome!.NewSensOffset, MinSensOffset, MaxSensOffset) : source.SensOffset;
            var touchDebounce = takeDebounce ? (byte)Math.Clamp(outcome!.NewTouchDebounce, 0, MaxDebounce) : source.TouchDebounce;
            var releaseDebounce = takeDebounce ? (byte)Math.Clamp(outcome!.NewReleaseDebounce, 0, MaxDebounce) : source.ReleaseDebounce;
            boards[board] = source with { BaseThreshold = baseThreshold, SensOffset = sensOffset, TouchDebounce = touchDebounce, ReleaseDebounce = releaseDebounce };
        }
        return new ConfigBlob(boards, offsets);
    }

    /// <summary>把相对偏移对齐到芯片分辨率（4）的最近倍数。</summary>
    public static int RoundToQuantum(int value)
    {
        var quotient = (int)Math.Round(value / (double)StepQuantum, MidpointRounding.AwayFromZero);
        return quotient * StepQuantum;
    }

    /// <summary>由耗时中位数推断去抖档位（0..7）。阈值从紧到松逐级判断。</summary>
    private static int SuggestDebounce(double medianMs, double dt0, double dt1, double dt2, double dt3)
    {
        if (medianMs <= dt0) return 0;
        if (medianMs <= dt1) return 1;
        if (medianMs <= dt2) return 2;
        if (medianMs <= dt3) return 3;
        return 4;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static double Median(List<int> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
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
    public GuidedKeyPreview(GuidedKeyOutcome outcome)
    {
        Outcome = outcome;
        if (outcome.Status != GuidedKeyStatus.Measured) adopt = false;
        Localizer.Instance.LanguageChanged += (_, _) => { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(StatusLabel)); };
    }
    public GuidedKeyOutcome Outcome { get; }
    public bool Adopt
    {
        get => adopt;
        set { adopt = value; OnPropertyChanged(); }
    }
    public string Label => Outcome.Label;
    public string DepthLabel => Outcome.PressDepth > 0 ? $"{Outcome.PressDepth:0} / {Outcome.NoiseSpan:0}" : "—";
    public string Detail => string.Format(Localizer.Instance["Guided.KeyDetail"], Outcome.OldOffset, Outcome.NewOffset, Outcome.KeyThreshold);
    public string StatusLabel => Outcome.Status switch
    {
        GuidedKeyStatus.Measured => Localizer.Instance["Guided.StatusMeasured"],
        GuidedKeyStatus.Skipped => Localizer.Instance["Guided.StatusSkipped"],
        GuidedKeyStatus.Timeout => Localizer.Instance["Guided.StatusTimeout"],
        GuidedKeyStatus.InsufficientDepth => Localizer.Instance["Guided.StatusInsufficient"],
        GuidedKeyStatus.Overflow => Localizer.Instance["Guided.StatusOverflow"],
        _ => string.Empty
    };
}
