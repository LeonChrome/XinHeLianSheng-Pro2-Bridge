using System;

namespace Y700Switch2V60Viiper;

/// <summary>Independent inner deadzones, applied before virtual packet mapping.
/// Raw reports, physical calibration and IMU samples are not changed.</summary>
public sealed record StickDeadzoneSettings(double LeftPercent = 4, double RightPercent = 4)
{
    public static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(Math.Round(value, 1), 0, 20) : 4;
    public StickDeadzoneSettings Normalize() => new(Normalize(LeftPercent), Normalize(RightPercent));
    public string Telemetry => FormattableString.Invariant($"left_percent={LeftPercent:F1} right_percent={RightPercent:F1} scope=all_output_modes raw_reports_unchanged=true");
}
