using System.Text.RegularExpressions;

namespace Y700Switch2V60Viiper;

public enum Pro2SleepResult { Deferred, PairingFailed, CommandFailed, Sleeping }

public static class Pro2SleepSafety
{
    // Recheck after both waits: activity or settings can change while another slot connects.
    public static async Task<Pro2SleepResult> EnterAsync(SemaphoreSlim operationGate,
        Func<bool> eligible, Func<Task<bool>> prepareWake, Func<Task<bool>> sendSleep,
        Func<Task> commit, CancellationToken token)
    {
        if (!eligible()) return Pro2SleepResult.Deferred;
        await operationGate.WaitAsync(token);
        try
        {
            if (!eligible()) return Pro2SleepResult.Deferred;
            if (!await prepareWake()) return Pro2SleepResult.PairingFailed;
            token.ThrowIfCancellationRequested();
            if (!eligible()) return Pro2SleepResult.Deferred;
            if (!await sendSleep()) return Pro2SleepResult.CommandFailed;
            await commit(); // Only a confirmed sleep disconnect may release this slot's input.
            return Pro2SleepResult.Sleeping;
        }
        finally { operationGate.Release(); }
    }

    public static async Task WaitForWakeAsync(Func<CancellationToken, Task<bool>> listen,
        Func<Exception?, CancellationToken, Task> retry, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Exception? error = null;
            try { if (await listen(token)) return; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { error = ex; }
            // Radio unavailable / watcher aborted is not evidence of a button press.
            await retry(error, token);
        }
    }

    public static bool IsWakeAdvertisement(ulong expected, ulong actual, bool nintendo) =>
        expected != 0 && expected == actual && nintendo;

    public static ulong ResolveHost(string deviceId, IEnumerable<ulong> exposedAddresses)
    {
        ulong[] addresses = exposedAddresses.Where(a => a != 0).Distinct().ToArray();
        // Windows BLE interface IDs can include the local radio before the remote address.
        // Match that field only; never mistake the remote controller address for a radio.
        Match match = Regex.Match(deviceId ?? "", @"BluetoothLE(?<host>(?:[0-9a-f]{2}:){5}[0-9a-f]{2}|[0-9a-f]{12})-",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            return ulong.TryParse(match.Groups["host"].Value.Replace(":", ""),
                System.Globalization.NumberStyles.HexNumber, null, out ulong host) && addresses.Contains(host) ? host : 0;
        if (addresses.Length == 1) return addresses[0];
        return 0; // Ambiguous ownership must not write another adapter's address to the controller.
    }
}
