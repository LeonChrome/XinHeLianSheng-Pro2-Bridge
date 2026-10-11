using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class QualityRegressionTests
{
    private static Action<bool, string> check = null!;
    private static readonly List<object> measurements = new();
    private static long Tick(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);
    private static GamepadState Motion(short x = 0, short y = 0, short z = 0,
        short ax = 0, short ay = 0, short az = 4096)
    {
        var state = GamepadState.Neutral();
        state.GyroValid = state.AccelValid = true;
        state.GyroX = x; state.GyroY = y; state.GyroZ = z;
        state.AccelX = ax; state.AccelY = ay; state.AccelZ = az;
        return state;
    }
    public static void Run(Action<bool, string> assertion, string output)
    {
        check = assertion; measurements.Clear();
        check(Pro2QualityMode.ResolveEnabled([], Pro2QualityMode.GyroArgument, Pro2QualityMode.DisableGyroArgument, true),
            "release gyro default on without arguments");
        check(Pro2QualityMode.ResolveEnabled([], Pro2QualityMode.HapticsArgument, Pro2QualityMode.DisableHapticsArgument, true),
            "release haptics default on without arguments");
        check(!Pro2QualityMode.ResolveEnabled([], Pro2QualityMode.GyroArgument, Pro2QualityMode.DisableGyroArgument, false),
            "non-release gyro remains opt-in");
        check(!Pro2QualityMode.ResolveEnabled([Pro2QualityMode.GyroArgument, Pro2QualityMode.DisableGyroArgument],
            Pro2QualityMode.GyroArgument, Pro2QualityMode.DisableGyroArgument, true), "gyro off overrides default and opt-in");
        check(!Pro2QualityMode.ResolveEnabled([Pro2QualityMode.DisableHapticsArgument],
            Pro2QualityMode.HapticsArgument, Pro2QualityMode.DisableHapticsArgument, true), "haptics off overrides release default");
        check(Pro2QualityMode.ResolveEnabled(["--GYRO-QUALITY-TEST"],
            Pro2QualityMode.GyroArgument, Pro2QualityMode.DisableGyroArgument, false), "legacy quality opt-in preserved");
        check(Pro2QualityMode.IsEnabled(new[] { "--GYRO-QUALITY-TEST" }, Pro2QualityMode.GyroArgument), "quality case insensitive");
        check(!Pro2QualityMode.IsEnabled(new[] { Pro2QualityMode.GyroArgument }, Pro2QualityMode.HapticsArgument), "independent quality flags");
        check(new Pro2HidReportParser().GyroCalibrationSummary.Contains("mode=quality") == Pro2QualityMode.GyroEnabled,
            "actual parser honors process gyro flag");
        Gyro(); Haptics();
        File.WriteAllText(Path.Combine(output, "quality-measurements.json"),
            JsonSerializer.Serialize(new { scope = "synthetic offline fixtures; NOT physical angle or motor validation", measurements },
                new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Gyro()
    {
        var learned = new Pro2GyroBiasCalibration();
        for (int i = 0; i < 64; i++) learned.Apply(Motion(5, -3, 2), Tick(i * .0075));
        check(!learned.Calibrated, "64 packets do not mean two seconds of stillness");
        for (int i = 64; i < 410; i++) learned.Apply(Motion((short)(5 + (i % 3 - 1)), -3, 2), Tick(i * .0075));
        check(learned.Calibrated && Math.Abs(learned.BiasX - 5) < .03 && learned.BiasY == -3, "stationary offset learned after time gate");
        var lowMotion = Motion(6, -3, 2); learned.Apply(lowMotion, Tick(4));
        check(lowMotion.GyroX == 1 && lowMotion.GyroY == 0, "one raw unit movement preserved without deadzone");
        double bx = learned.BiasX;
        for (int i = 0; i < 500; i++) learned.Apply(Motion(15, -3, 2), Tick(4 + i * .0075));
        check(learned.BiasX == bx, "no continuous gameplay zero drift learning");
        var fast = Motion(1000, -1000, 1234); learned.Apply(fast, Tick(8));
        check(fast.GyroX == 995 && fast.GyroY == -997 && fast.GyroZ == 1232, "fast three-axis signs and scale unchanged");
        learned.StartManualCalibration();
        for (int i = 0; i < 200; i++) learned.Apply(Motion(5, -3, 2), Tick(10 + i * .0075));
        check(learned.LastResult.StartsWith("manual_collecting"), "200 frames do not prematurely commit a three-second calibration");
        for (int i = 200; i <= 401; i++) learned.Apply(Motion(5, -3, 2), Tick(10 + i * .0075));
        check(learned.LastResult == "manual_committed_3s", "manual commits by three seconds, not frame count");
        learned.StartManualCalibration(); learned.Apply(Motion(600), Tick(15));
        check(learned.LastResult == "manual_rejected_motion" && learned.Calibrated && learned.BiasX == 5,
            "moving manual calibration rejected while preserving old bias");
        var saturated = Motion(short.MinValue); learned.Apply(saturated, Tick(16));
        check(saturated.GyroX == short.MinValue, "corrected sensor saturation clamped safely");

        var slow = new Pro2GyroBiasCalibration();
        for (int i = 0; i < 800; i++) slow.Apply(Motion(z: 96), Tick(i * .0075));
        var final = Motion(z: 96); slow.Apply(final, Tick(7));
        check(!slow.Calibrated && final.GyroZ == 96, "constant 5.86 dps yaw is never learned as zero bias");
        var old = new Pro2HidReportParser(false); var fd2 = new byte[63];
        fd2[11] = fd2[14] = 8; fd2[12] = fd2[15] = 128;
        BinaryPrimitives.WriteInt16LittleEndian(fd2.AsSpan(52), 4096);
        BinaryPrimitives.WriteInt16LittleEndian(fd2.AsSpan(58), 96);
        GamepadState oldState = null!;
        for (int i = 0; i < 80; i++) check(old.TryParseFd2Payload(fd2, out oldState, out _), "legacy fixture parses " + i);
        check(oldState.GyroZ == 0, "baseline reproduces erroneous slow-yaw zero learning");
        var newParser = new Pro2HidReportParser(true);
        GamepadState parsed = null!;
        for (int i = 0; i < 80; i++) check(newParser.TryParseFd2Payload(fd2, out parsed, out _), "quality fixture parses " + i);
        check(parsed.GyroZ == 96 && parsed.SwitchRawImuSamples.Length == 1,
            "actual FD2 quality parser preserves slow motion and single source IMU sample");
        foreach (var profile in new[] { ViiperDeviceProfile.DualSenseLike, ViiperDeviceProfile.DualSenseProfessionalImuTest })
        {
            var axes = Motion(100, 200, 300, 1000, 2000, 3000);
            byte[] wire = VirtualPadPackets.FromGamepad(profile, axes);
            short Read(int index) => BinaryPrimitives.ReadInt16LittleEndian(wire.AsSpan(index, 2));
            check(Read(21) == 100 && Read(23) == 300 && Read(25) == -200,
                "PS5 gyro axis +X,+Z,-Y " + profile.Mode);
            check(Read(27) == 2000 && Read(29) == 6000 && Read(31) == -4000,
                "PS5 accel axes and two-times raw unit scaling " + profile.Mode);
        }
        measurements.Add(new { test = "constant_yaw", raw_input = 96, input_dps = 96 / 16.384, legacy_output = oldState.GyroZ, quality_output = final.GyroZ });

        var duplicate = new Pro2GyroBiasCalibration();
        for (int i = 0; i < 1000; i++) duplicate.Apply(Motion(), Tick(1));
        check(!duplicate.Calibrated && duplicate.WindowSeconds == 0, "batched duplicate timestamps cannot fabricate stillness duration");
        for (int i = 0; i < 200; i++) duplicate.Apply(Motion(), Tick(2 + i * .0075));
        check(!duplicate.Calibrated, "a transport gap restarts stillness window");
        var gravity = new Pro2GyroBiasCalibration();
        for (int i = 0; i < 600; i++)
        {
            short ax = (short)(120 * Math.Sin(i * .02));
            gravity.Apply(Motion(ax: ax, az: (short)Math.Sqrt(4096.0 * 4096 - ax * ax)), Tick(i * .0075));
        }
        check(!gravity.Calibrated, "gravity direction change rejected even with constant norm");
        var zero = new Pro2GyroBiasCalibration();
        for (int i = 0; i < 300; i++) zero.Apply(Motion(), Tick(i * .0075));
        double angle = 0;
        for (int i = 0; i < 1000; i++)
        {
            var state = Motion(z: 1475); zero.Apply(state, Tick(4 + i * .001));
            angle += state.GyroZ / 16.384 * .001;
        }
        check(Math.Abs(angle - 90) < .04, "synthetic ninety-degree fixture keeps gain; not a physical angle measurement");
        measurements.Add(new { test = "synthetic_90_degree_gain", integrated_degrees = angle, physical_validation = false });
    }

    private readonly record struct Tone(int LowFrequency, int LowAmplitude, int HighFrequency, int HighAmplitude);
    private static Tone Decode(Pro2OutputPacket p, bool right = false)
    {
        var f = p.Report.AsSpan(right ? 18 : 2, 5);
        return new((f[2] >> 4) | ((f[3] & 63) << 4), (f[3] & 192) | (f[4] << 8),
            f[0] | ((f[1] & 3) << 8), ((f[1] & 252) << 4) | ((f[2] & 15) << 12));
    }
    private static byte[] Wave(int start, int frames, double lf, double la, double rf = 0, double ra = 0,
        double leftHigh = 0, double leftHighAmp = 0, bool speakers = false)
    {
        var data = new byte[frames * 8];
        for (int i = 0; i < frames; i++)
        {
            double t = (start + i) / 48000.0;
            short l = (short)Math.Clamp(Math.Round(la * Math.Sin(2 * Math.PI * lf * t) +
                leftHighAmp * Math.Sin(2 * Math.PI * leftHigh * t)), short.MinValue, short.MaxValue);
            short r = (short)Math.Clamp(Math.Round(ra * Math.Sin(2 * Math.PI * rf * t)), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 8 + (speakers ? 0 : 4)), l);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 8 + (speakers ? 2 : 6)), r);
        }
        return data;
    }
    private static (Pro2OutputPacket? Last, int FirstFrame, bool AnyActive) Feed(DualSenseHapticAudioProcessor processor,
        double lf, double la, double rf = 0, double ra = 0, int frames = 9600, int chunk = 48,
        double leftHigh = 0, double leftHighAmp = 0, bool speakers = false)
    {
        Pro2OutputPacket? last = null; int first = -1; bool active = false;
        for (int i = 0; i < frames; i += chunk)
        {
            int length = Math.Min(chunk, frames - i);
            if (!processor.TryProcess(Wave(i, length, lf, la, rf, ra, leftHigh, leftHighAmp, speakers), out var packet, out _)) continue;
            last = packet;
            if (packet.Active) { active = true; if (first < 0) first = i + length; }
        }
        return (last, first, active);
    }
    private static void Haptics()
    {
        foreach (double frequency in new double[] { 110, 160, 180, 237, 310, 400, 420, 490 })
        {
            bool high = frequency >= 250;
            var processor = new DualSenseHapticAudioProcessor(true);
            var result = Feed(processor, high ? 0 : frequency, high ? 0 : 10000,
                high ? frequency : 0, high ? 10000 : 0);
            check(result.Last?.Active == true, "quality sine active " + frequency);
            var tone = Decode(result.Last!, high);
            int estimate = high ? tone.HighFrequency : tone.LowFrequency;
            int amp = high ? tone.HighAmplitude : tone.LowAmplitude;
            measurements.Add(new { test = "single_tone", input_hz = frequency, frequency_field = estimate, amplitude_field = amp, telemetry = processor.ConversionTelemetry });
            Console.WriteLine($"QUALITY tone {frequency} Hz: estimated field {estimate}; amp {amp}");
            check(Math.Abs(estimate - frequency) <= 5, "sub-bin frequency estimate " + frequency);
            var other = Decode(result.Last!, !high);
            check(amp > 0 && other.LowAmplitude == 0 && other.HighAmplitude == 0, "actuator separation " + frequency);
            int unwanted = high ? tone.LowAmplitude : tone.HighAmplitude;
            check(unwanted <= amp * .02, "single tone does not create a second strong oscillator " + frequency);
        }
        var legacy = Feed(new DualSenseHapticAudioProcessor(), 160, 10000);
        var quality = Feed(new DualSenseHapticAudioProcessor(true), 160, 10000);
        int oldF = Decode(legacy.Last!).LowFrequency, newF = Decode(quality.Last!).LowFrequency;
        check(Math.Abs(newF - 160) < Math.Abs(oldF - 160), "off-bin sine frequency error improves relative to baseline");
        measurements.Add(new { test = "160_hz_before_after", legacy_frequency_field = oldF, quality_frequency_field = newF,
            legacy_first_output_ms = legacy.FirstFrame / 48.0, quality_first_output_ms = quality.FirstFrame / 48.0 });
        check(quality.FirstFrame <= 576 && quality.FirstFrame < legacy.FirstFrame, "first spectral output within twelve milliseconds");
        var oldWeak = Feed(new DualSenseHapticAudioProcessor(), 160, 500);
        var weak = Feed(new DualSenseHapticAudioProcessor(true), 160, 500);
        check(!oldWeak.AnyActive && weak.AnyActive && weak.Last?.Active == true, "weak actuator tone survives without increasing the maximum gain");
        measurements.Add(new { test = "weak_500_peak", legacy_active = oldWeak.AnyActive, quality_active = weak.AnyActive, quality_amplitude = Decode(weak.Last!).LowAmplitude });
        var alias = Feed(new DualSenseHapticAudioProcessor(true), 3150, 12000);
        check(alias.Last == null || !alias.Last.Active, "3150 Hz out-of-band tone is not folded into sustained 150 Hz rumble");
        check(!Feed(new DualSenseHapticAudioProcessor(true), 160, 10000, 400, 10000, speakers: true).AnyActive,
            "quality converter ignores the two speaker channels");
        var pair = Feed(new DualSenseHapticAudioProcessor(true), 160, 7000, 420, 9000, leftHigh: 400, leftHighAmp: 6000);
        var left = Decode(pair.Last!); var right = Decode(pair.Last!, true);
        check(Math.Abs(left.LowFrequency - 160) <= 5 && Math.Abs(left.HighFrequency - 400) <= 5 &&
            Math.Abs(right.HighFrequency - 420) <= 5, "independent dual-tone left and right frequency estimates");
        check(left.LowAmplitude > 0 && left.HighAmplitude > 0 && right.LowAmplitude == 0, "left dual bands do not create a right low band");
        var ble = new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
        check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(pair.Last!.Report, 7, ble, out bool bleActive, out _) && bleActive,
            "quality output accepted by unchanged physical BLE encoder");
        ulong Group(int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 5; i++) value |= (ulong)ble[offset + i] << (8 * i);
            return value;
        }
        ulong lb = Group(2), rb = Group(18);
        check((lb & 511) == (ulong)left.LowFrequency && ((lb >> 20) & 511) == (ulong)left.HighFrequency &&
            ((rb >> 20) & 511) == (ulong)right.HighFrequency, "tone frequency fields survive HID-to-BLE packing");
        check(((lb >> 10) & 1023) > 0 && ((lb >> 30) & 1023) > 0 && ((rb >> 10) & 1023) == 0,
            "independent amplitude bands survive physical BLE packing");
        var scheduler = new DualSenseHapticRumbleScheduler(true);
        bool schedulerActive = false;
        for (int i = 0; i < 100; i++)
        {
            var frame = new byte[388]; frame[0] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), 384);
            Wave(i * 48, 48, 160, 10000).CopyTo(frame, 4);
            if (scheduler.TryProcess(frame, out var p, out _, out _)) schedulerActive |= p.Active;
        }
        check(schedulerActive && scheduler.HasNativeAudio && scheduler.ConversionTelemetry.Contains("processor=quality_stft"),
            "actual VIIPER audio scheduler routes to quality converter");
        foreach (int chunk in new[] { 24, 96, 192 })
        {
            var chunked = Feed(new DualSenseHapticAudioProcessor(true), 160, 10000, chunk: chunk);
            var tone = Decode(chunked.Last!);
            check(Math.Abs(tone.LowFrequency - newF) <= 2 && Math.Abs(tone.LowAmplitude - Decode(quality.Last!).LowAmplitude) < 512,
                "PCM fragmentation preserves spectrum " + chunk);
        }
        int previous = 0;
        foreach (double amplitude in new double[] { 500, 1000, 3000, 10000, 32767 })
        {
            var signal = Feed(new DualSenseHapticAudioProcessor(true), 160, amplitude);
            int amp = Decode(signal.Last!).LowAmplitude;
            check(amp > previous && amp <= 29000, "amplitude monotonic and capped " + amplitude); previous = amp;
        }
        var used = new DualSenseHapticAudioProcessor(true); Feed(used, 160, 10000); used.ResetOutputState();
        var fresh = new DualSenseHapticAudioProcessor(true);
        for (int i = 0; i < 120; i++)
        {
            var pcm = Wave(i * 48, 48, 0, 0, 420, 10000);
            bool a = used.TryProcess(pcm, out var p, out _), c = fresh.TryProcess(pcm, out var q, out _);
            check(a == c && (!a || p.Report.SequenceEqual(q.Report)), "quality DSP reset " + i);
        }
        check(!fresh.TryProcess(new byte[9], out _, out _), "quality malformed PCM rejected");
        bool stop = false; int stopFrame = 0;
        for (int i = 0; i < 100; i++)
            if (fresh.TryProcess(new byte[384], out var p, out _) && !p.Active) { stop = true; stopFrame = i + 1; break; }
        check(stop && stopFrame <= 80, "quality silence releases instead of holding an old tone");
        var burst = new DualSenseHapticAudioProcessor(true); bool burstActive = false;
        for (int i = 0; i < 35; i++)
        {
            var data = i < 2 ? Wave(i * 48, 48, 160, 15000) : new byte[384];
            if (burst.TryProcess(data, out var p, out _)) burstActive |= p.Active;
        }
        check(burstActive, "two-millisecond actuator transient is not entirely discarded");
        var dc = new DualSenseHapticAudioProcessor(true); Pro2OutputPacket? lastDc = null;
        for (int i = 0; i < 200; i++)
        {
            var data = new byte[384];
            for (int f = 0; f < 48; f++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(f * 8 + 4), 12000);
            if (dc.TryProcess(data, out var p, out _)) lastDc = p;
        }
        check(lastDc == null || !lastDc.Active, "DC offset does not hold a false vibration");
        var batched = new DualSenseHapticAudioProcessor(true); Feed(batched, 160, 10000);
        check(batched.TryProcess(Wave(0, 4800, 220, 10000), out var latest, out _) &&
            Math.Abs(Decode(latest).LowFrequency - 220) <= 5, "large batch analyzes latest waveform instead of stale first window");
        byte[] benchmarkPcm = Wave(0, 48, 160, 10000, 420, 10000);
        var benchmark = new DualSenseHapticAudioProcessor(true); var watch = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++) benchmark.TryProcess(benchmarkPcm, out _, out _);
        watch.Stop();
        measurements.Add(new { test = "dsp_cpu_budget", simulated_audio_seconds = 10, elapsed_ms = watch.Elapsed.TotalMilliseconds,
            note = "in-process single-thread wall time, NOT transport latency; repeated fixture" });
        Console.WriteLine($"QUALITY DSP: 10 seconds PCM in {watch.Elapsed.TotalMilliseconds:F1} ms (offline)");
    }
}
