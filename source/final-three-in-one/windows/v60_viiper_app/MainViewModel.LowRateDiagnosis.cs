using System;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private BleLowRateDiagnosis lowRateDiagnosis = BleLowRateDiagnosis.Hidden;
    private Pro2RateEvidence? lowRateEvidenceSnapshot;
    private string lastLowRateLogKey = "none";
    public bool HasLowRateWarning => lowRateDiagnosis.Visible;
    public string LowRateTitle => lowRateDiagnosis.Title;
    public string LowRateSummary => lowRateDiagnosis.Summary;
    public string LowRateAdvice => lowRateDiagnosis.Advice;
    public string LowRateEvidence => lowRateDiagnosis.Evidence;
    public string LowRateMeasuredSummary
    {
        get
        {
            if (lowRateEvidenceSnapshot is not { } rate) return "实测速率：尚未获取";
            string age = double.IsFinite(rate.LastInputAgeMs) && rate.LastInputAgeMs >= 0
                ? rate.LastInputAgeMs.ToString("F0") + "ms" : "未获取";
            return "实测 FD2 " + LowRateNumber(rate.Fd2Hz) + " Hz / Parsed " + LowRateNumber(rate.ParsedHz) +
                " Hz（" + (rate.RecentWindow ? "最近约 5 秒" : "连接阶段累计") + "） · 输入年龄 " + age;
        }
    }
    public string LowRateEnvironmentSummary => EnvironmentHomeSummary;
    public bool CanOfferWin10Optimization => lowRateDiagnosis.CanOfferWin10Configuration;
    public string LowRateReportText => LowRateTitle + Environment.NewLine + LowRateMeasuredSummary +
        Environment.NewLine + LowRateEnvironmentSummary + Environment.NewLine +
        "诊断线索（原因待定位）：" + LowRateSummary +
        Environment.NewLine + LowRateAdvice + Environment.NewLine + LowRateEvidence + Environment.NewLine;

    private void RefreshLowRateDiagnosis()
    {
        Pro2RateEvidence rate = inputSource.GetRateEvidence();
        BleLowRateDiagnosis next = BleLowRateDiagnoser.Evaluate(rate, BleCompatibilityInspector.Current, environmentSnapshot);
        string key = next.Visible ? next.Code + ":" + rate.GameplayReady + ":" + rate.IntervalUnits : "none";
        lowRateEvidenceSnapshot = rate;
        lowRateDiagnosis = next;
        foreach (string property in new[] { nameof(HasLowRateWarning), nameof(LowRateTitle), nameof(LowRateSummary),
            nameof(LowRateAdvice), nameof(LowRateEvidence), nameof(LowRateMeasuredSummary), nameof(LowRateEnvironmentSummary),
            nameof(CanOfferWin10Optimization), nameof(LowRateReportText) })
            OnPropertyChanged(property);
        if (key == lastLowRateLogKey) return;
        bool previouslyVisible = lastLowRateLogKey != "none";
        lastLowRateLogKey = key;
        if (next.Visible)
            AppendLog("[BLE_LOW_RATE_DIAG] code=" + next.Code + " gameplay_allowed=" + rate.GameplayReady +
                " fd2_hz=" + rate.Fd2Hz.ToString("F2") + " parsed_hz=" + rate.ParsedHz.ToString("F2") +
                " interval_ms=" + (rate.IntervalUnits > 0 ? (rate.IntervalUnits * 1.25).ToString("F2") : "unavailable") +
                " gap_p50_ms=" + (rate.Gaps.Samples > 0 ? rate.Gaps.P50Ms.ToString("F2") : "unavailable") +
                " hint=\"" + next.Summary + "\" advice=\"" + next.Advice + "\" automatic_system_writes=false");
        else if (previouslyVisible)
            AppendLog("[BLE_LOW_RATE_DIAG] hidden reason=" + (rate.HighRate ? "high_rate_recovered" : "not_live_or_measuring") +
                " same_virtual_device_retained=" + Running);
    }

    private static string LowRateNumber(double value) => double.IsFinite(value) && value >= 0
        ? value.ToString("F1") : "未获取";
}
