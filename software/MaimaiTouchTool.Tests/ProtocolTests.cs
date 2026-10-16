using MaimaiTouchTool.Persistence;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.Touch;
using MaimaiTouchTool.ViewModels;
using MaimaiTouchTool.Localization;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void CrcMatchesProtocolVector() => Assert.Equal(0x29B1, Crc16Ccitt.Compute("123456789"u8));

    [Fact]
    public void FrameRoundTripsAndUsesBoundedLength()
    {
        var frame = new ProtocolFrame((byte)ProtocolConstants.FrameType.SetConfig, 42, ConfigBlob.Defaults().Encode());
        var encoded = frame.Encode();
        Assert.Equal(69, encoded.Length);
        var decoded = ProtocolFrame.Decode(encoded);
        Assert.Equal(frame.Type, decoded.Type); Assert.Equal(frame.Sequence, decoded.Sequence); Assert.Equal(frame.Payload, decoded.Payload);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolFrame(1, 1, new byte[257]).Encode());
    }

    [Fact]
    public void ParserHandlesPartialInputAndRejectsCorruptFrames()
    {
        var encoded = new ProtocolFrame(1, 7, []).Encode(); var parser = new FrameParser();
        Assert.Empty(parser.Push(encoded.AsMemory(0, 3), DateTimeOffset.UtcNow));
        Assert.Single(parser.Push(encoded.AsMemory(3), DateTimeOffset.UtcNow));
        encoded[^1] ^= 1;
        Assert.Empty(parser.Push(encoded, DateTimeOffset.UtcNow));
        var oversized = new byte[] { 0xAA, 0x55, 0x10, 1, 0, 0, 0, 1, 0xAA, 0x55 };
        Assert.Empty(parser.Push(oversized, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ConfigBlobIsExactly59BytesAndRoundTrips()
    {
        var config = ConfigBlob.Defaults();
        Assert.Equal(59, config.Encode().Length);
        var decoded = ConfigBlob.Decode(config.Encode());
        Assert.Equal(config.Boards, decoded.Boards); Assert.Equal(config.ElectrodeOffsets, decoded.ElectrodeOffsets);
        var bad = config.Encode(); bad[0] = 2;
        Assert.Throws<FormatException>(() => ConfigBlob.Decode(bad));
    }

    [Fact]
    public void DebugPayloadUsesDocumentedOffsets()
    {
        var frame = new DebugFrame(0x11223344, 1UL << 33, Enumerable.Range(0, 48).Select(i => (ushort)i).ToArray(), Enumerable.Range(100, 48).Select(i => (ushort)i).ToArray(), [1, 2, 3, 4], 0x5566);
        var bytes = frame.Encode();
        Assert.Equal(207, bytes.Length);
        Assert.Equal(0x44, bytes[0]); Assert.Equal(0x02, bytes[8]); Assert.Equal(0x2F, bytes[103]); Assert.Equal(0x64, bytes[105]); Assert.Equal(0x04, bytes[204]);
        var decoded = DebugFrame.Decode(bytes); Assert.Equal(frame.TimestampMs, decoded.TimestampMs); Assert.Equal(frame.TouchBits, decoded.TouchBits); Assert.Equal(frame.Filtered, decoded.Filtered); Assert.Equal(frame.Baseline, decoded.Baseline); Assert.Equal(frame.ChipStatus, decoded.ChipStatus); Assert.Equal(frame.I2cErrors, decoded.I2cErrors);
    }

    [Fact]
    public void LegacyFrameAndCommandsKeepFixedWidths()
    {
        var keys = new bool[34]; keys[0] = true; keys[33] = true;
        Assert.Equal(new byte[] { 0x28, 1, 0, 0, 0, 0, 0, 8, 0x29 }, LegacyCodec.EncodeGameFrame(keys));
        Assert.True(LegacyCodec.IsCommand("{xxOx}"u8)); Assert.False(LegacyCodec.IsCommand("{xxO}"u8));
    }

    [Fact]
    public void FixedTouchManifestHasExpectedOrderAndExplicitPaths()
    {
        TouchManifest.Validate();
        Assert.Equal(34, TouchManifest.Assets.Count);
        Assert.Equal(new[] { "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "C1", "C2", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" }, TouchManifest.Assets.Select(x => x.Label));
          Assert.All(TouchManifest.Assets, asset =>
          {
              Assert.Equal($"avares://MaimaiTouchTool/Assets/Touch/{asset.Label}.svg", asset.BasePath);
              Assert.Equal($"avares://MaimaiTouchTool/Assets/Touch/{asset.Label}_touch.svg", asset.PressedPath);
          });
         Assert.Equal(34, TouchManifest.Assets.Select(x => x.BasePath).Distinct().Count());
         Assert.Equal(34, TouchManifest.Assets.Select(x => x.PressedPath).Distinct().Count());
    }

    [Fact]
    public void TouchBoardLayoutUsesAuthoritativeRingCenters()
    {
        var expected = new Dictionary<string, (double Radius, double Angle)>(StringComparer.Ordinal)
        {
            ["A1"] = (230, 0), ["A2"] = (230, 45), ["A3"] = (230, 90), ["A4"] = (230, 135), ["A5"] = (230, 180), ["A6"] = (230, 225), ["A7"] = (230, 270), ["A8"] = (230, 315),
            ["B1"] = (100, 22.5), ["B2"] = (100, 67.5), ["B3"] = (100, 112.5), ["B4"] = (100, 157.5), ["B5"] = (100, 202.5), ["B6"] = (100, 247.5), ["B7"] = (100, 292.5), ["B8"] = (100, 337.5),
            ["D1"] = (190, 22.5), ["D2"] = (190, 67.5), ["D3"] = (190, 112.5), ["D4"] = (190, 157.5), ["D5"] = (190, 202.5), ["D6"] = (190, 247.5), ["D7"] = (190, 292.5), ["D8"] = (190, 337.5),
            ["E1"] = (130, 0), ["E2"] = (130, 45), ["E3"] = (130, 90), ["E4"] = (130, 135), ["E5"] = (130, 180), ["E6"] = (130, 225), ["E7"] = (130, 270), ["E8"] = (130, 315)
        };

        foreach (var position in TouchManifest.Layout)
        {
            var centerX = position.X + position.Width / 2;
            var centerY = position.Y + position.Height / 2;
            if (position.Label is "C1" or "C2")
            {
                var expectedY = position.Label == "C1" ? 366 : 546;
                Assert.Equal(480, centerX, 6);
                Assert.Equal(expectedY, centerY, 6);
                continue;
            }

            var (radius, angle) = expected[position.Label];
            var radians = angle * Math.PI / 180;
            Assert.Equal(480 + radius * Math.Sin(radians), centerX, 6);
            Assert.Equal(456 - radius * Math.Cos(radians), centerY, 6);
        }
    }

    [Fact]
    public void TouchBoardLayoutUsesAuthoritativeNaturalSvgDimensions()
    {
        TouchManifest.Validate();
        Assert.Equal(TouchManifest.Assets.Select(asset => asset.Label), TouchManifest.Layout.Select(position => position.Label));
        var expectedSizes = new Dictionary<string, (double Width, double Height)>(StringComparer.Ordinal)
        {
            ["A1"] = (480.4, 456.1), ["A2"] = (456.1, 480.4), ["A3"] = (456.1, 480.4), ["A4"] = (480.4, 456.1), ["A5"] = (480.4, 456.1), ["A6"] = (456.1, 480.4), ["A7"] = (456.1, 480.4), ["A8"] = (480.4, 456.1),
            ["B1"] = (266.5, 252.9), ["B2"] = (252.9, 266.5), ["B3"] = (252.9, 266.5), ["B4"] = (266.5, 252.9), ["B5"] = (266.5, 252.9), ["B6"] = (252.9, 266.5), ["B7"] = (252.9, 266.5), ["B8"] = (266.5, 252.9),
            ["C1"] = (170.2, 373.4), ["C2"] = (170.2, 373.4),
            ["D1"] = (241.1, 324.2), ["D2"] = (364.1, 362), ["D3"] = (324.2, 241.1), ["D4"] = (399.7, 399.7), ["D5"] = (241.1, 324.2), ["D6"] = (364.1, 362), ["D7"] = (324.2, 241.1), ["D8"] = (399.7, 399.7),
            ["E1"] = (257.5, 257.5), ["E2"] = (182.1, 182.1), ["E3"] = (257.5, 257.5), ["E4"] = (182.1, 182.1), ["E5"] = (257.5, 257.5), ["E6"] = (182.1, 182.1), ["E7"] = (257.5, 257.5), ["E8"] = (182.1, 182.1)
        };

        Assert.All(TouchManifest.Layout, position =>
        {
            var expected = expectedSizes[position.Label];
            Assert.Equal(expected.Width, position.Width, 6);
            Assert.Equal(expected.Height, position.Height, 6);
            Assert.InRange(position.X + position.Width / 2, 0, TouchManifest.BoardWidth);
            Assert.InRange(position.Y + position.Height / 2, 0, TouchManifest.BoardHeight);
        });

        var positions = TouchManifest.Layout.ToArray();
        Assert.Equal(34, positions.Select(position => (position.X, position.Y)).Distinct().Count());
    }

    [Fact]
    public void TouchTestFactoryCreatesAllRenderableKeysAndSwitchesPressedAsset()
    {
        var keys = TouchTestModelFactory.Create();

        Assert.Equal(34, keys.Count);
        Assert.All(keys, key =>
        {
            Assert.Equal(key.Label, key.Position.Label);
            Assert.Equal(key.Asset.BasePath, key.ImagePath);
            Assert.True(key.Position.Width > 0 && key.Position.Height > 0);
            Assert.StartsWith("avares://MaimaiTouchTool/Assets/Touch/", key.ImagePath);
        });

        var first = keys[0];
        first.IsPressed = true;
        Assert.Equal(first.Asset.PressedPath, first.ImagePath);
        first.IsPressed = false;
        Assert.Equal(first.Asset.BasePath, first.ImagePath);
    }

    [Fact]
    public void CalibrationPayloadUsesDocumentedThreeByteShapeAndBounds()
    {
        var payload = new CalibratePayload(1, 60000).Encode();
        Assert.Equal(new byte[] { 1, 0x60, 0xEA }, payload);
        Assert.Equal(new CalibratePayload(1, 60000), CalibratePayload.Decode(payload));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalibratePayload(2, 0).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalibratePayload(0, 60001).Encode());
        Assert.Throws<FormatException>(() => CalibratePayload.Decode([0, 0]));
    }

    [Fact]
    public void CalibrationDoneDecodesSuccessAndFailureResults()
    {
        var success = new CalDonePayload(CalibrationStatus.Success, CalibrationReason.None, Enumerable.Range(0, 48).Select(i => (ushort)i).ToArray(), Enumerable.Range(100, 48).Select(i => (ushort)i).ToArray(), []);
        var encoded = success.Encode(); var decoded = CalDonePayload.Decode(encoded); Assert.Equal(196, encoded.Length); Assert.Equal(success.Status, decoded.Status); Assert.Equal(success.Reason, decoded.Reason); Assert.Equal(success.Baseline, decoded.Baseline); Assert.Equal(success.TouchThreshold, decoded.TouchThreshold);
        var failed = new CalDonePayload(CalibrationStatus.Failed, CalibrationReason.TouchDetected, [], [], []);
        Assert.Equal(4, failed.Encode().Length); Assert.Equal(failed, CalDonePayload.Decode(failed.Encode()));
        Assert.Throws<FormatException>(() => CalDonePayload.Decode([0, 0, 0, 0]));

        // 扩展失败帧：status,reason,reserved:u16,count:u8,count×{channel,magnitude}。
        var withDiagnostics = new CalDonePayload(CalibrationStatus.Failed, CalibrationReason.TouchDetected, [], [],
            [new CalibrationDiagnostic(3, 42), new CalibrationDiagnostic(46, 200)]);
        var diagEncoded = withDiagnostics.Encode();
        Assert.Equal(5 + 2 * 2, diagEncoded.Length);
        var diagDecoded = CalDonePayload.Decode(diagEncoded);
        Assert.Equal(withDiagnostics.Status, diagDecoded.Status);
        Assert.Equal(withDiagnostics.Reason, diagDecoded.Reason);
        Assert.Equal(2, diagDecoded.Diagnostics.Length);
        Assert.Equal((byte)3, diagDecoded.Diagnostics[0].Channel);
        Assert.Equal((byte)200, diagDecoded.Diagnostics[1].Magnitude);
        // 长度与 count 不一致必须被拒绝。
        Assert.Throws<FormatException>(() => CalDonePayload.Decode([1, 2, 0, 0, 5, 1, 2]));
    }

    [Fact]
    public void DebugTouchBitmapDecodesBoundaryAndMasksInvalidUpperBits()
    {
        var frame = new DebugFrame(0, (1UL << 33) | 1, new ushort[48], new ushort[48], new byte[4], 0);
        var decoded = DebugFrame.Decode(frame.Encode());
        Assert.Equal(0x200000001UL, decoded.TouchBits);
        Assert.Equal(0UL, DebugFrame.Decode(frame.Encode().Select((value, index) => index == 8 ? (byte)(value | 0xFC) : value).ToArray()).TouchBits & ~0x3FFFFFFFFUL);
    }

    [Fact]
    public void StreamStartResultRequiresBothMeasuredRates()
    {
        var result = StreamStartResult.Decode([100, 0, 96, 0]);
        Assert.Equal((ushort)100, result.AcceptedRateHz);
        Assert.Equal((ushort)96, result.ActualRateHz);
        Assert.Throws<FormatException>(() => StreamStartResult.Decode([100, 0, 0]));
    }

    [Fact]
    public void TelemetryProjectsPhysicalChannelsAndTouchKeys()
    {
        var telemetry = new TelemetrySessionModel();
        var frame = new DebugFrame(1000, 1UL << 27, Enumerable.Range(0, 48).Select(i => (ushort)(500 + i)).ToArray(), Enumerable.Range(0, 48).Select(i => (ushort)(700 + i)).ToArray(), [7, 7, 7, 7], 0);
        telemetry.Begin(100);
        telemetry.AddFrame(frame, 0, DateTimeOffset.UnixEpoch);
        Assert.Equal(48, telemetry.Channels.Count);
        Assert.Equal("A7", telemetry.Channels[4].KeyLabel);
        Assert.Equal("CH1:04", telemetry.Channels[4].ChannelLabel);
        Assert.True(telemetry.Channels[4].IsTouched);
        Assert.Equal((ushort)500, telemetry.Channels[0].Filtered);
        Assert.Equal((ushort)700, telemetry.Channels[0].Baseline);
    }

    [Fact]
    public void TelemetryMeasuresRateCountsSequenceGapsAndDoesNotHideErrors()
    {
        var telemetry = new TelemetrySessionModel();
        var frame = new DebugFrame(1000, 0, new ushort[48], new ushort[48], [7, 7, 7, 7], 0);
        telemetry.Begin(100);
        telemetry.AddFrame(frame, 1, DateTimeOffset.UnixEpoch);
        telemetry.AddFrame(frame with { TimestampMs = 1010 }, 3, DateTimeOffset.UnixEpoch.AddMilliseconds(10));
        Assert.Equal(1, telemetry.SequenceGaps);
        Assert.Equal(100, telemetry.ActualRateHz, 5);
        Assert.Equal(TelemetryHealth.Healthy, telemetry.Health);

        telemetry.MarkMalformed("bad payload");
        Assert.Equal(TelemetryHealth.Error, telemetry.Health);
        telemetry.MarkDisconnected();
        Assert.Equal(TelemetryHealth.Disconnected, telemetry.Health);
        Assert.Contains(telemetry.Log, entry => entry.Level == "ERROR");
    }

    [Fact]
    public void TelemetryLogIsBounded()
    {
        var telemetry = new TelemetrySessionModel();
        telemetry.Begin(100);
        for (var i = 0; i < TelemetrySessionModel.MaxLogEntries + 20; i++) telemetry.MarkMalformed($"error {i}");
        Assert.Equal(TelemetrySessionModel.MaxLogEntries, telemetry.Log.Count);
        Assert.Contains(telemetry.Log, entry => entry.Message == "error 119");
        Assert.DoesNotContain(telemetry.Log, entry => entry.Message == "error 0");
    }

    [Fact]
    public async Task JsonRoundTripAndConflictAreExplicit()
    {
        var path = Path.Combine(Path.GetTempPath(), "maimai-tool-" + Guid.NewGuid().ToString("N"), "config.json");
        var document = ConfigDocument.Create(new DeviceIdentity("COM7", "abc"));
        await ConfigJsonStore.SaveAtomicAsync(path, document);
        var loaded = await ConfigJsonStore.LoadAsync(path);
        Assert.Equal(document.Device, loaded.Device); Assert.Equal(document.Status, loaded.Status); Assert.Equal(document.Config.Boards, loaded.Config.Boards); Assert.Equal(document.Config.ElectrodeOffsets, loaded.Config.ElectrodeOffsets);
        Assert.True(ConfigJsonStore.IsConflict(loaded, new DeviceIdentity("COM7", "abc"), new ConfigBlob(loaded.Config.Boards, loaded.Config.ElectrodeOffsets.Select((x, i) => (sbyte)(i == 0 ? x + 1 : x)).ToArray())));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public sealed class WorkflowTests
{
    [Fact]
    public async Task StageApplySaveIsOrdered()
    {
        var client = new RecordingClient();
        var workflow = new ConfigurationWorkflow(client);
        var applied = await workflow.ApplyAsync(ConfigBlob.Defaults());
        await workflow.SaveAsync();
        Assert.Equal(new byte[] { 0x11, 0x13, 0x10, 0x12 }, client.Types);
        Assert.Equal(59, client.PayloadLengths[0]);
        Assert.Equal(ConfigBlob.Defaults().Encode(), applied.Encode());
    }

    [Fact]
    public async Task ApplyRejectsMismatchedReadbackAndDoesNotClaimSuccess()
    {
        var client = new RecordingClient { Readback = new ConfigBlob(ConfigBlob.Defaults().Boards, ConfigBlob.Defaults().ElectrodeOffsets.Select((x, i) => (sbyte)(i == 0 ? x + 1 : x)).ToArray()) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ConfigurationWorkflow(client).ApplyAsync(ConfigBlob.Defaults()));
        Assert.Equal(new byte[] { 0x11, 0x13, 0x10 }, client.Types);
    }

    [Fact]
    public void EditorChangesExposeDirtyState()
    {
        var viewModel = new MaimaiTouchTool.ViewModels.MainViewModel();
        Assert.False(viewModel.IsDirty);
        viewModel.Editor.Boards[0].BaseThreshold = 21;
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void ConfigurationEditorGroupsElectrodesIntoFiveZonesAndExposesHardwareInfo()
    {
        var editor = new ConfigurationEditorModel(ConfigBlob.Defaults());
        Assert.Equal(4, editor.Boards.Count);
        Assert.Equal("Board 1 (0x5A)", editor.Boards[0].BoardWithAddress);
        Assert.Equal("Board 4 (0x5D)", editor.Boards[3].BoardWithAddress);

        Assert.Equal(5, editor.Zones.Count);
        var zoneKeys = editor.Zones.Select(z => z.Key).ToArray();
        Assert.Equal(["A", "B", "C", "D", "E"], zoneKeys);

        Assert.Equal(8, editor.Zones[0].Electrodes.Count); // A
        Assert.Equal(8, editor.Zones[1].Electrodes.Count); // B
        Assert.Equal(2, editor.Zones[2].Electrodes.Count); // C
        Assert.Equal(8, editor.Zones[3].Electrodes.Count); // D
        Assert.Equal(8, editor.Zones[4].Electrodes.Count); // E

        // Check hardware info for A1 and C1
        var a1 = editor.Zones[0].Electrodes[0];
        Assert.Equal("A1", a1.Label);
        Assert.Equal(3, a1.MprId);
        Assert.Equal(4, a1.PortId);
        Assert.Contains("Board 4 (0x5D)", a1.Tooltip);

        var c1 = editor.Zones[2].Electrodes[0];
        Assert.Equal("C1", c1.Label);
        Assert.Equal(2, c1.MprId);
        Assert.Equal(8, c1.PortId);
        Assert.Contains("Board 3 (0x5C)", c1.Tooltip);
    }

    [Fact]
    public async Task DisconnectResetsStateAndStatusMessage()
    {
        var vm = new MainViewModel();
        Assert.False(vm.IsConnected);
        Assert.False(vm.DisconnectCommand.CanExecute(null));

        await vm.RunDisconnectForTestAsync();
        Assert.False(vm.IsConnected);
        Assert.Equal(Localizer.Instance["Msg.DisconnectedManual"], vm.StatusMessage);
    }

    [Fact]
    public void GameModeTogglePropertiesReflectState()
    {
        var vm = new MainViewModel();
        Assert.False(vm.IsGameModeActive);
        Assert.False(vm.CanToggleGameMode); // disconnected -> false
    }

    private sealed class RecordingClient : IRequestClient
    {
        public List<byte> Types { get; } = []; public List<int> PayloadLengths { get; } = [];
        public ConfigBlob Readback { get; init; } = ConfigBlob.Defaults();
        public Task<ProtocolFrame> SendRequestAsync(byte type, byte[] payload, CancellationToken cancellationToken = default)
        {
            Types.Add(type); PayloadLengths.Add(payload.Length);
            var result = type == (byte)ProtocolConstants.FrameType.GetConfig ? Readback.Encode() : [];
            return Task.FromResult(new ProtocolFrame((byte)ProtocolConstants.FrameType.Ack, 1, new AckPayload(type, 0, 1, 0, result).Encode()));
        }
    }
}
