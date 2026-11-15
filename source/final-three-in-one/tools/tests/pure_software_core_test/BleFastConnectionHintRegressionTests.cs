using System;
using System.IO;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class BleFastConnectionHintRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "ble-fast-hint-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "hint.json");
        Directory.CreateDirectory(directory);
        BleAdaptiveMeasurement Sample(int count = 200) => new(new(1.5, count, count, 2, true, true, 8, 6), 0, 16);
        var key = new BleAdaptiveEnvironmentKey(26300, new string('A', 64), "USB\\VID_8087&PID_0033", "radio|AA:BB:CC:DD:EE:FF",
            "Intel", "24.40.11.1", "2026-07-02", "ble-adaptive-v2|os=10.0.26300.9974|fw=2.1.9|controller=38:C6:CE:27:FC:2D|peer_count=0");
        var hint = new BleFastConnectionHint(key, BleAdaptivePreset.Win11Fixed7p5, DateTimeOffset.UtcNow.AddMinutes(-1));
        void Raw(BleFastConnectionHint value) => File.WriteAllText(path, JsonSerializer.Serialize(value));
        void RejectSave(BleFastConnectionHint value, BleAdaptiveMeasurement? first = null, BleAdaptiveMeasurement? second = null)
        {
            bool rejected = false;
            try { BleFastConnectionHintStore.Save(value, first ?? Sample(), second ?? Sample(), path); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "fast hint save rejects invalid proof or identity " + value.Preset);
        }
        try
        {
            check(BleFastConnectionHintStore.Load(key, out string absent, path) == null && absent == "fast_hint_absent", "no fast hint is fabricated on a new machine");
            BleFastConnectionHintStore.Save(hint, Sample(), Sample(), path);
            check(BleFastConnectionHintStore.Load(key, out string exact, path) == hint && exact == "fast_hint_exact_environment_retest_required", "short-proven exact hint is returned only as a retest preference");
            var unknownFirmware = key with { PolicyRevision = key.PolicyRevision.Replace("fw=2.1.9", "fw=unknown") };
            check(BleFastConnectionHintStore.Load(unknownFirmware, out _, path) == null, "ordinary load never ignores an unknown firmware identity");
            check(BleFastConnectionHintStore.LoadForEarlyRequest(unknownFirmware, out string early, path) == hint && early == "fast_hint_early_request_only_retest_required", "early request can ignore only the firmware token before the ACK");
            var changedFirmware = key with { PolicyRevision = key.PolicyRevision.Replace("fw=2.1.9", "fw=2.2.0") };
            check(BleFastConnectionHintStore.Load(changedFirmware, out _, path) == null && BleFastConnectionHintStore.LoadForEarlyRequest(changedFirmware, out _, path) == hint,
                "explicit early-only route relaxes firmware while normal matching remains exact");
            foreach (var changed in new[]
            {
                key with { WindowsBuild = 26200 }, key with { BluetoothStackSha256 = new string('B', 64) },
                key with { AdapterPnpId = "USB\\VID_0BDA&PID_0852" }, key with { AdapterSetFingerprint = "another-radio" },
                key with { DriverProvider = "Realtek" }, key with { DriverVersion = "24.40.11.2" }, key with { DriverDate = "2026-07-03" },
                key with { PolicyRevision = key.PolicyRevision.Replace("controller=38:C6:CE:27:FC:2D", "controller=38:C6:CE:27:FC:2E") },
                key with { PolicyRevision = key.PolicyRevision.Replace("peer_count=0", "peer_count=1") },
                key with { PolicyRevision = key.PolicyRevision.Replace("os=10.0.26300.9974", "os=10.0.26300.9975") },
                key with { PolicyRevision = key.PolicyRevision.Replace("ble-adaptive-v2", "ble-adaptive-v3") }
            })
                check(BleFastConnectionHintStore.Load(changed, out _, path) == null && BleFastConnectionHintStore.LoadForEarlyRequest(changed, out _, path) == null,
                    "both routes reject every other changed identity field " + changed);
            foreach (var preset in new[] { BleAdaptivePreset.Native, BleAdaptivePreset.Win11Fixed15, BleAdaptivePreset.Win11Flexible7p5To15, (BleAdaptivePreset)99 })
                RejectSave(hint with { Preset = preset });
            RejectSave(hint, second: Sample(100));
            var same = Sample();
            RejectSave(hint, same, same);
            RejectSave(hint with { Environment = key with { BluetoothStackSha256 = "unknown" } });
            RejectSave(hint with { Environment = unknownFirmware });
            RejectSave(hint with { VerifiedAtUtc = DateTimeOffset.UtcNow.AddDays(-8) });
            RejectSave(hint with { VerifiedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) });
            foreach (string revision in new[] { "ble-adaptive-v2", key.PolicyRevision + "|fw=2.1.9", key.PolicyRevision.Replace("controller=38:C6:CE:27:FC:2D", "controller=unknown") })
            {
                Raw(hint with { Environment = key with { PolicyRevision = revision } });
                check(BleFastConnectionHintStore.LoadForEarlyRequest(key, out _, path) == null, "corrupt or incomplete policy tokens fail closed");
            }
            Raw(hint with { VerifiedAtUtc = DateTimeOffset.UtcNow.AddDays(-8) });
            check(BleFastConnectionHintStore.Load(key, out string expired, path) == null && expired == "fast_hint_expired_or_future", "expired hint never requests a preference");
            Raw(hint with { VerifiedAtUtc = DateTimeOffset.UtcNow.AddHours(1) });
            check(BleFastConnectionHintStore.Load(key, out _, path) == null, "future dated hint fails closed");
            File.WriteAllText(path, "{broken");
            check(BleFastConnectionHintStore.Load(key, out string corrupt, path) == null && corrupt.StartsWith("fast_hint_read_"), "malformed JSON does not break startup");
            File.WriteAllText(path, new string('x', BleFastConnectionHintStore.MaximumFileBytes + 1));
            check(BleFastConnectionHintStore.Load(key, out string oversized, path) == null && oversized == "fast_hint_oversized", "hint read is bounded");
            BleFastConnectionHintStore.Save(hint with { Preset = BleAdaptivePreset.Win11ThroughputOptimized }, Sample(), Sample(), path);
            check(BleFastConnectionHintStore.Load(key, out _, path)?.Preset == BleAdaptivePreset.Win11ThroughputOptimized, "catalog system-throughput preference can be saved after current high-rate proof");
            var baseline = new BleAdaptiveMeasurement(new(10, 660, 660, 2, true, false, 16, 12), 0, 30);
            var verified = new BleAdaptiveMeasurement(new(10, 1330, 1330, 2, true, true, 8, 6), 0, 16);
            var cached = new BleAdaptiveCachedPreset(key, BleAdaptivePreset.Win11Fixed7p5, baseline, verified, hint.VerifiedAtUtc);
            BleFastConnectionHintStore.SaveFromValidatedCache(cached, path);
            check(BleFastConnectionHintStore.Load(key, out _, path) == hint, "existing validated full-cache improvement can seed only a retest hint");
            foreach (var invalidCache in new[]
            {
                cached with { Verified = baseline }, cached with { Verified = Sample() },
                cached with { Native = verified }, cached with { Preset = BleAdaptivePreset.Win11Fixed15 },
                cached with { VerifiedAtUtc = DateTimeOffset.UtcNow.AddDays(-8) }
            })
            {
                bool rejected = false;
                try { BleFastConnectionHintStore.SaveFromValidatedCache(invalidCache, path); }
                catch (ArgumentException) { rejected = true; }
                check(rejected, "unvalidated partial short or disallowed full-cache entry cannot seed a fast hint");
            }
            check(Directory.GetFiles(directory, "*.tmp").Length == 0, "atomic hint writes leave no temporary file");
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
