using System;
using System.Linq;
using System.Text;
using Y700Switch2V60Viiper;

internal static class RecentSessionEvidenceRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var evidence = new RecentSessionStatus();
        const string origin = "[01:02:03.004] [SESSION_LOG] C:\\logs\\manager_original_session.log";
        const string battery = "[01:10:03.004] [PRO2_BATTERY] source=initial_only_snapshot raw_power=0x1C level=7/9 percent_approx=78";
        const string runtimeBattery = "[01:20:03.004] [BATTERY_RUNTIME_UPDATED] level=6/9 source=fresh_primary_notify ui_updated=true";
        const string disconnect = "[02:00:03.004] [PRO2_BLE_DISCONNECT_SIGNAL] detector=windows_connection_status sequence=7 address=A4:C1:E8:01:02:03 bluetooth_error=not_provided last_battery=67";
        const string genuineError = "[02:01:03.004] [PRO2_BLE] active watcher stopped status=Aborted error=RadioNotAvailable";
        evidence.Add(origin + "\r\n" + battery + "\n" + runtimeBattery + "\n" + disconnect + "\n" + genuineError);
        for (int i = 0; i < 20000; i++)
        {
            evidence.Add("[03:00:00.000] [PRO2_BLE] active watcher stopped status=Stopped error=Success\n" +
                "[03:00:00.001] [PRO2_BLE] passive scan result count=0 stop_error=Success\n" +
                "[03:00:00.002] [UNIMPORTANT] physical_disconnect=false parse_errors=0\n");
            evidence.Add("[03:00:00.003] [PRO2_BLE_RATE_WINDOW] fd2_hz=133.0 sample=" + i + "\n" +
                "[03:00:00.004] [PRO2_RUMBLE_TRANSPORT] input_hz=83.3 sent_hz=83.3 sample=" + i);
        }
        string snapshot = evidence.Snapshot;
        check(snapshot.Contains(origin) && snapshot.Contains(battery) && snapshot.Contains(runtimeBattery) &&
              snapshot.Contains(disconnect), "scan/measurement/rumble floods cannot evict identity, battery or disconnect");
        check(snapshot.Contains(genuineError), "actual radio error survives successful scan flood");
        check(!snapshot.Contains("stop_error=Success") && !snapshot.Contains("error=Success") &&
              !snapshot.Contains("[UNIMPORTANT]"), "successful scan and non-disconnect fields are not critical evidence");
        check(snapshot.Contains("sample=19999") && !snapshot.Contains("sample=0\n"),
            "bounded measurements and rumble keep their most recent samples");
        check(snapshot.Contains("battery_values=last_recorded_not_current") &&
              snapshot.Contains("[01:10:03.004] [PRO2_BATTERY] source=initial_only_snapshot") &&
              !snapshot.Contains("[03:00:00.004] [PRO2_BATTERY]"),
            "old battery retains original timestamp/source and is explicitly not a fresh query");
        check(snapshot.IndexOf(battery, StringComparison.Ordinal) < snapshot.IndexOf(runtimeBattery, StringComparison.Ordinal) &&
              snapshot.IndexOf(runtimeBattery, StringComparison.Ordinal) < snapshot.IndexOf(disconnect, StringComparison.Ordinal) &&
              snapshot.IndexOf(disconnect, StringComparison.Ordinal) < snapshot.IndexOf(genuineError, StringComparison.Ordinal),
            "independent retention classes retain original global record order");
        CheckBound(snapshot, check, "mixed-noise flood");
        const string queueSummary = "[03:01:00.000] [PRO2_RUMBLE_QUEUE] rumble_adaptive_drop=12 rumble_stop_retries=1";
        evidence.Add(queueSummary);
        check(evidence.Snapshot.Contains(queueSummary), "separate rumble queue summary is retained with transport evidence");
        foreach (string tag in new[] { "VIIPER_INPUT_PIPELINE", "VIIPER_INPUT_POLICY", "VIIPER_INPUT_SAFETY", "PS5_MOTION_VALIDITY", "DATA_PATH_POLICY", "PRO2_RUMBLE_HOLD", "PRO2_RUMBLE_SHUTDOWN" })
        {
            string record = "[03:01:00.001] [" + tag + "] fresh_source=133 held_repeat=117 counted_as_fd2=false";
            evidence.Add(record);
            check(evidence.Snapshot.Contains(record), "v2 fresh/repeat and generated rumble evidence retained: " + tag);
        }
        foreach (string tag in new[] { "PRO2_RUMBLE_CONVERTED", "RUMBLE_CONVERSION", "HAPTIC_CONVERSION", "HAPTIC_STOP", "PS5_FEEDBACK_STATE" })
        {
            string record = "[03:01:01.000] [" + tag + "] source=ps5 frequency_mapping=legacy_hz_proxy_unvalidated";
            evidence.Add(record);
            check(evidence.Snapshot.Contains(record), "new all-mode haptic evidence survives detail log limit: " + tag);
        }

        foreach (string message in new[]
        {
            "[04:00:00.001] [PRO2_BLE] active watcher stopped status=Aborted error=AccessDenied",
            "[04:00:00.002] [PRO2_BLE] passive scan result count=0 stop_error=OtherError",
            "[04:00:00.003] [PRO2_BLE] service discovery failed: Unreachable",
            "[04:00:00.004] [PRO2_BLE] error=Success follow-up failed: IOException",
            "[04:00:00.005] [TEST] worker_stop_error=InvalidOperationException",
            "[04:00:00.006] [TEST] hresult=0x80070005",
            "[04:00:00.007] [TEST] error=SuccessUnexpected",
        })
        {
            evidence.Add(message);
            check(evidence.Snapshot.Contains(message), "actual failure retained: " + message);
        }
        foreach (string message in new[]
        {
            "[04:00:01.001] [NOISE] error=success",
            "[04:00:01.002] [NOISE] stop_error=\"Success\" bluetooth_error=None",
            "[04:00:01.003] [NOISE] error=0x00000000 hresult=0",
            "[04:00:01.004] [NOISE] windows_disconnect=false",
        })
        {
            evidence.Add(message);
            check(!evidence.Snapshot.Contains(message), "neutral status excluded: " + message);
        }

        var saturated = new RecentSessionStatus();
        saturated.Add(origin);
        string large = string.Concat(Enumerable.Repeat("原始证据😀", 1000));
        for (int i = 0; i < 1000; i++)
        {
            saturated.Add("[05:00:00.000] [BLE_COMPATIBILITY] iteration=" + i + " " + large);
            saturated.Add("[05:00:00.001] [PRO2_BATTERY] source=read iteration=" + i + " " + large);
            saturated.Add("[05:00:00.002] [PRO2_BLE_DISCONNECT_SIGNAL] iteration=" + i + " " + large);
            saturated.Add("[05:00:00.003] [TEST] error=DeviceNotAvailable iteration=" + i + " " + large);
            saturated.Add("[05:00:00.004] [PRO2_BLE_RATE_WINDOW] iteration=" + i + " " + large);
            saturated.Add("[05:00:00.005] [PRO2_RUMBLE_TRANSPORT] iteration=" + i + " " + large);
        }
        string bounded = saturated.Snapshot;
        CheckBound(bounded, check, "all-category Unicode saturation");
        check(bounded.Contains(origin), "source file is pinned even during identity storms");
        check(bounded.Contains("[RECENT_LINE_TRUNCATED]") && !bounded.Contains('\uFFFD'),
            "long Unicode evidence is marked truncated without breaking surrogate pairs");
        check(bounded.Split('\n').Where(line => line.Contains("iteration=")).Count() > 20 &&
              bounded.Contains("iteration=999"), "each independent category retains a bounded useful history");
        check(Encoding.UTF8.GetByteCount(bounded) > 100 * 1024, "size test exercises most of retention capacity");

        var disconnected = new RecentSessionStatus();
        const string windows = "[06:00:00.001] [PRO2_BLE_LINK] windows_connection_status=Disconnected";
        const string gatt = "[06:00:00.002] [PRO2_BLE_LINK] gatt_session_status=Closed bluetooth_error=Success";
        disconnected.Add(windows + "\n" + gatt);
        check(disconnected.Snapshot.Contains(windows) && disconnected.Snapshot.Contains(gatt),
            "real disconnected/closed status retained even when Bluetooth reports Success");
        for (int i = 0; i < 10000; i++)
            disconnected.Add("[06:01:00.000] [PRO2_OFFLINE_NOTICE] duplicate_suppressed=1 sequence=1");
        check(disconnected.Snapshot.Contains(windows) && disconnected.Snapshot.Contains(gatt),
            "duplicate offline notices do not erase actual disconnect evidence");
    }

    private static void CheckBound(string snapshot, Action<bool, string> check, string name)
    {
        check(Encoding.UTF8.GetByteCount(snapshot) <= RecentSessionStatus.MaximumBytes,
            name + " stays within absolute UTF8 byte limit including notice");
        check(snapshot.Split('\n').All(line => Encoding.UTF8.GetByteCount(line) <= 2048),
            name + " individually bounds all retained lines");
    }
}
