using System;
using System.Linq;
using Y700Switch2V60Viiper;

internal static class BleAdaptivePresetRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        BleAdaptiveMeasurement Sample(int count = 660, double p95 = 15.5, double maximum = 30) =>
            new(new(10, count, count, 3, true, true, p95, 12), 0, maximum);
        var native = Sample();
        var win11 = new BleAdaptiveCapabilities(true, true, true);
        var plan = BleAdaptivePresetPolicy.CreatePlan(win11, native);
        check(plan.Code == "win11_bounded_presets" && plan.Candidates.Count == 4,
            "adaptive Windows 11 plan is bounded to four actual presets");
        check(BleAdaptivePresetPolicy.CreatePlan(win11, native with
            { Sample = native.Sample with { RequestedSuccessfully = false } }).Candidates.Count == 4,
            "native baseline does not need a successful parameter request");
        check(plan.Candidates.Select(c => c.Preset).Distinct().Count() == 4,
            "each preset is attempted once rather than a repeated retry loop");
        check(plan.Candidates[0] is { Preset: BleAdaptivePreset.Win11Fixed7p5, MinUnits: 6, MaxUnits: 6, Latency: 0, TimeoutUnits: 600, IsExperimental: true },
            "first candidate requests fixed 7.5ms with zero latency and 6 second timeout");
        check(plan.Candidates[1] is { MinUnits: 6, MaxUnits: 12 } &&
            plan.Candidates[2] is { Preset: BleAdaptivePreset.Win11ThroughputOptimized, MinUnits: null, IsExperimental: false } &&
            plan.Candidates[3] is { MinUnits: 12, MaxUnits: 12 },
            "flexible, official preset and fixed 15ms fallback preserve distinct semantics");
        var cachedOrder = BleAdaptivePresetPolicy.CreatePlan(win11, native, BleAdaptivePreset.Win11Fixed15);
        check(cachedOrder.Candidates.Count == 4 && cachedOrder.Candidates[0].Preset == BleAdaptivePreset.Win11Fixed15,
            "exact-environment cache hint reorders but never skips current measurement or other presets");
        var invalidOrder = BleAdaptivePresetPolicy.CreatePlan(win11, native, (BleAdaptivePreset)999);
        check(invalidOrder.Candidates[0].Preset == BleAdaptivePreset.Win11Fixed7p5,
            "invalid cache enum cannot introduce an arbitrary strategy");
        var healthyPlan = BleAdaptivePresetPolicy.CreatePlan(win11, Sample(1330, 8, 15));
        check(healthyPlan.NativeTargetAlreadyMet && healthyPlan.Candidates.Count == 0,
            "healthy native 133Hz is preserved without any parameter request");
        check(BleAdaptivePresetPolicy.CreatePlan(win11, Sample(1280, 8, 15)).NativeTargetAlreadyMet,
            "native acceptance uses measured 128Hz tolerance, not API status");
        check(BleAdaptivePresetPolicy.CreatePlan(new(false, false, false), native).Code == "win10_native_only",
            "unknown Windows 10 stack never receives Windows 11 ABI presets or guessed system values");
        var audited10 = BleAdaptivePresetPolicy.CreatePlan(new(false, false, false, true), native);
        check(audited10.Win10OptInEligible && audited10.Candidates.Count == 0 &&
            audited10.Code == "win10_audited_configuration_requires_opt_in",
            "audited Windows 10 system route is an opt-in eligibility result, not implicit permission or writes");
        foreach (var caps in new[] { new BleAdaptiveCapabilities(true, false, true), new(true, false, false) })
            check(BleAdaptivePresetPolicy.CreatePlan(caps, native).Candidates.Count == 0,
                "missing request API cannot be masked by a Windows version label");
        check(BleAdaptivePresetPolicy.CreatePlan(new(true, true, false), native).Candidates.Count == 4,
            "missing interval-read API does not prevent trying and measuring actual FD2 input");
        var unknown10 = BleAdaptivePresetPolicy.CreatePlan(new(false, false, false, false, true), native);
        check(unknown10.Win10OptInEligible && unknown10.Candidates.Count == 0 &&
            unknown10.Code == "win10_experimental_configuration_requires_opt_in",
            "unknown Win10 is offered explicit reversible system experiment, not Windows 11 requests or silent writes");

        void Reject(BleAdaptiveMeasurement candidate, string code, string message)
        {
            var result = BleAdaptivePresetPolicy.EvaluateCandidate(native, native, candidate);
            check(!result.Retain && result.Code == code, message);
        }
        var target = Sample(1330, 7.9, 22.5);
        check(BleAdaptivePresetPolicy.EvaluateCandidate(native, native, target).Code == "target_reached",
            "actual notifications and parses reaching target pass, independent of reported interval");
        check(BleAdaptivePresetPolicy.EvaluateCandidate(Sample(200, 50, 100), Sample(200, 50, 100), native).Code == "playable_improvement",
            "20Hz to actual 66Hz is retained as playable improvement, not falsely called 133Hz");
        check(BleAdaptivePresetPolicy.EvaluateCandidate(Sample(200, 50, 100), Sample(200, 50, 100), Sample(400, 30, 60)).Code == "partial_improvement",
            "partial improvement below 60Hz remains accurately classified");
        Reject(native, "incumbent_is_better", "unchanged average and gaps despite request success is not retained");
        Reject(Sample(630), "measured_rate_regressed", "more than two Hz regression is not retained");
        Reject(Sample(1330, 30, 100), "measured_stability_regressed",
            "133Hz average with substantially worse P95 and maximum gaps is rejected");
        var earlierBetter = Sample(1000, 10, 20);
        var laterWeak = BleAdaptivePresetPolicy.EvaluateCandidate(native, earlierBetter, Sample(900, 12, 25));
        check(!laterWeak.Retain && laterWeak.Code == "measured_rate_regressed",
            "later candidate must beat the best retained preset, not merely original baseline");
        var earlierNoisy = Sample(1330, 30, 100);
        var smootherTarget = Sample(1310, 8, 15);
        check(BleAdaptivePresetPolicy.CompareMeasurements(smootherTarget, earlierNoisy) > 0 &&
            BleAdaptivePresetPolicy.EvaluateCandidate(native, earlierNoisy, smootherTarget).Retain,
            "within two Hz target candidates prefer substantially better real timing instead of a noisy nominal 133");
        check(BleAdaptivePresetPolicy.CompareMeasurements(Sample(1270, 8, 15), Sample(1280, 15, 30)) < 0,
            "target class outranks nearby fallback without declaring 127Hz target achieved");
        check(BleAdaptivePresetPolicy.CompareMeasurements(Sample(1330, 8, 15), Sample(1280, 7.8, 14)) > 0,
            "rate differences beyond noise allowance rank by actual effective rate");
        check(BleAdaptivePresetPolicy.EvaluateCandidate(native, native, Sample(660, 12, 20)).Code == "stability_improvement",
            "same effective input rate can retain a clearly smoother preset");
        Reject(Sample(690, 15.5, 30), "no_meaningful_improvement",
            "small 3Hz rise below five percent is not confused with a meaningful gain");
        check(BleAdaptivePresetPolicy.EvaluateCandidate(native, native, Sample(693, 15.5, 30)).Retain,
            "exact three-Hz and five-percent meaningful-gain boundaries are accepted");

        var malformed = new[]
        {
            native with { Sample = native.Sample with { DurationSeconds = 9.99 } },
            native with { Sample = native.Sample with { DurationSeconds = 30.01 } },
            native with { Sample = native.Sample with { DurationSeconds = double.NaN } },
            native with { Sample = native.Sample with { Connected = false } },
            native with { Sample = native.Sample with { Fd2Count = 19, ParsedCount = 19 } },
            native with { Sample = native.Sample with { ParsedCount = 661 } },
            native with { Sample = native.Sample with { ParsedCount = 626 } },
            native with { Sample = native.Sample with { LastInputAgeMs = 500 } },
            native with { Sample = native.Sample with { LastInputAgeMs = -1 } },
            native with { Sample = native.Sample with { LastInputAgeMs = double.NaN } },
            native with { CompletedAgeSeconds = 15.01 },
            native with { CompletedAgeSeconds = -1 },
            native with { CompletedAgeSeconds = double.PositiveInfinity },
            native with { MaximumGapMs = 1 },
            native with { MaximumGapMs = double.NaN },
            native with { Sample = native.Sample with { P95GapMs = -1 } }
        };
        for (int i = 0; i < malformed.Length; i++)
        {
            check(!BleAdaptivePresetPolicy.IsValidMeasurement(malformed[i]) &&
                BleAdaptivePresetPolicy.CreatePlan(win11, malformed[i]).Candidates.Count == 0,
                "malformed or stale native evidence does not launch presets " + i);
            Reject(malformed[i], "invalid_or_stale_measurement", "malformed candidate cannot win " + i);
        }
        Reject(target with { Sample = target.Sample with { RequestedSuccessfully = false } },
            "invalid_or_stale_measurement", "failed API cannot retain the candidate even with historical high counts");
        Reject(target with { Sample = target.Sample with { DurationSeconds = 10.51 } },
            "unequal_windows", "matching capture duration has a five-percent bound");
        check(BleAdaptivePresetPolicy.IsValidMeasurement(native with { CompletedAgeSeconds = 15 }) &&
            BleAdaptivePresetPolicy.IsValidMeasurement(native with { Sample = native.Sample with { LastInputAgeMs = 499.9 } }),
            "measurement freshness boundaries are explicit");
        check(BleAdaptivePresetPolicy.IsValidMeasurement(native with { MaximumGapMs = null, Sample = native.Sample with { P95GapMs = null } }),
            "missing optional timing percentile remains unknown rather than manufacturing a gap");
        check(!BleAdaptivePresetPolicy.IsPlayable(Sample(599)) && BleAdaptivePresetPolicy.IsPlayable(Sample(600)),
            "playable result is measured at 60Hz, never a guaranteed minimum 66Hz claim");

        check(BleAdaptivePresetPolicy.VerifyFinal(target, Sample(1320, 8, 22.5)).Code == "final_target_verified",
            "target requires a fresh final complete window and tolerates one Hz measurement noise");
        check(!BleAdaptivePresetPolicy.VerifyFinal(target, Sample(1270, 8, 22.5)).Retain,
            "final falling below target cannot be declared 133Hz success");
        check(!BleAdaptivePresetPolicy.VerifyFinal(target, Sample(1330, 25, 100)).Retain,
            "final timing degradation overrides its high average input rate");
        check(BleAdaptivePresetPolicy.VerifyFinal(native, Sample(660)).Code == "final_playable_verified" &&
            BleAdaptivePresetPolicy.VerifyFinal(Sample(200, 50, 100), Sample(200, 50, 100)).Code == "final_low_rate_verified",
            "final fallback distinguishes playable and still-low actual results");

        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var key = new BleAdaptiveEnvironmentKey(26200, new string('A', 64), "USB\\VID_8087&PID_0026", "one-radio-8087-0026",
            "Intel", "22.250.0.2", "2023-06-01");
        var entry = new BleAdaptiveCachedPreset(key, BleAdaptivePreset.Win11Fixed7p5, native, target, now);
        check(BleAdaptivePresetPolicy.IsCacheUsable(entry, key, now, out var code) &&
            code == "cache_exact_environment_retest_preference",
            "cache only offers an exact-environment preference to retest, not automatic trust");
        foreach (var changed in new[]
        {
            key with { WindowsBuild = 26100 }, key with { BluetoothStackSha256 = new string('B', 64) },
            key with { AdapterPnpId = "USB\\VID_0BDA&PID_0852" }, key with { AdapterSetFingerprint = "two-radios" },
            key with { DriverProvider = "Realtek" }, key with { DriverVersion = "22.250.0.3" },
            key with { DriverDate = "2024-01-01" }, key with { PolicyRevision = "different-policy" }
        })
            check(!BleAdaptivePresetPolicy.IsCacheUsable(entry, changed, now, out _),
                "cache invalidates each exact environment identity field " + changed);
        check(!BleAdaptivePresetPolicy.IsCacheUsable(entry, key, now.AddDays(7.01), out _) &&
            !BleAdaptivePresetPolicy.IsCacheUsable(entry, key, now.AddSeconds(-1), out _),
            "expired or future-dated preference does not qualify");
        check(BleAdaptivePresetPolicy.IsCacheUsable(entry, key, now.AddDays(7), out _),
            "seven day expiry boundary is explicit");
        check(!BleAdaptivePresetPolicy.IsCacheUsable(entry with { Verified = native }, key, now, out _) &&
            !BleAdaptivePresetPolicy.IsCacheUsable(entry with { Preset = BleAdaptivePreset.Native }, key, now, out _),
            "unchanged samples and invalid strategy cannot manufacture cache success");
        check(!BleAdaptivePresetPolicy.IsCacheUsable(entry, key with { DriverVersion = "unknown" }, now, out _) &&
            !BleAdaptivePresetPolicy.IsCacheUsable(entry, key with { BluetoothStackSha256 = "missing" }, now, out _),
            "incomplete environment evidence never certifies an adapter family");
    }
}
