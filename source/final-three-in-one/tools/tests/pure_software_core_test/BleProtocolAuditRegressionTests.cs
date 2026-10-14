using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class BleProtocolAuditRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        byte[] pair = [0x15, 0x91, 1, 3, 0, 1, 0, 0, 0];
        byte[] usb = [3, 0x91, 1, 0x0d, 0, 8, 0, 0, 1, 0, 255, 255, 255, 255, 255, 255];
        byte[] mask = [12, 0x91, 1, 2, 0, 4, 0, 0, 255, 0, 0, 0];
        check(!BleInitAuditPolicy.ShouldSend(pair, false) && !BleInitAuditPolicy.ShouldSend(pair, true), "BLE input never sends pairing commit");
        check(BleInitAuditPolicy.ShouldSend(usb, false) && !BleInitAuditPolicy.ShouldSend(usb, true), "USB init is one independent next-connection comparison");
        check(BleInitAuditPolicy.ShouldSend(mask, false) && BleInitAuditPolicy.ShouldSend(mask, true), "Feature mask unchanged between profiles");
        check(!BleInitAuditPolicy.ShouldSend([], false), "Short command rejected");
        check(BleInitAuditPolicy.AckMatches(mask, [12, 1, 1, 2, 0x10, 0x78, 0, 0]), "ACK command/subcommand matched");
        check(!BleInitAuditPolicy.AckMatches(mask, [12, 1, 1, 4, 0x10, 0x78, 0, 0]), "Different subcommand identified as mismatch");
        byte[][] original = (byte[][])typeof(Pro2BleInputSource).GetField("InitCommands", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        check(original.Count(item => !BleInitAuditPolicy.ShouldSend(item, false)) == 1, "Default changes only one original initialization command");
        check(original.Count(item => !BleInitAuditPolicy.ShouldSend(item, true)) == 2, "Second profile skips only pairing and USB init");
        check(!BleInitAuditPolicy.CommandIdentity(pair).Contains("159101"), "Command summary exposes no payload hex");
        var input = new Pro2BleInputSource();
        try
        {
            var begin = typeof(Pro2BleInputSource).GetMethod("BeginProtocolAudit", BindingFlags.Instance | BindingFlags.NonPublic)!;
            begin.Invoke(input, [new SilentProgress()]);
            input.SkipUsbInitializationOnNextConnect = true;
            check(!(bool)typeof(Pro2BleInputSource).GetField("sessionSkipUsbInitialization", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input)!,
                "Next-connection toggle does not change frozen active initialization");
            begin.Invoke(input, [new SilentProgress()]);
            check((bool)typeof(Pro2BleInputSource).GetField("sessionSkipUsbInitialization", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input)!,
                "Next audit snapshots USB comparison flag");
            check(!input.IsRunning && !input.IsTransportConnected, "Policy test opens no radio connection");
        }
        finally { input.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var vm = new MainViewModel(previewOnly: true);
        try
        {
            vm.SkipUsbBleInitialization = true;
            check(vm.Pro2Slots.All(slot => slot.InputSource.SkipUsbInitializationOnNextConnect), "UI flag reaches all controller slots");
            check(!vm.Running && vm.Pro2Slots.All(slot => !slot.InputSource.IsRunning), "UI comparison opens no virtual or physical controller");
            check(vm.EnvironmentReportText.Contains("BLE 初始化与停流诊断"), "System/Bluetooth report includes protocol evidence");
        }
        finally { vm.ShutdownAsync().GetAwaiter().GetResult(); }

        long start = Stopwatch.Frequency;
        var audit = new BleProtocolSessionAudit();
        audit.Begin(false, start);
        audit.Connected("windows", start);
        audit.Connected("gatt", start + Stopwatch.Frequency / 10);
        audit.SetPdu(512);
        byte[] frame = new byte[63];
        for (uint i = 0; i < 10000; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame, i);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(42), i * 4000);
            audit.RecordFd2(frame, start + i * Stopwatch.Frequency / 250, 0.25);
        }
        string end = audit.End("synthetic_disconnect", start + 42 * Stopwatch.Frequency);
        check(end.Contains("fd2_count=10000"), "Full input count exceeds 8192 without truncation");
        check(end.Contains("fd2_active_span_hz=250.000"), "Distinct reports counted at 250Hz without output polling");
        check(end.Contains("last_fd2_after_windows_ms=39996.000"), "Stop relative to observed Windows connection");
        check(end.Contains("end_after_windows_ms=42000.000"), "End relative to same connection");
        check(end.Contains("motion_timestamp_changes=9999") && end.Contains("adjacent_same_raw=0"), "Motion/raw identity evidence retained");
        check(end.Contains("adjacent_same_controls=9999"), "Unchanged controls are valid, not declared duplicate packets");
        check(end.Contains("stable_250hz_proven=false"), "Fast notification count never claims stable 250Hz");
        check(end.Contains("winrt_max_pdu=512"), "Cached MaxPdu is included");
        audit.RecordFd2(frame, start + 50 * Stopwatch.Frequency, 0);
        check(audit.Summary(start + 51 * Stopwatch.Frequency).Contains("fd2_count=10000"), "Ended session ignores late notify samples");
        audit.Begin(true, start + 60 * Stopwatch.Frequency);
        string reconnect = audit.DiagnosticText(start + 61 * Stopwatch.Frequency);
        check(reconnect.Contains("synthetic_disconnect") && reconnect.Contains("fd2_count=10000"), "Reconnect preserves previous failed session");
        check(reconnect.Contains("no_pair_finalize_no_usb_init"), "Frozen comparison profile visible");
        for (int i = 0; i < 200; i++) audit.Note("control_record_" + i, start + 61 * Stopwatch.Frequency);
        string bounded = audit.DiagnosticText(start + 62 * Stopwatch.Frequency);
        check(!bounded.Contains("control_record_0\n") && bounded.Contains("control_record_199"), "Control history bounded");
        audit.RecordFd2(frame, start + 62 * Stopwatch.Frequency, -1);
        audit.RecordFd2(frame, start + 63 * Stopwatch.Frequency, 25);
        audit.RecordFd2(new byte[2], start + 64 * Stopwatch.Frequency, double.NaN);
        string repeats = audit.Summary(start + 65 * Stopwatch.Frequency);
        check(repeats.Contains("adjacent_same_raw=1") && repeats.Contains("adjacent_same_motion_timestamp=1"), "Repeated raw and motion identities separated");
        check(repeats.Contains("callback_age_negative=1") && repeats.Contains("callback_age_ge15=1"), "Delivery ages classified without RF latency claim");
        check(repeats.Contains("malformed=1"), "Malformed FD2 included without throwing");
        for (int i = 0; i < 6; i++) audit.Begin(false, start + (70 + i) * Stopwatch.Frequency);
        check(audit.DiagnosticText(start + 80 * Stopwatch.Frequency).Split("[BLE_SESSION_EVIDENCE]").Length == 5, "At most current plus three historical sessions");
        var perf = new BleProtocolSessionAudit();
        perf.Begin(false, start);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100000; i++) perf.RecordFd2(frame, start + i, 0);
        watch.Stop();
        Console.WriteLine("BLE AUDIT: 100000 cached FD2 observations in " + watch.Elapsed.TotalMilliseconds.ToString("F1") + "ms offline");
        string capture = "D:/CODEX/artifacts/pro1-fd2-250-audit-20261011/hci-182253/fd2-2.jsonl";
        if (File.Exists(capture))
        {
            var replay = new BleProtocolSessionAudit();
            var connectedAt = DateTimeOffset.Parse("2026-10-11T10:23:27.5150661Z");
            long Tick(DateTimeOffset time) => start + (long)((time - connectedAt).TotalSeconds * Stopwatch.Frequency);
            replay.Begin(false, start);
            replay.Connected("windows", start);
            foreach (string line in File.ReadLines(capture))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.GetProperty("type").GetString() != "frame") continue;
                var captured = document.RootElement.GetProperty("frame");
                replay.RecordFd2(Convert.FromHexString(captured.GetProperty("RawFd2Hex").GetString()!),
                    Tick(DateTimeOffset.Parse(captured.GetProperty("Timestamp").GetString()!)), 0);
            }
            string replayEnd = replay.End("historical_hci_0x08_replay_not_live", Tick(DateTimeOffset.Parse("2026-10-11T10:24:09.6179834Z")));
            check(replayEnd.Contains("fd2_count=9788") && replayEnd.Contains("fd2_active_span_hz=251.566"), "Real saved 9788-frame report replays without count clipping");
            check(replayEnd.Contains("motion_timestamp_changes=9637") && replayEnd.Contains("adjacent_same_motion_timestamp=150"), "Real motion timestamp repeat evidence reproduced");
            check(replayEnd.Contains("last_fd2_after_windows_ms=40004.676") && replayEnd.Contains("end_after_windows_ms=42102.917"), "Historical application times and HCI end kept on explicit replay clock");
            Console.WriteLine("BLE AUDIT REPLAY: " + replayEnd);
        }
    }

    private sealed class SilentProgress : IProgress<string> { public void Report(string value) { } }
}
