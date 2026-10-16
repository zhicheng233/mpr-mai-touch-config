using System.Text.Json;
using System.Text.Json.Serialization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.Persistence;

/// <summary>一次映射编辑的完整状态：34 键打包字节 + 四块板的地址排布。</summary>
public sealed class MappingState
{
    public byte[] KeyMap { get; set; } = (byte[])KeyMapping.Default.Clone();
    public int[] Slots { get; set; } = [0, 1, 2, 3];

    public MappingState Clone() => new() { KeyMap = (byte[])KeyMap.Clone(), Slots = (int[])Slots.Clone() };

    public bool ValueEquals(MappingState? other) =>
        other is not null && KeyMap.AsSpan().SequenceEqual(other.KeyMap) && Slots.AsSpan().SequenceEqual(other.Slots);
}

/// <summary>
/// 映射编辑专用的撤销/重做栈。它只记录「键 → 板/通道」与四块板的地址排布，
/// 与设备无关；每次成功编辑后由编辑器事件推入一个原子状态。
/// </summary>
public sealed class MappingHistory
{
    private const int MaxDepth = 64;
    private readonly List<MappingState> states = [];
    private int cursor = -1;

    public bool CanUndo => cursor > 0;
    public bool CanRedo => cursor >= 0 && cursor < states.Count - 1;

    /// <summary>建立基线（载入配置、切换设备时调用），清空历史。</summary>
    public void Reset(MappingState initial)
    {
        states.Clear();
        states.Add(initial.Clone());
        cursor = 0;
    }

    /// <summary>推入一个新状态；与当前状态相同则忽略。</summary>
    public void Push(MappingState state)
    {
        if (cursor < 0) { Reset(state); return; }
        if (states[cursor].ValueEquals(state)) return;
        if (cursor < states.Count - 1) states.RemoveRange(cursor + 1, states.Count - 1 - cursor);
        states.Add(state.Clone());
        if (states.Count > MaxDepth) { states.RemoveAt(0); }
        cursor = states.Count - 1;
    }

    public MappingState? Undo()
    {
        if (!CanUndo) return null;
        cursor--;
        return states[cursor].Clone();
    }

    public MappingState? Redo()
    {
        if (!CanRedo) return null;
        cursor++;
        return states[cursor].Clone();
    }
}

/// <summary>映射快照文件：每次 Apply/Save/写入 JSON 时留一份，可一键回滚到上一个好用的映射。</summary>
public sealed class MappingSnapshotStore
{
    public sealed record Entry(DateTimeOffset Timestamp, string Source, byte[] KeyMap, int[] Slots);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private const int MaxEntries = 20;

    /// <summary>把当前映射追加为一枚快照（与最新一枚相同则跳过）。</summary>
    public static void Append(string path, MappingState state, string source, int limit = MaxEntries)
    {
        var entries = Load(path);
        if (entries.Count > 0)
        {
            var last = entries[^1];
            if (last.KeyMap.AsSpan().SequenceEqual(state.KeyMap) && last.Slots.AsSpan().SequenceEqual(state.Slots)) return;
        }
        entries.Add(new Entry(DateTimeOffset.Now, source, (byte[])state.KeyMap.Clone(), (int[])state.Slots.Clone()));
        if (entries.Count > limit) entries.RemoveRange(0, entries.Count - limit);
        Save(path, entries);
    }

    public static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>最近一枚与当前不同的快照（用于「回滚到上一快照」）。</summary>
    public static Entry? Previous(string path, MappingState current)
    {
        var entries = Load(path);
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (!entry.KeyMap.AsSpan().SequenceEqual(current.KeyMap) || !entry.Slots.AsSpan().SequenceEqual(current.Slots)) return entry;
        }
        return null;
    }

    private static void Save(string path, List<Entry> entries)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
            File.Move(temp, fullPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 快照只是便利功能，写失败不应影响主流程。
        }
    }
}
