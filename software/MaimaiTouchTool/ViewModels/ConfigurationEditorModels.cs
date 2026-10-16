using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

public abstract class NotifyModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BoardEditorModel : NotifyModel
{
    private int baseThreshold;
    private int sensOffset;
    private int touchDebounce;
    private int releaseDebounce;
    private int sfi;
    private int esi;

    public BoardEditorModel(int index, BoardConfig config)
    {
        Index = index;
        Update(config);
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(BoardWithAddress));
        };
    }
    public int Index { get; }
    public int I2cAddress => 0x5A + (Index - 1);
    public string AddressHex => $"0x{I2cAddress:X2}";
    public string Label => string.Format(Localizer.Instance["Config.BoardN"], Index);
    public string BoardWithAddress => $"{Label} ({AddressHex})";
    public int BaseThreshold { get => baseThreshold; set { baseThreshold = Math.Clamp(value, 1, 255); OnPropertyChanged(); } }
    public int SensOffset { get => sensOffset; set { sensOffset = Math.Clamp(value, -127, 127); OnPropertyChanged(); } }
    public int TouchDebounce { get => touchDebounce; set { touchDebounce = Math.Clamp(value, 0, 7); OnPropertyChanged(); } }
    public int ReleaseDebounce { get => releaseDebounce; set { releaseDebounce = Math.Clamp(value, 0, 7); OnPropertyChanged(); } }
    public int Sfi { get => sfi; set { sfi = Math.Clamp(value, 0, 3); OnPropertyChanged(); } }
    public int Esi { get => esi; set { esi = Math.Clamp(value, 0, 7); OnPropertyChanged(); } }
    public BoardConfig ToConfig() => new((byte)BaseThreshold, (sbyte)SensOffset, (byte)TouchDebounce, (byte)ReleaseDebounce, (byte)Sfi, (byte)Esi);
    public void Update(BoardConfig config) { BaseThreshold = config.BaseThreshold; SensOffset = config.SensOffset; TouchDebounce = config.TouchDebounce; ReleaseDebounce = config.ReleaseDebounce; Sfi = config.Sfi; Esi = config.Esi; }
}

public sealed class ElectrodeEditorModel : NotifyModel
{
    private readonly Func<int> stepProvider;
    private int offset;
    private byte mprId;
    private byte portId;
    public ElectrodeEditorModel(int index, string label, int value, byte mprId = 0, byte portId = 0, Func<int>? stepProvider = null)
    {
        Index = index;
        Label = label;
        Offset = value;
        this.mprId = mprId;
        this.portId = portId;
        this.stepProvider = stepProvider ?? (() => 4);
        IncreaseCommand = new RelayCommand(() => Nudge(this.stepProvider()));
        DecreaseCommand = new RelayCommand(() => Nudge(-this.stepProvider()));
        Localizer.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(SensitivityLabel));
    }
    public int Index { get; }
    public string Label { get; }
    public int Offset { get => offset; set { var clamped = Math.Clamp(value, -127, 127); if (clamped == offset) return; offset = clamped; OnPropertyChanged(); OnPropertyChanged(nameof(SensitivityLabel)); OnPropertyChanged(nameof(TintBrush)); OnPropertyChanged(nameof(Tooltip)); } }
    public byte MprId { get => mprId; private set { mprId = value; OnPropertyChanged(); OnPropertyChanged(nameof(BoardNumber)); OnPropertyChanged(nameof(I2cAddress)); OnPropertyChanged(nameof(HardwareHint)); OnPropertyChanged(nameof(Tooltip)); } }
    public byte PortId { get => portId; private set { portId = value; OnPropertyChanged(); OnPropertyChanged(nameof(HardwareHint)); OnPropertyChanged(nameof(Tooltip)); } }
    public int BoardNumber => MprId + 1;
    public int I2cAddress => 0x5A + MprId;
    public string HardwareHint => $"Board {BoardNumber} (0x{I2cAddress:X2}) • Port {PortId}";

    /// <summary>按键映射变化后更新本键实际所在的板/通道提示。</summary>
    public void SetHardware(byte board, byte port) { MprId = board; PortId = port; }

    /// <summary>+/- 按钮：按当前步进调整本键偏移。</summary>
    public ICommand IncreaseCommand { get; }
    public ICommand DecreaseCommand { get; }

    /// <summary>按当前偏移增减（步进由界面传入），内部 clamp。供 +/- 按钮使用。</summary>
    public void Nudge(int delta) => Offset += delta;
    public void Reset() => Offset = 0;

    /// <summary>数值与灵敏度的人读描述（方向：负=更灵敏，正=更迟钝）。</summary>
    public string SensitivityLabel => Offset switch
    {
        <= -8 => Localizer.Instance["Config.Sens.High"],
        < 0 => Localizer.Instance["Config.Sens.Slight"],
        0 => Localizer.Instance["Config.Sens.Neutral"],
        < 8 => Localizer.Instance["Config.Sens.SlightDull"],
        _ => Localizer.Instance["Config.Sens.Dull"]
    };

    /// <summary>图例染色：负值偏蓝绿，0 中性，正值偏橙红。越偏离 0 越深。</summary>
    public string TintBrush => Offset == 0
        ? "#1A1E24"
        : Offset < 0
            ? Interpolate("#1E5A63", "#39B0A6", Math.Min(1.0, -Offset / 16.0))
            : Interpolate("#6B3A1E", "#E0703A", Math.Min(1.0, Offset / 16.0));

    public string Tooltip => $"{Label}  ({HardwareHint})\n{SensitivityLabel}  ·  offset {Offset:+0;-0;0}";

    private static string Interpolate(string from, string to, double amount)
    {
        var (fr, fg, fb) = Parse(from);
        var (tr, tg, tb) = Parse(to);
        return $"#{(int)(fr + (tr - fr) * amount):X2}{(int)(fg + (tg - fg) * amount):X2}{(int)(fb + (tb - fb) * amount):X2}";
    }

    private static (int, int, int) Parse(string hex) => (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
}

public sealed class KeyMappingModel : NotifyModel
{
    private int board;
    private int channel;
    private bool isDuplicate;

    public KeyMappingModel(int index, string label, byte packed)
    {
        Index = index;
        Label = label;
        board = KeyMapping.BoardOf(packed);
        channel = KeyMapping.ChannelOf(packed);
        Localizer.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(StateLabel));
    }

    public int Index { get; }
    public string Label { get; }
    public bool IsDuplicate { get => isDuplicate; set { if (isDuplicate == value) return; isDuplicate = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateLabel)); } }
    public int Board { get => board; set { var clamped = Math.Clamp(value, 0, KeyMapping.BoardCount - 1); if (clamped == board) return; board = clamped; OnPropertyChanged(); OnPropertyChanged(nameof(BoardNumber)); OnPropertyChanged(); OnPropertyChanged(nameof(HardwareHint)); } }
    public int Channel { get => channel; set { var clamped = Math.Clamp(value, 0, KeyMapping.PortCount - 1); if (clamped == channel) return; channel = clamped; OnPropertyChanged(); OnPropertyChanged(nameof(HardwareHint)); } }
    /// <summary>下拉框使用的 1-based 板号（1..4）。</summary>
    public int BoardNumber { get => board + 1; set => Board = value - 1; }
    public byte Packed => KeyMapping.Pack(board, channel);
    public string HardwareHint => $"B{board + 1}P{channel} • 0x{0x5A + board:X2} CH{channel:00}";
    public string StateLabel => IsDuplicate ? Localizer.Instance["Config.Mapping.Duplicate"] : Localizer.Instance["Config.Mapping.Unique"];
    /// <summary>下拉框选项（实例属性，便于 XAML 直接绑定）。</summary>
    public int[] BoardItems { get; } = [1, 2, 3, 4];
    public int[] ChannelItems { get; } = Enumerable.Range(0, KeyMapping.PortCount).ToArray();
}

public sealed class ElectrodeZoneModel : NotifyModel
{
    private readonly Func<int> stepProvider;
    public ElectrodeZoneModel(string key, string nameKey, IReadOnlyList<ElectrodeEditorModel> electrodes, Func<int>? stepProvider = null)
    {
        Key = key;
        NameKey = nameKey;
        Electrodes = electrodes;
        this.stepProvider = stepProvider ?? (() => 4);
        IncreaseAllCommand = new RelayCommand(() => NudgeAll(this.stepProvider()));
        DecreaseAllCommand = new RelayCommand(() => NudgeAll(-this.stepProvider()));
        ResetAllCommand = new RelayCommand(ResetAll);
        Localizer.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Name));
    }
    public string Key { get; }
    public string NameKey { get; }
    public string Name => Localizer.Instance[NameKey];
    public IReadOnlyList<ElectrodeEditorModel> Electrodes { get; }

    public ICommand IncreaseAllCommand { get; }
    public ICommand DecreaseAllCommand { get; }
    public ICommand ResetAllCommand { get; }

    public void NudgeAll(int delta) { foreach (var electrode in Electrodes) electrode.Nudge(delta); }
    public void ResetAll() { foreach (var electrode in Electrodes) electrode.Reset(); }
}

public sealed class ConfigurationEditorModel : NotifyModel
{
    public static readonly string[] Labels = KeyMapping.KeyLabels;

    public ConfigurationEditorModel(ConfigBlob config) { Update(config); }
    public ObservableCollection<BoardEditorModel> Boards { get; } = [];
    public ObservableCollection<ElectrodeEditorModel> Electrodes { get; } = [];
    public ObservableCollection<ElectrodeZoneModel> Zones { get; } = [];
    /// <summary>34 键的板/通道映射（可编辑）。</summary>
    public ObservableCollection<KeyMappingModel> KeyMappings { get; } = [];
    /// <summary>单键微调的步进值（可按界面上的 Shift+滚轮调整）。</summary>
    public static readonly int[] AvailableSteps = [1, 2, 4, 8, 16];
    private int step = 4;
    private bool hasMappingConflict;
    public bool HasMappingConflict { get => hasMappingConflict; private set { if (hasMappingConflict == value) return; hasMappingConflict = value; OnPropertyChanged(); } }
    public int Step { get => step; set { step = Array.IndexOf(AvailableSteps, value) >= 0 ? value : step; OnPropertyChanged(); } }
    public void CycleStep(int direction)
    {
        var index = Array.IndexOf(AvailableSteps, step);
        if (index < 0) index = Array.IndexOf(AvailableSteps, 4);
        index = (index + direction + AvailableSteps.Length) % AvailableSteps.Length;
        Step = AvailableSteps[index];
    }
    public void ResetAllElectrodes() { foreach (var electrode in Electrodes) electrode.Reset(); }
    public ConfigBlob ToConfig() => new(Boards.Select(x => x.ToConfig()).ToArray(), Electrodes.Select(x => (sbyte)x.Offset).ToArray(), KeyMappings.Select(x => x.Packed).ToArray());

    /// <summary>把映射恢复为出厂默认（与参考固件 touchmap::Keys 一致）。</summary>
    public void RestoreDefaultMapping()
    {
        for (var index = 0; index < KeyMappings.Count && index < KeyMapping.Default.Length; index++)
        {
            var model = KeyMappings[index];
            model.Board = KeyMapping.BoardOf(KeyMapping.Default[index]);
            model.Channel = KeyMapping.ChannelOf(KeyMapping.Default[index]);
        }
        RefreshMappingConflict();
        OnPropertyChanged(nameof(HasMappingConflict));
    }

    /// <summary>重新计算哪些键的通道重复（每个通道只能分配给一个键）。</summary>
    public void RefreshMappingConflict()
    {
        var seen = new Dictionary<int, int>();
        hasMappingConflict = false;
        foreach (var model in KeyMappings)
        {
            var channel = model.Board * KeyMapping.PortCount + model.Channel;
            if (seen.ContainsKey(channel)) { model.IsDuplicate = true; hasMappingConflict = true; }
            else { model.IsDuplicate = false; seen[channel] = model.Index; }
        }
        OnPropertyChanged(nameof(HasMappingConflict));
    }

    public void Update(ConfigBlob config)
    {
        Boards.Clear();
        for (var i = 0; i < config.Boards.Length; i++) Boards.Add(new BoardEditorModel(i + 1, config.Boards[i]));
        KeyMappings.Clear();
        var mapping = config.Mapping;
        for (var i = 0; i < mapping.Length && i < Labels.Length; i++) KeyMappings.Add(new KeyMappingModel(i, Labels[i], mapping[i]));
        RefreshMappingConflict();
        Electrodes.Clear();
        for (var i = 0; i < config.ElectrodeOffsets.Length; i++)
        {
            var packed = i < mapping.Length ? mapping[i] : (byte)0;
            Electrodes.Add(new ElectrodeEditorModel(i, Labels[i], config.ElectrodeOffsets[i],
                (byte)KeyMapping.BoardOf(packed), (byte)KeyMapping.ChannelOf(packed), () => Step));
        }
        Zones.Clear();
        Zones.Add(new ElectrodeZoneModel("A", "Config.Zone.A", Electrodes.Take(8).ToList(), () => Step));
        Zones.Add(new ElectrodeZoneModel("B", "Config.Zone.B", Electrodes.Skip(8).Take(8).ToList(), () => Step));
        Zones.Add(new ElectrodeZoneModel("C", "Config.Zone.C", Electrodes.Skip(16).Take(2).ToList(), () => Step));
        Zones.Add(new ElectrodeZoneModel("D", "Config.Zone.D", Electrodes.Skip(18).Take(8).ToList(), () => Step));
        Zones.Add(new ElectrodeZoneModel("E", "Config.Zone.E", Electrodes.Skip(26).Take(8).ToList(), () => Step));
    }

    /// <summary>映射表被编辑后，让单键面板上的板/通道提示跟上。</summary>
    public void SyncHardwareHints()
    {
        for (var i = 0; i < Electrodes.Count && i < KeyMappings.Count; i++)
            Electrodes[i].SetHardware((byte)KeyMappings[i].Board, (byte)KeyMappings[i].Channel);
    }
}
