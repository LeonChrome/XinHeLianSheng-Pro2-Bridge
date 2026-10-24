using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Y700Switch2V60Viiper;

internal static class ConvertedRumbleTimingRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (string source in new[] { "switch1-hd", "xinput", "dualsense", "dualsense-hd-audio" })
        {
            Edges(check, source);
            Stops(check, source);
        }
        Simulate(check, 7.5, false);
        Simulate(check, 15, true);
        var switched = new Pro2RumbleOutputQueue();
        switched.Enqueue(Packet(0, 200), false, Tick(1), "switch1-hd-converted");
        switched.Enqueue(Packet(1, 250), false, Tick(2), "xinput");
        check(switched.ModeDiscarded == 1 && switched.Count == 1 &&
            switched.TryDequeue(Tick(3), out var newMode) && newMode!.Source == "xinput",
            "mode change clears old converted effects even though both sources are non-native");
        Metrics(check);
    }

    private static long Tick(double ms) => 1 + (long)Math.Round(ms * Stopwatch.Frequency / 1000.0);
    private static byte[] Packet(int sequence, int amplitude)
    {
        byte[] packet = new byte[33];
        packet[1] = packet[17] = (byte)(0x50 | (sequence & 15));
        for (int side = 0; side < 2; side++)
        for (int frame = 0; frame < 3; frame++)
        {
            ulong bits = 391UL | ((ulong)amplitude << 10) | (274UL << 20) | ((ulong)(amplitude / 2) << 30);
            int offset = 2 + side * 16 + frame * 5;
            for (int i = 0; i < 5; i++) packet[offset + i] = (byte)(bits >> (i * 8));
        }
        return packet;
    }

    private static void Edges(Action<bool, string> check, string source)
    {
        var queue = new Pro2RumbleOutputQueue();
        foreach (bool startsActive in new[] { true, false })
        {
            queue.Reset();
            byte[] first = Packet(0, startsActive ? 300 : 0);
            byte[] second = Packet(1, startsActive ? 0 : 300);
            queue.Enqueue(first, false, Tick(100), source);
            queue.Enqueue(second, false, Tick(101), source);
            // Own the producer's buffer and preserve both events without overload.
            byte[] expectedFirst = first.ToArray();
            Array.Clear(first);
            check(queue.Count == 2 && queue.Replaced == 0, source + ": converted short edge is not latest-state overwritten");
            check(queue.TryDequeue(Tick(102), out var a) && !a!.Native &&
                  a.Source == source && a.Data.SequenceEqual(expectedFirst),
                source + ": first edge retains provenance and owned payload");
            check(queue.TryDequeue(Tick(110), out var b) && b!.Data.SequenceEqual(second),
                source + ": second edge remains ordered");
        }
    }

    private static void Stops(Action<bool, string> check, string source)
    {
        var queue = new Pro2RumbleOutputQueue();
        byte[] active = Packet(0, 200), stop = Packet(1, 0);
        queue.Enqueue(stop, false, Tick(1), source);
        check(queue.TryDequeue(Tick(2), out var item), source + ": explicit stop dequeues");
        queue.MarkWriteResult(item!, false, Tick(3));
        bool hasRetry = queue.TryDequeue(Tick(4), out var retry);
        check(queue.StopRetries == 1 && queue.Count == 0 &&
              hasRetry && !retry!.Native &&
              retry.Source == source && retry.StopRetryAttempt == 1 &&
              !Pro2NativeRumbleEncoder.HasEffect(retry.Data),
            source + ": failed converted stop has exactly one neutral retry");
        queue.MarkWriteResult(retry!, false, Tick(5));
        check(queue.Count == 0 && queue.StopRetryFailures == 1,
            source + ": converted stop retry is bounded");

        queue.Enqueue(active, false, Tick(6), source);
        queue.TryDequeue(Tick(7), out var running);
        queue.MarkWriteResult(running!, true);
        queue.Enqueue(active, false, Tick(8), source);
        check(queue.StopForGainChange(Tick(9)) && queue.GainDiscarded == 1 &&
              queue.TryDequeue(Tick(10), out var muted) && muted!.SafetyStop && !muted.Native &&
              !Pro2NativeRumbleEncoder.HasEffect(muted.Data),
            source + ": gain change clears stale scaled effects and queues neutral");

        queue.Reset();
        queue.Enqueue(active, false, Tick(10), source);
        check(queue.TryDequeue(Tick(41), out var expired) && expired!.SafetyStop &&
              !Pro2NativeRumbleEncoder.HasEffect(expired.Data) && queue.ExpiredDropped == 1,
            source + ": converted deadline is 30ms, not 120ms");

        queue.Reset();
        queue.Enqueue(active, false, Tick(1), source);
        queue.Enqueue(stop, false, Tick(2), source, safetyStop: true);
        check(queue.Count == 1 && queue.SafetyStops == 1 && queue.SafetyDiscarded == 1,
            source + ": invalid conversion can cancel queued impacts with a safety stop");
    }

    private static void Simulate(Action<bool, string> check, double serviceMs, bool lossy)
    {
        var queue = new Pro2RumbleOutputQueue();
        queue.ConfigureServiceBudget(serviceMs);
        var original = new Dictionary<long, byte[]>();
        int produced = 0, delivered = 0;
        long previous = -1, last = -1;
        void Deliver(double ms)
        {
            if (!queue.TryDequeue(Tick(ms), out var item)) return;
            delivered++;
            check(!item!.Native && item.Source == "dualsense-hd-audio",
                "converted simulation: provenance survives scheduling");
            check(item.EnqueuedTicks > previous && original.TryGetValue(item.EnqueuedTicks, out var expected) &&
                  expected.SequenceEqual(item.Data), "converted simulation: ordered exact generated parameter packets");
            check(Tick(ms) - item.EnqueuedTicks <= Stopwatch.Frequency * .030001,
                "converted simulation: no old impact replay");
            previous = last = item.EnqueuedTicks;
            queue.MarkWriteResult(item, true);
        }
        const int duration = 120000;
        int serviceSteps = (int)(serviceMs * 2);
        for (int step = 0; step < duration * 2; step++)
        {
            if (step % 24 == 0)
            {
                byte[] data = Packet(produced++, 200);
                original.Add(Tick(step / 2.0), data);
                queue.Enqueue(data, false, Tick(step / 2.0), "dualsense-hd-audio");
            }
            if (step % serviceSteps == 0) Deliver(step / 2.0);
        }
        long stopAt = Tick(duration);
        byte[] stop = Packet(produced++, 0);
        original.Add(stopAt, stop);
        queue.Enqueue(stop, false, stopAt, "dualsense-hd-audio");
        for (int step = ((duration * 2 + serviceSteps - 1) / serviceSteps) * serviceSteps;
             step <= (duration + 100) * 2; step += serviceSteps) Deliver(step / 2.0);
        check(queue.Count == 0 && last == stopAt, "converted simulation: final stop drains without new producer");
        check(queue.ExpiredDropped == 0 && queue.OverflowDropped == 0,
            "converted simulation: regular service avoids expiry and overflow");
        check(delivered + queue.AdaptiveDropped == produced && (lossy ? queue.AdaptiveDropped > 0 : delivered == produced),
            lossy ? "83-to-66: loss is explicitly counted, not called lossless" : "83-to-133: two simulated minutes preserve every command");
        Console.WriteLine(FormattableString.Invariant(
            $"[RUMBLE_QUEUE_SIM] scope=converted duration_ms={duration} source_ms=12 service_ms={serviceMs:F2} produced={produced} delivered={delivered} adaptive_drop={queue.AdaptiveDropped} expired={queue.ExpiredDropped} overflow={queue.OverflowDropped} offline=true"));
    }

    private static void Metrics(Action<bool, string> check)
    {
        var metrics = new NativeRumbleTransportMetrics();
        metrics.Reset(Tick(0));
        string[] sources = { "ns2pro-hd", "switch1-hd", "xinput", "dualsense", "dualsense-hd-audio" };
        for (int i = 0; i < sources.Length; i++)
        {
            byte[] packet = Packet(i, 453);
            metrics.ObserveSource(packet, Tick(10 + i), sources[i], native: i == 0);
            metrics.ObserveWrite(Tick(10 + i), Tick(11 + i), Tick(12 + i), true, i == 0, packet);
        }
        byte[] stop = Packet(6, 0);
        metrics.ObserveWrite(Tick(20), Tick(21), Tick(22), true, false, stop);
        check(metrics.TrySnapshot(Tick(5000), out string text) &&
              text.Contains("mode_scope=all_modes") && text.Contains("native_source_count=1 ") &&
              text.Contains("converted_source_count=4 ") && text.Contains("converted_write_success=5 "),
            "all-mode metrics separate native and converted source/write counts");
        check(text.Contains("pro1_converted") && text.Contains("ps5_pcm") &&
              text.Contains("source_left_peak_code=453 ") && text.Contains("write_left_peak_code=453 ") &&
              text.Contains("stop_write_success=1 ") && text.Contains("stop_source_to_api_max_ms=2.000"),
            "all-mode metrics include source kinds, packed amplitude and stop completion latency");
    }
}
