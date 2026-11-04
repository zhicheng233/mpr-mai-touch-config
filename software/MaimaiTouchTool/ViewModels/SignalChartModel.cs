using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

public sealed record SignalChartPoint(ulong TimeMs, int Filtered, int Baseline, int TouchThreshold,
    int ReleaseThreshold, bool Touched, bool Valid, bool Connect)
{
    public int Drop => Baseline - Filtered;
    public bool HardwareTouched { get; init; }
    public int CandidateThreshold { get; init; }
    public bool CompensationActive { get; init; }
}

/// <summary>UI-thread owned, bounded raw history. Switching keys never relabels old channel data.</summary>
public sealed class SignalChartModel : NotifyModel
{
    public const int Capacity = 1200;
    public const int WindowMs = 30000;
    private sealed record Frame(ulong Time, ushort[] Filtered, ushort[] Baseline, byte[] Status,
        ulong Bits, byte[] Mapping, int[] Thresholds, ulong HardwareBits, byte[] Candidates, ulong ActiveBits);
    private readonly Queue<Frame> frames = new();
    private Frame? latest;
    private IReadOnlyList<SignalChartPoint>? cachedPoints;
    private uint previousTimestamp;
    private ulong time;
    private bool hasTimestamp;
    private int selectedKeyIndex;
    private bool followGuided = true;
    private bool paused;
    private int revision;

    public SignalChartModel() => ClearCommand = new RelayCommand(Clear);
    public IReadOnlyList<string> Keys => KeyMapping.KeyLabels;
    public RelayCommand ClearCommand { get; }
    public int SelectedKeyIndex
    {
        get => selectedKeyIndex;
        set { if (value is < 0 or >= 34 || value == selectedKeyIndex) return; selectedKeyIndex = value; OnPropertyChanged(); Changed(); }
    }
    public bool FollowGuided { get => followGuided; set { followGuided = value; OnPropertyChanged(); } }
    public bool IsPaused { get => paused; set { paused = value; OnPropertyChanged(); } }
    public int Revision => revision;
    public int Count => frames.Count;
    public string CurrentValues
    {
        get
        {
            var point = latest is null ? null : ToPoint(latest, null);
            return point is null ? Localizer.Instance["Chart.Empty"] : !point.Valid ? Localizer.Instance["Chart.Invalid"] :
                string.Format(Localizer.Instance["Chart.Values"], Keys[SelectedKeyIndex], point.Filtered, point.Baseline,
                    point.Drop, point.TouchThreshold, point.ReleaseThreshold,
                    Localizer.Instance[point.Touched ? "Tele.Touch" : "Tele.ChannelIdle"]) +
                string.Format(Localizer.Instance[point.CompensationActive ? "Compensation.ActiveValues" : point.CandidateThreshold > 0 ? "Compensation.CandidateValues" : "Compensation.LearningValues"],
                    point.HardwareTouched ? 1 : 0, point.Touched ? 1 : 0, point.CandidateThreshold);
        }
    }

    public void Follow(int? key)
    {
        if (FollowGuided && key.HasValue) SelectedKeyIndex = key.Value;
    }

    public void Add(DebugFrame frame, ConfigBlob config)
    {
        if (IsPaused) return; // Freeze only the graph, never serial acquisition/calibration.
        if (frame.Filtered.Length < 48 || frame.Baseline.Length < 48 || frame.ChipStatus.Length < 4) return;
        var delta = hasTimestamp ? unchecked(frame.TimestampMs - previousTimestamp) : 0;
        if (hasTimestamp && delta > 60000) Clear(); // Reboot/new stream: do not bridge unrelated clocks.
        else time += delta;
        previousTimestamp = frame.TimestampMs;
        hasTimestamp = true;
        var thresholds = Enumerable.Range(0, 34).Select(k =>
        {
            var board = config.Boards[KeyMapping.BoardOf(config.Mapping[k])];
            return Math.Clamp(board.BaseThreshold + board.SensOffset + config.ElectrodeOffsets[k], 1, 1023);
        }).ToArray();
        latest = new(time, (ushort[])frame.Filtered.Clone(), (ushort[])frame.Baseline.Clone(),
            (byte[])frame.ChipStatus.Clone(), frame.TouchBits, KeyMapping.Clone(config.Mapping), thresholds,
            frame.HardwareTouchBits ?? frame.TouchBits, frame.CompensationThresholds is null ? new byte[34] : (byte[])frame.CompensationThresholds.Clone(), frame.CompensationActiveBits);
        frames.Enqueue(latest);
        while (frames.Count > Capacity || frames.Count > 1 && time - frames.Peek().Time > WindowMs) frames.Dequeue();
        Changed();
    }

    public IReadOnlyList<SignalChartPoint> Points()
    {
        if (cachedPoints is not null) return cachedPoints;
        var result = new List<SignalChartPoint>(frames.Count);
        SignalChartPoint? previous = null;
        byte? previousMapping = null;
        foreach (var frame in frames)
        {
            var point = ToPoint(frame, previous);
            if (previousMapping != frame.Mapping[SelectedKeyIndex]) point = point with { Connect = false };
            result.Add(point);
            previous = point;
            previousMapping = frame.Mapping[SelectedKeyIndex];
        }
        return cachedPoints = result.AsReadOnly();
    }

    private SignalChartPoint ToPoint(Frame frame, SignalChartPoint? previous)
    {
        var channel = KeyMapping.ChannelIndexOf(frame.Mapping[SelectedKeyIndex]);
        var filtered = frame.Filtered[channel];
        var baseline = frame.Baseline[channel];
        var valid = filtered is > 0 and <= 1023 && baseline is > 0 and <= 1023 && (frame.Status[channel / 12] & 15) == 7;
        var threshold = frame.Thresholds[SelectedKeyIndex];
        var candidate = frame.Candidates[SelectedKeyIndex] * 4;
        var active = (frame.ActiveBits & (1UL << SelectedKeyIndex)) != 0;
        return new(frame.Time, filtered, baseline, active ? candidate : (threshold + 3) / 4 * 4,
            active ? candidate - 4 : GuidedCalibrationAlgorithm.HardwareReleaseThreshold(threshold) * 4,
            (frame.Bits & (1UL << SelectedKeyIndex)) != 0, valid,
            previous is { Valid: true } && valid && frame.Time - previous.TimeMs <= 250)
        {
            HardwareTouched = (frame.HardwareBits & (1UL << SelectedKeyIndex)) != 0,
            CandidateThreshold = candidate, CompensationActive = active
        };
    }

    public void Clear()
    {
        frames.Clear();
        latest = null;
        hasTimestamp = false;
        time = 0;
        Changed();
    }
    public void RaiseLocalized() => Changed();
    private void Changed() { cachedPoints = null; revision++; OnPropertyChanged(nameof(Revision)); OnPropertyChanged(nameof(CurrentValues)); }
}
