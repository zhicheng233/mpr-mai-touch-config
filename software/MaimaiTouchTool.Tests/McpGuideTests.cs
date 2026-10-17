using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Mcp;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

/// <summary>
/// 「MCP 接入」引导页的内容守卫：工具清单来自服务器定义而不是手抄，
/// 本地化键齐全，语言切换不残留上一门语言的文本。
/// </summary>
public sealed class McpGuideTests
{
    [Fact]
    public void GuideListsEveryToolTheServerActuallyExposes()
    {
        var guide = new McpGuideModel();
        var serverTools = McpToolHost.Describe().Select(t => t.Name).ToArray();

        Assert.NotEmpty(serverTools);
        Assert.Equal(serverTools, guide.Tools.Select(t => t.Name).ToArray());
        // tools/list 与工具表必须同源：界面列出的每一项模型都真的能调。
        Assert.Equal(serverTools, McpToolHost.ToolDefinitions().Select(t => t!["name"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void EveryToolHasALocalizedDescriptionInBothLanguages()
    {
        var guide = new McpGuideModel();
        try
        {
            foreach (var language in new[] { "en", "zh-Hans" })
            {
                Localizer.Instance.SetLanguage(language);
                foreach (var tool in guide.Tools)
                {
                    // 缺键会回退到 MCP 定义里的中文原文；这里要求确实命中字典。
                    var key = "Mcp.Tool." + tool.Name;
                    var expected = Localizer.Instance[key];
                    Assert.NotEqual(key, expected);
                    Assert.False(string.IsNullOrWhiteSpace(expected));
                    Assert.Equal(expected, tool.Description);
                }
            }
        }
        finally
        {
            Localizer.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public void EmbeddedSnippetWarnsUntilTheServerIsActuallyRunning()
    {
        var guide = new McpGuideModel { Client = McpGuideModel.ClientKind.EmbeddedOpenCode };
        Assert.True(guide.IsEmbedded);
        Assert.False(guide.IsServerRunning);
        // 服务器没开时片段指向一个死端口，界面必须先提醒去点启动。
        Assert.True(guide.ShowStartServerWarning);

        guide.IsServerRunning = true;
        Assert.False(guide.ShowStartServerWarning);

        // 切到独立进程模式就不该再提内嵌服务器。
        guide.Client = McpGuideModel.ClientKind.Cursor;
        Assert.False(guide.IsEmbedded);
        Assert.False(guide.ShowStartServerWarning);
    }

    [Fact]
    public void ClientLabelsAndModeTextAreLocalized()
    {
        try
        {
            foreach (var language in new[] { "en", "zh-Hans" })
            {
                Localizer.Instance.SetLanguage(language);
                foreach (var key in McpGuideModel.ClientKeys)
                {
                    var label = Localizer.Instance[key];
                    // 缺键时索引器会回显键名；那说明下拉框里会露出 "Mcp.ClientOpenCode" 这种文本。
                    Assert.NotEqual(key, label);
                    Assert.False(string.IsNullOrWhiteSpace(label));
                }

                var guide = new McpGuideModel();
                Assert.Equal(McpGuideModel.ClientKeys.Count, guide.ClientLabels.Count);
                foreach (var label in guide.ClientLabels) Assert.DoesNotContain("Mcp.", label);
            }
        }
        finally
        {
            Localizer.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public void SwitchingLanguageDoesNotLeaveStaleKeysBehind()
    {
        try
        {
            Localizer.Instance.SetLanguage("en");
            var englishKeys = Localizer.Instance.Resources.Keys.Cast<string>().ToHashSet();
            var englishStep = Localizer.Instance.Resources["Mcp.StepConfig"];

            Localizer.Instance.SetLanguage("zh-Hans");
            var chineseKeys = Localizer.Instance.Resources.Keys.Cast<string>().ToHashSet();
            var chineseStep = Localizer.Instance.Resources["Mcp.StepConfig"];

            Assert.NotEqual(englishStep, chineseStep);
            // 同一批键都应还在（两个语言表必须键集合一致），且没有多余残留。
            Assert.Equal(englishKeys, chineseKeys);
        }
        finally
        {
            Localizer.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public void BothLanguageTablesDefineTheSameKeys()
    {
        var english = new HashSet<string>();
        var chinese = new HashSet<string>();
        try
        {
            Localizer.Instance.SetLanguage("en");
            foreach (var key in Localizer.Instance.Resources.Keys.Cast<string>()) english.Add(key);
            Localizer.Instance.SetLanguage("zh-Hans");
            foreach (var key in Localizer.Instance.Resources.Keys.Cast<string>()) chinese.Add(key);
        }
        finally
        {
            Localizer.Instance.SetLanguage("en");
        }

        Assert.Empty(english.Except(chinese));
        Assert.Empty(chinese.Except(english));
    }

    [Fact]
    public void OpenCodeSnippetUsesLocalTypeWithMcpArgument()
    {
        var guide = new McpGuideModel { Client = McpGuideModel.ClientKind.OpenCode };
        var snippet = guide.ConfigSnippet;

        Assert.Contains("\"type\": \"local\"", snippet);
        Assert.Contains("\"--mcp\"", snippet);
        Assert.Contains("maimai-touch", snippet);
        // JSON 里的路径必须转义反斜杠，否则客户端解析不出正确路径。
        Assert.DoesNotContain("\\(", snippet);
        foreach (var segment in McpGuideModel.ExecutablePath.Split('\\'))
        {
            if (segment.Length > 0) Assert.Contains(segment, snippet);
        }
    }

    [Fact]
    public void McpServersSnippetIsUsedForClaudeAndCursor()
    {
        var guide = new McpGuideModel();

        guide.Client = McpGuideModel.ClientKind.ClaudeDesktop;
        Assert.Contains("\"mcpServers\"", guide.ConfigSnippet);
        Assert.Contains("\"args\": [\"--mcp\"]", guide.ConfigSnippet);

        guide.Client = McpGuideModel.ClientKind.Cursor;
        Assert.Contains("\"mcpServers\"", guide.ConfigSnippet);
        Assert.Equal(guide.ConfigSnippet, new McpGuideModel { Client = McpGuideModel.ClientKind.ClaudeDesktop }.ConfigSnippet);
    }

    [Fact]
    public void TestCommandPointsAtThisExecutable()
    {
        var guide = new McpGuideModel { Client = McpGuideModel.ClientKind.OpenCode };
        Assert.Contains("--mcp", guide.TestCommand);
        Assert.Contains("tools/list", guide.TestCommand);
        Assert.Contains(McpGuideModel.ExecutablePath, guide.TestCommand);
    }

    [Fact]
    public void EmbeddedSnippetPointsAtTheLoopbackEndpoint()
    {
        var guide = new McpGuideModel { Client = McpGuideModel.ClientKind.EmbeddedOpenCode };
        Assert.True(guide.IsEmbedded);
        Assert.Contains("\"type\": \"remote\"", guide.ConfigSnippet);
        Assert.Contains(guide.Endpoint, guide.ConfigSnippet);
        // 内嵌方式不该出现 exe 路径：它连的是正在运行的这个窗口，不是新进程。
        Assert.DoesNotContain("--mcp", guide.ConfigSnippet);

        guide.Client = McpGuideModel.ClientKind.EmbeddedMcpServers;
        Assert.Contains("\"mcpServers\"", guide.ConfigSnippet);
        Assert.Contains(guide.Endpoint, guide.ConfigSnippet);
        // 内嵌自检走 HTTP，不该再提 exe 或 stdin 重定向。
        Assert.Contains("Invoke-RestMethod", guide.TestCommand);
        Assert.DoesNotContain("--mcp", guide.TestCommand);
    }

    [Fact]
    public void SwitchingBetweenEmbeddedAndStandaloneChangesTheModeText()
    {
        try
        {
            foreach (var language in new[] { "en", "zh-Hans" })
            {
                Localizer.Instance.SetLanguage(language);
                // 每次都从一个确定的状态出发，避免上轮循环把 Client 留在 standalone。
                var embedded = new McpGuideModel { Client = McpGuideModel.ClientKind.EmbeddedOpenCode };
                var standalone = new McpGuideModel { Client = McpGuideModel.ClientKind.OpenCode };

                Assert.True(embedded.IsEmbedded);
                Assert.False(standalone.IsEmbedded);
                Assert.NotEqual(standalone.ModeLabel, embedded.ModeLabel);
                Assert.NotEqual(standalone.ModeHint, embedded.ModeHint);
                // 本地化必须真的切到当前语言，不能回显键名。
                foreach (var text in new[] { embedded.ModeLabel, embedded.ModeHint, standalone.ModeLabel, standalone.ModeHint })
                    Assert.DoesNotContain("Mcp.", text);
            }
        }
        finally
        {
            Localizer.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public void EndpointPortFlowsIntoTheSnippet()
    {
        var guide = new McpGuideModel { Client = McpGuideModel.ClientKind.EmbeddedOpenCode };
        guide.EndpointPort = 9100;
        Assert.Equal("http://127.0.0.1:9100/mcp", guide.Endpoint);
        Assert.Contains("9100", guide.ConfigSnippet);
        Assert.Contains("9100", guide.TestCommand);
    }
}
