using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class Win10BlePresetRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var presets = Win10BlePresetPolicy.Presets;
        check(presets.Count == 3 && presets.Select(item => item.Id).Distinct().Count() == 3,
            "exactly three unambiguous audited-stack experiment choices");
        check(Win10BlePresetPolicy.Default.Preset == Win10BlePreset.Fixed7p5 &&
            Win10BlePresetPolicy.ParseCommand(Array.Empty<string>()) == Win10BlePresetPolicy.Default,
            "existing stack apply retains fixed 7.5ms default without a selector");
        foreach (var preset in presets)
        {
            var values = preset.Values;
            check(values.Count == 4 && values.Keys.OrderBy(key => key).SequenceEqual(
                Win10BleStackProfile.Values.Keys.OrderBy(key => key)),
                "every choice mutates only the four audited global names " + preset.Id);
            check(values["LEConnectionIntervalMin"] == preset.IntervalMinUnits &&
                values["LEConnectionIntervalMax"] == preset.IntervalMaxUnits &&
                values["LEConnectionLatency"] == 0 && values["LEConnectionLSTO"] == 200,
                "choice values match validated metadata " + preset.Id);
            check(Win10BlePresetPolicy.Get(preset.Preset) == preset &&
                Win10BlePresetPolicy.Get(preset.Id.ToUpperInvariant()) == preset &&
                Win10BlePresetPolicy.ParseCommand(new[] { "--ble-turbo-stack-apply",
                    Win10BlePresetPolicy.SelectorPrefix + preset.Id }) == preset,
                "enum and CLI selection resolve consistently " + preset.Id);
            check(preset.Latency == 0 && preset.TimeoutUnits == 200 &&
                preset.IntervalMinUnits >= 6 && preset.IntervalMaxUnits <= 12 &&
                preset.IntervalMinUnits <= preset.IntervalMaxUnits,
                "choices have legal BLE intervals and existing supervision timeout " + preset.Id);
            try
            {
                ((IDictionary<string, int>)values)["LEConnectionIntervalMin"] = 200;
                check(false, "preset values cannot be mutated " + preset.Id);
            }
            catch (NotSupportedException) { check(true, "preset values cannot be mutated " + preset.Id); }
        }
        check(Win10BlePresetPolicy.Get("7p5").Values.All(pair =>
            Win10BleStackProfile.Values[pair.Key] == pair.Value), "default remains identical to existing audited values");
        check(Win10BlePresetPolicy.Get("7p5").MinimumIntervalMs == 7.5 &&
            Win10BlePresetPolicy.Get("7p5").MaximumIntervalMs == 7.5 &&
            Win10BlePresetPolicy.Get("flexible").MinimumIntervalMs == 7.5 &&
            Win10BlePresetPolicy.Get("flexible").MaximumIntervalMs == 15 &&
            Win10BlePresetPolicy.Get("15").MinimumIntervalMs == 15 &&
            Win10BlePresetPolicy.Get("15").MaximumIntervalMs == 15,
            "unit conversion exposes the intended intervals, not guaranteed notification rates");
        foreach (string invalid in new[] { "", "6", "7.5", "133", "balanced", " 15", "15 ", "flexible=15" })
        {
            try
            {
                Win10BlePresetPolicy.ParseCommand(new[] { Win10BlePresetPolicy.SelectorPrefix + invalid });
                check(false, "invalid selector rejected before configuration " + invalid);
            }
            catch (ArgumentException) { check(true, "invalid selector rejected before configuration " + invalid); }
        }
        foreach (var args in new[]
        {
            new[] { "--ble-turbo-stack-preset" },
            new[] { "--ble-turbo-stack-presets=15" },
            new[] { "--ble-turbo-stack-preset=7p5", "--ble-turbo-stack-preset=15" },
            new[] { "--ble-turbo-stack-preset=15", "--ble-turbo-stack-preset=15" }
        })
        {
            try { Win10BlePresetPolicy.ParseCommand(args); check(false, "malformed or duplicate selector rejected"); }
            catch (ArgumentException) { check(true, "malformed or duplicate selector rejected"); }
        }
        // Calls only the invalid-selector early guard. It must return before
        // administrator detection, registry access, export, or adapter restart.
        Type configurator = typeof(Win10BleStackProfile).Assembly.GetType("Y700Switch2V60Viiper.BleTurboConfigurator")!;
        object result = configurator.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,
            new object[] { new[] { "--ble-turbo-stack-apply", "--ble-turbo-stack-preset=unknown" } })!;
        check(!(bool)result.GetType().GetProperty("Success")!.GetValue(result)! &&
            ((string)result.GetType().GetProperty("Message")!.GetValue(result)!).Contains("未知 Win10 BLE 预设"),
            "real configurator rejects unknown choice before checking privilege or performing system writes");
        // Deserializes a temporary JSON fixture only. No writable system key,
        // administrator process, or Bluetooth operation is opened by ReadState.
        MethodInfo reader = configurator.GetMethod("ReadState", BindingFlags.NonPublic | BindingFlags.Static)!;
        string fixture = Path.Combine(Path.GetTempPath(), "pro2-win10-preset-backup-" + Guid.NewGuid().ToString("N") + ".json");
        string[] names = Win10BleStackProfile.Values.Keys.ToArray();
        void WriteFixture(long value)
        {
            var values = names.ToDictionary(name => name, _ => new { present = true, value });
            File.WriteAllText(fixture, System.Text.Json.JsonSerializer.Serialize(new
            {
                capturedAt = "fixture", adapter = new { name = "fixture", pnpDeviceId = "USB\\FIXTURE",
                    driverProvider = "fixture", driverVersion = "fixture", driverDate = "fixture" },
                registryPath = "HKEY_LOCAL_MACHINE\\" + Win10BleStackProfile.RegistrySubKey,
                values, backupReg = "fixture.reg"
            }));
        }
        try
        {
            foreach (long value in new[] { 6L, (long)int.MinValue, (long)int.MaxValue })
            {
                WriteFixture(value);
                check(reader.Invoke(null, new object[] { fixture, names }) != null,
                    "valid signed DWORD backup is accepted before restore " + value);
            }
            foreach (long value in new[] { (long)int.MaxValue + 1, (long)int.MinValue - 1, long.MaxValue, long.MinValue })
            {
                WriteFixture(value);
                bool rejected = false;
                try { reader.Invoke(null, new object[] { fixture, names }); }
                catch (TargetInvocationException ex) { rejected = ex.InnerException is OverflowException; }
                check(rejected, "invalid wide backup value rejected before any restore writes " + value);
            }
        }
        finally { if (File.Exists(fixture)) File.Delete(fixture); }
    }
}
