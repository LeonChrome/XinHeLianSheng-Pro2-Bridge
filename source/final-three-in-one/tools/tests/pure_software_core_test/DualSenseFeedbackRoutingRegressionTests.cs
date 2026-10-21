using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Y700Switch2V60Viiper;

internal static class DualSenseFeedbackRoutingRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        byte[] Wire(byte[] report)
        {
            var frame = new byte[DualSenseHapticFrame.WireSize];
            frame[0] = (byte)DualSenseHapticFrameKind.HidOutput;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)report.Length);
            report.CopyTo(frame, 4);
            return frame;
        }

        void CheckPhysicalEncoding(Pro2OutputPacket packet, bool expectedActive, string name)
        {
            var ble = new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
            check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(packet.Report, 1, ble, out bool active, out _) &&
                active == expectedActive, name + " survives unchanged physical BLE encoding");
        }

        foreach (bool quality in new[] { false, true })
        foreach (int size in new[] { 48, 64 })
        foreach (var flags in new[] { (First: (byte)3, Third: (byte)0), (First: (byte)1, Third: (byte)0),
                     (First: (byte)2, Third: (byte)0), (First: (byte)2, Third: (byte)4),
                     (First: (byte)0, Third: (byte)4) })
        {
            var scheduler = new DualSenseHapticRumbleScheduler(quality);
            var report = new byte[size];
            report[0] = 2; report[1] = flags.First; report[39] = flags.Third;
            report[3] = 100; report[4] = 150;
            string name = $"PS5 ordinary quality={quality} size={size} flags={flags}";
            bool mapped = scheduler.TryProcess(Wire(report), out var active, out _, out _);
            check(mapped && scheduler.CompatibilityMode && active.Active && active.Source == "dualsense-ordinary",
                name + " does not require native PCM or Steam Input");
            check(!scheduler.HasNativeAudio, name + " no native audio fabricated");
            CheckPhysicalEncoding(active, true, name);

            report[3] = 0; report[4] = 0;
            mapped = scheduler.TryProcess(Wire(report), out var stop, out _, out _);
            check(mapped && !stop.Active && stop.Source == "dualsense-ordinary", name + " explicit stop delivered");
            CheckPhysicalEncoding(stop, false, name);

            report[1] = 0; report[39] = 0; report[3] = 100; report[4] = 150;
            mapped = scheduler.TryProcess(Wire(report), out var transition, out _, out _);
            check(mapped && !scheduler.CompatibilityMode && !transition.Active,
                name + " native mode transition does not replay stale motors");
            check(!scheduler.TryProcess(Wire(report), out _, out _, out _),
                name + " invalid ordinary bytes in native mode remain guarded");
        }

        string? directory = Environment.GetEnvironmentVariable("PRO2_FEEDBACK_FIXTURE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var scheduler = new DualSenseHapticRumbleScheduler(true);
            foreach (var item in new[] { (Name: "control-start", Active: true), (Name: "interrupt-update", Active: true),
                         (Name: "control-stop", Active: false), (Name: "native-mode", Active: false) })
            {
                byte[] wire = File.ReadAllBytes(Path.Combine(directory, item.Name + ".bin"));
                check(wire.Length == DualSenseHapticFrame.WireSize &&
                    DualSenseHapticFrame.TryParse(wire, out var frame, out _) &&
                    frame.Kind == DualSenseHapticFrameKind.HidOutput,
                    item.Name + " actual Go stream fixture parsed");
                bool mapped = scheduler.TryProcess(wire, out var packet, out _, out _);
                check(mapped && packet.Active == item.Active, item.Name + " Go-to-CSharp start/stop state");
                CheckPhysicalEncoding(packet, item.Active, item.Name);
            }
            scheduler.TryProcess(File.ReadAllBytes(Path.Combine(directory, "audio-pcm.bin")), out _, out _, out _);
            check(scheduler.HasNativeAudio && !scheduler.CompatibilityMode,
                "actual Go PCM stream still reaches native haptic converter");
        }

        string? expectedBackendHash = Environment.GetEnvironmentVariable("PRO2_EXPECTED_VIIPER_SHA256");
        if (!string.IsNullOrWhiteSpace(expectedBackendHash))
        {
            using var embedded = typeof(MainWindow).Assembly.GetManifestResourceStream("Embedded.viiper.haptic.exe");
            check(embedded != null && string.Equals(Convert.ToHexString(SHA256.HashData(embedded!)),
                expectedBackendHash, StringComparison.OrdinalIgnoreCase),
                "release embeds exactly the newly fixed backend, not a stale VIIPER binary");
        }
    }
}
