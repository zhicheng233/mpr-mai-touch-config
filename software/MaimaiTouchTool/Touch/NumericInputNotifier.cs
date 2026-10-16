using Avalonia.Threading;

namespace MaimaiTouchTool.Touch;

/// <summary>
/// 数字输入非法时的全局通知：SafeIntConverter 解析失败会调用 <see cref="Report"/>，
/// MainWindow 订阅 <see cref="InvalidNumberEntered"/> 后从窗口底部中间弹出一条 toast 提示。
/// 变体较多（转换器无法直接访问 ViewModel），因此用一个极简单例做解耦。
/// </summary>
public static class NumericInputNotifier
{
    public static event EventHandler? InvalidNumberEntered;

    // 去抖：连续非法按键/粘贴只在窗口内提示一次，避免每个字符都弹。
    private static long lastTicks;
    private static readonly long DebounceTicks = TimeSpan.FromMilliseconds(800).Ticks;

    public static void Report()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref lastTicks) < DebounceTicks) return;
        Interlocked.Exchange(ref lastTicks, now);
        if (Dispatcher.UIThread.CheckAccess()) InvalidNumberEntered?.Invoke(null, EventArgs.Empty);
        else Dispatcher.UIThread.Post(() => InvalidNumberEntered?.Invoke(null, EventArgs.Empty));
    }
}
