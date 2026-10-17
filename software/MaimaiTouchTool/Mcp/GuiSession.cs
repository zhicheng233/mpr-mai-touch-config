using Avalonia.Threading;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;

namespace MaimaiTouchTool.Mcp;

/// <summary>
/// GUI 内嵌模式下的会话：把 MCP 工具调用接到正在运行的主界面上。
/// 串口只有一条，由 GUI 持有；模型改的每一项都会立刻反映到界面（未应用标记、单键着色、映射板图），
/// 所以「AI 调参」和「人手动调参」用的是同一份状态，不存在两套配置互相覆盖的问题。
///
/// 所有调用都先切到 UI 线程：编辑器里的模型是绑定到界面的，Avalonia 会直接拒绝
/// 从 HTTP 线程改属性（<c>Call from invalid thread</c>）。
/// </summary>
public sealed class GuiSession(MainViewModel viewModel) : IMcpSession
{
    private readonly MainViewModel vm = viewModel;

    public bool IsConnected => vm.IsConnected;
    public string PortName => vm.ConnectedPort;
    public ConfigBlob AppliedConfig => OnUi(() => vm.AppliedConfig);
    public ConfigBlob StagedConfig => OnUi(() => vm.StagedConfig);

    public Task<string> ConnectAsync(string port, CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        if (vm.IsConnected)
        {
            if (string.Equals(vm.ConnectedPort, port, StringComparison.OrdinalIgnoreCase))
                return $"already connected to {port}";
            throw new InvalidOperationException(
                $"the GUI is already connected to {vm.ConnectedPort}; disconnect first");
        }
        // 走 GUI 的完整连接流程：版本校验、能力位检查、ENTER_CFG、GET_CFG、本地 JSON 冲突检测都照做。
        vm.SelectPort(port);
        await vm.McpConnectAsync().WaitAsync(cancellationToken);
        if (!vm.IsConnected) throw new InvalidOperationException(vm.StatusMessage);
        return $"connected to {port}, mode=config, boards=4, electrodes=34";
    });

    public Task<string> DisconnectAsync(CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        if (!vm.IsConnected) return "already disconnected";
        await vm.McpDisconnectAsync().WaitAsync(cancellationToken);
        return "disconnected";
    });

    public Task<string> ApplyAsync(CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        var applied = await vm.McpApplyAsync(cancellationToken).WaitAsync(cancellationToken);
        return $"applied & verified: {System.Text.Json.JsonSerializer.Serialize(McpToolHost.ConfigJson(applied))}";
    });

    public Task<string> SaveAsync(CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        var outcome = await vm.McpSaveAsync(cancellationToken).WaitAsync(cancellationToken);
        return outcome.MirrorError is null
            ? "saved to EEPROM"
            : $"saved to EEPROM, but the local JSON mirror could not be written: {outcome.MirrorError.Message}";
    });

    public Task<string> CalibrateAsync(CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        var client = vm.EventClient ?? throw new InvalidOperationException("not connected; call connect first");
        var (status, reason) = await vm.McpCalibrateAsync(client, cancellationToken).WaitAsync(cancellationToken);
        return status == CalibrationStatus.Success
            ? $"calibration succeeded (reason={reason})"
            : $"calibration failed (reason={reason})";
    });

    public Task<string> ReadTelemetryAsync(int durationMs, CancellationToken cancellationToken) =>
        OnUiAsync(() => vm.McpReadTelemetryAsync(durationMs, cancellationToken));

    public void StageBoard(int boardIndex, BoardEdit edit) => OnUi(() => vm.McpStageBoard(boardIndex, edit));

    public void StageElectrode(int keyIndex, int offset) => OnUi(() => vm.McpStageElectrode(keyIndex, offset));

    public void StageMapping(int keyIndex, int boardIndex, int channel, bool swap) =>
        OnUi(() => vm.McpStageMapping(keyIndex, boardIndex, channel, swap));

    /// <summary>在 UI 线程上同步执行（用于读属性、改编辑器这类短操作）。</summary>
    private static T OnUi<T>(Func<T> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.Invoke(action);

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }

    /// <summary>
    /// 在 UI 线程上执行一个异步操作并等它真正结束。
    /// 不能直接 <c>await Dispatcher.UIThread.InvokeAsync(async () =&gt; ...)</c>：
    /// 那个 lambda 是 async void，InvokeAsync 在第一个 await 处就返回了，await 会提前结束。
    /// </summary>
    private static async Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return await completion.Task;
    }
}
