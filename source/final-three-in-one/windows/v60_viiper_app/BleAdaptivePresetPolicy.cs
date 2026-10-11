using System;
using System.Collections.Generic;
using System.Linq;

namespace Y700Switch2V60Viiper;

public enum BleAdaptivePreset
{
    Native,
    Win11Fixed7p5,
    Win11Flexible7p5To15,
    Win11ThroughputOptimized,
    Win11Fixed15
}

public sealed record BleAdaptiveCapabilities(bool Win11, bool RequestApi, bool ReadApi,
    bool Win10AuditedStack = false, bool Win10ExperimentalStack = false);

public sealed record BleAdaptivePresetCandidate(BleAdaptivePreset Preset, string Id, string DisplayName,
    ushort? MinUnits, ushort? MaxUnits, ushort? Latency, ushort? TimeoutUnits, bool IsExperimental);

public sealed record BleAdaptivePresetPlan(IReadOnlyList<BleAdaptivePresetCandidate> Candidates,
    bool NativeTargetAlreadyMet, bool Win10OptInEligible, string Code);

/// <summary>Age is the age of the completed window, not the latest input packet.</summary>
public sealed record BleAdaptiveMeasurement(BleMeasuredTrialSample Sample,
    double CompletedAgeSeconds = 0, double? MaximumGapMs = null)
{
    public double EffectiveHz => Sample.EffectiveHz;
}

public sealed record BleAdaptivePresetDecision(bool Retain, string Code, string Summary);

/// <summary>
/// Every field participates in an exact match. An adapter family name alone is
/// not an environment identity. AdapterSetFingerprint also distinguishes multi-radio systems.
/// </summary>
public sealed record BleAdaptiveEnvironmentKey(int WindowsBuild, string BluetoothStackSha256,
    string AdapterPnpId, string AdapterSetFingerprint, string DriverProvider, string DriverVersion,
    string DriverDate, string PolicyRevision = "ble-adaptive-v1");

/// <summary>A preference to retest first, never permission to skip measurement or install/configure a driver.</summary>
public sealed record BleAdaptiveCachedPreset(BleAdaptiveEnvironmentKey Environment, BleAdaptivePreset Preset,
    BleAdaptiveMeasurement Native, BleAdaptiveMeasurement Verified, DateTimeOffset VerifiedAtUtc);

/// <summary>
/// Bounded, transport-free policy. No native API, driver, registry, pairing,
/// filesystem, timer or synthetic input operation is performed by this class.
/// </summary>
public static class BleAdaptivePresetPolicy
{
    public const int MaximumTrials = 4;
    public const double TargetHz = 128;
    public const double PlayableHz = 60;
    public const double MinimumWindowSeconds = 10;
    public const double MaximumWindowSeconds = 30;
    public const double MaximumWindowAgeSeconds = 15;
    public const double RateNoiseToleranceHz = 2;
    public const int CacheLifetimeDays = 7;

    private static readonly BleAdaptivePresetCandidate[] Windows11Candidates =
    {
        new(BleAdaptivePreset.Win11Fixed7p5, "win11_fixed_7p5", "固定 7.5ms", 6, 6, 0, 600, true),
        new(BleAdaptivePreset.Win11Flexible7p5To15, "win11_flexible_7p5_15", "7.5–15ms 协商", 6, 12, 0, 600, true),
        new(BleAdaptivePreset.Win11ThroughputOptimized, "win11_throughput", "系统吞吐量优先", null, null, null, null, false),
        new(BleAdaptivePreset.Win11Fixed15, "win11_fixed_15", "固定 15ms", 12, 12, 0, 600, true)
    };

    public static BleAdaptivePresetPlan CreatePlan(BleAdaptiveCapabilities capabilities,
        BleAdaptiveMeasurement native, BleAdaptivePreset? preferredPreset = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(native);
        bool win10Eligible = !capabilities.Win11 && (capabilities.Win10AuditedStack || capabilities.Win10ExperimentalStack);
        if (!Valid(native, requireRequestSuccess: false))
            return new(Array.Empty<BleAdaptivePresetCandidate>(), false, win10Eligible, "native_measurement_not_ready");
        if (MeetsTarget(native))
            return new(Array.Empty<BleAdaptivePresetCandidate>(), true, false, "native_target_preserved");
        if (!capabilities.Win11)
            return new(Array.Empty<BleAdaptivePresetCandidate>(), false, win10Eligible,
                capabilities.Win10AuditedStack ? "win10_audited_configuration_requires_opt_in" :
                    capabilities.Win10ExperimentalStack ? "win10_experimental_configuration_requires_opt_in" : "win10_native_only");
        // System-reported interval is useful evidence, not a prerequisite for
        // measuring actual FD2 notifications or trying a device-scoped request.
        if (!capabilities.RequestApi)
            return new(Array.Empty<BleAdaptivePresetCandidate>(), false, false, "win11_api_unavailable");

        // A previously measured exact-environment preference only changes order.
        // Its current connection is still measured and each candidate is tried once.
        var ordered = Windows11Candidates.OrderBy(c => preferredPreset.HasValue && c.Preset == preferredPreset.Value ? 0 : 1)
            .ToArray();
        return new(Array.AsReadOnly(ordered), false, false, "win11_bounded_presets");
    }

    public static bool CanOptimizeInApp(bool win11, bool requestApi, bool connected,
        bool busy, bool measuring, bool alreadyTried, double measuredFd2Hz) =>
        win11 && requestApi && connected && !busy && !measuring && !alreadyTried &&
        double.IsFinite(measuredFd2Hz) && measuredFd2Hz > 0 && measuredFd2Hz < TargetHz;

    public static bool IsValidMeasurement(BleAdaptiveMeasurement measurement) =>
        measurement != null && Valid(measurement, requireRequestSuccess: false);

    public static bool MeetsTarget(BleAdaptiveMeasurement measurement) =>
        IsValidMeasurement(measurement) && measurement.Sample.Fd2Hz >= TargetHz && measurement.Sample.ParsedHz >= TargetHz;

    public static bool IsPlayable(BleAdaptiveMeasurement measurement) =>
        IsValidMeasurement(measurement) && measurement.EffectiveHz >= PlayableHz;

    /// <summary>Positive means left is preferable. Invalid or old windows never outrank live evidence.</summary>
    public static int CompareMeasurements(BleAdaptiveMeasurement left, BleAdaptiveMeasurement right)
    {
        bool validLeft = IsValidMeasurement(left), validRight = IsValidMeasurement(right);
        if (validLeft != validRight) return validLeft ? 1 : -1;
        if (!validLeft) return 0;
        bool targetLeft = MeetsTarget(left), targetRight = MeetsTarget(right);
        if (targetLeft != targetRight) return targetLeft ? 1 : -1;
        double difference = left.EffectiveHz - right.EffectiveHz;
        if (Math.Abs(difference) > RateNoiseToleranceHz) return Math.Sign(difference);
        int gaps = CompareKnownGap(left.Sample.P95GapMs, right.Sample.P95GapMs);
        if (gaps != 0) return gaps;
        gaps = CompareKnownGap(left.MaximumGapMs, right.MaximumGapMs);
        if (gaps != 0) return gaps;
        return Math.Sign(difference);
    }

    /// <summary>
    /// Compare with both native and the best retained candidate, so a later
    /// preset cannot replace a better earlier one just because it beats native.
    /// A two-Hz allowance handles measurement noise, not substantial regression.
    /// </summary>
    public static BleAdaptivePresetDecision EvaluateCandidate(BleAdaptiveMeasurement native,
        BleAdaptiveMeasurement incumbent, BleAdaptiveMeasurement candidate)
    {
        BleAdaptivePresetDecision Reject(string code, string summary) => new(false, code, summary);
        if (!Valid(native, false) || !Valid(incumbent, false) || !Valid(candidate, true))
            return Reject("invalid_or_stale_measurement", "连接、输入新鲜度、完整窗口或解析完整性未通过，不保留该预设。");
        if (!MatchingWindows(native, candidate) || !MatchingWindows(incumbent, candidate))
            return Reject("unequal_windows", "前后窗口时长不匹配，不保留该预设。");
        if (MeetsTarget(native))
            return Reject("native_target_preserved", "原生输入已达标，保持原生连接。");
        if (Regressed(native, candidate) || Regressed(incumbent, candidate))
            return Reject("measured_rate_regressed", "FD2 或 Parsed 明显低于原生／已保留预设，恢复此前最佳路线。");
        if (GapRegressed(incumbent, candidate))
            return Reject("measured_stability_regressed", "通知间隙明显恶化，不以较高平均频率掩盖卡顿。");
        if (CompareMeasurements(candidate, incumbent) <= 0)
            return Reject("incumbent_is_better", "真实输入未优于已保留路线，恢复此前最佳路线。");
        if (MeetsTarget(candidate))
            return new(true, "target_reached", "真实 FD2 / Parsed 达到 133Hz 级门槛，进入最终完整窗口复核。");
        double increase = candidate.EffectiveHz - incumbent.EffectiveHz;
        if (increase >= 3 - 1e-9 && increase >= incumbent.EffectiveHz * .05 - 1e-9)
            return new(true, IsPlayable(candidate) ? "playable_improvement" : "partial_improvement",
                "真实输入明显改善，暂保留；继续尝试剩余预设，不把部分改善标为 133Hz。");
        if (Math.Abs(increase) <= RateNoiseToleranceHz && GapImproved(incumbent, candidate))
            return new(true, "stability_improvement", "频率处于测量容差内，但通知间隙明显改善，暂保留较稳定路线。");
        return Reject("no_meaningful_improvement", "没有达到可确认的频率或稳定性改善，恢复此前最佳路线。");
    }

    /// <summary>Final ten-second verify is required even after an earlier target window.</summary>
    public static BleAdaptivePresetDecision VerifyFinal(BleAdaptiveMeasurement retained,
        BleAdaptiveMeasurement final)
    {
        if (!Valid(retained, false) || !Valid(final, false) || !MatchingWindows(retained, final))
            return new(false, "final_measurement_invalid", "最终完整窗口无效，不能宣称稳定达标。");
        if (Regressed(retained, final) || GapRegressed(retained, final) || MeetsTarget(retained) && !MeetsTarget(final))
            return new(false, "final_verification_regressed", "最终复核发生降速或明显卡顿，不能宣称稳定 133Hz。");
        return new(true, MeetsTarget(final) ? "final_target_verified" : IsPlayable(final) ? "final_playable_verified" : "final_low_rate_verified",
            MeetsTarget(final) ? "最终窗口真实输入达到 133Hz 级；后续仍持续监测。" :
            IsPlayable(final) ? "最终窗口可游玩，但没有达到 133Hz 级。" : "已记录当前最佳低速路线，不承诺最低 66Hz。");
    }

    public static bool IsCacheUsable(BleAdaptiveCachedPreset entry, BleAdaptiveEnvironmentKey current,
        DateTimeOffset nowUtc, out string code)
    {
        code = "cache_invalid";
        if (entry == null || current == null || entry.Environment == null ||
            !ValidEnvironment(current) || !ValidEnvironment(entry.Environment)) return false;
        if (entry.Environment != current) { code = "cache_environment_changed"; return false; }
        if (!Windows11Candidates.Any(c => c.Preset == entry.Preset)) return false;
        double ageDays = (nowUtc - entry.VerifiedAtUtc).TotalDays;
        if (ageDays < 0 || ageDays > CacheLifetimeDays) { code = "cache_expired"; return false; }
        var result = EvaluateCandidate(entry.Native, entry.Native, entry.Verified);
        if (!result.Retain) { code = "cache_not_measured_improvement"; return false; }
        code = "cache_exact_environment_retest_preference";
        return true;
    }

    private static bool Valid(BleAdaptiveMeasurement value, bool requireRequestSuccess)
    {
        if (value?.Sample == null) return false;
        var s = value.Sample;
        return s.Connected && (!requireRequestSuccess || s.RequestedSuccessfully) &&
            double.IsFinite(s.DurationSeconds) && s.DurationSeconds >= MinimumWindowSeconds && s.DurationSeconds <= MaximumWindowSeconds &&
            s.Fd2Count >= BleMeasuredTrialPolicy.MinimumSamples && s.ParsedCount >= BleMeasuredTrialPolicy.MinimumSamples &&
            s.ParsedCount <= s.Fd2Count && s.ParsedHz >= s.Fd2Hz * BleMeasuredTrialPolicy.MinimumParsedRatio &&
            double.IsFinite(s.LastInputAgeMs) && s.LastInputAgeMs >= 0 && s.LastInputAgeMs < BleMeasuredTrialPolicy.MaximumInputAgeMs &&
            double.IsFinite(value.CompletedAgeSeconds) && value.CompletedAgeSeconds >= 0 && value.CompletedAgeSeconds <= MaximumWindowAgeSeconds &&
            ValidGap(s.P95GapMs) && ValidGap(value.MaximumGapMs) &&
            (!s.P95GapMs.HasValue || !value.MaximumGapMs.HasValue || value.MaximumGapMs >= s.P95GapMs);
    }

    private static bool ValidGap(double? gap) => !gap.HasValue || double.IsFinite(gap.Value) && gap.Value >= 0;
    private static bool MatchingWindows(BleAdaptiveMeasurement a, BleAdaptiveMeasurement b) =>
        Math.Abs(a.Sample.DurationSeconds - b.Sample.DurationSeconds) <=
        Math.Min(a.Sample.DurationSeconds, b.Sample.DurationSeconds) * BleMeasuredTrialPolicy.MaximumWindowDifferenceRatio;
    private static bool Regressed(BleAdaptiveMeasurement before, BleAdaptiveMeasurement after) =>
        after.Sample.Fd2Hz < before.Sample.Fd2Hz - RateNoiseToleranceHz - 1e-9 ||
        after.Sample.ParsedHz < before.Sample.ParsedHz - RateNoiseToleranceHz - 1e-9;
    private static int CompareKnownGap(double? left, double? right) =>
        left.HasValue && right.HasValue ? -left.Value.CompareTo(right.Value) : 0;
    private static bool GapRegressed(BleAdaptiveMeasurement before, BleAdaptiveMeasurement after) =>
        IncreasedGap(before.Sample.P95GapMs, after.Sample.P95GapMs, 2, .25) ||
        IncreasedGap(before.MaximumGapMs, after.MaximumGapMs, 15, .5);
    private static bool GapImproved(BleAdaptiveMeasurement before, BleAdaptiveMeasurement after) =>
        ReducedGap(before.Sample.P95GapMs, after.Sample.P95GapMs, 1, .1) ||
        ReducedGap(before.MaximumGapMs, after.MaximumGapMs, 5, .1);
    private static bool IncreasedGap(double? before, double? after, double absolute, double ratio) =>
        before.HasValue && after.HasValue && after.Value > before.Value + Math.Max(absolute, before.Value * ratio);
    private static bool ReducedGap(double? before, double? after, double absolute, double ratio) =>
        before.HasValue && after.HasValue && after.Value <= before.Value - Math.Max(absolute, before.Value * ratio);
    private static bool ValidEnvironment(BleAdaptiveEnvironmentKey key) =>
        key.WindowsBuild >= 22000 && key.BluetoothStackSha256 != null && key.BluetoothStackSha256.Length == 64 &&
        key.BluetoothStackSha256.All(Uri.IsHexDigit) && Known(key.AdapterPnpId) && Known(key.AdapterSetFingerprint) &&
        Known(key.DriverProvider) && Known(key.DriverVersion) && Known(key.DriverDate) && Known(key.PolicyRevision);
    private static bool Known(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 &&
        !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !value.Equals("unavailable", StringComparison.OrdinalIgnoreCase);
}
