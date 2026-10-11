using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Y700Switch2V60Viiper;

/// <summary>
/// One observed request, including construction failures before the Windows API
/// is called. Nullable evidence means not recorded, never presumed success/failure.
/// </summary>
public sealed record BleAdaptiveRequestEvidence(string PresetId, string Phase,
    ushort? MinUnits, ushort? MaxUnits, ushort? Latency, ushort? TimeoutUnits,
    string Status, bool? ApiAccepted, int? HResult = null, string? ExceptionType = null,
    bool? ApiCalled = null, string ParameterSource = "")
{
    public static BleAdaptiveRequestEvidence FromCandidate(BleAdaptivePresetCandidate? candidate,
        string phase, string status, bool? apiAccepted, int? hResult = null,
        string? exceptionType = null, bool? apiCalled = null, string parameterSource = "") =>
        new(candidate?.Id ?? "restore_original", phase, candidate?.MinUnits,
            candidate?.MaxUnits, candidate?.Latency, candidate?.TimeoutUnits, status,
            apiAccepted, hResult, exceptionType, apiCalled, parameterSource);
}

/// <summary>Immutable session cache. Formatting performs no device, log-file or system access.</summary>
public sealed record BleAdaptiveDiagnosticSnapshot(DateTimeOffset? CapturedAt, string State,
    string FinalCode, BleAdaptiveMeasurement? Native, BleAdaptiveMeasurement? Final,
    bool FinalVerified, string? SelectedPreset, IReadOnlyList<BleAdaptiveTrialRecord> Trials,
    IReadOnlyList<BleAdaptiveRequestEvidence> Requests, IReadOnlyList<BleTrialMeasurement>? Windows = null, string Stage = "")
{
    // Existing sessions may have an optimizer summary but no request-detail cache.
    // Absence of that cache is not evidence that Windows rejected their requests.
    public static BleAdaptiveDiagnosticSnapshot None() => new(null, "not_recorded",
        "not_recorded", null, null, false, null, Array.Empty<BleAdaptiveTrialRecord>(),
        Array.Empty<BleAdaptiveRequestEvidence>());

    public static BleAdaptiveDiagnosticSnapshot FromRun(BleAdaptiveRunResult result,
        IReadOnlyList<BleAdaptiveRequestEvidence>? requests = null, string state = "completed",
        DateTimeOffset? capturedAt = null, IReadOnlyList<BleTrialMeasurement>? windows = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(capturedAt ?? DateTimeOffset.UtcNow, state, result.Code, result.Native,
            result.Final, result.FinalVerified, result.Selected?.Id,
            result.Trials?.ToArray() ?? Array.Empty<BleAdaptiveTrialRecord>(),
            requests?.ToArray() ?? Array.Empty<BleAdaptiveRequestEvidence>(), windows?.ToArray());
    }
}

/// <summary>
/// Read-only Chinese evidence summary for the existing system/Bluetooth panel.
/// Request acceptance, actual interval and received-input verification stay distinct.
/// </summary>
public static class BleAdaptiveDiagnostics
{
    // Display-only catalog. These values are never submitted by this formatter.
    private static readonly (string Id, string Name)[] Presets =
    {
        ("win11_fixed_7p5", "固定 7.5ms"),
        ("win11_flexible_7p5_15", "7.5–15ms 协商"),
        ("win11_throughput", "系统吞吐量优先"),
        ("win11_fixed_15", "固定 15ms")
    };

    public static string Format(BleAdaptiveDiagnosticSnapshot? snapshot)
    {
        snapshot ??= BleAdaptiveDiagnosticSnapshot.None();
        IReadOnlyList<BleAdaptiveRequestEvidence> requests = snapshot.Requests ?? Array.Empty<BleAdaptiveRequestEvidence>();
        IReadOnlyList<BleAdaptiveTrialRecord> trials = snapshot.Trials ?? Array.Empty<BleAdaptiveTrialRecord>();
        var text = new StringBuilder();
        text.AppendLine("蓝牙预设测试记录（只读缓存）");
        text.AppendLine("流程状态：" + State(snapshot.State) + "；最终判定：" + Clean(snapshot.FinalCode));
        if (!string.IsNullOrWhiteSpace(snapshot.Stage)) text.AppendLine("最近阶段：" + Clean(snapshot.Stage));
        if (snapshot.CapturedAt.HasValue)
            text.AppendLine("记录时间：" + snapshot.CapturedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        text.AppendLine("请求被接受不等于采用了该间隔；133Hz 以真实 FD2 / Parsed 完整窗口及复核为准。");
        if (snapshot.State == "not_recorded")
        {
            text.AppendLine("本会话没有预设请求详细记录，可能是旧会话或未开启测试；不能据此认定请求被拒绝。");
            return text.ToString();
        }

        text.AppendLine("流程说明：" + ExplainCode(snapshot.FinalCode));
        text.AppendLine("原连接：" + Measurement(snapshot.Native));
        bool hasFinal = snapshot.Final?.Sample != null;
        text.AppendLine("最终连接：" + Measurement(snapshot.Final));
        text.AppendLine("选用路线：" + (string.IsNullOrWhiteSpace(snapshot.SelectedPreset) ? "原连接／未选中预设" : Clean(snapshot.SelectedPreset)) +
            "；最终复核：" + (hasFinal ? snapshot.FinalVerified ? "通过" : "未通过" : "未进行"));
        if (snapshot.Windows is { Count: > 0 } windows)
        {
            text.AppendLine();
            text.AppendLine("逐阶段实际测量（按发生顺序；不触发新的 GATT 查询）");
            foreach (BleTrialMeasurement window in windows)
            {
                if (window == null) continue;
                string interval = window.ConnectionIntervalUnits > 0
                    ? F(window.ConnectionIntervalUnits * 1.25) + "ms（系统报告）" : "未读取";
                text.AppendLine("阶段=" + Clean(window.Phase) + "；时长=" + F(window.DurationSeconds) +
                    "s；FD2 / Parsed=" + F(window.Fd2Hz) + " / " + F(window.ParsedHz) +
                    "Hz；通知／解析=" + window.Fd2Count + "/" + window.ParsedCount +
                    "；连接=" + (window.ConnectedAtEnd ? "在线" : "断开") + "；末尾输入年龄=" +
                    F(window.LastInputAgeMs) + "ms；实际间隔=" + interval +
                    "；窗口请求状态=" + Clean(window.RequestStatus));
                if (window.Gaps is { SampleCount: > 0 } gaps)
                {
                    text.AppendLine("  Notify 间隙：P50 / P90 / P95 / P99=" + F(gaps.P50Ms) + " / " +
                        F(gaps.P90Ms) + " / " + F(gaps.P95Ms) + " / " + F(gaps.P99Ms) +
                        "ms；最小 / 最大=" + F(gaps.MinMs) + " / " + F(gaps.MaxMs) +
                        "ms；间隙样本=" + gaps.SampleCount);
                    if (gaps.Distribution is { Count: > 0 })
                        text.AppendLine("  间隙分布：" + string.Join("；", gaps.Distribution.Select(p => Clean(p.Key) + "=" + p.Value)));
                }
                else text.AppendLine("  Notify 间隙：无有效间隙样本，不能把默认零值当作零延迟。");
            }
        }
        else text.AppendLine("逐阶段完整间隙：未记录；P50 / P90 / P99 不由平均 Hz 反推。已有 P95 / 最大间隙见窗口摘要。");

        foreach (var preset in Presets)
        {
            var recordedRequests = requests.Where(r => r != null && r.PresetId == preset.Id).ToArray();
            var recordedTrials = trials.Where(t => t != null &&
                (t.Preset == preset.Id || t.Preset == preset.Id + "_VERIFY")).ToArray();
            text.AppendLine();
            text.AppendLine("【" + preset.Name + "】" + preset.Id);
            if (recordedRequests.Length == 0 && recordedTrials.Length == 0)
            {
                text.AppendLine(snapshot.State == "running" ? "尚未进行；测试仍在运行。" :
                    "没有本组请求或实测记录；未测试／被跳过，原因见流程判定。不能视为请求被拒绝。");
                continue;
            }
            if (recordedRequests.Length == 0)
                text.AppendLine("请求详细记录：缺失（旧记录）；无法还原 API Status、HRESULT 或是否实际调用 API。");
            foreach (var request in recordedRequests)
                AppendRequest(text, request);
            if (recordedTrials.Length == 0)
                text.AppendLine("实测窗口：未进行／未完成；不能据请求状态确认实际间隔或输入改善。");
            foreach (var trial in recordedTrials)
            {
                bool verification = trial.Preset.EndsWith("_VERIFY", StringComparison.Ordinal);
                text.AppendLine((verification ? "复核窗口（沿用请求，不代表再次调用 API）" : "测试窗口") +
                    "：" + Measurement(trial.Measurement));
                text.AppendLine("运行器请求结果：" + (trial.RequestAccepted ? "接受" : "未接受") +
                    "；窗口判定：" + Clean(trial.Decision) + " · " + ExplainCode(trial.Decision));
                if (recordedRequests.Length == 1)
                    text.AppendLine("证据判断：" + Interpret(recordedRequests[0], trial.Measurement));
                else if (recordedRequests.Length > 1)
                    text.AppendLine("证据判断：本方案有多次请求；按阶段核对，不以最后的返回值替代各次测量。");
            }
        }

        // Restore/release events are important: the final interval can differ from
        // an earlier candidate despite every request having returned Success.
        var otherRequests = requests.Where(r => r != null && !Presets.Any(p => p.Id == r.PresetId)).ToArray();
        if (otherRequests.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("恢复／其他请求记录");
            foreach (var request in otherRequests) AppendRequest(text, request);
            text.AppendLine("释放或恢复请求不证明连接间隔已回退；以上最终实测窗口才是结果依据。");
        }
        return text.ToString();
    }

    public static string Interpret(BleAdaptiveRequestEvidence? request, BleAdaptiveMeasurement? measurement)
    {
        if (request == null) return "请求详细记录缺失，不能区分权限拒绝、API 异常或协商未生效。";
        if (request.ApiCalled == false)
            return "未调用 Windows 参数请求 API；构造、前置检查或释放路径没有发出本次请求。";
        bool denied = string.Equals(request.Status, "AccessDenied", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Status, "Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            request.HResult == unchecked((int)0x80070005);
        if (denied) return "有明确权限拒绝证据；不能据此判断适配器不支持短间隔。";
        if (request.ApiAccepted == false)
            return string.IsNullOrWhiteSpace(request.ExceptionType)
                ? "系统没有接受本次请求，具体返回值见 API Status；没有实测通过证据。"
                : "本次请求发生异常，类型和 HRESULT 见记录；不能当作请求成功或间隔已改变。";
        if (request.ApiAccepted != true)
            return "API 接受状态未记录，不能推断请求被接受或被拒绝。";
        if (measurement?.Sample == null || !BleAdaptivePresetPolicy.IsValidMeasurement(measurement))
            return "API 已接受请求，但尚无有效完整实测窗口，不能确认提速。";
        if (BleAdaptivePresetPolicy.MeetsTarget(measurement))
            return "本窗口真实 FD2 / Parsed 达到高速门槛；是否稳定仍以最终复核及持续监测为准。";
        ushort? actual = measurement.Sample.ConnectionIntervalUnits;
        if (actual.HasValue && request.MaxUnits.HasValue && actual.Value > request.MaxUnits.Value)
            return "API 已接受请求，但系统实际间隔仍长于请求上限，且真实输入未达高速门槛；连接协商未达到目标。";
        return actual is > 0
            ? "API 已接受请求，但本窗口真实输入未达高速门槛；请求成功不能代替通知速率验收。"
            : "API 已接受请求，但本窗口真实输入未达高速门槛；系统间隔未读取，不能仅凭时间戳判定空口原因。";
    }

    public static string Measurement(BleAdaptiveMeasurement? measurement)
    {
        if (measurement?.Sample == null) return "未实测／无记录";
        BleMeasuredTrialSample sample = measurement.Sample;
        bool valid = BleAdaptivePresetPolicy.IsValidMeasurement(measurement);
        string interval = sample.ConnectionIntervalUnits is { } units && units > 0
            ? F(units * 1.25) + "ms（系统报告）" : "未读取";
        return "FD2 / Parsed=" + F(sample.Fd2Hz) + " / " + F(sample.ParsedHz) + "Hz；时长=" +
            F(sample.DurationSeconds) + "s；通知／解析=" + sample.Fd2Count + "/" + sample.ParsedCount +
            "；连接=" + (sample.Connected ? "在线" : "断开") + "；末尾输入年龄=" + F(sample.LastInputAgeMs) +
            "ms；完整有效窗口=" + (valid ? "是" : "否") + "；实际间隔=" + interval +
            "；P95=" + Optional(sample.P95GapMs) + "ms；最大间隙=" + Optional(measurement.MaximumGapMs) + "ms";
    }

    public static string ExplainCode(string? code) => code switch
    {
        "native_measurement_not_ready" => "原连接测量窗口无效，未进行参数预设测试。",
        "native_target_preserved" => "原连接已达高速门槛，未追加参数请求。",
        "win11_api_unavailable" => "当前系统参数请求 API 不可用，未进行预设测试。",
        "win10_native_only" => "Win10 不使用 Win11 参数 API；连接流程不自动修改全局参数。",
        "win10_audited_configuration_requires_opt_in" or "win10_experimental_configuration_requires_opt_in" =>
            "Win10 系统参数实验需要首页单独授权；本连接流程不修改系统。",
        "win11_bounded_presets" => "Win11 有限预设测试。",
        "request_rejected" => "运行器记录请求未接受；具体 API 状态见本组请求。",
        "target_reached" => "本窗口达高速门槛，仍需复核。",
        "playable_improvement" => "真实输入改善并达到游玩门槛，尚未达高速门槛。",
        "partial_improvement" => "输入部分改善，尚未达到游玩门槛。",
        "stability_improvement" => "频率相近，但间隙统计改善。",
        "incumbent_is_better" or "no_meaningful_improvement" => "本方案没有优于已保留路线。",
        "measured_rate_regressed" => "输入频率下降，不保留本方案。",
        "measured_stability_regressed" => "通知间隙恶化，不保留本方案。",
        "invalid_or_stale_measurement" or "unequal_windows" => "测量无效、陈旧或窗口不匹配，不能保留本方案。",
        "final_target_verified" => "最终窗口达高速门槛；后续仍持续监测。",
        "final_playable_verified" => "最终窗口可游玩，未达高速门槛。",
        "final_low_rate_verified" => "最终窗口仍低速，不承诺最低 66Hz。",
        "final_measurement_invalid" => "最终窗口无效，不能宣称稳定。",
        "final_verification_regressed" => "最终复核降速或卡顿，不能宣称稳定高速。",
        "request_restore_failed" => "恢复请求失败；试验请求的释放结果见记录，不能保证间隔已回退。",
        "link_disconnected" or "connection_changed" => "测试连接已断开或改变，历史测量不算当前成功。",
        "bounded_timeout" => "有限时间内未完成测试，没有继续无限重试。",
        "trial_error" => "测试异常退出，未完成验收。",
        "cancelled" => "测试已取消，未完成验收。",
        "running" or "measuring_native" => "仍在测量或测试，尚无最终结果。",
        "not_started" => "尚未开始预设测试。",
        "not_recorded" => "请求详细记录缺失，不能还原这次参数流程。",
        _ => "按实际请求和测量记录判定；此结果码没有额外推断。"
    };

    private static void AppendRequest(StringBuilder text, BleAdaptiveRequestEvidence request)
    {
        text.AppendLine("请求阶段：" + Clean(request.Phase) + "；方案=" + Clean(request.PresetId));
        text.AppendLine("请求参数：Min=" + Units(request.MinUnits, 1.25) + "; Max=" + Units(request.MaxUnits, 1.25) +
            "; Latency=" + (request.Latency?.ToString(CultureInfo.InvariantCulture) ?? "未记录") +
            "; Timeout=" + Units(request.TimeoutUnits, 10) + "; 来源=" + Clean(request.ParameterSource));
        text.AppendLine("实际调用 API=" + Bool(request.ApiCalled) + "; API Status=" + Clean(request.Status) +
            "; API 接受=" + Bool(request.ApiAccepted) + "; HRESULT=" +
            (request.HResult.HasValue ? "0x" + request.HResult.Value.ToString("X8", CultureInfo.InvariantCulture) : "未记录／未发生异常") +
            "; 异常=" + Clean(request.ExceptionType));
    }

    private static string State(string? value) => value switch
    {
        "running" => "测试中", "completed" => "流程已结束", "cancelled" => "已取消", "error" => "异常结束",
        "not_started" => "未开始", "not_recorded" => "无详细记录", "interrupted" => "流程未完成",
        "historical" => "历史记录（非当前连接）", "historical_legacy" => "旧版历史记录（部分字段未采集）", _ => Clean(value)
    };
    private static string Units(ushort? value, double multiplier) => value.HasValue
        ? value.Value.ToString(CultureInfo.InvariantCulture) + " (" + F(value.Value * multiplier) + "ms)" : "系统预设／未记录";
    private static string Bool(bool? value) => value.HasValue ? value.Value ? "是" : "否" : "未记录";
    private static string F(double value) => double.IsFinite(value) && value >= 0
        ? value.ToString("F2", CultureInfo.InvariantCulture) : "无效／未获取";
    private static string Optional(double? value) => value.HasValue ? F(value.Value) : "未记录";
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未记录";
        string clean = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return clean.Length > 1024 ? clean[..1024] + "…" : clean;
    }
}
