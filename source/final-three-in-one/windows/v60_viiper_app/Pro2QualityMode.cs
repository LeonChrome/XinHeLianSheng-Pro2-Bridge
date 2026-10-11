using System;
using System.Linq;

namespace Y700Switch2V60Viiper;

public static class Pro2QualityMode
{
    public const string GyroArgument = "--gyro-quality-test";
    public const string HapticsArgument = "--haptics-quality-test";
    public const string DisableGyroArgument = "--gyro-quality-off";
    public const string DisableHapticsArgument = "--haptics-quality-off";
    // Pure-software releases use the accepted quality pipeline with no launcher
    // arguments. Non-release builds retain the former opt-in behavior.
    public static bool GyroEnabled { get; } = ResolveEnabled(Environment.GetCommandLineArgs(),
        GyroArgument, DisableGyroArgument, PureSoftware133Mode.IsReleaseBuild);
    public static bool HapticsEnabled { get; } = ResolveEnabled(Environment.GetCommandLineArgs(),
        HapticsArgument, DisableHapticsArgument, PureSoftware133Mode.IsReleaseBuild);
    public static bool IsEnabled(string[] arguments, string argument) =>
        arguments.Contains(argument, StringComparer.OrdinalIgnoreCase);
    public static bool ResolveEnabled(string[] arguments, string enableArgument, string disableArgument, bool defaultEnabled) =>
        !IsEnabled(arguments, disableArgument) && (defaultEnabled || IsEnabled(arguments, enableArgument));
}
