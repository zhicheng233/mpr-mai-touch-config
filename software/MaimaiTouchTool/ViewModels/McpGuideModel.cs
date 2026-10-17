using System.Reflection;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Mcp;

namespace MaimaiTouchTool.ViewModels;

/// <summary>
/// 「MCP 接入」引导页的数据源：把 AI 客户端接入本工具所需的启动命令、
/// 配置片段和工具清单集中在一处生成，避免文档与代码漂移。
/// 工具清单直接读 <see cref="McpToolHost.Describe"/>，不是手抄的。
/// </summary>
public sealed class McpGuideModel : NotifyModel
{
    /// <summary>接入方式。<see cref="Embedded"/> 用界面自己那条串口，不需要关程序。</summary>
    public enum ClientKind { EmbeddedOpenCode, EmbeddedMcpServers, OpenCode, ClaudeDesktop, Cursor }

    private ClientKind client = ClientKind.EmbeddedOpenCode;
    private bool copied;
    private bool serverRunning;
    private int port = McpHttpHost.DefaultPort;

    public McpGuideModel()
    {
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ClientLabels));
            OnPropertyChanged(nameof(SelectedClientIndex));            OnPropertyChanged(nameof(ConfigSnippet));
            OnPropertyChanged(nameof(ConfigPathHint));
            OnPropertyChanged(nameof(ConfigLabel));
            OnPropertyChanged(nameof(TestCommand));
            OnPropertyChanged(nameof(CopyLabel));
            OnPropertyChanged(nameof(ModeLabel));
            OnPropertyChanged(nameof(ModeHint));
            OnPropertyChanged(nameof(ShowStartServerWarning));
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

    /// <summary>下拉框里的接入方式；文案走本地化字典，切语言时会一起变。</summary>
    public static IReadOnlyList<string> ClientKeys =>
    [
        "Mcp.ClientEmbeddedOpenCode",
        "Mcp.ClientEmbeddedMcpServers",
        "Mcp.ClientOpenCode",
        "Mcp.ClientClaude",
        "Mcp.ClientCursor",
    ];

    public static IReadOnlyList<string> ClientNames =>
        ClientKeys.Select(key => Localizer.Instance[key]).ToArray();

    /// <summary>内嵌端点的实际端口；界面启动服务器后回填真实值。</summary>
    public int EndpointPort
    {
        get => port;
        set
        {
            var clamped = value is > 0 and < 65536 ? value : McpHttpHost.DefaultPort;
            if (clamped == port) return;
            port = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Endpoint));
            OnPropertyChanged(nameof(ConfigSnippet));
            OnPropertyChanged(nameof(TestCommand));
        }
    }

    public string Endpoint => $"http://127.0.0.1:{port}/mcp";

    /// <summary>内嵌端点是否在监听；由 MainViewModel 回填。</summary>
    public bool IsServerRunning
    {
        get => serverRunning;
        set
        {
            if (serverRunning == value) return;
            serverRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowStartServerWarning));
        }
    }

    /// <summary>选了内嵌但服务器没开：片段里的端口是死的，得先提醒去点启动。</summary>
    public bool ShowStartServerWarning => IsEmbedded && !serverRunning;

    /// <summary>当前选中的是不是内嵌（复用 GUI 串口）方式。</summary>
    public bool IsEmbedded => Client is ClientKind.EmbeddedOpenCode or ClientKind.EmbeddedMcpServers;

    public ClientKind Client
    {
        get => client;
        set
        {
            if (client == value) return;
            client = value;
            copied = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmbedded));
            OnPropertyChanged(nameof(ConfigSnippet));
            OnPropertyChanged(nameof(ConfigPathHint));
            OnPropertyChanged(nameof(ConfigLabel));
            OnPropertyChanged(nameof(TestCommand));
            OnPropertyChanged(nameof(ModeLabel));
            OnPropertyChanged(nameof(ModeHint));
            OnPropertyChanged(nameof(ShowStartServerWarning));
        }
    }

    public IReadOnlyList<string> ClientLabels => ClientNames;
    public int SelectedClientIndex
    {
        get => (int)client;
        set => Client = (ClientKind)Math.Clamp(value, 0, ClientNames.Count - 1);
    }

    /// <summary>当前方式的一句话说明：内嵌不用关程序，独立进程要断开串口。</summary>
    public string ModeLabel => IsEmbedded
        ? Localizer.Instance["Mcp.ModeEmbedded"]
        : Localizer.Instance["Mcp.ModeStandalone"];

    public string ModeHint => IsEmbedded
        ? Localizer.Instance["Mcp.ModeEmbeddedHint"]
        : Localizer.Instance["Mcp.ModeStandaloneHint"];

    public string ConfigLabel => Client switch
    {
        ClientKind.EmbeddedOpenCode or ClientKind.OpenCode => Localizer.Instance["Mcp.ConfigLabelOpenCode"],
        ClientKind.EmbeddedMcpServers => Localizer.Instance["Mcp.ConfigLabelEmbedded"],
        ClientKind.ClaudeDesktop => Localizer.Instance["Mcp.ConfigLabelClaude"],
        _ => Localizer.Instance["Mcp.ConfigLabelCursor"],
    };

    public string ConfigPathHint => Client switch
    {
        ClientKind.EmbeddedOpenCode or ClientKind.OpenCode => Localizer.Instance["Mcp.PathOpenCode"],
        ClientKind.EmbeddedMcpServers => Localizer.Instance["Mcp.PathCursor"],
        ClientKind.ClaudeDesktop => "%APPDATA%\\Claude\\claude_desktop_config.json",
        _ => Localizer.Instance["Mcp.PathCursor"],
    };

    /// <summary>可直接粘贴到客户端配置里的 JSON 片段（已填入本机 exe 路径或内嵌端点）。</summary>
    public string ConfigSnippet => Client switch
    {
        ClientKind.EmbeddedOpenCode =>
            "{\n" +
            "  \"$schema\": \"https://opencode.ai/config.json\",\n" +
            "  \"mcp\": {\n" +
            "    \"maimai-touch\": {\n" +
            "      \"type\": \"remote\",\n" +
            $"      \"url\": \"{Endpoint}\",\n" +
            "      \"enabled\": true\n" +
            "    }\n" +
            "  }\n" +
            "}",
        ClientKind.EmbeddedMcpServers =>
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"maimai-touch\": {\n" +
            "      \"type\": \"http\",\n" +
            $"      \"url\": \"{Endpoint}\"\n" +
            "    }\n" +
            "  }\n" +
            "}",
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

    /// <summary>自检脚本：内嵌方式用 HTTP 探一下，独立进程方式喂 stdin。</summary>
    public string TestCommand => IsEmbedded
        ? $"$body = '{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}}'\n" +
          $"Invoke-RestMethod -Uri \"{Endpoint}\" -Method Post -ContentType \"application/json\" -Body $body |\n" +
          "  ConvertTo-Json -Depth 6"
        : $"$exe = \"{ExecutablePath}\"\n" +
          "$dir = \"$env:TEMP\\mcp_probe\"; New-Item -ItemType Directory -Force $dir | Out-Null\n" +
          "@'\n" +
          "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"probe\",\"version\":\"1\"}}}\n" +
          "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
          "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n" +
          "'@ | Set-Content \"$dir\\in.txt\" -Encoding utf8\n" +
          "cmd /c \"`\"$exe`\" --mcp < `\"$dir\\in.txt`\" > `\"$dir\\out.txt`\"\"\n" +
          "Get-Content \"$dir\\out.txt\"";

    public IReadOnlyList<McpToolRow> Tools { get; } = McpToolHost.Describe()
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
/// 工具表的一行。名字来自 <see cref="McpToolHost.Describe"/>（保证不会漏）。
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
