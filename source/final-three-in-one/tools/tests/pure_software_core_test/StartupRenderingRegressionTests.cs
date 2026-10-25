using System;
using Y700Switch2V60Viiper;

internal static class StartupRenderingRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        check(!StartupRenderingPolicy.SelectSoftwareRendering(false, []), "standard startup keeps rendering default");
        check(StartupRenderingPolicy.SelectSoftwareRendering(true, []), "compatibility EXE needs no arguments");
        check(StartupRenderingPolicy.SelectSoftwareRendering(false, ["--software-rendering"]), "per-process software rendering opt-in");
        check(StartupRenderingPolicy.SelectSoftwareRendering(false, ["--SOFTWARE-RENDERING"]), "rendering argument case insensitive");
        check(!StartupRenderingPolicy.SelectSoftwareRendering(false, ["--software-rendering=false"]), "rendering argument exact match");
        check(!StartupRenderingPolicy.SelectSoftwareRendering(false, ["--ble-diagnostic"]), "diagnostic does not force software rendering");
        check(StartupRenderingPolicy.SelectSoftwareRendering(true, ["--hardware-rendering"]), "compatibility build cannot accidentally opt back into GPU path");
        check(StartupRenderingPolicy.Telemetry.Contains("scope=current_process") &&
            StartupRenderingPolicy.Telemetry.Contains("controller_path=unchanged"), "rendering diagnostic describes limited scope");
    }
}
