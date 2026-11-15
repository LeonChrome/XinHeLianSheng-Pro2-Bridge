using System;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Optional capture identity for callers which retain monotonic capture bounds.
/// Bounds are seconds from the same monotonic clock; sequence identifies the
/// physical connection, not a preferred-parameter request.
/// </summary>
public sealed record BleFastConnectionWindow(BleAdaptiveMeasurement Measurement,
    double StartedSeconds, double EndedSeconds, long ConnectionSequence);

/// <summary>
/// Short real-input admission only. This does not establish long-session
/// stability, validate a saved preset, or permit a driver/registry change.
/// Callers of the measurement overload must capture sequential, non-overlapping
/// windows while guarding the same physical connection throughout both captures.
/// </summary>
public static class BleFastConnectionPolicy
{
    public const double MinimumWindowSeconds = 1.5;
    public const double MaximumWindowSeconds = 4;
    public const double HighRateHz = 128;
    public const double CompatibleRateHz = 60;
    public const double MinimumParsedRatio = .99;
    public const double MaximumCompletedAgeSeconds = 4;
    public const double MaximumCaptureSeparationSeconds = .25;

    public static bool IsHigh(BleAdaptiveMeasurement first, BleAdaptiveMeasurement second) =>
        Distinct(first, second) && Valid(first, HighRateHz, 40, 12, 40) &&
        Valid(second, HighRateHz, 40, 12, 40);

    public static bool IsCompatible(BleAdaptiveMeasurement first, BleAdaptiveMeasurement second) =>
        Distinct(first, second) && Valid(first, CompatibleRateHz, 60, 30, 60) &&
        Valid(second, CompatibleRateHz, 60, 30, 60);

    public static bool IsHigh(BleFastConnectionWindow first, BleFastConnectionWindow second) =>
        Contiguous(first, second) && IsHigh(first.Measurement, second.Measurement);

    public static bool IsCompatible(BleFastConnectionWindow first, BleFastConnectionWindow second) =>
        Contiguous(first, second) && IsCompatible(first.Measurement, second.Measurement);

    private static bool Distinct(BleAdaptiveMeasurement? first, BleAdaptiveMeasurement? second) =>
        first != null && second != null && !ReferenceEquals(first, second) &&
        first.Sample != null && second.Sample != null && !ReferenceEquals(first.Sample, second.Sample);

    private static bool Valid(BleAdaptiveMeasurement measurement, double minimumHz,
        double maximumInputAgeMs, double maximumP95Ms, double maximumGapMs)
    {
        BleMeasuredTrialSample sample = measurement.Sample;
        return sample != null && sample.Connected &&
            double.IsFinite(sample.DurationSeconds) && sample.DurationSeconds >= MinimumWindowSeconds &&
            sample.DurationSeconds <= MaximumWindowSeconds &&
            double.IsFinite(measurement.CompletedAgeSeconds) && measurement.CompletedAgeSeconds >= 0 &&
            measurement.CompletedAgeSeconds <= MaximumCompletedAgeSeconds &&
            sample.Fd2Count > 0 && sample.ParsedCount > 0 && sample.ParsedCount <= sample.Fd2Count &&
            sample.Fd2Hz >= minimumHz && sample.ParsedHz >= minimumHz &&
            sample.ParsedCount >= sample.Fd2Count * MinimumParsedRatio &&
            double.IsFinite(sample.LastInputAgeMs) && sample.LastInputAgeMs >= 0 &&
            sample.LastInputAgeMs < maximumInputAgeMs &&
            BoundedGap(sample.P95GapMs, maximumP95Ms) &&
            BoundedGap(measurement.MaximumGapMs, maximumGapMs) &&
            sample.P95GapMs!.Value <= measurement.MaximumGapMs!.Value;
    }

    private static bool BoundedGap(double? value, double maximum) =>
        value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= maximum;

    private static bool Contiguous(BleFastConnectionWindow? first, BleFastConnectionWindow? second) =>
        first != null && second != null && !ReferenceEquals(first, second) &&
        first.ConnectionSequence == second.ConnectionSequence &&
        ValidBounds(first) && ValidBounds(second) &&
        second.StartedSeconds >= first.EndedSeconds &&
        second.StartedSeconds - first.EndedSeconds <= MaximumCaptureSeparationSeconds;

    private static bool ValidBounds(BleFastConnectionWindow window) =>
        window.Measurement?.Sample != null && double.IsFinite(window.StartedSeconds) &&
        double.IsFinite(window.EndedSeconds) && window.StartedSeconds >= 0 &&
        window.EndedSeconds > window.StartedSeconds &&
        Math.Abs(window.EndedSeconds - window.StartedSeconds - window.Measurement.Sample.DurationSeconds) <= .02;
}
