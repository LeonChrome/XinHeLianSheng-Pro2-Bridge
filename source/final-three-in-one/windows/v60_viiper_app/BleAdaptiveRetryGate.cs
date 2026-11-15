using System;

namespace Y700Switch2V60Viiper;

// Incomplete measurement is not an exhausted preset arsenal. Automatic retries
// are bounded and delayed; the manual action is never blocked by this gate.
public sealed class BleAdaptiveRetryGate(long frequency)
{
    private int incompleteRuns;
    private long lastIncomplete;
    public void Reset() { incompleteRuns = 0; lastIncomplete = 0; }
    public void NoteIncomplete(long now) { incompleteRuns = Math.Min(2, incompleteRuns + 1); lastIncomplete = now; }
    public bool AllowsAutomatic(long now) => incompleteRuns == 0 ||
        (incompleteRuns < 2 && now >= lastIncomplete && (now - lastIncomplete) / (double)frequency >= 30);
}
