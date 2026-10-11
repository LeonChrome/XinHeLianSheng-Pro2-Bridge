#if PRO2_BATTERY_READ_EXPERIMENT
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private readonly Pro2LiveBatteryReadPolicy liveBatteryReadPolicy = new();
    private int batteryExperimentStarted;

    public async Task<string> RunLiveBatteryReadExperimentAsync(IProgress<string> progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref batteryExperimentStarted, 1, 0) != 0)
            return "already_started";
        GattCharacteristic? characteristic;
        BluetoothLEDevice? ownerDevice;
        long ownerSequence;
        lock (gate)
        {
            characteristic = legacyCharacteristic;
            ownerDevice = device;
            ownerSequence = connectionSequence;
        }
        bool SameLiveConnection()
        {
            lock (gate)
                return IsRunning && transportConnected && connectionSequence == ownerSequence &&
                    ReferenceEquals(device, ownerDevice) && ReferenceEquals(legacyCharacteristic, characteristic) &&
                    lastParsedNotifyTicks != 0 && Stopwatch.GetElapsedTime(lastParsedNotifyTicks).TotalMilliseconds < 500 &&
                    ownerDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        if (characteristic == null || (characteristic.CharacteristicProperties & GattCharacteristicProperties.Read) == 0 || !SameLiveConnection())
            return "no_live_readable_characteristic";

        int disconnectSignals = 0;
        void RecordDisconnect(Pro2BleDisconnectSignal signal)
        {
            Interlocked.Increment(ref disconnectSignals);
            progress.Report("[BATTERY_READ_TEST_DISCONNECT] " + signal.TelemetryValue);
        }
        DisconnectDetected += RecordDisconnect;
        try
        {
            progress.Report("[BATTERY_READ_TEST] begin owner_sequence=" + ownerSequence +
                " fd2_source=existing_app_connection gameplay_queries_max=1 notify_writes=0 command_writes=0 pairing=none retries=0 watchdog=unchanged");
            var before = await CaptureBatteryReadTrialAsync("BEFORE", 15000, token);
            progress.Report(FormatBatteryTrial(before));
            bool accepted = Pro2LiveBatteryReadPolicy.AcceptBaseline(before.Fd2Hz, before.ParsedHz, before.Gaps.P50Ms);
            if (!accepted || !SameLiveConnection())
                return "baseline_or_connection_rejected";

            // Start the AFTER capture BEFORE the read so it includes the GATT
            // operation itself. Keep FD2 handlers and normal watchdog running.
            Task<BleTrialMeasurement> afterTask = CaptureBatteryReadTrialAsync("AFTER_INCLUDING_READ", 30000, token);
            Pro2LiveBatteryReadResult result;
            try
            {
                result = await liveBatteryReadPolicy.ReadOnceAsync(accepted, SameLiveConnection, async readToken =>
                {
                    progress.Report("[BATTERY_READ_TEST] read_begin handle=0x" + characteristic.AttributeHandle.ToString("X4") +
                        " uuid=" + characteristic.Uuid + " cache=Uncached notify_fallback=false");
                    var clock = Stopwatch.StartNew();
                    var response = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(readToken);
                    byte[]? payload = response.Status == GattCommunicationStatus.Success ? ReadBuffer(response.Value) : null;
                    progress.Report("[BATTERY_READ_TEST] read_end status=" + response.Status +
                        " protocol_error=" + (response.ProtocolError?.ToString() ?? "none") +
                        " elapsed_ms=" + clock.Elapsed.TotalMilliseconds.ToString("F2") +
                        " len=" + (payload?.Length ?? 0) + " payload=" + (payload == null ? "none" : Convert.ToHexString(payload)));
                    return Pro2LiveBatteryReadPolicy.Decode(response.Status.ToString(), response.ProtocolError, payload);
                }, token);
            }
            catch
            {
                // Always observe the capture's completion/cancellation. Never
                // leave a background exception or an active capture behind.
                try { await afterTask; } catch { }
                throw;
            }
            progress.Report("[BATTERY_READ_TEST_RESULT] status=" + result.Status +
                " fresh_level=" + (result.HasLevel ? result.Level + "/9" : "unknown") +
                " ui_connection_snapshot_unchanged=true no_retry=true");
            var after = await afterTask;
            progress.Report(FormatBatteryTrial(after));
            bool sameConnection = SameLiveConnection();
            bool stable = sameConnection && Volatile.Read(ref disconnectSignals) == 0 &&
                Pro2LiveBatteryReadPolicy.AcceptBaseline(after.Fd2Hz, after.ParsedHz, after.Gaps.P50Ms);
            progress.Report("[BATTERY_READ_TEST_VERDICT] level_read=" + result.HasLevel + " input_stable=" + stable +
                " same_connection=" + sameConnection + " disconnect_signals=" + disconnectSignals +
                " before_fd2_hz=" + before.Fd2Hz.ToString("F2") + " after_fd2_hz=" + after.Fd2Hz.ToString("F2") +
                " before_p50_ms=" + before.Gaps.P50Ms.ToString("F2") + " after_p50_ms=" + after.Gaps.P50Ms.ToString("F2") +
                " connection_kept=true queries_finished=true");
            return result.Status + (stable ? ";stable_133" : ";input_changed_or_interrupted");
        }
        finally { DisconnectDetected -= RecordDisconnect; }
    }

    private async Task<BleTrialMeasurement> CaptureBatteryReadTrialAsync(string phase, int milliseconds, CancellationToken token)
    {
        var capture = new Win10BleDiagnosticCapture(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        lock (gate)
        {
            if (activeBleDiagnosticCapture != null) throw new InvalidOperationException("Another FD2 capture is active.");
            activeBleDiagnosticCapture = capture;
        }
        try
        {
            await Task.Delay(milliseconds, token);
            lock (gate)
            {
                var samples = capture.Snapshot(Stopwatch.GetTimestamp());
                return new BleTrialMeasurement(phase, samples.DurationSeconds,
                    samples.Fd2Ticks.Length / samples.DurationSeconds, samples.ParsedTicks.Length / samples.DurationSeconds,
                    samples.Fd2Ticks.Length, samples.ParsedTicks.Length, connectionIntervalUnits, connectionPreferenceStatus,
                    Win10BleDiagnostic.BuildGapStatistics(samples.Fd2Ticks));
            }
        }
        finally
        {
            lock (gate)
                if (ReferenceEquals(activeBleDiagnosticCapture, capture)) activeBleDiagnosticCapture = null;
        }
    }

    private static string FormatBatteryTrial(BleTrialMeasurement trial) =>
        "[BATTERY_READ_TEST_RATE] phase=" + trial.Phase + " seconds=" + trial.DurationSeconds.ToString("F3") +
        " fd2_hz=" + trial.Fd2Hz.ToString("F2") + " parsed_hz=" + trial.ParsedHz.ToString("F2") +
        " fd2_count=" + trial.Fd2Count + " parsed_count=" + trial.ParsedCount +
        " p50_ms=" + trial.Gaps.P50Ms.ToString("F3") + " p90_ms=" + trial.Gaps.P90Ms.ToString("F3") +
        " p95_ms=" + trial.Gaps.P95Ms.ToString("F3") + " p99_ms=" + trial.Gaps.P99Ms.ToString("F3") +
        " min_ms=" + trial.Gaps.MinMs.ToString("F3") + " max_ms=" + trial.Gaps.MaxMs.ToString("F3");
}

public sealed partial class MainViewModel
{
    private int launchedBatteryReadTest;
    public async Task TestLiveBatteryReadForLaunchAsync()
    {
        if (previewOnly || Interlocked.CompareExchange(ref launchedBatteryReadTest, 1, 0) != 0) return;
        AppendLog("[BATTERY_READ_TEST] armed=true waiting_for_live_133=true normal_watchdog=unchanged");
        try
        {
            var clock = Stopwatch.StartNew();
            while ((!inputSource.IsHighRateReady || Busy) && clock.Elapsed < TimeSpan.FromSeconds(180))
                await Task.Delay(250, lifetimeCts.Token);
            if (!inputSource.IsHighRateReady) { AppendLog("[BATTERY_READ_TEST] skipped=no_high_rate_connection"); return; }
            // Run on a worker, not the UI or Notify thread. No operation gate is
            // held, so connection failure/recycling cannot be hidden by testing.
            string result = await Task.Run(() => inputSource.RunLiveBatteryReadExperimentAsync(
                new DirectProgress<string>(AppendLog), lifetimeCts.Token), lifetimeCts.Token);
            AppendLog("[BATTERY_READ_TEST] complete=" + result + " automatic_repeat=false connection_left_open=true");
        }
        catch (OperationCanceledException) { AppendLog("[BATTERY_READ_TEST] cancelled=true no_retry=true"); }
        catch (Exception ex) { AppendLog("[BATTERY_READ_TEST] error=" + ex + " no_retry=true"); }
    }
}
#endif
