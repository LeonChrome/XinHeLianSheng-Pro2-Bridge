using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private async Task WaitForReconnectCheckAsync(int slotIndex, CancellationToken token)
    {
        int index = Math.Clamp(slotIndex - 1, 0, reconnectWake.Length - 1);
        if (await reconnectWake[index].WaitAsync(TimeSpan.FromMilliseconds(750), token))
            AppendLog("[PRO2_RECONNECT_TIMING] slot=" + slotIndex +
                " phase=guard_woken reason=windows_disconnect_event stale_timeout_ms=" + LostInputTimeoutMilliseconds +
                " retry_backoff=unchanged");
    }

    private void ReportReconnectRecovery(int slotIndex)
    {
        int index = Math.Clamp(slotIndex - 1, 0, reconnectObservedTicks.Length - 1);
        long observed = Interlocked.Exchange(ref reconnectObservedTicks[index], 0);
        if (observed == 0) return;
        var source = pro2Slots[index].InputSource;
        AppendLog("[PRO2_RECONNECT_TIMING] slot=" + slotIndex + " phase=input_recovered observed_offline_to_live_ms=" +
            Stopwatch.GetElapsedTime(observed).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) +
            " high_rate_ready=" + source.IsHighRateReady + " gameplay_ready=" + source.IsGameplayReady +
            " scope=windows_observation_to_live_not_physical_power_on");
    }
}
