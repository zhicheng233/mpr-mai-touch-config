using MaimaiTouchTool.Persistence;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class CompensationTests
{
    [Fact]
    public void DefaultIsOffAndAllKeyBitsRoundTrip()
    {
        var defaults = ConfigBlob.Defaults();
        Assert.Equal(0, defaults.CompensationMode);
        Assert.Equal(0UL, defaults.CompensationKeys);
        var config = defaults with { CompensationMode = 2, CompensationKeys = (1UL << 33) | 1 };
        var bytes = config.Encode();
        Assert.Equal(99, bytes.Length);
        Assert.Equal(3, bytes[0]); Assert.Equal(2, bytes[93]);
        Assert.Equal(1, bytes[94]); Assert.Equal(2, bytes[98]);
        var decoded = ConfigBlob.Decode(bytes);
        Assert.Equal(config.CompensationMode, decoded.CompensationMode);
        Assert.Equal(config.CompensationKeys, decoded.CompensationKeys);
    }

    [Fact]
    public void LegacyBlobMigratesWithMappingAndThresholdsIntact()
    {
        var config = ConfigBlob.Defaults();
        config.Boards[1] = config.Boards[1] with { BaseThreshold = 37 };
        (config.Mapping[0], config.Mapping[1]) = (config.Mapping[1], config.Mapping[0]);
        var legacy = config.Encode(legacy: true);
        Assert.Equal(93, legacy.Length); Assert.Equal(2, legacy[0]);
        var migrated = ConfigBlob.Decode(legacy);
        Assert.Equal(config.Encode(), migrated.Encode());
        Assert.Throws<NotSupportedException>(() => (config with { CompensationMode = 1 }).Encode(legacy: true));
    }

    [Fact]
    public void InvalidSettingsAndReservedMaskBitsAreRejected()
    {
        var config = ConfigBlob.Defaults();
        Assert.Throws<ArgumentException>(() => (config with { CompensationMode = 3 }).Encode());
        Assert.Throws<ArgumentException>(() => (config with { CompensationKeys = 1UL << 34 }).Encode());
        var bytes = config.Encode(); bytes[98] = 4;
        Assert.Throws<FormatException>(() => ConfigBlob.Decode(bytes));
        bytes = config.Encode(); bytes[93] = 3;
        Assert.Throws<FormatException>(() => ConfigBlob.Decode(bytes));
    }

    private static DebugFrame Extended(bool active) => new(100, 0,
        Enumerable.Repeat((ushort)679, 48).ToArray(), Enumerable.Repeat((ushort)720, 48).ToArray(),
        [7, 7, 7, 7], 0, 1UL << 33, Enumerable.Range(0, 34).Select(k => (byte)(k == 33 ? 14 : 0)).ToArray(), active ? 1UL << 33 : 0);

    [Fact]
    public void ExtendedTelemetrySeparatesHardwareOutputAndRamThreshold()
    {
        var original = Extended(true);
        var bytes = original.Encode();
        Assert.Equal(251, bytes.Length);
        Assert.Equal(2, bytes[211]); Assert.Equal(14, bytes[245]); Assert.Equal(2, bytes[250]);
        var frame = DebugFrame.Decode(bytes);
        Assert.Equal(0UL, frame.TouchBits); Assert.Equal(1UL << 33, frame.HardwareTouchBits);
        Assert.Equal(original.CompensationThresholds, frame.CompensationThresholds);
        Assert.Equal(1UL << 33, frame.CompensationActiveBits);
        var legacy = DebugFrame.Decode(bytes.AsSpan(0, 207));
        Assert.Null(legacy.HardwareTouchBits);
        Assert.Throws<FormatException>(() => DebugFrame.Decode(bytes.AsSpan(0, 250)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChartKeepsHardwareAndOutputSeparateAndOwnsCandidateHistory(bool active)
    {
        var model = new SignalChartModel { SelectedKeyIndex = 33 };
        var frame = Extended(active);
        model.Add(frame, ConfigBlob.Defaults());
        frame.CompensationThresholds![33] = 25;
        var point = Assert.Single(model.Points());
        Assert.True(point.HardwareTouched); Assert.False(point.Touched);
        Assert.Equal(56, point.CandidateThreshold);
        Assert.Equal(active, point.CompensationActive);
        Assert.Equal(active ? 56 : 20, point.TouchThreshold);
        Assert.Equal(active ? 52 : 16, point.ReleaseThreshold);
    }

    [Fact]
    public void EditorPreservesSettingsAndSelectionFollowsKeyNotChannel()
    {
        var cfg = ConfigBlob.Defaults() with { CompensationMode = 1, CompensationKeys = 1UL << 33 };
        var editor = new ConfigurationEditorModel(cfg);
        Assert.True(editor.Electrodes[33].CompensationSelected);
        editor.AssignChannel(0, 0, 33);
        var result = editor.ToConfig();
        Assert.Equal(1, result.CompensationMode); Assert.Equal(1UL << 33, result.CompensationKeys);
        editor.CompensationMode = 2;
        Assert.Equal(2, editor.ToConfig().CompensationMode);
    }

    [Fact]
    public void ExperimentalMasterSwitchDefaultsToObserveAndDisablesWithoutLosingKeys()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        Assert.False(editor.ExperimentalFeaturesEnabled);
        editor.CompensationBehaviorIndex = 1;
        Assert.Equal(0, editor.CompensationMode);
        editor.Electrodes[7].CompensationSelected = true;
        editor.ExperimentalFeaturesEnabled = true;
        Assert.Equal(1, editor.ToConfig().CompensationMode);
        Assert.Equal(0, editor.CompensationBehaviorIndex);
        editor.CompensationBehaviorIndex = 1;
        Assert.Equal(2, editor.ToConfig().CompensationMode);
        editor.ExperimentalFeaturesEnabled = true;
        Assert.Equal(2, editor.CompensationMode);
        editor.ExperimentalFeaturesEnabled = false;
        Assert.False(editor.ExperimentalFeaturesEnabled);
        Assert.Equal(0, editor.ToConfig().CompensationMode);
        Assert.Equal(1UL << 7, editor.ToConfig().CompensationKeys);
        editor.ExperimentalFeaturesEnabled = true;
        Assert.Equal(1, editor.CompensationMode);
        Assert.True(editor.Electrodes[7].CompensationSelected);
    }

    [Fact]
    public void DeviceModeRefreshUpdatesMasterSwitchAndBehaviorBindings()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        var notifications = new List<string?>();
        editor.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        editor.Update(ConfigBlob.Defaults() with { CompensationMode = 2 });
        Assert.True(editor.ExperimentalFeaturesEnabled);
        Assert.Equal(1, editor.CompensationBehaviorIndex);
        Assert.Contains(nameof(ConfigurationEditorModel.ExperimentalFeaturesEnabled), notifications);
        Assert.Contains(nameof(ConfigurationEditorModel.CompensationBehaviorIndex), notifications);
        notifications.Clear();
        editor.Update(ConfigBlob.Defaults());
        Assert.False(editor.ExperimentalFeaturesEnabled);
        Assert.Equal(0, editor.CompensationBehaviorIndex);
        Assert.Contains(nameof(ConfigurationEditorModel.ExperimentalFeaturesEnabled), notifications);
        Assert.Contains(nameof(ConfigurationEditorModel.CompensationMode), notifications);
    }

    [Fact]
    public async Task JsonRoundTripAndLegacyJsonDefaultToOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"compensation-{Guid.NewGuid():N}.json");
        try
        {
            var document = ConfigDocument.Create(new("COM3", null));
            document = document with { Config = document.Config with { CompensationMode = 1, CompensationKeys = 1UL << 33 } };
            await ConfigJsonStore.SaveAtomicAsync(path, document);
            Assert.Equal(document.Config.Encode(), (await ConfigJsonStore.LoadAsync(path)).Config.Encode());
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["config"]!.AsObject().Remove("compensationMode");
            json["config"]!.AsObject().Remove("compensationKeys");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var legacy = (await ConfigJsonStore.LoadAsync(path)).Config;
            Assert.Equal(0, legacy.CompensationMode); Assert.Equal(0UL, legacy.CompensationKeys);
        }
        finally { File.Delete(path); }
    }

    private sealed class LegacyClient : IRequestClient
    {
        public int SetLength;
        public Task<ProtocolFrame> SendRequestAsync(byte type, byte[] payload, CancellationToken cancellationToken = default)
        {
            if (type == (byte)ProtocolConstants.FrameType.SetConfig) SetLength = payload.Length;
            return Task.FromResult(new ProtocolFrame((byte)ProtocolConstants.FrameType.Ack, 1,
                new AckPayload(type, 0, 1, 0, type == (byte)ProtocolConstants.FrameType.GetConfig ? ConfigBlob.Defaults().Encode(legacy: true) : []).Encode()));
        }
    }

    [Fact]
    public async Task OldFirmwareStillAcceptsNormalApplyButRejectsExperimentalSettings()
    {
        var client = new LegacyClient();
        var workflow = new ConfigurationWorkflow(client) { SupportsCompensation = false };
        await workflow.ApplyAsync(ConfigBlob.Defaults());
        Assert.Equal(93, client.SetLength);
        await Assert.ThrowsAsync<NotSupportedException>(() => workflow.ApplyAsync(ConfigBlob.Defaults() with { CompensationMode = 2 }));
    }
}
