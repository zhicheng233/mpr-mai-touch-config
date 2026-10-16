using Avalonia;
using MaimaiTouchTool.Mcp;

namespace MaimaiTouchTool;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // --mcp：以 MCP stdio 服务器模式运行，供 AI 模型接入做全自动调试/配置（不启动 GUI）。
        if (args.Contains("--mcp"))
        {
            McpServer.RunAsync().GetAwaiter().GetResult();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
