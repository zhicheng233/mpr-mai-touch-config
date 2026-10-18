using System.Collections.ObjectModel;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

public enum TelemetryHealth
{
    Idle,
    Starting,
    Healthy,
    Degraded,
    Error,
    Disconnected
}

public sealed record TelemetryLogEntry(DateTimeOffset Timestamp, string Level, string Message);

public sealed class TelemetryChannelModel : NotifyModel
{
    private ushort filtered;
    private ushort baseline;
    private ushort threshold;
    private bool isTouched;
    private byte chipStatus;
    private string keyLabel;

    public TelemetryChannelModel(int index, string keyLabel)
    {
        Index = index;
        Board = index / 12;
        Port = index % 12;
        this.keyLabel = keyLabel;
    }

    public int Index { get; }
    public int Board { get; }
    public int Port { get; }
    public string ChannelLabel => $"CH{Board + 1}:{Port:00}";
    // 该通道当前映射到的游戏按键名（未映射为 "unmapped"），随配置中的 keyMap 变化。
    public string KeyLabel { get => keyLabel; private set { keyLabel = value; OnPropertyChanged(); } }
    public void ApplyKeyLabel(string label) => KeyLabel = label;
    public ushort Filtered { get => filtered; private set { filtered = value; OnPropertyChanged(); OnPropertyChanged(nameof(Delta)); } }
    public ushort Baseline { get => baseline; private set { baseline = value; OnPropertyChanged(); OnPropertyChanged(nameof(Delta)); OnPropertyChanged(nameof(ThresholdValue)); } }
    public int Delta => Baseline - Filtered;
    public bool IsTouched { get => isTouched; private set { isTouched = value; OnPropertyChanged(); OnPropertyChanged(nameof(TouchState)); } }
    public string TouchState => IsTouched ? Localizer.Instance["Tele.Touch"] : Localizer.Instance["Tele.ChannelIdle"];
    public byte ChipStatus { get => chipStatus; private set { chipStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChipState)); } }
    public string ChipState => (ChipStatus & 0x07) == 0x07 ? Localizer.Instance["Tele.Online"] : Localizer.Instance["Tele.Check"];

    // 触摸触发的下降量阈值（delta = baseThreshold + sensOffset + electrodeOffset），由配置决定。
    public ushort Threshold { get => threshold; private set { threshold = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThresholdValue)); } }
    // 触发触摸时的 filtered 值（进度条上标记点的位置）：touch 当 filtered <= baseline - threshold。
    public double ThresholdValue => Math.Clamp((double)Baseline - threshold, 0, 1023);

    public void ApplyThreshold(ushort value) => Threshold = value;

    public void Apply(ushort filteredValue, ushort baselineValue, bool touched, byte status)
    {
        Filtered = filteredValue;
        Baseline = baselineValue;
        IsTouched = touched;
        ChipStatus = status;
    }
}

public sealed class TelemetryChipModel : NotifyModel
{
    private byte status;
    private ushort i2cErrors;

    public TelemetryChipModel(int index) { Index = index; Localizer.Instance.LanguageChanged += (_, _) => { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(StatusLabel)); }; }
    public int Index { get; }
    public ObservableCollection<TelemetryChannelModel> Channels { get; } = [];
    public string Label => string.Format(Localizer.Instance["Tele.Board"], Index + 1);
    public byte Status { get => status; private set { status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(IsHealthy)); } }
    public ushort I2cErrors { get => i2cErrors; private set { i2cErrors = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(IsHealthy)); } }
    public bool IsHealthy => (Status & 0x0F) == 0x07 && I2cErrors == 0;
    public string StatusLabel => IsHealthy ? Localizer.Instance["Tele.StatusOnline"] : $"{DescribeStatus(Status)} / I2C {I2cErrors}";

    public void Apply(byte newStatus, ushort errors)
    {
        Status = newStatus;
        I2cErrors = errors;
    }

    private static string DescribeStatus(byte value)
    {
        if ((value & 1) == 0) return Localizer.Instance["Tele.StatusOffline"];
        if ((value & 2) == 0) return Localizer.Instance["Tele.StatusNotInit"];
        if ((value & 4) == 0) return Localizer.Instance["Tele.StatusBaselinePending"];
        return (value & 8) != 0 ? Localizer.Instance["Tele.StatusI2cError"] : Localizer.Instance["Tele.StatusDegraded"];
    }
}

public sealed class TelemetrySessionModel : NotifyModel
{
    public const int MaxLogEntries = 100;
    public const int MaxHistorySamples = 1200;
    private readonly Queue<(uint TimestampMs, DateTimeOffset ReceivedAt)> history = new();
    private readonly Queue<ushort> rateIntervalsMs = new();
    private uint previousDeviceTimestamp;
    private DateTimeOffset previousReceivedAt;
    private ushort previousSequence;
    private bool hasPrevious;
    private TelemetryHealth health = TelemetryHealth.Idle;
    private double actualRateHz;
    private long sequenceGaps;
    private ushort requestedRateHz = ProtocolConstants.DefaultStreamRateHz;
    private ushort acceptedRateHz;
    private string lastError = string.Empty;
    // 当前生效的按键映射（随 ApplyThresholds 里的配置更新）；用于通道↔按键与触摸位解码。
    private byte[] keyMap = KeyMapping.Clone(null);

    public TelemetrySessionModel()
    {
        for (var index = 0; index < 4; index++) Chips.Add(new TelemetryChipModel(index));
        var labels = KeyMapping.ChannelLabels(keyMap);
        for (var index = 0; index < 48; index++)
        {
            var channel = new TelemetryChannelModel(index, labels[index]);
            Channels.Add(channel);
            Chips[index / 12].Channels.Add(channel);
        }
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HealthLabel));
            OnPropertyChanged(nameof(ActualRateLabel));
            OnPropertyChanged(nameof(SequenceLabel));
            OnPropertyChanged(nameof(AcceptedRateLabel));
        };
    }

    public ObservableCollection<TelemetryChannelModel> Channels { get; } = [];
    public ObservableCollection<TelemetryChipModel> Chips { get; } = [];
    public ObservableCollection<TelemetryLogEntry> Log { get; } = [];
    public TelemetryHealth Health { get => health; private set { health = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthLabel)); } }
    public string HealthLabel => Health switch { TelemetryHealth.Healthy => Localizer.Instance["Tele.Healthy"], TelemetryHealth.Degraded => Localizer.Instance["Tele.Degraded"], TelemetryHealth.Starting => Localizer.Instance["Tele.Starting"], TelemetryHealth.Error => Localizer.Instance["Tele.Error"], TelemetryHealth.Disconnected => Localizer.Instance["Tele.Disconnected"], _ => Localizer.Instance["Tele.Idle"] };
    public double ActualRateHz { get => actualRateHz; private set { actualRateHz = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActualRateLabel)); } }
    public string ActualRateLabel => ActualRateHz > 0 ? string.Format(Localizer.Instance["Tele.RateActual"], ActualRateHz) : Localizer.Instance["Tele.RateNotMeasured"];
    public long SequenceGaps { get => sequenceGaps; private set { sequenceGaps = value; OnPropertyChanged(); OnPropertyChanged(nameof(SequenceLabel)); } }
    public string SequenceLabel => SequenceGaps == 0 ? Localizer.Instance["Tele.NoGaps"] : string.Format(Localizer.Instance["Tele.Gaps"], SequenceGaps);
    public ushort RequestedRateHz { get => requestedRateHz; private set { requestedRateHz = value; OnPropertyChanged(); } }
    public ushort AcceptedRateHz { get => acceptedRateHz; private set { acceptedRateHz = value; OnPropertyChanged(); OnPropertyChanged(nameof(AcceptedRateLabel)); } }
    public string AcceptedRateLabel => AcceptedRateHz == 0 ? Localizer.Instance["Tele.AcceptedPending"] : string.Format(Localizer.Instance["Tele.Accepted"], AcceptedRateHz);
    public ushort I2cErrors => Chips.Count == 0 ? (ushort)0 : Chips.Max(chip => chip.I2cErrors);
    public string LastError { get => lastError; private set { lastError = value; OnPropertyChanged(); } }

    // 依据当前配置刷新每个通道的触摸阈值与按键标签（与固件 config::touchDelta 一致）：
    // delta = clamp(baseThreshold + sensOffset + electrodeOffset, 1, 1023)；未映射通道 electrodeOffset 取 0。
    public void ApplyThresholds(ConfigBlob config)
    {
        keyMap = KeyMapping.Clone(config.Mapping);
        var labels = KeyMapping.ChannelLabels(keyMap);
        for (var channel = 0; channel < Channels.Count; channel++)
        {
            var board = channel / 12;
            var keyIndex = KeyMapping.KeyIndexForChannel(keyMap, channel);
            var offset = keyIndex >= 0 ? (int)config.ElectrodeOffsets[keyIndex] : 0;
            var delta = Math.Clamp((int)config.Boards[board].BaseThreshold + (int)config.Boards[board].SensOffset + offset, 1, 1023);
            Channels[channel].ApplyThreshold((ushort)delta);
            Channels[channel].ApplyKeyLabel(labels[channel]);
        }
    }

    public void Begin(ushort requestedRate)
    {
        RequestedRateHz = requestedRate == 0 ? ProtocolConstants.DefaultStreamRateHz : requestedRate;
        AcceptedRateHz = 0;
        ActualRateHz = 0;
        SequenceGaps = 0;
        LastError = string.Empty;
        history.Clear();
        rateIntervalsMs.Clear();
        hasPrevious = false;
        Health = TelemetryHealth.Starting;
        AddLog("INFO", string.Format(Localizer.Instance["Tele.StartLog"], RequestedRateHz));
    }

    public void SetAcceptedRate(ushort acceptedRate, ushort actualRate)
    {
        AcceptedRateHz = acceptedRate;
        AddLog("INFO", string.Format(Localizer.Instance["Tele.AcceptedLog"], acceptedRate, actualRate));
    }

    public void AddFrame(DebugFrame frame, ushort sequence, DateTimeOffset receivedAt)
    {
        if (hasPrevious)
        {
            if (sequence != 0 && previousSequence != 0)
            {
                var gap = (sequence - previousSequence - 1 + 65536) % 65536;
                if (gap > 0) { SequenceGaps += gap; AddLog("WARN", string.Format(Localizer.Instance["Tele.DroppedLog"], gap, sequence)); }
            }

            var deviceDelta = unchecked(frame.TimestampMs - previousDeviceTimestamp);
            var hostDelta = (receivedAt - previousReceivedAt).TotalMilliseconds;
            var interval = deviceDelta is > 0 and < 60000 ? deviceDelta : hostDelta is > 0 and < 60000 ? hostDelta : 0;
            if (interval > 0)
            {
                rateIntervalsMs.Enqueue((ushort)Math.Clamp(interval, 1, ushort.MaxValue));
                while (rateIntervalsMs.Count > 32) rateIntervalsMs.Dequeue();
                ActualRateHz = 1000d / rateIntervalsMs.Select(intervalMs => (double)intervalMs).Average();
            }
        }

        previousDeviceTimestamp = frame.TimestampMs;
        previousReceivedAt = receivedAt;
        previousSequence = sequence;
        hasPrevious = true;
        history.Enqueue((frame.TimestampMs, receivedAt));
        while (history.Count > MaxHistorySamples) history.Dequeue();
        for (var index = 0; index < 4; index++) Chips[index].Apply(frame.ChipStatus[index], frame.I2cErrors);
        for (var index = 0; index < Channels.Count; index++)
        {
            var keyIndex = KeyMapping.KeyIndexForChannel(keyMap, index);
            var touched = keyIndex >= 0 && (frame.TouchBits & (1UL << keyIndex)) != 0;
            Channels[index].Apply(frame.Filtered[index], frame.Baseline[index], touched, frame.ChipStatus[index / 12]);
        }
        if (Health is not TelemetryHealth.Error and not TelemetryHealth.Disconnected) Health = Chips.All(chip => chip.IsHealthy) ? TelemetryHealth.Healthy : TelemetryHealth.Degraded;
        OnPropertyChanged(nameof(I2cErrors));
    }

    public void MarkMalformed(string message)
    {
        LastError = message;
        Health = TelemetryHealth.Error;
        AddLog("ERROR", message);
    }

    public void MarkDisconnected(string message = "")
    {
        LastError = message = string.IsNullOrEmpty(message) ? Localizer.Instance["Tele.DeviceDisconnected"] : message;
        Health = TelemetryHealth.Disconnected;
        AddLog("ERROR", message);
    }

    public void Stop()
    {
        if (Health is TelemetryHealth.Starting or TelemetryHealth.Healthy or TelemetryHealth.Degraded) AddLog("INFO", Localizer.Instance["Tele.Stopped"]);
        Health = TelemetryHealth.Idle;
    }

    // 供校准等非遥测流程写入带时间戳的事件记录（例如校准摘要）。
    public void RecordEvent(string level, string message) => AddLog(level, message);

    private void AddLog(string level, string message)
    {
        Log.Add(new TelemetryLogEntry(DateTimeOffset.UtcNow, level, message));
        while (Log.Count > MaxLogEntries) Log.RemoveAt(0);
    }

    /// <summary>默认映射下通道（board*12 + port）对应的游戏按键索引；默认映射里未使用返回 -1。</summary>
    public static int KeyIndexForChannel(int channel) => KeyMapping.KeyIndexForChannel(KeyMapping.Default, channel);
    public static bool IsMappedChannel(int channel) => KeyIndexForChannel(channel) >= 0;
}
