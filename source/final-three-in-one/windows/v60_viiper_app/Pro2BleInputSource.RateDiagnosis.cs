using System;
using System.Diagnostics;

namespace Y700Switch2V60Viiper;

public sealed record Pro2RateEvidence(Pro2ConnectionPhase Phase, bool Running, bool Connected, bool HighRate,
    double Fd2Hz, double ParsedHz, uint Fd2Count, uint ParsedCount, double LastInputAgeMs,
    ushort IntervalUnits, string DescriptorStatus, string ParameterStatus, string Win11Route,
    NotifyGapSummary Gaps, bool RecentWindow, ulong HandlerSamples = 0, double HandlerAverageUs = 0,
    double HandlerMaxUs = 0, uint ParseFailures = 0, string BatteryRefreshStatus = "unknown", string BatterySuspensionReason = "",
    BleRateStabilitySnapshot? Stability = null, bool CompatibilityTrialAttempted = false)
{
    public bool GameplayReady => Pro2GameplayRatePolicy.CanPlay(Running, Connected, Fd2Hz, ParsedHz,
        Fd2Count, ParsedCount, LastInputAgeMs);
}

public sealed partial class Pro2BleInputSource
{
    private double liveFd2RateHz;
    private NotifyGapSummary lastLiveGapSummary;
    private readonly BleRateStabilityMonitor rateStability = new(Stopwatch.Frequency);
    public bool IsGameplayReady => GetRateEvidence().GameplayReady;

    /// <summary>Cached structured evidence; no extra Notify subscription or parameter requests.</summary>
    public Pro2RateEvidence GetRateEvidence()
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            bool recent = liveRateMeasuredTicks != 0 &&
                Stopwatch.GetElapsedTime(liveRateMeasuredTicks, now).TotalSeconds <= 15;
            double fd2Hz = recent ? liveFd2RateHz : SampleRate(fd2InputNotifyCount, firstFd2InputTicks, lastFd2InputTicks);
            double parsedHz = recent ? liveParsedRateHz : fd2InputNotifyCount == 0 ? 0 :
                fd2Hz * fd2InputParsedCount / fd2InputNotifyCount;
            double ageMs = lastParsedNotifyTicks == 0 ? double.PositiveInfinity :
                Stopwatch.GetElapsedTime(lastParsedNotifyTicks, now).TotalMilliseconds;
            return new(connectionPhase, IsRunning, transportConnected, IsHighRateReady,
                fd2Hz, parsedHz, fd2InputNotifyCount, fd2InputParsedCount, ageMs, connectionIntervalUnits,
                fd2ReportRateStatus, connectionPreferenceStatus, win11ParameterRouteStatus,
                recent ? lastLiveGapSummary : default, recent, notifyHandlerCount,
                NotifyHandlerAverageMicrosecondsNoLock(), TicksToMicroseconds(notifyHandlerMaxTicks), parseFailCount,
                runtimeBatteryRefreshStatus, runtimeBatterySuspensionReason, rateStability.Snapshot(now),
                win11CompatibilityTrialAttempted || adaptiveOptimizationRunAttempted);
        }
    }
}
