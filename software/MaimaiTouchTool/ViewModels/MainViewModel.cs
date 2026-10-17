using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text.Json;
using Avalonia.Threading;
using System.Windows.Input;
using MaimaiTouchTool.Firmware;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Mcp;
using MaimaiTouchTool.Persistence;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.Touch;

namespace MaimaiTouchTool.ViewModels;

public enum ConnectionStatus { Disconnected, Connecting, Connected, Error, Unsupported }

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) { if (!CanExecute(parameter)) return; running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty); try { await execute(); } finally { running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); } }
}

public sealed class MainViewModel : NotifyModel, IAsyncDisposable
{
    private readonly Func<string, (SerialTransport Transport, IRequestClient Client)> transportFactory;
    private SerialTransport? transport;
    private IRequestClient? client;
    private ConfigurationWorkflow? workflow;
    private ConfigBlob applied = ConfigBlob.Defaults();
    private ConfigBlob? persisted;
    private string selectedPort = string.Empty;
    private string statusMessage = Localizer.Instance["Msg.SelectPort"];
    private ConnectionStatus status = ConnectionStatus.Disconnected;
    private bool isDirty;
    private bool hasConflict;
    private bool isBusy;
    private CancellationTokenSource? calibrationCancellation;
    private CancellationTokenSource? streamCancellation;
    private CancellationTokenSource? gameCancellation;
    private long gameFrameCount;
    private double gameFrameRate;
    private DateTimeOffset gameRateWindowStart;
    private CancellationTokenSource? connectCancellation;
    private string calibrationLabel = Localizer.Instance["Cal.Ready"];
    private CancellationTokenSource? toastCancellation;
    private bool isToastVisible;
    private double toastOpacity;
    private double calibrationProgress;
    private uint calibrationDeadlineMs;
    private string calibrationSummary = string.Empty;
    private GuidedCalibrationSession? guidedSession;
    private CancellationTokenSource? guidedCancellation;
    private GuidedCalibrationResult? guidedResult;
    private int guidedTriggerPercent = 50;
    private string guidedStepLabel = string.Empty;
    private double guidedProgress;
    private double guidedKeyProgress;
    private double guidedLiveDepth;
    private uint guidedLastFrameMs;
    private uint guidedPreviousRawMs;
    private bool guidedFirstFrame = true;
    private MappingLearnSession? mappingSession;
    private CancellationTokenSource? mappingCancellation;
    private string mappingStepLabel = string.Empty;
    private double mappingProgress;
    private double mappingLiveDepth;
    private uint mappingLastFrameMs;
    private uint mappingPreviousRawMs;
    private bool mappingFirstFrame = true;
    private string? mappingHeadline;
    private readonly MappingHistory mappingHistory = new();
    private bool replayingHistory;
    public TelemetrySessionModel Telemetry { get; } = new();
    public McpGuideModel McpGuide { get; } = new();
    /// <summary>固件版本页：工具/设备/待刷/远端四条版本，以及一键刷写。</summary>
    public FirmwareViewModel FirmwareInfo { get; } = new();

    // GUI 内嵌的 MCP 端点：开着的时候模型直接复用界面这条串口，不必关程序。
    private readonly McpHttpHost mcpHost = new();
    private Task<McpServer>? mcpServerFactory;
    private bool mcpServing;
    private string mcpEndpointError = string.Empty;

    /// <summary>内嵌 MCP 服务器是否在监听。由界面的开关控制，默认关。</summary>
    public bool IsMcpServing
    {
        get => mcpServing;
        private set { if (mcpServing == value) return; mcpServing = value; OnPropertyChanged(); OnPropertyChanged(nameof(McpServeLabel)); OnPropertyChanged(nameof(McpToggleLabel)); RefreshCommands(); }
    }

    public string McpServeLabel => IsMcpServing
        ? string.Format(Localizer.Instance["Mcp.ServingAt"], McpGuide.Endpoint)
        : Localizer.Instance["Mcp.NotServing"];

    /// <summary>按钮上的字：点它会启动还是停止。</summary>
    public string McpToggleLabel => IsMcpServing
        ? Localizer.Instance["Mcp.StopServe"]
        : Localizer.Instance["Mcp.Serve"];

    /// <summary>启动失败的原因（端口全被占 / 已在使用等），成功时为空。</summary>
    public string McpEndpointError
    {
        get => mcpEndpointError;
        private set { mcpEndpointError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMcpEndpointError)); }
    }

    public bool HasMcpEndpointError => !string.IsNullOrEmpty(mcpEndpointError);

    public ICommand ToggleMcpServeCommand { get; }
    /// <summary>重读本地固件清单 + 查 GitHub 上的最新版本。</summary>
    public ICommand RefreshFirmwareCommand { get; }
    /// <summary>把本地 firmware.bin 刷进板子（必要时先断开串口）。</summary>
    public ICommand FlashFirmwareCommand { get; }

    public MainViewModel(Func<string, (SerialTransport Transport, IRequestClient Client)>? transportFactory = null)
    {
        this.transportFactory = transportFactory ?? (port => { var serial = new SerialTransport(new SerialByteStream(port)); return (serial, serial); });
        RefreshPortsCommand = new AsyncCommand(() => { RefreshPorts(); return Task.CompletedTask; });
        // 允许从 未连接/错误/不支持 任一非连接态重试；连接中或已连接则禁用（避免并发）。
        ConnectCommand = new AsyncCommand(ConnectAsync, () => !IsBusy && !IsConnected && !string.IsNullOrWhiteSpace(SelectedPort));
        DisconnectCommand = new AsyncCommand(DisconnectAsync, () => IsConnected && !IsBusy);
        CancelConnectCommand = new AsyncCommand(CancelConnectAsync, () => connectCancellation is not null);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !IsBusy && IsConnected && IsDirty && !HasConflict && !Editor.HasMappingConflict);
        SaveCommand = new AsyncCommand(SaveAsync, () => !IsBusy && IsConnected && !IsDirty && !HasConflict && !Editor.HasMappingConflict);        ReadbackCommand = new AsyncCommand(ReadbackAsync, () => !IsBusy && IsConnected);
        RestoreDefaultsCommand = new AsyncCommand(RestoreDefaults, () => !IsBusy && IsConnected);
        UseDeviceCommand = new AsyncCommand(() => { HasConflict = false; StatusMessage = Localizer.Instance["Msg.DeviceSelected"]; return Task.CompletedTask; });
        UseLocalCommand = new AsyncCommand(UseLocalAsync, () => HasConflict && IsConnected);
        CalibrateCommand = new AsyncCommand(CalibrateAsync, () => IsConnected && !IsBusy && calibrationCancellation is null);
        CancelCalibrationCommand = new AsyncCommand(CancelCalibrationAsync, () => calibrationCancellation is not null);
        StartStreamCommand = new AsyncCommand(StartStreamAsync, () => IsConnected && streamCancellation is null && !IsBusy && !IsGuidedRunning && !IsMappingRunning);
        StopStreamCommand = new AsyncCommand(StopStreamAsync, () => streamCancellation is not null);
        GameModeCommand = new AsyncCommand(StartGameModeAsync, () => IsConnected && gameCancellation is null && !IsBusy && !IsStreaming && !IsGuidedRunning && !IsMappingRunning);
        StopGameModeCommand = new AsyncCommand(StopGameModeAsync, () => gameCancellation is not null);
        ResetAllElectrodesCommand = new RelayCommand(() => { Editor.ResetAllElectrodes(); MarkDirty(); });
        RestoreDefaultMappingCommand = new RelayCommand(() => { Editor.RestoreDefaultMapping(); MarkDirty(); });
        UndoMappingCommand = new RelayCommand(UndoMapping, () => mappingHistory.CanUndo);
        RedoMappingCommand = new RelayCommand(RedoMapping, () => mappingHistory.CanRedo);
        FillBoardsCommand = new RelayCommand(FillBoards);
        RollbackMappingCommand = new RelayCommand(RollbackMappingSnapshot);
        ToggleMcpServeCommand = new AsyncCommand(ToggleMcpServeAsync);
        RefreshFirmwareCommand = new AsyncCommand(RefreshFirmwareAsync);
        FlashFirmwareCommand = new AsyncCommand(FlashFirmwareAsync, () => FirmwareInfo.CanFlash);
        StepUpCommand = new RelayCommand(() => Editor.CycleStep(1));
        StepDownCommand = new RelayCommand(() => Editor.CycleStep(-1));
        StartGuidedCommand = new AsyncCommand(StartGuidedAsync, () => IsConnected && !IsBusy && guidedCancellation is null && !IsStreaming && !IsGameMode && !IsMappingRunning);
        SkipGuidedKeyCommand = new RelayCommand(SkipGuidedKey, () => guidedSession is not null);
        FinishGuidedKeyCommand = new RelayCommand(FinishGuidedKey, () => guidedSession?.CanFinishCurrentKeyEarly == true);
        CancelGuidedCommand = new AsyncCommand(CancelGuidedAsync, () => guidedCancellation is not null);
        ApplyGuidedResultCommand = new RelayCommand(ApplyGuidedResult, () => guidedResult is not null);
        DiscardGuidedResultCommand = new RelayCommand(DiscardGuidedResult, () => guidedResult is not null);
        StartMappingCommand = new AsyncCommand(StartMappingAsync, () => IsConnected && !IsBusy && mappingCancellation is null && guidedCancellation is null && !IsStreaming && !IsGameMode);
        SkipMappingKeyCommand = new RelayCommand(SkipMappingKey, () => mappingSession is not null);
        FinishMappingKeyCommand = new RelayCommand(FinishMappingKey, () => mappingSession?.CanFinishCurrentKeyEarly == true);
        CancelMappingCommand = new AsyncCommand(CancelMappingAsync, () => mappingCancellation is not null);
        TouchManifest.Validate();
        WireEditor();
        Editor.MappingChanged += (_, _) => OnMappingChanged();
        ResetMappingHistory();
        RefreshPorts();
        Telemetry.ApplyThresholds(applied);
        NumericInputNotifier.InvalidNumberEntered += OnInvalidNumericInput;
        Localizer.Instance.LanguageChanged += (_, _) => RaiseLocalized();
    }

    // 语言切换时重抛所有依赖语言的属性，让界面刷新
    private void RaiseLocalized()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(DirtyLabel));
        OnPropertyChanged(nameof(ConflictLabel));
        OnPropertyChanged(nameof(GameFrameRateLabel));
        OnPropertyChanged(nameof(CalibrationHeadline));
        OnPropertyChanged(nameof(GuidedLiveDepthLabel));
        OnPropertyChanged(nameof(MappingLiveDepthLabel));
        OnPropertyChanged(nameof(McpServeLabel));
        OnPropertyChanged(nameof(McpToggleLabel));
        FirmwareInfo.RaiseLocalized();
    }

    public ObservableCollection<string> Ports { get; } = [];
    public ConfigurationEditorModel Editor { get; } = new(ConfigBlob.Defaults());
    public ICommand RefreshPortsCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand CancelConnectCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ReadbackCommand { get; }
    public ICommand RestoreDefaultsCommand { get; }
    public ICommand UseDeviceCommand { get; }
    public ICommand UseLocalCommand { get; }
    public ICommand CalibrateCommand { get; }
    public ICommand CancelCalibrationCommand { get; }
    public ICommand StartStreamCommand { get; }
    public ICommand StopStreamCommand { get; }
    public ICommand GameModeCommand { get; }
    public ICommand StopGameModeCommand { get; }
    public ICommand ResetAllElectrodesCommand { get; }
    public ICommand RestoreDefaultMappingCommand { get; }
    public ICommand UndoMappingCommand { get; }
    public ICommand RedoMappingCommand { get; }
    public ICommand FillBoardsCommand { get; }
    public ICommand RollbackMappingCommand { get; }
    public ICommand StepUpCommand { get; }
    public ICommand StepDownCommand { get; }
    public ICommand StartGuidedCommand { get; }
    public ICommand SkipGuidedKeyCommand { get; }
    public ICommand FinishGuidedKeyCommand { get; }
    public ICommand CancelGuidedCommand { get; }
    public ICommand ApplyGuidedResultCommand { get; }
    public ICommand DiscardGuidedResultCommand { get; }
    public ICommand StartMappingCommand { get; }
    public ICommand SkipMappingKeyCommand { get; }
    public ICommand FinishMappingKeyCommand { get; }
    public ICommand CancelMappingCommand { get; }
    public ObservableCollection<TouchKeyModel> TouchKeys { get; } = TouchTestModelFactory.Create();
    public string SelectedPort { get => selectedPort; set { selectedPort = value; OnPropertyChanged(); FirmwareInfo.SetTargetPort(value); RefreshCommands(); } }
    public string StatusMessage { get => statusMessage; private set { statusMessage = value; OnPropertyChanged(); } }
    public ConnectionStatus Status { get => status; private set { status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(IsConnected)); RefreshCommands(); } }
    public string StatusLabel => Status switch { ConnectionStatus.Connected => Localizer.Instance["Status.Connected"], ConnectionStatus.Connecting => Localizer.Instance["Status.Connecting"], ConnectionStatus.Error => Localizer.Instance["Status.Error"], ConnectionStatus.Unsupported => Localizer.Instance["Status.Unsupported"], _ => Localizer.Instance["Status.Disconnected"] };
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsBusy { get => isBusy; private set { isBusy = value; OnPropertyChanged(); RefreshCommands(); } }
    public bool IsDirty { get => isDirty; private set { isDirty = value; OnPropertyChanged(); RefreshCommands(); } }
    public bool HasConflict { get => hasConflict; private set { hasConflict = value; OnPropertyChanged(); RefreshCommands(); } }
    public string DirtyLabel => IsDirty ? Localizer.Instance["Dirty.Staged"] : Localizer.Instance["Dirty.None"];
    public string ConflictLabel => HasConflict ? Localizer.Instance["Conflict.Label"] : "";
    public string CalibrationLabel { get => calibrationLabel; private set { calibrationLabel = value; OnPropertyChanged(); } }
    public double CalibrationProgress { get => calibrationProgress; private set { calibrationProgress = value; OnPropertyChanged(); } }
    public uint CalibrationDeadlineMs { get => calibrationDeadlineMs; private set { calibrationDeadlineMs = value; OnPropertyChanged(); } }
    public string CalibrationHeadline { get => calibrationSummary; private set { calibrationSummary = value; OnPropertyChanged(); } }
    public ObservableCollection<CalibrationBoardResult> CalibrationResults { get; } = [];
    public bool HasCalibrationResult => calibrationSummary.Length > 0 || CalibrationResults.Count > 0;
    // ---- 引导式强力调整 ----
    public ObservableCollection<GuidedBoardPreview> GuidedBoards { get; } = [];
    public ObservableCollection<GuidedKeyPreview> GuidedKeys { get; } = [];
    public int GuidedTriggerPercent
    {
        get => guidedTriggerPercent;
        set { guidedTriggerPercent = Math.Clamp(value, 20, 90); OnPropertyChanged(); OnPropertyChanged(nameof(GuidedTriggerRatio)); }
    }
    public double GuidedTriggerRatio => GuidedTriggerPercent / 100.0;
    public bool IsGuidedRunning => guidedCancellation is not null || guidedSession is not null;
    public bool ShowGuidedIntro => !IsGuidedRunning && guidedResult is null;
    public bool HasGuidedResult => guidedResult is not null;
    public string GuidedStepLabel { get => guidedStepLabel; private set { guidedStepLabel = value; OnPropertyChanged(); } }
    public double GuidedProgress { get => guidedProgress; private set { guidedProgress = value; OnPropertyChanged(); } }
    public double GuidedKeyProgress { get => guidedKeyProgress; private set { guidedKeyProgress = value; OnPropertyChanged(); } }
    public double GuidedLiveDepth { get => guidedLiveDepth; private set { guidedLiveDepth = value; OnPropertyChanged(); OnPropertyChanged(nameof(GuidedLiveDepthLabel)); } }
    public string GuidedLiveDepthLabel => string.Format(Localizer.Instance["Guided.LiveDepth"], guidedLiveDepth);
    public string? GuidedHeadline { get; private set; }

    // ---- 按键映射自动学习 ----
    public bool IsMappingRunning => mappingCancellation is not null || mappingSession is not null;
    public bool ShowMappingIntro => !IsMappingRunning;
    public bool HasMappingHeadline => !string.IsNullOrEmpty(mappingHeadline);
    public string? MappingHeadline { get => mappingHeadline; private set { mappingHeadline = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMappingHeadline)); } }
    public string MappingStepLabel { get => mappingStepLabel; private set { mappingStepLabel = value; OnPropertyChanged(); } }
    /// <summary>映射撤销/重做是否可用（供模板里的按钮刷新）。</summary>
    public bool CanUndoMapping => mappingHistory.CanUndo;
    public bool CanRedoMapping => mappingHistory.CanRedo;
    public double MappingProgress { get => mappingProgress; private set { mappingProgress = value; OnPropertyChanged(); } }
    public double MappingLiveDepth { get => mappingLiveDepth; private set { mappingLiveDepth = value; OnPropertyChanged(); OnPropertyChanged(nameof(MappingLiveDepthLabel)); } }
    public string MappingLiveDepthLabel => string.Format(Localizer.Instance["Mapping.LiveDepth"], mappingLiveDepth);
    public bool HasMappingConflict => Editor.HasMappingConflict;
    /// <summary>是否存在可回滚的更早映射快照（直接查快照文件）。</summary>
    public bool HasMappingSnapshot => MappingSnapshotStore.Previous(MappingSnapshotPath(), CurrentMappingState()) is not null;
    public bool IsToastVisible { get => isToastVisible; private set { isToastVisible = value; OnPropertyChanged(); } }
    public double ToastOpacity { get => toastOpacity; private set { toastOpacity = value; OnPropertyChanged(); } }
    public string ToastMessage => Localizer.Instance["Toast.InvalidNumber"];
    public bool IsCalibrating => calibrationCancellation is not null;
    public bool IsStreaming => streamCancellation is not null;
    public bool IsGameMode => gameCancellation is not null;
    public double GameFrameRate { get => gameFrameRate; private set { gameFrameRate = value; OnPropertyChanged(); OnPropertyChanged(nameof(GameFrameRateLabel)); } }
    public string GameFrameRateLabel => string.Format(Localizer.Instance["Live.GameRate"], gameFrameRate);
    public bool IsConnecting => connectCancellation is not null;
    public bool CanToggleGameMode => (IsConnected && !IsBusy && !IsStreaming) || IsGameMode;
    public bool IsGameModeActive
    {
        get => IsGameMode;
        set
        {
            if (value == IsGameMode) return;
            if (value)
            {
                if (GameModeCommand.CanExecute(null))
                    GameModeCommand.Execute(null);
                else
                    OnPropertyChanged();
            }
            else
            {
                if (StopGameModeCommand.CanExecute(null))
                    StopGameModeCommand.Execute(null);
                else
                    OnPropertyChanged();
            }
        }
    }

    public void RefreshPorts()
    {
        Ports.Clear();
        foreach (var port in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Ports.Add(port);
        if (!Ports.Contains(SelectedPort)) SelectedPort = Ports.FirstOrDefault() ?? string.Empty;
        if (Ports.Count == 0 && Status == ConnectionStatus.Disconnected) StatusMessage = Localizer.Instance["Msg.NoPorts"];
    }

    // ---- 供 GUI 内嵌 MCP 服务器使用的入口 ----
    // GUI 持有唯一一条串口；MCP 通过这几个成员把工具调用接到同一份实时状态上，
    // 而不是另开一个进程去抢 COM 口。

    /// <summary>设备上当前生效的配置。</summary>
    internal ConfigBlob AppliedConfig => applied;
    /// <summary>编辑器里的暂存配置（未 apply 的改动）。</summary>
    internal ConfigBlob StagedConfig => Editor.ToConfig();
    internal string ConnectedPort => IsConnected ? SelectedPort : string.Empty;
    internal IEventRequestClient? EventClient => client as IEventRequestClient;

    /// <summary>从 MCP 指定端口时用：设置下拉框再走 GUI 的完整连接流程。</summary>
    internal void SelectPort(string port) => SelectedPort = port;

    /// <summary>MCP 改板参数：直接改编辑器，界面和「未应用」标记同步变化。</summary>
    internal void McpStageBoard(int boardIndex, BoardEdit edit)
    {
        var board = Editor.Boards[boardIndex];
        if (edit.BaseThreshold is { } baseThreshold) board.BaseThreshold = baseThreshold;
        if (edit.SensOffset is { } sensOffset) board.SensOffset = sensOffset;
        if (edit.TouchDebounce is { } touchDebounce) board.TouchDebounce = touchDebounce;
        if (edit.ReleaseDebounce is { } releaseDebounce) board.ReleaseDebounce = releaseDebounce;
        if (edit.Sfi is { } sfi) board.Sfi = sfi;
        if (edit.Esi is { } esi) board.Esi = esi;
        MarkDirty();
    }

    /// <summary>MCP 改单键偏移：走编辑器属性，单键面板上的着色和提示会一起刷新。</summary>
    internal void McpStageElectrode(int keyIndex, int offset)
    {
        Editor.Electrodes[keyIndex].Offset = offset;
        MarkDirty();
    }

    /// <summary>MCP 改映射：与 stdio 模式共用同一套对调语义，34 键始终保持一一对应。</summary>
    internal void McpStageMapping(int keyIndex, int boardSlot, int channel, bool swap)
    {
        var mapping = McpMapping.Change(Editor.CurrentMapping(), keyIndex, boardSlot, channel, swap);
        // GUI 这边还要把「组序 → 板槽位」的排布带上：按键序写入映射，槽位保持现状。
        Editor.LoadMapping(mapping, Editor.CurrentSlots());
    }

    /// <summary>MCP 读遥测：复用 GUI 的那条串口。界面正在流式输出时拒绝，避免两路读循环互相抢帧。</summary>
    internal async Task<string> McpReadTelemetryAsync(int durationMs, CancellationToken cancellationToken)
    {
        if (transport is null) throw new InvalidOperationException("not connected; call connect first");
        if (IsStreaming || IsGameMode) throw new InvalidOperationException(
            "the GUI is already streaming; stop the debug stream / game mode in the app first");
        var last = default(DebugFrame?);
        var count = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(durationMs);
        try
        {
            await transport.StreamDebugEventsAsync(100, sample => { last = sample.Frame; count++; }, _ => { }, cts.Token, _ => { });
        }
        catch (OperationCanceledException) { }
        if (last is not { } frame) return "no telemetry frames received";
        return JsonSerializer.Serialize(new
        {
            frames = count,
            durationMs,
            touchBits = frame.TouchBits,
            filtered = frame.Filtered,
            baseline = frame.Baseline,
            chipStatus = frame.ChipStatus,
            i2cErrors = frame.I2cErrors,
        });
    }

    public void MarkDirty()
    {
        // 映射重复时 Encode() 会抛异常（34 键必须映射到互不相同的通道）；
        // 这里把"无法编码"视为有未应用改动，交由 HasMappingConflict 屏蔽 Apply/Save。
        bool dirty;
        try { dirty = !Editor.ToConfig().Encode().AsSpan().SequenceEqual(applied.Encode()); }
        catch (ArgumentException) { dirty = true; }
        IsDirty = dirty;
        OnPropertyChanged(nameof(DirtyLabel));
    }

    // 测试钩子：AsyncCommand.Execute 是 async void，无法被 await；此处提供可等待的等价入口。
    public Task RunConnectForTestAsync() => ConnectAsync();
    public Task RunDisconnectForTestAsync() => DisconnectAsync();
    public Task RunGameModeForTestAsync() => StartGameModeAsync();
    public Task RunStopGameModeForTestAsync() => StopGameModeAsync();

    // MCP 也是走这两条路径（GUI 里的「连接 / 断开」按钮），所以进度、错误提示、
    // 状态灯都和手工操作完全一致，AI 做的每一步在界面上都看得见。
    internal Task McpConnectAsync() => ConnectAsync();
    internal Task McpDisconnectAsync() => DisconnectAsync();

    private async Task ConnectAsync()
    {
        connectCancellation = new CancellationTokenSource();
        // 整体超时兜底：即使某步卡住也不会永久挂起（用户仍可用“取消”即时中断）。
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(connectCancellation.Token);
        overall.CancelAfter(TimeSpan.FromSeconds(10));
        var token = overall.Token;
        IsBusy = true; Status = ConnectionStatus.Connecting; StatusMessage = Localizer.Instance["Msg.Opening"]; RefreshCommands();
        try
        {
            // 上一次连接可能失败/中断而残留了未释放的传输对象（会占着 COM 句柄），先清掉再新建。
            if (transport is not null) await DisconnectTransportAsync();
            (transport, client) = transportFactory(SelectedPort); workflow = new ConfigurationWorkflow(client);
            await transport.ConnectAsync(token);
            var version = await workflow.RequestAsync(ProtocolConstants.FrameType.Version, [], token);
            var ack = ReadAck(version, ProtocolConstants.FrameType.Version);
            if (ack.Result.Length != 16 || ack.Result[0] != 1 || ack.Result[9] != 0 || ack.Result[10] != 1 || ack.Result[11] != 48 || ack.Result[12] != 34 || ack.Result[13] != 4) throw new NotSupportedException(Localizer.Instance["Msg.Unsupported"]);
            var capabilities = (uint)(ack.Result[5] | (ack.Result[6] << 8) | (ack.Result[7] << 16) | (ack.Result[8] << 24));
            // 先把版本号记下来再判能力位：连接被拒时界面也要能显示"设备上是哪个版本"，
            // 否则用户只看到"固件太旧"却不知道板子上到底是什么。
            FirmwareInfo.DeviceProtocol = $"{ack.Result[0]}.{ack.Result[1]}";
            FirmwareInfo.DeviceVersion = new FirmwareVersion(ack.Result[2], ack.Result[3], ack.Result[4]);
            FirmwareInfo.HasKeyMappingCapability = (capabilities & ProtocolConstants.CapabilityKeyMapping) != 0;
            if ((capabilities & ProtocolConstants.CapabilityKeyMapping) == 0) throw new NotSupportedException(Localizer.Instance["Msg.FirmwareTooOld"]);
            await EnterConfigAsync(token);
            var deviceConfig = await ReadConfigWithRecoveryAsync(token);
            applied = deviceConfig; Editor.Update(applied); WireEditor(); IsDirty = false; Telemetry.ApplyThresholds(applied); ResetMappingHistory();
            var identity = new DeviceIdentity(SelectedPort, null, ack.Result[0], ack.Result[1]);
            var path = ConfigPath(identity);
            if (File.Exists(path))
            {
                var local = await ConfigJsonStore.LoadAsync(path);
                persisted = local.Config;
                HasConflict = ConfigJsonStore.IsConflict(local, identity, applied);
        OnPropertyChanged(nameof(ConflictLabel));
        OnPropertyChanged(nameof(ToastMessage));
            }
            if (!HasConflict) await ConfigJsonStore.SaveAtomicAsync(path, new ConfigDocument(identity, applied, new ConfigStatus(false, false)));
            Status = ConnectionStatus.Connected; StatusMessage = HasConflict ? Localizer.Instance["Msg.ConnectedConflict"] : Localizer.Instance["Msg.Connected"];
        }
        catch (OperationCanceledException) { Status = ConnectionStatus.Disconnected; StatusMessage = connectCancellation.IsCancellationRequested ? Localizer.Instance["Msg.ConnectCancelled"] : Localizer.Instance["Msg.ConnectTimedOut"]; await DisconnectTransportAsync(); }
        catch (NotSupportedException ex) { Status = ConnectionStatus.Unsupported; StatusMessage = ex.Message; await DisconnectTransportAsync(); }
        catch (Exception ex) { Status = ConnectionStatus.Error; StatusMessage = UserMessage(ex); await DisconnectTransportAsync(); }
        finally { connectCancellation.Dispose(); connectCancellation = null; IsBusy = false; RefreshCommands(); }
    }

    private async Task CancelConnectAsync()
    {
        connectCancellation?.Cancel();
        StatusMessage = Localizer.Instance["Msg.CancellingConnect"];
        await Task.CompletedTask;
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        StatusMessage = Localizer.Instance["Msg.Disconnecting"];
        RefreshCommands();
        try
        {
            if (streamCancellation is not null)
            {
                streamCancellation.Cancel();
                streamCancellation = null;
            }
            if (gameCancellation is not null)
            {
                gameCancellation.Cancel();
                gameCancellation = null;
            }
            if (calibrationCancellation is not null)
            {
                calibrationCancellation.Cancel();
                calibrationCancellation = null;
            }
            mappingSession = null;
            EndMappingStream();

            if (workflow is not null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await workflow.RequestAsync(ProtocolConstants.FrameType.ExitConfig, [], cts.Token);
                }
                catch { }
            }

            await DisconnectTransportAsync();
        }
        finally
        {
            Status = ConnectionStatus.Disconnected;
            StatusMessage = Localizer.Instance["Msg.DisconnectedManual"];
            IsBusy = false;
            HasConflict = false;
            foreach (var key in TouchKeys) key.IsPressed = false;
            RefreshCommands();
        }
    }

    private async Task ApplyAsync()
    {
        IsBusy = true; StatusMessage = Localizer.Instance["Msg.Applying"];
        try { await McpApplyAsync(CancellationToken.None); StatusMessage = Localizer.Instance["Msg.Applied"]; }
        catch (Exception ex) { Status = transport?.State == TransportState.Disconnected ? ConnectionStatus.Disconnected : ConnectionStatus.Error; StatusMessage = Localizer.Instance["Msg.ApplyFailed"] + UserMessage(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>GUI 与 MCP 共用的应用路径；失败时抛出，由调用方决定怎么呈现。</summary>
    internal async Task<ConfigBlob> McpApplyAsync(CancellationToken cancellationToken)
    {
        if (workflow is null) throw new InvalidOperationException("not connected");
        applied = await workflow.ApplyAsync(Editor.ToConfig(), cancellationToken);
        IsDirty = false; OnPropertyChanged(nameof(DirtyLabel)); Telemetry.ApplyThresholds(applied); SaveMappingSnapshot("apply");
        return applied;
    }

    private async Task SaveAsync()
    {
        IsBusy = true; StatusMessage = Localizer.Instance["Msg.Saving"];
        var deviceSaved = false;
        try
        {
            var outcome = await McpSaveAsync(CancellationToken.None);
            deviceSaved = outcome.DeviceSaved;
            if (outcome.MirrorError is not null) throw outcome.MirrorError;
            StatusMessage = Localizer.Instance["Msg.Saved"];
        }
        catch (Exception ex) { Status = transport?.State == TransportState.Disconnected ? ConnectionStatus.Disconnected : ConnectionStatus.Error; StatusMessage = deviceSaved ? Localizer.Instance["Msg.EepromSavedJsonFailed"] + UserMessage(ex) : Localizer.Instance["Msg.SaveFailed"] + UserMessage(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// GUI 与 MCP 共用的保存路径：先写 EEPROM，再写本地 JSON 镜像。
    /// EEPROM 失败直接抛（板子确实没存）；镜像失败通过 <see cref="SaveOutcome.MirrorError"/> 返回，
    /// 因为此时板子已经存好了，不该报成“保存失败”。
    /// </summary>
    internal async Task<SaveOutcome> McpSaveAsync(CancellationToken cancellationToken)
    {
        if (workflow is null) throw new InvalidOperationException("not connected; call connect first");
        await workflow.SaveAsync(cancellationToken);
        try
        {
            var document = new ConfigDocument(new DeviceIdentity(SelectedPort, null), applied, new ConfigStatus(false, false));
            await ConfigJsonStore.SaveAtomicAsync(ConfigPath(document.Device), document);
            persisted = applied; HasConflict = false; OnPropertyChanged(nameof(ConflictLabel)); SaveMappingSnapshot("save");
            return new SaveOutcome(true, null);
        }
        catch (Exception ex) { return new SaveOutcome(true, ex); }
    }

    internal readonly record struct SaveOutcome(bool DeviceSaved, Exception? MirrorError);

    private async Task ReadbackAsync()
    {
        IsBusy = true; StatusMessage = Localizer.Instance["Msg.ReadingConfig"];
        try { applied = await ReadConfigAsync(); Editor.Update(applied); WireEditor(); IsDirty = false; Telemetry.ApplyThresholds(applied); ResetMappingHistory(); StatusMessage = Localizer.Instance["Msg.ReadbackLoaded"]; }
        catch (Exception ex) { Status = transport?.State == TransportState.Disconnected ? ConnectionStatus.Disconnected : ConnectionStatus.Error; StatusMessage = Localizer.Instance["Msg.ReadbackFailed"] + UserMessage(ex); }
        finally { IsBusy = false; }
    }

    private async Task UseLocalAsync()
    {
        try
        {
            var path = ConfigPath(new DeviceIdentity(SelectedPort, null));
            var local = await ConfigJsonStore.LoadAsync(path); Editor.Update(local.Config); WireEditor(); HasConflict = false; IsDirty = true; OnPropertyChanged(nameof(ConflictLabel)); ResetMappingHistory(); StatusMessage = Localizer.Instance["Msg.LocalStaged"];
        }
        catch (Exception ex) { StatusMessage = Localizer.Instance["Msg.LocalStageFailed"] + UserMessage(ex); }
    }

    private Task RestoreDefaults()
    {
        Editor.Update(ConfigBlob.Defaults()); WireEditor(); MarkDirty(); ResetMappingHistory(); StatusMessage = Localizer.Instance["Msg.DefaultsStaged"]; return Task.CompletedTask;
    }

    private void WireEditor()
    {
        foreach (var board in Editor.Boards) board.PropertyChanged += (_, _) => MarkDirty();
        foreach (var electrode in Editor.Electrodes) electrode.PropertyChanged += (_, _) => MarkDirty();
        foreach (var mapping in Editor.KeyMappings) mapping.PropertyChanged += (_, _) => MarkDirty();
    }

    /// <summary>映射变化：脏标记 + 撤销栈 + 应用条件刷新。载入配置时用 <see cref="ResetMappingHistory"/> 重置基线。</summary>
    private void OnMappingChanged()
    {
        MarkDirty();
        if (!replayingHistory) mappingHistory.Push(CurrentMappingState());
        NotifyMappingState();
    }

    public void ResetMappingHistory()
    {
        mappingHistory.Reset(CurrentMappingState());
        NotifyMappingState();
    }

    private void NotifyMappingState()
    {
        OnPropertyChanged(nameof(CanUndoMapping));
        OnPropertyChanged(nameof(CanRedoMapping));
        OnPropertyChanged(nameof(HasMappingConflict));
        OnPropertyChanged(nameof(HasMappingSnapshot));
        RefreshCommands();
    }

    private MappingState CurrentMappingState() => new() { KeyMap = Editor.CurrentMapping(), Slots = Editor.CurrentSlots() };

    private void ApplyMappingState(MappingState state)
    {
        // 撤销/重做本身不应再往栈里推一条（否则重做会被清掉）。
        replayingHistory = true;
        try { Editor.LoadMapping(state.KeyMap, state.Slots); }
        finally { replayingHistory = false; }
        MarkDirty();
        NotifyMappingState();
    }

    private void UndoMapping()
    {
        var state = mappingHistory.Undo();
        if (state is null) return;
        ApplyMappingState(state);
        StatusMessage = Localizer.Instance["Config.Mapping.Undone"];
    }

    private void RedoMapping()
    {
        var state = mappingHistory.Redo();
        if (state is null) return;
        ApplyMappingState(state);
        StatusMessage = Localizer.Instance["Config.Mapping.Redone"];
    }

    /// <summary>「新建板卡」：按界面上的每板键数把连续键名灌进四块板。</summary>
    private void FillBoards()
    {
        Editor.FillBoards(Editor.BoardMappings.Select(b => b.FillCount).ToList());
        MarkDirty();
        StatusMessage = string.Format(Localizer.Instance["Config.Mapping.Filled"], Editor.BoardMappings.Sum(b => b.Channels.Count(c => c.IsAssigned)));
    }

    private string MappingSnapshotPath() => Path.Combine(Path.GetDirectoryName(ConfigPath(new DeviceIdentity(SelectedPort, null))) ?? ".", "mapping_snapshots.json");

    private void SaveMappingSnapshot(string source)
    {
        try { MappingSnapshotStore.Append(MappingSnapshotPath(), CurrentMappingState(), source); }
        catch (Exception ex) { Telemetry.RecordEvent("WARN", ex.Message); }
        OnPropertyChanged(nameof(HasMappingSnapshot));
    }    private void RollbackMappingSnapshot()
    {
        var entry = MappingSnapshotStore.Previous(MappingSnapshotPath(), CurrentMappingState());
        if (entry is null) { StatusMessage = Localizer.Instance["Config.Mapping.NoSnapshot"]; return; }
        Editor.LoadMapping(entry.KeyMap, entry.Slots);
        MarkDirty();
        NotifyMappingState();
        StatusMessage = string.Format(Localizer.Instance["Config.Mapping.Rolled"], entry.Timestamp.LocalDateTime);
    }

    // ---- GUI 内嵌 MCP 端点 ----

    private async Task ToggleMcpServeAsync()
    {
        if (IsMcpServing) { await StopMcpServerAsync(); return; }
        try
        {
            mcpServerFactory ??= Task.FromResult(new McpServer(new McpToolHost(new GuiSession(this))));
            var server = await mcpServerFactory;
            var port = mcpHost.Start(server);
            McpGuide.EndpointPort = port;
            McpEndpointError = string.Empty;
            // IsMcpServing 的 setter 会重抛 McpServeLabel / McpToggleLabel，不用在这里再抛一次。
            McpGuide.IsServerRunning = true;
            IsMcpServing = true;
            Telemetry.RecordEvent("INFO", McpServeLabel);
        }
        catch (Exception ex)
        {
            McpEndpointError = ex.Message;
            McpGuide.IsServerRunning = false;
            IsMcpServing = false;
        }
    }

    /// <summary>关闭内嵌端点；退出程序时也会走这里，避免端口留在监听状态。</summary>
    internal async Task StopMcpServerAsync()
    {
        await mcpHost.StopAsync();
        McpGuide.IsServerRunning = false;
        IsMcpServing = false;
    }

    /// <summary>
    /// 重读本地固件清单，并查一次 GitHub 上的最新版本。
    /// 远端查询失败不弹错误框 —— 版本页里直接写明失败原因就够了。
    /// </summary>
    private async Task RefreshFirmwareAsync()
    {
        FirmwareInfo.RefreshLocalManifest();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await FirmwareInfo.CheckRemoteAsync(cts.Token);
    }

    /// <summary>
    /// 把本地 firmware.bin 刷进板子。
    ///
    /// 串口必须先释放：刷写期间任何还开着这个口的程序都会让 1200bps 复位失效。
    /// 所以这里先正常断开（发 EXIT_CFG 让固件回到游戏模式，再关端口），
    /// 刷完把端口选回刷写后的那个口，但**不自动重连** —— 连接动作留给用户确认。
    /// </summary>
    private async Task FlashFirmwareAsync()
    {
        var port = SelectedPort;
        if (string.IsNullOrWhiteSpace(port))
        {
            FirmwareInfo.ReportFlashResult(Localizer.Instance["Fw.SelectPortFirst"], failed: true);
            return;
        }

        if (IsConnected) await DisconnectAsync();

        StatusMessage = Localizer.Instance["Fw.Flashing"];
        try
        {
            var ok = await FirmwareInfo.FlashAsync(port, CancellationToken.None);
            StatusMessage = ok ? Localizer.Instance["Fw.FlashDone"] : Localizer.Instance["Fw.FlashFailed"];
            if (!ok) return;

            // 刷完固件会重新枚举，端口号可能变了；刷新列表并把选中项指到新口。
            RefreshPorts();
            var reported = FirmwareInfo.LastFlashPort;
            if (!string.IsNullOrEmpty(reported) && Ports.Contains(reported)) SelectedPort = reported;
            else if (Ports.Contains(port)) SelectedPort = port;

            // 版本号要重新连上才会更新，先清掉旧的，免得界面显示刷写前的数字。
            FirmwareInfo.DeviceVersion = FirmwareVersion.Unknown;
            FirmwareInfo.DeviceProtocol = string.Empty;
            FirmwareInfo.HasKeyMappingCapability = false;
        }
        finally
        {
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(IsCalibrating));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsGameMode));
        OnPropertyChanged(nameof(IsGameModeActive));
        OnPropertyChanged(nameof(CanToggleGameMode));
        foreach (var command in new[] { RefreshPortsCommand, ConnectCommand, DisconnectCommand, CancelConnectCommand, ApplyCommand, SaveCommand, ReadbackCommand, RestoreDefaultsCommand, UseDeviceCommand, UseLocalCommand, CalibrateCommand, CancelCalibrationCommand, StartStreamCommand, StopStreamCommand, GameModeCommand, StopGameModeCommand, StartGuidedCommand, SkipGuidedKeyCommand, FinishGuidedKeyCommand, CancelGuidedCommand, ApplyGuidedResultCommand, DiscardGuidedResultCommand, StartMappingCommand, SkipMappingKeyCommand, FinishMappingKeyCommand, CancelMappingCommand, UndoMappingCommand, RedoMappingCommand, FillBoardsCommand, RollbackMappingCommand, ToggleMcpServeCommand, FlashFirmwareCommand })
        {
            if (command is AsyncCommand asyncCommand) asyncCommand.Refresh();
            else if (command is RelayCommand relayCommand) relayCommand.Refresh();
        }
        OnPropertyChanged(nameof(IsGuidedRunning));
        OnPropertyChanged(nameof(ShowGuidedIntro));
        OnPropertyChanged(nameof(HasGuidedResult));
        OnPropertyChanged(nameof(IsMappingRunning));
        OnPropertyChanged(nameof(ShowMappingIntro));
        OnPropertyChanged(nameof(HasMappingConflict));
        OnPropertyChanged(nameof(CanUndoMapping));
        OnPropertyChanged(nameof(CanRedoMapping));
        OnPropertyChanged(nameof(HasMappingSnapshot));
    }

    private void OnInvalidNumericInput(object? sender, EventArgs args)
    {
        toastCancellation?.Cancel();
        toastCancellation?.Dispose();
        toastCancellation = new CancellationTokenSource();
        _ = ShowToastAsync(toastCancellation.Token);
    }

    private async Task ShowToastAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsToastVisible = true;
            ToastOpacity = 1;
        });
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => ToastOpacity = 0);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => IsToastVisible = false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CalibrateAsync()
    {
        if (client is not IEventRequestClient events) { CalibrationLabel = Localizer.Instance["Cal.Unavailable"]; return; }
        calibrationCancellation = new CancellationTokenSource(); IsBusy = true; CalibrationProgress = 0.1; CalibrationLabel = Localizer.Instance["Cal.NoTouch"]; RefreshCommands();
        ClearCalibrationResult();
        try
        {
            await McpCalibrateAsync(events, calibrationCancellation.Token);
        }
        catch (OperationCanceledException) { CalibrationProgress = 0; CalibrationLabel = calibrationCancellation?.IsCancellationRequested == true ? Localizer.Instance["Cal.Cancelled"] : Localizer.Instance["Cal.TimedOut"]; }
        catch (Exception ex) { CalibrationProgress = 0; CalibrationLabel = Localizer.Instance["Cal.Failed"] + UserMessage(ex); }
        finally { calibrationCancellation.Dispose(); calibrationCancellation = null; IsBusy = false; RefreshCommands(); }
    }

    /// <summary>GUI 与 MCP 共用的校准路径。MCP 调用时不带 <c>calibrationCancellation</c>
    /// （那是界面「取消」按钮的通道），失败直接抛给调用方。</summary>
    internal async Task<(CalibrationStatus Status, CalibrationReason Reason)> McpCalibrateAsync(IEventRequestClient events, CancellationToken cancellationToken)
    {
        var previous = applied;
        CalibrationDeadlineMs = 60000;
        CalibrationProgress = 0.1;
        var operation = events.SendRequestAndWaitForEventAsync((byte)ProtocolConstants.FrameType.Calibrate, new CalibratePayload(0, 0).Encode(), (byte)ProtocolConstants.FrameType.CalDone, TimeSpan.FromSeconds(61), cancellationToken);
        while (!operation.IsCompleted)
        {
            await Task.Delay(250, cancellationToken);
            CalibrationDeadlineMs = (uint)Math.Max(0, CalibrationDeadlineMs - 250);
            CalibrationProgress = 0.1 + 0.8 * (1 - CalibrationDeadlineMs / 60000d);
        }
        var result = await operation;
        if (result.Ack.Type == (byte)ProtocolConstants.FrameType.Error) { var error = ErrorPayload.Decode(result.Ack.Payload); throw new InvalidOperationException($"CALIBRATE rejected: {Enum.GetName((ProtocolConstants.ErrorStatus)error.Status) ?? "unknown error"}."); }
        var done = CalDonePayload.Decode(result.Event.Payload); CalibrationProgress = 1;
        if (done.Status == CalibrationStatus.Success)
        {
            // 校准只改 RAM 已应用阈值；补发一次 GET_CFG 读回真实的新配置，再同步编辑器与遥测阈值。
            ConfigBlob? current = null;
            try { current = await ReadConfigWithRecoveryAsync(cancellationToken); applied = current; Editor.Update(applied); WireEditor(); IsDirty = false; OnPropertyChanged(nameof(DirtyLabel)); Telemetry.ApplyThresholds(applied); }
            catch (Exception ex) { StatusMessage = Localizer.Instance["Msg.CalibrationReadbackFailed"] + UserMessage(ex); }
            PublishCalibrationResult(CalibrationSummary.From(previous, current, done));
        }
        else
        {
            CalibrationLabel = string.Format(Localizer.Instance["Cal.FailedReason"], done.Reason);
            PublishCalibrationResult(CalibrationSummary.Failed(done));
        }
        return (done.Status, done.Reason);
    }

    private void ClearCalibrationResult()
    {
        calibrationSummary = string.Empty;
        CalibrationResults.Clear();
        OnPropertyChanged(nameof(CalibrationHeadline));
        OnPropertyChanged(nameof(HasCalibrationResult));
    }

    // 校准结束后发布可展示的结果：状态行追加概要、校准页明细、遥测事件日志留痕。
    private void PublishCalibrationResult(CalibrationSummary summary)
    {
        var headline = summary.BuildHeadline();
        calibrationSummary = headline;
        CalibrationResults.Clear();
        foreach (var board in summary.Boards) CalibrationResults.Add(board);
        if (summary.Succeeded) CalibrationLabel = Localizer.Instance["Cal.Succeeded"];
        OnPropertyChanged(nameof(CalibrationHeadline));
        OnPropertyChanged(nameof(HasCalibrationResult));
        Telemetry.RecordEvent(summary.Succeeded ? "INFO" : "ERROR", Localizer.Instance["Cal.LogPrefix"] + " " + headline);
    }
    private async Task CancelCalibrationAsync()
    {
        calibrationCancellation?.Cancel(); CalibrationLabel = Localizer.Instance["Cal.Cancelling"]; await Task.CompletedTask;
    }
    private async Task StartStreamAsync()
    {
        if (transport is null) { StatusMessage = Localizer.Instance["Msg.StreamUnavailable"]; return; }
        streamCancellation = new CancellationTokenSource(); Telemetry.Begin(ProtocolConstants.DefaultStreamRateHz); RefreshCommands(); StatusMessage = Localizer.Instance["Msg.StreamStarting"];
        try
        {
            var stream = await transport.StreamDebugEventsAsync(ProtocolConstants.DefaultStreamRateHz, sample =>
            {
                Telemetry.AddFrame(sample.Frame, sample.Sequence, sample.ReceivedAt);
                for (var i = 0; i < TouchKeys.Count; i++) TouchKeys[i].IsPressed = (sample.Frame.TouchBits & (1UL << (33 - i))) != 0;
            }, error => Telemetry.MarkMalformed(Localizer.Instance["Msg.MalformedFrame"] + error.Message), streamCancellation.Token, info => Telemetry.SetAcceptedRate(info.AcceptedRateHz, info.ActualRateHz));
            Telemetry.SetAcceptedRate(stream.AcceptedRateHz, stream.ActualRateHz);
        }
        catch (OperationCanceledException) { Telemetry.Stop(); }
        catch (IOException ex) { await MarkTransportLostAsync(); Telemetry.MarkDisconnected(UserMessage(ex)); StatusMessage = Localizer.Instance["Msg.TelemetryStopped"] + UserMessage(ex); }
        catch (Exception ex) { if (transport?.State == TransportState.Disconnected) await MarkTransportLostAsync(); Telemetry.MarkMalformed(UserMessage(ex)); StatusMessage = Localizer.Instance["Msg.TelemetryFailed"] + UserMessage(ex); }
        finally { streamCancellation?.Dispose(); streamCancellation = null; RefreshCommands(); }
    }

    // 底层传输已断开时同步 UI 状态，避免“左上角仍显示已连接、但端口其实已死”的状态不一致。
    private async Task MarkTransportLostAsync()
    {
        if (transport is not null && transport.State != TransportState.Disconnected) return; // 明确的超时/拒绝不算断开
        await DisconnectTransportAsync();
        Status = ConnectionStatus.Disconnected;
    }
    private async Task StopStreamAsync() { streamCancellation?.Cancel(); await Task.CompletedTask; }

    // ---- 引导式强力自动调整：复用调试流，逐键引导"按下→保持→松开"，工具侧算阈值与板级参数 ----
    private async Task StartGuidedAsync()
    {
        if (transport is null) { StatusMessage = Localizer.Instance["Msg.StreamUnavailable"]; return; }
        // 目标键按 34 键标签顺序（与编辑器 Electrodes 一一对应）。
        var targets = BuildGuidedTargets();
        if (targets.Count == 0) { StatusMessage = Localizer.Instance["Guided.NoTargets"]; return; }
        guidedResult = null; GuidedBoards.Clear(); GuidedKeys.Clear();
        guidedProgress = 0; guidedKeyProgress = 0; guidedLiveDepth = 0;
        guidedSession = new GuidedCalibrationSession(targets);
        guidedCancellation = new CancellationTokenSource();
        guidedLastFrameMs = 0; guidedPreviousRawMs = 0; guidedFirstFrame = true;
        GuidedStepLabel = string.Format(Localizer.Instance["Guided.BaselineStep"], 0);
        OnPropertyChanged(nameof(GuidedHeadline));
        StreamForGuided();
        RefreshCommands();
        StatusMessage = Localizer.Instance["Guided.Running"];
        await Task.CompletedTask;
    }

    // 引导采集在后台线程消费调试流（~33Hz），逐帧推进状态机；UI 更新一律 Post 回 UI 线程。
    private void StreamForGuided()
    {
        var token = guidedCancellation!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await transport!.StreamDebugEventsAsync(ProtocolConstants.DefaultStreamRateHz, OnGuidedFrame, _ => { }, token,
                    info => Telemetry.SetAcceptedRate(info.AcceptedRateHz, info.ActualRateHz));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }, token);
    }

    private void OnGuidedFrame(DebugFrameEvent sample)
    {
        var session = guidedSession;
        if (session is null) return;
        if (guidedFirstFrame)
        {
            guidedFirstFrame = false;
            guidedLastFrameMs = sample.Frame.TimestampMs;
            session.Begin(guidedLastFrameMs);
        }
        else
        {
            // 设备 millis() 可能回绕；按原始时间戳的增量累加成单调时间轴。
            guidedLastFrameMs += unchecked(sample.Frame.TimestampMs - guidedPreviousRawMs);
        }
        guidedPreviousRawMs = sample.Frame.TimestampMs;
        session.PushFrame(guidedLastFrameMs, sample.Frame.Filtered, sample.Frame.Baseline);
        UpdateGuidedUi(session);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            for (var i = 0; i < TouchKeys.Count; i++) TouchKeys[i].IsPressed = (sample.Frame.TouchBits & (1UL << (33 - i))) != 0;
        });
    }

    private void UpdateGuidedUi(GuidedCalibrationSession session)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(guidedSession, session)) return; // 已完成/已取消
            GuidedProgress = session.TotalProgress;
            GuidedKeyProgress = session.KeyProgress;
            GuidedLiveDepth = session.LiveDepth;
            GuidedStepLabel = DescribeGuidedStep(session);
            (SkipGuidedKeyCommand as RelayCommand)?.Refresh();
            (FinishGuidedKeyCommand as RelayCommand)?.Refresh();
            if (session.IsComplete) CompleteGuided();
        });
    }

    private string DescribeGuidedStep(GuidedCalibrationSession session)
    {
        if (session.Phase == GuidedPhase.Baseline)
            return string.Format(Localizer.Instance["Guided.BaselineStep"], Math.Round(session.BaselineProgress * 100));
        var target = session.CurrentTarget;
        if (target is null) return Localizer.Instance["Guided.Finishing"];
        return string.Format(Localizer.Instance["Guided.KeyStep"], target.Label, session.CompletedRepeats + 1, session.RepeatsPerKey);
    }

    private void CompleteGuided()
    {
        var session = guidedSession;
        if (session is null || guidedResult is not null) return;
        guidedResult = session.BuildResult(Editor.ToConfig(), GuidedTriggerRatio);
        guidedSession = null;
        EndGuidedStream();
        PublishGuidedResult();
    }

    private void EndGuidedStream()
    {
        guidedCancellation?.Cancel();
        guidedCancellation = null;
    }

    private void PublishGuidedResult()
    {
        GuidedBoards.Clear(); GuidedKeys.Clear();
        foreach (var board in guidedResult!.Boards) GuidedBoards.Add(new GuidedBoardPreview(board));
        foreach (var key in guidedResult.Keys) GuidedKeys.Add(new GuidedKeyPreview(key));
        var measured = guidedResult.MeasuredCount;
        var failed = guidedResult.FailedCount;
        GuidedHeadline = string.Format(Localizer.Instance["Guided.Headline"], measured, guidedResult.Keys.Count, failed, GuidedTriggerPercent);
        OnPropertyChanged(nameof(GuidedHeadline));
        GuidedStepLabel = Localizer.Instance["Guided.Done"];
        RefreshCommands();
        Telemetry.RecordEvent(failed > 0 ? "WARN" : "INFO", Localizer.Instance["Guided.LogPrefix"] + " " + GuidedHeadline);
    }

    private void SkipGuidedKey()
    {
        var session = guidedSession;
        if (session is null) return;
        session.SkipCurrentKey(session.CurrentTimestampMs);
        if (session.IsComplete) CompleteGuided();
    }

    private void FinishGuidedKey()
    {
        var session = guidedSession;
        if (session is null) return;
        session.FinishCurrentKeyEarly(session.CurrentTimestampMs);
        if (session.IsComplete) CompleteGuided();
        else RefreshCommands();
    }

    private async Task CancelGuidedAsync()
    {
        guidedSession = null;
        EndGuidedStream();
        GuidedStepLabel = Localizer.Instance["Guided.Cancelled"];
        StatusMessage = Localizer.Instance["Guided.Cancelled"];
        RefreshCommands();
        await Task.CompletedTask;
    }

    // 把预览表里勾选的行写回编辑器（不改设备；用户随后照常 应用 → 保存）。
    private void ApplyGuidedResult()
    {
        var result = guidedResult;
        if (result is null) return;
        var adoptedKeys = GuidedKeys.Where(key => key.Adopt).Select(key => key.Outcome).ToHashSet();
        var adoptedBoards = GuidedBoards.Where(board => board.Adopt).Select(board => board.Outcome).ToHashSet();
        var updated = GuidedCalibrationAlgorithm.Apply(result, Editor.ToConfig(),
            key => adoptedKeys.Contains(key), board => adoptedBoards.Contains(board));
        Editor.Update(updated);
        WireEditor();
        MarkDirty();
        Telemetry.ApplyThresholds(Editor.ToConfig());
        StatusMessage = Localizer.Instance["Guided.Applied"];
        DiscardGuidedResult();
    }

    private void DiscardGuidedResult()
    {
        guidedResult = null;
        GuidedBoards.Clear(); GuidedKeys.Clear();
        guidedProgress = 0; guidedKeyProgress = 0; guidedLiveDepth = 0;
        GuidedHeadline = null;
        GuidedStepLabel = string.Empty;
        OnPropertyChanged(nameof(GuidedHeadline));
        RefreshCommands();
    }

    private List<GuidedCalibrationSession.TargetKey> BuildGuidedTargets()
    {
        var targets = new List<GuidedCalibrationSession.TargetKey>();
        for (var keyIndex = 0; keyIndex < Editor.Electrodes.Count; keyIndex++)
        {
            var electrode = Editor.Electrodes[keyIndex];
            targets.Add(new GuidedCalibrationSession.TargetKey(keyIndex, electrode.Label, electrode.MprId * 12 + electrode.PortId, electrode.MprId));
        }
        return targets;
    }

    // ---- 映射自动学习：逐个提示用户按住物理电极，工具在 48 通道里找响应最大的那个 ----
    private async Task StartMappingAsync()
    {
        if (transport is null) { StatusMessage = Localizer.Instance["Msg.StreamUnavailable"]; return; }
        var targets = MappingLearnSession.BuildKeyOrderTargets(
            Editor.KeyMappings.Select(m => (m.Index, m.Label)));
        if (targets.Count == 0) { StatusMessage = Localizer.Instance["Mapping.NoTargets"]; return; }
        mappingSession = new MappingLearnSession(targets);
        mappingCancellation = new CancellationTokenSource();
        mappingLastFrameMs = 0; mappingPreviousRawMs = 0; mappingFirstFrame = true;
        mappingProgress = 0; mappingLiveDepth = 0;
        MappingHeadline = null;
        MappingStepLabel = string.Format(Localizer.Instance["Mapping.BaselineStep"], 0);
        StreamForMapping();
        RefreshCommands();
        StatusMessage = Localizer.Instance["Mapping.Running"];
        await Task.CompletedTask;
    }

    private void StreamForMapping()
    {
        var token = mappingCancellation!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await transport!.StreamDebugEventsAsync(ProtocolConstants.DefaultStreamRateHz, OnMappingFrame, _ => { }, token,
                    info => Telemetry.SetAcceptedRate(info.AcceptedRateHz, info.ActualRateHz));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }, token);
    }

    private void OnMappingFrame(DebugFrameEvent sample)
    {
        var session = mappingSession;
        if (session is null) return;
        if (mappingFirstFrame)
        {
            mappingFirstFrame = false;
            mappingLastFrameMs = sample.Frame.TimestampMs;
            session.Begin(mappingLastFrameMs);
        }
        else
        {
            mappingLastFrameMs += unchecked(sample.Frame.TimestampMs - mappingPreviousRawMs);
        }
        mappingPreviousRawMs = sample.Frame.TimestampMs;
        session.PushFrame(mappingLastFrameMs, sample.Frame.Filtered, sample.Frame.Baseline);
        UpdateMappingUi(session);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            for (var i = 0; i < TouchKeys.Count; i++) TouchKeys[i].IsPressed = (sample.Frame.TouchBits & (1UL << (33 - i))) != 0;
        });
    }

    private void UpdateMappingUi(MappingLearnSession session)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(mappingSession, session)) return;
            MappingProgress = session.TotalProgress;
            MappingLiveDepth = session.LiveDepth;
            MappingStepLabel = DescribeMappingStep(session);
            (SkipMappingKeyCommand as RelayCommand)?.Refresh();
            (FinishMappingKeyCommand as RelayCommand)?.Refresh();
            if (session.IsComplete) CompleteMapping();
        });
    }

    private string DescribeMappingStep(MappingLearnSession session)
    {
        if (session.Phase == GuidedPhase.Baseline)
            return string.Format(Localizer.Instance["Mapping.BaselineStep"], Math.Round(session.BaselineProgress * 100));
        var target = session.CurrentTarget;
        if (target is null) return Localizer.Instance["Mapping.Done"];
        return string.Format(Localizer.Instance["Mapping.KeyStep"], target.Label,
            session.CurrentIndex + 1, Editor.KeyMappings.Count);
    }

    private void CompleteMapping()
    {
        var session = mappingSession;
        if (session is null) return;
        mappingSession = null;
        EndMappingStream();
        var current = Editor.ToConfig();
        var (mapping, _) = session.ApplyTo(current.Mapping);
        Editor.Update(current with { KeyMap = mapping });
        WireEditor();
        MarkDirty();
        ResetMappingHistory();
        Telemetry.ApplyThresholds(Editor.ToConfig());
        MappingHeadline = string.Format(Localizer.Instance["Mapping.Headline"], session.DetectedCount, session.Outcomes.Count, session.UndetectedCount);
        MappingStepLabel = Localizer.Instance["Mapping.Done"];
        StatusMessage = Localizer.Instance["Mapping.Applied"];
        OnPropertyChanged(nameof(HasMappingConflict));
        RefreshCommands();
        Telemetry.RecordEvent(session.UndetectedCount > 0 ? "WARN" : "INFO", Localizer.Instance["Mapping.LogPrefix"] + " " + MappingHeadline);
    }

    private void EndMappingStream()
    {
        mappingCancellation?.Cancel();
        mappingCancellation = null;
    }

    private void SkipMappingKey()
    {
        var session = mappingSession;
        if (session is null) return;
        session.SkipCurrentKey(session.CurrentTimestampMs);
        if (session.IsComplete) CompleteMapping();
    }

    private void FinishMappingKey()
    {
        var session = mappingSession;
        if (session is null) return;
        session.FinishCurrentKeyEarly(session.CurrentTimestampMs);
        if (session.IsComplete) CompleteMapping();
        else RefreshCommands();
    }

    private async Task CancelMappingAsync()
    {
        mappingSession = null;
        EndMappingStream();
        MappingStepLabel = Localizer.Instance["Mapping.Cancelled"];
        StatusMessage = Localizer.Instance["Mapping.Cancelled"];
        RefreshCommands();
        await Task.CompletedTask;
    }

    // GAME 模式：发 EXIT_CFG 让固件退出配置模式并以不受限速率输出 9 字节游戏帧（实测数百 Hz），
    // 直接驱动 34 键触摸反馈并显示实测帧率。停止时发 ENTER_CFG 把板子拉回 CONFIG_IDLE。
    private async Task StartGameModeAsync()
    {
        if (transport is null || workflow is null) { StatusMessage = Localizer.Instance["Msg.StreamUnavailable"]; return; }
        gameCancellation = new CancellationTokenSource();
        gameFrameCount = 0; GameFrameRate = 0; gameRateWindowStart = DateTimeOffset.UtcNow;
        var token = gameCancellation.Token;
        IsBusy = true; RefreshCommands(); StatusMessage = Localizer.Instance["Msg.GameStarting"];
        try
        {
            // 进入 GAME 模式：板子回 ACK 后随即开始流；容忍 IllegalState/Busy（可能已在游戏态）。
            try { await workflow.RequestAsync(ProtocolConstants.FrameType.ExitConfig, [], token); }
            catch (InvalidOperationException) { /* 已在 GAME/其他态：继续尝试读取流 */ }
            IsBusy = false; StatusMessage = Localizer.Instance["Msg.GameRunning"]; RefreshCommands();
            var rateTimer = ReportGameRateAsync(token);
            try { await transport.StreamGameFramesAsync(ApplyGameBits, token); }
            finally { try { await rateTimer; } catch (OperationCanceledException) { } }
        }
        catch (OperationCanceledException) { StatusMessage = Localizer.Instance["Msg.GameStopped"]; }
        catch (IOException ex) { await MarkTransportLostAsync(); StatusMessage = Localizer.Instance["Msg.GameFailed"] + UserMessage(ex); }
        catch (Exception ex) { if (transport?.State == TransportState.Disconnected) await MarkTransportLostAsync(); StatusMessage = Localizer.Instance["Msg.GameFailed"] + UserMessage(ex); }
        finally { gameCancellation?.Dispose(); gameCancellation = null; IsBusy = false; GameFrameRate = 0; RefreshCommands(); }
    }

    private void ApplyGameBits(ulong bits)
    {
        for (var i = 0; i < TouchKeys.Count; i++) TouchKeys[i].IsPressed = (bits & (1UL << (33 - i))) != 0;
        gameFrameCount++;
    }

    private async Task ReportGameRateAsync(CancellationToken token)
    {
        var windowStart = DateTimeOffset.UtcNow;
        var lastCount = 0L;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(500, token);
            var now = DateTimeOffset.UtcNow;
            var count = Interlocked.Read(ref gameFrameCount);
            var elapsed = (now - windowStart).TotalSeconds;
            if (elapsed > 0) GameFrameRate = (count - lastCount) / elapsed;
            windowStart = now; lastCount = count;
        }
    }

    private async Task StopGameModeAsync()
    {
        gameCancellation?.Cancel();
        // 回到配置模式；容忍 AlreadyConfigured（板子可能仍在配置态）。
        if (workflow is not null)
        {
            try { await EnterConfigAsync(CancellationToken.None); } catch { }
        }
        await Task.CompletedTask;
    }

    private async Task<ConfigBlob> ReadConfigAsync(CancellationToken cancellationToken = default)
    {
        var ack = ReadAck(await workflow!.RequestAsync(ProtocolConstants.FrameType.GetConfig, [], cancellationToken), ProtocolConstants.FrameType.GetConfig);
        if (ack.Result.Length != ProtocolConstants.ConfigBlobLength) throw new FormatException("GET_CFG returned a malformed configuration.");
        return ConfigBlob.Decode(ack.Result);
    }

    private async Task ExpectAckAsync(ProtocolConstants.FrameType type, CancellationToken cancellationToken = default) => ReadAck(await workflow!.RequestAsync(type, [], cancellationToken), type);

    // 进入配置模式：正常返回 ACK；若板子已在配置模式（例如上次工具被强杀后残留），
    // 固件会回 AlreadyConfigured 错误——这对我们同样是“可配置”状态，应视为成功继续。
    private async Task EnterConfigAsync(CancellationToken cancellationToken)
    {
        var frame = await workflow!.RequestAsync(ProtocolConstants.FrameType.EnterConfig, [], cancellationToken);
        if (frame.Type == (byte)ProtocolConstants.FrameType.Error)
        {
            var error = ErrorPayload.Decode(frame.Payload);
            if ((ProtocolConstants.ErrorStatus)error.Status == ProtocolConstants.ErrorStatus.AlreadyConfigured) return;
            throw new InvalidOperationException($"{ProtocolConstants.FrameType.EnterConfig} rejected: {Enum.GetName((ProtocolConstants.ErrorStatus)error.Status) ?? "unknown error"}.");
        }
        ReadAck(frame, ProtocolConstants.FrameType.EnterConfig);
    }

    // 上次工具被强杀可能让板子残留 STREAMING/CALIBRATING 模式，此时 GET_CFG 会被固件以 Busy/IllegalState 拒绝。
    // 校准刚结束时固件可能尚未执行到 mode=CONFIG_IDLE（CAL_DONE 先发，收尾在其后），因此这里做退避重试：
    // 先尝试 STREAM_STOP 让其回 CONFIG_IDLE，若仍被拒则短暂等待后重试若干次。
    private async Task<ConfigBlob> ReadConfigWithRecoveryAsync(CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                return await ReadConfigAsync(cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                last = ex;
                try { await workflow!.RequestAsync(ProtocolConstants.FrameType.StreamStop, [], cancellationToken); } catch { }
                // 固件校准收尾（cancel + mode 切换）在 CAL_DONE 之后，给它一点时间稳定到 CONFIG_IDLE。
                try { await Task.Delay(120, cancellationToken); } catch (OperationCanceledException) { throw last; }
            }
        }
        throw last ?? new InvalidOperationException("GET_CFG could not be read back.");
    }
    private static AckPayload ReadAck(ProtocolFrame frame, ProtocolConstants.FrameType expected)
    {
        if (frame.Type == (byte)ProtocolConstants.FrameType.Error) { var error = ErrorPayload.Decode(frame.Payload); throw new InvalidOperationException($"{expected} rejected: {Enum.GetName((ProtocolConstants.ErrorStatus)error.Status) ?? "unknown error"}."); }
        if (frame.Type != (byte)ProtocolConstants.FrameType.Ack) throw new FormatException("Malformed response type.");
        var ack = AckPayload.Decode(frame.Payload);
        if (ack.RequestType != (byte)expected || ack.Status != 0) throw new FormatException($"Malformed {expected} acknowledgement.");
        return ack;
    }
    private string ConfigPath(DeviceIdentity identity) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaimaiTouchTool", identity.StableKey.Replace(':', '_') + ".json");
    private static string UserMessage(Exception exception) => exception switch { OperationCanceledException => Localizer.Instance["Msg.Timeout"], IOException => Localizer.Instance["Msg.Disconnected"], FormatException => exception.Message, _ => exception.Message };
    // 释放当前传输：DisconnectAsync 让端口回到断开态，DisposeAsync 真正归还 COM 句柄。
    // 只 Close 不 Dispose 会导致句柄滞留，再次连接时报 "Access to the path 'COMx' is denied"。
    private async Task DisconnectTransportAsync()
    {
        if (transport is not null) { try { await transport.DisconnectAsync(); } catch { } }
        if (transport is IAsyncDisposable disposable) { try { await disposable.DisposeAsync(); } catch { } }
        transport = null; client = null; workflow = null;
    }
    public async ValueTask DisposeAsync() { NumericInputNotifier.InvalidNumberEntered -= OnInvalidNumericInput; toastCancellation?.Cancel(); toastCancellation?.Dispose(); connectCancellation?.Cancel(); calibrationCancellation?.Cancel(); streamCancellation?.Cancel(); gameCancellation?.Cancel(); mappingCancellation?.Cancel(); guidedCancellation?.Cancel(); await StopMcpServerAsync(); if (IsConnected && workflow is not null) { try { await workflow.RequestAsync(ProtocolConstants.FrameType.ExitConfig, []); } catch { } } await DisconnectTransportAsync(); }
}
