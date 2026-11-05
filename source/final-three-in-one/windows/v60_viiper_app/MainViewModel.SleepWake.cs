using System.Diagnostics;
using Windows.Devices.Bluetooth.Advertisement;

namespace Y700Switch2V60Viiper;

// v2.5 auto sleep + any-button wake. The virtual pad stays attached (neutral) while the
// controller sleeps, so games do not see an unplug.
public sealed partial class MainViewModel
{
    private static readonly int[] AutoSleepMinuteChoices = [5, 10, 15, 20, 30, 60];
    private static readonly TimeSpan WakePairingRetryInterval = TimeSpan.FromMinutes(2);
    private readonly Dictionary<int, AutoSleepSlotState> autoSleepStates = new();

    private sealed class AutoSleepSlotState
    {
        public bool Sleeping;
        public string SleepingAddress = "";
        public DateTime SleepingSinceUtc;
        public string WakeReadyAddress = "";
        public long LastPairingAttemptTicks;
        public string LastPairingAttemptAddress = "";
        public long LastSleepAttemptTicks;
        public double BeforeSleepFd2Hz;
        public double BeforeSleepParsedHz;
        public long WakeObservedTicks;
        public bool WakeRateReported;
        public bool WakeRateWasLow;
        public Pro2WakeAdvertisement? WakeAdvertisement;
    }

    public IReadOnlyList<int> AutoSleepMinuteOptions => AutoSleepMinuteChoices;

    public bool AutoSleepEnabled
    {
        get => userSettings.AutoSleepEnabled;
        set
        {
            if (userSettings.AutoSleepEnabled == value) return;
            userSettings.AutoSleepEnabled = value;
            SaveUserSettings("[PRO2_SLEEP]");
            AppendLog("[PRO2_SLEEP] user_enabled=" + value + " idle_minutes=" + AutoSleepMinutes);
            OnPropertyChanged();
        }
    }

    public int AutoSleepMinutes
    {
        get => AutoSleepMinuteChoices.Contains(userSettings.AutoSleepMinutes) ? userSettings.AutoSleepMinutes : 10;
        set
        {
            if (!AutoSleepMinuteChoices.Contains(value) || userSettings.AutoSleepMinutes == value) return;
            userSettings.AutoSleepMinutes = value;
            SaveUserSettings("[PRO2_SLEEP]");
            AppendLog("[PRO2_SLEEP] idle_minutes=" + value);
            OnPropertyChanged();
        }
    }

    public bool IsPrimarySleeping => AutoSleepState(1).Sleeping;

    private AutoSleepSlotState AutoSleepState(int slotIndex)
    {
        lock (autoSleepStates)
        {
            if (!autoSleepStates.TryGetValue(slotIndex, out AutoSleepSlotState? state))
            {
                state = new AutoSleepSlotState();
                autoSleepStates[slotIndex] = state;
            }
            return state;
        }
    }

    private void OnAutoSleepLiveSessionStarted(Pro2ControllerSlot slot, Pro2BleInputSource source)
    {
        source.ResetUserActivity();
        AutoSleepSlotState state = AutoSleepState(slot.Index);
        state.WakeReadyAddress = "";
        state.LastPairingAttemptTicks = 0;
        state.LastSleepAttemptTicks = 0;
        state.Sleeping = false;
        state.WakeAdvertisement = null;
        slot.IsSleeping = false;
        slot.SleepStatus = AutoSleepEnabled ? "自动休眠：每只手柄独立计时" : "";
        OnPropertyChanged(nameof(IsPrimarySleeping));
    }

    /// <summary>
    /// Called from the reconnect guard while the slot is live. Returns true when the controller
    /// was put to sleep (the caller must then continue its loop).
    /// </summary>
    private async Task<bool> TryEnterAutoSleepAsync(
        Pro2ControllerSlot slot,
        Pro2BleInputSource source,
        CancellationToken cancellationToken)
    {
        if (!AutoSleepEnabled || !source.IsRunning ||
            source.UserIdleFor < TimeSpan.FromMinutes(AutoSleepMinutes)) return false;
        string address = source.ConnectedAddress;
        if (string.IsNullOrWhiteSpace(address)) return false;
        AutoSleepSlotState state = AutoSleepState(slot.Index);
        var progress = new DirectProgress<string>(line => AppendLog("[PRO2_SLOT " + slot.Index + "] " + line));

        if (state.LastSleepAttemptTicks != 0 &&
            Stopwatch.GetElapsedTime(state.LastSleepAttemptTicks) < WakePairingRetryInterval) return false;
        int minutes = AutoSleepMinutes;
        bool Eligible() => AutoSleepEnabled && slot.Enabled && source.IsRunning &&
            !source.IsCompatibilityOptimizationRunning &&
            string.Equals(source.ConnectedAddress, address, StringComparison.OrdinalIgnoreCase) &&
            source.UserIdleFor >= TimeSpan.FromMinutes(AutoSleepMinutes);

        Pro2SleepResult result = await Pro2SleepSafety.EnterAsync(bleOperationGate, Eligible, async () =>
        {
            // Prepare wake only when actually idle, serialized with all slot connection/tuning work.
            if (string.Equals(state.WakeReadyAddress, address, StringComparison.OrdinalIgnoreCase)) return true;
            if (state.LastPairingAttemptTicks != 0 &&
                string.Equals(state.LastPairingAttemptAddress, address, StringComparison.OrdinalIgnoreCase) &&
                Stopwatch.GetElapsedTime(state.LastPairingAttemptTicks) < WakePairingRetryInterval)
            {
                return false;
            }

            state.LastPairingAttemptTicks = Stopwatch.GetTimestamp();
            state.LastPairingAttemptAddress = address;
            Pro2WakePairingResult pairing = await source.EnsureWakePairingAsync(progress, cancellationToken);
            AppendLog("[PRO2_SLEEP] slot=" + slot.Index + " address=" + address + " wake_pairing=" + pairing);
            if (pairing is Pro2WakePairingResult.AlreadyPaired or Pro2WakePairingResult.Paired)
            {
                state.WakeReadyAddress = address;
            }
            else
            {
                slot.SleepStatus = "自动休眠暂缓：" + source.WakePairingAdvice;
                return false;
            }
            return true;
        }, async () =>
        {
            minutes = AutoSleepMinutes;
            state.LastSleepAttemptTicks = Stopwatch.GetTimestamp();
            var rate = source.GetRateEvidence();
            state.BeforeSleepFd2Hz = rate.RecentWindow ? rate.Fd2Hz : 0;
            state.BeforeSleepParsedHz = rate.RecentWindow ? rate.ParsedHz : 0;
            AppendLog("[PRO2_SLEEP] slot=" + slot.Index + " address=" + address +
                " idle_s=" + source.UserIdleFor.TotalSeconds.ToString("F0") + " entering_sleep=true" +
                " before_fd2_hz=" + state.BeforeSleepFd2Hz.ToString("F2") +
                " before_parsed_hz=" + state.BeforeSleepParsedHz.ToString("F2"));
            return await source.SendSleepCommandAsync(progress, cancellationToken, Eligible);
        }, async () =>
        {
            // Commit intent before cleanup, so cancellation cannot lose the sleeping address.
            state.Sleeping = true;
            state.SleepingAddress = address;
            state.SleepingSinceUtc = DateTime.UtcNow;
            state.WakeAdvertisement = null;
            slot.IsSleeping = true;
            await source.StopAsync();
            // The sleep-induced disconnect is expected; do not report it as an outage.
            while (source.TryConsumeDisconnectSignal(out _)) { }
            AppendLog("[PRO2_SLEEP] slot=" + slot.Index + " sleeping=true link_dropped=true peers_preserved=true");
        }, cancellationToken);
        if (result != Pro2SleepResult.Sleeping)
        {
            if (result == Pro2SleepResult.CommandFailed) slot.SleepStatus = "休眠未确认，保留连接，稍后重试";
            AppendLog("[PRO2_SLEEP] slot=" + slot.Index + " result=" + result + " forced_stop=false");
            return false;
        }

        string text = "已自动休眠（" + minutes + " 分钟无操作）· 按手柄任意键唤醒";
        slot.RefreshFromSource();
        slot.Status = text;
        slot.SleepStatus = "已休眠 · 按任意键唤醒；其他手柄不受影响";
        if (slot.Index == 1)
        {
            InputStatus = "Pro2 " + text + "。虚拟手柄保持在线。";
            Status = "Pro2 休眠中，按任意键后会自动重连。";
        }
        UpdateMultiSlotInputStatus();
        RaiseConnectionStateChanged();
        OnPropertyChanged(nameof(IsPrimarySleeping));
        return true;
    }

    /// <summary>
    /// If the slot is sleeping, waits (passive scan, no GATT) until the controller advertises
    /// after a button press. Returns immediately when the slot is not sleeping.
    /// </summary>
    private async Task WaitWhileAutoSleepingAsync(
        Pro2ControllerSlot slot,
        CancellationToken cancellationToken)
    {
        AutoSleepSlotState state = AutoSleepState(slot.Index);
        if (!state.Sleeping) return;

        ulong target;
        try
        {
            target = Convert.ToUInt64(state.SleepingAddress.Replace(":", ""), 16);
        }
        catch
        {
            state.Sleeping = false;
            slot.IsSleeping = false;
            AppendLog("[PRO2_WAKE] slot=" + slot.Index + " invalid_saved_address=true");
            return;
        }

        await Pro2SleepSafety.WaitForWakeAsync(async token =>
        {
            state.WakeAdvertisement = await ListenForWakeAdvertisementAsync(slot.Index, target, token);
            return state.WakeAdvertisement != null;
        }, async (error, token) =>
        {
            state.WakeAdvertisement = null;
            AppendLog("[PRO2_WAKE] slot=" + slot.Index + " listener_retry=true sleeping_preserved=true error=" + error?.Message);
            slot.Status = "休眠中 · 蓝牙监听暂不可用，正在重试";
            slot.SleepStatus = slot.Status;
            await Task.Delay(2000, token);
        }, cancellationToken);
        state.Sleeping = false;
        state.WakeObservedTicks = Stopwatch.GetTimestamp();
        state.WakeRateReported = false;
        state.WakeRateWasLow = false;
        slot.IsSleeping = false;
        slot.Status = "检测到手柄唤醒，正在重连…";
        slot.SleepStatus = slot.Status;
        AppendLog("[PRO2_WAKE] slot=" + slot.Index + " confirmed_advertisement=true address=" + state.SleepingAddress +
            " connectable=" + state.WakeAdvertisement?.Connectable +
            " slept_seconds=" + (DateTime.UtcNow - state.SleepingSinceUtc).TotalSeconds.ToString("F0"));
        UpdateMultiSlotInputStatus();
        RaiseConnectionStateChanged();
        OnPropertyChanged(nameof(IsPrimarySleeping));
    }

    private async Task<Pro2WakeAdvertisement?> ListenForWakeAdvertisementAsync(
        int slot, ulong target, CancellationToken token)
    {
        var woke = new TaskCompletionSource<Pro2WakeAdvertisement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        Windows.Foundation.TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> onReceived = (_, args) =>
        {
            if (Pro2SleepSafety.IsWakeAdvertisement(target, args.BluetoothAddress,
                args.Advertisement.ManufacturerData.Any(data => data.CompanyId == 0x0553)))
            {
                var candidate = new Pro2ManualCandidate(args.BluetoothAddress, args.BluetoothAddressType,
                    args.Advertisement.LocalName ?? "", args.RawSignalStrengthInDBm, true, 30);
                bool connectable = args.AdvertisementType is BluetoothLEAdvertisementType.ConnectableUndirected
                    or BluetoothLEAdvertisementType.ConnectableDirected ||
                    args.AdvertisementType == BluetoothLEAdvertisementType.Extended && args.IsConnectable;
                if (candidate.IsValid && (DateTimeOffset.UtcNow - args.Timestamp).TotalSeconds is >= -1 and <= 5)
                    woke.TrySetResult(new(candidate, Stopwatch.GetTimestamp(), connectable));
            }
        };
        Windows.Foundation.TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> onStopped = (_, args) =>
        {
            stopped.TrySetResult(true);
            if (args.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
                woke.TrySetException(new InvalidOperationException("watcher_stopped=" + args.Error));
            else woke.TrySetResult(null);
        };
        watcher.Received += onReceived;
        watcher.Stopped += onStopped;

        try
        {
            using CancellationTokenRegistration registration =
                token.Register(() => woke.TrySetCanceled(token));
            watcher.Start();
            AppendLog("[PRO2_WAKE] slot=" + slot + " waiting address=" + target.ToString("X12") +
                      " watcher=" + watcher.Status + " scan=passive");
            return await woke.Task;
        }
        finally
        {
            watcher.Received -= onReceived;
            try
            {
                watcher.Stop();
                if (watcher.Status is not (BluetoothLEAdvertisementWatcherStatus.Stopped or BluetoothLEAdvertisementWatcherStatus.Aborted))
                    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
                AppendLog("[PRO2_WAKE_SCAN_RELEASE] slot=" + slot + " stopped=true status=" + watcher.Status + " before_gatt_reconnect=true");
            }
            finally { watcher.Stopped -= onStopped; }
        }
    }

    private async Task WaitForOfflineRetryAsync(Pro2ControllerSlot slot, TimeSpan delay, CancellationToken token)
    {
        string savedAddress = LastKnownPro2AddressForSlot(slot);
        string reason = slot.InputSource.ConnectionDiagnosis.Code;
        if ((slot.Index == 1 && manualReconnectTarget != null) ||
            reason is not ("no_pro2" or "no_advertisements" or "saved_not_seen") ||
            !BleDiagnosticTargetResolver.TryParsePublicAddress(savedAddress, out ulong target))
        {
            await Task.Delay(delay, token);
            return;
        }

        // Even during the long offline backoff, listen for this slot's wake
        // beacon. No connection is attempted unless this address is heard.
        long started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(delay);
        try
        {
            var advertisement = await ListenForWakeAdvertisementAsync(slot.Index, target, deadline.Token);
            token.ThrowIfCancellationRequested();
            if (advertisement != null)
            {
                AutoSleepState(slot.Index).WakeAdvertisement = advertisement;
                AppendLog("[PRO2_BLE_RECOVERY] slot=" + slot.Index + " action=offline_backoff_interrupted_by_wake address=" +
                    advertisement.Candidate.AddressText + " connectable=" + advertisement.Connectable);
                return;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested) { }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            AppendLog("[PRO2_BLE_RECOVERY] slot=" + slot.Index + " offline_listener_error=" + ex.Message);
        }
        TimeSpan remaining = delay - Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        token.ThrowIfCancellationRequested();
    }

    // Consume under the connection gate. A delayed observation falls back to a
    // fresh scan, still restricted to the same controller for this wake attempt.
    private Pro2WakeAdvertisement? TakeWakeAdvertisement(Pro2ControllerSlot slot)
    {
        var state = AutoSleepState(slot.Index);
        var advertisement = state.WakeAdvertisement;
        state.WakeAdvertisement = null;
        return advertisement;
    }

    private void RecordWakeRateOutcome(Pro2ControllerSlot slot)
    {
        var state = AutoSleepState(slot.Index);
        if (state.WakeObservedTicks == 0 || state.WakeRateReported && !state.WakeRateWasLow ||
            Stopwatch.GetElapsedTime(state.WakeObservedTicks).TotalSeconds < 15) return;
        var rate = slot.InputSource.GetRateEvidence();
        if (!rate.Connected || !rate.Running || !rate.RecentWindow || rate.LastInputAgeMs >= 500) return;
        bool high = rate.Fd2Hz >= 128 && rate.ParsedHz >= 128;
        if (state.WakeRateReported && !high) return;
        bool recovered = state.WakeRateReported && state.WakeRateWasLow && high;
        state.WakeRateReported = true;
        state.WakeRateWasLow = !high;
        AppendLog("[PRO2_WAKE_RATE] slot=" + slot.Index + " address=" + slot.InputSource.ConnectedAddress +
            " before_sleep_fd2_hz=" + state.BeforeSleepFd2Hz.ToString("F2") +
            " before_sleep_parsed_hz=" + state.BeforeSleepParsedHz.ToString("F2") +
            " after_wake_fd2_hz=" + rate.Fd2Hz.ToString("F2") + " after_wake_parsed_hz=" + rate.ParsedHz.ToString("F2") +
            " p50_ms=" + rate.Gaps.P50Ms.ToString("F2") + " p95_ms=" + rate.Gaps.P95Ms.ToString("F2") +
            " high_rate=" + high + " recovered=" + recovered +
            " source=cached_notify_window system_changes=false cause_proven=false");
    }
}
