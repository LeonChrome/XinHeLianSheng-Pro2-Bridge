using System;

namespace Y700Switch2V60Viiper;

public readonly record struct BatteryRefreshRate(
    double Fd2Hz, double ParsedHz, double P50Ms, double P95Ms, double MaxGapMs);

public readonly record struct BatteryRefreshDecision(bool Accepted, string Reason);

// No clock, radio, worker or input generation here: decisions can be tested offline.
public static class Pro2RuntimeBatteryPolicy
{
    public const double IntervalSeconds = 120;
    public const double FirstRefreshSeconds = 30;
    public const double LevelFreshSeconds = 300;
    public const double MaximumOperationGapMs = 45;
    public const string DisableArgument = "--battery-runtime-off";

    public static bool CanProbe(BatteryRefreshRate rate) =>
        Finite(rate) && rate.Fd2Hz >= 128 && rate.ParsedHz >= 128 && rate.P50Ms <= 8.5;

    public static BatteryRefreshDecision Evaluate(
        BatteryRefreshRate before, BatteryRefreshRate after,
        Pro2InitialBatterySnapshotResult report, bool sameSession, double operationGapMs)
    {
        if (!sameSession) return new(false, "session_changed");
        if (!report.SubscriptionReleased) return new(false, "cleanup_unverified");
        if (report.Payload == null || !Pro2InitialBatterySnapshot.IsValid(report.Payload))
            return new(false, report.Status);
        if (!CanProbe(before)) return new(false, "baseline_not_133");
        if (!Finite(after) || after.Fd2Hz < Math.Max(128, before.Fd2Hz * 0.94) ||
            after.ParsedHz < Math.Max(128, before.ParsedHz * 0.94) ||
            after.P50Ms > 8.5 || after.P95Ms > Math.Max(15, before.P95Ms + 3))
            return new(false, "fd2_rate_or_cadence_changed");
        // The battery operation is not latency-free. Bound its correlated gap,
        // rather than hiding it in a long average or blaming unrelated jitter.
        if (!double.IsFinite(operationGapMs) || operationGapMs < 0 ||
            operationGapMs > MaximumOperationGapMs || after.MaxGapMs > 120)
            return new(false, "notify_gap_exceeded");
        return new(true, "updated");
    }

    public static bool IsFreshRuntimeLevel(bool runtime, double ageSeconds) =>
        runtime && double.IsFinite(ageSeconds) && ageSeconds >= 0 && ageSeconds <= LevelFreshSeconds;

    private static bool Finite(BatteryRefreshRate rate) =>
        double.IsFinite(rate.Fd2Hz) && double.IsFinite(rate.ParsedHz) &&
        double.IsFinite(rate.P50Ms) && double.IsFinite(rate.P95Ms) && double.IsFinite(rate.MaxGapMs) &&
        rate.Fd2Hz >= 0 && rate.ParsedHz >= 0 && rate.P50Ms >= 0 &&
        rate.P95Ms >= rate.P50Ms && rate.MaxGapMs >= rate.P95Ms;
}
