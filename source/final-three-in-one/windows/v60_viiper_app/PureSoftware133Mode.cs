using System;

namespace Y700Switch2V60Viiper;

internal static class PureSoftware133Mode
{
    private const string EnvironmentVariable = "PRO2_PURE_SOFTWARE_133";

    public const double MinimumAcceptedHz = 120.0;
    public const double MaximumRepeatedStateRatio = 0.01;

#if PURE_SOFTWARE_RELEASE
    public const bool IsReleaseBuild = true;
#else
    public const bool IsReleaseBuild = false;
#endif

    public static bool IsEnabled => IsReleaseBuild || IsTrue(
        Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static void EnableForCurrentProcess()
    {
        Environment.SetEnvironmentVariable(
            EnvironmentVariable,
            "1",
            EnvironmentVariableTarget.Process);
    }

    private static bool IsTrue(string? value) =>
        value != null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
