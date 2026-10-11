using System;
using System.Linq;
using Y700Switch2V60Viiper;

internal static class GeneralizedReportedEnvironmentRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        const string external10Hash = "F46BF2E7B20D4A6D2158738C587E8482828D62C2E0E8210874EEB79493DC853D";
        var win10 = new BleEnvironmentFacts(true, 19045, false, false, external10Hash, false);
        var win10Plan = BleCompatibilityPolicy.Select(win10);
        check(win10Plan.Route == BleCompatibilityRoute.Win10ExperimentalStack && win10Plan.CanConfigureWin10Stack &&
            !win10Plan.CanRequestPreferredParameters && !win10Plan.CanReadConnectionParameters,
            "user external Win10 19045.3570 hash F46 may explicitly experiment, never receives fabricated public API");
        check(!Win10BleStackProfile.IsAudited(19045, external10Hash),
            "external Win10 is not falsely promoted to a verified binary or hardware profile");
        var facts11 = new BleEnvironmentFacts(true, 26200, true, true,
            Win11ValidatedConnectionProfile.ValidatedStackSha256, false);
        var intel0033 = new BluetoothAdapterDiagnosticInfo("Intel Wireless", "USB\\VID_8087&PID_0033\\fixture",
            "8087", "0033", "Intel Corporation", "24.70.0.4", "7-31-2026", "fixture");
        check(Win11ValidatedConnectionProfile.Select(facts11, intel0033, false) == Win11ParameterRoute.UnvalidatedEnvironment,
            "legacy 1.3.13 route really excluded user's Intel 0033 and 24.70.0.4 tuple");
        var native66 = new BleAdaptiveMeasurement(new(10, 665, 665, 11, true, false, 15.26, 12), 0, 30.11);
        foreach (string vendor in new[] { "Intel", "Realtek", "MediaTek", "Microsoft Generic", "Future vendor" })
        foreach (int build in new[] { 22000, 22621, 22631, 26100, 26200, 30000 })
        foreach (bool read in new[] { false, true })
        {
            var plan = BleAdaptivePresetPolicy.CreatePlan(new(true, true, read), native66);
            check(plan.Candidates.Count == 4 && plan.Candidates[0].Preset == BleAdaptivePreset.Win11Fixed7p5 &&
                plan.Candidates.Select(p => p.Preset).Distinct().Count() == 4,
                "bounded 7.5ms attempts independent of driver family / hash / optional reader " + vendor + "/" + build + "/" + read);
            var compatibility = BleCompatibilityPolicy.Select(facts11 with { Build = build, ReadParametersApiAvailable = read,
                StackSha256 = "unknown-future-stack" });
            check(compatibility.CanRequestPreferredParameters && !compatibility.CanConfigureWin10Stack,
                "new Win11 builds request by capability and never use Win10 global registry route");
        }
        check(BleAdaptivePresetPolicy.CanOptimizeInApp(true, true, true, false, false, false, 66.5),
            "in-app adaptive retry is not gated by optional read API or adapter whitelist");
        foreach (double rate in new[] { 0, 128, 133, double.NaN, double.PositiveInfinity })
            check(!BleAdaptivePresetPolicy.CanOptimizeInApp(true, true, true, false, false, false, rate),
                "invalid or native-target connection never opens extra optimization " + rate);
        check(!BleAdaptivePresetPolicy.CanOptimizeInApp(false, true, true, false, false, false, 66) &&
            !BleAdaptivePresetPolicy.CanOptimizeInApp(true, false, true, false, false, false, 66) &&
            !BleAdaptivePresetPolicy.CanOptimizeInApp(true, true, false, false, false, false, 66) &&
            !BleAdaptivePresetPolicy.CanOptimizeInApp(true, true, true, true, false, false, 66) &&
            !BleAdaptivePresetPolicy.CanOptimizeInApp(true, true, true, false, true, false, 66) &&
            !BleAdaptivePresetPolicy.CanOptimizeInApp(true, true, true, false, false, true, 66),
            "generic optimization preserves live-platform, capability, exclusive-operation and once-per-connection safeguards");
    }
}
