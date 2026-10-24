using System;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Race one source signal against one precise due-time timer, then cancel AND
/// join the loser. An abandoned semaphore waiter could consume a future source
/// signal and stall the stream; never leave either operation behind.
/// </summary>
public static class RumbleWriterWake
{
    public static async Task WaitAsync(SemaphoreSlim sourceSignal, double dueMilliseconds,
        NativeRumblePacer pacer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (double.IsPositiveInfinity(dueMilliseconds))
        {
            await sourceSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!double.IsFinite(dueMilliseconds) || dueMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(dueMilliseconds));
        if (dueMilliseconds == 0) return;
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task signal = sourceSignal.WaitAsync(turn.Token);
        Task timer = pacer.DelayAsync(TimeSpan.FromMilliseconds(dueMilliseconds), turn.Token);
        try
        {
            Task winner = await Task.WhenAny(signal, timer).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
        }
        finally
        {
            turn.Cancel();
            try { await Task.WhenAll(signal, timer).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
