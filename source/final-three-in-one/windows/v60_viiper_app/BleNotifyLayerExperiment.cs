using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Y700Switch2V60Viiper;

// Source-only, same production GATT initialization and subscription. No new
// receiver architecture or production dependency. Requires a live physical pad.
internal static class BleNotifyLayerExperiment
{
    public static async Task<int> RunAsync(Pro2BleInputSource source, string directory,
        Action<string> log, CancellationToken token)
    {
        if (source.RuntimeBatteryRefreshEnabled)
            throw new InvalidOperationException("Runtime battery must be disabled for this experiment.");
        string address = source.ConnectedAddress;
        string runDirectory = Path.Combine(directory, "notify-layers-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runDirectory);
        log("[BLE_NOTIFY_LAYER] results_dir=\"" + runDirectory + "\"");
        var reports = new List<NotifyLayerPhaseReport>();
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        log("[BLE_NOTIFY_LAYER] begin physical_connections=1 virtual_output=false runtime_battery=false phases=4 phase_seconds=30");
        log("[BLE_NOTIFY_LAYER] timestamp_scope=Windows_GATT_event_not_air_capture system_timestamp_resolution_may_be_coarse=true");

        foreach (bool minimal in new[] { false, true, false, true })
        {
            string phase = (reports.Count + 1) + (minimal ? "_MINIMAL_RAW_ONLY" : "_NORMAL_HANDLER");
            RequireSameConnection();
            var probe = new BleNotifyLayerProbe(phase, minimal, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
            long pauseBefore = GC.GetTotalPauseDuration().Ticks;
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            var windows = new List<NotifyLayerWindow>();
            var sourceBefore = source.GetCompactCounters();
            using var schedulerProbe = new BleSchedulerLatencyProbe(dispatcher);
            source.BeginNotifyLayerProbe(probe);
            long previousTicks = probe.StartedTicks, previousCount = 0;
            Exception? phaseError = null;
            try
            {
                log("[BLE_NOTIFY_LAYER_PHASE] begin phase=" + phase + " minimal=" + minimal);
                for (int window = 0; window < 6; window++)
                {
                    await Task.Delay(5000, token);
                    RequireSameConnection();
                    long now = Stopwatch.GetTimestamp(), count = probe.Count;
                    double seconds = Stopwatch.GetElapsedTime(previousTicks, now).TotalSeconds;
                    var sample = new NotifyLayerWindow(seconds, (count - previousCount) / seconds,
                        ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                        (GC.GetTotalPauseDuration().Ticks - pauseBefore) / (double)TimeSpan.TicksPerMillisecond);
                    windows.Add(sample);
                    log("[BLE_NOTIFY_LAYER_WINDOW] phase=" + phase + " seconds=" + F(seconds) +
                        " fd2_callback_hz=" + F(sample.Fd2CallbackHz) + " threadpool_threads=" + sample.ThreadPoolThreads +
                        " threadpool_pending=" + sample.ThreadPoolPending + " gc_pause_total_ms=" + F(sample.GcPauseTotalMs));
                    if (count == previousCount)
                        throw new InvalidOperationException("No FD2 callbacks during a full 5-second window; stop instead of retrying/reconnecting.");
                    previousTicks = now; previousCount = count;
                }
            }
            catch (Exception ex) { phaseError = ex; }
            finally { source.EndNotifyLayerProbe(probe); }
            await schedulerProbe.StopAsync();
            var sourceAfter = source.GetCompactCounters();
            // Let callbacks already in flight finish; this is not counted in the
            // measurement interval and never initiates GATT traffic.
            await Task.Delay(100);
            var report = probe.Snapshot() with
            {
                Windows = windows.ToArray(),
                Gen0Collections = GC.CollectionCount(0) - gen0,
                Gen1Collections = GC.CollectionCount(1) - gen1,
                Gen2Collections = GC.CollectionCount(2) - gen2,
                GcPauseTotalMs = (GC.GetTotalPauseDuration().Ticks - pauseBefore) / (double)TimeSpan.TicksPerMillisecond,
                PhaseCompleted = phaseError == null && windows.Count == 6,
                Failure = phaseError?.Message ?? "",
                FullHandlerFd2Delta = !minimal && sourceAfter.Fd2 >= sourceBefore.Fd2 ? sourceAfter.Fd2 - sourceBefore.Fd2 : null,
                FullHandlerParsedDelta = !minimal && sourceAfter.Parsed >= sourceBefore.Parsed ? sourceAfter.Parsed - sourceBefore.Parsed : null,
                Scheduling = schedulerProbe.Snapshot()
            };
            reports.Add(report);
            await File.WriteAllTextAsync(Path.Combine(runDirectory, phase + ".csv"), probe.ToCsv(), new UTF8Encoding(false));
            await Save();
            if (phaseError != null)
                throw new InvalidOperationException("Layer phase aborted; partial evidence saved in " + runDirectory, phaseError);
            RequireSameConnection();
            log("[BLE_NOTIFY_LAYER_PHASE] complete phase=" + phase + " count=" + report.Fd2CallbackCount +
                " fd2_callback_hz=" + F(report.Fd2CallbackHz) + " min_window_hz=" + F(windows.Min(w => w.Fd2CallbackHz)) +
                " arrival_p95_ms=" + F(report.CallbackGaps.P95Ms) + " windows_event_p95_ms=" + F(report.SystemEventGaps.P95Ms) +
                " delivery_lag_p95_ms=" + F(report.DeliveryLag.P95Ms) + " handler_p95_ms=" + F(report.HandlerDuration.P95Ms) +
                " timestamps_complete=" + report.TimestampEvidenceComplete + " stats_truncated=" + report.StatsTruncated);
        }
        bool rawOnlyLow = reports.Where(r => r.MinimalReceive).Any(r => r.Windows.Any(w => w.Fd2CallbackHz < 120));
        bool normalLow = reports.Where(r => !r.MinimalReceive).Any(r => r.Windows.Any(w => w.Fd2CallbackHz < 120));
        string evidence = rawOnlyLow ? "low_rate_reproduced_with_parser_queue_output_bypassed" :
            normalLow ? "normal_handler_involvement_candidate_requires_repeat_confirmation" : "not_reproduced_in_this_short_run";
        log("[BLE_NOTIFY_LAYER_EVIDENCE] code=" + evidence +
            " cause_proven=false timestamp_is_not_hci_capture=true test_order_not_randomized=true");
        log("[BLE_NOTIFY_LAYER] complete summary=" + Path.Combine(runDirectory, "summary.json") +
            " cause_proven=false same_connection=true");
        return reports.All(IsRateAccepted) ? 0 : 3;

        void RequireSameConnection()
        {
            if (!source.IsRunning || !source.IsTransportConnected || source.ConnectedAddress != address)
                throw new InvalidOperationException("Physical BLE connection changed; no silent reconnect or successful result claimed.");
        }
        Task Save() => File.WriteAllTextAsync(Path.Combine(runDirectory, "summary.json"),
            JsonSerializer.Serialize(new
            {
                Address = address, WindowsVersion = Environment.OSVersion.VersionString,
                VirtualOutput = false, RuntimeBatteryQueries = false,
                ConnectionBatterySnapshotRetained = source.BatteryLevelReadStatus != "disabled",
                TimestampScope = "Windows GATT notification event, NOT HCI/air packets or direct Connection Interval",
                MinimalReceiveScope = "same subscription; FD2 UUID/length/time/count only, no parser/state/queue/output",
                SystemWrites = false, CauseProven = false, Phases = reports
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static bool IsRateAccepted(NotifyLayerPhaseReport report) =>
        report.PhaseCompleted && report.Windows.Length == 6 && report.Fd2CallbackHz >= 120 &&
        report.Windows.All(w => w.Fd2CallbackHz >= 120) && report.Fd2CallbackCount > 0 &&
        report.PayloadLength63Count >= report.Fd2CallbackCount * .9 &&
        (report.MinimalReceive || (report.FullHandlerFd2Delta > 0 &&
            report.FullHandlerParsedDelta >= report.FullHandlerFd2Delta * .9));

    // Synthetic checks only: never opens Bluetooth, changes registry or starts
    // a source/server. Guards the measurement itself against false success.
    public static int SelfTest(Action<string> log)
    {
        int checks = 0;
        void Check(bool condition, string detail)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Notify layer self-test: " + detail);
        }
        long start = Stopwatch.Frequency * 10;
        DateTimeOffset utc = DateTimeOffset.Parse("2026-10-10T00:00:00Z", CultureInfo.InvariantCulture);
        var regular = new BleNotifyLayerProbe("synthetic_133", false, start, utc);
        for (int i = 0; i < 4000; i++)
        {
            long tick = start + (long)Math.Round(i * .0075 * Stopwatch.Frequency);
            long system = utc.UtcTicks + (long)Math.Round(i * 7.5 * TimeSpan.TicksPerMillisecond);
            int slot = regular.RecordEntry(tick, system + 2 * TimeSpan.TicksPerMillisecond, system, 63);
            regular.RecordCompleted(slot, Stopwatch.Frequency / 50000.0);
        }
        regular.Stop(start + 30 * Stopwatch.Frequency);
        var result = regular.Snapshot();
        Check(result.Fd2CallbackCount == 4000 && result.CompletedHandlers == 4000, "actual counts/completions");
        Check(Math.Abs(result.Fd2CallbackHz - 133.3333333) < .001, "rate denominator");
        Check(Math.Abs(result.CallbackGaps.P50Ms - 7.5) < .001, "QPC gaps");
        Check(Math.Abs(result.SystemEventGaps.P95Ms - 7.5) < .001, "Windows event gaps");
        Check(Math.Abs(result.DeliveryLag.P95Ms - 2) < .001, "delivery lag");
        Check(Math.Abs(result.HandlerDuration.P95Ms - .02) < .001, "handler timing");
        Check(result.TimestampEvidenceComplete && result.PayloadLength63Count == 4000, "metadata coverage");
        Check(!result.StatsTruncated && result.StatsSampleCount == 4000, "bounded complete capture");
        Check(regular.RecordEntry(start + 31 * Stopwatch.Frequency, utc.UtcTicks, utc.UtcTicks, 63) == -2 && regular.Count == 4000,
            "no samples admitted after stop");
        Check(regular.ToCsv().Split('\n').Length == 4002, "one CSV evidence row per stored event");

        var missing = new BleNotifyLayerProbe("synthetic_missing", true, start, utc);
        for (int i = 0; i < 300; i++)
        {
            long tick = start + i * Stopwatch.Frequency / 10;
            missing.RecordEntry(tick, utc.UtcTicks + i * TimeSpan.TicksPerSecond / 10, 0, 63);
        }
        missing.Stop(start + 30 * Stopwatch.Frequency);
        var low = missing.Snapshot();
        Check(low.Fd2CallbackHz == 10 && !low.TimestampEvidenceComplete, "missing timestamp cannot invent 133Hz");
        Check(low.SystemEventGaps.Samples == 0 && low.DeliveryLag.Samples == 0, "no manufactured timing samples");
        Check(low.HandlerDuration.Samples == 0 && low.CompletedHandlers == 0, "incomplete callbacks exposed");

        var reordered = new BleNotifyLayerProbe("synthetic_reordered", true, start, utc);
        reordered.RecordEntry(start + Stopwatch.Frequency, utc.UtcTicks + TimeSpan.TicksPerSecond, utc.UtcTicks + TimeSpan.TicksPerSecond, 63);
        reordered.RecordEntry(start, utc.UtcTicks, utc.UtcTicks, 63);
        reordered.Stop(start + 2 * Stopwatch.Frequency);
        Check(reordered.Snapshot().CallbackGaps.P50Ms == 1000, "sort concurrent callback arrival, not negative gaps");
        var equal = new BleNotifyLayerProbe("synthetic_coarse", true, start, utc);
        equal.RecordEntry(start, utc.UtcTicks, utc.UtcTicks, 63);
        equal.RecordEntry(start + Stopwatch.Frequency / 100, utc.UtcTicks + TimeSpan.TicksPerMillisecond * 10, utc.UtcTicks, 63);
        equal.Stop(start + Stopwatch.Frequency);
        Check(equal.Snapshot().RepeatedSystemTimestampCount == 1, "coarse timestamps remain visible");
        var bounded = new BleNotifyLayerProbe("synthetic_overflow", true, start, utc);
        for (int i = 0; i < BleNotifyLayerProbe.Capacity + 5; i++)
            bounded.RecordEntry(start + i, utc.UtcTicks + i, utc.UtcTicks + i, 63);
        bounded.Stop(start + Stopwatch.Frequency);
        var overflow = bounded.Snapshot();
        Check(overflow.StatsTruncated && overflow.StatsSampleCount == BleNotifyLayerProbe.Capacity, "bounded allocation");
        Check(overflow.Fd2CallbackCount == BleNotifyLayerProbe.Capacity + 5, "overflow must not lose true event count");
        var empty = new BleNotifyLayerProbe("synthetic_empty", true, start, utc);
        empty.Stop(start + 30 * Stopwatch.Frequency);
        Check(empty.Snapshot().Fd2CallbackHz == 0 && !empty.Snapshot().TimestampEvidenceComplete,
            "no data is not success");
        var badMetadata = new BleNotifyLayerProbe("synthetic_bad_metadata", false, start, utc);
        badMetadata.RecordMetadataError();
        badMetadata.RecordEntry(start, utc.UtcTicks, utc.UtcTicks, 63);
        badMetadata.Stop(start + Stopwatch.Frequency);
        Check(!badMetadata.Snapshot().TimestampEvidenceComplete && badMetadata.Snapshot().MetadataErrors == 1,
            "failed timestamp/length access exposed");
        var validRate = result with
        {
            PhaseCompleted = true, FullHandlerFd2Delta = 4000, FullHandlerParsedDelta = 4000,
            Windows = Enumerable.Repeat(new NotifyLayerWindow(5, 133.33, 1, 0, 0), 6).ToArray()
        };
        Check(IsRateAccepted(validRate), "complete high-rate evidence accepted");
        Check(!IsRateAccepted(validRate with { FullHandlerParsedDelta = 0 }), "failed parser cannot pass high-rate acceptance");
        Check(!IsRateAccepted(validRate with { PhaseCompleted = false }), "aborted phase cannot pass high-rate acceptance");
        var threading = new BleNotifyLayerProbe("synthetic_threading", true, start, utc);
        threading.RecordEntry(start, utc.UtcTicks, utc.UtcTicks, 63, 101, ApartmentState.STA, false, true);
        threading.RecordEntry(start + 1, utc.UtcTicks + 1, utc.UtcTicks + 1, 63, 202, ApartmentState.MTA, true, false);
        threading.RecordEntry(start + 2, utc.UtcTicks + 2, utc.UtcTicks + 2, 63, 202, ApartmentState.MTA, true, false);
        threading.Stop(start + Stopwatch.Frequency);
        var threadResult = threading.Snapshot();
        Check(threadResult.CallbackThreads.Length == 2 && threadResult.CallbackThreads.Sum(t => t.Count) == 3,
            "thread attribution preserves callback count");
        Check(threadResult.CallbackThreads.Single(t => t.ManagedThreadId == 101).DispatcherContextCount == 1 &&
            threadResult.CallbackThreads.Single(t => t.ManagedThreadId == 202).ThreadPoolCount == 2,
            "STA/dispatcher and MTA/threadpool attribution are separate evidence");
        log("[BLE_NOTIFY_LAYER_SELF_TEST] PASS assertions=" + checks + " synthetic_only=true radio_opened=false");
        var trigger = new BleRateFaultTrigger();
        Check(!trigger.Observe(5, 133, 133) && trigger.HadHighRateWindow, "watch real high-rate window");
        Check(!trigger.Observe(5, 66, 66) && trigger.ConsecutiveLow == 1, "one low window is not persistent fault");
        Check(!trigger.Observe(5, 132, 132) && trigger.ConsecutiveLow == 0, "watch recovery clears low streak");
        Check(!trigger.Observe(5, 132, 60) && trigger.Observe(5, 132, 60), "parsed-only degradation also captured");
        var initialLow = new BleRateFaultTrigger();
        Check(!initialLow.Observe(5, 20, 20) && initialLow.Observe(5, 20, 20) && !initialLow.HadHighRateWindow,
            "initial low rate distinguished from a recovered high link");
        var zero = new BleRateFaultTrigger();
        Check(!zero.Observe(5, 0, 0) && zero.Observe(5, 0, 0), "zero callbacks never become success");
        bool invalidRejected = false;
        try { new BleRateFaultTrigger().Observe(5, double.NaN, 133); }
        catch (InvalidOperationException) { invalidRejected = true; }
        Check(invalidRejected, "invalid watch telemetry rejected");
        bool shortRejected = false;
        try { new BleRateFaultTrigger().Observe(1, 133, 133); }
        catch (InvalidOperationException) { shortRejected = true; }
        Check(shortRejected, "short burst not a stable-rate test");
        log("[BLE_RATE_WATCH_SELF_TEST] complete all_checks=" + checks + " bluetooth_opened=false");
        return 0;
    }
}

internal sealed record NotifyLayerWindow(double Seconds, double Fd2CallbackHz, int ThreadPoolThreads,
    long ThreadPoolPending, double GcPauseTotalMs);
internal sealed record LayerPercentiles(int Samples, double P50Ms, double P95Ms, double P99Ms, double MinMs, double MaxMs);
internal sealed record NotifyLayerPhaseReport(string Phase, bool MinimalReceive, double DurationSeconds,
    long Fd2CallbackCount, double Fd2CallbackHz, long CompletedHandlers, long PayloadLength63Count,
    int StatsSampleCount, bool StatsTruncated, int MetadataErrors, bool TimestampEvidenceComplete,
    int RepeatedSystemTimestampCount, int NegativeDeliveryLagCount, LayerPercentiles CallbackGaps,
    LayerPercentiles SystemEventGaps, LayerPercentiles DeliveryLag, LayerPercentiles HandlerDuration)
{
    public NotifyLayerWindow[] Windows { get; init; } = [];
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public double GcPauseTotalMs { get; init; }
    public bool PhaseCompleted { get; init; }
    public string Failure { get; init; } = "";
    public long? FullHandlerFd2Delta { get; init; }
    public long? FullHandlerParsedDelta { get; init; }
    public NotifyCallbackThread[] CallbackThreads { get; init; } = [];
    public BleSchedulingReport? Scheduling { get; init; }
}
internal sealed record NotifyCallbackThread(int ManagedThreadId, string Apartment, int Count, int ThreadPoolCount,
    int DispatcherContextCount);

internal sealed class BleNotifyLayerProbe
{
    internal const int Capacity = 8192;
    private readonly object gate = new();
    private readonly Entry[] entries = new Entry[Capacity];
    private long count, completed, length63, stoppedTicks;
    private int stored, metadataErrors;
    private bool stopped;
    private struct Entry
    {
        public long Qpc, CallbackUtc, SystemUtc;
        public uint Length;
        public double BodyTicks;
        public bool Completed;
        public int ThreadId;
        public ApartmentState Apartment;
        public bool IsPool, DispatcherContext;
    }
    public BleNotifyLayerProbe(string phase, bool minimalReceive, long startedTicks, DateTimeOffset startedUtc)
    { Phase = phase; MinimalReceive = minimalReceive; StartedTicks = startedTicks; StartedUtc = startedUtc; }
    public string Phase { get; }
    public bool MinimalReceive { get; }
    public long StartedTicks { get; }
    public DateTimeOffset StartedUtc { get; }
    public long Count { get { lock (gate) return count; } }
    public void RecordMetadataError() { lock (gate) metadataErrors++; }
    public int RecordEntry(long ticks, long callbackUtc, long systemUtc, uint length, int threadId = 0,
        ApartmentState apartment = ApartmentState.Unknown, bool isPool = false, bool dispatcherContext = false)
    {
        lock (gate)
        {
            if (stopped || ticks < StartedTicks) return -2;
            count++;
            if (length == 63) length63++;
            if (stored == Capacity) return -1;
            int slot = stored++;
            entries[slot] = new Entry { Qpc = ticks, CallbackUtc = callbackUtc, SystemUtc = systemUtc, Length = length,
                ThreadId = threadId, Apartment = apartment, IsPool = isPool, DispatcherContext = dispatcherContext };
            return slot;
        }
    }
    public void RecordCompleted(int slot, double bodyTicks)
    {
        if (slot == -2) return;
        lock (gate)
        {
            if (slot >= 0)
            {
                if (slot >= stored || entries[slot].Completed) return;
                entries[slot].BodyTicks = Math.Max(0, bodyTicks);
                entries[slot].Completed = true;
            }
            completed++;
        }
    }
    public void Stop(long ticks)
    {
        lock (gate) { if (!stopped) { stopped = true; stoppedTicks = ticks; } }
    }
    public NotifyLayerPhaseReport Snapshot()
    {
        Entry[] samples;
        long seen, done, payload63, end;
        int errors;
        lock (gate)
        {
            samples = entries.AsSpan(0, stored).ToArray();
            seen = count; done = completed; payload63 = length63; errors = metadataErrors;
            end = stopped ? stoppedTicks : Stopwatch.GetTimestamp();
        }
        double seconds = Math.Max(0, (end - StartedTicks) / (double)Stopwatch.Frequency);
        long[] arrivals = samples.Select(e => e.Qpc).OrderBy(t => t).ToArray();
        long[] system = samples.Where(e => e.SystemUtc >= DateTimeOffset.UnixEpoch.UtcTicks)
            .Select(e => e.SystemUtc).OrderBy(t => t).ToArray();
        double[] lags = samples.Where(e => e.SystemUtc >= DateTimeOffset.UnixEpoch.UtcTicks)
            .Select(e => (e.CallbackUtc - e.SystemUtc) / (double)TimeSpan.TicksPerMillisecond).ToArray();
        return new NotifyLayerPhaseReport(Phase, MinimalReceive, seconds, seen, seconds > 0 ? seen / seconds : 0,
            done, payload63, samples.Length, seen > Capacity, errors,
            seen > 0 && seen <= Capacity && errors == 0 && system.Length == samples.Length,
            system.Zip(system.Skip(1), (a, b) => a == b ? 1 : 0).Sum(), lags.Count(v => v < 0),
            Percentiles(Gaps(arrivals, Stopwatch.Frequency / 1000.0)),
            Percentiles(Gaps(system, TimeSpan.TicksPerMillisecond)), Percentiles(lags),
            Percentiles(samples.Where(e => e.Completed).Select(e => e.BodyTicks * 1000 / Stopwatch.Frequency).ToArray()))
        {
            CallbackThreads = samples.GroupBy(e => (e.ThreadId, e.Apartment)).Select(g =>
                new NotifyCallbackThread(g.Key.ThreadId, g.Key.Apartment.ToString(), g.Count(),
                    g.Count(e => e.IsPool), g.Count(e => e.DispatcherContext))).ToArray()
        };
    }
    private static double[] Gaps(long[] ticks, double ticksPerMs) => ticks.Zip(ticks.Skip(1),
        (a, b) => (b - a) / ticksPerMs).ToArray();
    internal static LayerPercentiles Percentiles(double[] values)
    {
        if (values.Length == 0) return new(0, 0, 0, 0, 0, 0);
        Array.Sort(values);
        double P(double q) => values[Math.Clamp((int)Math.Ceiling(values.Length * q) - 1, 0, values.Length - 1)];
        return new(values.Length, P(.5), P(.95), P(.99), values[0], values[^1]);
    }
    public string ToCsv()
    {
        Entry[] samples;
        lock (gate) samples = entries.AsSpan(0, stored).ToArray();
        var csv = new StringBuilder("callback_elapsed_ms,callback_utc_ticks,windows_event_utc_ticks,payload_length,handler_ms,handler_completed,managed_thread_id,apartment,threadpool,dispatcher_context\n");
        foreach (Entry entry in samples)
            csv.Append(((entry.Qpc - StartedTicks) * 1000.0 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.CallbackUtc).Append(',').Append(entry.SystemUtc).Append(',').Append(entry.Length).Append(',')
                .Append((entry.BodyTicks * 1000 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.Completed ? 1 : 0).Append(',').Append(entry.ThreadId).Append(',').Append(entry.Apartment).Append(',')
                .Append(entry.IsPool ? 1 : 0).Append(',').Append(entry.DispatcherContext ? 1 : 0).Append('\n');
        return csv.ToString();
    }
    internal readonly struct BodyScope : IDisposable
    {
        private readonly BleNotifyLayerProbe? probe;
        private readonly int slot;
        private readonly long started;
        public BodyScope(BleNotifyLayerProbe? probe, int slot, long started)
        { this.probe = probe; this.slot = slot; this.started = started; }
        public void Dispose() => probe?.RecordCompleted(slot, Stopwatch.GetTimestamp() - started);
    }
}

internal sealed record BleSchedulingReport(int IntervalMs, int DispatcherThreadId, LayerPercentiles TimerOvershoot,
    LayerPercentiles DispatcherQueueDelay, int CaptureErrors, bool SamplesTruncated);

// Observational 10Hz heartbeat, not a busy loop / elevated priority / timer
// resolution change. Records worker wakeup lateness and WPF queue delay.
internal sealed class BleSchedulerLatencyProbe : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dispatcher? dispatcher;
    private readonly object gate = new();
    private readonly double[] timer = new double[1024], dispatch = new double[1024];
    private int timerCount, dispatchCount, errors;
    private readonly Task worker;
    public BleSchedulerLatencyProbe(Dispatcher? dispatcher)
    {
        this.dispatcher = dispatcher;
        worker = Task.Run(ObserveAsync);
    }
    private async Task ObserveAsync()
    {
        try
        {
            while (true)
            {
                long waitStart = Stopwatch.GetTimestamp();
                await Task.Delay(100, lifetime.Token).ConfigureAwait(false);
                double overshoot = Math.Max(0, Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds - 100);
                lock (gate) { if (timerCount < timer.Length) timer[timerCount] = overshoot; timerCount++; }
                if (dispatcher == null || dispatcher.HasShutdownStarted) continue;
                long queued = Stopwatch.GetTimestamp();
                DispatcherOperation operation = dispatcher.InvokeAsync(() =>
                {
                    double delay = Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
                    lock (gate) { if (dispatchCount < dispatch.Length) dispatch[dispatchCount] = delay; dispatchCount++; }
                }, DispatcherPriority.Background, lifetime.Token);
                await operation.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { lock (gate) errors++; }
    }
    public async Task StopAsync()
    {
        await lifetime.CancelAsync();
        await worker.ConfigureAwait(false);
    }
    public BleSchedulingReport Snapshot()
    {
        lock (gate) return new(100, dispatcher?.Thread.ManagedThreadId ?? 0,
            BleNotifyLayerProbe.Percentiles(timer.AsSpan(0, Math.Min(timerCount, timer.Length)).ToArray()),
            BleNotifyLayerProbe.Percentiles(dispatch.AsSpan(0, Math.Min(dispatchCount, dispatch.Length)).ToArray()),
            errors, timerCount > timer.Length || dispatchCount > dispatch.Length);
    }
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }

    public static async Task<int> RunStandaloneAsync(string directory, Action<string> log, CancellationToken token)
    {
        using var probe = new BleSchedulerLatencyProbe(System.Windows.Application.Current?.Dispatcher);
        log("[BLE_SCHEDULER_DIAG] begin seconds=10 bluetooth_opened=false synthetic_ble_input=false system_settings_changed=false");
        try { await Task.Delay(10000, token); }
        finally { await probe.StopAsync(); }
        var report = probe.Snapshot();
        string path = Path.Combine(directory, "scheduler-only-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
        log("[BLE_SCHEDULER_DIAG] complete worker_delay_p95_ms=" + report.TimerOvershoot.P95Ms.ToString("F2", CultureInfo.InvariantCulture) +
            " worker_delay_max_ms=" + report.TimerOvershoot.MaxMs.ToString("F2", CultureInfo.InvariantCulture) +
            " dispatcher_delay_p95_ms=" + report.DispatcherQueueDelay.P95Ms.ToString("F2", CultureInfo.InvariantCulture) +
            " dispatcher_delay_max_ms=" + report.DispatcherQueueDelay.MaxMs.ToString("F2", CultureInfo.InvariantCulture) +
            " errors=" + report.CaptureErrors + " json=" + path + " real_ble_cause_proven=false");
        return report.TimerOvershoot.Samples > 0 && report.DispatcherQueueDelay.Samples > 0 && report.CaptureErrors == 0 ? 0 : 5;
    }
}
