using System;

namespace Y700Switch2V60Viiper;

public static class InputContinuityPolicy
{
    public static readonly TimeSpan NormalInputAge = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan JitterInputAge = TimeSpan.FromMilliseconds(33);
    public static readonly TimeSpan MissedCycleInputAge = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan AgedInputAge = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan HoldLastStateAge = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SafeReleaseAge = TimeSpan.FromMilliseconds(750);

    public static GamepadState Resolve(
        GamepadState latest,
        TimeSpan age,
        out string source)
    {
        if (age <= NormalInputAge)
        {
            source = "pro2_ble_age_0_20";
            return latest;
        }

        if (age <= JitterInputAge)
        {
            source = "pro2_ble_age_20_33";
            return latest;
        }

        if (age <= MissedCycleInputAge)
        {
            source = "pro2_ble_age_33_50";
            return latest;
        }

        if (age <= AgedInputAge)
        {
            source = "pro2_ble_age_50_100";
            return latest;
        }

        if (age <= HoldLastStateAge)
        {
            source = "pro2_ble_age_gt100";
            return latest;
        }

        if (age <= SafeReleaseAge)
        {
            source = "pro2_ble_danger_safe_hold";
            return SafeHoldAnalog(latest);
        }

        source = "neutral";
        return GamepadState.Neutral();
    }

    private static GamepadState SafeHoldAnalog(GamepadState state)
    {
        GamepadState result = state.Clone();
        result.Buttons = GamepadButtons.None;
        result.L2 = 0;
        result.R2 = 0;
        result.AccelValid = false;
        result.GyroValid = false;
        return result;
    }
}
