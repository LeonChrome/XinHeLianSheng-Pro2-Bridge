using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Y700Switch2V60Viiper;

// Runs the actual native adapter, not a second managed implementation of btkeyLib.
// The --btkey-self-test entry must return before adapter discovery or driver work.
internal static class BtkeyEngineIntegrationTests
{
    private const string PinnedCommit = "4b49cb955d08dc80a82673083fc0463cbc0c2540";

    public static void Run(Action<bool, string> check)
    {
        string? path = Environment.GetEnvironmentVariable("PRO2_BTKEY_NATIVE_EXE");
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("SKIP: btkey native integration; set PRO2_BTKEY_NATIVE_EXE to the freshly built relay executable.");
            return;
        }
        Run(check, path);
    }

    public static void Run(Action<bool, string> check, string executablePath)
    {
        string fullPath = Path.GetFullPath(executablePath);
        check(File.Exists(fullPath), "btkey integration uses an explicit existing native executable");
        if (!File.Exists(fullPath)) return;

        var start = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!
        };
        start.ArgumentList.Add("--btkey-self-test");
        using var process = Process.Start(start) ?? throw new IOException("The native btkey self-test did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(30_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        check(exited, "btkey offline self-test finishes within 30 seconds");
        if (!exited) return;
        string stdout = output.GetAwaiter().GetResult();
        _ = errors.GetAwaiter().GetResult();
        check(process.ExitCode == 0, "actual btkey native self-test returns success");

        JsonDocument? evidence = null;
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int firstBrace = line.IndexOf('{');
            if (firstBrace < 0) continue;
            try
            {
                var candidate = JsonDocument.Parse(line[firstBrace..]);
                if (candidate.RootElement.TryGetProperty("event", out var kind) &&
                    kind.GetString() == "BTKEYLIB_SELF_TEST")
                {
                    evidence?.Dispose();
                    evidence = candidate;
                }
                else candidate.Dispose();
            }
            catch (JsonException) { }
        }
        check(evidence != null, "native adapter emits machine-readable BTKEYLIB_SELF_TEST evidence");
        if (evidence == null) return;
        using (evidence) ValidateEvidence(check, evidence.RootElement);
        ValidateTransitionCapture(check, fullPath);
    }

    private static void ValidateTransitionCapture(Action<bool, string> check, string executable)
    {
        var folder = Directory.CreateTempSubdirectory("pro2-switch-capture-");
        string capture = Path.Combine(folder.FullName, "switch-active-01.btsnoop");
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--verify-transition-capture");
            start.ArgumentList.Add(Path.Combine(folder.FullName, "result.json"));
            using var process = Process.Start(start) ?? throw new IOException("The capture fixture did not start.");
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException("The offline capture writer did not finish.");
            }
            check(process.ExitCode == 0 && File.Exists(capture), "transport fixture publishes its BTSnoop file");
            byte[] bytes = File.ReadAllBytes(capture);
            check(bytes.AsSpan(0, 8).SequenceEqual("btsnoop\0"u8) && U32(8) == 1 && U32(12) == 1002,
                "standard BTSnoop v1 header with H4 datalink");

            (uint Original, uint Captured, uint Direction, int OffsetUs)[] rows =
            [
                (17,17,1,0), (21,21,0,100000), (59,59,0,200000), (9,9,3,900000),
                (59,25,0,1000000), (59,11,0,1100000), (59,9,1,1200000), (59,5,0,1300000),
                (6,6,3,1600000), (59,22,1,1650000), (11,11,3,1700000), (17,17,1,1750000), (7,7,2,1800000)
            ];
            int offset = 16;
            ulong firstStamp = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                check(offset + 24 <= bytes.Length, "complete BTSnoop record header");
                check(U32(offset) == r.Original && U32(offset+4) == r.Captured &&
                      U32(offset+8) == r.Direction && U32(offset+12) == 0, "original length, safe length and direction " + i);
                ulong stamp = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset+16, 8));
                if (i == 0) firstStamp = stamp;
                check(stamp == firstStamp + (uint)r.OffsetUs, "monotonic microsecond timestamp " + i);
                offset += 24;
                var body = bytes.AsSpan(offset, (int)r.Captured);
                check(!body.Contains((byte)0xab), "private fixture marker absent " + i);
                if (i == 4) check(body[^2] == 0x90 && body[^1] == 0x10, "flash reply ends at ACK and command ID");
                if (i == 5) check(body[^1] == 0x31, "NFC reply ends at report ID");
                if (i == 9) check(body[^2] == 3 && body[^1] == 0x30, "public report-mode request argument retained");
                if (i == 10) check(body.Slice(4,6).SequenceEqual(new byte[6]) && body[^1] == 1, "role kept and address masked");
                if (i == 11) check(body[9] == 6 && body[0] == 2, "incoming L2CAP close request retained");
                if (i == 12) check(body[0] == 1 && body[^1] == 0x13, "local HCI disconnect retains its reason");
                offset += body.Length;
            }
            check(offset == bytes.Length, "out-of-window traffic and key exchanges omitted");
            uint U32(int startAt) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(startAt, 4));
        }
        finally
        {
            File.Delete(capture);
            File.Delete(capture + ".partial");
            folder.Delete();
        }
    }

    private static void ValidateEvidence(Action<bool, string> check, JsonElement evidence)
    {
        check(Number(evidence, "schema") == 1, "btkey self-test evidence has the agreed schema version");
        check(Text(evidence, "source_commit") == PinnedCommit, "native engine identifies the pinned complete upstream source");
        check(Flag(evidence, "hardware_opened") == false, "native self-test opens no physical Bluetooth adapter");
        check(Flag(evidence, "engine_owns_protocol") == true, "native engine owns ACKs and handshake state");
        check(Number(evidence, "failed") == 0, "all actual native engine assertions pass");
        check(Number(evidence, "cases") >= 8, "native self-test exercises multiple engine behaviors");
        check(Math.Abs(Number(evidence, "requested_hz") - 133) < 0.01 &&
              Math.Abs(Number(evidence, "effective_hz") - 60) < 0.01,
            "upstream 60Hz compatibility cap remains distinct from the requested 133Hz");

        if (evidence.TryGetProperty("cases_detail", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (JsonElement result in results.EnumerateArray())
                check(Flag(result, "pass") == true, "btkey actual engine behavior: " + Text(result, "name"));

        // Optional fixtures are generated by the real adapter self-test. When present,
        // decode them through the production physical parser to catch disagreements
        // between the SPI calibration the native side advertises and its actual reports.
        if (evidence.TryGetProperty("reports", out var reports) &&
            evidence.TryGetProperty("expected_snapshot", out var expected))
            ValidateReportRoundtrip(check, reports, expected);
        else
            Console.WriteLine("SKIP: native-to-managed calibration roundtrip; native evidence contains no report fixtures.");
    }

    private static void ValidateReportRoundtrip(Action<bool, string> check, JsonElement reports, JsonElement expected)
    {
        byte[] sticks = SpiPayload(Hex(reports, "spi603d_hex"), 0x603d, 18);
        byte[] imu = SpiPayload(Hex(reports, "spi6020_hex"), 0x6020, 24);
        byte[] input = WithoutTransactionHeader(Hex(reports, "latest_input_hex"));
        check(sticks.Length >= 18 && imu.Length >= 24, "native fixtures carry the actual advertised stick and IMU calibration");
        check(input.Length >= 49 && input[0] is 0x30 or 0x31, "native fixture contains a complete real-engine Nintendo input report");
        if (sticks.Length < 18 || imu.Length < 24 || input.Length < 49) return;

        var parser = new NintendoPhysicalParser(NintendoInputKind.Pro1Bluetooth);
        parser.SetDeadzones(new(0, 0));
        parser.SetPro1FactoryCalibration(sticks, imu);
        bool parsed = parser.TryParse(input, out var state, Stopwatch.GetTimestamp());
        check(parsed, "production managed parser accepts the real native engine fixture");
        if (!parsed) return;
        check(Math.Abs(state.Lx - Number(expected, "lx")) <= 4 &&
              Math.Abs(state.Ly - Number(expected, "ly")) <= 4 &&
              Math.Abs(state.Rx - Number(expected, "rx")) <= 4 &&
              Math.Abs(state.Ry - Number(expected, "ry")) <= 4,
            "native stick encoding agrees with its advertised factory calibration");
        check(Math.Abs(state.AccelX - Number(expected, "accel_x")) <= 4 &&
              Math.Abs(state.AccelY - Number(expected, "accel_y")) <= 4 &&
              Math.Abs(state.AccelZ - Number(expected, "accel_z")) <= 4 &&
              Math.Abs(state.GyroX - Number(expected, "gyro_x")) <= 4 &&
              Math.Abs(state.GyroY - Number(expected, "gyro_y")) <= 4 &&
              Math.Abs(state.GyroZ - Number(expected, "gyro_z")) <= 4,
            "native six-axis output roundtrips to shared acceleration and gyro units");
        if (expected.TryGetProperty("buttons", out var buttons) && buttons.TryGetUInt64(out ulong mask))
            check((ulong)state.Buttons == mask, "native button positions roundtrip to the shared managed button mask");
    }

    private static byte[] WithoutTransactionHeader(byte[] report) =>
        report.Length >= 2 && report[0] == 0xa1 ? report[1..] : report;

    private static byte[] SpiPayload(byte[] bytes, uint address, int minimum)
    {
        bytes = WithoutTransactionHeader(bytes);
        if (bytes.Length >= 20 && bytes[0] == 0x21)
        {
            uint actual = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15, 4));
            if (bytes[13] != 0x90 || bytes[14] != 0x10 || actual != address ||
                bytes[19] < minimum || bytes.Length < 20 + bytes[19]) return [];
            return bytes.AsSpan(20, bytes[19]).ToArray();
        }
        return bytes.Length >= minimum ? bytes : [];
    }

    private static byte[] Hex(JsonElement value, string key) => Convert.FromHexString(Text(value, key));
    private static string Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static double Number(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.TryGetDouble(out double number) ? number : double.NaN;
    private static bool? Flag(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;
}
