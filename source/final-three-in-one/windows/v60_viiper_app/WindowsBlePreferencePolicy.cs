using System;

namespace Y700Switch2V60Viiper;

public enum WindowsBlePreferenceDecision
{
    Request,
    RequestPartialImprovement,
    DeferUntilMeasured,
    SkipSlowerThanLive,
    SkipIncompatiblePreset,
    SkipNoMeasuredImprovement
}

/// <summary>
/// Request success is not input-rate success. The 133Hz path first measures
/// untouched FD2, then refuses a preset that could impose a longer event cadence.
/// This is a conservative negotiation policy, not a claim that BLE allows only
/// one notification per event. Only measured raw and parsed FD2 establish success.
/// </summary>
public static class WindowsBlePreferencePolicy
{
    public static int NativeObservationMilliseconds(bool windows11, bool pure133) =>
        windows11 && pure133 ? 10000 : 1800;

    public static WindowsBlePreferenceDecision Decide(
        bool pure133, string phase, uint currentIntervalUnits,
        uint preferredMinUnits, uint preferredMaxUnits, uint preferredLatency,
        double measuredNativeHz = double.NaN)
    {
        if (pure133 && phase != "fallback")
            return WindowsBlePreferenceDecision.DeferUntilMeasured;
        if (currentIntervalUnits != 0 && preferredMaxUnits > currentIntervalUnits)
            return WindowsBlePreferenceDecision.SkipSlowerThanLive;
        if (!pure133) return WindowsBlePreferenceDecision.Request;
        if (preferredMinUnits < 6 || preferredMaxUnits < preferredMinUnits || preferredLatency != 0)
            return WindowsBlePreferenceDecision.SkipIncompatiblePreset;
        if (currentIntervalUnits != 0 && preferredMaxUnits == currentIntervalUnits)
            return WindowsBlePreferenceDecision.SkipNoMeasuredImprovement;
        if (preferredMinUnits == 6 && preferredMaxUnits == 6)
            return WindowsBlePreferenceDecision.Request;
        // A 45ms -> 15ms improvement is useful even though it does NOT pass the
        // 133Hz requirement. Never confuse the request with measured acceptance.
        if (currentIntervalUnits > preferredMaxUnits ||
            (currentIntervalUnits == 0 && double.IsFinite(measuredNativeHz) &&
                measuredNativeHz > 0 && measuredNativeHz < 800.0 / preferredMaxUnits))
            return WindowsBlePreferenceDecision.RequestPartialImprovement;
        return WindowsBlePreferenceDecision.SkipNoMeasuredImprovement;
    }
}
