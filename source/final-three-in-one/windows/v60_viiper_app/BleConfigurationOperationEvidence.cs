using System;
using System.IO;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

public sealed record BleConfigurationOperationResult(bool Known, bool RestartSucceeded, bool RestartRequired, string Code);

/// <summary>Saved values and a successful child exit are not proof that adapter restart completed.</summary>
public static class BleConfigurationOperationEvidence
{
    public static BleConfigurationOperationResult Parse(string json, string operation, string preset,
        DateTimeOffset requestedAt, DateTimeOffset now)
    {
        var unknown = new BleConfigurationOperationResult(false, false, true, "completion_unknown");
        if (json.Length > 65536 || requestedAt > now) return unknown;
        try
        {
            using var document = JsonDocument.Parse(json);
            var item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("operation", out var action) || action.ValueKind != JsonValueKind.String || action.GetString() != operation ||
                !item.TryGetProperty("preset", out var choice) || choice.ValueKind != JsonValueKind.String || choice.GetString() != preset ||
                !item.TryGetProperty("completedAt", out var stamp) || stamp.ValueKind != JsonValueKind.String ||
                !stamp.TryGetDateTimeOffset(out var completed) || completed < requestedAt || completed > now ||
                !item.TryGetProperty("restartSucceeded", out var restarted) || restarted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !item.TryGetProperty("restartRequired", out var needed) || needed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unknown;
            bool success = restarted.GetBoolean(), required = needed.GetBoolean();
            if (success == required) return unknown;
            return new(true, success, required, success ? "adapter_restart_verified" : "system_restart_required");
        }
        catch (JsonException) { return unknown; }
    }

    public static BleConfigurationOperationResult Read(string operation, string preset, DateTimeOffset requestedAt)
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "XinHeLianSheng-Pro2-Bridge", "BLE-Turbo", "last-stack-operation.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) return new(false, false, true, "completion_absent");
            return Parse(File.ReadAllText(path), operation, preset, requestedAt, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, false, true, "completion_read_" + ex.GetType().Name); }
    }
}
