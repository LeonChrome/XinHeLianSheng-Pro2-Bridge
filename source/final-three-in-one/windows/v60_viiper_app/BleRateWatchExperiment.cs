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

namespace Y700Switch2V60Viiper;

// Internal, same physical connection and production parser. No virtual output,
// battery operation, parameter rewrite, reconnect or guessed input frames.
internal static class BleRateWatchExperiment
{
    public static async Task<int> RunAsync(Pro2BleInputSource source, int seconds,
        string directory, Action<string> log, CancellationToken token)
    {
        if (source.RuntimeBatteryRefreshEnabled || source.BatteryLevelReadStatus != "disabled")
            throw new InvalidOperationException("Long rate watch requires ALL primary battery operations disabled.");
        string address = source.ConnectedAddress;
        string runDirectory = Path.Combine(directory, "rate-watch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runDirectory);
        log("[BLE_RATE_WATCH] results_dir=\"" + runDirectory + "\"");
        var trigger = new BleRateFaultTrigger();
        var windows = new List<BleRateWatchWindow>();
        var recent = new Queue<(NotifyLayerPhaseReport Report, string Csv)>();
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        int chunk = 0;
        bool fault = false;
        log("[BLE_RATE_WATCH] begin watch_seconds=" + seconds +
            " threshold_hz=128 consecutive_low_windows=2 virtual_output=false initial_battery=false runtime_battery=false" +
            " system_writes=false same_connection=true no_automatic_reconnect=true");
        try
        {
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds && !fault)
            {
                RequireSameConnection();
                var probe = new BleNotifyLayerProbe("WATCH_" + (++chunk).ToString("D3"), false,
                    Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
                using var scheduling = new BleSchedulerLatencyProbe(System.Windows.Application.Current?.Dispatcher);
                long previousTicks = probe.StartedTicks, previousCount = 0;
                var counters = source.GetCompactCounters();
                uint previousParsed = counters.Parsed;
                var chunkWindows = new List<NotifyLayerWindow>();
                Exception? failure = null;
                source.BeginNotifyLayerProbe(probe);
                try
                {
                    for (int i = 0; i < 6; i++)
                    {
                        await Task.Delay(5000, token);
                        RequireSameConnection();
                        long now = Stopwatch.GetTimestamp(), count = probe.Count;
                        counters = source.GetCompactCounters();
                        double duration = Stopwatch.GetElapsedTime(previousTicks, now).TotalSeconds;
                        long windowCount = count - previousCount;
                        if (windowCount < 0 || counters.Parsed < previousParsed)
                            throw new InvalidOperationException("Source counters reset; this is not a same-session rate result.");
                        double rawHz = windowCount / duration;
                        double parsedHz = (counters.Parsed - previousParsed) / duration;
                        var sample = new BleRateWatchWindow(Stopwatch.GetElapsedTime(started, now).TotalSeconds,
                            duration, rawHz, parsedHz, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                            GC.GetTotalPauseDuration().TotalMilliseconds);
                        windows.Add(sample);
                        chunkWindows.Add(new(duration, rawHz, sample.ThreadPoolThreads, sample.ThreadPoolPending, sample.GcPauseTotalMs));
                        fault = trigger.Observe(duration, rawHz, parsedHz);
                        log("[BLE_RATE_WATCH_WINDOW] elapsed_s=" + F(sample.ElapsedSeconds) +
                            " seconds=" + F(duration) + " fd2_callback_hz=" + F(rawHz) + " parsed_hz=" + F(parsedHz) +
                            " consecutive_low=" + trigger.ConsecutiveLow + " fault_triggered=" + fault);
                        previousTicks = now; previousCount = count; previousParsed = counters.Parsed;
                        if (windowCount == 0)
                            throw new InvalidOperationException("No FD2 for a full watch window; never claim a rate success.");
                        if (fault || Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds) break;
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    source.EndNotifyLayerProbe(probe);
                    await scheduling.StopAsync();
                }
                await Task.Delay(100);
                var report = probe.Snapshot() with
                {
                    PhaseCompleted = failure == null,
                    Failure = failure?.Message ?? "",
                    Windows = chunkWindows.ToArray(), Scheduling = scheduling.Snapshot()
                };
                recent.Enqueue((report, probe.ToCsv()));
                while (recent.Count > 3) recent.Dequeue();
                // Write once per bounded chunk, never on the Notify hot path.
                await Save(failure?.Message ?? "", complete: false);
                if (failure != null) throw new InvalidOperationException("Watch interrupted; partial evidence saved.", failure);
            }
            if (!fault)
            {
                await Save("", complete: true);
                log("[BLE_RATE_WATCH] bounded_watch_complete fault_observed=false long_term_or_other_pc_guarantee=false summary=" +
                    Path.Combine(runDirectory, "summary.json"));
                return 0;
            }
            log("[BLE_RATE_WATCH] real_drop_captured=true entry_rate_hz=" + F(windows[^1].Fd2CallbackHz) +
                " previous_chunks_saved=3 automatic_same_connection_layer_comparison=true");
            // Keep the same subscription and physical connection; no new pairing
            // or reinitialization between a captured drop and the layer comparison.
            int layerResult = await BleNotifyLayerExperiment.RunAsync(source, directory, log, token);
            await Save("", complete: true, layerExit: layerResult);
            return 3; // A real drop occurred, even if the later comparison recovers.
        }
        catch (Exception ex)
        {
            await Save(ex.Message, complete: false);
            throw;
        }

        void RequireSameConnection()
        {
            if (!source.IsRunning || !source.IsTransportConnected || source.ConnectedAddress != address)
                throw new InvalidOperationException("Physical connection changed; no hidden retry/reconnect.");
        }
        async Task Save(string failure, bool complete, int? layerExit = null)
        {
            foreach (var item in recent)
                await File.WriteAllTextAsync(Path.Combine(runDirectory, item.Report.Phase + ".csv"), item.Csv, new UTF8Encoding(false));
            // Memory/summary retain three chunks. Earlier CSV evidence is preserved
            // on disk (bounded by the <=1h watch), never silently deleted.
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "summary.json"), JsonSerializer.Serialize(new
            {
                Address = address, TargetHz = 133, MinimumWindowHz = BleRateFaultTrigger.MinimumWindowHz,
                StartedUtc = startedUtc, SnapshotUtc = DateTimeOffset.UtcNow,
                RequestedSeconds = seconds, MeasuredSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                Completed = complete, Failure = failure, FaultObserved = fault,
                HadHighRateWindow = trigger.HadHighRateWindow, ConsecutiveLowWindows = trigger.ConsecutiveLow,
                LayerComparisonExit = layerExit, VirtualOutput = false, InitialBatteryQueries = false,
                RuntimeBatteryQueries = false, SystemWrites = false, CauseProven = false,
                TimestampScope = "Windows GATT delivery; HCI must be correlated separately; no air capture",
                Windows = windows, RecentChunks = recent.Select(item => item.Report).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
}

internal sealed record BleRateWatchWindow(double ElapsedSeconds, double Seconds, double Fd2CallbackHz,
    double ParsedHz, int ThreadPoolThreads, long ThreadPoolPending, double GcPauseTotalMs);

// Pure guard, also used by synthetic offline tests. Two real >=4s windows,
// not an average over the whole connection and not a single burst/gap.
internal sealed class BleRateFaultTrigger
{
    public const double MinimumWindowHz = 128;
    public int ConsecutiveLow { get; private set; }
    public bool HadHighRateWindow { get; private set; }
    public bool Observe(double seconds, double rawHz, double parsedHz)
    {
        if (!double.IsFinite(seconds) || seconds < 4 || !double.IsFinite(rawHz) ||
            !double.IsFinite(parsedHz) || rawHz < 0 || parsedHz < 0)
            throw new InvalidOperationException("Invalid rate window is not evidence of success or a Bluetooth cause.");
        bool good = rawHz >= MinimumWindowHz && parsedHz >= MinimumWindowHz;
        HadHighRateWindow |= good;
        ConsecutiveLow = good ? 0 : ConsecutiveLow + 1;
        return ConsecutiveLow >= 2;
    }
}
