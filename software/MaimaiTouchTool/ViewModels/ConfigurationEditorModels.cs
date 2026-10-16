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
    public ElectrodeEditorModel(int index, string label, int value, byte mprId = 0, byte portId = 0, Func<int>? stepProvider = null)
    {
        Index = index;
        Label = label;
        Offset = value;
        MprId = mprId;
        PortId = portId;
        this.stepProvider = stepProvider ?? (() => 4);
        IncreaseCommand = new RelayCommand(() => Nudge(this.stepProvider()));
        DecreaseCommand = new RelayCommand(() => Nudge(-this.stepProvider()));
        Localizer.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(SensitivityLabel));
    }
    public int Index { get; }
    public string Label { get; }
    public int Offset { get => offset; set { var clamped = Math.Clamp(value, -127, 127); if (clamped == offset) return; offset = clamped; OnPropertyChanged(); OnPropertyChanged(nameof(SensitivityLabel)); OnPropertyChanged(nameof(TintBrush)); OnPropertyChanged(nameof(Tooltip)); } }
    public byte MprId { get; }
    public byte PortId { get; }
    public int BoardNumber => MprId + 1;
    public int I2cAddress => 0x5A + MprId;
    public string HardwareHint => $"Board {BoardNumber} (0x{I2cAddress:X2}) • Port {PortId}";

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
    private static readonly string[] Labels = ["A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "C1", "C2", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8"];

    private static readonly (byte MprId, byte PortId)[] HardwareMapping =
    [
        // Group A (A1..A8)
        (3, 4), (3, 0), (2, 4), (2, 0), (1, 5), (1, 0), (0, 4), (0, 0),
        // Group B (B1..B8)
        (3, 5), (3, 1), (2, 5), (2, 1), (1, 6), (1, 1), (0, 5), (0, 1),
        // Group C (C1..C2)
        (2, 8), (1, 2),
        // Group D (D1..D8)
        (3, 6), (3, 2), (2, 6), (2, 2), (1, 7), (1, 3), (0, 6), (0, 2),
        // Group E (E1..E8)
        (3, 7), (3, 3), (2, 7), (2, 3), (1, 8), (1, 4), (0, 7), (0, 3)
    ];

    public ConfigurationEditorModel(ConfigBlob config) { Update(config); }
    public ObservableCollection<BoardEditorModel> Boards { get; } = [];
    public ObservableCollection<ElectrodeEditorModel> Electrodes { get; } = [];
    public ObservableCollection<ElectrodeZoneModel> Zones { get; } = [];
    /// <summary>单键微调的步进值（可按界面上的 Shift+滚轮调整）。</summary>
    public static readonly int[] AvailableSteps = [1, 2, 4, 8, 16];
    private int step = 4;
    public int Step { get => step; set { step = Array.IndexOf(AvailableSteps, value) >= 0 ? value : step; OnPropertyChanged(); } }
    public void CycleStep(int direction)
    {
        var index = Array.IndexOf(AvailableSteps, step);
        if (index < 0) index = Array.IndexOf(AvailableSteps, 4);
        index = (index + direction + AvailableSteps.Length) % AvailableSteps.Length;
        Step = AvailableSteps[index];
    }
    public void ResetAllElectrodes() { foreach (var electrode in Electrodes) electrode.Reset(); }
    public ConfigBlob ToConfig() => new(Boards.Select(x => x.ToConfig()).ToArray(), Electrodes.Select(x => (sbyte)x.Offset).ToArray());
    public void Update(ConfigBlob config)
    {
        Boards.Clear();
        for (var i = 0; i < config.Boards.Length; i++) Boards.Add(new BoardEditorModel(i + 1, config.Boards[i]));
        Electrodes.Clear();
        for (var i = 0; i < config.ElectrodeOffsets.Length; i++)
        {
            var (mprId, portId) = i < HardwareMapping.Length ? HardwareMapping[i] : ((byte)0, (byte)0);
            Electrodes.Add(new ElectrodeEditorModel(i, Labels[i], config.ElectrodeOffsets[i], mprId, portId, () => Step));
        }
        Zones.Clear();
        Zones.Add(new ElectrodeZoneModel("A", "Config.Zone.A", Electrodes.Take(8).ToList(), () => Step));
        Zones.Add(new ElectrodeZoneModel("B", "Config.Zone.B", Electrodes.Skip(8).Take(8).ToList(), () => Step));
        Zones.Add(new ElectrodeZoneModel("C", "Config.Zone.C", Electrodes.Skip(16).Take(2).ToList(), () => Step));
        Zones.Add(new ElectrodeZoneModel("D", "Config.Zone.D", Electrodes.Skip(18).Take(8).ToList(), () => Step));
        Zones.Add(new ElectrodeZoneModel("E", "Config.Zone.E", Electrodes.Skip(26).Take(8).ToList(), () => Step));
    }
}
