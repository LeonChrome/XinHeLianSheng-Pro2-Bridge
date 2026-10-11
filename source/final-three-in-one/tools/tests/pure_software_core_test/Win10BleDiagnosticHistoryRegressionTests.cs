using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class Win10BleDiagnosticHistoryRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(8));
        string Json(string operation = "apply", string preset = "7p5", bool succeeded = true,
            bool required = false, DateTimeOffset? completedAt = null) => JsonSerializer.Serialize(new
        {
            operation, preset, completedAt = (completedAt ?? now.AddHours(-1)).ToString("O"),
            restartSucceeded = succeeded, restartRequired = required
        });
        var parsed = Win10BleDiagnosticHistory.Parse(Json(), now);
        check(parsed is { Valid: true, Operation: "apply", Preset: "7p5", RestartSucceeded: true,
            RestartRequired: false }, "Win10 historical configuration parses literal recorded action and restart flags");
        check(parsed.CompletedAt == now.AddHours(-1), "historical operation preserves recorded timestamp and offset");
        foreach (Win10BlePresetDefinition preset in Win10BlePresetPolicy.Presets)
        {
            var choice = Win10BleDiagnosticHistory.Parse(Json(preset: preset.Id), now);
            check(choice.Valid && choice.Preset == preset.Id, "all audited Win10 preset identifiers are supported: " + preset.Id);
        }
        check(Win10BleDiagnosticHistory.Parse(Json("restore", "original"), now).Valid,
            "original-backup restore marker is recognized without claiming any rate");
        foreach (var pair in new[] { ("apply", "original"), ("restore", "7p5"), ("unknown", "7p5"),
            ("apply", "other"), ("Apply", "7p5"), ("apply\nforged", "7p5") })
            check(!Win10BleDiagnosticHistory.Parse(Json(pair.Item1, pair.Item2), now).Valid,
                "unknown or mismatched action/preset is not authoritative: " + pair.Item1 + "/" + pair.Item2);
        check(!Win10BleDiagnosticHistory.Parse(Json(completedAt: now.AddTicks(1)), now).Valid,
            "a future historical timestamp cannot certify a completed action");
        check(!Win10BleDiagnosticHistory.Parse(Json(succeeded: true, required: true), now).Valid &&
            !Win10BleDiagnosticHistory.Parse(Json(succeeded: false, required: false), now).Valid,
            "contradictory restart flags are rejected rather than making a current reboot decision");
        check(Win10BleDiagnosticHistory.Parse(null, now).Code == "history_absent" &&
            Win10BleDiagnosticHistory.Parse(" ", now).Code == "history_absent",
            "missing history remains unknown, not all-presets-untested");
        foreach (string bad in new[] { "[]", "null", "{", "{}", "{\"operation\":3}",
            Json().Replace("\"restartSucceeded\":true", "\"restartSucceeded\":\"true\"", StringComparison.Ordinal),
            Json().Replace("\"completedAt\":", "\"completedAt\":\"not-a-date\",\"ignored\":", StringComparison.Ordinal) })
            check(!Win10BleDiagnosticHistory.Parse(bad, now).Valid,
                "malformed, missing or nonliteral required history fields are rejected");
        string duplicate = Json().Replace("\"operation\":\"apply\"",
            "\"operation\":\"restore\",\"operation\":\"apply\"", StringComparison.Ordinal);
        check(Win10BleDiagnosticHistory.Parse(duplicate, now).Code == "history_duplicate_field",
            "duplicate authoritative fields cannot exploit JSON last-value-wins parsing");
        string oversized = new('x', Win10BleDiagnosticHistory.MaximumFileBytes + 1);
        check(Win10BleDiagnosticHistory.Parse(oversized, now).Code == "history_oversize",
            "oversize JSON text is rejected before parsing");
        string multibyte = new('中', Win10BleDiagnosticHistory.MaximumFileBytes / 2);
        check(multibyte.Length < Win10BleDiagnosticHistory.MaximumFileBytes &&
            Encoding.UTF8.GetByteCount(multibyte) > Win10BleDiagnosticHistory.MaximumFileBytes &&
            Win10BleDiagnosticHistory.Parse(multibyte, now).Code == "history_oversize",
            "64KiB limit counts UTF-8 bytes, not only .NET characters");

        string text = Win10BleDiagnosticHistory.Format(parsed);
        foreach (Win10BlePresetDefinition preset in Win10BlePresetPolicy.Presets)
            check(text.Contains(preset.Id + " · " + preset.Title, StringComparison.Ordinal) &&
                text.Contains("Min=" + preset.IntervalMinUnits + " / Max=" + preset.IntervalMaxUnits,
                    StringComparison.Ordinal), "information panel exposes each Win10 method and exact interval units: " + preset.Id);
        check(text.Contains("Latency=0 / LSTO=200", StringComparison.Ordinal) &&
            text.Contains("监督超时 2000.00ms", StringComparison.Ordinal),
            "Win10 LSTO uses 10ms units rather than connection interval's 1.25ms units");
        check(text.Contains("历史配置操作（非本次连接验收）", StringComparison.Ordinal) &&
            text.Contains("不证明本次连接采用了参数或达到 133Hz", StringComparison.Ordinal),
            "historical apply/restart success cannot masquerade as current 133Hz validation");
        check(text.Contains("此文件仅保留最近一次配置操作", StringComparison.Ordinal),
            "last-operation marker is not presented as full per-preset trial history");
        string needsRestart = Win10BleDiagnosticHistory.Format(
            Win10BleDiagnosticHistory.Parse(Json(succeeded: false, required: true), now));
        check(needsRestart.Contains("之后可能已重启", StringComparison.Ordinal) &&
            needsRestart.Contains("不能据此判定当前仍需重启", StringComparison.Ordinal),
            "an old reboot-needed flag is not a present-day reboot requirement");
        string missing = Win10BleDiagnosticHistory.Format(null);
        check(missing.Contains("执行结果未记录", StringComparison.Ordinal) &&
            missing.Contains("不能据此认定所有预设均未测试、请求被拒绝", StringComparison.Ordinal),
            "absent marker cannot label all presets rejected or skipped");
        string win11 = Win10BleDiagnosticHistory.ReadAndFormat(false, "Z:\\path-that-must-not-be-read\\history.json", now);
        check(win11.Contains("不套用 Win10 系统配置", StringComparison.Ordinal) &&
            !win11.Contains("Min=", StringComparison.Ordinal) && !win11.Contains("未找到", StringComparison.Ordinal),
            "Win11 branch neither reads Win10 history nor advertises modifying its system stack");
        string forged = Win10BleDiagnosticHistory.Format(parsed with { Operation = "apply\nforged" });
        check(!forged.Contains("\nforged", StringComparison.Ordinal) && forged.Contains("结果未知", StringComparison.Ordinal),
            "formatter does not trust a directly constructed invalid historical record");
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            check(Win10BleDiagnosticHistory.Format(parsed).Contains("7.50–15.00ms", StringComparison.Ordinal),
                "Windows language differences cannot change diagnostic numeric notation");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
