using System.Reflection;

namespace MaimaiTouchTool.Firmware;

/// <summary>
/// 本工具的版本号。只有一个来源（csproj 的 &lt;Version&gt;），
/// 界面上的"本工具"和 MCP 握手里的 serverInfo 都读这里，
/// 免得两处各写一个数字然后慢慢漂开。
/// </summary>
public static class ToolInfo
{
    /// <summary>纯版本号，已去掉 SDK 追加的 +git-sha 后缀。</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(ToolInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
}
