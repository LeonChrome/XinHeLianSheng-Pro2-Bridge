#if PRO2_BATTERY_READ_EXPERIMENT
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private int batteryFollowupStarted;
    private TaskCompletionSource<byte[]>? batteryCommandReply;
    private byte batteryExpectedSubcommand;

    partial void ObserveExperimentalCommandResponse(byte[] data)
    {
        lock (gate)
            if (data.Length >= 12 && data[0] == 0x0b && data[1] == 1 && data[2] == 1 &&
                data[3] == batteryExpectedSubcommand && data[5] == 0x78)
                batteryCommandReply?.TrySetResult((byte[])data.Clone());
    }

    public async Task<string> RunBatteryFollowupExperimentAsync(IProgress<string> progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref batteryFollowupStarted, 1, 0) != 0) return "already_started";
        GattCharacteristic? primary;
        BluetoothLEDevice? ownerDevice;
        long sequence;
        lock (gate) { primary = legacyCharacteristic; ownerDevice = device; sequence = connectionSequence; }
        bool SameLive()
        {
            lock (gate)
                return IsRunning && transportConnected && connectionSequence == sequence &&
                    ReferenceEquals(device, ownerDevice) && ReferenceEquals(legacyCharacteristic, primary) &&
                    lastParsedNotifyTicks != 0 && Stopwatch.GetElapsedTime(lastParsedNotifyTicks).TotalMilliseconds < 500 &&
                    ownerDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        if (primary == null || !SameLive()) return "no_live_connection";
        int disconnections = 0;
        void NoteDisconnect(Pro2BleDisconnectSignal signal)
        {
            Interlocked.Increment(ref disconnections);
            progress.Report("[BATTERY_FOLLOWUP_DISCONNECT] " + signal.TelemetryValue);
        }
        DisconnectDetected += NoteDisconnect;
        try
        {
            progress.Report("[BATTERY_FOLLOWUP] begin sequence=" + sequence +
                " known_read_only_commands=0B03,0B04 unknown_command_fuzzing=false primary_taps_max=4 normal_watchdog=unchanged");
            var initial = await CaptureBatteryReadTrialAsync("FOLLOWUP_BEFORE", 15000, token);
            progress.Report(FormatBatteryTrial(initial));
            if (!Accept(initial) || !SameLive()) return "baseline_rejected";
            foreach (byte sub in new byte[] { 3, 4 })
            {
                if (!SameLive()) return "connection_changed";
                await QueryKnownBatteryCommandAsync(sub, progress, token);
                await Task.Delay(500, token);
            }
            var commandAfter = await CaptureBatteryReadTrialAsync("COMMAND_AFTER", 10000, token);
            progress.Report(FormatBatteryTrial(commandAfter));
            if (!Accept(commandAfter) || !SameLive() || disconnections != 0) return "command_route_changed_input";

            int successfulTaps = 0;
            for (int index = 1; index <= 4; index++)
            {
                // Only repeat a tap after a previously proven safe result. No
                // repeated pairing, controller init or new GATT enumeration.
                if (index > 1) await Task.Delay(TimeSpan.FromSeconds(30), token);
                if (!SameLive()) return "connection_changed_before_tap";
                var before = await CaptureBatteryReadTrialAsync("TAP_" + index + "_BEFORE", 10000, token);
                progress.Report(FormatBatteryTrial(before));
                if (!Accept(before)) return "tap_baseline_rejected";
                // Include the CCCD enable, first report, and cleanup themselves.
                var afterTask = CaptureBatteryReadTrialAsync("TAP_" + index + "_INCLUDING_OPERATION", 20000, token);
                Pro2InitialBatterySnapshotResult tap;
                try { tap = await CaptureRuntimeBatteryTapAsync(primary, SameLive, index, progress, token); }
                catch { try { await afterTask; } catch { } throw; }
                var after = await afterTask;
                progress.Report(FormatBatteryTrial(after));
                bool same = SameLive();
                bool stable = same && disconnections == 0 && tap.SubscriptionReleased && Accept(after) &&
                    after.Gaps.MaxMs <= Math.Max(35, before.Gaps.MaxMs + 7.5);
                bool captured = tap.Payload != null && Pro2InitialBatterySnapshot.IsValid(tap.Payload);
                progress.Report("[BATTERY_TAP_VERDICT] index=" + index + " captured=" + captured +
                    " cleanup_verified=" + tap.SubscriptionReleased + " same_connection=" + same +
                    " input_stable=" + stable + " disconnect_signals=" + disconnections +
                    " before_fd2_hz=" + before.Fd2Hz.ToString("F2") + " after_fd2_hz=" + after.Fd2Hz.ToString("F2") +
                    " max_gap_budget_ms=" + Math.Max(35, before.Gaps.MaxMs + 7.5).ToString("F2"));
                if (!stable)
                {
                    // Reassert FD2 only after live data proves a rate change.
                    // Never reinitialize, alter BLE parameters or claim success
                    // based on a descriptor/CCCD write status.
                    if (same && tap.SubscriptionReleased && after.Fd2Hz < 128)
                    {
                        progress.Report("[BATTERY_TAP_RECOVERY] reason=measured_rate_drop action=fd2_notify_and_133_descriptor_once no_reinit=true");
                        var notify = await fd2Characteristic!.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token);
                        progress.Report("[BATTERY_TAP_RECOVERY] fd2_notify=" + notify);
                        var descriptors = await fd2Characteristic.GetDescriptorsForUuidAsync(
                            new Guid("679d5510-5a24-4dee-9557-95df80486ecb"), BluetoothCacheMode.Cached).AsTask(token);
                        if (descriptors.Status == GattCommunicationStatus.Success && descriptors.Descriptors.Count == 1)
                        {
                            byte[] rate = new byte[2];
                            BinaryPrimitives.WriteUInt16LittleEndian(rate, RequestedFd2ReportRateHz);
                            var written = await descriptors.Descriptors[0].WriteValueAsync(ToBuffer(rate)).AsTask(token);
                            progress.Report("[BATTERY_TAP_RECOVERY] fd2_descriptor=" + written + " requested_hz=133 ble_parameter_requests=0");
                        }
                        var recovery = await CaptureBatteryReadTrialAsync("TAP_RECOVERY", 15000, token);
                        progress.Report(FormatBatteryTrial(recovery));
                    }
                    return "tap_not_safe_no_repeat";
                }
                if (!captured) return "no_native_level_no_repeat";
                successfulTaps++;
                Pro2BatteryStatus.TryDecodePrimary(tap.Payload!, out var battery);
                progress.Report("[BATTERY_TAP_NATIVE_LEVEL] index=" + index + " source=live_primary_report " + battery.Telemetry +
                    " report_counter=" + tap.Payload![0] + " ui_snapshot_unchanged=true");
            }
            return "native_level_live_taps=" + successfulTaps + ";stable_same_connection";
        }
        finally
        {
            DisconnectDetected -= NoteDisconnect;
            lock (gate) batteryCommandReply = null;
            progress.Report("[BATTERY_FOLLOWUP] queries_finished=true no_automatic_repeat=true connection_left_open=true");
        }
        static bool Accept(BleTrialMeasurement measurement) =>
            Pro2LiveBatteryReadPolicy.AcceptBaseline(measurement.Fd2Hz, measurement.ParsedHz, measurement.Gaps.P50Ms);
    }

    private async Task QueryKnownBatteryCommandAsync(byte sub, IProgress<string> progress, CancellationToken token)
    {
        if (sub is not 3 and not 4) throw new ArgumentOutOfRangeException(nameof(sub));
        var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        GattCharacteristic? command;
        lock (gate) { command = commandCharacteristic; batteryCommandReply = reply; batteryExpectedSubcommand = sub; }
        if (command?.Uuid != CmdUuid) throw new InvalidOperationException("Existing command channel unavailable.");
        try
        {
            byte[] request = [0x0b, 0x91, 0x01, sub, 0, 0, 0, 0];
            progress.Report("[BATTERY_COMMAND_QUERY] sub=0x" + sub.ToString("X2") + " request=" + Convert.ToHexString(request) + " retries=0");
            var written = await command.WriteValueAsync(ToBuffer(request), GattWriteOption.WriteWithoutResponse).AsTask(token).WaitAsync(TimeSpan.FromSeconds(2), token);
            if (written != GattCommunicationStatus.Success) { progress.Report("[BATTERY_COMMAND_QUERY] write=" + written); return; }
            var response = await reply.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8, 2));
            progress.Report("[BATTERY_COMMAND_QUERY] sub=0x" + sub.ToString("X2") + " ack=" + Convert.ToHexString(response) +
                (sub == 3 ? " voltage_mv=" + value : " charge_code=0x" + value.ToString("X4")) + " native_level_available=false");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { progress.Report("[BATTERY_COMMAND_QUERY] error=" + ex.GetType().Name + " " + ex.Message + " no_retry=true"); }
        finally { lock (gate) if (ReferenceEquals(batteryCommandReply, reply)) batteryCommandReply = null; }
    }

    private async Task<Pro2InitialBatterySnapshotResult> CaptureRuntimeBatteryTapAsync(
        GattCharacteristic primary, Func<bool> sameLive, int index, IProgress<string> progress, CancellationToken token)
    {
        if (!sameLive()) throw new InvalidOperationException("Session changed before primary tap.");
        var prior = await primary.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(2), token);
        if (prior.Status != GattCommunicationStatus.Success || prior.ClientCharacteristicConfigurationDescriptor != GattClientCharacteristicConfigurationDescriptorValue.None)
            throw new InvalidOperationException("Primary CCCD is not confirmed None; refusing to change an existing stream.");
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reports = 0;
        void OnReport(GattCharacteristic _, GattValueChangedEventArgs args)
        {
            try
            {
                Interlocked.Increment(ref reports);
                byte[] payload = ReadBuffer(args.CharacteristicValue);
                if (Pro2InitialBatterySnapshot.IsValid(payload)) received.TrySetResult(payload);
            }
            catch { /* Malformed report cannot interrupt FD2. */ }
        }
        var clock = Stopwatch.StartNew();
        primary.ValueChanged += OnReport;
        try
        {
            // Fresh first notification only; no cached reads, feature changes,
            // state replay, gyro/report parser swap, or primary input routing.
            var result = await Pro2InitialBatterySnapshot.CaptureAsync(
                async ct =>
                {
                    if (!sameLive()) return false;
                    progress.Report("[BATTERY_TAP] index=" + index + " notify_begin=true max_window_ms=500");
                    return await primary.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct) == GattCommunicationStatus.Success;
                }, ct => received.Task.WaitAsync(ct),
                async _ =>
                {
                    // Cleanup has its own deadline even on caller cancellation.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var off = await primary.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(cleanup.Token);
                    var verified = await primary.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask(cleanup.Token);
                    bool released = off == GattCommunicationStatus.Success && verified.Status == GattCommunicationStatus.Success &&
                        verified.ClientCharacteristicConfigurationDescriptor == GattClientCharacteristicConfigurationDescriptorValue.None;
                    progress.Report("[BATTERY_TAP] index=" + index + " cleanup_write=" + off + " cleanup_read=" + verified.Status +
                        " cccd=" + verified.ClientCharacteristicConfigurationDescriptor + " verified=" + released +
                        " total_operation_ms=" + clock.Elapsed.TotalMilliseconds.ToString("F2") + " primary_reports=" + reports);
                    return released;
                }, token, TimeSpan.FromMilliseconds(500));
            progress.Report("[BATTERY_TAP] index=" + index + " status=" + result.Status + " cleanup=" + result.CleanupStatus +
                " payload=" + (result.Payload == null ? "none" : Convert.ToHexString(result.Payload)));
            return result;
        }
        finally { primary.ValueChanged -= OnReport; }
    }
}

public sealed partial class MainViewModel
{
    private int launchedBatteryFollowup;
    public async Task TestBatteryFollowupForLaunchAsync()
    {
        if (previewOnly || Interlocked.CompareExchange(ref launchedBatteryFollowup, 1, 0) != 0) return;
        AppendLog("[BATTERY_FOLLOWUP] armed=true test_only=true waiting_for_live_133=true");
        try
        {
            var clock = Stopwatch.StartNew();
            while ((!inputSource.IsHighRateReady || Busy) && clock.Elapsed < TimeSpan.FromSeconds(180))
                await Task.Delay(250, lifetimeCts.Token);
            if (!inputSource.IsHighRateReady) { AppendLog("[BATTERY_FOLLOWUP] skipped=no_high_rate_connection"); return; }
            string result = await Task.Run(() => inputSource.RunBatteryFollowupExperimentAsync(
                new DirectProgress<string>(AppendLog), lifetimeCts.Token), lifetimeCts.Token);
            AppendLog("[BATTERY_FOLLOWUP_COMPLETE] result=" + result);
        }
        catch (OperationCanceledException) { AppendLog("[BATTERY_FOLLOWUP] cancelled=true no_retry=true"); }
        catch (Exception ex) { AppendLog("[BATTERY_FOLLOWUP] error=" + ex + " no_retry=true"); }
    }
}
#endif
