using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Y700Switch2V60Viiper;

internal static class LiveBatteryReadRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (double low in new[] { 0d, 20d, 66d, 127.99, double.NaN, double.PositiveInfinity })
        {
            check(!Pro2LiveBatteryReadPolicy.AcceptBaseline(low, 133, 7.5), "reject unproven raw 133Hz baseline");
            check(!Pro2LiveBatteryReadPolicy.AcceptBaseline(133, low, 7.5), "reject unproven parsed 133Hz baseline");
        }
        check(Pro2LiveBatteryReadPolicy.AcceptBaseline(132.8, 132.8, 7.5), "accept real measured 133Hz baseline");
        check(!Pro2LiveBatteryReadPolicy.AcceptBaseline(133, 133, 15), "reject 15ms baseline");
        check(!Pro2LiveBatteryReadPolicy.AcceptBaseline(133, 133, double.NaN), "reject missing gap evidence");
        for (int level = 0; level <= 15; level++)
        {
            byte[] report = new byte[63]; report[0] = 1; report[1] = (byte)(level << 2);
            var result = Pro2LiveBatteryReadPolicy.Decode("Success", null, report);
            check(result.HasLevel == (level <= 9), "accept only native battery levels 0..9");
            if (level <= 9) check(result.Level == level, "preserve native battery level");
        }
        foreach (int length in Enumerable.Range(0, 11))
            check(!Pro2LiveBatteryReadPolicy.Decode("Success", null, new byte[length]).HasLevel, "reject short or all-zero reports");
        check(!Pro2LiveBatteryReadPolicy.Decode("Success", null, new byte[63]).HasLevel, "zero cache is not a fresh level");
        check(!Pro2LiveBatteryReadPolicy.Decode("ProtocolError", 2, new byte[] { 1, 32 }).HasLevel, "ATT rejection does not become battery data");
        var denied = Pro2LiveBatteryReadPolicy.Decode("ProtocolError", 2, null);
        check(denied.Status == "read_not_permitted", "preserve ATT 0x02 reason");

        var once = new Pro2LiveBatteryReadPolicy(); int calls = 0;
        Task<Pro2LiveBatteryReadResult> Read(CancellationToken _) { Interlocked.Increment(ref calls); return Task.FromResult(denied); }
        await once.ReadOnceAsync(false, () => true, Read, CancellationToken.None);
        check(calls == 0 && !once.Attempted, "no radio operation before baseline approval");
        await once.ReadOnceAsync(true, () => false, Read, CancellationToken.None);
        check(calls == 0 && !once.Attempted, "no query of a replaced connection");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => once.ReadOnceAsync(true, () => true, Read, CancellationToken.None)));
        check(calls == 1 && once.Attempted, "concurrent request guard permits exactly one read");
        check(outcomes.Count(r => r.Status == "read_not_permitted") == 1, "failed read has no retries/fallback");
        check(outcomes.Count(r => r.Status == "already_attempted") == 19, "all additional reads rejected");

        bool same = true;
        var changed = await new Pro2LiveBatteryReadPolicy().ReadOnceAsync(true, () => same, _ =>
        {
            same = false; var report = new byte[63]; report[0] = 1; report[1] = 32;
            return Task.FromResult(Pro2LiveBatteryReadPolicy.Decode("Success", null, report));
        }, CancellationToken.None);
        check(!changed.HasLevel && changed.Status == "connection_changed", "stale completion cannot update a different session");
        var failurePolicy = new Pro2LiveBatteryReadPolicy(); int failures = 0;
        var failed = await failurePolicy.ReadOnceAsync(true, () => true, _ => { failures++; throw new InvalidOperationException(); }, CancellationToken.None);
        await failurePolicy.ReadOnceAsync(true, () => true, Read, CancellationToken.None);
        check(failed.Status == "error_InvalidOperationException" && failures == 1 && calls == 1, "exception also consumes the one-shot budget");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await new Pro2LiveBatteryReadPolicy().ReadOnceAsync(true, () => true, Read, cancelled.Token); check(false, "cancelled guard"); }
        catch (OperationCanceledException) { check(calls == 1, "shutdown never sends a new read"); }
        var late = await new Pro2LiveBatteryReadPolicy().ReadOnceAsync(true, () => true,
            _ => Task.FromException<Pro2LiveBatteryReadResult>(new TimeoutException()), CancellationToken.None);
        check(late.Status == "timeout_os_may_still_complete" && !late.HasLevel, "timeout is not a claim of OS cancellation");
    }
}
