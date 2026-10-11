using System;
using System.IO;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

/// <summary>One local exact-environment hint. It changes retest order, never skips validation.</summary>
public static class BleAdaptivePresetStore
{
    private static readonly object FileGate = new();
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PRO2WirelessReceiverControlBoard", "ble-adaptive-preset-v1.json");

    public static BleAdaptiveCachedPreset? Load(BleAdaptiveEnvironmentKey? environment, out string code, string? path = null)
    {
        code = "cache_environment_unavailable";
        if (environment == null) return null;
        lock (FileGate)
        {
            try
            {
                var file = new FileInfo(path ?? DefaultPath);
                if (!file.Exists) { code = "cache_absent"; return null; }
                if (file.Length > 65536) { code = "cache_oversized"; return null; }
                var entry = JsonSerializer.Deserialize<BleAdaptiveCachedPreset>(File.ReadAllText(file.FullName));
                return entry != null && BleAdaptivePresetPolicy.IsCacheUsable(entry, environment, DateTimeOffset.UtcNow, out code)
                    ? entry : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
            { code = "cache_read_" + ex.GetType().Name; return null; }
        }
    }

    public static void Save(BleAdaptiveCachedPreset entry, string? path = null)
    {
        if (!BleAdaptivePresetPolicy.IsCacheUsable(entry, entry.Environment, DateTimeOffset.UtcNow, out _))
            throw new ArgumentException("Only fully measured exact-environment preferences may be stored.", nameof(entry));
        lock (FileGate)
        {
            string target = Path.GetFullPath(path ?? DefaultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry));
            File.Move(temp, target, overwrite: true);
        }
    }

    public static void Invalidate(BleAdaptiveEnvironmentKey? environment, string? path = null)
    {
        if (environment == null) return;
        lock (FileGate)
        {
            var entry = Load(environment, out _, path);
            // Never remove a hint for another environment.
            if (entry != null) File.Delete(path ?? DefaultPath);
        }
    }
}
