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
        var serverTools = McpServer.DescribeTools().Select(t => t.Name).ToArray();

        Assert.NotEmpty(serverTools);
        Assert.Equal(serverTools, guide.Tools.Select(t => t.Name).ToArray());
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
        var guide = new McpGuideModel();
        Assert.Contains("--mcp", guide.TestCommand);
        Assert.Contains("tools/list", guide.TestCommand);
        Assert.Contains(McpGuideModel.ExecutablePath, guide.TestCommand);
    }
}
