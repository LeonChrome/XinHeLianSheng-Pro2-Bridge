using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Y700Switch2V60Viiper;

internal static class RumbleHoldAndPacingRegressionTests
{
    private static long Tick(double ms) => 1 + (long)Math.Round(ms * Stopwatch.Frequency / 1000);
    private static byte[] Packet(byte amplitude)
    {
        var report = Pro2OutputPacketMapper.BuildOrdinaryPacket(amplitude, amplitude, "xinput").Report;
        byte[] ble = new byte[33];
        if (!Pro2BleRumblePacketEncoder.TryEncodeRaw02(report, 3, ble, out _, out var error))
            throw new InvalidOperationException(error);
        return ble;
    }

    public static void Run(Action<bool, string> check)
    {
        foreach (string source in new[] { "xinput", "dualsense-ordinary" })
        {
            var hold = new OrdinaryRumbleHoldState();
            byte[] active = Packet(150), neutral = Packet(0);
            var start = new Pro2QueuedRumble(active, false, Tick(0), Source: source);
            hold.Observe(active, source, false, true);
            check(hold.Active && !hold.TryCreate(Tick(100), false, 0, out _), source + " must write original start before refresh");
            hold.Written(start, true, Tick(0));
            check(!hold.TryCreate(Tick(11), false, 0, out _), source + " refresh cannot run early");
            check(!hold.TryCreate(Tick(12), true, 0, out _), source + " source FIFO has priority over refresh");
            check(hold.TryCreate(Tick(12), false, 4, out var refresh) && refresh!.GeneratedKind == "refresh" &&
                  !refresh.Native && refresh.Data[1] == 0x54 && refresh.Data[17] == 0x54,
                source + " refresh has independent provenance and sequence");
            check(!hold.TryCreate(Tick(100), false, 5, out _), source + " at most one in-flight refresh");
            hold.Written(refresh!, true, Tick(12));
            long count = hold.Generated;
            // Long start/stop effects must not be silently cut at 120ms/2s.
            for (int i = 2; i <= 50000; i++)
            {
                check(hold.TryCreate(Tick(i * 12.0), false, (byte)i, out var next), source + " bounded ten-minute keepalive");
                check(next!.Data.AsSpan(2, 15).SequenceEqual(active.AsSpan(2, 15)) &&
                      next.Data.AsSpan(18, 15).SequenceEqual(active.AsSpan(18, 15)),
                    source + " refresh preserves latest encoded state");
                hold.Written(next, true, Tick(i * 12.0));
            }
            check(hold.Generated == count + 49999, source + " exactly one refresh per due service, no backlog");
            check(hold.TryCreate(Tick(600012), false, 7, out var stale), source + " select refresh for stop race");
            hold.Observe(neutral, source, false, true);
            check(!hold.Active && !hold.IsCurrent(stale!) && !hold.TryCreate(Tick(700000), false, 8, out _),
                source + " source stop immediately cancels selected and future old active refresh");
            hold.Written(stale!, true, Tick(600013));
            check(!hold.Active, source + " late in-flight completion cannot resurrect received stop");
            hold.Observe(active, source, false, true); hold.Written(start, false, Tick(1));
            check(!hold.Active && hold.FailureResets == 1, source + " failed write invalidates hold, no active retry loop");
            hold.Observe(active, source, false, true); hold.Written(start, true, Tick(1)); hold.Invalidate();
            check(!hold.TryCreate(Tick(500), false, 0, out _), source + " gain/cancel invalidation prevents refresh");
            hold.Observe(active, source, false, true); hold.Written(start, true, Tick(1));
            var expiry = new Pro2QueuedRumble(neutral, false, Tick(2), true, Source: source, GeneratedKind: "deadline_stop");
            hold.Written(expiry, true, Tick(3));
            check(!hold.Active, source + " generated deadline neutral cannot leave active hold armed");
        }
        foreach (var excluded in new[] { "ns2pro-hd", "switch1-hd-converted", "dualsense-hd-audio", "raw02", "dualsense-hd-transition-stop" })
        {
            var hold = new OrdinaryRumbleHoldState();
            hold.Observe(Packet(100), "xinput", false, true);
            hold.Observe(Packet(100), excluded, excluded.StartsWith("ns2pro"), true);
            check(!hold.Active && !hold.TryCreate(Tick(1000), false, 0, out _), excluded + " must never be synthesized or periodically replayed");
        }
        var ownership = new OrdinaryRumbleHoldState();
        byte[] raw = Packet(100); var original = raw.ToArray();
        ownership.Observe(raw, "xinput", false, true); Array.Clear(raw);
        ownership.Written(new(original, false, Tick(0), Source: "xinput"), true, Tick(0));
        check(ownership.TryCreate(Tick(12), false, 0, out var owned) && Pro2NativeRumbleEncoder.HasEffect(owned!.Data),
            "hold owns producer buffer");
        ownership.Reset();
        check(!ownership.Active && ownership.Generated == 0 && ownership.FailureResets == 0,
            "session reset clears hold and its connection-local counters");
        check(Pro2RumblePacingPolicy.MinimumWriteIntervalMs == 7.5,
            "Win10 unknown CI and Win11 15ms input do not artificially impose an 8/15ms output cap");
        QueueDeadline(check);
        Metrics(check);
        StartClockAndWake(check);
    }

    private static void QueueDeadline(Action<bool, string> check)
    {
        var q = new Pro2RumbleOutputQueue();
        q.Enqueue(Packet(100), false, Tick(0), "xinput");
        q.Enqueue(Packet(0), false, Tick(1), "xinput");
        q.Enqueue(Packet(200), false, Tick(49), "xinput");
        check(q.TryDequeue(Tick(50), out var stopped) && !Pro2NativeRumbleEncoder.HasEffect(stopped!.Data) &&
              stopped.GeneratedKind == "source" && q.DeadlineStopsDelivered == 1 && q.ExpiredDropped == 1,
            "expired impact is dropped but explicit source stop remains a barrier before fresh start");
        check(q.TryDequeue(Tick(57.5), out var next) && Pro2NativeRumbleEncoder.HasEffect(next!.Data),
            "fresh start follows original stop in order without resurrecting expired impact");
        q.Reset(); q.Enqueue(Packet(0), false, Tick(0), "xinput");
        for (int i = 0; i < 40; i++) q.Enqueue(Packet(100), false, Tick(i), "xinput");
        check(q.Count == Pro2RumbleOutputQueue.Capacity && q.TryDequeue(Tick(50), out var barrier) &&
              !Pro2NativeRumbleEncoder.HasEffect(barrier!.Data), "capacity pressure prefers discarding impacts over explicit stop");
        q.Reset(); q.ConfigureServiceBudget(Pro2RumblePacingPolicy.MinimumWriteIntervalMs);
        int input = 0, output = 0;
        for (int i = 0; i < 1334; i++)
        {
            q.Enqueue(Packet(100), false, Tick(i * 7.5), "dualsense-ordinary"); input++;
            if (q.TryDequeue(Tick(i * 7.5), out var item)) { q.MarkWriteResult(item!, true); output++; }
        }
        check(input == output && q.ExpiredDropped == 0 && q.AdaptiveDropped == 0,
            "133.33Hz source no longer expires behind old 8ms artificial service limit in ideal model");
        q.Reset(); q.Enqueue(Packet(100), false, Tick(0), "xinput");
        q.TryDequeue(Tick(1), out var failed);
        q.MarkWriteResult(failed!, false, Tick(2));
        Pro2QueuedRumble? safe = null;
        check(q.TryEnqueueFailureStop(failed!, Tick(2)) && q.TryDequeue(Tick(3), out safe) &&
              safe!.GeneratedKind == "transport_stop" && !Pro2NativeRumbleEncoder.HasEffect(safe.Data),
            "failed ordinary state makes one explicit fail-closed neutral, not endless old refresh");
        q.MarkWriteResult(safe!, false, Tick(4)); q.TryDequeue(Tick(5), out var retry);
        q.MarkWriteResult(retry!, false, Tick(6));
        check(q.Count == 0 && q.FailureStops == 1 && q.StopRetries == 1 && q.StopRetryFailures == 1 &&
              !q.TryEnqueueFailureStop(retry!, Tick(7)), "transport-failure neutral has only one bounded stop retry");
        q.Enqueue(Packet(100), false, Tick(8), "xinput");
        check(!q.TryEnqueueFailureStop(failed!, Tick(9)) && q.Count == 1,
            "old failed active cannot insert stop ahead of newer queued source");
    }

    private static void Metrics(Action<bool, string> check)
    {
        var m = new NativeRumbleTransportMetrics(); m.Reset(Tick(0));
        var packet = Packet(100); m.ObserveSource(packet, Tick(1), "xinput", false);
        m.ObserveWrite(Tick(1), Tick(1), Tick(2), true, false, packet);
        m.ObserveWrite(Tick(12), Tick(12), Tick(13), true, false, packet, "refresh");
        m.ObserveWrite(Tick(14), Tick(14), Tick(15), true, false, Packet(0), "shutdown_stop");
        check(m.TrySnapshot(Tick(5000), out var text) && text.Contains("source_count=1 ") &&
              text.Contains("source_write_success=1 ") && text.Contains("generated_refresh_success=1 ") &&
              text.Contains("generated_stop_success=1 ") && text.Contains("ble_api_success=3 "),
            "generated refresh and neutral do not masquerade as delivered source events");
    }

    private static void StartClockAndWake(Action<bool, string> check)
    {
        var hold = new OrdinaryRumbleHoldState(); byte[] active = Packet(100);
        hold.Observe(active, "xinput", false, true);
        var item = new Pro2QueuedRumble(active, false, Tick(0), Source: "xinput");
        hold.Written(item, true, Tick(9), writeStarted: Tick(0));
        check(Math.Abs(hold.MillisecondsUntilDue(Tick(9)) - 3) < .001 &&
              !hold.TryCreate(Tick(11), false, 0, out _) && hold.TryCreate(Tick(12), false, 0, out _),
            "ordinary refresh due is 12ms from write start, not completion plus 12ms");
        hold.Written(item, true, Tick(40), writeStarted: Tick(20));
        check(hold.MillisecondsUntilDue(Tick(40)) == 0 && hold.TryCreate(Tick(40), false, 0, out _),
            "slow GATT completion does not add another whole refresh interval");
        WakeAsync(check).GetAwaiter().GetResult();
    }

    private static async Task WakeAsync(Action<bool, string> check)
    {
        using var pacer = new NativeRumblePacer(forceFallback: true);
        using var signal = new SemaphoreSlim(0, 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 32; i++)
        {
            // Timer wins. A cancelled semaphore waiter must not consume the
            // next source signal after this method returns.
            await RumbleWriterWake.WaitAsync(signal, .5, pacer, deadline.Token);
            signal.Release();
            check(signal.CurrentCount == 1 && signal.Wait(0), "timer-won race joins losing source waiter");
            // Source wins. The native/fallback timer is cancelled and joined,
            // so the same pacer can safely service the next iteration.
            signal.Release();
            await RumbleWriterWake.WaitAsync(signal, 2000, pacer, deadline.Token);
            check(signal.CurrentCount == 0, "source-won race consumes exactly its own signal");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool observed = false;
        try { await RumbleWriterWake.WaitAsync(signal, 1, pacer, cancelled.Token); }
        catch (OperationCanceledException) { observed = true; }
        check(observed && signal.CurrentCount == 0, "cancelled wake returns no background signal consumer");
    }
}
