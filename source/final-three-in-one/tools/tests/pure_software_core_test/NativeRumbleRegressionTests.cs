using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Y700Switch2V60Viiper;

internal static class NativeRumbleRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var random = new Random(1324);
        var ble = new byte[33];
        const ulong ampMask = (0x3ffUL << 10) | (0x3ffUL << 30);
        for (int sample = 0; sample < 256; sample++)
        {
            var report = new byte[64]; random.NextBytes(report);
            report[0] = 2; report[1] = (byte)(0x50 | sample % 16);
            report[17] = (byte)(0x50 | (sample + 7) % 16);
            byte[] original = report.ToArray();
            check(Pro2NativeRumbleEncoder.TryEncode(report, ble, 1, out _, out _), "native unity accepted");
            check(ble[0] == 0 && report.AsSpan(1, 32).SequenceEqual(ble.AsSpan(1)),
                "unity preserves both headers and all six subframes byte for byte");
            foreach (double gain in new[] { 0.0, 0.1, 0.5, 2.0, 3.0 })
            {
                check(Pro2NativeRumbleEncoder.TryEncode(report, ble, gain, out bool active, out _), "native gain accepted");
                check(ble[1] == report[1] && ble[17] == report[17], "gain preserves independent sequence/control bytes");
                for (int side = 1; side <= 17; side += 16)
                for (int frame = 0; frame < 3; frame++)
                {
                    int offset = side + 1 + 5 * frame;
                    ulong input = Read(report, offset), output = Read(ble, offset);
                    check((input & ~ampMask) == (output & ~ampMask), "gain never changes frequency/control bits");
                    foreach (int shift in new[] { 10, 30 })
                    {
                        int expected = (int)Math.Min(1023, Math.Floor(((input >> shift) & 1023) * gain + 0.5));
                        check((int)((output >> shift) & 1023) == expected, "gain scales packed amplitude without renormalization");
                    }
                }
                if (gain == 0) check(!active, "mute clears every subframe on both motors");
                check(report.SequenceEqual(original), "encoder never mutates host buffer");
            }
        }

        byte[] fixture = new byte[64]; fixture[0] = 2; fixture[1] = 0x5a; fixture[17] = 0x53;
        // Independent wire oracle: 10-bit amplitude 453, not 16-bit full-scale 29000.
        Write(fixture, 2, 391UL | (453UL << 10) | (274UL << 20) | (226UL << 30));
        check(Pro2NativeRumbleEncoder.TryEncode(fixture, ble, 1, out _, out _) &&
            ((Read(ble, 2) >> 10) & 1023) == 453, "native 100% does not amplify 453 to 1023");
        check(Pro2NativeRumbleEncoder.TryEncode(fixture, ble, 0.1, out _, out _) &&
            ((Read(ble, 2) >> 10) & 1023) == 45, "native 10% is 45 not old 102");
        var oldBle = new byte[33];
        check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(fixture, 0, oldBle, out _, out _) &&
            oldBle[1] == 0x50 && oldBle[17] == 0x50 &&
            Read(oldBle, 2) == Read(fixture, 2) && Read(oldBle, 18) == Read(fixture, 18),
            "converted encoder preserves amplitude/frequency layout and owns only sequence nibbles");
        // First slot silent, later slot active: must not be dropped or called neutral.
        Array.Clear(fixture, 2, 5); Write(fixture, 12, 200UL << 30);
        var feedback = new byte[34]; fixture.AsSpan(1, 32).CopyTo(feedback); feedback[32] = 1;
        check(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Pro2, feedback, out var packet, out _) &&
            packet.PreserveNativePayload && packet.Active, "native mode tags full report; third slot alone is active");
        check(Pro2NativeRumbleEncoder.TryEncode(packet.Report, ble, 1, out bool activeLater, out _) && activeLater &&
            feedback.AsSpan(0, 32).SequenceEqual(ble.AsSpan(1)), "VIIPER feedback to BLE retains all 32 bytes");
        feedback[32] = 2;
        check(!Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Pro2, feedback, out _, out _), "LED-only never becomes rumble");
        fixture[1] = 0x7a;
        check(Pro2NativeRumbleEncoder.TryEncode(fixture, ble, 1, out _, out _) &&
            fixture.AsSpan(1, 32).SequenceEqual(ble.AsSpan(1)), "unknown format remains opaque at unity");
        check(!Pro2NativeRumbleEncoder.TryEncode(fixture, ble, 0.5, out _, out _), "unknown non-unity format is not guessed");
        check(Pro2NativeRumbleEncoder.TryEncode(fixture, ble, 0, out var muteActive, out _) && !muteActive &&
            !Pro2NativeRumbleEncoder.HasEffect(ble), "unknown format can still be explicitly muted");
        check(!Pro2NativeRumbleEncoder.TryEncode(new byte[63], ble, 1, out _, out _), "short native input rejected");
        check(!Pro2NativeRumbleEncoder.TryEncode(fixture, new byte[32], 1, out _, out _), "short native destination rejected");
        check(!Pro2OutputPacketMapper.BuildOrdinaryPacket(100, 150, "xbox").PreserveNativePayload,
            "synthesized PS5/Xbox path stays separate");

        QueueTests(check);
        string? directory = Environment.GetEnvironmentVariable("PRO2_NATIVE_FIXTURE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        foreach (string path in Directory.GetFiles(directory, "*-feedback.bin"))
        {
            byte[] wire = File.ReadAllBytes(path);
            byte[] usb = File.ReadAllBytes(path.Replace("-feedback.bin", "-usb.bin"));
            check(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Pro2, wire, out var mapped, out _) &&
                mapped.PreserveNativePayload, "actual Go output selects native encoding: " + Path.GetFileName(path));
            check(Pro2NativeRumbleEncoder.TryEncode(mapped.Report, ble, 1, out _, out _) &&
                usb.AsSpan(1, 32).SequenceEqual(ble.AsSpan(1)), "Go USB-to-feedback-to-BLE exact payload equality");
        }
    }

    private static void QueueTests(Action<bool, string> check)
    {
        var queue = new Pro2RumbleOutputQueue();
        long Tick(double ms) => 1 + (long)(ms * Stopwatch.Frequency / 1000);
        byte[] data = new byte[33]; data[1] = data[17] = 0x50;
        for (int i = 0; i < 8; i++) { data[2] = (byte)i; queue.Enqueue(data, true, Tick(i)); }
        for (int i = 0; i < 8; i++)
            check(queue.TryDequeue(Tick(10), out var item) && item!.Data[2] == i, "FIFO owns buffers and keeps every native edge");
        check(queue.Count == 0 && queue.Replaced == 0, "native drain has no latest-state merges");
        // Source cadence of 12 ms and BLE service at 7.5 ms for two simulated minutes.
        queue.Reset(); int sent = 0, delivered = 0;
        for (int time = 0; time < 240000; time++)
        {
            if (time % 24 == 0) { data[2] = (byte)(sent++ % 256); queue.Enqueue(data, true, Tick(time / 2.0)); }
            if (time % 15 == 0 && queue.TryDequeue(Tick(time / 2.0), out var item))
                check(item!.Data[2] == (byte)(delivered++ % 256), "steady native order");
        }
        while (queue.TryDequeue(Tick(120001), out var tail))
            check(tail!.Data[2] == (byte)(delivered++ % 256), "steady native tail order");
        check(delivered == sent && queue.OverflowDropped == 0 && queue.ExpiredDropped == 0,
            "two-minute simulated 83Hz producer / 133Hz BLE service has zero packet loss");
        queue.Reset();
        data[3] |= 0x04; // nonzero amplitude: this fixture tests expired impacts, not 40 explicit stops
        for (int i = 0; i < 40; i++) queue.Enqueue(data, true, Tick(0));
        check(queue.Count == 32 && queue.OverflowDropped == 8, "overload bounded and disclosed");
        check(queue.TryDequeue(Tick(500), out var expired) && !Pro2NativeRumbleEncoder.HasEffect(expired!.Data) &&
            queue.Count == 0 && queue.ExpiredDropped == 32 && queue.ExpiryStops == 1,
            "stalled writer sends neutral rather than replaying impacts or losing final stop");
        queue.Reset();
        queue.Enqueue(data, true, Tick(0)); queue.Enqueue(data, true, Tick(1));
        var stop = Pro2NativeRumbleEncoder.Neutralize(data);
        queue.Enqueue(stop, false, Tick(2));
        check(queue.Count == 1 && queue.ModeDiscarded == 2 && queue.TryDequeue(Tick(3), out var changed) &&
            !changed!.Native && changed.Data.SequenceEqual(stop), "mode-switch stop clears pending native effects");
        queue.Enqueue(data, false, Tick(4)); queue.Enqueue(data, false, Tick(5));
        check(queue.Count == 2 && queue.Replaced == 0 &&
            queue.TryDequeue(Tick(6), out _) && queue.TryDequeue(Tick(7), out _),
            "converted rumble keeps both events when service is not overloaded");
        queue.Reset();
        var native = new byte[64]; native[0] = 2; native[1] = native[17] = 0x50;
        Write(native, 2, 453UL << 10);
        check(queue.TryEnqueueNative(native, 0.5, Tick(6), out _, out _) &&
            queue.TryDequeue(Tick(7), out var running) && Pro2NativeRumbleEncoder.HasEffect(running!.Data),
            "native production enqueue can deliver an active effect before unsupported input");
        queue.TryEnqueueNative(native, 0.5, Tick(8), out _, out _);
        queue.TryEnqueueNative(native, 0.5, Tick(9), out _, out _);
        native[1] = 0x70;
        check(!queue.TryEnqueueNative(native, 0.5, Tick(10), out _, out var error) &&
            error.Contains("safety_stop_queued=true"), "unknown scaled report explicitly reports safe fallback");
        check(queue.Count == 1 && queue.SafetyStops == 1 && queue.SafetyDiscarded == 2 &&
            queue.TryDequeue(Tick(11), out var safety) && !Pro2NativeRumbleEncoder.HasEffect(safety!.Data),
            "unknown scaled update clears all queued impacts and sends neutral instead of leaving old rumble");
        check(queue.TryEnqueueNative(native, 1, Tick(12), out _, out _) &&
            queue.TryDequeue(Tick(13), out var opaque) &&
            native.AsSpan(1, 32).SequenceEqual(opaque!.Data.AsSpan(1)),
            "100% still forwards opaque payload after a safety stop");
        queue.Reset(); check(queue.Count == 0 && queue.ExpiryStops == 0, "reconnection clears queue and counters");
        check(queue.SafetyStops == 0 && queue.SafetyDiscarded == 0, "reconnection clears safety counters");
    }

    private static ulong Read(byte[] data, int offset) =>
        (ulong)data[offset] | ((ulong)data[offset + 1] << 8) | ((ulong)data[offset + 2] << 16) |
        ((ulong)data[offset + 3] << 24) | ((ulong)data[offset + 4] << 32);
    private static void Write(byte[] data, int offset, ulong value)
    { for (int i = 0; i < 5; i++) data[offset + i] = (byte)(value >> (8 * i)); }
}
