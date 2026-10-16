using System.Text.Json;
using System.Text.Json.Serialization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.Persistence;

public sealed record DeviceIdentity(string Endpoint, string? UsbSerial, byte ProtocolMajor = 1, byte ProtocolMinor = 0)
{
    public string StableKey => UsbSerial is { Length: > 0 } ? $"serial:{UsbSerial}" : $"endpoint:{Endpoint}";
}
public sealed record ConfigStatus(bool StagedDirty, bool AppliedDiffersFromPersisted);
public sealed record ConfigDocument(DeviceIdentity Device, ConfigBlob Config, ConfigStatus Status)
{
    public static ConfigDocument Create(DeviceIdentity device) => new(device, ConfigBlob.Defaults(), new(false, true));
}
public static class ConfigJsonStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static async Task SaveAtomicAsync(string path, ConfigDocument document, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path); var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Path must include a directory.", nameof(path)); Directory.CreateDirectory(directory); var temp = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, Options), cancellationToken); File.Move(temp, fullPath, true);
    }
    public static async Task<ConfigDocument> LoadAsync(string path, CancellationToken cancellationToken = default) => JsonSerializer.Deserialize<ConfigDocument>(await File.ReadAllTextAsync(path, cancellationToken), Options) ?? throw new FormatException("Config JSON is empty.");
    public static bool IsConflict(ConfigDocument local, DeviceIdentity device, ConfigBlob appliedFromDevice) => local.Device.StableKey != device.StableKey || !local.Config.Encode().AsSpan().SequenceEqual(appliedFromDevice.Encode());
}
