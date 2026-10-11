using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Y700Switch2V60Viiper;

internal static class BleMeasurementWindowRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        TimeSpan target = TimeSpan.FromSeconds(10);
        var waits = new List<TimeSpan>();
        TimeSpan now = TimeSpan.FromMilliseconds(9999);
        await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => now, (duration, _) =>
        {
            waits.Add(duration);
            now += duration;
            return Task.CompletedTask;
        }, CancellationToken.None);
        check(now == target && waits.Count == 1 && waits[0] == TimeSpan.FromMilliseconds(1),
            "9.999 second capture waits the final millisecond rather than becoming an invalid native baseline");

        waits.Clear();
        now = TimeSpan.FromSeconds(9);
        await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => now, (duration, _) =>
        {
            waits.Add(duration);
            now += waits.Count == 1 ? TimeSpan.FromMilliseconds(999) : duration;
            return Task.CompletedTask;
        }, CancellationToken.None);
        check(now == target && waits.Count == 2 && waits[0] == TimeSpan.FromSeconds(1) &&
            waits[1] == TimeSpan.FromMilliseconds(1),
            "early timer completion is rechecked against the capture clock and supplemented");

        waits.Clear();
        now = TimeSpan.FromMilliseconds(9999.9);
        await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => now, (duration, _) =>
        {
            waits.Add(duration);
            now += TimeSpan.FromTicks(1);
            return Task.CompletedTask;
        }, CancellationToken.None);
        check(now >= target && waits.Count > 1 && waits.TrueForAll(wait => wait >= TimeSpan.FromMilliseconds(1)),
            "repeated early completions and a fractional millisecond tail never use a zero delay or incomplete window");

        waits.Clear();
        now = TimeSpan.FromMilliseconds(10000.1);
        await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => now, (duration, _) =>
        {
            waits.Add(duration);
            return Task.CompletedTask;
        }, CancellationToken.None);
        check(waits.Count == 0, "already completed capture does not add another requested window");

        waits.Clear();
        now = TimeSpan.Zero;
        await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => now, (duration, _) =>
        {
            waits.Add(duration);
            now += duration;
            return Task.CompletedTask;
        }, CancellationToken.None);
        check(now == target && waits.Count == 10 && waits.TrueForAll(wait => wait == TimeSpan.FromSeconds(1)),
            "long measurement waits use bounded cooperative timer chunks and the same absolute capture deadline");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool called = false;
            bool cancelled = false;
            try
            {
                await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => TimeSpan.Zero, (_, _) =>
                {
                    called = true;
                    return Task.CompletedTask;
                }, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && !called, "cancelled measurement exits before scheduling a timer");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            now = TimeSpan.Zero;
            int delayCount = 0;
            bool cancelled = false;
            try
            {
                await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => now, (duration, token) =>
                {
                    check(token == cancellation.Token, "measurement delay receives the caller cancellation token");
                    delayCount++;
                    now += duration;
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && delayCount == 1 && now < target,
                "cancellation between partial windows cannot return a completed capture");
        }

        foreach (TimeSpan invalid in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1) })
        {
            bool rejected = false;
            try
            {
                await BleMeasurementWindow.WaitUntilElapsedAsync(invalid, () => TimeSpan.Zero,
                    (_, _) => Task.CompletedTask, CancellationToken.None);
            }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "nonpositive measurement duration is rejected " + invalid);
        }

        foreach (bool nullClock in new[] { true, false })
        {
            bool rejected = false;
            try
            {
                await BleMeasurementWindow.WaitUntilElapsedAsync(target,
                    nullClock ? null! : () => TimeSpan.Zero,
                    nullClock ? (_, _) => Task.CompletedTask : null!, CancellationToken.None);
            }
            catch (ArgumentNullException) { rejected = true; }
            check(rejected, "missing injected measurement clock or delay is rejected " + nullClock);
        }

        bool negativeRejected = false;
        try
        {
            await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => TimeSpan.FromTicks(-1),
                (_, _) => Task.CompletedTask, CancellationToken.None);
        }
        catch (InvalidOperationException) { negativeRejected = true; }
        check(negativeRejected, "future capture origin cannot fabricate a valid elapsed window");

        now = TimeSpan.FromSeconds(1);
        bool backwardsRejected = false;
        try
        {
            await BleMeasurementWindow.WaitUntilElapsedAsync(target, () => now, (_, _) =>
            {
                now = TimeSpan.FromMilliseconds(999);
                return Task.CompletedTask;
            }, CancellationToken.None);
        }
        catch (InvalidOperationException) { backwardsRejected = true; }
        check(backwardsRejected, "clock regression is explicit rather than producing a misleading capture duration");

        long completedOrigin = Stopwatch.GetTimestamp() - checked(Stopwatch.Frequency * 11);
        await BleMeasurementWindow.WaitUntilElapsedAsync(completedOrigin, target, CancellationToken.None);
        check(Stopwatch.GetElapsedTime(completedOrigin) >= target,
            "production overload uses actual monotonic elapsed time from the caller capture origin");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try
            {
                await BleMeasurementWindow.WaitUntilElapsedAsync(Stopwatch.GetTimestamp(), target, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "production stopwatch timer overload preserves cancellation");
        }
    }
}
