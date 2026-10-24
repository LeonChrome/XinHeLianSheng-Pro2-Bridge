using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Y700Switch2V60Viiper;

public sealed record Pro2QueuedRumble(byte[] Data, bool Native, long EnqueuedTicks, bool SafetyStop = false,
    int StopRetryAttempt = 0, string Source = "unknown", string GeneratedKind = "source", long StateRevision = 0);

/// <summary>
/// Full original packets when service keeps up. Under measured overload, select
/// whole known-format events; never synthesize carrier frequencies/subframe timing.
/// This is lossy event selection, NOT PCM resampling or a playback guarantee.
/// </summary>
public sealed class Pro2RumbleOutputQueue
{
    public const int Capacity = 32;
    public const double MaxAgeMs = 30;
    private readonly object gate = new();
    private readonly List<Pro2QueuedRumble> pending = new();
    private readonly Queue<double> sourceGaps = new();
    private double gapSum, serviceBudgetMs = 8;
    private long lastSourceTicks;
    private bool? nativeMode;
    private string lastSource = "unknown";
    private string sourceFamily = "unknown";
    private byte[] lastWritten = Pro2NativeRumbleEncoder.Neutralize(ReadOnlySpan<byte>.Empty);
    private string policy = "passthrough";
    public long OverflowDropped { get; private set; }
    public long ExpiredDropped { get; private set; }
    public long Replaced { get; private set; }
    public long ModeDiscarded { get; private set; }
    public long ExpiryStops { get; private set; }
    public long SafetyStops { get; private set; }
    public long SafetyDiscarded { get; private set; }
    public long AdaptiveDropped { get; private set; }
    public long DeadlineEdgeDropped { get; private set; }
    public long OpaqueDropped { get; private set; }
    public long ProtectedEdges { get; private set; }
    public long StopProtected { get; private set; }
    public long GainDiscarded { get; private set; }
    public long GainStops { get; private set; }
    public long WriteFailures { get; private set; }
    public long StopRetries { get; private set; }
    public long StopRetryFailures { get; private set; }
    public long StopRetrySuperseded { get; private set; }
    public long DeadlinePeakDropped { get; private set; }
    public long DeadlineFrequencyDropped { get; private set; }
    public long DeadlineStopDropped { get; private set; }
    public long DeadlineStopsDelivered { get; private set; }
    public long FailureStops { get; private set; }
    public int MaximumDepth { get; private set; }
    public double MaximumAgeMs { get; private set; }
    public int Count { get { lock (gate) return pending.Count; } }
    public bool LastWrittenActive { get { lock (gate) return !Known(lastWritten) || Activity(lastWritten) != 0; } }
    public byte[] LastWrittenNeutral { get { lock (gate) return Pro2NativeRumbleEncoder.Neutralize(lastWritten); } }
    public bool HasNativePending { get { lock (gate) return pending.Count > 0 && pending[0].Native; } }
    public string Telemetry
    {
        get
        {
            lock (gate) return FormattableString.Invariant(
                $"rumble_policy={policy} rumble_lossy={(AdaptiveDropped + OverflowDropped + ExpiredDropped + SafetyDiscarded + GainDiscarded + ModeDiscarded + Replaced > 0 ? "true" : "false")} source_ms={(sourceGaps.Count > 0 ? gapSum / sourceGaps.Count : 0):F2} service_ms={serviceBudgetMs:F2}") +
                $" rumble_depth={pending.Count} rumble_max_depth={MaximumDepth}" +
                $" rumble_overflow={OverflowDropped} rumble_expired={ExpiredDropped} rumble_adaptive_drop={AdaptiveDropped}" +
                $" rumble_deadline_edge_drop={DeadlineEdgeDropped} rumble_opaque_drop={OpaqueDropped}" +
                $" rumble_edge_protection_checks={ProtectedEdges} rumble_stop_protection_checks={StopProtected}" +
                $" rumble_deadline_peak_drop={DeadlinePeakDropped} rumble_deadline_frequency_drop={DeadlineFrequencyDropped} rumble_deadline_stop_drop={DeadlineStopDropped}" +
                $" rumble_deadline_stop_delivered={DeadlineStopsDelivered}" +
                $" rumble_transport_failure_stops={FailureStops}" +
                $" rumble_mode_discard={ModeDiscarded} rumble_expiry_stops={ExpiryStops}" +
                $" rumble_safety_stops={SafetyStops} rumble_safety_discard={SafetyDiscarded}" +
                $" rumble_gain_discard={GainDiscarded} rumble_gain_stops={GainStops} rumble_write_failures={WriteFailures}" +
                $" rumble_stop_retries={StopRetries} rumble_stop_retry_failures={StopRetryFailures} rumble_stop_retry_superseded={StopRetrySuperseded}" +
                " rumble_max_wait_ms=" + MaximumAgeMs.ToString("F2", CultureInfo.InvariantCulture) +
                " native_queue_deadline_ms=30 converted_queue_deadline_ms=30 queue_scope=all_modes loss_scope=connection_local_queue protection_checks_are_not_deliveries=true sequence_gaps_possible=true";
        }
    }

    public void ConfigureServiceBudget(double milliseconds)
    {
        lock (gate)
            serviceBudgetMs = double.IsFinite(milliseconds) ? Math.Clamp(milliseconds, 7.5, 30) : 8;
    }

    public bool TryEnqueueNative(ReadOnlySpan<byte> report, double gain, long now,
        out byte[] packet, out string error, string source = "ns2pro-hd")
    {
        packet = new byte[Pro2NativeRumbleEncoder.PacketSize];
        bool encoded = Pro2NativeRumbleEncoder.TryEncode(report, packet, gain, out _, out error);
        lock (gate)
        {
            if (!encoded)
            {
                SafetyDiscarded += pending.Count;
                pending.Clear(); SafetyStops++;
                packet = Pro2NativeRumbleEncoder.Neutralize(report);
                error += "; safety_stop_queued=true";
            }
            Enqueue(packet, true, now, source);
            if (!encoded) pending[^1] = pending[^1] with { SafetyStop = true, GeneratedKind = "encoding_stop" };
        }
        return encoded;
    }

    public void Enqueue(ReadOnlySpan<byte> data, bool native, long now, string source = "unknown", bool safetyStop = false)
    {
        lock (gate)
        {
            if (safetyStop)
            {
                SafetyDiscarded += pending.Count;
                pending.Clear();
                SafetyStops++;
            }
            string nextFamily = Family(source);
            if (nativeMode.HasValue && (nativeMode != native || sourceFamily != nextFamily))
            {
                ModeDiscarded += pending.Count;
                pending.Clear(); sourceGaps.Clear(); gapSum = 0; lastSourceTicks = 0;
            }
            nativeMode = native;
            lastSource = source;
            sourceFamily = nextFamily;
            // A fresh source event supersedes a generated retry. Never insert an
            // old stop in front of a newly arrived impact merely because its
            // preceding stop failed once.
            for (int i = pending.Count - 1; i >= 0; i--)
                if (pending[i].StopRetryAttempt > 0) { pending.RemoveAt(i); StopRetrySuperseded++; }
            // Converted packets now have the same final parameter format as
            // native packets. Provenance is NOT permission to discard an event.
            // Coalesce only after sustained source/service overload is measured.
            {
                double gap = lastSourceTicks == 0 ? 0 : (now - lastSourceTicks) * 1000.0 / Stopwatch.Frequency;
                if (gap > 250) { sourceGaps.Clear(); gapSum = 0; }
                else if (lastSourceTicks != 0 && gap >= 0)
                {
                    sourceGaps.Enqueue(gap); gapSum += gap;
                    if (sourceGaps.Count > 32) gapSum -= sourceGaps.Dequeue();
                }
                lastSourceTicks = now;
            }
            // Prune on the consumer, not here: a new active command must not
            // silently erase an older explicit stop merely because it is old.
            if (pending.Count == Capacity)
            {
                // Prefer dropping an old impact to losing an explicit stop.
                // A pathological all-stop flood still remains capacity-bound.
                int expendable = pending.FindIndex(x => !x.SafetyStop &&
                    !(Known(x.Data) && Activity(x.Data) == 0));
                DropAt(expendable >= 0 ? expendable : 0, expired: false);
            }
            pending.Add(new(data.ToArray(), native, now, safetyStop, Source: source,
                GeneratedKind: safetyStop ? "encoding_stop" : "source"));
            MaximumDepth = Math.Max(MaximumDepth, pending.Count);
        }
    }

    public bool TryDequeue(long now, out Pro2QueuedRumble? item)
    {
        lock (gate)
        {
            while (pending.Count > 0 && Expired(pending[0], now))
            {
                var expired = pending[0];
                MaximumAgeMs = Math.Max(MaximumAgeMs, AgeMs(expired, now));
                if (expired.SafetyStop || (Known(expired.Data) && Activity(expired.Data) == 0))
                {
                    // A late neutral is safe; a late impact is not. Preserve
                    // the original stop as a barrier before a fresher start.
                    pending.RemoveAt(0); DeadlineStopsDelivered++;
                    item = expired; policy = "deadline_explicit_stop";
                    return true;
                }
                DropAt(0, expired: true);
                if (pending.Count == 0)
                {
                    item = new(Pro2NativeRumbleEncoder.Neutralize(expired.Data), expired.Native, now, true,
                        expired.StopRetryAttempt, expired.Source, GeneratedKind: "deadline_stop");
                    ExpiryStops++; policy = "deadline_stop";
                    return true;
                }
            }
            if (pending.Count == 0) { item = null; return false; }
            policy = "passthrough";
            // Sustained rate evidence, not FD2 Hz. A temporary burst alone is not
            // permission to coalesce a stream whose service can keep up.
            bool overloaded = sourceGaps.Count >= 8 &&
                gapSum / sourceGaps.Count < serviceBudgetMs * 0.9 &&
                AgeMs(pending[0], now) >= serviceBudgetMs;
            if (overloaded && pending.Count > 1)
            {
                policy = "event_selective_lossy";
                while (pending.Count > 1)
                {
                    int selected = -1, bestScore = int.MaxValue;
                    for (int i = 0; i < pending.Count - 1; i++)
                    {
                        var current = pending[i];
                        byte[] before = i == 0 ? lastWritten : pending[i - 1].Data;
                        byte[] after = pending[i + 1].Data;
                        if (current.SafetyStop || !Known(current.Data) || !Known(before) || !Known(after)) continue;
                        Protection protection = ClassifyProtection(current.Data, before, after);
                        if (protection != Protection.None)
                        {
                            ProtectedEdges++;
                            if ((protection & Protection.Stop) != 0) StopProtected++;
                            continue;
                        }
                        int score = 0;
                        for (int band = 0; band < 12; band++)
                        {
                            int amplitude = Amplitude(current.Data, band), prior = Amplitude(before, band), next = Amplitude(after, band);
                            score += Math.Abs(amplitude - prior) + Math.Abs(amplitude - next);
                        }
                        if (score < bestScore) { bestScore = score; selected = i; }
                    }
                    if (selected < 0) { policy = "edge_or_opaque_protected"; break; }
                    pending.RemoveAt(selected); AdaptiveDropped++;
                }
            }
            item = pending[0]; pending.RemoveAt(0);
            MaximumAgeMs = Math.Max(MaximumAgeMs, AgeMs(item, now));
            return true;
        }
    }

    // State is acknowledged only after the GATT call succeeds, never on dequeue.
    public void MarkWriteResult(Pro2QueuedRumble item, bool success, long? completedTicks = null)
    {
        lock (gate)
        {
            if (success) { lastWritten = item.Data.ToArray(); return; }
            WriteFailures++;
            if (!(item.SafetyStop || (Known(item.Data) && Activity(item.Data) == 0))) return;
            if (item.StopRetryAttempt > 0) { StopRetryFailures++; return; }
            if (pending.Count > 0 || nativeMode != item.Native || sourceFamily != Family(item.Source))
            { StopRetrySuperseded++; return; }
            // One final neutral retry only, never a replayed failed impact and
            // never an unbounded retry chain. Writer re-arms because Count > 0.
            pending.Add(new(Pro2NativeRumbleEncoder.Neutralize(item.Data), item.Native,
                completedTicks ?? Stopwatch.GetTimestamp(), true, 1, item.Source, GeneratedKind: "stop_retry"));
            StopRetries++;
        }
    }

    public bool StopForGainChange(long now)
    {
        lock (gate)
        {
            if (!nativeMode.HasValue) return false;
            GainDiscarded += pending.Count; pending.Clear(); GainStops++;
            pending.Add(new(Pro2NativeRumbleEncoder.Neutralize(lastWritten), nativeMode.Value, now, true,
                Source: lastSource, GeneratedKind: "gain_stop"));
            return true;
        }
    }

    public bool TryEnqueueFailureStop(Pro2QueuedRumble failed, long now)
    {
        lock (gate)
        {
            // Only an ordinary active state can have our generated keepalive.
            // Do not overwrite a newer source event, refresh HD/native payloads,
            // or start another stop loop after a failed stop/retry.
            if (failed.Native || !OrdinaryRumbleHoldState.IsOrdinarySource(failed.Source) ||
                !Known(failed.Data) || Activity(failed.Data) == 0 || pending.Count != 0 ||
                sourceFamily != Family(failed.Source)) return false;
            pending.Add(new(Pro2NativeRumbleEncoder.Neutralize(failed.Data), false, now, true,
                Source: failed.Source, GeneratedKind: "transport_stop"));
            FailureStops++;
            return true;
        }
    }

    private void DropAt(int index, bool expired)
    {
        var current = pending[index];
        // Both native and converted known-format commands carry salience.
        {
            if (!Known(current.Data)) OpaqueDropped++;
            else
            {
                byte[] before = index == 0 ? lastWritten : pending[index - 1].Data;
                byte[]? after = index + 1 < pending.Count ? pending[index + 1].Data : null;
                Protection protection = ClassifyProtection(current.Data, before, after);
                if (protection != Protection.None) DeadlineEdgeDropped++;
                if ((protection & Protection.Peak) != 0) DeadlinePeakDropped++;
                if ((protection & Protection.Frequency) != 0) DeadlineFrequencyDropped++;
                if ((protection & Protection.Stop) != 0) DeadlineStopDropped++;
            }
        }
        pending.RemoveAt(index);
        if (expired) ExpiredDropped++; else OverflowDropped++;
    }
    private static bool Known(byte[] data) => data.Length >= 33 && (data[1] & 0xf0) == 0x50 && (data[17] & 0xf0) == 0x50;
    [Flags]
    private enum Protection { None = 0, Edge = 1, Stop = 2, Frequency = 4, Peak = 8, Opaque = 16 }
    private static Protection ClassifyProtection(byte[] current, byte[] before, byte[]? after)
    {
        if (!Known(current) || !Known(before) || (after != null && !Known(after))) return Protection.Opaque;
        int state = Activity(current), previousState = Activity(before), nextState = after == null ? state : Activity(after);
        Protection result = Protection.None;
        if (state != previousState || state != nextState) result |= Protection.Edge;
        if ((previousState & ~state) != 0 || (state & ~nextState) != 0) result |= Protection.Stop;
        if (!SameFrequencies(before, current) || (after != null && !SameFrequencies(current, after)))
            result |= Protection.Frequency;
        if (after != null)
            for (int band = 0; band < 12; band++)
                // Packed amplitude-code salience, not physical energy/RMS.
                if (Amplitude(current, band) >= Math.Max(Amplitude(before, band), Amplitude(after, band)) + 4)
                { result |= Protection.Peak; break; }
        return result;
    }
    private static int Activity(byte[] data)
    {
        int result = 0;
        for (int band = 0; band < 12; band++) if (Amplitude(data, band) != 0) result |= band < 6 ? 1 : 2;
        return result;
    }
    private static ulong Frame(byte[] data, int index)
    {
        int offset = 2 + index / 3 * 16 + index % 3 * 5;
        ulong bits = 0;
        for (int i = 0; i < 5; i++) bits |= (ulong)data[offset + i] << (8 * i);
        return bits;
    }
    private static int Amplitude(byte[] data, int band) => (int)((Frame(data, band / 2) >> (band % 2 == 0 ? 10 : 30)) & 1023);
    private static bool SameFrequencies(byte[] a, byte[] b)
    {
        const ulong frequencyMask = 1023UL | (1023UL << 20);
        for (int frame = 0; frame < 6; frame++) if ((Frame(a, frame) & frequencyMask) != (Frame(b, frame) & frequencyMask)) return false;
        return true;
    }
    public void Reset()
    {
        lock (gate)
        {
            pending.Clear(); nativeMode = null; lastSource = sourceFamily = "unknown"; sourceGaps.Clear(); gapSum = 0; lastSourceTicks = 0;
            lastWritten = Pro2NativeRumbleEncoder.Neutralize(ReadOnlySpan<byte>.Empty); serviceBudgetMs = 8; policy = "passthrough";
            OverflowDropped = ExpiredDropped = Replaced = ModeDiscarded = ExpiryStops = 0;
            SafetyStops = SafetyDiscarded = AdaptiveDropped = DeadlineEdgeDropped = OpaqueDropped = 0;
            ProtectedEdges = StopProtected = GainDiscarded = GainStops = WriteFailures = 0;
            StopRetries = StopRetryFailures = StopRetrySuperseded = 0;
            DeadlinePeakDropped = DeadlineFrequencyDropped = DeadlineStopDropped = 0;
            DeadlineStopsDelivered = 0;
            FailureStops = 0;
            MaximumDepth = 0; MaximumAgeMs = 0;
        }
    }
    private static bool Expired(Pro2QueuedRumble item, long now) => AgeMs(item, now) > MaxAgeMs;
    private static string Family(string source) =>
        source.StartsWith("ns2pro", StringComparison.Ordinal) ? "pro2" :
        source.StartsWith("switch1", StringComparison.Ordinal) ? "pro1" :
        source.StartsWith("xinput", StringComparison.Ordinal) ? "xbox" :
        source.StartsWith("dualsense-hd", StringComparison.Ordinal) ? "ps5_pcm" :
        source.StartsWith("dualsense", StringComparison.Ordinal) ? "ps5_ordinary" : source;
    private static double AgeMs(Pro2QueuedRumble item, long now) => Math.Max(0, (now - item.EnqueuedTicks) * 1000.0 / Stopwatch.Frequency);
}
