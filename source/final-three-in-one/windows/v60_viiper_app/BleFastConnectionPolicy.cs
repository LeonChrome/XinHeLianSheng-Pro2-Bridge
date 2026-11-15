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
    public const double HighRateHz = Pro2GameplayRatePolicy.HighRateAcceptanceHz;
    public const double WarmHighCandidateMinimumHz = Pro2GameplayRatePolicy.NearTargetHz;
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

    /// <summary>
    /// Preserve an already accepted, still-owned Win11 fixed-7.5ms request when
    /// initial input is usable and near target. This is compatibility admission,
    /// not IsHigh or proof of stable 133Hz. The caller must verify Win11 ownership
    /// and continue passive health monitoring without saving this as high proof.
    /// </summary>
    public static bool IsWarmHighCandidate(BleFastConnectionWindow first, BleFastConnectionWindow second,
        bool acceptedFixed7p5) => acceptedFixed7p5 && Contiguous(first, second) &&
        Distinct(first.Measurement, second.Measurement) &&
        first.Measurement.Sample.ConnectionIntervalUnits == 6 && second.Measurement.Sample.ConnectionIntervalUnits == 6 &&
        Valid(first.Measurement, WarmHighCandidateMinimumHz, 60, 16, 60) &&
        Valid(second.Measurement, WarmHighCandidateMinimumHz, 60, 16, 60);

    /// <summary>
    /// Suppress automatic preset cycling while warm-admitted input remains live
    /// at the already owned minimum interval. Manual optimization is separate;
    /// this never changes measured rates or promotes compatibility to IsHigh.
    /// </summary>
    public static bool ShouldPreserveMinimumInterval(bool admittedWarm, bool ownsFixed7p5,
        bool connected, bool running, bool recent, ushort interval, double fd2Hz,
        double parsedHz, double lastInputAgeMs) =>
        admittedWarm && ownsFixed7p5 && connected && running && recent && interval == 6 &&
        double.IsFinite(fd2Hz) && double.IsFinite(parsedHz) && fd2Hz >= WarmHighCandidateMinimumHz &&
        parsedHz >= WarmHighCandidateMinimumHz && parsedHz >= fd2Hz * MinimumParsedRatio && parsedHz <= fd2Hz &&
        double.IsFinite(lastInputAgeMs) && lastInputAgeMs >= 0 && lastInputAgeMs < 500;

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
