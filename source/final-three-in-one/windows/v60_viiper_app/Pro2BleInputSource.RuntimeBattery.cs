using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private volatile bool runtimeBatteryRefreshEnabled;
    private volatile string runtimeBatteryRefreshStatus = "waiting";
    private volatile string runtimeBatterySuspensionReason = "";
    private CancellationTokenSource? runtimeBatteryCts;
    private Task? runtimeBatteryTask;
    private Win10BleDiagnosticCapture? runtimeBatteryCapture;
    private string batteryLevelSource = "connection";
    private byte batteryLevelAtConnection = GamepadState.BatteryUnknown;
    public bool RuntimeBatteryRefreshEnabled => runtimeBatteryRefreshEnabled;
    public string RuntimeBatteryRefreshStatus => runtimeBatteryRefreshStatus;
    public string RuntimeBatterySuspensionReason => runtimeBatterySuspensionReason;

    public void SetRuntimeBatteryRefreshEnabled(bool enabled)
    {
#if PRO2_BATTERY_READ_EXPERIMENT
        // An isolated experiment must never compete with the shipping worker.
        enabled = false;
#endif
        runtimeBatteryRefreshEnabled = enabled &&
            !Environment.GetCommandLineArgs().Contains(Pro2RuntimeBatteryPolicy.DisableArgument, StringComparer.OrdinalIgnoreCase) &&
            !Environment.GetCommandLineArgs().Contains(Pro2InitialBatterySnapshot.DisableArgument, StringComparer.OrdinalIgnoreCase) &&
            !Environment.GetCommandLineArgs().Contains("--battery-snapshot-off-test", StringComparer.OrdinalIgnoreCase);
    }

    private void StartRuntimeBatteryRefresh(IProgress<string> progress)
    {
        // Exactly one worker per BLE owner. UI visibility/mode changes do not
        // restart it. Audio/CLI tools do not enable it through a view model.
        if (runtimeBatteryTask != null) return;
        runtimeBatteryCts = new CancellationTokenSource();
        CancellationToken token = runtimeBatteryCts.Token;
        runtimeBatteryTask = Task.Run(() => RuntimeBatteryLoopAsync(progress, token));
    }

    private async Task StopRuntimeBatteryRefreshAsync()
    {
        CancellationTokenSource? cts = runtimeBatteryCts;
        Task? worker = runtimeBatteryTask;
        cts?.Cancel();
        try { if (worker != null) await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A failed background worker must not prevent the BLE owner from
            // releasing its GATT services during a normal stop/reconnect.
            try { connectionProgress?.Report("[BATTERY_RUNTIME] worker_stop_error=" + ex.GetType().Name); } catch { }
        }
        finally
        {
            runtimeBatteryTask = null;
            runtimeBatteryCts = null;
            cts?.Dispose();
        }
    }

    private async Task RuntimeBatteryLoopAsync(IProgress<string> progress, CancellationToken token)
    {
        GattCharacteristic? primary;
        BluetoothLEDevice? owner;
        long sequence;
        lock (gate) { primary = legacyCharacteristic; owner = device; sequence = connectionSequence; }
        bool SameSession()
        {
            lock (gate)
                return IsRunning && transportConnected && !disconnectSignalCaptured &&
                    sequence == connectionSequence && ReferenceEquals(owner, device) &&
                    ReferenceEquals(primary, legacyCharacteristic) &&
                    lastParsedNotifyTicks != 0 && Stopwatch.GetElapsedTime(lastParsedNotifyTicks).TotalMilliseconds < 500 &&
                    owner?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        if (primary == null || (primary.CharacteristicProperties & GattCharacteristicProperties.Notify) == 0)
        { runtimeBatteryRefreshStatus = "unsupported"; return; }
        progress.Report("[BATTERY_RUNTIME] enabled=" + runtimeBatteryRefreshEnabled +
            " interval_s=" + Pro2RuntimeBatteryPolicy.IntervalSeconds +
            " first_refresh_s=" + Pro2RuntimeBatteryPolicy.FirstRefreshSeconds +
            " native_level=true input_route=fd2_only normal_watchdog=unchanged");
        long due = Stopwatch.GetTimestamp() + (long)(Pro2RuntimeBatteryPolicy.FirstRefreshSeconds * Stopwatch.Frequency);
        int cooldownRetries = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (runtimeBatterySuspensionReason.Length > 0)
                { runtimeBatteryRefreshStatus = "suspended"; return; }
                // Wait before the ten-second baseline; no lock or UI thread is
                // held during any delay. Never replay or generate input frames.
                runtimeBatteryRefreshStatus = runtimeBatteryRefreshEnabled ? "waiting" : "disabled";
                double wait = Math.Max(0, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due).TotalSeconds - 10);
                await Task.Delay(TimeSpan.FromSeconds(wait), token);
                if (!runtimeBatteryRefreshEnabled)
                { due = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency; continue; }
                if (!SameSession()) return;
                bool diagnosticActive;
                lock (gate) diagnosticActive = activeBleDiagnosticCapture != null;
                if (diagnosticActive)
                { due = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency; continue; }
                runtimeBatteryRefreshStatus = "measuring";
                var before = await CaptureRuntimeBatteryRateAsync(10000, token);
                if (!Pro2RuntimeBatteryPolicy.CanProbe(before.Rate) || !SameSession())
                {
                    progress.Report("[BATTERY_RUNTIME] skipped=baseline_not_133 " + RuntimeRateText(before.Rate));
                    due = Stopwatch.GetTimestamp() + 60 * Stopwatch.Frequency;
                    continue;
                }
                lock (gate) diagnosticActive = activeBleDiagnosticCapture != null;
                if (!runtimeBatteryRefreshEnabled || diagnosticActive) continue;
                runtimeBatteryRefreshStatus = "refreshing";
                long operationStart = Stopwatch.GetTimestamp();
                due = operationStart + (long)(Pro2RuntimeBatteryPolicy.IntervalSeconds * Stopwatch.Frequency);
                var afterTask = CaptureRuntimeBatteryRateAsync(20000, token);
                Pro2InitialBatterySnapshotResult report;
                long receivedTicks;
                long operationEnd;
                try
                {
                    var captured = await TapRuntimeBatteryAsync(primary, SameSession, progress, token);
                    report = captured.Report;
                    receivedTicks = captured.ReceivedTicks;
                    operationEnd = Stopwatch.GetTimestamp();
                }
                catch
                {
                    try { await afterTask; } catch { }
                    throw;
                }
                var after = await afterTask;
                double correlatedGap = OperationGap(after.Samples.Fd2Ticks, operationStart, operationEnd, after.PreviousTicks);
                var decision = Pro2RuntimeBatteryPolicy.Evaluate(before.Rate, after.Rate, report, SameSession(), correlatedGap);
                progress.Report("[BATTERY_RUNTIME_RATE] before_" + RuntimeRateText(before.Rate) +
                    " after_" + RuntimeRateText(after.Rate) + " operation_max_gap_ms=" + correlatedGap.ToString("F3") +
                    " allowed_operation_gap_ms=45 same_session=" + SameSession() +
                    " accepted=" + decision.Accepted + " reason=" + decision.Reason);
                if (!decision.Accepted)
                {
                    runtimeBatterySuspensionReason = decision.Reason;
                    runtimeBatteryRefreshStatus = "suspended";
                    if (Pro2RuntimeBatteryPolicy.CanCooldownRetry(decision, after.Rate,
                        SameSession(), report.SubscriptionReleased, cooldownRetries))
                    {
                        cooldownRetries++;
                        runtimeBatteryRefreshStatus = "cooldown";
                        progress.Report("[BATTERY_RUNTIME] cooldown=true reason=" + decision.Reason +
                            " healthy_seconds_required=600 retry=" + cooldownRetries + "/1 cccd_released=true");
                        long healthySince = 0;
                        long lastCooldownCheck = Stopwatch.GetTimestamp();
                        while (true)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), token);
                            if (!SameSession()) return;
                            Pro2RateEvidence evidence = GetRateEvidence();
                            bool healthy = runtimeBatteryRefreshEnabled && evidence.RecentWindow &&
                                evidence.LastInputAgeMs is >= 0 and < 500 &&
                                evidence.Stability is { IsStableHighRate: true, IsRecent: true };
                            long current = Stopwatch.GetTimestamp();
                            long lastLowEnd = evidence.Stability?.RecentLowWindow?.WindowEndTicks ?? 0;
                            bool continuityLost = Stopwatch.GetElapsedTime(lastCooldownCheck, current).TotalSeconds > 10 ||
                                (healthySince != 0 && lastLowEnd > healthySince);
                            lastCooldownCheck = current;
                            if (!healthy || continuityLost) healthySince = 0;
                            if (healthy && healthySince == 0) healthySince = current;
                            double healthySeconds = healthySince == 0 ? 0 :
                                Stopwatch.GetElapsedTime(healthySince, current).TotalSeconds;
                            if (!Pro2RuntimeBatteryPolicy.HealthyCooldownComplete(
                                healthySeconds, runtimeBatteryRefreshEnabled, SameSession(), healthy)) continue;
                            runtimeBatterySuspensionReason = "";
                            runtimeBatteryRefreshStatus = "waiting";
                            // Re-run the normal 10s baseline, with no Notify held during cooldown.
                            due = current + 10 * Stopwatch.Frequency;
                            progress.Report("[BATTERY_RUNTIME] cooldown_complete=true normal_baseline_required=true retry_budget_remaining=0");
                            break;
                        }
                        continue;
                    }
                    // An owned Notify that cannot be confirmed off is a
                    // controlled safety reconnect, not a claimed RF disconnect.
                    // The ordinary reconnect loop consumes this signal after
                    // this worker returns; no self-await or hidden watchdog.
                    if (!report.SubscriptionReleased)
                        CaptureDisconnectSignal("battery_cccd_cleanup_unverified_safety_reconnect", null);
                    else if (SameSession() && after.Rate.Fd2Hz < 120)
                        await RecoverFd2AfterBatteryAsync(progress, SameSession, token);
                    return;
                }
                if (!runtimeBatteryRefreshEnabled) continue;
                if (!CommitRuntimeBattery(report.Payload!, sequence, receivedTicks, progress)) return;
                runtimeBatteryRefreshStatus = "updated";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            runtimeBatterySuspensionReason = "error_" + ex.GetType().Name;
            runtimeBatteryRefreshStatus = "suspended";
            progress.Report("[BATTERY_RUNTIME] suspended=true error=" + ex.GetType().Name + " " + ex.Message + " retries=0");
        }
    }

    private bool CommitRuntimeBattery(byte[] payload, long expectedSequence, long receivedTicks, IProgress<string> progress)
    {
        if (!Pro2InitialBatterySnapshot.IsValid(payload) ||
            !Pro2BatteryStatus.TryDecodePrimary(payload, out var value)) return false;
        lock (gate)
        {
            if (!runtimeBatteryRefreshEnabled || !IsRunning || !transportConnected ||
                disconnectSignalCaptured || expectedSequence != connectionSequence ||
                receivedTicks <= 0 || receivedTicks < batteryStatusTicks || receivedTicks > Stopwatch.GetTimestamp()) return false;
            batteryStatus = value;
            batteryStatusTicks = receivedTicks;
            batteryLevelSource = "runtime_notify";
            value.ApplyTo(latest);
            value.ApplyTo(rawState);
            value.ApplyTo(filteredState);
        }
        progress.Report("[BATTERY_RUNTIME_UPDATED] " + value.Telemetry +
            " report_counter=" + payload[0] + " source=fresh_primary_notify ui_updated=true");
        return true;
    }

    private async Task<(Pro2InitialBatterySnapshotResult Report, long ReceivedTicks)> TapRuntimeBatteryAsync(
        GattCharacteristic primary, Func<bool> sameSession, IProgress<string> progress, CancellationToken token)
    {
        var prior = await primary.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask(token)
            .WaitAsync(TimeSpan.FromSeconds(2), token);
        if (prior.Status != GattCommunicationStatus.Success ||
            prior.ClientCharacteristicConfigurationDescriptor != GattClientCharacteristicConfigurationDescriptorValue.None)
            throw new InvalidOperationException("Primary CCCD is not confirmed None; no foreign subscription changed.");
        var received = new TaskCompletionSource<(byte[] Data, long Ticks)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBattery(GattCharacteristic _, GattValueChangedEventArgs args)
        {
            try
            {
                byte[] data = ReadBuffer(args.CharacteristicValue);
                if (Pro2InitialBatterySnapshot.IsValid(data)) received.TrySetResult((data, Stopwatch.GetTimestamp()));
            }
            catch { /* Invalid status cannot enter the gameplay parser. */ }
        }
        primary.ValueChanged += OnBattery;
        try
        {
            // This handler does not route controls or IMU into the input queue.
            // Fresh first Notify, then immediately off; no reinit/FD2 descriptor,
            // connection parameter change, cached battery Read or ACK command.
            var report = await Pro2InitialBatterySnapshot.CaptureAsync(
                async ct => sameSession() && runtimeBatteryRefreshEnabled &&
                    await primary.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct) == GattCommunicationStatus.Success,
                async ct => (await received.Task.WaitAsync(ct)).Data,
                async _ =>
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var disabled = await primary.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(cleanup.Token);
                    var verified = await primary.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask(cleanup.Token);
                    bool released = disabled == GattCommunicationStatus.Success &&
                        verified.Status == GattCommunicationStatus.Success &&
                        verified.ClientCharacteristicConfigurationDescriptor == GattClientCharacteristicConfigurationDescriptorValue.None;
                    progress.Report("[BATTERY_RUNTIME_TAP] cleanup_write=" + disabled + " cleanup_read=" + verified.Status +
                        " cccd=" + verified.ClientCharacteristicConfigurationDescriptor + " verified=" + released);
                    return released;
                }, token, TimeSpan.FromMilliseconds(500));
            return (report, received.Task.IsCompletedSuccessfully ? received.Task.Result.Ticks : 0);
        }
        finally { primary.ValueChanged -= OnBattery; }
    }

    private async Task<(BatteryRefreshRate Rate, Win10BleDiagnosticSamples Samples, long PreviousTicks)> CaptureRuntimeBatteryRateAsync(
        int milliseconds, CancellationToken token)
    {
        var capture = new Win10BleDiagnosticCapture(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        long previousTicks;
        lock (gate)
        {
            if (runtimeBatteryCapture != null) throw new InvalidOperationException("Battery capture is already active.");
            previousTicks = lastFd2InputTicks;
            runtimeBatteryCapture = capture;
        }
        try
        {
            await Task.Delay(milliseconds, token);
            Win10BleDiagnosticSamples samples;
            lock (gate) samples = capture.Snapshot(Stopwatch.GetTimestamp());
            var gaps = Win10BleDiagnostic.BuildGapStatistics(samples.Fd2Ticks);
            return (new(samples.Fd2Count / samples.DurationSeconds,
                samples.ParsedCount / samples.DurationSeconds, gaps.P50Ms, gaps.P95Ms, gaps.MaxMs), samples, previousTicks);
        }
        finally { lock (gate) if (ReferenceEquals(runtimeBatteryCapture, capture)) runtimeBatteryCapture = null; }
    }

    public static double OperationGap(long[] fd2Ticks, long started, long ended, long previousTick = 0)
    {
        double maximum = 0;
        // Include the gap crossing cleanup completion and the next two events.
        long endMargin = ended + (long)(0.015 * Stopwatch.Frequency);
        for (int i = 0; i < fd2Ticks.Length; i++)
        {
            long previous = i == 0 ? previousTick : fd2Ticks[i - 1];
            if (previous > 0 && fd2Ticks[i] >= started && previous <= endMargin)
                maximum = Math.Max(maximum, Stopwatch.GetElapsedTime(previous, fd2Ticks[i]).TotalMilliseconds);
        }
        return maximum;
    }

    private async Task RecoverFd2AfterBatteryAsync(IProgress<string> progress, Func<bool> sameSession, CancellationToken token)
    {
        GattCharacteristic? fd2;
        lock (gate) fd2 = fd2Characteristic;
        if (fd2 == null || !sameSession()) return;
        progress.Report("[BATTERY_RUNTIME_RECOVERY] measured_fd2_drop=true queries_suspended=true no_reinit=true parameter_requests=0");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var notify = await fd2.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(deadline.Token);
        if (!sameSession()) return;
        var descriptors = await fd2.GetDescriptorsForUuidAsync(
            new Guid("679d5510-5a24-4dee-9557-95df80486ecb"), BluetoothCacheMode.Cached).AsTask(deadline.Token);
        if (descriptors.Status != GattCommunicationStatus.Success || descriptors.Descriptors.Count != 1)
        { progress.Report("[BATTERY_RUNTIME_RECOVERY] descriptor_unavailable=true write_skipped=true"); return; }
        byte[] rate = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(rate, RequestedFd2ReportRateHz);
        var written = await descriptors.Descriptors[0].WriteValueAsync(ToBuffer(rate)).AsTask(deadline.Token);
        var recovered = await CaptureRuntimeBatteryRateAsync(10000, token);
        progress.Report("[BATTERY_RUNTIME_RECOVERY] fd2_notify=" + notify + " descriptor_write=" + written +
            " " + RuntimeRateText(recovered.Rate) + " recovered=" + (sameSession() && Pro2RuntimeBatteryPolicy.CanProbe(recovered.Rate)));
    }

    private static string RuntimeRateText(BatteryRefreshRate rate) =>
        "fd2_hz=" + rate.Fd2Hz.ToString("F2") + " parsed_hz=" + rate.ParsedHz.ToString("F2") +
        " p50_ms=" + rate.P50Ms.ToString("F3") + " p95_ms=" + rate.P95Ms.ToString("F3") +
        " max_gap_ms=" + rate.MaxGapMs.ToString("F3");
}
