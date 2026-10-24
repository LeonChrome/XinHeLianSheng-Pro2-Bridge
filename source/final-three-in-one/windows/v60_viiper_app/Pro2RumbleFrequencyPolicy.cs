using System;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Separates measured/estimated source Hz from opaque Pro2 protocol controls.
/// SDL supplies reference controls, but no independently validated physical
/// Hz-to-control equation. Until measured, retain the existing bounded variable
/// proxy explicitly; it must never be reported as calibrated physical frequency.
/// </summary>
public static class Pro2RumbleFrequencyPolicy
{
    public const string Mapping = "legacy_hz_proxy_unvalidated";
    // SDL_hidapi_switch2.c: HIDAPI_DriverSwitch2_OpenJoystick (not Hz).
    public const ushort ReferenceLowControl = 0x112;
    public const ushort ReferenceHighControl = 0x187;
    public const ushort MaximumLegacyControl = 511;

    public static ushort FromEstimatedHz(double hz, double minimumHz = 40, double maximumHz = 511)
    {
        double minimum = double.IsFinite(minimumHz) ? Math.Clamp(minimumHz, 1, MaximumLegacyControl) : 40;
        double maximum = double.IsFinite(maximumHz) ? Math.Clamp(maximumHz, minimum, MaximumLegacyControl) : MaximumLegacyControl;
        double finite = double.IsFinite(hz) ? hz : minimum;
        return (ushort)Math.Clamp(Math.Round(finite, MidpointRounding.AwayFromZero), minimum, maximum);
    }

    public static bool WouldClamp(double hz, double minimumHz = 40, double maximumHz = 511) =>
        !double.IsFinite(hz) || hz < minimumHz || hz > Math.Min(maximumHz, MaximumLegacyControl);
}
