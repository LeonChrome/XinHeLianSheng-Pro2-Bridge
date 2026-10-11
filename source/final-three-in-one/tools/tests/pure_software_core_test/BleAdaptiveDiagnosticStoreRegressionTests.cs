using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class BleAdaptiveDiagnosticStoreRegressionTests
{
    public static void Run(Action<bool, string> check, string output)
    {
        string folder = Path.Combine(output, "diagnostic-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        check(BleAdaptiveDiagnosticStore.LoadLatest(out _, folder) == null,
            "absent history returns no record without creating a Bluetooth session");
        var sample = new BleAdaptiveMeasurement(new(10.01, 167, 167, 20, true, true, 60.5, 48), 0, 119.93);
        var trial = new BleAdaptiveTrialRecord("win11_fixed_7p5", true, sample, "no_meaningful_improvement");
        var result = new BleAdaptiveRunResult(null, sample, sample, true, "final_low_rate_verified", new[] { trial });
        string legacyPath = Path.Combine(folder, "presets_legacy.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { result }, BleAdaptiveDiagnosticStore.JsonOptions));
        File.SetLastWriteTimeUtc(legacyPath, DateTime.UtcNow.AddHours(-1));
        var legacy = BleAdaptiveDiagnosticStore.LoadLatest(out _, folder);
        check(legacy is { State: "historical_legacy" } && legacy.Requests.Count == 0 && legacy.Trials.Count == 1,
            "old result restores measured evidence but never reconstructs API return codes");
        check(BleAdaptiveDiagnostics.Format(legacy).Contains("缺失（旧记录）"),
            "old evidence explicitly admits missing requests in the copyable report");
        string badPath = Path.Combine(folder, "presets_malformed.json");
        foreach (string malformed in new[] { "[]", "null", "1", "true", "{\"diagnostics\":[]}", "{\"result\":null}", "{" })
        {
            File.WriteAllText(badPath, malformed);
            check(BleAdaptiveDiagnosticStore.LoadLatest(out _, folder)?.State == "historical_legacy",
                "malformed newest history cannot crash UI and falls back to earlier evidence: " + malformed);
        }
        File.WriteAllText(badPath, new string(' ', 256 * 1024 + 1));
        check(BleAdaptiveDiagnosticStore.LoadLatest(out _, folder)?.State == "historical_legacy",
            "oversize local history is skipped before parsing");
        var request = new BleAdaptiveRequestEvidence("win11_fixed_7p5", "apply", 6, 6, 0, 600,
            "AccessDenied", false, unchecked((int)0x80070005), "UnauthorizedAccessException", true, "own_abi");
        var snapshot = BleAdaptiveDiagnosticSnapshot.FromRun(result, new[] { request });
        string modernPath = Path.Combine(folder, "presets_modern.json");
        File.WriteAllText(modernPath, JsonSerializer.Serialize(new { diagnostics = snapshot }, BleAdaptiveDiagnosticStore.JsonOptions));
        File.SetLastWriteTimeUtc(modernPath, DateTime.UtcNow.AddSeconds(1));
        var modern = BleAdaptiveDiagnosticStore.LoadLatest(out _, folder);
        check(modern is { State: "historical" } && modern.Requests[0].Status == "AccessDenied" &&
            modern.Requests[0].HResult == unchecked((int)0x80070005),
            "new stored evidence preserves API status and HRESULT, explicitly historical");
        byte[] before = File.ReadAllBytes(modernPath);
        var source = new Pro2BleInputSource();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Pro2BleInputSource).GetField("adaptiveDiagnosticSnapshot", flags)!.SetValue(source, snapshot);
        var counters = source.GetCompactCounters();
        for (int i = 0; i < 200; i++)
            check(source.GetAdaptiveDiagnosticText(includeHistory: false).Contains("API Status=AccessDenied"),
                "readonly diagnostic getter preserves per-preset evidence " + i);
        check(counters == source.GetCompactCounters() && !source.IsRunning,
            "diagnostic display performs no BLE connect, parse, request or artificial input");
        check(before.AsSpan().SequenceEqual(File.ReadAllBytes(modernPath)),
            "reading history never rewrites its persisted evidence");
        object Start() => typeof(Pro2BleInputSource).GetMethod("StartAdaptiveDiagnosticEvidence", flags)!.Invoke(source, null)!;
        void Record(object capture, BleAdaptiveRequestEvidence evidence) => typeof(Pro2BleInputSource)
            .GetMethod("RecordAdaptiveRequest", flags)!.Invoke(source, new[] { capture, evidence });
        object firstCapture = Start();
        Record(firstCapture, request);
        typeof(Pro2BleInputSource).GetMethod("ResetAdaptiveDiagnosticEvidenceNoLock", flags)!.Invoke(source, null);
        check(source.GetAdaptiveDiagnosticText().Contains("上一轮已保存记录"),
            "disconnect retains completed or partial evidence as history rather than active state");
        object secondCapture = Start();
        Record(secondCapture, request with { Status = "Success", ApiAccepted = true, HResult = null });
        typeof(Pro2BleInputSource).GetMethod("CompleteAdaptiveDiagnosticEvidence", flags)!
            .Invoke(source, new[] { firstCapture, result });
        typeof(Pro2BleInputSource).GetMethod("MarkAdaptiveDiagnosticInterrupted", flags)!
            .Invoke(source, new[] { firstCapture, "cancelled" });
        string current = source.GetAdaptiveDiagnosticText(includeHistory: false);
        check(current.Contains("API Status=Success") && !current.Contains("API Status=AccessDenied") &&
            current.Contains("流程状态：测试中"),
            "a late completion/cancellation of an old run cannot replace another connection's active diagnostics");
        var firstSnapshot = (BleAdaptiveDiagnosticSnapshot)firstCapture.GetType().GetField("Snapshot")!.GetValue(firstCapture)!;
        check(firstSnapshot.CapturedAt != null && firstSnapshot.FinalCode == "cancelled" && firstSnapshot.Requests.Count == 1,
            "cancelled capture retains its timestamp and exact request evidence for independent persistence");
        typeof(Pro2BleInputSource).GetMethod("RecordAdaptiveStage", flags)!
            .Invoke(source, new[] { secondCapture, "正在优化蓝牙 · 方案 1/4" });
        check(source.GetAdaptiveDiagnosticText(false).Contains("最近阶段：正在优化蓝牙 · 方案 1/4"),
            "live panel distinguishes parameter trial stage from initial baseline measurement");
        source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
