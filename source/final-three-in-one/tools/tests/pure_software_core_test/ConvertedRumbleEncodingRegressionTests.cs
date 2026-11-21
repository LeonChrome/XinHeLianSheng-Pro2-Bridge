using System;
using System.Buffers.Binary;
using System.Linq;
using Y700Switch2V60Viiper;

internal static class ConvertedRumbleEncodingRegressionTests
{
    private const ulong AmplitudeMask = (1023UL << 10) | (1023UL << 30);
    public static void Run(Action<bool, string> check)
    {
        CheckSwitch1Stream(check);
        var random = new Random(261023);
        foreach (int sample in Enumerable.Range(0, 128))
        {
            var report = new byte[64]; random.NextBytes(report);
            report[0] = 2; report[1] = 0x73; report[17] = 0x79;
            var original = report.ToArray();
            foreach (double gain in new[] { 0.0, 0.1, 0.5, 1.0, 2.0, 3.0 })
            {
                var ble = new byte[33];
                check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(report, 0x1c, ble, out bool active, out _, gain),
                    "converted known-frame transport accepted");
                check(ble[0] == 0 && ble[1] == 0x7c && ble[17] == 0x7c,
                    "converted transport owns sequence only");
                bool expectedActive = false;
                for (int side = 1; side <= 17; side += 16)
                for (int frame = 0; frame < 3; frame++)
                {
                    int offset = side + 1 + frame * 5;
                    ulong before = Read(report, offset), after = Read(ble, offset);
                    check((before & ~AmplitudeMask) == (after & ~AmplitudeMask),
                        "all converted frequency and control bits remain in place");
                    foreach (int shift in new[] { 10, 30 })
                    {
                        int expected = (int)Math.Clamp(Math.Round(((before >> shift) & 1023) * gain,
                            MidpointRounding.AwayFromZero), 0, 1023);
                        check((int)((after >> shift) & 1023) == expected,
                            "converted gain scales packed amplitude exactly once");
                        expectedActive |= expected != 0;
                    }
                    if (gain == 1) check(before == after, "unity retains all six converted subframes");
                }
                check(active == expectedActive && original.SequenceEqual(report),
                    "converted active flag follows encoded data without mutating source");
            }
        }

        var payload = Pro2OutputPacketMapper.BuildRaw02Packet(160, 29000, 320, 29000,
            180, 9800, 400, 9800, "test");
        var final = Encode(payload, 1, check);
        check(FirstAmplitude(final, 2) == 453 && SecondAmplitude(final, 2) == 453,
            "unity keeps safe reference 453 instead of old second normalization to 1023");
        check(FirstAmplitude(final, 18) == 153 && SecondAmplitude(final, 18) == 153,
            "lower right amplitude remains independent at 153 not 345");
        final = Encode(payload, .1, check);
        check(FirstAmplitude(final, 2) == 45 && SecondAmplitude(final, 2) == 45,
            "ten percent uses 45 not old 102");
        check((Read(final, 2) & 1023) == 320 && ((Read(final, 2) >> 20) & 1023) == 160,
            "SDL high-first low-second ordering is unchanged by transport");
        var tiny = Pro2OutputPacketMapper.BuildRaw02Packet(160, 1, 320, 1, 160, 1, 320, 1, "test");
        check(!tiny.Active && !Pro2NativeRumbleEncoder.HasEffect(Encode(tiny, 1, check)),
            "sub-step amplitudes are classified from packed output");
        var saturated = Pro2OutputPacketMapper.BuildRaw02Packet(160, ushort.MaxValue, 320, ushort.MaxValue,
            160, 0, 320, 0, "test");
        check(FirstAmplitude(Encode(saturated, 1, check), 2) == 453,
            "over-range caller clamps before quantization instead of wrapping");

        for (int value = 0; value <= 255; value++)
        {
            var strong = Pro2OutputPacketMapper.BuildOrdinaryPacket(0, (byte)value, "ordinary");
            var weak = Pro2OutputPacketMapper.BuildOrdinaryPacket((byte)value, 0, "ordinary");
            var a = Encode(strong, 1, check); var b = Encode(weak, 1, check);
            int expected = (int)(((value * 29000u + 127u) / 255u) >> 6);
            check(FirstAmplitude(a, 2) == 0 && SecondAmplitude(a, 2) == expected &&
                FirstAmplitude(a, 18) == 0 && SecondAmplitude(a, 18) == 0,
                "ordinary strong motor stays on left low-frequency actuator " + value);
            check(FirstAmplitude(b, 18) == expected && SecondAmplitude(b, 18) == 0 &&
                FirstAmplitude(b, 2) == 0 && SecondAmplitude(b, 2) == 0,
                "ordinary weak motor stays on right high-frequency actuator " + value);
            check((Read(a, 2) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceHighControl &&
                ((Read(a, 2) >> 20) & 1023) == Pro2RumbleFrequencyPolicy.ReferenceLowControl,
                "ordinary intensity does not invent a frequency sweep " + value);
            check(!strong.PreserveNativePayload && !weak.PreserveNativePayload,
                "payload-safe conversion does not mislabel source as native");
        }
        check(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Xbox, new byte[] { 255, 0 },
            out var xbox, out _) && SecondAmplitude(Encode(xbox, 1, check), 2) == 453,
            "Xbox feedback strong/weak source ordering preserved");
        check(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.DualSenseLike, new byte[] { 0, 255 },
            out var ps5, out _) && SecondAmplitude(Encode(ps5, 1, check), 2) == 453,
            "DualSense feedback weak/strong source ordering preserved");

        // The compatibility helper still supports physical first-gen absolute tones.
        var quiet = Switch1RumbleConverter.Decode(new byte[] { 0, 3, 0xc0, 0x40 });
        ushort firstStep = (ushort)((514u * 29000u + 32767u) / 65535u);
        check(quiet.LowHz == 160 && quiet.HighHz == 320 &&
            quiet.LowAmplitude == firstStep && quiet.HighAmplitude == firstStep,
            "Pro1 first nonzero amplitude uses precise SDL inverse rather than rounded milli-units");
        byte[] source = [0xfc, 0xc9, 0xff, 0x71, 0, 1, 0x40, 0x40, 1, 0];
        var sourceLeft = Switch1RumbleConverter.Decode(source);
        check(sourceLeft.HighHz == 1253 && sourceLeft.LowHz == 626,
            "Pro1 source decoder retains frequency above final proxy limit");
        check(Switch1RumbleConverter.TryMap(source, out var mapped, out _) && mapped.FrequencyClamps == 0 &&
            mapped.ConversionTelemetry.Contains("pro2_log96"),
            "Pro1 haptics use native logarithmic pitch codes");
        check((Read(mapped.Report, 2) & 1023) == 574 && ((Read(mapped.Report, 2) >> 20) & 1023) == 670,
            "high and low source pitches above 511 Hz remain distinct and in the correct output bands");
        check(Pro2RumbleFrequencyPolicy.FromEstimatedHz(double.NaN) == 40 &&
            Pro2RumbleFrequencyPolicy.FromEstimatedHz(900) == 511 &&
            Pro2RumbleFrequencyPolicy.FromEstimatedHz(160.5) == 161,
            "shared proxy handles nonfinite, out-of-range and half-step inputs deterministically");

        var invalid = payload.Report.ToArray(); invalid[17] = 0x80;
        check(!Pro2BleRumblePacketEncoder.TryEncodeRaw02(invalid, 0, new byte[33], out _, out _),
            "converted unknown second motor format is rejected instead of guessed");
        check(!Pro2BleRumblePacketEncoder.TryEncodeRaw02(payload.Report.AsSpan(0, 63), 0, new byte[33], out _, out _) &&
            !Pro2BleRumblePacketEncoder.TryEncodeRaw02(payload.Report, 0, new byte[32], out _, out _),
            "converted short input and destination fail safely");
    }

    private static void CheckSwitch1Stream(Action<bool, string> check)
    {
        const uint neutral = 0x40400100;
        var stream = new Switch1RumbleStream();
        Pro2OutputPacket Send(uint left, uint right)
        {
            byte[] feedback = new byte[10];
            BinaryPrimitives.WriteUInt32LittleEndian(feedback, left);
            BinaryPrimitives.WriteUInt32LittleEndian(feedback.AsSpan(4), right);
            feedback[8] = 1;
            check(stream.TryMap(feedback, out var packet, out _), "stateful Pro1 feedback accepted");
            check(packet.NativePro1Rumble!.SequenceEqual(feedback[..8]) &&
                Pro1PhysicalRumble.ScaleNative(packet.NativePro1Rumble, 1).SequenceEqual(feedback[..8]),
                "physical Pro1 at 100% receives untouched original commands");
            return packet;
        }
        void Sample(Pro2OutputPacket packet, int motor, int slot, int lf, int la, int hf, int ha)
        {
            ulong value = Read(packet.Report, 2 + motor * 16 + slot * 5);
            check((int)(value & 1023) == lf && (int)((value >> 10) & 1023) == la &&
                  (int)((value >> 20) & 1023) == hf && (int)((value >> 30) & 1023) == ha,
                $"Pro1 pitch/level for motor {motor}, 5ms slot {slot} expected {lf},{la},{hf},{ha} got {value:x10}");
        }
        // Two equal half-level absolute tones. The wire oracle is low-first
        // at 160 Hz, high-second at 320 Hz, 501 linear steps after the cap.
        var seed = Send(0x6fc0bf00, neutral);
        check(seed.Report.AsSpan(2, 5).SequenceEqual(Convert.FromHexString("81D5175E7D")) &&
            seed.Report.AsSpan(18, 5).SequenceEqual(Convert.FromHexString("8101101E00")), "documented Pro2 wire vectors");
        var delta = Send(0x77700000, 0);
        Sample(delta, 0, 0, 385, 491, 484, 501);
        check(!stream.TryMap(new byte[10], out _, out _), "unflagged feedback produces no samples");
        var empty = Send(0, 0);
        check(empty.Report[1] == 0x40 && empty.Report[17] == 0x40, "empty source words do not add time or reset state");
        // Identical command a second time must advance from the first result.
        Sample(Send(0x77700000, neutral), 0, 0, 385, 480, 487, 501);
        stream = new();
        var split = Send(0x86000400, 0xc20c6001);
        check(split.Report[1] == 0x60 && split.Report[17] == 0x70, "two and three independent motor sample counts");
        Sample(split, 0, 0, 385, 501, 481, 0);
        Sample(split, 0, 1, 385, 0, 481, 1003);
        Sample(split, 1, 0, 385, 1003, 481, 0);
        Sample(split, 1, 1, 385, 1003, 481, 0);
        Sample(split, 1, 2, 385, 0, 481, 1003);
        var mixed = Send(0xaf8400c1, 0);
        check(mixed.Report[1] == 0x60 && mixed.Report[17] == 0x60, "empty peer holds previous tone through new samples");
        Sample(mixed, 0, 0, 385, 668, 577, 334);
        Sample(mixed, 0, 1, 385, 0, 481, 0);
        Sample(mixed, 1, 0, 385, 0, 481, 1003);
        Sample(mixed, 1, 1, 385, 0, 481, 1003);
        Send(0x6fc0bf00, neutral);
        var triple = Send(0x7063000e, neutral);
        check(triple.Report[1] == 0x70, "format-one single-field update carries three samples");
        Sample(triple, 0, 0, 481, 501, 481, 501);
        Sample(triple, 0, 1, 481, 501, 481, 501);
        Sample(triple, 0, 2, 385, 0, 481, 1003);
        stream = new();
        Sample(Send(0x77700000, neutral), 0, 0, 385, 0, 484, 0);
    }

    private static byte[] Encode(Pro2OutputPacket packet, double gain, Action<bool, string> check)
    {
        var output = new byte[33];
        check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(packet.Report, 7, output, out _, out _, gain),
            "mapped packet encodes");
        return output;
    }
    private static int FirstAmplitude(byte[] data, int offset) => (int)((Read(data, offset) >> 10) & 1023);
    private static int SecondAmplitude(byte[] data, int offset) => (int)((Read(data, offset) >> 30) & 1023);
    private static ulong Read(byte[] data, int offset)
    {
        ulong value = 0;
        for (int i = 0; i < 5; i++) value |= (ulong)data[offset + i] << (8 * i);
        return value;
    }
}
