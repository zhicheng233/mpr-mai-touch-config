using MaimaiTouchTool.Firmware;
using MaimaiTouchTool.Localization;

namespace MaimaiTouchTool.ViewModels;

/// <summary>
/// 固件版本页：把「工具版本 / 设备上跑的固件 / 要刷入的固件 / URL 上的最新版本」四条信息
/// 摆在一起，并提供一键刷写。
///
/// 四种版本号来自完全不同的地方，混起来最容易误判：
/// <list type="bullet">
/// <item>工具版本 —— 本程序的程序集版本。</item>
/// <item>设备固件 —— 板子通过 VERSION 应答报出来的（连接时读到）。</item>
/// <item>待刷固件 —— 本地 <c>firmware.json</c>（由 <c>firmware/extra_script.py</c> 在编译后生成）。</item>
/// <item>最新版本 —— GitHub Releases 的 tag_name。</item>
/// </list>
/// 所以四个分开显示，不合并成"当前版本"一句话。
/// </summary>
public sealed class FirmwareViewModel : NotifyModel
{
    private FirmwareVersion deviceVersion = FirmwareVersion.Unknown;
    private string deviceProtocol = string.Empty;
    private bool hasKeyMapping;
    private FirmwareManifest? localManifest;
    private string localManifestPath = string.Empty;
    private FirmwareManifest? remoteManifest;
    private string remoteError = string.Empty;
    private bool isCheckingRemote;
    private bool isFlashing;
    private string flashLog = string.Empty;
    private string flashStatus = string.Empty;
    private bool flashFailed;
    private string flashPort = string.Empty;
    private readonly string? manifestPathOverride;

    /// <param name="manifestPath">
    /// 指定 firmware.json 的位置；null 表示按默认规则找（随程序分发的那份，或源码树里的 .pio 产物）。
    /// 测试用这个参数指向临时目录，避免依赖真实的构建产物。
    /// </param>
    public FirmwareViewModel(string? manifestPath = null)
    {
        manifestPathOverride = manifestPath;
        RefreshLocalManifest();
    }

    /// <summary>刷写成功后通知外层：串口已不在，需要重新连接。</summary>
    public event EventHandler<FlashOutcome>? Flashed;

    // ---- 四条版本信息 ----

    /// <summary>本工具的版本（程序集版本）。</summary>
    public string ToolVersion => ToolInfo.Version;

    /// <summary>设备当前在跑的固件版本；未连接时是未知。</summary>
    public FirmwareVersion DeviceVersion
    {
        get => deviceVersion;
        set
        {
            deviceVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeviceVersionText));
            OnPropertyChanged(nameof(DeviceSummary));
            OnPropertyChanged(nameof(UpToDateLabel));
            OnPropertyChanged(nameof(NeedsUpdate));
            OnPropertyChanged(nameof(ShowUpdateVerdict));
            OnPropertyChanged(nameof(ShowUpToDate));
        }
    }

    public string DeviceVersionText => deviceVersion.IsKnown ? deviceVersion.ToString() : Localizer.Instance["Fw.Unknown"];

    /// <summary>设备协议版本；1.0 就是没有可配置映射的那批旧固件。</summary>
    public string DeviceProtocol
    {
        get => deviceProtocol;
        set
        {
            deviceProtocol = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeviceSummary));
            OnPropertyChanged(nameof(HasKeyMappingCapability));
            OnPropertyChanged(nameof(ShowUpdateVerdict));
            OnPropertyChanged(nameof(ShowUpToDate));
        }
    }

    public bool HasKeyMappingCapability
    {
        get => hasKeyMapping;
        set
        {
            hasKeyMapping = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeviceSummary));
            OnPropertyChanged(nameof(NeedsUpdate));
        }
    }

    /// <summary>设备一行摘要：未连接 / 版本 + 协议 + 是否支持映射。</summary>
    public string DeviceSummary
    {
        get
        {
            if (!deviceVersion.IsKnown && string.IsNullOrEmpty(deviceProtocol))
                return Localizer.Instance["Fw.NotConnected"];
            var mapping = hasKeyMapping ? Localizer.Instance["Fw.MappingYes"] : Localizer.Instance["Fw.MappingNo"];
            return string.Format(Localizer.Instance["Fw.DeviceFormat"], DeviceVersionText, deviceProtocol, mapping);
        }
    }

    /// <summary>本地编译出来的固件（要刷入的那份）。</summary>
    public FirmwareManifest? LocalManifest
    {
        get => localManifest;
        private set
        {
            localManifest = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LocalVersionText));
            OnPropertyChanged(nameof(LocalSourceText));
            OnPropertyChanged(nameof(HasLocalBinary));
            OnPropertyChanged(nameof(NeedsUpdate));
            OnPropertyChanged(nameof(UpToDateLabel));
            OnPropertyChanged(nameof(ShowUpToDate));
        }
    }

    public string LocalVersionText => localManifest?.Version ?? Localizer.Instance["Fw.NoLocalBuild"];

    /// <summary>待刷固件来自哪个文件，方便确认刷的到底是哪一份。</summary>
    public string LocalSourceText => localManifest is null
        ? Localizer.Instance["Fw.NoLocalBuildHint"]
        : string.Format(Localizer.Instance["Fw.LocalSourceFormat"],
            LocalBinaryPath, localManifest.Protocol ?? "?", localManifest.Size?.ToString() ?? "?");

    public bool HasLocalBinary => localManifest is not null && File.Exists(LocalBinaryPath);

    /// <summary>要刷入的 firmware.bin 路径（与 firmware.json 同目录）。</summary>
    public string LocalBinaryPath => localManifestPath.Length > 0
        ? Path.Combine(Path.GetDirectoryName(localManifestPath)!, "firmware.bin")
        : string.Empty;

    /// <summary>URL 查到的最新版本。</summary>
    public FirmwareManifest? RemoteManifest
    {
        get => remoteManifest;
        private set
        {
            remoteManifest = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoteVersionText));
            OnPropertyChanged(nameof(RemoteSourceText));
        }
    }

    public string RemoteVersionText
    {
        get
        {
            if (isCheckingRemote) return Localizer.Instance["Fw.Checking"];
            if (remoteManifest is not null) return remoteManifest.Version;
            return string.IsNullOrEmpty(remoteError) ? Localizer.Instance["Fw.NotChecked"] : Localizer.Instance["Fw.CheckFailed"];
        }
    }

    public string RemoteSourceText
    {
        get
        {
            if (!string.IsNullOrEmpty(remoteError)) return remoteError;
            if (remoteManifest is not null) return remoteManifest.Source ?? FirmwareRelease.LatestReleaseApi;
            return FirmwareRelease.IsConfigured
                ? FirmwareRelease.LatestReleaseApi
                : Localizer.Instance["Fw.RepositoryNotConfigured"];
        }
    }

    public bool IsCheckingRemote
    {
        get => isCheckingRemote;
        private set { isCheckingRemote = value; OnPropertyChanged(); OnPropertyChanged(nameof(RemoteVersionText)); }
    }

    // ---- 刷写状态 ----

    public bool IsFlashing
    {
        get => isFlashing;
        private set
        {
            isFlashing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanFlash));
            OnPropertyChanged(nameof(FlashLabel));
        }
    }

    public bool CanFlash => !isFlashing && HasLocalBinary && flashPort.Length > 0;

    public string FlashLabel => isFlashing ? Localizer.Instance["Fw.Flashing"] : Localizer.Instance["Fw.Flash"];

    public string FlashLog
    {
        get => flashLog;
        private set { flashLog = value; OnPropertyChanged(); }
    }

    public string FlashStatus
    {
        get => flashStatus;
        private set { flashStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFlashStatus)); }
    }

    public bool HasFlashStatus => !string.IsNullOrEmpty(flashStatus);

    public bool FlashFailed
    {
        get => flashFailed;
        private set { flashFailed = value; OnPropertyChanged(); }
    }

    /// <summary>最近一次成功刷写后设备所在的应用口（可能是新 COM 号）。</summary>
    public string LastFlashPort { get; private set; } = string.Empty;

    /// <summary>由外层在刷写被拒绝（例如没选串口）时补一条状态。</summary>
    public void ReportFlashResult(string message, bool failed)
    {
        FlashStatus = message;
        FlashFailed = failed;
    }

    /// <summary>
    /// 需要更新：设备版本未知（没连）时不算，免得一启动就红一片。
    /// 支持映射的能力位缺失也算需要更新 —— 那正是"固件太旧"的定义。
    /// </summary>
    public bool NeedsUpdate
    {
        get
        {
            if (!deviceVersion.IsKnown && string.IsNullOrEmpty(deviceProtocol)) return false;
            if (!hasKeyMapping && !string.IsNullOrEmpty(deviceProtocol)) return true;
            if (localManifest is null) return false;
            return deviceVersion.IsKnown && deviceVersion < localManifest.Parsed;
        }
    }

    /// <summary>没连板子时没有可比的对象，就别急着下"已是最新"的结论。</summary>
    public bool ShowUpdateVerdict => deviceVersion.IsKnown || !string.IsNullOrEmpty(deviceProtocol);

    /// <summary>"已是最新"这几个字只在真的比较过之后才显示。</summary>
    public bool ShowUpToDate => ShowUpdateVerdict && !NeedsUpdate;

    public string UpToDateLabel => NeedsUpdate
        ? Localizer.Instance["Fw.NeedsUpdate"]
        : Localizer.Instance["Fw.UpToDate"];

    // ---- 操作 ----

    /// <summary>重新读本地 build 产物（编译完固件后刷新用）。</summary>
    public void RefreshLocalManifest()
    {
        var path = manifestPathOverride ?? LocateLocalManifest();
        localManifestPath = path.Length > 0 && File.Exists(path) ? path : string.Empty;
        var manifest = FirmwareManifest.TryLoad(path);
        if (manifest is not null) manifest = manifest with { Source = path };
        LocalManifest = manifest;
        OnPropertyChanged(nameof(LocalBinaryPath));
    }

    /// <summary>
    /// 找本地 firmware.json。优先随程序分发的 Firmware/ 目录，
    /// 其次是开发时源码树的 .pio 构建目录 —— 否则每次改固件都要手工拷贝一份。
    /// </summary>
    private static string LocateLocalManifest()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Firmware", "firmware.json");
        if (File.Exists(shipped)) return shipped;

        // 从 bin/Debug/net8.0 往上找仓库根（含 firmware/platformio.ini 的那层）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "firmware", ".pio", "build", "seeed_xiao", "firmware.json");
            if (File.Exists(candidate)) return candidate;
        }
        return shipped;
    }

    /// <summary>告诉界面当前要刷哪个串口；断开连接时传空字符串。</summary>
    public void SetTargetPort(string port)
    {
        flashPort = port ?? string.Empty;
        OnPropertyChanged(nameof(CanFlash));
    }

    /// <summary>查 GitHub Releases 最新版本。查不到不是异常，只把原因显示出来。</summary>
    public async Task CheckRemoteAsync(CancellationToken cancellationToken)
    {
        IsCheckingRemote = true;
        remoteError = string.Empty;
        try
        {
            var latest = await FirmwareRelease.FetchLatestAsync(cancellationToken);
            RemoteManifest = latest.Manifest;
            remoteError = latest.Manifest is null ? latest.Error : string.Empty;
        }
        catch (OperationCanceledException)
        {
            remoteError = Localizer.Instance["Fw.Cancelled"];
        }
        finally
        {
            IsCheckingRemote = false;
            OnPropertyChanged(nameof(RemoteVersionText));
            OnPropertyChanged(nameof(RemoteSourceText));
        }
    }

    /// <summary>
    /// 刷写。调用方负责先断开串口 —— 刷写期间绝不能有别的程序占着这个口。
    /// 返回 false 表示失败，细节在 <see cref="FlashLog"/>。
    /// </summary>
    public async Task<bool> FlashAsync(string applicationPort, CancellationToken cancellationToken)
    {
        if (!HasLocalBinary)
        {
            FlashStatus = Localizer.Instance["Fw.NoLocalBuild"];
            FlashFailed = true;
            return false;
        }

        IsFlashing = true;
        FlashFailed = false;
        FlashLog = string.Empty;
        FlashStatus = Localizer.Instance["Fw.Flashing"];
        var flasher = new FirmwareFlasher
        {
            OnLog = line => FlashLog += line + Environment.NewLine,
        };

        try
        {
            var outcome = await flasher.FlashAsync(applicationPort, LocalBinaryPath, cancellationToken);
            FlashLog = outcome.Log;
            FlashStatus = outcome.Message;
            FlashFailed = !outcome.Success;
            if (outcome.Success)
            {
                LastFlashPort = outcome.ApplicationPort ?? string.Empty;
                Flashed?.Invoke(this, outcome);
            }
            return outcome.Success;
        }
        catch (OperationCanceledException)
        {
            FlashStatus = Localizer.Instance["Fw.Cancelled"];
            FlashFailed = true;
            return false;
        }
        catch (Exception ex)
        {
            FlashStatus = ex.Message;
            FlashFailed = true;
            return false;
        }
        finally
        {
            IsFlashing = false;
        }
    }

    /// <summary>语言切换时重抛所有依赖字典的属性。</summary>
    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(DeviceVersionText));
        OnPropertyChanged(nameof(DeviceSummary));
        OnPropertyChanged(nameof(LocalVersionText));
        OnPropertyChanged(nameof(LocalSourceText));
        OnPropertyChanged(nameof(RemoteVersionText));
        OnPropertyChanged(nameof(RemoteSourceText));
        OnPropertyChanged(nameof(FlashLabel));
        OnPropertyChanged(nameof(UpToDateLabel));
    }
}
