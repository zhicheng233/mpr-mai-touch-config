using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaimaiTouchTool.Firmware;

/// <summary>
/// 固件语义版本。三段独立于协议版本：协议看 <see cref="ProtocolMajor"/>，
/// 固件改动看 major/minor/patch。
/// </summary>
public readonly record struct FirmwareVersion(int Major, int Minor, int Patch) : IComparable<FirmwareVersion>
{
    public static readonly FirmwareVersion Unknown = new(-1, -1, -1);

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    /// <summary>写进配置/日志的稳定形式，-1 段不参与比较。</summary>
    public bool IsKnown => Major >= 0 && Minor >= 0 && Patch >= 0;

    public int CompareTo(FirmwareVersion other)
    {
        // 未知版本永远排在已知版本之前：没读到版本号不该被当成"最新"。
        if (!IsKnown) return other.IsKnown ? -1 : 0;
        if (!other.IsKnown) return 1;
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;
        var byMinor = Minor.CompareTo(other.Minor);
        return byMinor != 0 ? byMinor : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) <= 0;

    /// <summary>解析 "1.2.3" / "v1.2.3" / "1.2"；失败返回 <see cref="Unknown"/>。</summary>
    public static FirmwareVersion Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Unknown;
        var cleaned = text.Trim().TrimStart('v', 'V');
        // 允许 "1.2.3-rc1" 这类后缀，只取前面的数字段
        var dash = cleaned.IndexOfAny(['-', '+']);
        if (dash >= 0) cleaned = cleaned[..dash];

        var parts = cleaned.Split('.');
        if (parts.Length < 2) return Unknown;

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length) { numbers[i] = 0; continue; }
            if (!int.TryParse(parts[i], out var value) || value < 0) return Unknown;
            numbers[i] = value;
        }
        return new FirmwareVersion(numbers[0], numbers[1], numbers[2]);
    }
}

/// <summary>
/// firmware.json 的内容。本地产物由 <c>firmware/extra_script.py</c> 在编译后生成，
/// 远端版本则从 GitHub Releases 的 release 描述里读（见 <see cref="FirmwareRelease"/>）。
/// </summary>
public sealed record FirmwareManifest(
    string Version,
    string? Protocol,
    string? Board,
    string? Binary,
    long? Size,
    string? Source)
{
    public FirmwareVersion Parsed => FirmwareVersion.Parse(Version);

    /// <summary>读本地 build 目录里那份 firmware.json；不存在返回 null（不是错误，可能还没编译）。</summary>
    public static FirmwareManifest? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node is null) return null;
            return FromJson(node, path);
        }
        catch (Exception)
        {
            // 清单坏了不该让整个界面崩掉；调用方会显示"读不到本地固件版本"
            return null;
        }
    }

    public static FirmwareManifest? FromJson(JsonObject node, string? source = null)
    {
        var version = node["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version)) return null;
        return new FirmwareManifest(
            version.Trim(),
            node["protocol"]?.GetValue<string>(),
            node["board"]?.GetValue<string>(),
            node["binary"]?.GetValue<string>(),
            node["size"] is { } size ? size.GetValue<long>() : null,
            source);
    }

    public static FirmwareManifest? Parse(string json, string? source = null)
    {
        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            return node is null ? null : FromJson(node, source);
        }
        catch (JsonException) { return null; }
    }
}
