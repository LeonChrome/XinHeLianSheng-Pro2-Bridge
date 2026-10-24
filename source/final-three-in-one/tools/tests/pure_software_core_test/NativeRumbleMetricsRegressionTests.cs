using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Y700Switch2V60Viiper;

internal static class NativeRumbleMetricsRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        long Tick(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);
        byte[] report = new byte[64]; report[0] = 2; report[1] = report[17] = 0x50;
        var metrics = new NativeRumbleTransportMetrics();
        metrics.Reset(0);
        for (int i = 0; i < 417; i++)
        {
            report[1] = (byte)(0x50 | (i & 15)); report[17] = (byte)(0x50 | ((i + 7) & 15));
            long at = Tick(i * 12);
            metrics.ObserveSource(report, at);
            metrics.ObserveWrite(at, at + Tick(.2), at + Tick(.8), true);
        }
        check(!metrics.TrySnapshot(Tick(4999), out _), "rumble metrics do not emit sub-five-second windows");
        check(metrics.TrySnapshot(Tick(5000), out string s), "rumble metrics emit five-second window");
        check(Math.Abs(Number(s, "source_hz") - 83.4) < .01 && Number(s, "source_count") == 417,
            "12ms host feedback measured as 83.4Hz, not assumed FD2 frequency");
        check(Number(s, "source_gap_p50_ms") == 12 && Number(s, "source_gap_p95_ms") == 12 &&
            Number(s, "source_gap_p99_ms") == 12, "uniform source gaps preserve percentiles");
        check(Number(s, "ble_api_success_hz") == 83.4 && Number(s, "write_p95_ms") == .6 &&
            Number(s, "queue_wait_p95_ms") == .2 && Number(s, "queue_wait_max_ms") == .2,
            "BLE API completion timing and queue timing kept distinct");
        check(Number(s, "write_start_gap_p50_ms") == 12 && Number(s, "write_start_gap_p95_ms") == 12 &&
            Number(s, "write_start_gap_p99_ms") == 12, "actual write starts have independent interval percentiles");
        check(Number(s, "duplicate_ratio") == 1 && Number(s, "duplicate_comparisons") == 416 &&
            Number(s, "format_known5n") == 417, "known header sequence changes alone are duplicate effects");
        check(s.Contains("source_clock=manager_feedback_receive") &&
            s.Contains("delivery_scope=BLE_API_completion_not_air_or_playback"),
            "telemetry explicitly limits clock and delivery claims");
        check(metrics.TrySnapshot(Tick(10000), out s) && Number(s, "source_hz") == 0 &&
            Number(s, "ble_api_success_hz") == 0 && Number(s, "source_gap_samples") == 0 && s.Contains("idle=True"),
            "idle window never retains active rate or old gap samples");

        metrics.Reset(0);
        for (int i = 0; i < 1250; i++)
        {
            report[2] = (byte)i;
            metrics.ObserveSource(report, Tick(i * 4));
            if (i % 2 == 0) metrics.ObserveWrite(Tick(i * 4), Tick(i * 4 + 1), Tick(i * 4 + 2), true);
        }
        metrics.ObserveWrite(Tick(4900), Tick(4910), Tick(4912), false);
        check(metrics.TrySnapshot(Tick(5000), out s) && Number(s, "source_hz") == 250 &&
            Number(s, "ble_api_success_hz") == 125 && Number(s, "ble_api_failure") == 1,
            "250Hz host feedback and slower writes reported independently with failures");
        check(Number(s, "source_gap_p50_ms") == 4 && Number(s, "duplicate_ratio") == 0 &&
            Number(s, "queue_wait_max_ms") == 10, "changing payloads not duplicates; unsuccessful wait included");

        metrics.Reset(0);
        report[1] = report[17] = 0x70;
        metrics.ObserveSource(report, Tick(1));
        report[1] = 0x71;
        metrics.ObserveSource(report, Tick(2));
        metrics.ObserveSource(report, Tick(3));
        metrics.ObserveSource(new byte[2], Tick(4));
        metrics.ObserveWrite(Tick(5), Tick(4), Tick(6), true);
        check(metrics.TrySnapshot(Tick(5000), out s) && Number(s, "duplicate_ratio") == .5 &&
            Number(s, "format_opaque") == 3 && Number(s, "format_invalid") == 1 && Number(s, "clock_errors") == 1,
            "opaque sequence not guessed; invalid payload/timestamp diagnosed");

        metrics.Reset(0);
        report[1] = report[17] = 0x50;
        for (int i = 0; i < 20000; i++)
        {
            long at = Tick(i * .2);
            metrics.ObserveSource(report, at);
            metrics.ObserveWrite(at, at + Tick(.01), at + Tick(.02), true);
        }
        check(metrics.TrySnapshot(Tick(5000), out s) && Number(s, "source_count") == 20000 &&
            Number(s, "source_gap_retained") == NativeRumbleTransportMetrics.SampleCapacity &&
            Number(s, "samples_overwritten") == 19999 + 20000 + 20000 + 19999 - 4 * NativeRumbleTransportMetrics.SampleCapacity,
            "sample memory bounded under extreme load; overwritten sample count explicit");
        metrics.Reset(Tick(100000));
        check(!metrics.TrySnapshot(Tick(104999), out _) && metrics.TrySnapshot(Tick(105000), out s) &&
            Number(s, "source_count") == 0 && Number(s, "samples_overwritten") == 0,
            "explicit reset removes previous session state");

        metrics.Reset(0);
        Parallel.For(0, 4, worker =>
        {
            byte[] local = report.ToArray();
            for (int i = 0; i < 1000; i++)
            {
                metrics.ObserveSource(local, Tick(1));
                metrics.ObserveWrite(Tick(1), Tick(2), Tick(3), true);
            }
        });
        check(metrics.TrySnapshot(Tick(5000), out s) && Number(s, "source_count") == 4000 &&
            Number(s, "ble_api_success") == 4000, "concurrent observations do not lose counts");
    }

    private static double Number(string text, string name)
    {
        string prefix = name + "=";
        string token = text.Split(' ').Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return double.Parse(token.Substring(prefix.Length), CultureInfo.InvariantCulture);
    }
}
