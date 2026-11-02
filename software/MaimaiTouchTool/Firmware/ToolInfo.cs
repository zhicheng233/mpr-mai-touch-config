using System.Reflection;

namespace MaimaiTouchTool.Firmware;

/// <summary>
/// 本工具的版本号。只有一个来源（csproj 的 &lt;Version&gt;），
/// 界面上的"本工具"和 MCP 握手里的 serverInfo 都读这里，
/// 免得两处各写一个数字然后慢慢漂开。
/// </summary>
public static class ToolInfo
{
    /// <summary>程序集 informational version；CI 注入的 +g<短哈希> 会显示出来。</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(ToolInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // 普通本地构建可能由 SDK 自动追加完整源码哈希，不把它当产品版本显示。
                // CI 注入的 tag+短哈希使用 +g 前缀，需要展示给用户。
                var buildMetadata = info.IndexOf('+');
                return buildMetadata > 0 && info.AsSpan(buildMetadata + 1).StartsWith("g", StringComparison.Ordinal)
                    ? info
                    : buildMetadata > 0 ? info[..buildMetadata] : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
}
