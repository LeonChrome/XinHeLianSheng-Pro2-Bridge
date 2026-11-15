using System;

namespace Y700Switch2V60Viiper;

/// <summary>Playable input, near-target retention and high-rate acceptance are distinct.</summary>
public static class Pro2GameplayRatePolicy
{
    public const double MinimumGameplayHz = 60;
    public const double NearTargetHz = 120;
    public const double HighRateAcceptanceHz = 128;

    public static bool CanPlay(bool running, bool connected, double fd2Hz, double parsedHz,
        uint fd2Count, uint parsedCount, double lastInputAgeMs) => running && connected &&
        fd2Count >= 20 && parsedCount >= 20 && double.IsFinite(fd2Hz) && double.IsFinite(parsedHz) &&
        fd2Hz >= MinimumGameplayHz && parsedHz >= MinimumGameplayHz && parsedHz >= fd2Hz * .9 &&
        double.IsFinite(lastInputAgeMs) && lastInputAgeMs >= 0 && lastInputAgeMs < 500;
}
