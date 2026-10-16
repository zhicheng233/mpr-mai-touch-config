using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class ElectrodeTrimTests
{
    private static ConfigurationEditorModel Editor(params sbyte[] offsets)
    {
        var blob = new ConfigBlob(
            [
                new BoardConfig(), new BoardConfig(), new BoardConfig(), new BoardConfig()
            ],
            offsets.Length == 34 ? offsets : Enumerable.Repeat((sbyte)0, 34).ToArray());
        return new ConfigurationEditorModel(blob);
    }

    [Fact]
    public void NudgeUsesEditorStep()
    {
        var editor = Editor();
        Assert.Equal(4, editor.Step); // 默认步进 4（芯片分辨率）
        editor.Electrodes[0].IncreaseCommand.Execute(null);
        Assert.Equal(4, editor.Electrodes[0].Offset);
        editor.Electrodes[0].DecreaseCommand.Execute(null);
        editor.Electrodes[0].DecreaseCommand.Execute(null);
        Assert.Equal(-4, editor.Electrodes[0].Offset);
    }

    [Fact]
    public void CycleStepWalksAvailableSteps()
    {
        var editor = Editor();
        editor.Step = 4;
        editor.CycleStep(1);
        Assert.Equal(8, editor.Step);
        editor.CycleStep(-1);
        editor.CycleStep(-1);
        Assert.Equal(2, editor.Step);
    }

    [Fact]
    public void StepChangeAffectsSubsequentNudges()
    {
        var editor = Editor();
        editor.Step = 16;
        editor.Electrodes[2].IncreaseCommand.Execute(null);
        Assert.Equal(16, editor.Electrodes[2].Offset);
    }

    [Fact]
    public void OffsetIsClampedToSignedByteRange()
    {
        var editor = Editor();
        for (var i = 0; i < 40; i++) editor.Electrodes[0].Nudge(16);
        Assert.Equal(127, editor.Electrodes[0].Offset);
        for (var i = 0; i < 40; i++) editor.Electrodes[0].Nudge(-16);
        Assert.Equal(-127, editor.Electrodes[0].Offset);
    }

    [Fact]
    public void SensitivityLabelFollowsDirection()
    {
        var editor = Editor();
        var electrode = editor.Electrodes[0];
        Assert.Equal(Localizer.Instance["Config.Sens.Neutral"], electrode.SensitivityLabel);
        electrode.Nudge(-8);
        Assert.Equal(Localizer.Instance["Config.Sens.High"], electrode.SensitivityLabel);
        electrode.Offset = -4;
        Assert.Equal(Localizer.Instance["Config.Sens.Slight"], electrode.SensitivityLabel);
        electrode.Offset = 4;
        Assert.Equal(Localizer.Instance["Config.Sens.SlightDull"], electrode.SensitivityLabel);
        electrode.Offset = 8;
        Assert.Equal(Localizer.Instance["Config.Sens.Dull"], electrode.SensitivityLabel);
    }

    [Fact]
    public void TintDiffersByDirectionAndIsNeutralAtZero()
    {
        var editor = Editor();
        var electrode = editor.Electrodes[0];
        var neutral = electrode.TintBrush;
        electrode.Offset = -16;
        var sensitive = electrode.TintBrush;
        electrode.Offset = 16;
        var dull = electrode.TintBrush;
        Assert.NotEqual(neutral, sensitive);
        Assert.NotEqual(neutral, dull);
        Assert.NotEqual(sensitive, dull);
    }

    [Fact]
    public void ZoneBatchAdjustsOnlyThatZone()
    {
        var editor = Editor();
        editor.Step = 8;
        var zoneA = editor.Zones.First(zone => zone.Key == "A"); // 前 8 个键
        zoneA.IncreaseAllCommand.Execute(null);
        Assert.All(zoneA.Electrodes, electrode => Assert.Equal(8, electrode.Offset));
        Assert.All(editor.Electrodes.Skip(8), electrode => Assert.Equal(0, electrode.Offset));
        zoneA.ResetAllCommand.Execute(null);
        Assert.All(zoneA.Electrodes, electrode => Assert.Equal(0, electrode.Offset));
    }

    [Fact]
    public void ResetAllElectrodesClearsEverything()
    {
        var editor = Editor();
        foreach (var electrode in editor.Electrodes) electrode.Nudge(12);
        editor.ResetAllElectrodes();
        Assert.All(editor.Electrodes, electrode => Assert.Equal(0, electrode.Offset));
    }

    [Fact]
    public void HardwareMappingMatchesCanonicalLayout()
    {
        var editor = Editor();
        // 官方映射：A1 = board3/port4，C1 = board2/port8，E8 = board0/port3。
        Assert.Equal((3, 4), (editor.Electrodes[0].MprId, editor.Electrodes[0].PortId));
        Assert.Equal((2, 8), (editor.Electrodes[16].MprId, editor.Electrodes[16].PortId));
        Assert.Equal((0, 3), (editor.Electrodes[33].MprId, editor.Electrodes[33].PortId));
    }
}
