using System;

namespace Y700Switch2V60Viiper;

public sealed record Pro2StickCalibrationProfile
{
    private const ushort FactoryCenter = GamepadState.AxisCenter;
    private const int FallbackDirectionalRange = 1600;
    private const int MinimumPersistedDirectionalRange = 256;

    public bool CenterCalibrated { get; init; }
    public bool RangeCalibrated { get; init; }
    public ushort CenterLx { get; init; } = FactoryCenter;
    public ushort CenterLy { get; init; } = FactoryCenter;
    public ushort CenterRx { get; init; } = FactoryCenter;
    public ushort CenterRy { get; init; } = FactoryCenter;
    public ushort MinLx { get; init; } = FactoryCenter - FallbackDirectionalRange;
    public ushort MaxLx { get; init; } = FactoryCenter + FallbackDirectionalRange;
    public ushort MinLy { get; init; } = FactoryCenter - FallbackDirectionalRange;
    public ushort MaxLy { get; init; } = FactoryCenter + FallbackDirectionalRange;
    public ushort MinRx { get; init; } = FactoryCenter - FallbackDirectionalRange;
    public ushort MaxRx { get; init; } = FactoryCenter + FallbackDirectionalRange;
    public ushort MinRy { get; init; } = FactoryCenter - FallbackDirectionalRange;
    public ushort MaxRy { get; init; } = FactoryCenter + FallbackDirectionalRange;
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public string Summary =>
        "center=" + CenterLx + "," + CenterLy + "," + CenterRx + "," + CenterRy +
        " range=" +
        MinLx + ".." + MaxLx + "," +
        MinLy + ".." + MaxLy + "," +
        MinRx + ".." + MaxRx + "," +
        MinRy + ".." + MaxRy +
        " center_calibrated=" + CenterCalibrated.ToString().ToLowerInvariant() +
        " range_calibrated=" + RangeCalibrated.ToString().ToLowerInvariant();

    public static Pro2StickCalibrationProfile Normalize(
        Pro2StickCalibrationProfile? source)
    {
        if (source == null || !source.CenterCalibrated)
        {
            return new Pro2StickCalibrationProfile();
        }

        ushort centerLx = NormalizeCenter(source.CenterLx);
        ushort centerLy = NormalizeCenter(source.CenterLy);
        ushort centerRx = NormalizeCenter(source.CenterRx);
        ushort centerRy = NormalizeCenter(source.CenterRy);
        bool rangeValid =
            source.RangeCalibrated &&
            IsRangeValid(source.MinLx, centerLx, source.MaxLx) &&
            IsRangeValid(source.MinLy, centerLy, source.MaxLy) &&
            IsRangeValid(source.MinRx, centerRx, source.MaxRx) &&
            IsRangeValid(source.MinRy, centerRy, source.MaxRy);

        return source with
        {
            CenterCalibrated = true,
            RangeCalibrated = rangeValid,
            CenterLx = centerLx,
            CenterLy = centerLy,
            CenterRx = centerRx,
            CenterRy = centerRy,
            MinLx = rangeValid ? source.MinLx : FallbackMinimum(centerLx),
            MaxLx = rangeValid ? source.MaxLx : FallbackMaximum(centerLx),
            MinLy = rangeValid ? source.MinLy : FallbackMinimum(centerLy),
            MaxLy = rangeValid ? source.MaxLy : FallbackMaximum(centerLy),
            MinRx = rangeValid ? source.MinRx : FallbackMinimum(centerRx),
            MaxRx = rangeValid ? source.MaxRx : FallbackMaximum(centerRx),
            MinRy = rangeValid ? source.MinRy : FallbackMinimum(centerRy),
            MaxRy = rangeValid ? source.MaxRy : FallbackMaximum(centerRy)
        };
    }

    private static ushort NormalizeCenter(ushort value)
    {
        return value is >= 1024 and <= 3071 ? value : FactoryCenter;
    }

    private static bool IsRangeValid(ushort minimum, ushort center, ushort maximum)
    {
        return minimum < center &&
               maximum > center &&
               center - minimum >= MinimumPersistedDirectionalRange &&
               maximum - center >= MinimumPersistedDirectionalRange;
    }

    private static ushort FallbackMinimum(ushort center)
    {
        return (ushort)Math.Max(0, center - FallbackDirectionalRange);
    }

    private static ushort FallbackMaximum(ushort center)
    {
        return (ushort)Math.Min(GamepadState.AxisMax, center + FallbackDirectionalRange);
    }
}

public readonly record struct Pro2PhysicalStickAxes(
    ushort Lx,
    ushort Ly,
    ushort Rx,
    ushort Ry)
{
    public string TelemetryValue =>
        "lx=" + Lx + " ly=" + Ly + " rx=" + Rx + " ry=" + Ry;
}

public sealed record Pro2StickCalibrationResult(
    bool Success,
    string Message,
    Pro2StickCalibrationProfile? Profile);
