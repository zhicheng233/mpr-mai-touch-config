using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.Transport;
using MaimaiTouchTool.ViewModels;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(MaimaiTouchTool.Tests.UiTestApp))]

namespace MaimaiTouchTool.Tests;

public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class StreamingUiTests
{
    // One application lifetime: Localizer owns a process-wide ResourceDictionary.
    // Keep these dispatcher tests in their own process, separate from model-only tests.
    [AvaloniaFact(Timeout = 20000)]
    public async Task DebugAndGuidedStreamingUseTheRealUiDispatcherWithoutFreezing()
    {
        await UiCommandCanStartStopRestartSwitchTabsAndDisconnectWhileStreaming();
        await GuidedCaptureAndDiagnosticsRemainResponsiveAndCollapsedByDefault();
    }

    private static async Task UiCommandCanStartStopRestartSwitchTabsAndDisconnectWhileStreaming()
    {
        var endpoints = new List<FakeSerialEndpoint>();
        await using var vm = new MainViewModel(_ =>
        {
            var endpoint = new FakeSerialEndpoint();
            endpoints.Add(endpoint);
            var t = new SerialTransport(endpoint);
            return (t, t);
        });
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            vm.Ports.Add("COM_UI_STREAM_TEST");
            vm.SelectedPort = "COM_UI_STREAM_TEST";
            await vm.RunConnectForTestAsync();
            Assert.True(vm.IsConnected, vm.StatusMessage);
            var endpoint = endpoints[^1];
            var tabs = window.GetLogicalDescendants().OfType<TabControl>().First();
            // UI-bound notifications must never escape onto the serial reader thread.
            foreach (var model in new NotifyModel[] { vm.SignalChart, vm.Telemetry, vm.TouchKeys[0] })
                model.PropertyChanged += (_, _) => Assert.True(Dispatcher.UIThread.CheckAccess());

            for (var round = 0; round < 3; round++)
            {
                Assert.True(vm.StartStreamCommand.CanExecute(null));
                vm.StartStreamCommand.Execute(null); // Actual command path, not Task.Run or a direct callback.
                await Until(() => endpoint.IsStreaming);
                endpoint.InjectDebug(1, Frame(0, 1));
                await Until(() => vm.SignalChart.Count == 1);
                Assert.True(vm.TouchKeys[0].IsPressed);
                Assert.Equal((ushort)100, vm.Telemetry.AcceptedRateHz);
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, Localizer.Instance["Chart.Tab"]));
                for (ushort i = 2; i <= 100; i++) endpoint.InjectDebug(i, Frame((uint)i * 10, 0));
                await Until(() => vm.SignalChart.Count == 100);
                // A dispatcher heartbeat must run while frames and chart updates are active.
                await Dispatcher.UIThread.InvokeAsync(() => Assert.True(vm.IsStreaming), DispatcherPriority.Input);
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, Localizer.Instance["Tab.Calibration"]));
                vm.StopStreamCommand.Execute(null);
                await Until(() => vm.StartStreamCommand.CanExecute(null));
                Assert.False(endpoint.IsStreaming);
                Assert.Equal(TelemetryHealth.Idle, vm.Telemetry.Health);
                Assert.All(vm.TouchKeys, key => Assert.False(key.IsPressed));
                await vm.RunApplyForTestAsync();
                Assert.True(vm.IsConnected, vm.StatusMessage);
            }

            vm.StartStreamCommand.Execute(null);
            await Until(() => endpoint.IsStreaming);
            endpoint.InjectDebug(1, Frame(0, 1));
            await Until(() => vm.SignalChart.Count == 1);
            await vm.RunDisconnectForTestAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(vm.IsConnected);
            Assert.False(endpoint.IsStreaming);
            Assert.All(vm.TouchKeys, key => Assert.False(key.IsPressed));
            await vm.RunConnectForTestAsync();
            Assert.True(vm.IsConnected, vm.StatusMessage);
            vm.StartStreamCommand.Execute(null);
            await Until(() => endpoints[^1].IsStreaming);
            endpoints[^1].InjectDebug(1, Frame(0));
            await Until(() => vm.SignalChart.Count == 1);
            endpoints[^1].Disconnect(); // Unplug path, not just an explicit UI disconnect.
            await Until(() => !vm.IsConnected && !vm.IsStreaming);
            Assert.Equal(TelemetryHealth.Disconnected, vm.Telemetry.Health);
            Assert.True(vm.ConnectCommand.CanExecute(null));
            await vm.RunConnectForTestAsync();
            Assert.True(vm.IsConnected, vm.StatusMessage);
            await vm.RunDisconnectForTestAsync();
        }
        finally { window.Close(); }
    }

    private static async Task GuidedCaptureAndDiagnosticsRemainResponsiveAndCollapsedByDefault()
    {
        var endpoint = new FakeSerialEndpoint();
        await using var vm = new MainViewModel(_ => { var t = new SerialTransport(endpoint); return (t, t); });
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            vm.Ports.Add("COM_UI_GUIDED_TEST");
            vm.SelectedPort = "COM_UI_GUIDED_TEST";
            await vm.RunConnectForTestAsync();
            var tabs = window.GetLogicalDescendants().OfType<TabControl>().First();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, Localizer.Instance["Tab.Calibration"]));
            Assert.False(window.FindControl<Expander>("CalibrationDiagnostics")!.IsExpanded);
            Assert.False(window.FindControl<Expander>("CalibrationAdvanced")!.IsExpanded);
            var autoCalibration = window.FindControl<Expander>("CalibrationBaseline")!;
            Assert.True(autoCalibration.IsExpanded);
            Assert.Same(autoCalibration, Assert.IsType<StackPanel>(autoCalibration.Parent).Children[1]);
            vm.StartGuidedCommand.Execute(null);
            await Until(() => endpoint.IsStreaming);
            for (ushort i = 1; i <= 10; i++) endpoint.InjectDebug(i, Frame((uint)i * 30));
            await Until(() => vm.SignalChart.Count == 10);
            window.FindControl<Expander>("CalibrationDiagnostics")!.IsExpanded = true;
            await Dispatcher.UIThread.InvokeAsync(() => Assert.True(vm.IsGuidedRunning), DispatcherPriority.Input);
            await vm.RunCancelGuidedForTestAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(vm.IsGuidedRunning);
            Assert.False(endpoint.IsStreaming);
            Assert.True(vm.StartStreamCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    private static DebugFrame Frame(uint timestamp, ulong bits = 0) => new(timestamp, bits,
        Enumerable.Repeat((ushort)700, 48).ToArray(), Enumerable.Repeat((ushort)720, 48).ToArray(), [7, 7, 7, 7], 0);

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
