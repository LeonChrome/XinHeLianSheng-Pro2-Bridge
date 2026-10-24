using System;
using System.Diagnostics;
using System.Globalization;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Bounded, observation-only all-mode rumble telemetry. Source timestamps are the
/// manager's feedback receive clock, not the game's original USB submission
/// clock. Successful writes mean BLE API completion, not air delivery/playback.
/// Gaps and duplicate comparisons are within each reported window only.
/// </summary>
public sealed class NativeRumbleTransportMetrics
{
    public const int SampleCapacity = 2048;
    public const int WindowMilliseconds = 5000;
    private readonly object gate = new();
    private readonly Samples sourceGaps = new();
    private readonly Samples writeDurations = new();
    private readonly Samples writeGaps = new();
    private readonly Samples queueWaits = new();
    private readonly byte[] previousPayload = new byte[32];
    private long windowStart = -1, previousSource = -1, previousWrite = -1;
    private long sources, writes, failures, known, opaque, invalid, duplicates, comparisons, clockErrors;
    private long nativeSources, convertedSources, nativeWrites, convertedWrites, stopWrites, maxStopWait;
    private long frequencyClamps;
    private long sourceWriteSuccess, generatedRefreshSuccess, generatedStopSuccess, generatedWriteFailures;
    private int sourceKinds, sourceLeftPeak, sourceRightPeak, writeLeftPeak, writeRightPeak;
    private bool hasPreviousPayload, previousKnown;

    public void ObserveSource(ReadOnlySpan<byte> report, long ticks, string source = "ns2pro-hd", bool native = true,
        int clampedFrequencies = 0)
    {
        lock (gate)
        {
            EnsureStart(ticks);
            sources++;
            if (native) nativeSources++; else convertedSources++;
            sourceKinds |= Kind(source);
            frequencyClamps += Math.Max(0, clampedFrequencies);
            if (previousSource >= 0)
            {
                if (ticks >= previousSource) sourceGaps.Add(ticks - previousSource);
                else clockErrors++;
            }
            previousSource = ticks;
            if (report.Length < 33)
            {
                invalid++;
                hasPreviousPayload = false;
                return;
            }
            bool isKnown = (report[1] & 0xf0) == 0x50 && (report[17] & 0xf0) == 0x50;
            if (isKnown) known++; else opaque++;
            if (isKnown)
            {
                sourceLeftPeak = Math.Max(sourceLeftPeak, Peak(report, 0));
                sourceRightPeak = Math.Max(sourceRightPeak, Peak(report, 1));
            }
            bool same = hasPreviousPayload && isKnown == previousKnown;
            if (hasPreviousPayload) comparisons++;
            for (int i = 0; i < 32; i++)
            {
                byte value = report[i + 1];
                // Only the known format's two header sequence nibbles may be
                // excluded. Opaque formats must be compared without guessing.
                if (isKnown && (i == 0 || i == 16)) value &= 0xf0;
                same &= previousPayload[i] == value;
                previousPayload[i] = value;
            }
            if (same) duplicates++;
            hasPreviousPayload = true;
            previousKnown = isKnown;
        }
    }

    public void ObserveWrite(long enqueuedTicks, long started, long completed, bool success,
        bool native = true, ReadOnlySpan<byte> packet = default, string generatedKind = "source")
    {
        lock (gate)
        {
            EnsureStart(completed);
            if (success)
            {
                writes++;
                if (generatedKind == "source") sourceWriteSuccess++;
                else if (generatedKind == "refresh") generatedRefreshSuccess++;
                else generatedStopSuccess++;
                if (native) nativeWrites++; else convertedWrites++;
                if (packet.Length >= 33 && (packet[1] & 0xf0) == 0x50 && (packet[17] & 0xf0) == 0x50)
                {
                    int left = Peak(packet, 0), right = Peak(packet, 1);
                    writeLeftPeak = Math.Max(writeLeftPeak, left);
                    writeRightPeak = Math.Max(writeRightPeak, right);
                    if (left == 0 && right == 0)
                    {
                        stopWrites++;
                        if (enqueuedTicks >= 0 && completed >= enqueuedTicks)
                            maxStopWait = Math.Max(maxStopWait, completed - enqueuedTicks);
                    }
                }
            }
            else { failures++; if (generatedKind != "source") generatedWriteFailures++; }
            if (previousWrite >= 0 && started >= previousWrite) writeGaps.Add(started - previousWrite);
            previousWrite = started;
            if (enqueuedTicks < 0 || started < enqueuedTicks || completed < started)
            {
                clockErrors++;
                return;
            }
            queueWaits.Add(started - enqueuedTicks);
            if (success) writeDurations.Add(completed - started);
        }
    }

    /// <summary>
    /// Call periodically; a window is emitted only after at least five seconds.
    /// Idle windows are explicit zero rates, not a retained previous rate.
    /// Snapshot allocation/sorting is never performed on ObserveSource/Write.
    /// </summary>
    public bool TrySnapshot(long now, out string text, bool force = false)
    {
        Window sample;
        lock (gate)
        {
            EnsureStart(now);
            if (now <= windowStart || (!force && now - windowStart < Stopwatch.Frequency * 5L))
            {
                text = "";
                return false;
            }
            sample = new Window((now - windowStart) / (double)Stopwatch.Frequency,
                sources, writes, failures, known, opaque, invalid, duplicates, comparisons,
                clockErrors, sourceGaps.Capture(), writeDurations.Capture(), queueWaits.Capture(), writeGaps.Capture(),
                new ModeStats(nativeSources, convertedSources, nativeWrites, convertedWrites, stopWrites,
                    maxStopWait, sourceKinds, sourceLeftPeak, sourceRightPeak, writeLeftPeak, writeRightPeak,
                    frequencyClamps, sourceWriteSuccess, generatedRefreshSuccess, generatedStopSuccess, generatedWriteFailures));
            ResetWindow(now);
        }
        // Keep sorting and formatting outside the producer's lock. At most
        // four fixed-size arrays are copied every five seconds.
        Array.Sort(sample.Gaps.Values);
        Array.Sort(sample.Durations.Values);
        Array.Sort(sample.Waits.Values);
        Array.Sort(sample.WriteGaps.Values);
        text = string.Create(CultureInfo.InvariantCulture,
            $"window_s={sample.Seconds:F3} source_hz={sample.Sources / sample.Seconds:F2} " +
            $"source_count={sample.Sources} source_clock=manager_feedback_receive " +
            $"source_gap_p50_ms={Percentile(sample.Gaps.Values, .50):F3} " +
            $"source_gap_p95_ms={Percentile(sample.Gaps.Values, .95):F3} " +
            $"source_gap_p99_ms={Percentile(sample.Gaps.Values, .99):F3} " +
            $"source_gap_samples={sample.Gaps.Total} source_gap_retained={sample.Gaps.Values.Length} " +
            $"ble_api_success_hz={sample.Writes / sample.Seconds:F2} ble_api_success={sample.Writes} " +
            $"ble_api_failure={sample.Failures} write_p95_ms={Percentile(sample.Durations.Values, .95):F3} " +
            $"write_start_gap_p50_ms={Percentile(sample.WriteGaps.Values, .50):F3} " +
            $"write_start_gap_p95_ms={Percentile(sample.WriteGaps.Values, .95):F3} " +
            $"write_start_gap_p99_ms={Percentile(sample.WriteGaps.Values, .99):F3} " +
            $"queue_wait_p95_ms={Percentile(sample.Waits.Values, .95):F3} " +
            $"queue_wait_max_ms={Milliseconds(sample.Waits.Max):F3} " +
            $"duplicate_ratio={(sample.Comparisons == 0 ? 0 : sample.Duplicates / (double)sample.Comparisons):F4} " +
            $"duplicate_count={sample.Duplicates} duplicate_comparisons={sample.Comparisons} " +
            $"format_known5n={sample.Known} format_opaque={sample.Opaque} format_invalid={sample.Invalid} " +
            $"sample_capacity={SampleCapacity} samples_overwritten={sample.Gaps.Overwritten + sample.Durations.Overwritten + sample.Waits.Overwritten + sample.WriteGaps.Overwritten} " +
            $"clock_errors={sample.ClockErrors} duplicate_scope=adjacent_32B_ignore_seq_for_5n_only " +
            $"mode_scope=all_modes source_kinds={Kinds(sample.Modes.Kinds)} native_source_count={sample.Modes.NativeSources} " +
            $"converted_source_count={sample.Modes.ConvertedSources} native_write_success={sample.Modes.NativeWrites} converted_write_success={sample.Modes.ConvertedWrites} " +
            $"source_write_success={sample.Modes.SourceWrites} generated_refresh_success={sample.Modes.RefreshWrites} generated_stop_success={sample.Modes.GeneratedStops} generated_write_failure={sample.Modes.GeneratedFailures} source_delivery_count_excludes_generated=true " +
            $"source_left_peak_code={sample.Modes.SourceLeft} source_right_peak_code={sample.Modes.SourceRight} " +
            $"write_left_peak_code={sample.Modes.WriteLeft} write_right_peak_code={sample.Modes.WriteRight} amplitude_scope=packed_code_not_force " +
            $"stop_write_success={sample.Modes.StopWrites} stop_source_to_api_max_ms={Milliseconds(sample.Modes.MaxStopWait):F3} " +
            $"converted_frequency_clamps={sample.Modes.FrequencyClamps} converted_frequency_mapping={Pro2RumbleFrequencyPolicy.Mapping} " +
            $"gap_scope=within_window write_gap_scope=all_attempts_including_idle queue_wait_scope=all_write_attempts " +
            $"delivery_scope=BLE_API_completion_not_air_or_playback idle={sample.Sources == 0 && sample.Writes == 0 && sample.Failures == 0}");
        return true;
    }

    public void Reset() => Reset(Stopwatch.GetTimestamp());
    public void Reset(long now)
    {
        lock (gate) ResetWindow(now);
    }

    private void EnsureStart(long now)
    {
        if (windowStart < 0) windowStart = now;
    }

    private void ResetWindow(long now)
    {
        windowStart = now;
        previousSource = -1;
        previousWrite = -1;
        hasPreviousPayload = false;
        previousKnown = false;
        sources = writes = failures = known = opaque = invalid = duplicates = comparisons = clockErrors = 0;
        nativeSources = convertedSources = nativeWrites = convertedWrites = stopWrites = maxStopWait = 0;
        frequencyClamps = 0;
        sourceWriteSuccess = generatedRefreshSuccess = generatedStopSuccess = generatedWriteFailures = 0;
        sourceKinds = sourceLeftPeak = sourceRightPeak = writeLeftPeak = writeRightPeak = 0;
        sourceGaps.Reset(); writeDurations.Reset(); queueWaits.Reset(); writeGaps.Reset();
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static int Kind(string source) =>
        source.StartsWith("ns2pro", StringComparison.Ordinal) ? 1 :
        source.StartsWith("switch1", StringComparison.Ordinal) ? 2 :
        source.StartsWith("xinput", StringComparison.Ordinal) ? 4 :
        source.StartsWith("dualsense-hd", StringComparison.Ordinal) ? 16 :
        source.StartsWith("dualsense", StringComparison.Ordinal) ? 8 : 32;
    private static string Kinds(int bits)
    {
        if (bits == 0) return "none";
        string value = "";
        string[] names = { "pro2_native", "pro1_converted", "xbox_ordinary", "ps5_ordinary", "ps5_pcm", "other" };
        for (int i = 0; i < names.Length; i++)
            if ((bits & (1 << i)) != 0) value += (value.Length == 0 ? "" : ",") + names[i];
        return value;
    }
    private static int Peak(ReadOnlySpan<byte> packet, int side)
    {
        int peak = 0;
        for (int frame = 0; frame < 3; frame++)
        {
            int offset = 2 + side * 16 + frame * 5;
            ulong bits = 0;
            for (int i = 0; i < 5; i++) bits |= (ulong)packet[offset + i] << (8 * i);
            peak = Math.Max(peak, Math.Max((int)((bits >> 10) & 1023), (int)((bits >> 30) & 1023)));
        }
        return peak;
    }
    private static double Percentile(long[] sorted, double p) => sorted.Length == 0 ? 0 :
        Milliseconds(sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)]);

    private sealed class Samples
    {
        private readonly long[] values = new long[SampleCapacity];
        private int next, count;
        private long total, max;
        public void Add(long value)
        {
            values[next] = value;
            next = (next + 1) % SampleCapacity;
            count = Math.Min(SampleCapacity, count + 1);
            total++;
            max = Math.Max(max, value);
        }
        public SampleSet Capture()
        {
            var copy = new long[count];
            Array.Copy(values, copy, count);
            return new SampleSet(copy, total, max, total - count);
        }
        public void Reset() { next = count = 0; total = max = 0; }
    }

    private readonly record struct SampleSet(long[] Values, long Total, long Max, long Overwritten);
    private readonly record struct Window(double Seconds, long Sources, long Writes, long Failures,
        long Known, long Opaque, long Invalid, long Duplicates, long Comparisons, long ClockErrors,
        SampleSet Gaps, SampleSet Durations, SampleSet Waits, SampleSet WriteGaps, ModeStats Modes);
    private readonly record struct ModeStats(long NativeSources, long ConvertedSources,
        long NativeWrites, long ConvertedWrites, long StopWrites, long MaxStopWait, int Kinds,
        int SourceLeft, int SourceRight, int WriteLeft, int WriteRight, long FrequencyClamps,
        long SourceWrites, long RefreshWrites, long GeneratedStops, long GeneratedFailures);
}
