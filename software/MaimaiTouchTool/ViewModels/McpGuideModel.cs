using System.Reflection;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Mcp;

namespace MaimaiTouchTool.ViewModels;

/// <summary>
/// 「MCP 接入」引导页的数据源：把 AI 客户端接入本工具所需的启动命令、
/// 配置片段和工具清单集中在一处生成，避免文档与代码漂移。
/// 工具清单直接读 <see cref="McpServer.DescribeTools"/>，不是手抄的。
/// </summary>
public sealed class McpGuideModel : NotifyModel
{
    public enum ClientKind { OpenCode, ClaudeDesktop, Cursor }

    private ClientKind client = ClientKind.OpenCode;
    private bool copied;

    public McpGuideModel()
    {
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ClientLabels));
            OnPropertyChanged(nameof(SelectedClientIndex));
            OnPropertyChanged(nameof(ConfigSnippet));
            OnPropertyChanged(nameof(TestCommand));
            OnPropertyChanged(nameof(CopyLabel));
            foreach (var tool in Tools) tool.RaiseLocalized();
        };
    }

    /// <summary>当前进程的 exe 绝对路径；设计器/单元测试宿主下会是测试宿主路径。</summary>
    public static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return path;
            // 兜底：单文件/dotnet 宿主场景下退回程序集位置
            var asm = Assembly.GetEntryAssembly()?.Location;
            return string.IsNullOrEmpty(asm) ? "MaimaiTouchTool.exe" : Path.ChangeExtension(asm, ".exe");
        }
    }

    /// <summary>XAML 绑定用（静态属性无法直接绑定）。</summary>
    public string Executable => ExecutablePath;

    /// <summary>在配置文件里要写的路径（JSON 需要转义反斜杠）。</summary>
    public static string EscapedPath => ExecutablePath.Replace("\\", "\\\\");

    public static IReadOnlyList<string> ClientNames =>
    [
        "OpenCode",
        "Claude Desktop",
        "Cursor / mcpServers",
    ];

    public ClientKind Client
    {
        get => client;
        set
        {
            if (client == value) return;
            client = value;
            copied = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConfigSnippet));
            OnPropertyChanged(nameof(ConfigPathHint));
            OnPropertyChanged(nameof(ConfigLabel));
            OnPropertyChanged(nameof(TestCommand));
        }
    }

    public IReadOnlyList<string> ClientLabels => ClientNames;

    public int SelectedClientIndex
    {
        get => (int)client;
        set => Client = (ClientKind)Math.Clamp(value, 0, ClientNames.Count - 1);
    }

    public string ConfigLabel => Client switch
    {
        ClientKind.OpenCode => Localizer.Instance["Mcp.ConfigLabelOpenCode"],
        ClientKind.ClaudeDesktop => Localizer.Instance["Mcp.ConfigLabelClaude"],
        _ => Localizer.Instance["Mcp.ConfigLabelCursor"],
    };

    public string ConfigPathHint => Client switch
    {
        ClientKind.OpenCode => Localizer.Instance["Mcp.PathOpenCode"],
        ClientKind.ClaudeDesktop => "%APPDATA%\\Claude\\claude_desktop_config.json",
        _ => Localizer.Instance["Mcp.PathCursor"],
    };

    /// <summary>可直接粘贴到客户端配置里的 JSON 片段（已填入本机 exe 路径）。</summary>
    public string ConfigSnippet => Client switch
    {
        ClientKind.OpenCode =>
            "{\n" +
            "  \"$schema\": \"https://opencode.ai/config.json\",\n" +
            "  \"mcp\": {\n" +
            "    \"maimai-touch\": {\n" +
            "      \"type\": \"local\",\n" +
            "      \"command\": [\n" +
            $"        \"{EscapedPath}\",\n" +
            "        \"--mcp\"\n" +
            "      ],\n" +
            "      \"enabled\": true\n" +
            "    }\n" +
            "  }\n" +
            "}",
        _ =>
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"maimai-touch\": {\n" +
            $"      \"command\": \"{EscapedPath}\",\n" +
            "      \"args\": [\"--mcp\"]\n" +
            "    }\n" +
            "  }\n" +
            "}",
    };

    /// <summary>自检脚本：不装客户端也能确认服务器能起来并列出工具。</summary>
    public string TestCommand
    {
        get
        {
            var exe = ExecutablePath;
            return
                $"$exe = \"{exe}\"\n" +
                "$dir = \"$env:TEMP\\mcp_probe\"; New-Item -ItemType Directory -Force $dir | Out-Null\n" +
                "@'\n" +
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"probe\",\"version\":\"1\"}}}\n" +
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n" +
                "'@ | Set-Content \"$dir\\in.txt\" -Encoding utf8\n" +
                "cmd /c \"`\"$exe`\" --mcp < `\"$dir\\in.txt`\" > `\"$dir\\out.txt`\"\"\n" +
                "Get-Content \"$dir\\out.txt\"";
        }
    }

    public IReadOnlyList<McpToolRow> Tools { get; } = McpServer.DescribeTools()
        .Select(t => new McpToolRow(t.Name, t.Description))
        .ToArray();

    public bool Copied
    {
        get => copied;
        set { copied = value; OnPropertyChanged(); OnPropertyChanged(nameof(CopyLabel)); }
    }

    public string CopyLabel => copied ? Localizer.Instance["Mcp.Copied"] : Localizer.Instance["Mcp.Copy"];
}

/// <summary>
/// 工具表的一行。名字来自 <see cref="McpServer.DescribeTools"/>（保证不会漏）。
/// 说明走本地化字典 <c>Mcp.Tool.{name}</c>；缺键时回退到 MCP 定义里的原文，
/// 这样新增工具哪怕忘了补翻译也还是能显示，不会出现空行。
/// </summary>
public sealed class McpToolRow : NotifyModel
{
    private readonly string fallback;

    public McpToolRow(string name, string fallback)
    {
        Name = name;
        this.fallback = fallback;
    }

    public string Name { get; }

    public string Description
    {
        get
        {
            var key = "Mcp.Tool." + Name;
            var text = Localizer.Instance[key];
            return text == key ? fallback : text;
        }
    }

    internal void RaiseLocalized() => OnPropertyChanged(nameof(Description));
}
