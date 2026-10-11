using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Waits against the capture's monotonic clock, not just a timer's requested delay.
/// A timer may complete early; only elapsed capture time closes a measurement window.
/// </summary>
public static class BleMeasurementWindow
{
    public static Task WaitUntilElapsedAsync(long startedAtTicks, TimeSpan minimumDuration,
        CancellationToken token) =>
        WaitUntilElapsedAsync(minimumDuration, () => Stopwatch.GetElapsedTime(startedAtTicks),
            (duration, cancellation) => Task.Delay(duration, cancellation), token);

    /// <summary>
    /// Injectable monotonic elapsed clock and cooperative delay for offline boundary tests.
    /// The elapsed clock must use the same origin as the measured capture.
    /// </summary>
    public static async Task WaitUntilElapsedAsync(TimeSpan minimumDuration, Func<TimeSpan> elapsed,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token)
    {
        if (minimumDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumDuration), "A measurement duration must be positive.");
        ArgumentNullException.ThrowIfNull(elapsed);
        ArgumentNullException.ThrowIfNull(delay);

        TimeSpan previousElapsed = TimeSpan.Zero;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan actualElapsed = elapsed();
            if (actualElapsed < previousElapsed)
                throw new InvalidOperationException("The measurement elapsed clock must be nonnegative and monotonic.");
            previousElapsed = actualElapsed;
            if (actualElapsed >= minimumDuration) return;

            // Round every positive remainder upward, including a sub-millisecond tail.
            // At least 1 ms avoids a busy-spin; short chunks keep cancellation responsive
            // and do not permit the requested Task.Delay duration to stand in for evidence.
            long remainingTicks = (minimumDuration - actualElapsed).Ticks;
            long milliseconds = remainingTicks / TimeSpan.TicksPerMillisecond;
            if (remainingTicks % TimeSpan.TicksPerMillisecond != 0) milliseconds++;
            milliseconds = Math.Clamp(milliseconds, 1, 1000);
            await delay(TimeSpan.FromMilliseconds(milliseconds), token).ConfigureAwait(false);
        }
    }
}
