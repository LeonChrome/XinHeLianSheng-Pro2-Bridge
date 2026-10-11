using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

/// <summary>On-demand export only. No writes, pairing, adapter restart, or BLE stream subscriptions.</summary>
internal static class BleEnvironmentDiagnostics
{
    private static readonly string[] ValueNames =
    [
        "LEConnectionIntervalMin", "LEConnectionIntervalMax", "LEConnectionLatency", "LEConnectionLSTO",
        "ConnectionIntervalMin", "ConnectionIntervalMax", "ConnectionLatency", "LinkSupervisionTimeout"
    ];

    public static async Task<IReadOnlyList<string>> CaptureAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        try
        {
            BluetoothAdapterDiagnosticInfo adapter = await Win10BleDiagnostic.CaptureAdapterAsync()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            lines.Add("[BLE_DRIVER_AUDIT] name=\"" + adapter.Name + "\" pnp_id=\"" + adapter.PnpDeviceId +
                "\" vid=" + adapter.Vid + " pid=" + adapter.Pid + " provider=\"" + adapter.DriverProvider +
                "\" driver_version=" + adapter.DriverVersion + " driver_date=" + adapter.DriverDate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { lines.Add("[BLE_DRIVER_AUDIT] unavailable=" + ex.GetType().Name); }
        lines.AddRange(await Task.Run(ReadStack, cancellationToken));
        return lines;
    }

    private static IReadOnlyList<string> ReadStack()
    {
        var lines = new List<string>();
        try
        {
            string path = Path.Combine(Environment.SystemDirectory, "drivers", "bthport.sys");
            lines.Add("[BLE_STACK_AUDIT] bthport_version=" + FileVersionInfo.GetVersionInfo(path).FileVersion +
                " runtime_os=" + Environment.OSVersion.Version + " sha256=" + BleCompatibilityInspector.Current.Facts.StackSha256);
            using RegistryKey? osKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            lines.Add("[BLE_STACK_AUDIT] display_version=" + osKey?.GetValue("DisplayVersion") +
                " build=" + osKey?.GetValue("CurrentBuildNumber") + " ubr=" + osKey?.GetValue("UBR"));
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(Win10BleStackProfile.RegistrySubKey, writable: false);
            lines.AddRange(ReadValues(key));
        }
        catch (Exception ex) { lines.Add("[BLE_STACK_AUDIT] unavailable=" + ex.GetType().Name); }
        return lines;
    }

    private static IReadOnlyList<string> ReadValues(RegistryKey? key)
    {
        var lines = new List<string>();
        foreach (string name in ValueNames)
        {
            object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string text = value == null ? "absent" : value is int number ? number.ToString() : "non_dword";
            lines.Add("[BLE_STACK_VALUE] name=" + name + " value=" + text +
                " read_only=true configured_value_is_not_effectiveness_proof");
        }
        return lines;
    }
}
