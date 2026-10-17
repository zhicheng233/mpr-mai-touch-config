using MaimaiTouchTool.Firmware;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class FirmwareTests
{
    // ---- FirmwareVersion.Parse ----

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData(" 1.2.3 ", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("0.2.0-rc1", 0, 2, 0)]
    [InlineData("0.2.0+build7", 0, 2, 0)]
    public void ParsesVersions(string text, int major, int minor, int patch)
    {
        Assert.Equal(new FirmwareVersion(major, minor, patch), FirmwareVersion.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("1.x.3")]
    [InlineData("-1.2.3")]
    public void RejectsNonVersions(string? text)
    {
        var parsed = FirmwareVersion.Parse(text);
        Assert.False(parsed.IsKnown);
    }

    [Fact]
    public void ComparesNumericallyNotLexically()
    {
        // 字符串比较会把 "0.10.0" 判成小于 "0.9.0"
        Assert.True(FirmwareVersion.Parse("0.10.0") > FirmwareVersion.Parse("0.9.0"));
        Assert.True(FirmwareVersion.Parse("1.0.0") > FirmwareVersion.Parse("0.99.99"));
        Assert.True(FirmwareVersion.Parse("0.2.1") > FirmwareVersion.Parse("0.2.0"));
    }

    [Fact]
    public void UnknownSortsBeforeKnown()
    {
        Assert.True(FirmwareVersion.Unknown < FirmwareVersion.Parse("0.0.1"));
        Assert.False(FirmwareVersion.Unknown > FirmwareVersion.Parse("0.0.1"));
        Assert.Equal(0, FirmwareVersion.Unknown.CompareTo(FirmwareVersion.Unknown));
    }

    [Fact]
    public void FormatsWithoutUnknownMarker()
    {
        Assert.Equal("1.2.3", new FirmwareVersion(1, 2, 3).ToString());
    }

    // ---- FirmwareManifest ----

    [Fact]
    public void ReadsManifestProducedByTheBuildScript()
    {
        // 这段就是 firmware/extra_script.py 实际输出的形状
        const string json = """
        {
          "schema": 1,
          "version": "0.2.0",
          "versionCode": 512,
          "protocol": "1.1",
          "board": "seeed_xiao",
          "env": "seeed_xiao",
          "binary": "firmware.bin",
          "size": 29532
        }
        """;

        var manifest = FirmwareManifest.Parse(json);

        Assert.NotNull(manifest);
        Assert.Equal("0.2.0", manifest!.Version);
        Assert.Equal("1.1", manifest.Protocol);
        Assert.Equal(29532, manifest.Size);
        Assert.Equal(new FirmwareVersion(0, 2, 0), manifest.Parsed);
    }

    [Fact]
    public void ManifestWithoutVersionIsRejected()
    {
        Assert.Null(FirmwareManifest.Parse("""{ "protocol": "1.1" }"""));
    }

    [Fact]
    public void MalformedManifestIsRejectedNotThrown()
    {
        Assert.Null(FirmwareManifest.Parse("not json at all"));
        Assert.Null(FirmwareManifest.Parse("[]"));
    }

    [Fact]
    public void MissingManifestFileReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "firmware.json");
        Assert.False(File.Exists(path));
        Assert.Null(FirmwareManifest.TryLoad(path));
    }

    [Fact]
    public void RemoteManifestNeedsOnlyAVersion()
    {
        // 远端 release 只有 tag_name，没有 protocol/size；构造出来也要能用
        var remote = new FirmwareManifest("0.3.0", null, null, "firmware.bin", null, "https://example/rel");
        Assert.Equal(new FirmwareVersion(0, 3, 0), remote.Parsed);
        Assert.Null(remote.Protocol);
    }

    // ---- 版本比较驱动的"需要更新" ----

    [Fact]
    public void NeedsUpdateWhenDeviceIsOlderThanTheLocalBuild()
    {
        using var build = new FakeBuild("0.2.0");
        var model = NewModel(build);

        model.DeviceProtocol = "1.1";
        model.HasKeyMappingCapability = true;
        model.DeviceVersion = new FirmwareVersion(0, 1, 0);

        Assert.Equal(Localizer.Instance["Fw.NeedsUpdate"], model.UpToDateLabel);
        Assert.True(model.NeedsUpdate);
    }

    [Fact]
    public void NoUpdateWhenVersionsMatch()
    {
        using var build = new FakeBuild("0.2.0");
        var model = NewModel(build);

        model.DeviceProtocol = "1.1";
        model.HasKeyMappingCapability = true;
        model.DeviceVersion = new FirmwareVersion(0, 2, 0);

        Assert.False(model.NeedsUpdate);
        Assert.Equal(Localizer.Instance["Fw.UpToDate"], model.UpToDateLabel);
    }

    [Fact]
    public void MissingKeyMappingCapabilityCountsAsOutdated()
    {
        // 协议 1.0 的旧固件：版本号可能和本地一样，但没有 mapping 能力位。
        // 这正是用户碰到的那条"固件早于可配置按键映射"报错的判据。
        using var build = new FakeBuild("0.2.0");
        var model = NewModel(build);

        model.DeviceProtocol = "1.0";
        model.HasKeyMappingCapability = false;
        model.DeviceVersion = new FirmwareVersion(0, 2, 0);

        Assert.True(model.NeedsUpdate);
    }

    [Fact]
    public void NotConnectedDoesNotClaimAnUpdateIsAvailable()
    {
        // 没连板子时不能红一片：版本未知 ≠ 需要更新
        using var build = new FakeBuild("0.2.0");
        var model = NewModel(build);

        Assert.False(model.NeedsUpdate);
        Assert.Equal(Localizer.Instance["Fw.NotConnected"], model.DeviceSummary);
    }

    [Fact]
    public void DeviceSummaryMentionsProtocolAndMappingSupport()
    {
        using var build = new FakeBuild("0.2.0");
        var model = NewModel(build);

        model.DeviceVersion = new FirmwareVersion(0, 2, 0);
        model.DeviceProtocol = "1.1";
        model.HasKeyMappingCapability = true;

        var summary = model.DeviceSummary;
        Assert.Contains("0.2.0", summary);
        Assert.Contains("1.1", summary);
        Assert.Contains(Localizer.Instance["Fw.MappingYes"], summary);
    }

    // ---- 本地清单发现 ----

    [Fact]
    public void ReadsTheLocalBuildManifestFromTheFakeBuildDirectory()
    {
        using var build = new FakeBuild("0.2.0");
        var model = NewModel(build);

        Assert.Equal("0.2.0", model.LocalVersionText);
        Assert.True(model.HasLocalBinary);
        Assert.Equal(Path.Combine(build.Directory, "firmware.bin"), model.LocalBinaryPath);
    }

    [Fact]
    public void MissingBuildDirectoryIsReportedNotCrashed()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "firmware.json");
        var model = new FirmwareViewModel(missing);

        Assert.Null(model.LocalManifest);
        Assert.Equal(Localizer.Instance["Fw.NoLocalBuild"], model.LocalVersionText);
        Assert.Equal(Localizer.Instance["Fw.NoLocalBuildHint"], model.LocalSourceText);
        Assert.False(model.HasLocalBinary);
        Assert.False(model.CanFlash);
    }

    // ---- 刷写前置条件 ----

    [Fact]
    public void CannotFlashWithoutAConfiguredPort()
    {
        using var build = new FakeBuild("0.2.0");
        var model = NewModel(build);

        Assert.True(model.HasLocalBinary);
        Assert.False(model.CanFlash);
        model.SetTargetPort("COM3");
        Assert.True(model.CanFlash);
    }

    [Fact]
    public void ReportFlashResultSurfacesAMessage()
    {
        using var build = new FakeBuild("0.2.0");
        var model = NewModel(build);

        Assert.False(model.HasFlashStatus);
        model.ReportFlashResult("select a port", failed: true);
        Assert.True(model.HasFlashStatus);
        Assert.True(model.FlashFailed);
        Assert.Equal("select a port", model.FlashStatus);
    }

    [Fact]
    public void FlashingWithNoBinaryFailsWithoutTouchingHardware()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "firmware.json");
        var model = new FirmwareViewModel(missing);

        var ok = model.FlashAsync("COM3", CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(ok);
        Assert.True(model.FlashFailed);
        Assert.False(model.IsFlashing);
    }

    // ---- 本地化 ----

    [Fact]
    public void FirmwareStringsExistInBothLanguages()
    {
        var keys = new[]
        {
            "Tab.Firmware", "Fw.Title", "Fw.Hint", "Fw.ToolVersion", "Fw.DeviceVersion",
            "Fw.LocalVersion", "Fw.RemoteVersion", "Fw.Unknown", "Fw.NotConnected",
            "Fw.DeviceFormat", "Fw.MappingYes", "Fw.MappingNo", "Fw.NoLocalBuild",
            "Fw.NoLocalBuildHint", "Fw.LocalSourceFormat", "Fw.Checking", "Fw.NotChecked",
            "Fw.CheckFailed", "Fw.RepositoryNotConfigured", "Fw.Cancelled", "Fw.NeedsUpdate",
            "Fw.UpToDate", "Fw.Flash", "Fw.Flashing", "Fw.FlashDone", "Fw.FlashFailed",
            "Fw.SelectPortFirst", "Fw.Refresh", "Fw.RefreshTip", "Fw.Log", "Fw.FlashHint",
        };

        foreach (var language in new[] { "en", "zh-Hans" })
        {
            Localizer.Instance.SetLanguage(language);
            try
            {
                foreach (var key in keys)
                {
                    var value = Localizer.Instance[key];
                    Assert.False(string.IsNullOrWhiteSpace(value), $"{key} missing in {language}");
                    Assert.NotEqual(key, value);
                }
            }
            finally
            {
                Localizer.Instance.SetLanguage("en");
            }
        }
    }

    // ---- helpers ----

    private static FirmwareViewModel NewModel(FakeBuild build) => new(build.ManifestPath);

    /// <summary>
    /// 造一份假的 build 产物目录（firmware.json + firmware.bin），
    /// 让测试不依赖真实的 PlatformIO 输出。
    /// </summary>
    private sealed class FakeBuild : IDisposable
    {
        public string Directory { get; }
        public string ManifestPath => Path.Combine(Directory, "firmware.json");

        public FakeBuild(string version)
        {
            Directory = Path.Combine(Path.GetTempPath(), "maimai-fw-" + Guid.NewGuid().ToString("n"));
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(ManifestPath, $$"""
            {
              "schema": 1,
              "version": "{{version}}",
              "protocol": "1.1",
              "board": "seeed_xiao",
              "env": "seeed_xiao",
              "binary": "firmware.bin",
              "size": 4
            }
            """);
            File.WriteAllBytes(Path.Combine(Directory, "firmware.bin"), [0xAA, 0x55, 0x11, 0x00]);
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
        }
    }
}
