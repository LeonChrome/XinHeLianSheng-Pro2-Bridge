using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

public sealed record Win10BleConfigurationHistory(bool Valid, string Code,
    string? Operation = null, string? Preset = null, DateTimeOffset? CompletedAt = null,
    bool? RestartSucceeded = null, bool? RestartRequired = null);

/// <summary>
/// Read-only historical configuration evidence. Never writes settings, runs a preset,
/// restarts a device, or treats an old configuration result as current BLE telemetry.
/// </summary>
public static class Win10BleDiagnosticHistory
{
    public const int MaximumFileBytes = 65536;
    private static readonly string[] RequiredFields =
        { "operation", "preset", "completedAt", "restartSucceeded", "restartRequired" };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "XinHeLianSheng-Pro2-Bridge", "BLE-Turbo", "last-stack-operation.json");

    public static Win10BleConfigurationHistory Parse(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(false, "history_absent");
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes) return new(false, "history_oversize");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object) return new(false, "history_invalid");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in item.EnumerateObject())
                if (RequiredFields.Contains(property.Name, StringComparer.Ordinal) && !seen.Add(property.Name))
                    return new(false, "history_duplicate_field");
            if (RequiredFields.Any(name => !seen.Contains(name)) ||
                !TryString(item, "operation", out string? operation) ||
                !TryString(item, "preset", out string? preset) ||
                !TryString(item, "completedAt", out _) ||
                !item.GetProperty("completedAt").TryGetDateTimeOffset(out DateTimeOffset completedAt) ||
                completedAt > now ||
                item.GetProperty("restartSucceeded").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                item.GetProperty("restartRequired").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(false, "history_invalid");
            bool succeeded = item.GetProperty("restartSucceeded").GetBoolean();
            bool required = item.GetProperty("restartRequired").GetBoolean();
            if (succeeded == required) return new(false, "history_inconsistent_restart");
            bool validPreset = operation == "apply" && Win10BlePresetPolicy.Presets.Any(p => p.Id == preset) ||
                operation == "restore" && preset == "original";
            if (!validPreset) return new(false, "history_invalid_operation_or_preset");
            return new(true, "history_valid", operation, preset, completedAt, succeeded, required);
        }
        catch (JsonException) { return new(false, "history_invalid_json"); }
    }

    public static Win10BleConfigurationHistory Read(string? path = null, DateTimeOffset? now = null)
    {
        try
        {
            // Bound the read itself, not only an earlier FileInfo length check: another
            // process can replace or grow the marker while this information page is open.
            using var stream = new FileStream(path ?? DefaultPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes) return new(false, "history_oversize");
            var bytes = new byte[MaximumFileBytes + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumFileBytes) return new(false, "history_oversize");
            string json = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
            return Parse(json, now ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return new(false, "history_absent"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        { return new(false, "history_read_" + ex.GetType().Name); }
    }

    public static string ReadAndFormat(bool isWindows10, string? path = null, DateTimeOffset? now = null) =>
        Format(isWindows10 ? Read(path, now) : null, isWindows10);

    public static string Format(Win10BleConfigurationHistory? history, bool isWindows10 = true)
    {
        var text = new StringBuilder();
        text.AppendLine("Win10 系统 BLE 参数预设与配置记录");
        if (!isWindows10)
        {
            text.AppendLine("当前为 Win11：不套用 Win10 系统配置；请查看本连接的 API 预设结果。");
            return text.ToString().TrimEnd();
        }
        text.AppendLine("可用方法（首页授权后测试；此页只读，不执行修改）：");
        foreach (Win10BlePresetDefinition preset in Win10BlePresetPolicy.Presets)
        {
            text.AppendLine(preset.Id + " · " + preset.Title + "：Min=" + preset.IntervalMinUnits +
                " / Max=" + preset.IntervalMaxUnits + " / Latency=" + preset.Latency +
                " / LSTO=" + preset.TimeoutUnits + "（" + Number(preset.MinimumIntervalMs) +
                "–" + Number(preset.MaximumIntervalMs) + "ms；监督超时 " +
                Number(preset.TimeoutUnits * 10.0) + "ms）");
        }
        text.AppendLine("参数目录不是执行结果；以当前真实 FD2 / Parsed 测量判定速率。");
        if (history is not { Valid: true, CompletedAt: not null, Operation: not null, Preset: not null,
            RestartSucceeded: not null, RestartRequired: not null })
        {
            text.AppendLine("历史配置操作：" + ExplainUnknown(history?.Code ?? "history_absent"));
            text.AppendLine("不能据此认定所有预设均未测试、请求被拒绝或蓝牙硬件不支持。");
            return text.ToString().TrimEnd();
        }
        // Format only validated records, even when callers construct the record directly.
        if (history.RestartSucceeded == history.RestartRequired ||
            !(history.Operation == "apply" && Win10BlePresetPolicy.Presets.Any(p => p.Id == history.Preset) ||
              history.Operation == "restore" && history.Preset == "original"))
            return Format(new(false, "history_invalid"));
        text.AppendLine("历史配置操作（非本次连接验收）：");
        text.AppendLine("完成时间：" + history.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        string presetTitle = history.Operation == "restore" ? "原始备份" :
            Win10BlePresetPolicy.Presets.Single(p => p.Id == history.Preset).Title;
        text.AppendLine("操作：" + (history.Operation == "restore" ? "恢复" : "应用") +
            "；预设：" + history.Preset + " / " + presetTitle);
        text.AppendLine("该次记录：restartSucceeded=" + (history.RestartSucceeded.Value ? "True" : "False") +
            "；restartRequired=" + (history.RestartRequired.Value ? "True" : "False"));
        text.AppendLine(history.RestartSucceeded.Value ?
            "该次适配器重启已完成；不证明本次连接采用了参数或达到 133Hz。" :
            "该次操作提示需重启 Windows；之后可能已重启，不能据此判定当前仍需重启。");
        text.AppendLine("此文件仅保留最近一次配置操作，不是三组预设的完整测量历史。");
        return text.ToString().TrimEnd();
    }

    private static bool TryString(JsonElement item, string name, out string? value)
    {
        value = null;
        if (item.GetProperty(name).ValueKind != JsonValueKind.String) return false;
        value = item.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string Number(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string ExplainUnknown(string code) => code switch
    {
        "history_absent" => "未找到；执行结果未记录。",
        "history_oversize" => "文件超过 64KiB，未读取；结果未知。",
        _ => "记录不可用（" + code.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ') + "）；结果未知。"
    };
}
