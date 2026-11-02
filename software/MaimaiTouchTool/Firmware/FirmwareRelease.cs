using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace MaimaiTouchTool.Firmware;

/// <summary>
/// 从 GitHub Releases 查「最新固件版本号」。
///
/// 只读 tag_name 和 assets，不下载二进制 —— 界面上要显示的是版本号，
/// 真正刷写用的是本地编译出来的 bin（同一个源码树，版本号同源）。
///
/// GitHub Releases 同时包含 firmware-v* 与 tool-v*；这里只选固件标签，
/// 避免桌面工具发布覆盖固件更新检查结果。
/// </summary>
public static class FirmwareRelease
{
    public const string Owner = "zhicheng233";

    public const string Repository = "mpr-mai-touch-config";

    public const string FirmwareTagPrefix = "firmware-";
    public const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=100";

    public static bool IsConfigured => true;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API 要求带 UA，否则 403
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MaimaiTouchTool", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// 查最新 release。Manifest 为 null 表示查不到（网络、限流、404、JSON 不合规都算），
    /// 具体原因放在 Error 里给界面显示。
    /// </summary>
    public static async Task<(FirmwareManifest? Manifest, string Error)> FetchLatestAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return (null, "no release repository configured");

        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var releases = JsonNode.Parse(text) as JsonArray;
            if (releases is null)
                return (null, "release payload was not a JSON array");

            foreach (var node in releases.OfType<JsonObject>())
            {
                var tag = node["tag_name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(tag) ||
                    !tag.StartsWith(FirmwareTagPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var version = FirmwareVersion.Parse(tag[FirmwareTagPrefix.Length..]);
                if (!version.IsKnown)
                    continue;

                // 有 firmware.bin 资产就顺带记下大小，没有也不影响显示版本号。
                long? size = null;
                string? binary = null;
                if (node["assets"] is JsonArray assets)
                {
                    foreach (var asset in assets.OfType<JsonObject>())
                    {
                        var name = asset["name"]?.GetValue<string>();
                        if (string.Equals(name, "firmware.bin", StringComparison.OrdinalIgnoreCase))
                        {
                            binary = name;
                            size = asset["size"] is { } s ? s.GetValue<long>() : null;
                            break;
                        }
                    }
                }

                return (new FirmwareManifest(
                    version.ToString(),
                    Protocol: null,
                    Board: null,
                    Binary: binary,
                    Size: size,
                    Source: node["html_url"]?.GetValue<string>() ?? LatestReleaseApi), string.Empty);
            }

            return (null, "no firmware-v* release with a valid version was found");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
