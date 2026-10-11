using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

/// <summary>Reads saved measurements only. Never requests Bluetooth access or reapplies a preset.</summary>
public static class BleAdaptiveDiagnosticStore
{
    private const int MaximumFileBytes = 256 * 1024;
    public static string ReportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PRO2WirelessReceiverControlBoard", "v6_logs", "ble_optimizer");
    public static JsonSerializerOptions JsonOptions => new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static BleAdaptiveDiagnosticSnapshot? LoadLatest(out string warning, string? directory = null)
    {
        warning = "";
        try
        {
            string folder = directory ?? ReportDirectory;
            if (!Directory.Exists(folder)) return null;
            var files = new DirectoryInfo(folder).EnumerateFiles("presets_*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(10);
            foreach (FileInfo file in files)
            {
                try
                {
                    if (file.Length is <= 0 or > MaximumFileBytes) { warning = "部分历史文件过大或为空，已跳过。"; continue; }
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    byte[] bytes = new byte[MaximumFileBytes + 1];
                    int count = 0;
                    while (count < bytes.Length)
                    {
                        int read = stream.Read(bytes, count, bytes.Length - count);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count > MaximumFileBytes) { warning = "部分历史文件过大，已跳过。"; continue; }
                    int offset = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                    using JsonDocument json = JsonDocument.Parse(bytes.AsMemory(offset, count - offset));
                    if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (json.RootElement.TryGetProperty("diagnostics", out JsonElement stored))
                    {
                        if (stored.ValueKind != JsonValueKind.Object) continue;
                        BleAdaptiveDiagnosticSnapshot? snapshot = stored.Deserialize<BleAdaptiveDiagnosticSnapshot>(JsonOptions);
                        if (snapshot?.CapturedAt != null) return snapshot with { State = "historical" };
                    }
                    if (!json.RootElement.TryGetProperty("result", out JsonElement resultJson) ||
                        resultJson.ValueKind != JsonValueKind.Object) continue;
                    BleAdaptiveRunResult? result = resultJson.Deserialize<BleAdaptiveRunResult>(JsonOptions);
                    if (result?.Native?.Sample == null || result.Final?.Sample == null || result.Trials == null) continue;
                    // Old reports contain measurements/decisions, not exact API statuses or HRESULTs.
                    return BleAdaptiveDiagnosticSnapshot.FromRun(result, state: "historical_legacy",
                        capturedAt: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
                { warning = "部分历史记录无法读取，已跳过。"; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { warning = "历史记录未能读取；不影响当前连接。"; }
        return null;
    }
}
