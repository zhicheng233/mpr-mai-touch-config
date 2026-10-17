using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

/// <summary>单个按键的自动学习结论。</summary>
public sealed record MappingLearnOutcome(
    int KeyIndex,
    string Label,
    int Channel,
    int Board,
    double Depth,
    bool Detected)
{
    public bool IsValidChannel => Detected && Channel >= 0 && Channel < KeyMapping.ChannelCount;
    public string ChannelLabel => IsValidChannel ? $"B{Board + 1}P{Channel % 12}" : Localizer.Instance["Mapping.Undetected"];
}

/// <summary>
/// 映射自动学习状态机：先测静止基线，再按按键顺序（A1 → E8）逐键提示用户按住该物理电极，
/// 在全部 48 个通道里找下降量最大的那个作为该键的通道（无需知道当前映射）。
/// 无 UI 依赖，可直接单测；时间单位为毫秒，调用方每次收到调试帧就 PushFrame。
/// </summary>
public sealed class MappingLearnSession
{
    public sealed record TargetKey(int KeyIndex, string Label);

    private const double StartThresholdMin = 12;
    private const double StartThresholdNoiseFactor = 3.0;
    private const double PressSpanFactor = 2.0;
    private const double HoldGraceMs = 400;

    private readonly List<TargetKey> targets;
    private readonly double[] minFiltered = new double[KeyMapping.ChannelCount];
    private readonly double[] maxFiltered = new double[KeyMapping.ChannelCount];
    private readonly double[] noiseSpan = new double[KeyMapping.ChannelCount];
    private readonly bool[] noiseSeen = new bool[KeyMapping.ChannelCount];
    private readonly double[] peakDrop = new double[KeyMapping.ChannelCount];
    private readonly bool[] channelActive = new bool[KeyMapping.ChannelCount];

    private readonly List<MappingLearnOutcome> outcomes = [];

    private uint baselineStartMs;
    private uint keyStartMs;
    private uint lastAboveMs;
    private bool pressed;
    private double liveDepth;

    public MappingLearnSession(IEnumerable<TargetKey> keys, int baselineDurationMs = 3000, int perKeyTimeoutMs = 15000)
    {
        targets = keys.ToList();
        BaselineDurationMs = baselineDurationMs;
        PerKeyTimeoutMs = perKeyTimeoutMs;
    }

    public int BaselineDurationMs { get; }
    public int PerKeyTimeoutMs { get; }
    public GuidedPhase Phase { get; private set; } = GuidedPhase.Idle;
    public int CurrentIndex { get; private set; }
    public bool CurrentKeyTimedOut { get; private set; }
    public double LiveDepth { get => liveDepth; private set { liveDepth = value; } }
    public bool IsPressed => pressed;

    public TargetKey? CurrentTarget => Phase == GuidedPhase.Measuring && CurrentIndex < targets.Count ? targets[CurrentIndex] : null;
    public bool IsComplete => Phase == GuidedPhase.Complete;
    public IReadOnlyList<MappingLearnOutcome> Outcomes => outcomes;
    public uint CurrentTimestampMs => lastTimestampMs;

    public double BaselineProgress => Phase == GuidedPhase.Baseline
        ? Math.Clamp((lastTimestampMs - baselineStartMs) / (double)BaselineDurationMs, 0, 1)
        : (Phase == GuidedPhase.Idle ? 0 : 1);

    public double TotalProgress
    {
        get
        {
            if (Phase == GuidedPhase.Idle) return 0;
            if (Phase == GuidedPhase.Complete) return 1;
            if (targets.Count == 0) return BaselineProgress * 0.1;
            var keyFraction = (CurrentIndex + (pressed ? 0.5 : 0)) / targets.Count;
            return 0.1 + 0.9 * Math.Clamp(keyFraction, 0, 1);
        }
    }

    private uint lastTimestampMs;

    public void Begin(uint timestampMs)
    {
        Phase = GuidedPhase.Baseline;
        baselineStartMs = timestampMs;
        lastTimestampMs = timestampMs;
        Array.Fill(noiseSeen, false);
        for (var channel = 0; channel < KeyMapping.ChannelCount; channel++) noiseSpan[channel] = 0;
    }

    public void PushFrame(uint timestampMs, ushort[] filtered, ushort[] baseline)
    {
        if (Phase is GuidedPhase.Idle or GuidedPhase.Complete) return;
        lastTimestampMs = timestampMs;
        if (Phase == GuidedPhase.Baseline)
        {
            for (var channel = 0; channel < KeyMapping.ChannelCount && channel < filtered.Length; channel++)
            {
                if (filtered[channel] == 0 || baseline[channel] == 0) continue;
                var value = (double)filtered[channel];
                if (!noiseSeen[channel]) { minFiltered[channel] = maxFiltered[channel] = value; noiseSeen[channel] = true; }
                else { if (value < minFiltered[channel]) minFiltered[channel] = value; if (value > maxFiltered[channel]) maxFiltered[channel] = value; }
            }
            if (timestampMs - baselineStartMs >= BaselineDurationMs)
            {
                for (var channel = 0; channel < KeyMapping.ChannelCount; channel++)
                    noiseSpan[channel] = noiseSeen[channel] ? maxFiltered[channel] - minFiltered[channel] : 0;
                Phase = GuidedPhase.Measuring;
                BeginCurrentKey(timestampMs);
            }
            return;
        }
        Observe(timestampMs, filtered, baseline);
    }

    private void BeginCurrentKey(uint timestampMs)
    {
        Array.Clear(peakDrop);
        Array.Clear(channelActive);
        pressed = false;
        liveDepth = 0;
        CurrentKeyTimedOut = false;
        keyStartMs = timestampMs;
        lastAboveMs = timestampMs;
    }

    private double StartThreshold(int channel) => Math.Max(StartThresholdMin, noiseSpan[channel] * StartThresholdNoiseFactor);

    private void Observe(uint timestampMs, ushort[] filtered, ushort[] baseline)
    {
        var target = CurrentTarget;
        if (target is null) { Phase = GuidedPhase.Complete; return; }

        var anyAbove = false;
        var maxDrop = 0.0;
        for (var channel = 0; channel < KeyMapping.ChannelCount && channel < filtered.Length; channel++)
        {
            if (filtered[channel] == 0 || baseline[channel] == 0) continue;
            var drop = (double)baseline[channel] - filtered[channel];
            if (drop <= 0) continue;
            if (drop > maxDrop) maxDrop = drop;
            if (drop >= StartThreshold(channel))
            {
                anyAbove = true;
                if (drop > peakDrop[channel]) peakDrop[channel] = drop;
                channelActive[channel] = true;
            }
        }
        liveDepth = maxDrop;

        if (anyAbove)
        {
            pressed = true;
            lastAboveMs = timestampMs;
        }
        else if (pressed && timestampMs - lastAboveMs >= HoldGraceMs)
        {
            // 所有通道都回落：本次按压结束，选下降量最大的通道。
            RecordCurrentKey(pressed: true);
            if (IsComplete) return;
            BeginCurrentKey(timestampMs);
            return;
        }

        if (!CurrentKeyTimedOut && timestampMs - keyStartMs >= PerKeyTimeoutMs)
        {
            CurrentKeyTimedOut = true;
            RecordCurrentKey(pressed);
            return;
        }
    }

    /// <summary>
    /// 按按键顺序生成学习目标：A1 → A8、B1 → B8、C1、C2、D1 → D8、E1 → E8。
    /// 界面提示只给键名，不提示板号/通道，学到的通道由工具自己测出来。
    /// </summary>
    public static List<TargetKey> BuildKeyOrderTargets(IEnumerable<(int KeyIndex, string Label)> keys)
        => keys.OrderBy(k => k.KeyIndex)
               .Select(k => new TargetKey(k.KeyIndex, k.Label)).ToList();

    private void RecordCurrentKey(bool pressed)
    {
        var target = CurrentTarget;
        if (target is null) return;
        var bestChannel = -1;
        var bestDepth = 0.0;
        if (pressed)
        {
            for (var channel = 0; channel < KeyMapping.ChannelCount; channel++)
            {
                if (!channelActive[channel]) continue;
                if (peakDrop[channel] <= bestDepth) continue;
                bestDepth = peakDrop[channel];
                bestChannel = channel;
            }
        }
        var detected = bestChannel >= 0 && bestDepth >= StartThresholdMin;
        outcomes.Add(new MappingLearnOutcome(
            target.KeyIndex, target.Label, detected ? bestChannel : -1,
            detected ? bestChannel / 12 : 0,
            detected ? bestDepth : 0, detected));
        CurrentIndex++;
        if (CurrentIndex >= targets.Count) { Phase = GuidedPhase.Complete; return; }
        BeginCurrentKey(lastTimestampMs);
    }

    /// <summary>用户点击"跳过此键"：记为未识别。</summary>
    public void SkipCurrentKey(uint timestampMs)
    {
        if (Phase != GuidedPhase.Measuring) return;
        lastTimestampMs = timestampMs;
        RecordCurrentKey(pressed: false);
    }

    /// <summary>当前键已检测到按压时允许提前结束。</summary>
    public void FinishCurrentKeyEarly(uint timestampMs)
    {
        if (Phase != GuidedPhase.Measuring) return;
        lastTimestampMs = timestampMs;
        RecordCurrentKey(pressed);
    }

    public bool CanFinishCurrentKeyEarly => pressed;

    /// <summary>把识别结果写回给定映射（未识别的键保留原映射）。返回新映射与发生冲突的通道数。</summary>
    public (byte[] Mapping, int ConflictCount) ApplyTo(byte[] current)
    {
        var mapping = KeyMapping.Clone(current);
        var used = new Dictionary<int, int>();
        foreach (var outcome in outcomes)
        {
            if (!outcome.IsValidChannel) continue;
            mapping[outcome.KeyIndex] = KeyMapping.Pack(outcome.Board, outcome.Channel % KeyMapping.PortCount);
        }
        var conflicts = 0;
        for (var key = 0; key < mapping.Length; key++)
        {
            var channel = KeyMapping.ChannelIndexOf(mapping[key]);
            if (used.TryGetValue(channel, out var other))
            {
                conflicts++;
                // 保留先出现（键序更靠前）的映射，冲突的把后者标成未识别 -> 无法标未识别，
                // 故这里只是统计；调用方将通过校验发现重复并提示用户手工修正。
                _ = other;
            }
            else used[channel] = key;
        }
        return (mapping, conflicts);
    }

    public int DetectedCount => outcomes.Count(outcome => outcome.Detected);
    public int UndetectedCount => outcomes.Count(outcome => !outcome.Detected);
}
