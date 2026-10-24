using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Y700Switch2V60Viiper;

/// <summary>
/// Deterministic queue-policy tests, not air-interface or subjective haptics tests.
/// Source intervals model complete native commands, not motor sample periods.
/// Amplitude codes are used only as policy features; no physical RMS is assumed.
/// </summary>
internal static class NativeRumbleTimingRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        check(Pro2RumbleOutputQueue.MaxAgeMs == 30, "native queue has a 30ms local age budget");
        Simulate(check, 12, 7.5, 120000, false, "83Hz source / 133Hz service");
        Simulate(check, 12, 15, 30000, true, "83Hz source / 66Hz service");
        Simulate(check, 4, 7.5, 30000, true, "250Hz source / 133Hz service");
        Simulate(check, 12, 15, 30000, true, "gradual envelope / 66Hz service", gradient: true);
        ProtectedFeatures(check);
        StopsAndDeadlines(check);
        FailureAndReset(check);
        FailedStopRetry(check);
        DeadlineSignificance(check);
    }

    private static void Simulate(Action<bool, string> check, double sourceMs, double serviceMs,
        int durationMs, bool expectSelection, string label, bool gradient = false)
    {
        var queue = new Pro2RumbleOutputQueue();
        queue.ConfigureServiceBudget(serviceMs);
        var originals = new Dictionary<long, byte[]>();
        int produced = 0, delivered = 0;
        long previousSourceTicks = -1;
        double maxDeliveredAgeMs = 0;
        int sourceSteps = (int)(sourceMs * 2), serviceSteps = (int)(serviceMs * 2);
        void Deliver(double now)
        {
            if (!queue.TryDequeue(Tick(now), out var item)) return;
            check(item is not null && item.Native, label + ": native metadata retained");
            if (item is null) return;
            double age = (Tick(now) - item.EnqueuedTicks) * 1000.0 / Stopwatch.Frequency;
            maxDeliveredAgeMs = Math.Max(age, maxDeliveredAgeMs);
            check(age <= Pro2RumbleOutputQueue.MaxAgeMs + 0.001, label + ": no expired impact replay");
            check(originals.TryGetValue(item.EnqueuedTicks, out var original) && original.SequenceEqual(item.Data),
                label + ": emitted command is byte-exact original, never a synthesized spectrum");
            check(item.EnqueuedTicks > previousSourceTicks, label + ": selected source order is strict");
            previousSourceTicks = item.EnqueuedTicks;
            queue.MarkWriteResult(item, true);
            delivered++;
        }

        for (int step = 0; step < durationMs * 2; step++)
        {
            double now = step / 2.0;
            if (step % sourceSteps == 0)
            {
                int phase = produced % 600;
                int amplitude = gradient ? 100 + (phase <= 300 ? phase : 600 - phase) : 200;
                byte[] packet = Packet(produced, amplitude, amplitude / 2);
                originals.Add(Tick(now), packet);
                queue.Enqueue(packet, true, Tick(now));
                produced++;
            }
            if (step % serviceSteps == 0) Deliver(now);
        }
        // Continue the same service clock; a fresh stop is part of the source stream.
        long stopTicks = Tick(durationMs);
        byte[] stop = Packet(produced, 0, 0);
        originals.Add(stopTicks, stop);
        queue.Enqueue(stop, true, stopTicks);
        produced++;
        int firstService = ((durationMs * 2 + serviceSteps - 1) / serviceSteps) * serviceSteps;
        for (int step = firstService; step <= (durationMs + 100) * 2; step += serviceSteps)
            Deliver(step / 2.0);

        check(queue.Count == 0 && previousSourceTicks == stopTicks, label + ": final source stop drains without new input");
        check(queue.ExpiredDropped == 0 && queue.OverflowDropped == 0,
            label + ": regular workload needs neither expiry nor capacity overflow");
        check(maxDeliveredAgeMs <= 30.001, label + ": delivered local age bounded to 30ms");
        if (expectSelection)
            check(queue.AdaptiveDropped > 0 && delivered < produced && delivered + queue.AdaptiveDropped == produced,
                label + ": overload is explicitly counted as lossy selection");
        else
            check(delivered == produced && queue.AdaptiveDropped == 0,
                label + ": every original command delivered, including repeated payloads");
        Console.WriteLine(FormattableString.Invariant(
            $"[RUMBLE_QUEUE_SIM] case=\"{label}\" duration_ms={durationMs} source_ms={sourceMs:F2} service_ms={serviceMs:F2} produced={produced} delivered={delivered} adaptive_drop={queue.AdaptiveDropped} expired={queue.ExpiredDropped} overflow={queue.OverflowDropped} max_delivered_age_ms={maxDeliveredAgeMs:F2} offline=true"));
        check(queue.Telemetry.Contains("policy=", StringComparison.Ordinal) &&
              queue.Telemetry.Contains("lossy=", StringComparison.Ordinal) &&
              queue.Telemetry.Contains("source_ms=", StringComparison.Ordinal) &&
              queue.Telemetry.Contains("service_ms=", StringComparison.Ordinal),
            label + ": diagnostics include policy, loss, source interval and service budget");
    }

    private static void ProtectedFeatures(Action<bool, string> check)
    {
        // A rapid source history triggers overload classification without leaving backlog.
        // These focused policy tests intentionally service warm-up writes immediately.
        var queue = Warm(check, Packet(0, 100, 100));
        byte[] baseline = Packet(10, 100, 100), peak = Packet(11, 700, 100), after = Packet(12, 100, 100);
        EnqueueTriplet(queue, baseline, peak, after);
        List<Pro2QueuedRumble> written = DrainTwo(queue, check);
        check(written.Any(item => item.Data.SequenceEqual(peak)), "short left impact is not adaptively flattened");
        check(queue.ProtectedEdges > 0, "protected impact/edge decision is exposed in counters");

        queue = Warm(check, Packet(0, 100, 100));
        peak = Packet(11, 100, 700);
        EnqueueTriplet(queue, baseline, peak, after);
        written = DrainTwo(queue, check);
        check(written.Any(item => item.Data.SequenceEqual(peak)), "right impact is protected independently of left motor");

        queue = Warm(check, Packet(0, 100, 100));
        byte[] rightOff = Packet(11, 100, 0), rightOn = Packet(12, 100, 100);
        EnqueueTriplet(queue, baseline, rightOff, rightOn);
        written = DrainTwo(queue, check);
        check(written.Any(item => item.Data.SequenceEqual(rightOff)), "single-side stop is protected while other side remains active");
        check(queue.StopProtected > 0, "single-side stop protection check is recorded");

        queue = Warm(check, Packet(0, 100, 100));
        byte[] changedFrequency = Packet(11, 100, 100, frequency: 700);
        EnqueueTriplet(queue, baseline, changedFrequency, after);
        written = DrainTwo(queue, check);
        check(written.Any(item => item.Data.SequenceEqual(changedFrequency)), "frequency change survives without frequency synthesis");

        queue = Warm(check, Packet(0, 100, 100));
        byte[] opaque = Packet(11, 100, 100); opaque[1] = 0x7b;
        EnqueueTriplet(queue, baseline, opaque, after);
        written = DrainTwo(queue, check);
        check(written.Any(item => item.Data.SequenceEqual(opaque)) && queue.OpaqueDropped == 0,
            "unknown motor header is not treated as silent or adaptively interpreted");

        // A local maximum in a later subframe must be as visible as slot zero.
        queue = Warm(check, Packet(0, 100, 100));
        byte[] thirdSlotPeak = Packet(11, 100, 100);
        SetFrame(thirdSlotPeak, 12, 391, 900, 274, 100);
        EnqueueTriplet(queue, baseline, thirdSlotPeak, after);
        written = DrainTwo(queue, check);
        check(written.Any(item => item.Data.SequenceEqual(thirdSlotPeak)), "later native subframe impact is not discarded");
    }

    private static void StopsAndDeadlines(Action<bool, string> check)
    {
        var queue = Warm(check, Packet(0, 0, 0));
        byte[] impact = Packet(10, 700, 300), stop = Packet(11, 0, 0), stopAgain = Packet(12, 0, 0);
        EnqueueTriplet(queue, impact, stop, stopAgain);
        List<Pro2QueuedRumble> written = DrainTwo(queue, check);
        check(written.Count == 2 && written[0].Data.SequenceEqual(impact) &&
              !Pro2NativeRumbleEncoder.HasEffect(written[1].Data),
            "short onset is sent before its stop, not replaced by a premature stop");
        check(queue.StopProtected > 0, "stop protection is recorded rather than silently described as lossless");

        queue = Warm(check, Packet(0, 300, 100));
        queue.Enqueue(impact, true, Tick(30));
        queue.Enqueue(stop, true, Tick(34));
        check(queue.TryDequeue(Tick(100), out var expiry) && expiry is not null &&
              !Pro2NativeRumbleEncoder.HasEffect(expiry.Data),
            "transport stall produces neutral, never an expired old impact");
        if (expiry is not null) queue.MarkWriteResult(expiry, true);
        check(queue.ExpiredDropped == 1 && queue.ExpiryStops == 0 && queue.DeadlineStopsDelivered == 1 &&
              expiry!.Data.SequenceEqual(stop) && queue.Count == 0,
            "stall drops expired impact but delivers original explicit stop rather than replacing it");
        check(queue.DeadlineEdgeDropped > 0, "deadline-discarded edge is not misreported as preserved");

        queue = Warm(check, Packet(0, 100, 100));
        byte[] opaque = Packet(10, 300, 100); opaque[17] = 0x7d;
        queue.Enqueue(opaque, true, Tick(30));
        check(queue.TryDequeue(Tick(100), out var opaqueStop) && opaqueStop is not null &&
              !Pro2NativeRumbleEncoder.HasEffect(opaqueStop.Data), "expired opaque command fails closed to neutral");
        if (opaqueStop is not null) queue.MarkWriteResult(opaqueStop, true);
        check(queue.OpaqueDropped == 1, "opaque expiry is separately counted");
    }

    private static void FailureAndReset(Action<bool, string> check)
    {
        var queue = Warm(check, Packet(0, 0, 0));
        byte[] impact = Packet(10, 500, 500), stop = Packet(11, 0, 0);
        queue.Enqueue(impact, true, Tick(30));
        check(queue.TryDequeue(Tick(34), out var failed) && failed is not null, "failed transport attempt is available to simulate");
        if (failed is not null) queue.MarkWriteResult(failed, false);
        queue.Enqueue(stop, true, Tick(38));
        check(queue.TryDequeue(Tick(46), out var final) && final is not null && final.Data.SequenceEqual(stop),
            "write failure does not swallow the subsequent explicit stop");
        if (final is not null) queue.MarkWriteResult(final, true);

        queue.Enqueue(impact, true, Tick(50));
        queue.Enqueue(stop, false, Tick(51));
        bool changedMode = queue.Count == 1 && queue.ModeDiscarded > 0;
        bool hasConverted = queue.TryDequeue(Tick(52), out var converted);
        check(changedMode && hasConverted &&
              converted is not null && !converted.Native && converted.Data.SequenceEqual(stop),
            "mode switch still clears stale native effects and sends converted stop");
        if (converted is not null) queue.MarkWriteResult(converted, true);

        queue = Warm(check, Packet(0, 300, 300));
        queue.Enqueue(impact, true, Tick(50));
        queue.Enqueue(impact, true, Tick(54));
        check(queue.StopForGainChange(Tick(55)) && queue.GainDiscarded == 2 && queue.GainStops == 1,
            "gain change clears old-strength backlog and counts the discard");
        check(queue.Telemetry.Contains("rumble_lossy=true", StringComparison.Ordinal),
            "intentional gain-change discards are not called lossless");
        byte[] lowerGain = Packet(12, 50, 50);
        queue.Enqueue(lowerGain, true, Tick(56));
        check(queue.TryDequeue(Tick(57), out var gainStop) && gainStop is not null && gainStop.SafetyStop &&
              !Pro2NativeRumbleEncoder.HasEffect(gainStop.Data), "gain change sends neutral before newly scaled input");
        if (gainStop is not null) queue.MarkWriteResult(gainStop, true);
        check(queue.TryDequeue(Tick(61), out var gainNext) && gainNext is not null && gainNext.Data.SequenceEqual(lowerGain),
            "new lower-strength event survives gain-change stop");

        queue.Reset();
        check(queue.Count == 0 && queue.AdaptiveDropped == 0 && queue.DeadlineEdgeDropped == 0 &&
              queue.OpaqueDropped == 0 && queue.ProtectedEdges == 0 && queue.StopProtected == 0 &&
              queue.StopRetries == 0 && queue.StopRetryFailures == 0 && queue.StopRetrySuperseded == 0 &&
              queue.DeadlinePeakDropped == 0 && queue.DeadlineFrequencyDropped == 0 && queue.DeadlineStopDropped == 0,
            "reconnection resets adaptive history and all policy counters");
    }

    private static void FailedStopRetry(Action<bool, string> check)
    {
        var queue = Warm(check, Packet(0, 300, 300));
        byte[] stop = Packet(10, 0, 0), impact = Packet(11, 400, 300);
        queue.Enqueue(stop, true, Tick(30));
        check(queue.TryDequeue(Tick(31), out var first) && first != null, "final source stop is available");
        if (first != null) queue.MarkWriteResult(first, false, Tick(32));
        check(queue.StopRetries == 1 && queue.Count == 1 && queue.WriteFailures == 1,
            "failed final neutral schedules one bounded safety retry without another source event");
        check(queue.TryDequeue(Tick(46), out var retry) && retry != null && retry.SafetyStop &&
              retry.StopRetryAttempt == 1 && !Pro2NativeRumbleEncoder.HasEffect(retry.Data),
            "retry contains neutral only and explicit generation metadata");
        if (retry != null) queue.MarkWriteResult(retry, false, Tick(47));
        check(queue.Count == 0 && queue.StopRetryFailures == 1 && queue.StopRetries == 1,
            "second stop failure terminates retry chain instead of retrying forever");

        queue = Warm(check, Packet(0, 300, 300));
        queue.Enqueue(stop, true, Tick(30)); queue.TryDequeue(Tick(31), out first);
        if (first != null) queue.MarkWriteResult(first, false, Tick(32));
        queue.Enqueue(impact, true, Tick(33));
        check(queue.StopRetrySuperseded == 1 && queue.Count == 1 &&
              queue.TryDequeue(Tick(34), out var fresh) && fresh != null && fresh.Data.SequenceEqual(impact),
            "fresh source supersedes generated retry, never receives an obsolete inserted stop");

        queue = Warm(check, Packet(0, 300, 300));
        queue.Enqueue(stop, true, Tick(30)); queue.TryDequeue(Tick(31), out first);
        queue.Enqueue(impact, true, Tick(32));
        if (first != null) queue.MarkWriteResult(first, false, Tick(33));
        check(queue.StopRetries == 0 && queue.StopRetrySuperseded == 1 && queue.Count == 1,
            "already pending fresh source is not displaced by failed stop retry");

        queue = Warm(check, Packet(0, 300, 300));
        queue.Enqueue(impact, true, Tick(30)); queue.TryDequeue(Tick(31), out first);
        if (first != null) queue.MarkWriteResult(first, false, Tick(32));
        check(queue.Count == 0 && queue.StopRetries == 0, "failed impact is never replayed by stop retry policy");

        queue = Warm(check, Packet(0, 300, 300));
        queue.Enqueue(stop, true, Tick(30)); queue.TryDequeue(Tick(31), out first);
        if (first != null) queue.MarkWriteResult(first, false, Tick(32));
        check(queue.TryDequeue(Tick(100), out var agedRetry) && agedRetry != null &&
              agedRetry.StopRetryAttempt == 1 && !Pro2NativeRumbleEncoder.HasEffect(agedRetry.Data),
            "retry expiration preserves generation rather than resetting retry allowance");
        if (agedRetry != null) queue.MarkWriteResult(agedRetry, false, Tick(101));
        check(queue.Count == 0 && queue.StopRetries == 1 && queue.StopRetryFailures == 1,
            "expired retry cannot create an infinite sequence of deadline stops");
    }

    private static void DeadlineSignificance(Action<bool, string> check)
    {
        byte[] baseline = Packet(10, 100, 100), peak = Packet(11, 700, 100), after = Packet(12, 100, 100);
        var queue = Warm(check, Packet(0, 100, 100));
        EnqueueTriplet(queue, baseline, peak, after);
        queue.TryDequeue(Tick(100), out _);
        check(queue.DeadlinePeakDropped > 0 && queue.DeadlineEdgeDropped > 0,
            "expired nonzero local peak is counted as lost salience even without active-state transition");

        queue = Warm(check, Packet(0, 100, 100));
        EnqueueTriplet(queue, baseline, Packet(11, 100, 100, frequency: 700), after);
        queue.TryDequeue(Tick(100), out _);
        check(queue.DeadlineFrequencyDropped > 0, "expired frequency transition is separately disclosed");

        queue = Warm(check, Packet(0, 100, 100));
        EnqueueTriplet(queue, baseline, Packet(11, 100, 0), after);
        queue.TryDequeue(Tick(100), out _);
        check(queue.DeadlineStopDropped > 0, "expired single-side stop is separately disclosed");
        check(queue.Telemetry.Contains("rumble_edge_protection_checks=", StringComparison.Ordinal) &&
              queue.Telemetry.Contains("protection_checks_are_not_deliveries=true", StringComparison.Ordinal),
            "protection counters are explicitly decision checks, never delivered-event claims");
    }

    private static Pro2RumbleOutputQueue Warm(Action<bool, string> check, byte[] baseline)
    {
        var queue = new Pro2RumbleOutputQueue(); queue.ConfigureServiceBudget(15);
        for (int i = 0; i < 10; i++)
        {
            queue.Enqueue(baseline, true, Tick(i * 3));
            check(queue.TryDequeue(Tick(i * 3), out var item) && item is not null, "warm-up is immediately serviced");
            if (item is not null) queue.MarkWriteResult(item, true);
        }
        return queue;
    }

    private static void EnqueueTriplet(Pro2RumbleOutputQueue queue, byte[] first, byte[] middle, byte[] last)
    {
        queue.Enqueue(first, true, Tick(30));
        queue.Enqueue(middle, true, Tick(34));
        queue.Enqueue(last, true, Tick(38));
    }

    private static List<Pro2QueuedRumble> DrainTwo(Pro2RumbleOutputQueue queue, Action<bool, string> check)
    {
        var written = new List<Pro2QueuedRumble>();
        foreach (double now in new[] { 46.0, 61.0 })
        {
            if (!queue.TryDequeue(Tick(now), out var item) || item is null) continue;
            check(item.EnqueuedTicks >= Tick(30) && item.EnqueuedTicks <= Tick(38),
                "feature protection forwards an original command rather than inventing a frame");
            if (written.Count > 0)
                check(item.EnqueuedTicks > written[^1].EnqueuedTicks, "protected effects never reorder source events");
            queue.MarkWriteResult(item, true);
            written.Add(item);
        }
        return written;
    }

    private static long Tick(double ms) => 1 + (long)Math.Round(ms * Stopwatch.Frequency / 1000.0);

    private static byte[] Packet(int sequence, int left, int right, int frequency = 391)
    {
        var data = new byte[33]; data[1] = (byte)(0x50 | (sequence & 15));
        data[17] = (byte)(0x50 | ((sequence + 7) & 15));
        for (int frame = 0; frame < 3; frame++)
        {
            SetFrame(data, 2 + frame * 5, frequency, left, 274, left / 2);
            SetFrame(data, 18 + frame * 5, frequency, right, 274, right / 2);
        }
        return data;
    }

    private static void SetFrame(byte[] data, int offset, int firstFrequency, int firstAmplitude,
        int secondFrequency, int secondAmplitude)
    {
        ulong packed = (ulong)(uint)firstFrequency | ((ulong)(uint)firstAmplitude << 10) |
                       ((ulong)(uint)secondFrequency << 20) | ((ulong)(uint)secondAmplitude << 30);
        for (int i = 0; i < 5; i++) data[offset + i] = (byte)(packed >> (i * 8));
    }
}
