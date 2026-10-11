using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Y700Switch2V60Viiper;

internal static class BleAdaptiveSessionRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string outputDir)
    {
        BleAdaptiveMeasurement Sample(int count = 660, double p95 = 15.5, double maximum = 30, bool connected = true) =>
            new(new(10, count, count, 3, connected, true, p95, 12), 0, maximum);
        var native66 = Sample();
        var native20 = Sample(200, 50, 100);
        var target = Sample(1330, 8, 22.5);
        var caps = new BleAdaptiveCapabilities(true, true, true);
        Task<BleAdaptiveRunResult> Run(MockBackend backend, CancellationToken token = default) =>
            BleAdaptiveSessionRunner.RunAsync(caps, backend, null, token);

        var noIntervalReader = new MockBackend(native66, target, target);
        var measuredWithoutReader = await BleAdaptiveSessionRunner.RunAsync(new(true, true, false),
            noIntervalReader, null, CancellationToken.None);
        check(measuredWithoutReader.FinalVerified && measuredWithoutReader.Final.EffectiveHz == 133 &&
            measuredWithoutReader.Selected?.Preset == BleAdaptivePreset.Win11Fixed7p5 && noIntervalReader.Requests.Count == 1,
            "actual FD2 measurement can verify 7.5ms candidate without requiring a system interval reader");

        var rejected = new MockBackend(native66, native66) { AcceptRequest = _ => false };
        var rejectedResult = await Run(rejected);
        check(rejected.Requests.Count == 4 && rejectedResult.Trials.Count == 4 &&
            rejectedResult.Trials.All(t => !t.RequestAccepted),
            "all four rejected adaptive requests are bounded and each failure is recorded");
        check(rejectedResult.Selected == null && rejectedResult.FinalVerified &&
            rejectedResult.Final.EffectiveHz == 66 && rejected.MeasurePhases.SequenceEqual(new[] { "NATIVE", "FINAL" }),
            "rejected API requests preserve real native 66Hz and finish with actual measurement");

        var same = new MockBackend(native66, native66, native66, native66, native66, native66);
        var sameResult = await Run(same);
        check(same.Requests.Count(r => r != null) == 4 && same.Requests.Count(r => r == null) == 4 &&
            sameResult.Selected == null && sameResult.FinalVerified,
            "all four unchanged 66Hz presets are released instead of pretending parameter success is improvement");
        check(sameResult.Code == "final_playable_verified" && sameResult.Trials.All(t => t.Decision == "incumbent_is_better"),
            "unchanged actual 66Hz remains playable without a false 133Hz result");

        var nativeFast = new MockBackend(target);
        var nativeFastResult = await Run(nativeFast);
        check(nativeFast.Requests.Count == 0 && nativeFastResult.Trials.Count == 0 &&
            nativeFastResult.Selected == null && nativeFastResult.FinalVerified && nativeFastResult.Code == "native_target_preserved",
            "already real native 133Hz exits without requesting or restoring any parameter");
        var nativeFast10 = new MockBackend(target);
        var nativeFast10Result = await BleAdaptiveSessionRunner.RunAsync(new(false, false, false, true), nativeFast10, null, CancellationToken.None);
        check(nativeFast10.Requests.Count == 0 && nativeFast10Result.Code == "native_target_preserved",
            "healthy Windows 10 native connection is not touched merely because audited system tools exist");

        var firstTarget = new MockBackend(native66, target, target);
        var firstTargetResult = await Run(firstTarget);
        check(firstTarget.Requests.Count == 1 && firstTargetResult.Selected?.Preset == BleAdaptivePreset.Win11Fixed7p5 &&
            firstTargetResult.FinalVerified && firstTargetResult.Code == "final_target_verified",
            "first real target plus independent target verification stops after one preset");
        check(firstTarget.MeasurePhases.Count == 3 && firstTargetResult.Trials.Count == 2 &&
            firstTarget.MeasurePhases.Last().EndsWith("_VERIFY", StringComparison.Ordinal),
            "target stop requires a second actual window and records verification separately");

        var transient = new MockBackend(native66, target, native66, native66) { AcceptRequest = candidate =>
            candidate == null || candidate.Preset == BleAdaptivePreset.Win11Fixed7p5 };
        var transientResult = await Run(transient);
        check(transient.Requests.Count(r => r != null) == 4 && transientResult.Selected == null &&
            transientResult.FinalVerified && transientResult.Code == "final_playable_verified",
            "transient 133Hz followed by 66Hz verification continues all remaining presets");
        check(transient.Requests[1] == null && transient.ActivePreset == null &&
            transientResult.Trials.Any(t => t.Decision == "final_verification_regressed"),
            "failed target verification restores native before remaining candidates");

        var partial = new MockBackend(native20, native66, native20, native20, native20, native66);
        var partialResult = await Run(partial);
        check(partialResult.Selected?.Preset == BleAdaptivePreset.Win11Fixed7p5 &&
            partialResult.FinalVerified && partialResult.Final.EffectiveHz == 66,
            "actual 20 to 66 improvement survives worse later presets and passes final playable verification");
        check(partial.Requests.Count(r => r == BleAdaptivePreset.Win11Fixed7p5) == 4 &&
            partial.ActivePreset == BleAdaptivePreset.Win11Fixed7p5 &&
            partialResult.Trials.Skip(1).All(t => t.Decision == "measured_rate_regressed"),
            "each later 20Hz regression restores retained best 66Hz rather than baseline or last request");

        var finalRegression = new MockBackend(native20, native66, native20, native20, native20, native20, native20);
        var finalRegressionResult = await Run(finalRegression);
        check(finalRegressionResult.Selected == null && finalRegression.ActivePreset == null &&
            finalRegression.MeasurePhases.Last() == "RESTORE_VERIFY" && finalRegressionResult.Final.EffectiveHz == 20,
            "a retained preset failing final verification is released and actual restore outcome is measured");
        check(finalRegressionResult.Code == "final_low_rate_verified" && finalRegressionResult.FinalVerified,
            "verified still-low native outcome is not a target or 66Hz guarantee");

        using (var cancelled = new CancellationTokenSource())
        {
            var beforeAcceptedReturn = new MockBackend(native66);
            bool injected = false;
            beforeAcceptedReturn.AfterApplyRequest = (candidate, token) =>
            {
                if (!injected && candidate != null)
                {
                    injected = true;
                    cancelled.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            };
            bool cancelledThrown = false;
            try { await Run(beforeAcceptedReturn, cancelled.Token); }
            catch (OperationCanceledException) { cancelledThrown = true; }
            check(cancelledThrown && beforeAcceptedReturn.Requests.Count == 2 &&
                beforeAcceptedReturn.Requests.Last() == null && beforeAcceptedReturn.ActivePreset == null,
                "cancellation after applying request but before request task returns restores original preference");
            check(beforeAcceptedReturn.RequestTokens.Last() == CancellationToken.None,
                "cancellation cleanup uses a noncancelled restoration token");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var cancelledAfterBest = new MockBackend(native20, native66);
            cancelledAfterBest.BeforeMeasure = phase =>
            {
                if (phase == "win11_flexible_7p5_15") cancelled.Cancel();
            };
            bool cancelledThrown = false;
            try { await Run(cancelledAfterBest, cancelled.Token); }
            catch (OperationCanceledException) { cancelledThrown = true; }
            check(cancelledThrown && cancelledAfterBest.ActivePreset == BleAdaptivePreset.Win11Fixed7p5 &&
                cancelledAfterBest.Requests.Last() == BleAdaptivePreset.Win11Fixed7p5,
                "cancellation during later measurement restores earlier retained best instead of leaving unverified preset");
        }

        var disconnected = new MockBackend(native20, native66, Sample(200, 50, 100, connected: false));
        var disconnectedResult = await Run(disconnected);
        check(!disconnectedResult.FinalVerified && disconnectedResult.Selected == null &&
            disconnectedResult.Code == "link_disconnected",
            "real disconnect cannot retain a historical candidate or report a verified result");
        check(disconnected.MeasurePhases.Count == 3 && disconnected.Requests.Count == 2,
            "disconnected run stops without new measurement or restoration on a dead link");
        var disconnectDuringVerify = new MockBackend(native66, target, Sample(660, connected: false));
        var disconnectVerifyResult = await Run(disconnectDuringVerify);
        check(!disconnectVerifyResult.FinalVerified && disconnectVerifyResult.Selected == null &&
            disconnectDuringVerify.Requests.Count == 1 && disconnectDuringVerify.MeasurePhases.Count == 3,
            "disconnect during apparent 133Hz verification cannot stop as target success");

        var failedNativeRestore = new MockBackend(native66, native20, native66) { AllowNativeRestoration = false };
        var failedNativeResult = await Run(failedNativeRestore);
        check(failedNativeResult.Code == "request_restore_failed" && !failedNativeResult.FinalVerified &&
            failedNativeResult.Selected == null && failedNativeRestore.Releases == 1 && failedNativeRestore.ActivePreset == null,
            "rejected original restoration releases the bad experimental request and never claims rollback");

        var failedBestRestore = new MockBackend(native20, native66, native20, native66);
        failedBestRestore.AcceptRequest = candidate => candidate == null || candidate.Preset != BleAdaptivePreset.Win11Fixed7p5 ||
            failedBestRestore.Requests.Count(p => p == BleAdaptivePreset.Win11Fixed7p5) == 1;
        var failedBestResult = await Run(failedBestRestore);
        check(failedBestResult.Code == "request_restore_failed" && !failedBestResult.FinalVerified &&
            failedBestRestore.Releases == 1 && failedBestRestore.ActivePreset == null,
            "failure to reapply an earlier best preset does not leave the worse later request retained");

        var failedFinalRestore = new MockBackend(native20, native66, native20, native20) { AllowNativeRestoration = false };
        failedFinalRestore.AcceptRequest = candidate => candidate == null || candidate.Preset == BleAdaptivePreset.Win11Fixed7p5;
        var failedFinalResult = await Run(failedFinalRestore);
        check(failedFinalResult.Code == "request_restore_failed" && !failedFinalResult.FinalVerified &&
            failedFinalResult.Selected == null && failedFinalRestore.Releases == 1,
            "final verification rollback rejection is checked and followed by release plus real measurement");

        await TestStoreAsync(check, outputDir, native66, target);
    }

    private static async Task TestStoreAsync(Action<bool, string> check, string outputDir,
        BleAdaptiveMeasurement native, BleAdaptiveMeasurement target)
    {
        string folder = Path.Combine(Path.GetFullPath(outputDir), "adaptive-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "preset.json");
        var key = new BleAdaptiveEnvironmentKey(26200, new string('A', 64), "USB\\VID_8087&PID_0026", "one-radio-8087-0026",
            "Intel", "22.250.0.2", "2023-06-01");
        var entry = new BleAdaptiveCachedPreset(key, BleAdaptivePreset.Win11Fixed7p5, native, target, DateTimeOffset.UtcNow);
        check(BleAdaptivePresetStore.Load(key, out var absent, path) == null && absent == "cache_absent",
            "missing local preference is an ordinary absent hint");
        check(BleAdaptivePresetStore.Load(null, out var unavailable, path) == null && unavailable == "cache_environment_unavailable",
            "unknown environment cannot load a preference from a default device assumption");
        BleAdaptivePresetStore.Save(entry, path);
        var roundtrip = BleAdaptivePresetStore.Load(key, out var loaded, path);
        check(roundtrip == entry && loaded == "cache_exact_environment_retest_preference" && !File.Exists(path + ".tmp"),
            "cache roundtrip preserves exact environment, measurements and time with completed atomic replacement");
        var changed = key with { DriverVersion = "22.250.0.3" };
        check(BleAdaptivePresetStore.Load(changed, out var changedCode, path) == null && changedCode == "cache_environment_changed",
            "driver version changes invalidate the cached reordering hint");
        BleAdaptivePresetStore.Invalidate(changed, path);
        check(File.Exists(path) && BleAdaptivePresetStore.Load(key, out _, path) != null,
            "invalidating another environment never deletes the original environment's preference");
        BleAdaptivePresetStore.Invalidate(key, path);
        check(!File.Exists(path), "same-environment explicit invalidation removes its measured preference");

        bool rejectedSave = false;
        try { BleAdaptivePresetStore.Save(entry with { Verified = native }, path); }
        catch (ArgumentException) { rejectedSave = true; }
        check(rejectedSave && !File.Exists(path), "unchanged or unverified sample cannot create a persisted success hint");

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry with { VerifiedAtUtc = DateTimeOffset.UtcNow.AddDays(-8) }));
        check(BleAdaptivePresetStore.Load(key, out var expired, path) == null && expired == "cache_expired",
            "expired cache is not used even if its adapter family matches");
        await File.WriteAllTextAsync(path, new string(' ', 65537));
        check(BleAdaptivePresetStore.Load(key, out var oversized, path) == null && oversized == "cache_oversized",
            "oversized local JSON is rejected before deserializing it");
        await File.WriteAllTextAsync(path, "{invalid JSON");
        check(BleAdaptivePresetStore.Load(key, out var malformed, path) == null && malformed == "cache_read_JsonException",
            "bad local JSON is a recoverable absent preference rather than a startup crash");
        await File.WriteAllTextAsync(path, "null");
        check(BleAdaptivePresetStore.Load(key, out _, path) == null,
            "JSON null cannot manufacture an environment match or successful preset");
        BleAdaptivePresetStore.Save(entry, path);
        check(BleAdaptivePresetStore.Load(key, out _, path) == entry,
            "a valid new measured preference can replace a corrupt cache safely");
    }

    private sealed class MockBackend(params BleAdaptiveMeasurement[] measurements) : IBleAdaptiveTrialBackend
    {
        private readonly Queue<BleAdaptiveMeasurement> measurements = new(measurements);
        public bool Connected { get; private set; } = true;
        public BleAdaptivePreset? ActivePreset { get; private set; }
        public List<BleAdaptivePreset?> Requests { get; } = new();
        public List<CancellationToken> RequestTokens { get; } = new();
        public List<string> MeasurePhases { get; } = new();
        public List<string> Reports { get; } = new();
        public Func<BleAdaptivePresetCandidate?, bool>? AcceptRequest { get; set; }
        public bool AllowNativeRestoration { get; init; } = true;
        public int Releases { get; private set; }
        public Action<BleAdaptivePresetCandidate?, CancellationToken>? AfterApplyRequest { get; set; }
        public Action<string>? BeforeMeasure { get; set; }

        public Task<BleAdaptiveMeasurement> MeasureAsync(string phase, CancellationToken token)
        {
            MeasurePhases.Add(phase);
            BeforeMeasure?.Invoke(phase);
            token.ThrowIfCancellationRequested();
            if (measurements.Count == 0) throw new InvalidOperationException("Unexpected measurement: " + phase);
            var measurement = measurements.Dequeue();
            Connected = measurement.Sample.Connected;
            return Task.FromResult(measurement);
        }

        public Task<bool> RequestAsync(BleAdaptivePresetCandidate? candidate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(candidate?.Preset);
            RequestTokens.Add(token);
            bool accepted = candidate == null ? AllowNativeRestoration : AcceptRequest?.Invoke(candidate) != false;
            if (accepted)
            {
                ActivePreset = candidate?.Preset;
                AfterApplyRequest?.Invoke(candidate, token);
            }
            return Task.FromResult(accepted);
        }

        public void ReleaseTrialRequest() { Releases++; ActivePreset = null; }
        public void Report(string message) => Reports.Add(message);
    }
}
