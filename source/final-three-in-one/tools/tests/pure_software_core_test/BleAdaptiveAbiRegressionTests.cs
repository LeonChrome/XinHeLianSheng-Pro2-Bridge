using System;
using System.Linq;
using Y700Switch2V60Viiper;

internal static class BleAdaptiveAbiRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var pair in new[] { (Minimum: (ushort)6, Maximum: (ushort)12), (Minimum: (ushort)12, Maximum: (ushort)12) })
        {
            var parameters = new Win11CustomConnectionParameters(pair.Minimum, pair.Maximum, 0, 600);
            check(parameters.ReadBackForOfflineTest().SequenceEqual(new ushort[] { 600, 0, pair.Minimum, pair.Maximum }),
                "own-object ABI roundtrip preserves bounded flexible/fixed15 preset " + pair);
            check(parameters.GetterTelemetry.Contains("get_min=1", StringComparison.Ordinal) &&
                parameters.GetterTelemetry.Contains("get_max=1", StringComparison.Ordinal),
                "ABI getter counters confirm reading our object, not invoking a radio request " + pair);
        }
        foreach (var tuple in new[]
        {
            (Min: (ushort)5, Max: (ushort)6, Latency: (ushort)0, Timeout: (ushort)600),
            (Min: (ushort)12, Max: (ushort)6, Latency: (ushort)0, Timeout: (ushort)600),
            (Min: (ushort)6, Max: (ushort)13, Latency: (ushort)0, Timeout: (ushort)600),
            (Min: (ushort)6, Max: (ushort)6, Latency: (ushort)1, Timeout: (ushort)600),
            (Min: (ushort)6, Max: (ushort)6, Latency: (ushort)0, Timeout: (ushort)199),
            (Min: (ushort)6, Max: (ushort)6, Latency: (ushort)0, Timeout: (ushort)601)
        })
        {
            bool rejected = false;
            try { _ = new Win11CustomConnectionParameters(tuple.Min, tuple.Max, tuple.Latency, tuple.Timeout); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "ABI constructor cannot escape bounded arsenal parameters " + tuple);
        }
    }
}
