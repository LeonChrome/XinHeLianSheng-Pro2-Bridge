using System;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Retains one read and one idle tick. Normal idle must not throw 25 timeout
// exceptions per second, or replace a read which still owns the next packet.
public sealed class FeedbackIdleWait : IAsyncDisposable
{
    private readonly PeriodicTimer timer;
    private Task<bool>? tick;
    public FeedbackIdleWait(TimeSpan interval) => timer = new(interval);
    public async Task<bool> ReadReadyAsync(Task<byte[]> read, CancellationToken token)
    {
        tick ??= timer.WaitForNextTickAsync(token).AsTask();
        await Task.WhenAny(read, tick).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (read.IsCompleted) return true;
        await tick.ConfigureAwait(false);
        tick = null;
        return false;
    }
    public async ValueTask DisposeAsync()
    {
        timer.Dispose();
        if (tick != null) { try { await tick.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    }
}
