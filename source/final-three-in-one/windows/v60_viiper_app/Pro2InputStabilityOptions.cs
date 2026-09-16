using System;

namespace Y700Switch2V60Viiper;

public sealed class Pro2InputStabilityOptions
{
    public int AxisNormalDeltaThreshold { get; init; } = 500;
    public int AxisSpikeDeltaThreshold { get; init; } = 900;
    public int AxisReturnToGoodThreshold { get; init; } = 280;
    public double AxisMinConfirmMs { get; init; } = 110;
    public double AxisMaxDeltaPerMs { get; init; } = 75;
    public bool AxisRampEnabled { get; init; } = true;
    public double AxisRampMaxDeltaPerMs { get; init; } = 48;
    public double AxisIdleMinConfirmMs { get; init; } = 110;
    public double AxisActiveMinConfirmMs { get; init; } = 0;
    public double AxisFastReversalConfirmMs { get; init; } = 0;
    public double AxisIdleRampMaxDeltaPerMs { get; init; } = 48;
    public double AxisActiveRampMaxDeltaPerMs { get; init; } = 135;
    public double AxisFastReversalRampMaxDeltaPerMs { get; init; } = 220;
    public double AxisMotionActiveWindowMs { get; init; } = 140;
    public bool AxisLowLatencyMotionBypassEnabled { get; init; } = true;
    public int AxisLowLatencyBypassDeltaThreshold { get; init; } = 700;
    public double AxisLinkRecoveryGapMs { get; init; } = 80;
    public int AxisMotionDeltaThreshold { get; init; } = 180;
    public int AxisCenterCrossThreshold { get; init; } = 260;
    public double AxisInputSwallowDetectMs { get; init; } = 50;
    public bool RawIntegrityModeEnabled { get; init; } =
        IsEnvironmentFlagEnabled("PRO2_RAW_INTEGRITY_MODE");
    public bool AxisSpikeLogEnabled { get; init; } = true;
    public bool AxisSpikeRawDumpEnabled { get; init; } = true;
    public int AxisSpikeRingBufferFramesBefore { get; init; } = 8;
    public int AxisSpikeRingBufferFramesAfter { get; init; } = 8;
    public int AxisSpikeRingBufferCapacity { get; init; } = 64;
    public int AxisSpikeDumpRateLimitPer10Seconds { get; init; } = 3;

    public static Pro2InputStabilityOptions Default { get; } = new();

    public string Summary =>
        "AxisNormalDeltaThreshold=" + AxisNormalDeltaThreshold +
        " AxisSpikeDeltaThreshold=" + AxisSpikeDeltaThreshold +
        " AxisReturnToGoodThreshold=" + AxisReturnToGoodThreshold +
        " AxisMinConfirmMs=" + AxisMinConfirmMs.ToString("F1") +
        " AxisMaxDeltaPerMs=" + AxisMaxDeltaPerMs.ToString("F1") +
        " AxisRampEnabled=" + AxisRampEnabled +
        " AxisRampMaxDeltaPerMs=" + AxisRampMaxDeltaPerMs.ToString("F1") +
        " AxisIdleMinConfirmMs=" + AxisIdleMinConfirmMs.ToString("F1") +
        " AxisActiveMinConfirmMs=" + AxisActiveMinConfirmMs.ToString("F1") +
        " AxisFastReversalConfirmMs=" + AxisFastReversalConfirmMs.ToString("F1") +
        " AxisIdleRampMaxDeltaPerMs=" + AxisIdleRampMaxDeltaPerMs.ToString("F1") +
        " AxisActiveRampMaxDeltaPerMs=" + AxisActiveRampMaxDeltaPerMs.ToString("F1") +
        " AxisFastReversalRampMaxDeltaPerMs=" + AxisFastReversalRampMaxDeltaPerMs.ToString("F1") +
        " AxisMotionActiveWindowMs=" + AxisMotionActiveWindowMs.ToString("F1") +
        " AxisLowLatencyMotionBypassEnabled=" + AxisLowLatencyMotionBypassEnabled +
        " AxisLowLatencyBypassDeltaThreshold=" + AxisLowLatencyBypassDeltaThreshold +
        " AxisLinkRecoveryGapMs=" + AxisLinkRecoveryGapMs.ToString("F1") +
        " AxisMotionDeltaThreshold=" + AxisMotionDeltaThreshold +
        " AxisCenterCrossThreshold=" + AxisCenterCrossThreshold +
        " AxisInputSwallowDetectMs=" + AxisInputSwallowDetectMs.ToString("F1") +
        " RawIntegrityModeEnabled=" + RawIntegrityModeEnabled +
        " AxisSpikeLogEnabled=" + AxisSpikeLogEnabled +
        " AxisSpikeRawDumpEnabled=" + AxisSpikeRawDumpEnabled +
        " AxisSpikeRingBufferFramesBefore=" + AxisSpikeRingBufferFramesBefore +
        " AxisSpikeRingBufferFramesAfter=" + AxisSpikeRingBufferFramesAfter +
        " AxisSpikeRingBufferCapacity=" + AxisSpikeRingBufferCapacity +
        " AxisSpikeDumpRateLimitPer10Seconds=" + AxisSpikeDumpRateLimitPer10Seconds;

    public static Pro2InputStabilityOptions Normalize(Pro2InputStabilityOptions? options)
    {
        options ??= Default;
        return new Pro2InputStabilityOptions
        {
            AxisNormalDeltaThreshold = Clamp(options.AxisNormalDeltaThreshold, 1, GamepadState.AxisMax),
            AxisSpikeDeltaThreshold = Clamp(
                Math.Max(options.AxisSpikeDeltaThreshold, options.AxisNormalDeltaThreshold + 1),
                2,
                GamepadState.AxisMax),
            AxisReturnToGoodThreshold = Clamp(options.AxisReturnToGoodThreshold, 1, GamepadState.AxisMax),
            AxisMinConfirmMs = Clamp(options.AxisMinConfirmMs, 30, 250),
            AxisMaxDeltaPerMs = Clamp(options.AxisMaxDeltaPerMs, 5, 300),
            AxisRampEnabled = options.AxisRampEnabled,
            AxisRampMaxDeltaPerMs = Clamp(options.AxisRampMaxDeltaPerMs, 5, 300),
            AxisIdleMinConfirmMs = Clamp(options.AxisIdleMinConfirmMs, 30, 250),
            AxisActiveMinConfirmMs = Clamp(options.AxisActiveMinConfirmMs, 0, 90),
            AxisFastReversalConfirmMs = Clamp(options.AxisFastReversalConfirmMs, 0, 80),
            AxisIdleRampMaxDeltaPerMs = Clamp(options.AxisIdleRampMaxDeltaPerMs, 5, 300),
            AxisActiveRampMaxDeltaPerMs = Clamp(options.AxisActiveRampMaxDeltaPerMs, 20, 500),
            AxisFastReversalRampMaxDeltaPerMs = Clamp(options.AxisFastReversalRampMaxDeltaPerMs, 20, 600),
            AxisMotionActiveWindowMs = Clamp(options.AxisMotionActiveWindowMs, 30, 500),
            AxisLowLatencyMotionBypassEnabled = options.AxisLowLatencyMotionBypassEnabled,
            AxisLowLatencyBypassDeltaThreshold = Clamp(options.AxisLowLatencyBypassDeltaThreshold, 1, GamepadState.AxisMax),
            AxisLinkRecoveryGapMs = Clamp(options.AxisLinkRecoveryGapMs, 45, 500),
            AxisMotionDeltaThreshold = Clamp(options.AxisMotionDeltaThreshold, 1, GamepadState.AxisMax),
            AxisCenterCrossThreshold = Clamp(options.AxisCenterCrossThreshold, 1, GamepadState.AxisMax / 2),
            AxisInputSwallowDetectMs = Clamp(options.AxisInputSwallowDetectMs, 15, 250),
            RawIntegrityModeEnabled = options.RawIntegrityModeEnabled,
            AxisSpikeLogEnabled = options.AxisSpikeLogEnabled,
            AxisSpikeRawDumpEnabled = options.AxisSpikeRawDumpEnabled,
            AxisSpikeRingBufferFramesBefore = Clamp(options.AxisSpikeRingBufferFramesBefore, 0, 64),
            AxisSpikeRingBufferFramesAfter = Clamp(options.AxisSpikeRingBufferFramesAfter, 0, 64),
            AxisSpikeRingBufferCapacity = Clamp(options.AxisSpikeRingBufferCapacity, 32, 512),
            AxisSpikeDumpRateLimitPer10Seconds = Clamp(options.AxisSpikeDumpRateLimitPer10Seconds, 0, 60)
        };
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min;
        }
        return Math.Clamp(value, min, max);
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return value != null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
