using System;

namespace Y700Switch2V60Viiper;

// Seed from the current source count, not zero: a consumer can start minutes
// after BLE input started. Pre-consumer reports never belong to its first window.
public sealed class InputUpdateRateWindow(uint initialUpdates)
{
    private uint previous = initialUpdates;

    public double Measure(uint current, double seconds)
    {
        uint delta = current >= previous ? current - previous : current;
        previous = current; // A new source session may reset its count.
        return double.IsFinite(seconds) && seconds > 0 ? delta / seconds : 0;
    }
}
