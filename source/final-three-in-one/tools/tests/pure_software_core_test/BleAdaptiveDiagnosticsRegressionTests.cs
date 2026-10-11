using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y700Switch2V60Viiper;

internal static class BleAdaptiveDiagnosticsRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        BleAdaptiveMeasurement Sample(int count = 167, ushort units = 48, double duration = 10) =>
            new(new(duration, count, count, 25, true, true, 60.5, units), 0, 119.93);
        var native = Sample();
        var candidates = BleAdaptivePresetPolicy.CreatePlan(new(true, true, true), native).Candidates;
        var fixed7p5 = candidates[0];
        var request = BleAdaptiveRequestEvidence.FromCandidate(fixed7p5, "apply", "Success", true,
            apiCalled: true, parameterSource: "own_abi");
        check(request is { MinUnits: 6, MaxUnits: 6, Latency: 0, TimeoutUnits: 600, ApiCalled: true },
            "request evidence preserves requested values and actual API-call evidence without doing a request");

        string none = BleAdaptiveDiagnostics.Format(null);
        check(none.Contains("没有预设请求详细记录", StringComparison.Ordinal) &&
            none.Contains("不能据此认定请求被拒绝", StringComparison.Ordinal),
            "missing pre-upgrade session evidence is not described as permission rejection");
        check(!none.Contains("API Status=Success", StringComparison.Ordinal),
            "missing request metadata cannot fabricate a successful API call");

        var rejected = request with { Status = "AccessDenied", ApiAccepted = false };
        var trials = new List<BleAdaptiveTrialRecord> { new(fixed7p5.Id, false, null, "request_rejected") };
        var requests = new List<BleAdaptiveRequestEvidence> { rejected };
        var result = new BleAdaptiveRunResult(null, native, native, true, "final_low_rate_verified", trials);
        var snapshot = BleAdaptiveDiagnosticSnapshot.FromRun(result, requests,
            capturedAt: new DateTimeOffset(2026, 10, 11, 1, 0, 0, TimeSpan.Zero));
        string denial = BleAdaptiveDiagnostics.Format(snapshot);
        check(denial.Contains("API Status=AccessDenied", StringComparison.Ordinal) &&
            denial.Contains("API 接受=否", StringComparison.Ordinal) && denial.Contains("实际调用 API=是", StringComparison.Ordinal),
            "access-denied records expose Windows API status, accepted flag and API-call evidence in the panel");
        check(denial.Contains("Min=6 (7.50ms)", StringComparison.Ordinal) &&
            denial.Contains("Max=6 (7.50ms)", StringComparison.Ordinal) &&
            denial.Contains("Timeout=600 (6000.00ms)", StringComparison.Ordinal),
            "panel renders connection units and supervision-timeout units with their distinct correct scales");
        check(denial.Contains("final_low_rate_verified", StringComparison.Ordinal) &&
            denial.Contains("request_rejected", StringComparison.Ordinal),
            "final run code and per-preset decision are visible without exporting a log");
        foreach (var candidate in candidates)
            check(denial.Contains(candidate.Id, StringComparison.Ordinal),
                "all four preset methods have a visible requested, missing or skipped state: " + candidate.Id);
        check(denial.Contains("未测试／被跳过", StringComparison.Ordinal) &&
            denial.Contains("不能视为请求被拒绝", StringComparison.Ordinal),
            "an absent candidate record cannot masquerade as an API refusal");

        string evidence = BleAdaptiveDiagnostics.Interpret(rejected, null);
        check(evidence.Contains("明确权限拒绝", StringComparison.Ordinal),
            "AccessDenied is classified as permission evidence, not a radio capability verdict");
        var hresultDenied = request with { Status = "Exception", HResult = unchecked((int)0x80070005),
            ApiAccepted = false, ExceptionType = "UnauthorizedAccessException" };
        check(BleAdaptiveDiagnostics.Interpret(hresultDenied, null).Contains("明确权限拒绝", StringComparison.Ordinal),
            "E_ACCESSDENIED is retained as a signed HRESULT and interpreted correctly");
        string hrText = BleAdaptiveDiagnostics.Format(snapshot with { Requests = new[] { hresultDenied } });
        check(hrText.Contains("HRESULT=0x80070005", StringComparison.Ordinal),
            "signed HRESULT is displayed as the exact eight-digit hex value");
        var abiFailure = request with { Status = "Exception", ApiAccepted = false, ApiCalled = false,
            ExceptionType = "ArgumentException", HResult = unchecked((int)0x80070057) };
        check(BleAdaptiveDiagnostics.Interpret(abiFailure, null).Contains("未调用 Windows", StringComparison.Ordinal),
            "pre-call construction failure differs from an actual API rejection");
        check(!BleAdaptiveDiagnostics.Interpret(abiFailure, null).Contains("权限拒绝", StringComparison.Ordinal),
            "generic construction failure never implies insufficient privileges");
        check(BleAdaptiveDiagnostics.Interpret(request with { ApiAccepted = false, Status = "OtherError" }, null)
            .Contains("系统没有接受", StringComparison.Ordinal),
            "unknown rejection preserves the literal return status rather than inventing a root cause");
        check(BleAdaptiveDiagnostics.Interpret(request with { ApiAccepted = null, ApiCalled = null }, native)
            .Contains("状态未记录", StringComparison.Ordinal),
            "missing status remains unknown even if a usable measurement exists");
        check(BleAdaptiveDiagnostics.Interpret(request, null).Contains("尚无有效完整实测窗口", StringComparison.Ordinal),
            "API acceptance without measurement never claims adoption or 133Hz");
        check(BleAdaptiveDiagnostics.Interpret(request, Sample(duration: 9.999))
            .Contains("尚无有效完整实测窗口", StringComparison.Ordinal),
            "short-window acceptance cannot certify the requested interval");
        check(BleAdaptiveDiagnostics.Interpret(request, native).Contains("实际间隔仍长于请求上限", StringComparison.Ordinal),
            "accepted fixed 7.5ms followed by actual 60ms is identified as a measured non-adoption");
        check(BleAdaptiveDiagnostics.Interpret(request, Sample(1330, 6)).Contains("达到高速门槛", StringComparison.Ordinal) &&
            BleAdaptiveDiagnostics.Interpret(request, Sample(1330, 6)).Contains("最终复核", StringComparison.Ordinal),
            "a high-rate candidate window is separate from final stability verification");
        check(BleAdaptiveDiagnostics.Interpret(request, native with
            { Sample = native.Sample with { ConnectionIntervalUnits = null } }).Contains("系统间隔未读取", StringComparison.Ordinal),
            "missing interval readout is not inferred from the notification cadence");

        var oldTrials = new[] { new BleAdaptiveTrialRecord(fixed7p5.Id, true, native, "no_meaningful_improvement") };
        string historical = BleAdaptiveDiagnostics.Format(BleAdaptiveDiagnosticSnapshot.FromRun(
            result with { Trials = oldTrials }));
        check(historical.Contains("请求详细记录：缺失（旧记录）", StringComparison.Ordinal) &&
            historical.Contains("运行器请求结果：接受", StringComparison.Ordinal),
            "historical stored result displays its actual runner evidence while admitting missing request details");
        check(historical.Contains("无法还原 API Status、HRESULT", StringComparison.Ordinal),
            "historical trial boolean is not promoted to a reconstructed API status or HRESULT");

        string running = BleAdaptiveDiagnostics.Format(snapshot with { State = "running", Final = null,
            FinalCode = "running", Trials = Array.Empty<BleAdaptiveTrialRecord>(),
            Requests = Array.Empty<BleAdaptiveRequestEvidence>() });
        check(running.Contains("尚未进行；测试仍在运行", StringComparison.Ordinal) &&
            running.Contains("最终复核：未进行", StringComparison.Ordinal),
            "running diagnostic state shows pending methods and no final verification rather than completion");
        string skipped = BleAdaptiveDiagnostics.Format(snapshot with { FinalCode = "native_measurement_not_ready",
            FinalVerified = false, Trials = Array.Empty<BleAdaptiveTrialRecord>(),
            Requests = Array.Empty<BleAdaptiveRequestEvidence>() });
        check(skipped.Contains("未进行参数预设测试", StringComparison.Ordinal),
            "invalid native window explicitly states that the arsenal was not tested");

        string measured = BleAdaptiveDiagnostics.Measurement(native);
        check(measured.Contains("16.70 / 16.70Hz", StringComparison.Ordinal) &&
            measured.Contains("时长=10.00s", StringComparison.Ordinal) &&
            measured.Contains("实际间隔=60.00ms（系统报告）", StringComparison.Ordinal),
            "measurement includes actual FD2 and parsed rate, duration and the system-read 60ms interval");
        check(measured.Contains("P95=60.50ms", StringComparison.Ordinal) &&
            measured.Contains("最大间隙=119.93ms", StringComparison.Ordinal),
            "recorded percentile and maximum-gap evidence is preserved exactly");
        check(BleAdaptiveDiagnostics.Measurement(native with { Sample = native.Sample with { Connected = false } })
            .Contains("完整有效窗口=否", StringComparison.Ordinal),
            "disconnected historical input cannot be rendered as a valid current window");

        var gaps = new BleGapStatistics(166, 60.07, 60.40, 60.56, 119.80, 0.05, 119.93,
            new Dictionary<string, int>());
        var windows = new List<BleTrialMeasurement>
        {
            new("AUTO_NATIVE_RETRY", 10.02, 16.70, 16.70, 167, 167, 48, "deferred", gaps, 33, true),
            new("AUTO_win11_fixed_7p5_VERIFY", 10.03, 16.65, 16.65, 167, 167, 48, "Success", gaps, 25, true),
            new("AUTO_RESTORE_VERIFY", 10.01, 16.70, 16.70, 167, 167, 48, "released", gaps, 30, true)
        };
        var detailed = BleAdaptiveDiagnosticSnapshot.FromRun(result, requests, windows: windows);
        string full = BleAdaptiveDiagnostics.Format(detailed);
        foreach (var window in windows)
            check(full.Contains(window.Phase, StringComparison.Ordinal),
                "native retry, candidate verification and restore verification windows are retained: " + window.Phase);
        check(full.Contains("P50 / P90 / P95 / P99=60.07 / 60.40 / 60.56 / 119.80ms", StringComparison.Ordinal),
            "full measurement evidence displays real percentiles rather than inventing P50 from Hz");
        check(full.Contains("最小 / 最大=0.05 / 119.93ms", StringComparison.Ordinal),
            "complete window includes actual minimum and maximum notification gaps");
        string emptyGaps = BleAdaptiveDiagnostics.Format(detailed with { Windows = new[]
        {
            windows[0] with { Gaps = gaps with { SampleCount = 0, P50Ms = 0, P95Ms = 0, MaxMs = 0 } }
        } });
        check(emptyGaps.Contains("无有效间隙样本", StringComparison.Ordinal) &&
            !emptyGaps.Contains("P50 / P90 / P95 / P99=0.00", StringComparison.Ordinal),
            "zero-sample gap defaults are never presented as measured zero latency");
        string restore = BleAdaptiveDiagnostics.Format(detailed with { Requests = new[]
        {
            request, BleAdaptiveRequestEvidence.FromCandidate(null, "restore", "Released", null, apiCalled: false)
        } });
        check(restore.Contains("恢复／其他请求记录", StringComparison.Ordinal) &&
            restore.Contains("不证明连接间隔已回退", StringComparison.Ordinal),
            "restore/release event does not certify physical interval rollback");

        // A snapshot must not retain caller-owned mutable event lists.
        trials.Clear(); requests.Clear(); windows.Clear();
        check(snapshot.Trials.Count == 1 && snapshot.Requests.Count == 1 && detailed.Windows?.Count == 3,
            "FromRun snapshots defensive-copy mutable request, trial and measurement lists");

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            check(BleAdaptiveDiagnostics.Measurement(native).Contains("16.70", StringComparison.Ordinal),
                "diagnostic decimals use invariant formatting across Windows language settings");
        }
        finally { CultureInfo.CurrentCulture = original; }
        string malformed = BleAdaptiveDiagnostics.Format(snapshot with { Requests = new[]
        {
            request with { Phase = "apply\nforged-line", Status = "Success\r\nstatus=Rejected" }
        } });
        check(!malformed.Contains("\nforged-line", StringComparison.Ordinal) &&
            !malformed.Contains("\nstatus=Rejected", StringComparison.Ordinal),
            "multiline external strings cannot inject fabricated diagnostic rows");
        check(!BleAdaptiveDiagnostics.Measurement(native with { Sample = native.Sample with
            { DurationSeconds = double.NaN, P95GapMs = double.NaN } }).Contains("NaN", StringComparison.Ordinal),
            "nonfinite measurement values are marked unavailable rather than printed as numbers");
        foreach (var code in new[] { "request_restore_failed", "bounded_timeout", "connection_changed", "final_measurement_invalid" })
            check(BleAdaptiveDiagnostics.ExplainCode(code) != BleAdaptiveDiagnostics.ExplainCode("unknown_future_code"),
                "recovery, timeout, disconnect and invalid-final codes have explicit concise explanations: " + code);
    }
}
