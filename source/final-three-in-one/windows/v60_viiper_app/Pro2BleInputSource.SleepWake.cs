using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace Y700Switch2V60Viiper;

public enum Pro2WakePairingResult
{
    NotConnected,
    AlreadyPaired,
    Paired,
    Failed
}

// Auto sleep / button wake (v2.5).
// Verified on real hardware 2026-11-01 (probe run-20261101-094212-pair-wake):
//  * 0x15 pairing handshake stores this PC's address + LTK at flash 0x1FA000;
//  * 06/02 makes the controller drop the link and turn its LEDs off;
//  * a paired controller then emits a connectable advertisement (host field = this PC)
//    on any button press, and accepts the normal unencrypted reconnect at 133 Hz.
public sealed partial class Pro2BleInputSource
{
    private readonly SemaphoreSlim wakeCommandGate = new(1, 1);
    private readonly Pro2UserActivityTracker activityTracker = new();
    private long lastUserActivityTicks;
    public string WakePairingAdvice { get; private set; } = "";

    /// <summary>Time since the last real user input (buttons, sticks, triggers; IMU ignored).</summary>
    public TimeSpan UserIdleFor
    {
        get
        {
            long ticks;
            lock (gate) ticks = lastUserActivityTicks;
            return ticks == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(ticks);
        }
    }

    public void ResetUserActivity()
    {
        lock (gate)
        {
            lastUserActivityTicks = Stopwatch.GetTimestamp();
            activityTracker.Reset(lastUserActivityTicks);
        }
    }

    // Called under lock(gate) for every parsed report.
    private void NoteUserActivityNoLock(GamepadState state, long nowTicks)
    {
        if (activityTracker.Note(state, nowTicks)) lastUserActivityTicks = nowTicks;
    }

    /// <summary>
    /// Makes sure the connected controller has this PC in its pairing list so that a button press
    /// after sleep produces a reconnect advertisement. Read-only when already paired.
    /// </summary>
    public async Task<Pro2WakePairingResult> EnsureWakePairingAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        await wakeCommandGate.WaitAsync(cancellationToken);
        try
        {
            WakePairingAdvice = "唤醒登记未确认，保持连接，稍后重试";
            if (!IsRunning || !transportConnected || commandCharacteristic == null || ackCharacteristic == null)
                return Pro2WakePairingResult.NotConnected;

            var interfaces = await DeviceInformation.FindAllAsync(BluetoothAdapter.GetDeviceSelector()).AsTask(cancellationToken);
            var localAddresses = new List<ulong>();
            foreach (var info in interfaces)
            {
                BluetoothAdapter? radio = await BluetoothAdapter.FromIdAsync(info.Id).AsTask(cancellationToken);
                if (radio != null && radio.IsLowEnergySupported && radio.IsCentralRoleSupported)
                    localAddresses.Add(radio.BluetoothAddress);
            }
            ulong host = Pro2SleepSafety.ResolveHost(device?.DeviceId ?? "", localAddresses);
            if (host == 0)
            {
                WakePairingAdvice = "无法确认此连接使用的蓝牙适配器，保持连接";
                progress.Report("[PRO2_WAKE_PAIR] skipped=local_radio_ownership_unknown adapters=" + localAddresses.Count +
                    " pairing_written=false; keep_connected=true");
                return Pro2WakePairingResult.Failed;
            }
            byte[]? before = await ReadWakeFlashAsync(Pro2SleepWakeProtocol.PairingFlashAddress, 18, cancellationToken);
            if (before == null)
            {
                progress.Report("[PRO2_WAKE_PAIR] pairing area read failed; pairing skipped.");
                return Pro2WakePairingResult.Failed;
            }

            progress.Report("[PRO2_WAKE_PAIR] stored_count=" + before[0] + " stored_host1=" +
                            Convert.ToHexString(before.AsSpan(8, 6)) + " this_host=" + host.ToString("X12"));
            if (Pro2SleepWakeProtocol.IsHostStored(before, host))
                return Pro2WakePairingResult.AlreadyPaired;

            byte[]? r1 = await WakeCommandAsync(Pro2SleepWakeProtocol.ExchangeAddressesCommand(host),
                r => r.Length >= 17, 2000, cancellationToken);
            if (r1 == null) { progress.Report("[PRO2_WAKE_PAIR] 15/01 no response."); return Pro2WakePairingResult.Failed; }

            byte[] a1 = RandomNumberGenerator.GetBytes(16);
            byte[]? r2 = await WakeCommandAsync(Pro2SleepWakeProtocol.ExchangeKeysCommand(a1),
                r => r.Length >= 25, 2000, cancellationToken);
            if (r2 == null) { progress.Report("[PRO2_WAKE_PAIR] 15/04 no response."); return Pro2WakePairingResult.Failed; }
            byte[] ltk = Pro2SleepWakeProtocol.ComputeLtk(a1, r2.AsSpan(9, 16));

            byte[] a2 = RandomNumberGenerator.GetBytes(16);
            byte[]? r3 = await WakeCommandAsync(Pro2SleepWakeProtocol.ConfirmCommand(a2),
                r => r.Length >= 25, 2000, cancellationToken);
            if (r3 == null) { progress.Report("[PRO2_WAKE_PAIR] 15/02 no response."); return Pro2WakePairingResult.Failed; }
            byte[] expected = Pro2SleepWakeProtocol.ExpectedConfirmResponse(ltk, a2);
            if (!r3.AsSpan(9, 16).SequenceEqual(expected.AsSpan()))
            {
                progress.Report("[PRO2_WAKE_PAIR] LTK confirm mismatch; not finalised.");
                return Pro2WakePairingResult.Failed;
            }

            byte[]? r4 = await WakeCommandAsync(Pro2SleepWakeProtocol.FinaliseCommand(), r => true, 3000, cancellationToken);
            if (r4 == null) { progress.Report("[PRO2_WAKE_PAIR] 15/03 no response."); return Pro2WakePairingResult.Failed; }

            byte[]? after = await ReadWakeFlashAsync(Pro2SleepWakeProtocol.PairingFlashAddress, 18, cancellationToken);
            bool stored = after != null && Pro2SleepWakeProtocol.IsHostStored(after, host);
            progress.Report("[PRO2_WAKE_PAIR] finalised stored=" + stored + " count=" + (after?[0].ToString() ?? "?"));
            return stored ? Pro2WakePairingResult.Paired : Pro2WakePairingResult.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress.Report("[PRO2_WAKE_PAIR] error " + ex.GetType().Name + ": " + ex.Message);
            return Pro2WakePairingResult.Failed;
        }
        finally
        {
            wakeCommandGate.Release();
        }
    }

    private bool HasConnectedBleOwner()
    {
        try { return device?.ConnectionStatus == BluetoothConnectionStatus.Connected &&
            (gattSession == null || gattSession.SessionStatus == GattSessionStatus.Active); }
        catch (ObjectDisposedException) { return false; }
    }

    private bool SetMaintainConnectionForLifecycle(bool maintain)
    {
        try
        {
            if (gattSession?.CanMaintainConnection == true)
                gattSession.MaintainConnection = maintain;
            return true;
        }
        catch (Exception ex)
        {
            connectionProgress?.Report("[PRO2_BLE_RELEASE] maintain=" + maintain + " error=" + ex.GetType().Name);
            return false; // Normal teardown still disposes the owner; intentional sleep must abort.
        }
    }

    /// <summary>Quiesces this owner's traffic before sending 06/02; peers stay online.</summary>
    public async Task<bool> SendSleepCommandAsync(IProgress<string> progress, CancellationToken cancellationToken,
        Func<bool>? stillIdle = null)
    {
        BluetoothLEDevice? owner = device;
        long sequence = connectionSequence;
        bool SameOwner() => ReferenceEquals(owner, device) && sequence == connectionSequence &&
            IsRunning && transportConnected && HasConnectedBleOwner();
        try { return await Pro2SleepSafety.SendQuiescedAsync(async () =>
        {
            await StopRuntimeBatteryRefreshAsync();
            await StopRumbleWriterAsync(); // final neutral, if needed, is sent while still connected
            progress.Report("[PRO2_SLEEP_QUIESCE] battery_worker=stopped rumble_writer=joined final_stop_before_sleep=true");
        }, () => SameOwner() && (stillIdle?.Invoke() ?? true), maintain =>
        {
            bool changed = SetMaintainConnectionForLifecycle(maintain);
            if (!maintain && !changed)
                throw new InvalidOperationException("Windows BLE reconnect intent was not released; sleep aborted.");
            progress.Report("[PRO2_SLEEP_QUIESCE] maintain_connection=" + maintain);
        }, () => SendSleepCommandCoreAsync(progress, cancellationToken), SameOwner, () =>
        {
            if (!SameOwner()) return;
            StartRumbleWriter(progress);
            StartRuntimeBatteryRefresh(progress);
            progress.Report("[PRO2_SLEEP_QUIESCE] sleep_not_committed=true live_workers=restored");
        }, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            progress.Report("[PRO2_SLEEP_QUIESCE] sleep_aborted=true error=" + ex.GetType().Name + " message=" + ex.Message);
            return false;
        }
    }

    /// <summary>Sends 06/02 after all other command producers have stopped.</summary>
    private async Task<bool> SendSleepCommandCoreAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        await wakeCommandGate.WaitAsync(cancellationToken);
        try
        {
            GattCharacteristic? command = commandCharacteristic;
            if (command == null || !transportConnected) return false;
            long started = Stopwatch.GetTimestamp();
            GattCommunicationStatus status = await WriteCharacteristicAsync(command, Pro2SleepWakeProtocol.SleepCommand(), cancellationToken);
            if (status != GattCommunicationStatus.Success)
            {
                progress.Report("[PRO2_SLEEP] command=06/02 write=" + status + " sleep_confirmed=false");
                return false;
            }
            while (transportConnected && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2500)
                await Task.Delay(25, CancellationToken.None); // Finish a command already accepted before cancelling the listener.
            bool dropped = !transportConnected;
            progress.Report("[PRO2_SLEEP] command=06/02 write=" + status + " link_dropped=" + dropped +
                            " elapsed_ms=" + Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0"));
            return dropped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress.Report("[PRO2_SLEEP] error " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
        finally
        {
            wakeCommandGate.Release();
        }
    }

    private async Task<byte[]?> ReadWakeFlashAsync(uint baseAddress, int length, CancellationToken cancellationToken)
    {
        byte[] block = new byte[length];
        for (int offset = 0; offset < length; offset += 9)
        {
            uint address = baseAddress + (uint)offset;
            int chunk = Math.Min(9, length - offset);
            byte[]? reply = await WakeCommandAsync(BuildBleFlashReadCommand(address),
                r => r.Length >= 16 + chunk && BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(12, 4)) == address,
                1500, cancellationToken);
            if (reply == null) return null;
            reply.AsSpan(16, chunk).CopyTo(block.AsSpan(offset));
        }
        return block;
    }

    // Writes one command and waits for its own ACK on a private listener, so the
    // formal init/battery ACK routing (ackTcs) is never touched.
    private async Task<byte[]?> WakeCommandAsync(byte[] request, Func<byte[], bool> match, int timeoutMs,
        CancellationToken cancellationToken)
    {
        GattCharacteristic? command = commandCharacteristic;
        GattCharacteristic? ack = ackCharacteristic;
        if (command == null || ack == null) return null;
        Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler =
            (_, args) => replies.Writer.TryWrite(ReadBuffer(args.CharacteristicValue));
        ack.ValueChanged += handler;
        try
        {
            GattCommunicationStatus status = await WriteCharacteristicAsync(command, request, cancellationToken);
            if (status != GattCommunicationStatus.Success) return null;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            while (true)
            {
                byte[] reply = await replies.Reader.ReadAsync(timeout.Token);
                if (reply.Length >= 8 && reply[0] == request[0] && reply[1] == 0x01 && reply[3] == request[3] && match(reply))
                    return reply;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            ack.ValueChanged -= handler;
        }
    }
}
