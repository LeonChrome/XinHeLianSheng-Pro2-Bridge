using System;

namespace Y700Switch2V60Viiper;

public enum BleRateStabilityState { Observing, StableHighRate, LowRate, Recovering, Stale }

// These describe Windows notification delivery, not a proven connection interval,
// RF fault, driver fault or cause of a low input rate.
public enum BleNotifyCadenceHint { Unknown, Predominantly15Ms, MixedShortAndLong, Irregular }

public sealed record BleRateStabilityOptions
{
    // A measured 133 Hz stream has window/scheduling tolerance. This health
    // threshold is independent of gameplay (60 Hz) and preflight acceptance
    // (PureSoftware133Mode.MinimumAcceptedHz, currently 120 Hz).
    public double HighRateThresholdHz { get; init; } = 128;
    public int GoodWindowsRequired { get; init; } = 3;
    public int LowWindowsRequired { get; init; } = 2;
    public double MinimumWindowSeconds { get; init; } = 4;
    public double MaximumWindowSeconds { get; init; } = 10;
    public double RecentWindowSeconds { get; init; } = 15;
    public double MaximumContinuityGapSeconds { get; init; } = 1;
}

/// <summary>Counts are deltas for this exact completed window, never session totals.</summary>
public sealed record BleRateMeasurementWindow(long WindowStartTicks, long WindowEndTicks,
    ulong RawNotifications, ulong ParsedInputs, NotifyGapSummary Gaps = default);

public sealed record BleRateStabilityWindow(long WindowStartTicks, long WindowEndTicks,
    ulong RawNotifications, ulong ParsedInputs, double Seconds, double RawHz,
    double ParsedHz, NotifyGapSummary Gaps, BleNotifyCadenceHint CadenceHint)
{
    public double EffectiveHz => Math.Min(RawHz, ParsedHz);
}

public sealed record BleRateStabilitySnapshot(BleRateStabilityState State, bool IsRecent,
    bool IsStableHighRate, bool ShouldWarnLowRate, int ConsecutiveGoodWindows,
    int ConsecutiveLowWindows, long ValidWindowCount, long LowWindowCount,
    BleRateStabilityWindow? LastWindow, BleRateStabilityWindow? RecentLowWindow,
    double? SessionMinimumHz, double? SessionMaximumHz, double? SessionMinimumRawHz,
    double? SessionMaximumRawHz, double? SessionMinimumParsedHz, double? SessionMaximumParsedHz,
    double HighRateThresholdHz, double ValidMeasuredSeconds)
{
    public bool HasValidWindow => LastWindow != null;
    public BleNotifyCadenceHint CadenceHint => LastWindow?.CadenceHint ?? BleNotifyCadenceHint.Unknown;
    public bool CauseProven => false;
}

/// <summary>
/// Pure, bounded session monitor. The caller owns synchronization and supplies
/// monotonic timestamps. No timer, wall clock, transport, process or system I/O.
/// Reset when disconnecting, changing physical connection or clearing counters.
/// </summary>
public sealed class BleRateStabilityMonitor
{
    private readonly long timestampFrequency;
    private readonly BleRateStabilityOptions options;
    private BleRateStabilityWindow? lastWindow, recentLowWindow;
    private int consecutiveGood, consecutiveLow;
    private long validWindows, lowWindows;
    private bool lowRateConfirmed;
    private double? minimumHz, maximumHz, minimumRawHz, maximumRawHz, minimumParsedHz, maximumParsedHz;
    private double measuredSeconds;

    public BleRateStabilityMonitor(long timestampFrequency, BleRateStabilityOptions? options = null)
    {
        if (timestampFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        this.timestampFrequency = timestampFrequency;
        this.options = options ?? new();
        if (!double.IsFinite(this.options.HighRateThresholdHz) || this.options.HighRateThresholdHz <= 0 ||
            this.options.GoodWindowsRequired < 2 || this.options.LowWindowsRequired < 2 ||
            !double.IsFinite(this.options.MinimumWindowSeconds) || this.options.MinimumWindowSeconds <= 0 ||
            !double.IsFinite(this.options.MaximumWindowSeconds) ||
            this.options.MaximumWindowSeconds < this.options.MinimumWindowSeconds ||
            !double.IsFinite(this.options.RecentWindowSeconds) ||
            this.options.RecentWindowSeconds < this.options.MaximumWindowSeconds ||
            !double.IsFinite(this.options.MaximumContinuityGapSeconds) || this.options.MaximumContinuityGapSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid stability window policy.");
    }

    /// <summary>
    /// Accepts an independent completed window once. Invalid, duplicate and
    /// overlapping windows return false without changing session evidence.
    /// Zero counts in a valid completed window are real low-rate evidence.
    /// </summary>
    public bool ObserveWindow(BleRateMeasurementWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowStartTicks < 0 || window.WindowEndTicks <= window.WindowStartTicks ||
            window.ParsedInputs > window.RawNotifications ||
            (lastWindow != null && window.WindowStartTicks < lastWindow.WindowEndTicks)) return false;

        double seconds = (window.WindowEndTicks - window.WindowStartTicks) / (double)timestampFrequency;
        if (!double.IsFinite(seconds) || seconds < options.MinimumWindowSeconds ||
            seconds > options.MaximumWindowSeconds) return false;
        double rawHz = window.RawNotifications / seconds;
        double parsedHz = window.ParsedInputs / seconds;
        if (!double.IsFinite(rawHz) || !double.IsFinite(parsedHz)) return false;

        // A gap in observations cannot be used to join old and new good/low
        // streaks. An existing confirmed warning still requires good recovery.
        if (lastWindow != null && (window.WindowStartTicks - lastWindow.WindowEndTicks) /
                (double)timestampFrequency > options.MaximumContinuityGapSeconds)
            consecutiveGood = consecutiveLow = 0;

        lastWindow = new(window.WindowStartTicks, window.WindowEndTicks, window.RawNotifications,
            window.ParsedInputs, seconds, rawHz, parsedHz, window.Gaps, ClassifyCadence(window.Gaps));
        validWindows = Increment(validWindows);
        measuredSeconds += seconds;
        minimumHz = Min(minimumHz, lastWindow.EffectiveHz);
        maximumHz = Max(maximumHz, lastWindow.EffectiveHz);
        minimumRawHz = Min(minimumRawHz, rawHz);
        maximumRawHz = Max(maximumRawHz, rawHz);
        minimumParsedHz = Min(minimumParsedHz, parsedHz);
        maximumParsedHz = Max(maximumParsedHz, parsedHz);

        if (lastWindow.EffectiveHz >= options.HighRateThresholdHz)
        {
            consecutiveLow = 0;
            consecutiveGood = Increment(consecutiveGood);
            if (consecutiveGood >= options.GoodWindowsRequired) lowRateConfirmed = false;
        }
        else
        {
            consecutiveGood = 0;
            consecutiveLow = Increment(consecutiveLow);
            lowWindows = Increment(lowWindows);
            recentLowWindow = lastWindow;
            if (consecutiveLow >= options.LowWindowsRequired) lowRateConfirmed = true;
        }
        return true;
    }

    /// <summary>A read-only snapshot; repeated UI reads never extend a streak.</summary>
    public BleRateStabilitySnapshot Snapshot(long nowTicks)
    {
        bool recent = lastWindow != null && nowTicks >= lastWindow.WindowEndTicks &&
            (nowTicks - lastWindow.WindowEndTicks) / (double)timestampFrequency <= options.RecentWindowSeconds;
        bool stable = recent && !lowRateConfirmed && consecutiveGood >= options.GoodWindowsRequired;
        BleRateStabilityState state = lastWindow == null ? BleRateStabilityState.Observing :
            !recent ? BleRateStabilityState.Stale :
            lowRateConfirmed ? (consecutiveGood > 0 ? BleRateStabilityState.Recovering : BleRateStabilityState.LowRate) :
            stable ? BleRateStabilityState.StableHighRate : BleRateStabilityState.Observing;
        return new(state, recent, stable, recent && lowRateConfirmed, consecutiveGood, consecutiveLow,
            validWindows, lowWindows, lastWindow, recentLowWindow, minimumHz, maximumHz,
            minimumRawHz, maximumRawHz, minimumParsedHz, maximumParsedHz,
            options.HighRateThresholdHz, measuredSeconds);
    }

    public void Reset()
    {
        lastWindow = recentLowWindow = null;
        consecutiveGood = consecutiveLow = 0;
        validWindows = lowWindows = 0;
        lowRateConfirmed = false;
        minimumHz = maximumHz = minimumRawHz = maximumRawHz = minimumParsedHz = maximumParsedHz = null;
        measuredSeconds = 0;
    }

    public static BleNotifyCadenceHint ClassifyCadence(NotifyGapSummary gaps)
    {
        if (gaps.Samples < 50 || gaps.Near7p5 < 0 || gaps.Near15 < 0 || gaps.Over22 < 0 ||
            (long)gaps.Near7p5 + gaps.Near15 + gaps.Over22 > gaps.Samples ||
            !double.IsFinite(gaps.P50Ms) || !double.IsFinite(gaps.P95Ms) ||
            !double.IsFinite(gaps.P99Ms) || !double.IsFinite(gaps.MinMs) || !double.IsFinite(gaps.MaxMs) ||
            gaps.MinMs < 0 || gaps.P50Ms < gaps.MinMs || gaps.P95Ms < gaps.P50Ms ||
            gaps.P99Ms < gaps.P95Ms || gaps.MaxMs < gaps.P99Ms) return BleNotifyCadenceHint.Unknown;
        if (gaps.Near7p5 >= gaps.Samples * .2 && gaps.Over22 >= gaps.Samples * .1)
            return BleNotifyCadenceHint.MixedShortAndLong;
        if (gaps.Near15 >= gaps.Samples * .8 && gaps.Near7p5 < gaps.Samples * .1 &&
            gaps.Over22 < gaps.Samples * .1 && gaps.P50Ms is >= 13.5 and <= 16.5 && gaps.P95Ms <= 18)
            return BleNotifyCadenceHint.Predominantly15Ms;
        return BleNotifyCadenceHint.Irregular;
    }

    private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
    private static int Increment(int value) => value == int.MaxValue ? value : value + 1;
    private static double Min(double? previous, double value) => previous.HasValue ? Math.Min(previous.Value, value) : value;
    private static double Max(double? previous, double value) => previous.HasValue ? Math.Max(previous.Value, value) : value;
}
