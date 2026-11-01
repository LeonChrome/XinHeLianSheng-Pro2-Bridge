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
        if (!AutoSleepEnabled || !source.IsRunning) return false;
        string address = source.ConnectedAddress;
        if (string.IsNullOrWhiteSpace(address)) return false;
        AutoSleepSlotState state = AutoSleepState(slot.Index);
        var progress = new DirectProgress<string>(line => AppendLog("[PRO2_SLOT " + slot.Index + "] " + line));

        // The controller only emits a wake advertisement for hosts in its pairing list.
        if (!string.Equals(state.WakeReadyAddress, address, StringComparison.OrdinalIgnoreCase))
        {
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
                return false;
            }
        }

        TimeSpan idle = source.UserIdleFor;
        int minutes = AutoSleepMinutes;
        if (idle < TimeSpan.FromMinutes(minutes)) return false;

        AppendLog("[PRO2_SLEEP] slot=" + slot.Index + " address=" + address +
                  " idle_s=" + idle.TotalSeconds.ToString("F0") + " threshold_min=" + minutes + " entering_sleep=true");
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            bool dropped = await source.SendSleepCommandAsync(progress, cancellationToken);
            await source.StopAsync();
            // The sleep-induced disconnect is expected; do not report it as an outage.
            while (source.TryConsumeDisconnectSignal(out _)) { }
            state.Sleeping = true;
            state.SleepingAddress = address;
            state.SleepingSinceUtc = DateTime.UtcNow;
            AppendLog("[PRO2_SLEEP] slot=" + slot.Index + " sleeping=true link_dropped=" + dropped);
        }
        finally
        {
            bleOperationGate.Release();
        }

        string text = "已自动休眠（" + minutes + " 分钟无操作）· 按手柄任意键唤醒";
        slot.RefreshFromSource();
        slot.Status = text;
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
            return;
        }

        var woke = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.Received += (_, args) =>
        {
            if (args.BluetoothAddress != target) return;
            foreach (BluetoothLEManufacturerData data in args.Advertisement.ManufacturerData)
            {
                if (data.CompanyId == 0x0553)
                {
                    woke.TrySetResult("advertisement rssi=" + args.RawSignalStrengthInDBm);
                    return;
                }
            }
        };
        watcher.Stopped += (_, args) =>
        {
            if (args.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
                woke.TrySetResult("watcher_stopped error=" + args.Error);
        };

        try
        {
            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => woke.TrySetCanceled(cancellationToken));
            watcher.Start();
            AppendLog("[PRO2_WAKE] slot=" + slot.Index + " waiting address=" + state.SleepingAddress +
                      " watcher=" + watcher.Status + " scan=passive");
            string reason = await woke.Task;
            AppendLog("[PRO2_WAKE] slot=" + slot.Index + " wake=" + reason +
                      " slept_s=" + (DateTime.UtcNow - state.SleepingSinceUtc).TotalSeconds.ToString("F0"));
            slot.Status = "检测到手柄唤醒，正在重连…";
            if (slot.Index == 1)
            {
                InputStatus = "检测到 Pro2 按键唤醒，正在自动重连…";
            }
        }
        finally
        {
            try { watcher.Stop(); } catch { }
            state.Sleeping = false;
            UpdateMultiSlotInputStatus();
            RaiseConnectionStateChanged();
            OnPropertyChanged(nameof(IsPrimarySleeping));
        }
    }
}
