using System;

namespace Y700Switch2V60Viiper;

public readonly record struct NotifyGapSummary(int Samples, double P50Ms, double P95Ms, double P99Ms,
    double MinMs, double MaxMs, int Near7p5, int Near15, int Over22);

// Bounded, allocation-free on the notify hot path. Caller owns synchronization.
public sealed class NotifyGapWindow
{
    private readonly double[] gaps = new double[2048];
    private int count, stored, near75, near15, over22;
    private double min = double.PositiveInfinity, max;
    public void Record(double gapMs)
    {
        if (!double.IsFinite(gapMs) || gapMs < 0) return;
        gaps[stored++ % gaps.Length] = gapMs;
        count++;
        min = Math.Min(min, gapMs); max = Math.Max(max, gapMs);
        if (Math.Abs(gapMs - 7.5) <= 1.5) near75++;
        if (Math.Abs(gapMs - 15) <= 1.5) near15++;
        if (gapMs >= 22) over22++;
    }
    public string SummarizeAndReset() => SummarizeAndReset(out _);
    public string SummarizeAndReset(out NotifyGapSummary summary)
    {
        int n = Math.Min(stored, gaps.Length);
        Array.Sort(gaps, 0, n);
        double P(double q) => n == 0 ? 0 : gaps[Math.Clamp((int)Math.Ceiling(n * q) - 1, 0, n - 1)];
        summary = new(count, P(.5), P(.95), P(.99), count == 0 ? 0 : min, max, near75, near15, over22);
        string line = "gap_samples=" + count + " gap_p50_ms=" + P(.5).ToString("F2") +
            " gap_p95_ms=" + P(.95).ToString("F2") + " gap_p99_ms=" + P(.99).ToString("F2") +
            " gap_min_ms=" + (count == 0 ? 0 : min).ToString("F2") + " gap_max_ms=" + max.ToString("F2") +
            " gap_near7p5=" + near75 + " gap_near15=" + near15 + " gap_ge22=" + over22;
        Reset(); return line;
    }
    public void Reset() { count = stored = near75 = near15 = over22 = 0; min = double.PositiveInfinity; max = 0; }
}
