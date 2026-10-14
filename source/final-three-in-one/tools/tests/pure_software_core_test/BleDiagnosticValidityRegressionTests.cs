using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class BleDiagnosticValidityRegressionTests
{
    private static readonly Assembly AppAssembly = typeof(Pro2BleInputSource).Assembly;
    private static readonly Type CaptureType = AppAssembly.GetType("Y700Switch2V60Viiper.Win10BleDiagnosticCapture")!;
    private static readonly Type DiagnosticType = AppAssembly.GetType("Y700Switch2V60Viiper.Win10BleDiagnostic")!;
    private const long Start = 1000000;
    private static long Tick(double seconds) => Start + (long)Math.Round(seconds * Stopwatch.Frequency);
    private static object Capture() => Activator.CreateInstance(CaptureType, [Start, DateTimeOffset.UtcNow])!;
    private static void Record(object capture, double seconds, bool parsed = true, bool fd2 = true)
    {
        CaptureType.GetMethod("RecordRaw")!.Invoke(capture, [Tick(seconds), fd2]);
        if (parsed) CaptureType.GetMethod("RecordParsed")!.Invoke(capture, [Tick(seconds), fd2]);
    }
    private static void Feed(object capture, double begin, double end, int hz, int parseEvery = 1)
    {
        int count = (int)Math.Round((end - begin) * hz);
        for (int i = 0; i < count; i++) Record(capture, begin + i / (double)hz, i % parseEvery == 0);
    }
    private static void Interrupt(object capture, string reason) =>
        CaptureType.GetMethod("MarkInterrupted")!.Invoke(capture, [reason]);
    private static Win10BleDiagnosticReport Report(object capture, double seconds, bool complete = true, bool connected = true)
    {
        object samples = CaptureType.GetMethod("Snapshot")!.Invoke(capture, [Tick(seconds), complete, connected])!;
        var adapter = new BluetoothAdapterDiagnosticInfo("offline", "none", "none", "none", "none", "none", "none", "none");
        var report = (Win10BleDiagnosticReport)DiagnosticType.GetMethod("BuildReport")!.Invoke(null,
            [samples, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(seconds), adapter, "offline", (ushort)12,
                "offline", (ushort)133, "not_read", "offline_only", "not_read"])!;
        return report with { ExperimentProfile = "OFFLINE_REGRESSION_NO_RADIO" };
    }

    public static void Run(Action<bool, string> check, string output)
    {
        object stable = Capture(); Feed(stable, 0, 60, 250);
        var high = Report(stable, 60);
        check(high.RawNotifyCount == 15000 && high.Fd2NotifyCount == 15000 && high.ParsedCount == 15000,
            "diagnostic full counts exceed 8192 without rate truncation");
        check(Math.Abs(high.Fd2NotifyHz - 250) < .001 && high.Input133HzAccepted && high.IsInputAccepted(128),
            "uninterrupted fresh 60s high rate passes Win10/Win11 acceptance");
        check(high.GapSamplesTruncated && high.Fd2NotifyGaps.SampleCount == 8191 && high.MaximumFd2SilenceMs < 5,
            "bounded percentile samples are explicitly separate from full counts and full gap guard");
        string log = string.Join('\n', high.ToLogLines());
        check(log.Contains("gap_samples_truncated=True") && log.Contains("rate_counts=full_capture") &&
            log.Contains("raw_count=15000"), "diagnostic exposes count scope and sample truncation");

        object failed = Capture(); Feed(failed, 0, 39, 250);
        Interrupt(failed, "windows_connection_status");
        var disconnected = Report(failed, 60, connected: false);
        check(disconnected.Fd2NotifyHz > 120 && !disconnected.Input133HzAccepted && !disconnected.IsInputAccepted(60),
            "39s high-rate then disconnect cannot pass even the 60Hz CLI threshold");
        check(!disconnected.ConnectionContinuous && !disconnected.ConnectedAtEnd &&
            disconnected.Verdict.StartsWith("connection_interrupted:") && disconnected.MaximumFd2SilenceMs > 21000,
            "physical disconnect is sticky and silent tail is represented");

        object resumed = Capture(); Feed(resumed, 0, 20, 250);
        Interrupt(resumed, "gatt_session_closed"); Interrupt(resumed, "receiver_stopped");
        Feed(resumed, 20, 60, 250);
        var reconnect = Report(resumed, 60);
        check(reconnect.ConnectedAtEnd && !reconnect.Input133HzAccepted &&
            reconnect.MeasurementFailure == "connection_interrupted:gatt_session_closed",
            "reconnected end state cannot erase the first interruption");

        object silent = Capture(); Feed(silent, 0, 39, 250);
        var stalled = Report(silent, 60);
        check(stalled.ConnectedAtEnd && stalled.ConnectionContinuous && !stalled.Input133HzAccepted &&
            stalled.MeasurementFailure == "input_stalled", "silent connected source cannot pass using its old average");

        object middle = Capture(); Feed(middle, 0, 10, 1000); Feed(middle, 11, 60, 250);
        var recovered = Report(middle, 60);
        check(recovered.Fd2NotifyHz > 120 && recovered.LastParsedInputAgeMs < 5 && recovered.GapSamplesTruncated &&
            recovered.MaximumFd2SilenceMs >= 1000 && !recovered.Input133HzAccepted,
            "mid-test stall after the sample cap still fails even when fresh input resumes");

        object parseLoss = Capture(); Feed(parseLoss, 0, 10, 250, parseEvery: 2);
        check(!Report(parseLoss, 10).Input133HzAccepted, "parsed ratio still gates high raw rate");
        object parseStall = Capture(); Feed(parseStall, 0, 6, 250); Feed(parseStall, 6, 10, 250, parseEvery: 100000);
        check(!Report(parseStall, 10).Input133HzAccepted, "raw arrivals cannot hide a stalled parser");

        object compatible = Capture(); Feed(compatible, 0, 15, 66);
        var low = Report(compatible, 15);
        check(low.MeasurementValid && low.IsInputAccepted(60) && !low.Input133HzAccepted && !low.IsInputAccepted(128),
            "healthy 66Hz remains compatible without falsely passing 133Hz");
        check(!Report(stable, 60, complete: false).Input133HzAccepted &&
            Report(stable, 60, complete: false).MeasurementFailure == "measurement_not_completed",
            "incomplete/cancelled measurement cannot pass");
        check(!Report(stable, 9).Input133HzAccepted && Report(stable, 9).MeasurementFailure == "measurement_window_too_short",
            "short measurement never earns a stability success");
        check(!Report(stable, 60, connected: false).Input133HzAccepted,
            "disconnected final state fails even without a delivered disconnect callback");
        object empty = Capture();
        check(!Report(empty, 15).IsInputAccepted(60), "no input does not pass");
        Record(empty, -1); Record(empty, .5, fd2: false);
        var rawOnly = Report(empty, 15);
        check(rawOnly.RawNotifyCount == 1 && rawOnly.Fd2NotifyCount == 0 && !rawOnly.Input133HzAccepted,
            "pre-capture callback and non-FD2 traffic do not inflate FD2 counters");
        foreach (double? bad in new double?[] { null, -1, double.NaN, double.PositiveInfinity, 501 })
            check(!(high with { LastParsedInputAgeMs = bad }).Input133HzAccepted,
                "invalid or stale freshness fails closed: " + bad);
        check(!(high with { ConnectionContinuous = false }).Input133HzAccepted &&
            !(high with { MeasurementFailure = "measurement_not_verified" }).IsInputAccepted(60),
            "missing continuity evidence is not inferred from high Hz");

        // Verify the production event/stop routes mark the SAME capture object;
        // the tests open no radio and use the actual receiver's private gate path.
        var receiver = new Pro2BleInputSource();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Pro2BleInputSource).GetField("activeBleDiagnosticCapture", flags)!.SetValue(receiver, stable);
        typeof(Pro2BleInputSource).GetMethod("CaptureDisconnectSignal", flags)!.Invoke(receiver, ["offline_event_test", null]);
        check(!Report(stable, 60).Input133HzAccepted, "receiver disconnect path marks the active diagnostic before running-state guard");
        object stopped = Capture(); Feed(stopped, 0, 15, 133);
        typeof(Pro2BleInputSource).GetField("activeBleDiagnosticCapture", flags)!.SetValue(receiver, stopped);
        receiver.DisposeAsync().AsTask().GetAwaiter().GetResult();
        check(!Report(stopped, 15).Input133HzAccepted, "manual stop/disposal marks a measurement interrupted");

        string json = JsonSerializer.Serialize(disconnected);
        check(json.Contains("\"Input133HzAccepted\":false") && json.Contains("\"MeasurementValid\":false") &&
            json.Contains("connection_interrupted"), "serialized failed report cannot claim accepted");
        File.WriteAllText(Path.Combine(output, "diagnostic-disconnect-regression.json"), json);
        File.WriteAllText(Path.Combine(output, "diagnostic-disconnect-regression.log"), string.Join(Environment.NewLine, disconnected.ToLogLines()));

        string? replays = Environment.GetEnvironmentVariable("PRO2_DIAGNOSTIC_REPLAY_PATHS");
        foreach (string path in (replays ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            object replay = Capture(); DateTimeOffset? first = null; int frames = 0;
            foreach (string line in File.ReadLines(path))
            {
                using var item = JsonDocument.Parse(line);
                if (item.RootElement.GetProperty("type").GetString() != "frame") continue;
                DateTimeOffset arrival = item.RootElement.GetProperty("frame").GetProperty("Timestamp").GetDateTimeOffset();
                first ??= arrival;
                Record(replay, (arrival - first.Value).TotalSeconds); frames++;
            }
            Interrupt(replay, "replayed_windows_disconnect");
            var result = Report(replay, 60, connected: false);
            check(frames > 8192 && result.Fd2NotifyCount == frames && result.Fd2NotifyHz > 120 && !result.Input133HzAccepted,
                "real failed-session replay preserves full counts and rejects: " + Path.GetFileName(Path.GetDirectoryName(path)));
            File.WriteAllText(Path.Combine(output, "diagnostic-replay-" + frames + ".json"), JsonSerializer.Serialize(result));
        }
    }
}
