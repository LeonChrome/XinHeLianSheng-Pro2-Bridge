using System;
using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Media;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Process-local WPF rendering preference. Applied before constructing any UI;
/// never modifies Avalon registry settings, drivers, or the controller path.
/// SoftwareOnly avoids WPF's accelerated drawing path, not every native graphics
/// call or DLL load. It cannot recover an already-corrupted native process.
/// </summary>
public static class StartupRenderingPolicy
{
    public const string SoftwareArgument = "--software-rendering";
#if PRO2_COMPATIBILITY_RENDERING
    public static bool IsCompatibilityBuild => true;
#else
    public static bool IsCompatibilityBuild => false;
#endif
    public static bool SoftwareRequested { get; private set; }

    public static bool SelectSoftwareRendering(bool compatibilityBuild, IEnumerable<string> arguments)
    {
        if (compatibilityBuild) return true;
        foreach (string argument in arguments)
            if (string.Equals(argument, SoftwareArgument, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static void Apply(IEnumerable<string> arguments)
    {
        SoftwareRequested = SelectSoftwareRendering(IsCompatibilityBuild, arguments);
        // Leave a normal build's default untouched, including system overrides.
        if (SoftwareRequested) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    public static string Telemetry =>
        "build=" + (IsCompatibilityBuild ? "ui_compatible" : "standard") +
        " software_requested=" + SoftwareRequested +
        " preference=" + RenderOptions.ProcessRenderMode +
        " scope=current_process registry_writes=false controller_path=unchanged";
}
