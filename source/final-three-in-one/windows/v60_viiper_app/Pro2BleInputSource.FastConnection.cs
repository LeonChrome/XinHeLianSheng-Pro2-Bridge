using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private AdaptiveDiagnosticCapture? fastConnectionDiagnostics;
    private BleAdaptiveEnvironmentKey? fastConnectionEnvironment;
    private BleAdaptivePresetCandidate? fastConnectionPreference;
    private BluetoothLEPreferredConnectionParameters? fastConnectionOriginalPreference;
    private IReadOnlyList<BlePeerRate>? fastConnectionPeerBaseline;
    private bool fastMinimumIntervalAdmitted;
    private int fastMinimumIntervalLowWindows;

    private bool OwnsFixed7p5Preference => fastConnectionPreference?.Preset == BleAdaptivePreset.Win11Fixed7p5 &&
        connectionParametersRequest != null && retainedConnectionPreference is { MinConnectionInterval: 6, MaxConnectionInterval: 6, ConnectionLatency: 0 };

    // Manual optimization remains available. Automatic callers preserve a
    // measured near-target minimum-interval link instead of restarting tests.
    public bool CanAutomaticallyOptimizeConnectionCompatibility
    {
        get
        {
            lock (gate) if (!adaptiveRetryGate.AllowsAutomatic(Stopwatch.GetTimestamp())) return false;
            if (!CanOptimizeConnectionCompatibility) return false;
            var rate = GetRateEvidence();
            lock (gate)
            {
                if (BleFastConnectionPolicy.ShouldPreserveMinimumInterval(fastMinimumIntervalAdmitted,
                    OwnsFixed7p5Preference, rate.Connected, rate.Running, rate.RecentWindow, rate.IntervalUnits,
                    rate.Fd2Hz, rate.ParsedHz, rate.LastInputAgeMs)) return false;
                // One slow passive window cannot undo the retained request.
                if (fastMinimumIntervalAdmitted && OwnsFixed7p5Preference && rate.Connected && rate.Running &&
                    rate.RecentWindow && rate.IntervalUnits == 6 && rate.LastInputAgeMs is >= 0 and < 500 &&
                    rate.Fd2Hz >= 60 && rate.ParsedHz >= rate.Fd2Hz * .99 && rate.ParsedHz <= rate.Fd2Hz &&
                    fastMinimumIntervalLowWindows < 2) return false;
            }
            return true;
        }
    }

    // Metadata only: overlap this with GATT discovery, never with another BLE
    // connection attempt. A timeout disables remembered hints, not connection.
    private async Task<BleAdaptiveEnvironmentKey?> CaptureFastConnectionEnvironmentAsync(
        BluetoothLEDevice owner, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var key = await CaptureAdaptiveEnvironmentKeyAsync(deadline.Token).WaitAsync(deadline.Token);
            return ReferenceEquals(owner, device) ? key : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { connectionProgress?.Report("[BLE_FAST_CONNECT] metadata=" + ex.GetType().Name + " hint_reuse=false"); return null; }
    }

    // Only after the exact Pro2 cmd/ack/FD2 identities are established. The saved
    // hint changes request order only; the current FD2 stream must still pass.
    private async Task PrepareFastConnectionAsync(BluetoothLEDevice owner,
        Task<BleAdaptiveEnvironmentKey?>? environmentTask, IProgress<string> progress, CancellationToken token)
    {
        if (!AutoOptimizeBleEnabled || customConnectionParameterExperiment) return;
        long sequence = connectionSequence;
        var diagnostic = StartAdaptiveDiagnosticEvidence();
        fastConnectionDiagnostics = diagnostic;
        fastConnectionOriginalPreference = retainedConnectionPreference;
        fastConnectionPeerBaseline = PeerRatesProvider?.Invoke() ?? Array.Empty<BlePeerRate>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            progress.Report("[BLE_FAST_CONNECT] route=win10_existing_configuration registry_written=false elevation=false");
            return;
        }
        if (!BleCompatibilityInspector.Current.Plan.CanRequestPreferredParameters) return;
        var environment = environmentTask == null ? null : await environmentTask;
        token.ThrowIfCancellationRequested();
        if (!transportConnected || !ReferenceEquals(owner, device) || sequence != connectionSequence) return;
        fastConnectionEnvironment = environment;
        string hintCode = "metadata_unavailable";
        var hint = fastConnectionEnvironment == null ? null :
            BleFastConnectionHintStore.LoadForEarlyRequest(fastConnectionEnvironment, out hintCode);
        // Fixed 7.5ms is already part of the bounded Win11 repertoire. Unknown
        // metadata does not authorise global settings, nor prevent this request.
        var candidate = hint?.Preset == BleAdaptivePreset.Win11ThroughputOptimized
            ? new BleAdaptivePresetCandidate(BleAdaptivePreset.Win11ThroughputOptimized,
                "win11_throughput", "系统吞吐量优先", null, null, null, null, false)
            : BleAdaptivePresetPolicy.GetCachedHighRateCandidate(BleAdaptivePreset.Win11Fixed7p5)!;
        progress.Report("[BLE_FAST_CONNECT] hint=" + hintCode + " preferred=" + candidate.Id +
            " firmware_recheck=after_init hint_is_current_evidence=false system_writes=false");
        if (!BlePeerRateGuard.Healthy(fastConnectionPeerBaseline, PeerRatesProvider?.Invoke() ?? Array.Empty<BlePeerRate>()))
        {
            progress.Report("[BLE_FAST_CONNECT] peer_guard=changed request_sent=false");
            return;
        }
        if (SubmitAdaptiveConnectionPreset(owner, candidate, retainedConnectionPreference,
            diagnostic, progress))
        {
            fastConnectionPreference = candidate;
            win11ParameterRouteStatus = hint != null ? "RememberedPreferenceShortCheck" : "Priority7p5ShortCheck";
        }
    }

    private async Task<bool> TryFastConnectionAcceptanceAsync(IProgress<string> progress, CancellationToken token)
    {
        BluetoothLEDevice? owner = device;
        long sequence = connectionSequence;
        if (owner == null || !transportConnected) return false;
        bool SameConnection()
        {
            lock (gate) return transportConnected && ReferenceEquals(device, owner) && connectionSequence == sequence;
        }
        var peers = fastConnectionPeerBaseline ?? PeerRatesProvider?.Invoke() ?? Array.Empty<BlePeerRate>();
        bool PeersHealthy() => BlePeerRateGuard.Healthy(peers, PeerRatesProvider?.Invoke() ?? Array.Empty<BlePeerRate>());
        AdaptiveDiagnosticCapture diagnostic = fastConnectionDiagnostics ?? StartAdaptiveDiagnosticEvidence();
        long started = Stopwatch.GetTimestamp();
        compatibilityOptimizationSummary = "正在快速确认真实输入";
        ConnectionStateChanged?.Invoke();
        RecordAdaptiveStage(diagnostic, "正在快速确认真实输入");
        progress.Report("[BLE_FAST_CONNECT] phase=fd2_check windows=2 duration_each_ms=1500 requested_hz=133 synthetic_reports=0");
        try
        {
            // Firmware initialization may renegotiate after the early request.
            // Reassert once only on an observed mismatch, never periodically.
            token.ThrowIfCancellationRequested();
            if (SameConnection() && PeersHealthy() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && fastConnectionPreference != null &&
                retainedConnectionPreference is { } preferred)
            {
                ushort interval;
                lock (gate) interval = connectionIntervalUnits;
                if (interval > 0 && (interval < preferred.MinConnectionInterval || interval > preferred.MaxConnectionInterval))
                {
                    progress.Report("[BLE_FAST_CONNECT] post_init_interval_units=" + interval + " reassert_once=true");
                    if (SubmitAdaptiveConnectionPreset(owner, fastConnectionPreference, preferred, diagnostic, progress,
                        refreshUnmatchedInterval: true)) await WaitForAdaptivePreferenceAsync(token);
                }
            }
            async Task<(BleFastConnectionWindow Window, BleTrialMeasurement Measured)> Measure(string phase)
            {
                double begin = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                var measured = await CaptureParameterTrialAsync(phase, 1500, progress, token);
                RecordAdaptiveMeasurement(diagnostic, measured);
                var sample = new BleAdaptiveMeasurement(ToMeasuredTrialSample(measured, SameConnection()),
                    MaximumGapMs: measured.Gaps.MaxMs);
                return (new(sample, begin, begin + measured.DurationSeconds, sequence), measured);
            }
            var first = await Measure("FAST_START_CHECK");
            if (!SameConnection()) return false;
            var second = await Measure("FAST_START_VERIFY");
            token.ThrowIfCancellationRequested();
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var firstWindow = first.Window with { Measurement = first.Window.Measurement with
                { CompletedAgeSeconds = Math.Max(0, now - first.Window.EndedSeconds) } };
            var secondWindow = second.Window with { Measurement = second.Window.Measurement with
                { CompletedAgeSeconds = Math.Max(0, now - second.Window.EndedSeconds) } };
            bool high = SameConnection() && PeersHealthy() && BleFastConnectionPolicy.IsHigh(firstWindow, secondWindow);
            bool compatible = SameConnection() && PeersHealthy() && BleFastConnectionPolicy.IsCompatible(firstWindow, secondWindow);
            bool win11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
            bool nearTarget = win11 && SameConnection() && PeersHealthy() &&
                BleFastConnectionPolicy.IsWarmHighCandidate(firstWindow, secondWindow, OwnsFixed7p5Preference);
            // Win10 has no per-link public request API. Re-running its existing
            // settings cannot improve a measured 66Hz link; leave it playable.
            bool accepted = high || nearTarget || (!win11 && compatible);
            string code = high ? "fast_real_input_verified" : nearTarget ? "fast_minimum_interval_retained" :
                accepted ? "win10_fast_compatible" : "fast_check_full_optimization_required";
            progress.Report("[BLE_FAST_CONNECT_RESULT] result=" + code + " first_fd2_hz=" + first.Measured.Fd2Hz.ToString("F2") +
                " second_fd2_hz=" + second.Measured.Fd2Hz.ToString("F2") + " high=" + high + " compatible=" + compatible +
                " elapsed_ms=" + Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0") +
                " near_target_preserved=" + nearTarget + " long_stability_proven=false full_preset_scan_skipped=" + accepted);
            if (!accepted)
            {
                if (SameConnection()) RestoreFastConnectionPreference(owner, diagnostic, progress);
                MarkAdaptiveDiagnosticInterrupted(diagnostic, code);
                await SaveAdaptiveDiagnosticEvidenceAsync(diagnostic, fastConnectionEnvironment, progress);
                return false;
            }
            AdoptAdaptiveMeasurement(second.Measured);
            lock (gate)
            {
                // A compatible Win10 window is not a passed high-rate gate.
                rateAccepted = high;
                linkRateClass = high ? "133hz" : "rate_limited";
                fastMinimumIntervalAdmitted = nearTarget && !high;
                fastMinimumIntervalLowWindows = 0;
            }
            compatibilityOptimizationSummary = high ? "真实输入已达 133Hz 级 · 保持当前参数" :
                nearTarget ? "当前输入可游玩 · 保持 7.5ms，继续观察，不重复轮测" :
                "当前输入可游玩 · 可在首页优化 Win10 蓝牙参数";
            if (nearTarget && !high) win11ParameterRouteStatus = "Fixed7p5RetainedPassiveMonitor";
            CompleteAdaptiveDiagnosticEvidence(diagnostic, new(fastConnectionPreference,
                firstWindow.Measurement, secondWindow.Measurement, true, code, Array.Empty<BleAdaptiveTrialRecord>()));
            if (high && fastConnectionEnvironment != null && fastConnectionPreference != null)
            {
                // Initial metadata precedes firmware discovery. Save the exact
                // firmware now; the next early request is still a hint only.
                var environment = WithCurrentFastFirmware(fastConnectionEnvironment);
                try
                {
                    BleFastConnectionHintStore.Save(new(environment, fastConnectionPreference.Preset, DateTimeOffset.UtcNow),
                        firstWindow.Measurement, secondWindow.Measurement);
                    progress.Report("[BLE_FAST_CONNECT] preference_saved=true scope=exact_environment short_proof_only=true");
                }
                catch (Exception ex) { progress.Report("[BLE_FAST_CONNECT] hint_save=" + ex.GetType().Name + " connection_retained=true"); }
            }
            await SaveAdaptiveDiagnosticEvidenceAsync(diagnostic, fastConnectionEnvironment, progress);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (SameConnection()) RestoreFastConnectionPreference(owner, diagnostic, progress);
            MarkAdaptiveDiagnosticInterrupted(diagnostic, "cancelled"); throw;
        }
    }

    private void RestoreFastConnectionPreference(BluetoothLEDevice owner, AdaptiveDiagnosticCapture diagnostic, IProgress<string> progress)
    {
        if (fastConnectionPreference == null) return;
        bool restored = SubmitAdaptiveConnectionPreset(owner, null, fastConnectionOriginalPreference, diagnostic, progress);
        if (!restored)
            ReleaseAdaptiveTrialRequest(diagnostic, progress);
        fastConnectionPreference = null;
        progress.Report("[BLE_FAST_CONNECT] restored_pre_start_request=" + restored + " actual_interval_rollback_not_guaranteed=true");
    }

    private BleAdaptiveEnvironmentKey WithCurrentFastFirmware(BleAdaptiveEnvironmentKey key) => key with
    {
        PolicyRevision = string.Join("|", key.PolicyRevision.Split('|').Select(part => part.StartsWith("fw=", StringComparison.Ordinal)
            ? "fw=" + (FirmwareInfo?.ControllerVersion ?? "unknown") : part))
    };

    // Parameter-change events update the cached interval. Matching the desired
    // range can end the settle wait early; it never substitutes for FD2 proof.
    private async Task WaitForAdaptivePreferenceAsync(CancellationToken token)
    {
        var preferred = retainedConnectionPreference;
        if (preferred == null) return;
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1500)
        {
            token.ThrowIfCancellationRequested();
            ushort interval;
            lock (gate) interval = connectionIntervalUnits;
            if (!transportConnected || interval > 0 && interval >= preferred.MinConnectionInterval &&
                interval <= preferred.MaxConnectionInterval) return;
            await Task.Delay(40, token);
        }
    }
}
