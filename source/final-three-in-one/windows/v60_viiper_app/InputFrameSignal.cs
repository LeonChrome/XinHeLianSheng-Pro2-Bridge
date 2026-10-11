using System;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed class InputFrameSignal
{
    private readonly SemaphoreSlim pending = new(0, 1);
    private readonly object gate = new();

    public void Notify()
    {
        lock (gate)
        {
            // A stopped virtual session may leave the signal pending for minutes.
            // Coalesce without allocating an exception for every BLE callback.
            if (pending.CurrentCount == 0) pending.Release();
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token) =>
        pending.WaitAsync(timeout, token);
}
