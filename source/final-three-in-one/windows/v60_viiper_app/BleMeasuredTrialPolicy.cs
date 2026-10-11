using System;

namespace Y700Switch2V60Viiper;

/// <summary>
/// A complete capture window. Counts include its silent tail; API status and the
/// system-reported interval are evidence, not substitutes for received input.
/// </summary>
public sealed record BleMeasuredTrialSample(double DurationSeconds, int Fd2Count, int ParsedCount,
    double LastInputAgeMs, bool Connected, bool RequestedSuccessfully, double? P95GapMs = null,
    ushort? ConnectionIntervalUnits = null)
{
    public double Fd2Hz => double.IsFinite(DurationSeconds) && DurationSeconds > 0
        ? Fd2Count / DurationSeconds : double.NaN;
    public double ParsedHz => double.IsFinite(DurationSeconds) && DurationSeconds > 0
        ? ParsedCount / DurationSeconds : double.NaN;
    public double EffectiveHz => Math.Min(Fd2Hz, ParsedHz);
}

public sealed record BleMeasuredTrialResult(bool RetainRequest, string Code, string Summary);

/// <summary>
/// Pure measured keep/release policy for an explicitly requested connection trial.
/// It neither authorizes an automatic ABI request nor certifies a hardware family.
/// </summary>
public static class BleMeasuredTrialPolicy
{
    public const double TargetHz = 128;
    public const double MinimumWindowSeconds = 10;
    public const int MinimumSamples = 20;
    public const double MaximumInputAgeMs = 500;
    public const double MinimumParsedRatio = .95;
    public const double MinimumImprovementHz = 3;
    public const double MinimumImprovementRatio = .05;
    // Matching capture durations allow at most 5% scheduler overrun.
    public const double MaximumWindowDifferenceRatio = .05;
    private const double RateComparisonTolerance = 1e-9;

    /// <summary>Additional prerequisites for an explicit in-app button, not permission to auto-request.</summary>
    public static bool CanTryInApp(bool win11, bool requestApi, bool readApi, bool connected,
        bool busy, bool measuring, bool alreadyTried, double measuredFd2Hz) =>
        win11 && requestApi && readApi && connected && !busy && !measuring && !alreadyTried &&
        double.IsFinite(measuredFd2Hz) && measuredFd2Hz > 0 && measuredFd2Hz < TargetHz;

    public static BleMeasuredTrialResult Evaluate(BleMeasuredTrialSample before, BleMeasuredTrialSample after)
    {
        BleMeasuredTrialResult Rollback(string code, string summary) => new(false, code, summary);
        if (!after.RequestedSuccessfully)
            return Rollback("request_not_successful", "参数请求未确认成功，释放本次请求。");
        if (!before.Connected || !after.Connected)
            return Rollback("link_not_live", "测量窗口结束时连接未保持在线，释放本次请求。");
        if (!ValidMeasurement(before) || !ValidMeasurement(after))
            return Rollback("invalid_measurement", "测量时长、计数或间隔统计无效，不能据此保留请求。");
        if (before.DurationSeconds < MinimumWindowSeconds || after.DurationSeconds < MinimumWindowSeconds)
            return Rollback("incomplete_window", "前后都需要至少 10 秒完整计数窗口，短时 API 成功不能作为改善证据。");
        double shorterWindow = Math.Min(before.DurationSeconds, after.DurationSeconds);
        if (Math.Abs(before.DurationSeconds - after.DurationSeconds) > shorterWindow * MaximumWindowDifferenceRatio)
            return Rollback("unequal_windows", "前后测量窗口时长不匹配，释放请求后按相同窗口重新验收。");
        if (before.Fd2Count < MinimumSamples || before.ParsedCount < MinimumSamples ||
            after.Fd2Count < MinimumSamples || after.ParsedCount < MinimumSamples)
            return Rollback("insufficient_samples", "前后真实 FD2 与 Parsed 样本不足，不能仅凭请求成功保留参数。");
        if (!Fresh(before.LastInputAgeMs) || !Fresh(after.LastInputAgeMs))
            return Rollback("input_not_fresh", "测量末尾输入已停顿或输入年龄未知，历史频率不能证明当前连接改善。");
        if (before.Fd2Hz >= TargetHz)
            return Rollback("native_target_already_met", "原生 FD2 已达到 128Hz 门槛，无需保留额外参数请求。");
        if (after.ParsedHz < after.Fd2Hz * MinimumParsedRatio)
            return Rollback("parsed_integrity_failed", "FD2 通知增加但成功解析比例低于 95%，不算有效改善。");
        if (after.Fd2Hz < before.Fd2Hz || after.ParsedHz < before.ParsedHz)
            return Rollback("measured_rate_regressed", "真实 FD2 或 Parsed 比原生窗口下降，释放本次请求。");

        // The interval can exceed 15ms when several real notifications arrive in
        // one event. Only received/parsed input establishes the measured outcome.
        if (after.Fd2Hz >= TargetHz && after.ParsedHz >= TargetHz)
            return new(true, "target_reached", "真实 FD2 / Parsed 均达到 128Hz 门槛，且两路不低于原生测量；保留请求并继续监测。");
        double increase = after.EffectiveHz - before.EffectiveHz;
        if (increase + RateComparisonTolerance >= MinimumImprovementHz &&
            increase + RateComparisonTolerance >= before.EffectiveHz * MinimumImprovementRatio)
            return new(true, "measured_improvement", "真实有效输入提升至少 3Hz 且至少 5%；保留这次改善，但尚未达到 128Hz 门槛。");
        return Rollback("no_measured_improvement", "真实输入没有达到可确认的改善门槛，释放本次请求。");
    }

    private static bool Fresh(double age) => double.IsFinite(age) && age >= 0 && age < MaximumInputAgeMs;
    private static bool ValidMeasurement(BleMeasuredTrialSample sample) =>
        double.IsFinite(sample.DurationSeconds) && sample.DurationSeconds > 0 &&
        sample.Fd2Count >= 0 && sample.ParsedCount >= 0 && sample.ParsedCount <= sample.Fd2Count &&
        (!sample.P95GapMs.HasValue || double.IsFinite(sample.P95GapMs.Value) && sample.P95GapMs.Value >= 0);
}
