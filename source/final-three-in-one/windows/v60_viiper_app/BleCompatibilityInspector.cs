using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;
using Windows.Foundation.Metadata;

namespace Y700Switch2V60Viiper;

public sealed record BleCompatibilitySnapshot(BleEnvironmentFacts Facts, BleCompatibilityPlan Plan, string ProbeWarning,
    bool ApiProbeSucceeded = true, bool ConfigurationProbeSucceeded = true, bool StackProbeSucceeded = true,
    Win10StackParameterAssessment? Win10Assessment = null)
{
    public string Telemetry => "route=" + Plan.Route + " build=" + Facts.Build +
        " request_api=" + Facts.RequestParametersApiAvailable + " read_api=" + Facts.ReadParametersApiAvailable +
        " win10_configured=" + Facts.Win10StackValuesConfigured + " bthport_sha256=" + Facts.StackSha256 +
        " api_probe_ok=" + ApiProbeSucceeded + " configuration_probe_ok=" + ConfigurationProbeSucceeded +
        " stack_probe_ok=" + StackProbeSucceeded +
        (Win10Assessment == null ? "" : " win10_parameter_evidence=" + Win10Assessment.EvidenceLevel +
            " win10_parameter_names=" + Win10Assessment.ParameterNamesFound.Count + "/4" +
            " win10_experiment_available=" + Win10Assessment.CanExperiment +
            " win10_explicit_experiment_required=" + Win10Assessment.RequiresExperimentalConsent) +
        " warning=" + ProbeWarning.Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>Read-only environment probe, cached once per process, never on the Notify hot path.</summary>
public static class BleCompatibilityInspector
{
    private static readonly Lazy<BleCompatibilitySnapshot> cached = new(Inspect);
    private static BleCompatibilitySnapshot? refreshed;
    public static BleCompatibilitySnapshot Current => Volatile.Read(ref refreshed) ?? cached.Value;
    public static BleCompatibilitySnapshot Refresh()
    {
        var snapshot = Inspect();
        Volatile.Write(ref refreshed, snapshot);
        return snapshot;
    }

    private static BleCompatibilitySnapshot Inspect()
    {
        bool windows = OperatingSystem.IsWindows();
        int build = Environment.OSVersion.Version.Build;
        bool request = false, read = false, configured = false;
        bool apiProbeOk = true, configurationProbeOk = true, stackProbeOk = true;
        Win10StackParameterAssessment? assessment = null;
        string hash = "unavailable", warning = "";
        if (windows && build >= 22000)
        {
            try
            {
                request = ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "RequestPreferredConnectionParameters") &&
                    ApiInformation.IsPropertyPresent("Windows.Devices.Bluetooth.BluetoothLEPreferredConnectionParameters", "ThroughputOptimized");
                read = ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "GetConnectionParameters") &&
                    ApiInformation.IsEventPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "ConnectionParametersChanged");
            }
            catch (Exception ex) { apiProbeOk = false; warning = "api_probe:" + ex.GetType().Name; }
        }
        if (windows)
        {
            try
            {
                if (build >= 19041 && build < 22000)
                {
                    assessment = Win10StackParameterAssessment.ReadCurrent();
                    hash = assessment.StackSha256;
                    stackProbeOk = assessment.ValidSystemX64Image;
                    if (!stackProbeOk) warning += " stack_probe:" + assessment.Reason;
                }
                else
                {
                    using var stream = File.OpenRead(Path.Combine(Environment.SystemDirectory, "drivers", "bthport.sys"));
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }
            }
            catch (Exception ex) { stackProbeOk = false; warning += " stack_probe:" + ex.GetType().Name; }
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(Win10BleStackProfile.RegistrySubKey);
                configured = key != null && Win10BleStackProfile.Values.All(pair =>
                    key.GetValue(pair.Key) is int actual && actual == pair.Value);
            }
            catch (Exception ex) { configurationProbeOk = false; warning += " configuration_probe:" + ex.GetType().Name; }
        }
        var facts = new BleEnvironmentFacts(windows, build, request, read, hash, configured);
        BleCompatibilityPlan plan = BleCompatibilityPolicy.Select(facts);
        if (assessment is { CanExperiment: false })
            plan = plan with { Route = BleCompatibilityRoute.NativeNotifyOnly, Title = "Win10 · 系统文件检查未通过",
                Guidance = assessment.EvidenceSummary, CanConfigureWin10Stack = false };
        return new(facts, plan, warning, apiProbeOk, configurationProbeOk, stackProbeOk, assessment);
    }
}
