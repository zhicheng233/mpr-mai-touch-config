using MaimaiTouchTool.Persistence;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class MappingLearnTests
{
    private const int BaselineMs = 300;   // 测试里缩短基线，避免大量帧
    private const int KeyTimeoutMs = 2000;

    private static ushort[] Flat(ushort value = 700)
    {
        var values = new ushort[48];
        Array.Fill(values, value);
        return values;
    }

    private static MappingLearnSession Started(params (int KeyIndex, string Label)[] targets)
    {
        var session = new MappingLearnSession(
            targets.Select(t => new MappingLearnSession.TargetKey(t.KeyIndex, t.Label)), BaselineMs, KeyTimeoutMs);
        session.Begin(0);
        return session;
    }

    private static void RunBaseline(MappingLearnSession session)
    {
        var flat = Flat();
        session.PushFrame(0, flat, flat);
        session.PushFrame((uint)BaselineMs, flat, flat);
    }

    private static void Press(MappingLearnSession session, uint startMs, int channel, ushort depth)
    {
        for (var frame = 0; frame < 4; frame++)
        {
            var filtered = Flat();
            var baseline = Flat();
            filtered[channel] = (ushort)(baseline[channel] - depth);
            session.PushFrame(startMs + (uint)(frame * 30), filtered, baseline);
        }
    }

    private static void Release(MappingLearnSession session, uint startMs)
    {
        var flat = Flat();
        // 需要超过 HoldGraceMs(400ms) 才算释放。
        session.PushFrame(startMs, flat, flat);
        session.PushFrame(startMs + 500, flat, flat);
    }

    [Fact]
    public void LearnFindsPressedChannelForEachKey()
    {
        var session = Started((0, "A1"), (5, "B6"));
        RunBaseline(session);
        Assert.Equal(GuidedPhase.Measuring, session.Phase);

        Press(session, 400, channel: 10, depth: 220);
        Release(session, 600);
        Assert.Single(session.Outcomes);
        Assert.True(session.Outcomes[0].Detected);
        Assert.Equal(10, session.Outcomes[0].Channel);
        Assert.Equal(0, session.Outcomes[0].Board);

        Press(session, 1200, channel: 37, depth: 180);
        Release(session, 1400);

        Assert.True(session.IsComplete);
        Assert.Equal(2, session.DetectedCount);
        Assert.Equal(37, session.Outcomes[1].Channel);
        Assert.Equal(3, session.Outcomes[1].Board);
    }

    [Fact]
    public void IgnoringNoiseBelowSpanDoesNotDetect()
    {
        var session = Started((0, "A1"));
        RunBaseline(session);
        // 低于启动阈值（12）的极小抖动不应被当成按压。
        Press(session, 400, channel: 5, depth: 5);
        Release(session, 600);
        Assert.Empty(session.Outcomes);

        // 真实按压仍能识别。
        Press(session, 900, channel: 5, depth: 200);
        Release(session, 1100);
        Assert.Single(session.Outcomes);
        Assert.True(session.Outcomes[0].Detected);
        Assert.Equal(5, session.Outcomes[0].Channel);
    }

    [Fact]
    public void TimeoutLeavesKeyUndetected()
    {
        var session = Started((0, "A1"));
        RunBaseline(session);
        var flat = Flat();
        session.PushFrame((uint)(BaselineMs + KeyTimeoutMs + 100), flat, flat);
        Assert.True(session.IsComplete);
        Assert.Single(session.Outcomes);
        Assert.False(session.Outcomes[0].Detected);
    }

    [Fact]
    public void SkipAndEarlyFinishAdvance()
    {
        var session = Started((0, "A1"), (1, "A2"));
        RunBaseline(session);
        session.SkipCurrentKey(400);
        Assert.Single(session.Outcomes);
        Assert.False(session.Outcomes[0].Detected);

        Press(session, 500, channel: 20, depth: 150);
        Assert.True(session.CanFinishCurrentKeyEarly);
        session.FinishCurrentKeyEarly(560);
        Assert.True(session.IsComplete);
        Assert.True(session.Outcomes[1].Detected);
        Assert.Equal(20, session.Outcomes[1].Channel);
    }

    [Fact]
    public void ApplyToPreservesUndetectedKeysAndFlagsDuplicates()
    {
        var session = Started((0, "A1"), (1, "A2"));
        RunBaseline(session);
        // 两个键都测到同一个通道 → 重复映射。
        Press(session, 400, channel: 15, depth: 200);
        Release(session, 600);
        Press(session, 1000, channel: 15, depth: 200);
        Release(session, 1200);

        var original = KeyMapping.Default;
        var (mapping, _) = session.ApplyTo(original);
        Assert.Equal(KeyMapping.Pack(1, 3), mapping[0]);
        Assert.Equal(KeyMapping.Pack(1, 3), mapping[1]);
        Assert.False(KeyMapping.TryValidate(mapping, out _));

        // 未识别的键保留原映射。
        var single = Started((7, "A8"));
        RunBaseline(single);
        single.SkipCurrentKey(400);
        var (kept, _) = single.ApplyTo(original);
        Assert.Equal(original[7], kept[7]);
        Assert.True(KeyMapping.TryValidate(kept, out _));
    }

    [Fact]
    public void ProgressAdvancesThroughBaselineAndKeys()
    {
        var session = Started((0, "A1"), (1, "A2"));
        session.PushFrame(0, Flat(), Flat());
        Assert.InRange(session.TotalProgress, 0, 0.1);
        session.PushFrame((uint)BaselineMs, Flat(), Flat());
        Assert.Equal(GuidedPhase.Measuring, session.Phase);
        Assert.True(session.TotalProgress >= 0.1);
        Press(session, 400, 12, 200);
        Assert.True(session.LiveDepth >= 200);
        Release(session, 600);
        // 第一个键完成后进度必须越过基线段。
        Assert.True(session.TotalProgress > 0.1);
    }
}

public sealed class KeyMappingEditorTests
{
    [Fact]
    public void EditorExposesOneRowPerKeyAndTracksConflicts()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        Assert.Equal(34, editor.KeyMappings.Count);
        Assert.False(editor.HasMappingConflict);
        Assert.Equal("A1", editor.KeyMappings[0].Label);
        Assert.Equal(3, editor.KeyMappings[0].Board);          // 0-based board 3
        Assert.Equal(4, editor.KeyMappings[0].BoardNumber);    // 1-based board 4
        Assert.Equal(4, editor.KeyMappings[0].Channel);
        Assert.Equal(4, editor.Electrodes[0].MprId + 1);        // 单键面板提示跟随映射
        Assert.Equal(4, editor.Electrodes[0].PortId);

        // 把 A2 指到 A1 的通道 → 冲突。
        editor.KeyMappings[1].Board = editor.KeyMappings[0].Board;
        editor.KeyMappings[1].Channel = editor.KeyMappings[0].Channel;
        editor.RefreshMappingConflict();
        Assert.True(editor.HasMappingConflict);
        Assert.True(editor.KeyMappings[1].IsDuplicate);
    }

    [Fact]
    public void RestoreDefaultMappingClearsConflicts()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        editor.KeyMappings[3].Board = editor.KeyMappings[0].Board;
        editor.KeyMappings[3].Channel = editor.KeyMappings[0].Channel;
        editor.RefreshMappingConflict();
        Assert.True(editor.HasMappingConflict);

        editor.RestoreDefaultMapping();
        Assert.False(editor.HasMappingConflict);
        Assert.All(editor.KeyMappings, model => Assert.False(model.IsDuplicate));
        Assert.Equal(KeyMapping.Default, editor.ToConfig().Mapping);
    }

    [Fact]
    public void MappingFlowsThroughToConfigAndEncoding()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        editor.KeyMappings[0].Board = 0;   // 0-based board 0
        editor.KeyMappings[0].Channel = 9;
        editor.SyncHardwareHints();
        var config = editor.ToConfig();
        Assert.Equal(KeyMapping.Pack(0, 9), config.Mapping[0]);
        Assert.Equal(0, editor.Electrodes[0].MprId);
        Assert.Equal(9, editor.Electrodes[0].PortId);

        var roundTrip = ConfigBlob.Decode(config.Encode());
        Assert.Equal(config.Mapping, roundTrip.Mapping);

        // 让映射与默认冲突（占用 A1 的默认通道）——先还原 A1 再用 A2 抢占。
        editor.RestoreDefaultMapping();
        editor.KeyMappings[1].Board = editor.KeyMappings[0].Board;
        editor.KeyMappings[1].Channel = editor.KeyMappings[0].Channel;
        editor.RefreshMappingConflict();
        Assert.Throws<ArgumentException>(() => editor.ToConfig().Encode());
    }

    [Fact]
    public void TelemetryFollowsConfiguredMapping()
    {
        var telemetry = new TelemetrySessionModel();
        var mapping = (byte[])KeyMapping.Default.Clone();
        mapping[0] = KeyMapping.Pack(0, 8);   // A1 -> board0/channel8
        var config = ConfigBlob.Defaults() with { KeyMap = mapping, ElectrodeOffsets = (sbyte[])ConfigBlob.Defaults().ElectrodeOffsets.Clone() };
        config.ElectrodeOffsets[0] = -20;
        telemetry.ApplyThresholds(config);

        // board0/channel8 现在属于 A1，阈值 = 20 + 0 + (-20) = 1（下限）。
        Assert.Equal("A1", telemetry.Channels[8].KeyLabel);
        Assert.Equal((ushort)1, telemetry.Channels[8].Threshold);
        // 原 A1 通道（board3/channel4 = 40）不再映射到 A1。
        Assert.Equal("unmapped", telemetry.Channels[40].KeyLabel);

        // TouchBits 位 33-0 = 键 0 → 通道 8 显示为触摸，通道 40 不是。
        var frame = new DebugFrame(0, 1UL << 33, new ushort[48], new ushort[48], new byte[4], 0);
        telemetry.AddFrame(frame, 1, DateTimeOffset.UtcNow);
        Assert.True(telemetry.Channels[8].IsTouched);
        Assert.False(telemetry.Channels[40].IsTouched);
    }
}

/// <summary>按板分组视图 / 换位指派 / 板号对调 / 批量填充 / 撤销重做 / 快照。</summary>
public sealed class BoardGroupedMappingTests
{
    [Fact]
    public void BoardViewGroupsKeysByBoardAndChannel()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        Assert.Equal(4, editor.BoardMappings.Count);
        Assert.All(editor.BoardMappings, board => Assert.Equal(12, board.Channels.Count));
        Assert.Equal([0, 1, 2, 3], editor.BoardMappings.Select(b => b.Slot).ToArray());
        Assert.Equal(["0x5A", "0x5B", "0x5C", "0x5D"], editor.BoardMappings.Select(b => b.AddressHex).ToArray());

        // A1 依据默认映射落在 board 3（0x5D）通道 4。
        var board3 = editor.BoardMappings[3];
        Assert.Equal("A1", board3.Channels[4].KeyLabel);
        Assert.True(board3.Channels[4].IsAssigned);
        // 同一块板上没有第二个键占据通道 4；而空通道显示未分配。
        Assert.False(editor.BoardMappings[0].Channels[8].IsAssigned);   // 默认映射只用到 CH0..CH7
        Assert.Equal(34, editor.BoardMappings.Sum(b => b.Channels.Count(c => c.IsAssigned)));
    }

    [Fact]
    public void AssignChannelSwapsInsteadOfLosingKeys()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        // A1 在 board3/CH4，A2 在 board3/CH0；把 A2 指派到 A1 的位置 → 两者对调。
        editor.AssignChannel(3, 4, 1);
        Assert.Equal(KeyMapping.Pack(3, 4), editor.KeyMappings[1].Packed);
        Assert.Equal(KeyMapping.Pack(3, 0), editor.KeyMappings[0].Packed);
        Assert.False(editor.HasMappingConflict);
        Assert.Equal(34, editor.BoardMappings.Sum(b => b.Channels.Count(c => c.IsAssigned)));

        // 指派到一个空通道：只搬走，来源变空（34 个键始终都在某个通道上）。
        editor.AssignChannel(0, 11, 0);
        Assert.Equal(KeyMapping.Pack(0, 11), editor.KeyMappings[0].Packed);
        Assert.False(editor.BoardMappings[3].Channels[0].IsAssigned);   // A1 离开后 CH0 空出来
        Assert.Equal(34, editor.BoardMappings.Sum(b => b.Channels.Count(c => c.IsAssigned)));
    }

    [Fact]
    public void ChangingBoardAddressSwapsWholeBoards()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        // 板 1（组 0 / 0x5A）其实是 0x5D：与占用 0x5D 的组对调整块板（含其上的键）。
        var beforeA1 = editor.KeyMappings[0].Packed;   // A1 原本在 0x5D
        editor.SetBoardSlot(0, 3);
        Assert.Equal(3, editor.BoardMappings[0].Slot);
        Assert.Equal(0, editor.BoardMappings[3].Slot);
        Assert.Equal(0, editor.KeyMappings[0].Board);            // 键跟着板走到 0x5A
        Assert.NotEqual(beforeA1, editor.KeyMappings[0].Packed);
        Assert.Equal([0, 1, 2, 3], editor.BoardMappings.Select(b => b.Slot).OrderBy(x => x).ToArray());
        Assert.Equal(34, editor.BoardMappings.Sum(b => b.Channels.Count(c => c.IsAssigned)));
        editor.RestoreDefaultMapping();
        Assert.Equal(KeyMapping.Default, editor.CurrentMapping());
    }

    [Fact]
    public void FillBoardsLaysOutKeysInBoardOrder()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        editor.FillBoards([8, 9, 9, 8]);
        Assert.False(editor.HasMappingConflict);
        Assert.Equal(34, editor.CurrentMapping().Distinct().Count());
        // 板 1 通道 0..7 = A1..A8，板 2 通道 0..8 = B1..B9。
        Assert.Equal("A1", editor.BoardMappings[0].Channels[0].KeyLabel);
        Assert.Equal("A8", editor.BoardMappings[0].Channels[7].KeyLabel);
        Assert.Equal("B1", editor.BoardMappings[1].Channels[0].KeyLabel);
        Assert.Equal("E8", editor.BoardMappings[3].Channels[7].KeyLabel);
        // 总数仍是 34 键全分配。
        Assert.Equal(34, editor.BoardMappings.Sum(b => b.Channels.Count(c => c.IsAssigned)));
    }

    [Fact]
    public void FillBoardsPlacesLeftoversWhenCountsAreShort()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        editor.FillBoards([4, 4, 4, 4]);   // 只指定 16 个通道，其余键顺次填到空闲通道
        Assert.Equal(34, editor.BoardMappings.Sum(b => b.Channels.Count(c => c.IsAssigned)));
        Assert.False(editor.HasMappingConflict);
        Assert.Equal("A1", editor.BoardMappings[0].Channels[0].KeyLabel);
        Assert.Equal("C1", editor.BoardMappings[0].Channels[4].KeyLabel);   // 剩余键按板序填进第一个空闲通道
    }

    [Fact]
    public void MappingHistoryTracksUndoRedoOnTheEditor()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        var history = new MappingHistory();
        history.Reset(new MappingState { KeyMap = editor.CurrentMapping(), Slots = editor.CurrentSlots() });
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);

        editor.AssignChannel(0, 11, 0);
        history.Push(new MappingState { KeyMap = editor.CurrentMapping(), Slots = editor.CurrentSlots() });
        Assert.True(history.CanUndo);

        var undo = history.Undo()!;
        editor.LoadMapping(undo.KeyMap, undo.Slots);
        Assert.Equal(KeyMapping.Default, editor.CurrentMapping());
        Assert.True(history.CanRedo);

        var redo = history.Redo()!;
        editor.LoadMapping(redo.KeyMap, redo.Slots);
        Assert.Equal(KeyMapping.Pack(0, 11), editor.KeyMappings[0].Packed);
    }

    [Fact]
    public void MappingSnapshotsRoundTripAndFindPrevious()
    {
        var path = Path.Combine(Path.GetTempPath(), "maimai-snap-" + Guid.NewGuid().ToString("N"), "snap.json");
        try
        {
            var first = new MappingState { KeyMap = (byte[])KeyMapping.Default.Clone(), Slots = [0, 1, 2, 3] };
            MappingSnapshotStore.Append(path, first, "test");
            var second = first.Clone();
            second.KeyMap[0] = KeyMapping.Pack(0, 11);
            MappingSnapshotStore.Append(path, second, "test");
            Assert.Equal(2, MappingSnapshotStore.Load(path).Count);
            MappingSnapshotStore.Append(path, second, "test");   // 重复不追加
            Assert.Equal(2, MappingSnapshotStore.Load(path).Count);

            var previous = MappingSnapshotStore.Previous(path, second);
            Assert.NotNull(previous);
            Assert.Equal(KeyMapping.Default, previous!.KeyMap);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LearnTargetsAreOrderedByKeyLabel()
    {
        var targets = MappingLearnSession.BuildKeyOrderTargets(
        [
            (16, "C1"),
            (0, "A1"),
            (8, "B1"),
        ]);
        Assert.Equal(["A1", "B1", "C1"], targets.Select(t => t.Label).ToArray());
        Assert.Equal([0, 8, 16], targets.Select(t => t.KeyIndex).ToArray());
    }

    /// <summary>界面下拉框走的是 SelectedIndex↔SelectedOptionIndex 双向绑定，这里直接驱动它。</summary>
    [Fact]
    public void ChannelSelectionFollowsTheUiBindingPath()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        var board0 = editor.BoardMappings[0];

        // 空通道：SelectedIndex = -1，焊盘显示短横。
        var empty = board0.Channels[10];
        Assert.False(empty.IsAssigned);
        Assert.Equal(-1, empty.SelectedOptionIndex);
        Assert.Equal("–", empty.PadLabel);

        // 下拉框选到 0 号键（A1）→ 通过 KeyRequested 走 AssignChannel。
        empty.SelectedOptionIndex = 0;
        Assert.Equal(KeyMapping.Pack(0, 10), editor.KeyMappings[0].Packed);
        Assert.Equal("A1", empty.KeyLabel);
        Assert.Equal("A1", empty.PadLabel);
        Assert.Equal(0, empty.SelectedOptionIndex);

        // 重新读到同一值不应再触发一次换位（幂等）。
        var before = editor.CurrentMapping();
        empty.SelectedOptionIndex = 0;
        Assert.Equal(before, editor.CurrentMapping());

        // 焊盘弹出列表的选项带指派命令，且显示的是键名而不是记录 ToString()。
        var option = empty.KeyOptions[1];
        Assert.Equal("A2", option.Label);
        Assert.Equal("A2", option.ToString());
        Assert.NotNull(option.SelectCommand);
        option.SelectCommand!.Execute(null);
        Assert.Equal("A2", empty.KeyLabel);
        Assert.False(editor.HasMappingConflict);
        Assert.Equal(34, editor.BoardMappings.Sum(b => b.Channels.Count(c => c.IsAssigned)));
    }
}
