using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Y700Switch2V60Viiper;

internal static class NativeRumblePacerRegressionTests
{
    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        foreach (bool fallback in new[] { false, true })
        {
            using var pacer = new NativeRumblePacer(fallback);
            check(pacer.Backend.StartsWith(fallback ? "task_delay_fallback:forced_test" : "high_resolution_waitable_timer",
                    StringComparison.Ordinal) || (!fallback && pacer.Backend.StartsWith("task_delay_fallback:", StringComparison.Ordinal)),
                "pacer backend reported, including explicit fallback");
            await pacer.DelayAsync(TimeSpan.Zero);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) await pacer.DelayAsync(TimeSpan.FromMilliseconds(2));
            check(watch.Elapsed.TotalMilliseconds >= 5, "pacer one-shot waits are not premature immediate completion");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check(await IsCancelled(pacer.DelayAsync(TimeSpan.FromSeconds(10), cancelled.Token)), "pacer honors pre-cancelled token");
            using var midway = new CancellationTokenSource();
            Task pending = pacer.DelayAsync(TimeSpan.FromSeconds(30), midway.Token);
            midway.Cancel();
            check(await IsCancelled(pending.WaitAsync(TimeSpan.FromSeconds(2))), "long pending pacer wait cancels promptly");
            await pacer.DelayAsync(TimeSpan.FromMilliseconds(1));
            check(true, "cancelled one-shot can be followed by normal reuse");
            bool invalid = false;
            try { await pacer.DelayAsync(TimeSpan.FromMilliseconds(-1)); }
            catch (ArgumentOutOfRangeException) { invalid = true; }
            check(invalid, "pacer rejects infinite/negative periods");

            // Multiple callers are serialized; cancellation of one queued caller
            // must not cancel the currently active one-shot or leak a permit.
            using var queueCancel = new CancellationTokenSource();
            Task first = pacer.DelayAsync(TimeSpan.FromMilliseconds(10));
            Task queued = pacer.DelayAsync(TimeSpan.FromSeconds(30), queueCancel.Token);
            queueCancel.Cancel();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            check(await IsCancelled(queued), "queued pacer caller cancellation does not affect active wait");
            await pacer.DelayAsync(TimeSpan.FromMilliseconds(1));
            pending = pacer.DelayAsync(TimeSpan.FromSeconds(30));
            Task another = pacer.DelayAsync(TimeSpan.FromSeconds(30));
            pacer.Dispose();
            check(await IsCancelled(pending.WaitAsync(TimeSpan.FromSeconds(2))) &&
                await IsCancelled(another.WaitAsync(TimeSpan.FromSeconds(2))),
                "pacer disposal cancels active and queued waits and drains native resources");
            pacer.Dispose();
            bool disposed = false;
            try { await pacer.DelayAsync(TimeSpan.FromMilliseconds(1)); }
            catch (ObjectDisposedException) { disposed = true; }
            check(disposed, "disposed pacer cannot acquire a new native handle");
        }

        // Cancellation can race a just-signaled timer and its queued callback.
        // A later operation must not consume that old signal or lose its own
        // signal to the old registration. Do not assert an upper scheduling
        // bound under load; the deliberately loose lower bound detects the
        // zero/near-zero completion caused by stale-signal reuse.
        using (var pacer = new NativeRumblePacer())
        using (var cancellationPacer = new NativeRumblePacer())
        for (int i = 0; i < 40; i++)
        {
            using var nearDeadline = new CancellationTokenSource();
            Task oldWait = pacer.DelayAsync(TimeSpan.FromMilliseconds(2), nearDeadline.Token);
            await cancellationPacer.DelayAsync(TimeSpan.FromMilliseconds(i % 3 == 0 ? .1 : i % 3 == 1 ? 1.8 : 2.2));
            nearDeadline.Cancel();
            await IsCancelled(oldWait); // Expiry wins in some iterations, which is valid.
            var elapsed = Stopwatch.StartNew();
            await pacer.DelayAsync(TimeSpan.FromMilliseconds(12)).WaitAsync(TimeSpan.FromSeconds(2));
            check(elapsed.Elapsed.TotalMilliseconds >= 8,
                "cancel-at-expiry cannot prematurely complete or swallow a subsequent timer");
        }

        // Warm the runtime's registered wait pool before comparing OS handles;
        // the test itself neither opens Bluetooth nor requests UAC.
        using (var warm = new NativeRumblePacer())
            for (int i = 0; i < 10; i++) await warm.DelayAsync(TimeSpan.FromMilliseconds(.1));
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh(); int before = process.HandleCount;
            for (int pass = 0; pass < 100; pass++)
            {
                using var pacer = new NativeRumblePacer();
                using var cancellation = new CancellationTokenSource();
                Task wait = pacer.DelayAsync(TimeSpan.FromSeconds(30), cancellation.Token);
                cancellation.Cancel();
                await IsCancelled(wait);
            }
            await Task.Delay(100); // Let the shared pool finish unregister notifications.
            process.Refresh(); int after = process.HandleCount;
            check(after - before < 16, "100 native timer create/cancel/dispose cycles do not leak OS handles");
        }
    }

    private static async Task<bool> IsCancelled(Task task)
    {
        try { await task.ConfigureAwait(false); return false; }
        catch (OperationCanceledException) { return true; }
    }
}
