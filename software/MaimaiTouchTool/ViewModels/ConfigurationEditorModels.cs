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

/// <summary>
/// 按键 → 板/通道 的权威映射条目（键序，A1…E8）。<see cref="Board"/> 是配置板槽位 0..3，
/// 也就是 I2C 地址 0x5A+Board；<see cref="Channel"/> 是该芯片的端口 0..11。
/// </summary>
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
    public int Board { get => board; set { var clamped = Math.Clamp(value, 0, KeyMapping.BoardCount - 1); if (clamped == board) return; board = clamped; OnPropertyChanged(); OnPropertyChanged(nameof(BoardNumber)); OnPropertyChanged(nameof(HardwareHint)); OnPropertyChanged(nameof(AddressHex)); } }
    public int Channel { get => channel; set { var clamped = Math.Clamp(value, 0, KeyMapping.PortCount - 1); if (clamped == channel) return; channel = clamped; OnPropertyChanged(); OnPropertyChanged(nameof(HardwareHint)); } }
    /// <summary>下拉框使用的 1-based 板号（1..4）。</summary>
    public int BoardNumber { get => board + 1; set => Board = value - 1; }
    public int I2cAddress => 0x5A + board;
    public string AddressHex => $"0x{I2cAddress:X2}";
    public byte Packed => KeyMapping.Pack(board, channel);
    public string HardwareHint => $"B{board + 1} · {AddressHex} · CH{channel:00}";
    public string StateLabel => IsDuplicate ? Localizer.Instance["Config.Mapping.Duplicate"] : Localizer.Instance["Config.Mapping.Unique"];
}

/// <summary>
/// 焊盘弹出列表里的一项（键名 + 选中即指派到该通道的命令）。
/// <see cref="Label"/> 是给界面显示的文本（<see cref="ToString"/> 也返回它）。
/// </summary>
public sealed record MappingKeyOption(int KeyIndex, string Label, ICommand? SelectCommand = null)
{
    public override string ToString() => Label;
}
/// <summary>
/// 某块 PCB 上的一个电极槽位（12 个焊盘之一）。它只是「板号 + 通道」的一个视图，
/// 真正的归属由 <see cref="ConfigurationEditorModel"/> 在键序模型上裁决（换位，不丢键）。
/// </summary>
public sealed class MappingChannelModel : NotifyModel
{
    /// <summary>SVG 画布上的槽位边长（与 mpr121.svg 的焊盘坐标对齐）。</summary>
    public const double SlotSize = 40;
    private static readonly MappingKeyOption[] SharedKeyOptions = KeyMapping.KeyLabels
        .Select((label, index) => new MappingKeyOption(index, label))
        .ToArray();
    private const double PadBaseX = 55;
    private const double PadStepX = 50;
    private const double PadY = 385;

    private readonly MappingKeyOption[] keyOptions;
    private int keyIndex = -1;
    private bool isDuplicate;

    public MappingChannelModel(int groupIndex, int channel)
    {
        GroupIndex = groupIndex;
        Channel = channel;
        keyOptions = SharedKeyOptions
            .Select(option => option with { SelectCommand = new RelayCommand(() => KeyRequested?.Invoke(this, option.KeyIndex)) })
            .ToArray();
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(KeyLabel));
            OnPropertyChanged(nameof(PadLabel));
            OnPropertyChanged(nameof(PadTooltip));
            OnPropertyChanged(nameof(ChannelLabel));
            OnPropertyChanged(nameof(StateLabel));
        };
    }

    /// <summary>用户在下拉框里选了另一个键（-1 = 未分配）；由编辑器裁决换位。</summary>
    public event EventHandler<int>? KeyRequested;

    public int GroupIndex { get; }
    public int Channel { get; }
    /// <summary>该键在 34 键序里的索引；-1 表示这个通道没有键。</summary>
    public int KeyIndex { get => keyIndex; set { if (value == keyIndex) return; keyIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAssigned)); OnPropertyChanged(nameof(KeyLabel)); OnPropertyChanged(nameof(PadLabel)); OnPropertyChanged(nameof(PadBrush)); OnPropertyChanged(nameof(PadBorderBrush)); OnPropertyChanged(nameof(PadLabelBrush)); OnPropertyChanged(nameof(PadTooltip)); OnPropertyChanged(nameof(SelectedOptionIndex)); OnPropertyChanged(nameof(StateLabel)); } }
    public bool IsAssigned => keyIndex >= 0;
    public bool IsDuplicate { get => isDuplicate; set { if (isDuplicate == value) return; isDuplicate = value; OnPropertyChanged(); OnPropertyChanged(nameof(PadBrush)); OnPropertyChanged(nameof(PadBorderBrush)); OnPropertyChanged(nameof(StateLabel)); } }
    public string KeyLabel => keyIndex >= 0 ? KeyMapping.KeyLabels[keyIndex] : Localizer.Instance["Config.Mapping.Unassigned"];
    public string StateLabel => IsDuplicate ? Localizer.Instance["Config.Mapping.Duplicate"] : (IsAssigned ? KeyLabel : Localizer.Instance["Config.Mapping.Unassigned"]);
    public string ChannelLabel => string.Format(Localizer.Instance["Config.Mapping.ChannelN"], Channel);
    /// <summary>焊盘上的文字：已分配显示键名，空显示短横。</summary>
    public string PadLabel => keyIndex >= 0 ? KeyMapping.KeyLabels[keyIndex] : "–";
    /// <summary>下拉框选项（含每项的指派命令，供焊盘弹出列表使用）。</summary>
    public IReadOnlyList<MappingKeyOption> KeyOptions => keyOptions;

    // ---- PCB 图形：把焊盘坐标换算成 Canvas 定位 ----
    public double PadX => PadBaseX + PadStepX * Channel;
    public double PadLeft => PadX - SlotSize / 2;
    public double PadTop => PadY - SlotSize / 2;

    // ---- 焊盘配色 ----
    public string PadBrush => IsDuplicate ? "#B4453A" : IsAssigned ? "#5FA88F" : "#00000000";
    public string PadBorderBrush => IsDuplicate ? "#E86A5A" : IsAssigned ? "#8FD3BC" : "#4A535D";
    public string PadLabelBrush => IsAssigned ? "#0F172A" : "#8A939D";
    public string PadTooltip => $"{ChannelLabel} · {StateLabel}";

    /// <summary>下拉框用 SelectedIndex 绑定：0..33 = 键序，-1 = 未分配。</summary>
    public int SelectedOptionIndex
    {
        get => keyIndex;
        set
        {
            var wanted = value < 0 ? -1 : Math.Min(value, KeyMapping.KeyCount - 1);
            if (wanted == keyIndex) return;
            KeyRequested?.Invoke(this, wanted);
        }
    }
}

/// <summary>
/// 一块 MPR121 PCB（图形 + 通道列表）。<see cref="Slot"/> 是它占用的配置板槽位 0..3
/// （即 I2C 地址 0x5A+Slot）；换槽位会与另一块板对调，保证四块板地址始终互不相同。
/// </summary>
public sealed class MappingBoardModel : NotifyModel
{
    public static readonly string[] AddressOptions = ["0x5A", "0x5B", "0x5C", "0x5D"];
    private int slot;
    private int fillCount;

    public MappingBoardModel(int groupIndex, int slot, int fillCount, IReadOnlyList<MappingChannelModel> channels)
    {
        GroupIndex = groupIndex;
        this.slot = slot;
        this.fillCount = fillCount;
        Channels = channels;
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Title));
        };
    }

    /// <summary>用户选了另一个 I2C 地址（板槽位）；由编辑器对调。</summary>
    public event EventHandler<int>? SlotRequested;

    /// <summary>界面上第几块板（0..3，固定不随地址变化）。</summary>
    public int GroupIndex { get; }
    /// <summary>下拉框选项（实例属性，便于 XAML 直接绑定）。</summary>
    public string[] AddressItems => AddressOptions;
    public IReadOnlyList<MappingChannelModel> Channels { get; }
    public int Slot { get => slot; set { var clamped = Math.Clamp(value, 0, KeyMapping.BoardCount - 1); if (clamped == slot) return; SlotRequested?.Invoke(this, clamped); } }
    /// <summary>编辑器内部使用的静默赋值（不触发对调）。</summary>
    internal void SetSlotQuiet(int value) { value = Math.Clamp(value, 0, KeyMapping.BoardCount - 1); if (value == slot) return; slot = value; OnPropertyChanged(nameof(Slot)); OnPropertyChanged(nameof(I2cAddress)); OnPropertyChanged(nameof(AddressHex)); OnPropertyChanged(nameof(Title)); }
    public int I2cAddress => 0x5A + slot;
    public string AddressHex => $"0x{I2cAddress:X2}";
    /// <summary>PCB 图形（与触摸键 SVG 一样通过 SvgPathConverter 加载）。</summary>
    public string SvgPath => "avares://MaimaiTouchTool/Assets/mpr121.svg";
    public string Label => string.Format(Localizer.Instance["Config.Mapping.BoardN"], GroupIndex + 1);
    public string Title => $"{Label} · {AddressHex}";

    /// <summary>「新建板卡」批量填充时本板占用的通道数。</summary>
    public int FillCount { get => fillCount; set { var clamped = Math.Clamp(value, 0, KeyMapping.PortCount); if (clamped == fillCount) return; fillCount = clamped; OnPropertyChanged(); } }
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
    /// <summary>34 键的板/通道映射（权威、键序）。映射编辑器只通过下面的方法改动它。</summary>
    public ObservableCollection<KeyMappingModel> KeyMappings { get; } = [];
    /// <summary>按板分组的映射视图（4 块 PCB × 12 通道），随 <see cref="KeyMappings"/> 重建。</summary>
    public ObservableCollection<MappingBoardModel> BoardMappings { get; } = [];
    /// <summary>单键微调的步进值（可按界面上的 Shift+滚轮调整）。</summary>
    public static readonly int[] AvailableSteps = [1, 2, 4, 8, 16];
    private readonly int[] groupSlots = [0, 1, 2, 3];
    private bool mappingSync;
    private int step = 4;
    private bool hasMappingConflict;
    public bool HasMappingConflict { get => hasMappingConflict; private set { if (hasMappingConflict == value) return; hasMappingConflict = value; OnPropertyChanged(); } }
    public int Step { get => step; set { step = Array.IndexOf(AvailableSteps, value) >= 0 ? value : step; OnPropertyChanged(); } }
    /// <summary>映射编辑器的一次改动已被应用（供撤销栈捕获）。</summary>
    public event EventHandler? MappingChanged;
    public void CycleStep(int direction)
    {
        var index = Array.IndexOf(AvailableSteps, step);
        if (index < 0) index = Array.IndexOf(AvailableSteps, 4);
        index = (index + direction + AvailableSteps.Length) % AvailableSteps.Length;
        Step = AvailableSteps[index];
    }
    public void ResetAllElectrodes() { foreach (var electrode in Electrodes) electrode.Reset(); }
    public ConfigBlob ToConfig() => new(Boards.Select(x => x.ToConfig()).ToArray(), Electrodes.Select(x => (sbyte)x.Offset).ToArray(), KeyMappings.Select(x => x.Packed).ToArray());

    /// <summary>把映射恢复为出厂默认（与参考固件 touchmap::Keys 一致），并把四块板的地址复原。</summary>
    public void RestoreDefaultMapping()
    {
        mappingSync = true;
        for (var index = 0; index < KeyMappings.Count && index < KeyMapping.Default.Length; index++)
        {
            var model = KeyMappings[index];
            model.Board = KeyMapping.BoardOf(KeyMapping.Default[index]);
            model.Channel = KeyMapping.ChannelOf(KeyMapping.Default[index]);
        }
        for (var group = 0; group < groupSlots.Length; group++) groupSlots[group] = group;
        mappingSync = false;
        RebuildBoardMappings();
        OnMappingMutated();
    }

    /// <summary>当前映射快照（34 个打包字节），供撤销栈 / 快照文件使用。</summary>
    public byte[] CurrentMapping() => KeyMappings.Select(x => x.Packed).ToArray();

    /// <summary>当前四块板的地址排布（组序 → 板槽位）。</summary>
    public int[] CurrentSlots() => (int[])groupSlots.Clone();

    /// <summary>把一组映射 + 地址排布整体写回（撤销/重做/快照回滚）。</summary>
    public void LoadMapping(byte[] mapping, int[]? slots)
    {
        mappingSync = true;
        for (var index = 0; index < KeyMappings.Count && index < mapping.Length; index++)
        {
            var packed = mapping[index];
            if (!KeyMapping.IsValidPacked(packed)) continue;
            KeyMappings[index].Board = KeyMapping.BoardOf(packed);
            KeyMappings[index].Channel = KeyMapping.ChannelOf(packed);
        }
        if (slots is not null)
            for (var group = 0; group < groupSlots.Length && group < slots.Length; group++)
                groupSlots[group] = Math.Clamp(slots[group], 0, KeyMapping.BoardCount - 1);
        mappingSync = false;
        RebuildBoardMappings();
        OnMappingMutated();
    }

    /// <summary>把某个键指派到「组/通道」；原本占位的键会被换到该键原来所在的通道（不丢键）。</summary>
    public void AssignChannel(int groupIndex, int channel, int keyIndex)
    {
        if (groupIndex < 0 || groupIndex >= groupSlots.Length) return;
        if (channel < 0 || channel >= KeyMapping.PortCount) return;
        if (keyIndex < 0 || keyIndex >= KeyMappings.Count) return;
        var slot = groupSlots[groupIndex];
        var target = KeyMappings[keyIndex];
        var previousBoard = target.Board;
        var previousChannel = target.Channel;

        var occupant = KeyMappings.FirstOrDefault(k => k.Board == slot && k.Channel == channel);
        if (ReferenceEquals(occupant, target)) return;

        mappingSync = true;
        target.Board = slot;
        target.Channel = channel;
        if (occupant is not null)
        {
            occupant.Board = previousBoard;
            occupant.Channel = previousChannel;
        }
        mappingSync = false;
        RebuildBoardMappings();
        OnMappingMutated();
    }

    /// <summary>把一块板换成另一个 I2C 地址：与当前占用该地址的板整体对调（含其上的键）。</summary>
    public void SetBoardSlot(int groupIndex, int slot)
    {
        if (groupIndex < 0 || groupIndex >= groupSlots.Length) return;
        slot = Math.Clamp(slot, 0, KeyMapping.BoardCount - 1);
        var old = groupSlots[groupIndex];
        if (old == slot) return;
        var other = Array.IndexOf(groupSlots, slot);

        mappingSync = true;
        groupSlots[groupIndex] = slot;
        if (other >= 0 && other != groupIndex) groupSlots[other] = old;
        // 键跟着板走：OLD 槽位的键搬到 SLOT，反之亦然。
        foreach (var model in KeyMappings)
        {
            if (model.Board == old) model.Board = slot;
            else if (other >= 0 && other != groupIndex && model.Board == slot) model.Board = old;
        }
        mappingSync = false;
        RebuildBoardMappings();
        OnMappingMutated();
    }

    /// <summary>
    /// 「新建板卡」批量填充：按板序把连续键名灌进每块板的前 N 个通道，
    /// 剩下的键顺次填到其余空闲通道（结果始终是 34 键全分配的合法映射）。
    /// </summary>
    public void FillBoards(IReadOnlyList<int> counts)
    {
        var used = new bool[KeyMapping.ChannelCount];
        var packed = new byte[KeyMappings.Count];
        var cursor = 0;
        for (var group = 0; group < groupSlots.Length; group++)
        {
            var count = group < counts.Count ? Math.Clamp(counts[group], 0, KeyMapping.PortCount) : 0;
            for (var channel = 0; channel < count && cursor < packed.Length; channel++)
            {
                var index = groupSlots[group] * KeyMapping.PortCount + channel;
                if (used[index]) continue;
                used[index] = true;
                packed[cursor++] = KeyMapping.Pack(groupSlots[group], channel);
            }
        }
        // 剩余的键按板序填到还没用到的通道里。
        for (var group = 0; group < groupSlots.Length && cursor < packed.Length; group++)
        {
            for (var channel = 0; channel < KeyMapping.PortCount && cursor < packed.Length; channel++)
            {
                var index = groupSlots[group] * KeyMapping.PortCount + channel;
                if (used[index]) continue;
                used[index] = true;
                packed[cursor++] = KeyMapping.Pack(groupSlots[group], channel);
            }
        }
        LoadMapping(packed, null);
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
        foreach (var model in KeyMappings) model.PropertyChanged += OnKeyMappingChanged;
        for (var group = 0; group < groupSlots.Length; group++) groupSlots[group] = group;
        RefreshMappingConflict();
        RebuildBoardMappings();
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

    /// <summary>按当前键序映射刷新「按板分组」视图（4 组 × 12 通道，实例保持稳定）。</summary>
    public void RebuildBoardMappings()
    {
        EnsureBoardMappings();
        mappingSync = true;
        var flat = CurrentMapping();
        for (var group = 0; group < BoardMappings.Count && group < groupSlots.Length; group++)
        {
            var board = BoardMappings[group];
            var slot = groupSlots[group];
            board.SetSlotQuiet(slot);
            for (var channel = 0; channel < board.Channels.Count; channel++)
            {
                var model = board.Channels[channel];
                model.KeyIndex = KeyMapping.KeyIndexForChannel(flat, slot * KeyMapping.PortCount + channel);
                model.IsDuplicate = model.IsAssigned && model.KeyIndex < KeyMappings.Count && KeyMappings[model.KeyIndex].IsDuplicate;
            }
        }
        mappingSync = false;
    }

    private void EnsureBoardMappings()
    {
        if (BoardMappings.Count == groupSlots.Length) return;
        mappingSync = true;
        BoardMappings.Clear();
        for (var group = 0; group < groupSlots.Length; group++)
        {
            var channels = new List<MappingChannelModel>(KeyMapping.PortCount);
            for (var channel = 0; channel < KeyMapping.PortCount; channel++) channels.Add(new MappingChannelModel(group, channel));
            foreach (var model in channels) model.KeyRequested += OnChannelKeyRequested;
            var board = new MappingBoardModel(group, groupSlots[group], DefaultFillCount(group), channels);
            board.SlotRequested += OnBoardSlotRequested;
            BoardMappings.Add(board);
        }
        mappingSync = false;
    }

    private static int DefaultFillCount(int group) => group switch { 0 => 8, 1 => 9, 2 => 9, _ => 8 };

    private void OnChannelKeyRequested(object? sender, int keyIndex)
    {
        if (mappingSync || sender is not MappingChannelModel channel) return;
        AssignChannel(channel.GroupIndex, channel.Channel, keyIndex);
    }

    /// <summary>键序映射被直接改动（测试/导入路径）时同步分组视图与冲突状态。</summary>
    private void OnKeyMappingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (mappingSync) return;
        if (args.PropertyName is not (nameof(KeyMappingModel.Board) or nameof(KeyMappingModel.Channel) or nameof(KeyMappingModel.BoardNumber))) return;
        RebuildBoardMappings();
        OnMappingMutated();
    }

    private void OnBoardSlotRequested(object? sender, int slot)
    {
        if (mappingSync || sender is not MappingBoardModel board) return;
        SetBoardSlot(board.GroupIndex, slot);
    }

    private void OnMappingMutated()
    {
        // 先算冲突（更新键序模型的 IsDuplicate），再重建分组视图把它映射到通道，最后刷新单键面板提示。
        RefreshMappingConflict();
        RebuildBoardMappings();
        SyncHardwareHints();
        OnPropertyChanged(nameof(HasMappingConflict));
        MappingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>映射表被编辑后，让单键面板上的板/通道提示跟上。</summary>
    public void SyncHardwareHints()
    {
        for (var i = 0; i < Electrodes.Count && i < KeyMappings.Count; i++)
            Electrodes[i].SetHardware((byte)KeyMappings[i].Board, (byte)KeyMappings[i].Channel);
    }
}
