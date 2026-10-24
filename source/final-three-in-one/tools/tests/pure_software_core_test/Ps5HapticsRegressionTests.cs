using System;
using System.Buffers.Binary;
using System.Diagnostics;
using Y700Switch2V60Viiper;

internal static class Ps5HapticsRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        IdleAndTransitions(check);
        SpectralRange(check);
    }

    private static byte[] Wire(DualSenseHapticFrameKind kind, byte[] payload)
    {
        byte[] wire = new byte[DualSenseHapticFrame.WireSize];
        wire[0] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(wire, 4);
        return wire;
    }

    private static byte[] Hid(byte flags = 0, byte weak = 0, byte strong = 0)
    {
        byte[] report = new byte[48];
        report[0] = 2; report[1] = flags; report[3] = weak; report[4] = strong;
        report[2] = 4; report[45] = 22; // continuous LED traffic is not PCM.
        return Wire(DualSenseHapticFrameKind.HidOutput, report);
    }

    private static byte[] Wave(int first, int frames, double frequency, double peak,
        double secondFrequency = 0, double secondPeak = 0, bool right = false)
    {
        byte[] pcm = new byte[frames * 8];
        for (int i = 0; i < frames; i++)
        {
            double t = (first + i) / 48000.0;
            short value = (short)Math.Clamp(Math.Round(peak * Math.Sin(2 * Math.PI * frequency * t) +
                secondPeak * Math.Sin(2 * Math.PI * secondFrequency * t)), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 8 + (right ? 6 : 4)), value);
        }
        return pcm;
    }

    private static void IdleAndTransitions(Action<bool, string> check)
    {
        long ticks = 0;
        var scheduler = new DualSenseHapticRumbleScheduler(true, () => ticks);
        scheduler.TryProcess(Hid(), out _, out _, out _);
        for (int i = 0; i < 100; i++)
        {
            ticks = (long)((i + 1) * .001 * Stopwatch.Frequency);
            scheduler.TryProcess(Wire(DualSenseHapticFrameKind.AudioPcm, Wave(i * 48, 48, 160, 10000)),
                out _, out _, out _);
        }
        check(scheduler.NativeOutputActive && scheduler.HasNativeAudio && scheduler.AudioFrames == 100,
            "PS5 PCM source becomes active without Steam");
        long lastPcm = ticks;
        int stops = 0; double firstStop = -1;
        for (int i = 1; i <= 80; i++)
        {
            ticks = lastPcm + (long)(i * .005 * Stopwatch.Frequency);
            // The feedback loop performs this check on every pass, not only
            // when the combined HID/PCM stream times out.
            if (scheduler.TryStopIdle(out var stop))
            {
                stops++; firstStop = i * 5;
                check(!stop.Active && stop.Source == "dualsense-hd-idle-stop", "PS5 PCM idle emits neutral");
            }
            scheduler.TryProcess(Hid(), out _, out _, out _);
        }
        check(stops == 1 && firstStop >= 120 && firstStop <= 125 && !scheduler.NativeOutputActive,
            "400ms continuous unrelated HID cannot starve PCM stop");
        check(scheduler.HidFrames == 81 && scheduler.IdleStops == 1,
            "PS5 independent source/stop telemetry counts actual messages");
        check(!scheduler.TryStopIdle(out _) && !scheduler.TryStop("exit", out _), "PS5 stop is idempotent");

        for (int i = 0; i < 80; i++)
        {
            ticks += Stopwatch.Frequency / 1000;
            scheduler.TryProcess(Wire(DualSenseHapticFrameKind.AudioPcm, Wave(i * 48, 48, 160, 10000)), out _, out _, out _);
        }
        check(scheduler.TryProcess(Hid(3, 100, 150), out var ordinary, out _, out _) && ordinary.Active &&
            scheduler.CompatibilityMode && !scheduler.NativeOutputActive, "PCM to ordinary clears old PCM state");
        check(!scheduler.TryProcess(Wire(DualSenseHapticFrameKind.AudioPcm, Wave(0, 48, 160, 10000)),
            out _, out _, out _) && scheduler.BlockedAudioFrames == 1, "ordinary mode blocks stale PCM packets");
        check(scheduler.TryStop("stream-lost", out var forced) && !forced.Active && forced.Source == "stream-lost",
            "stream loss explicitly stops ordinary output and clears DSP");
        check(!scheduler.TryStop("exit", out _), "subsequent shutdown cannot replay stopped output");
        check(scheduler.TryProcess(Hid(), out var transition, out _, out _) && !transition.Active,
            "ordinary to PCM transition starts neutral");
        scheduler.TryProcess(Wire(DualSenseHapticFrameKind.AudioPcm, new byte[7]), out _, out _, out _);
        check(scheduler.AudioAgeMilliseconds < 0 && !scheduler.NativeOutputActive,
            "malformed PCM cannot renew the independent audio clock");
        var startedAtZero = new DualSenseHapticRumbleScheduler(true, () => 0);
        startedAtZero.TryProcess(Wire(DualSenseHapticFrameKind.AudioPcm, Wave(0, 48, 160, 10000)), out _, out _, out _);
        check(startedAtZero.AudioAgeMilliseconds == 0, "zero monotonic clock value is a valid PCM timestamp");
    }

    private readonly record struct Tones(int FirstFrequency, int FirstAmplitude, int SecondFrequency, int SecondAmplitude);
    private static Tones Decode(Pro2OutputPacket packet, bool right = false)
    {
        ReadOnlySpan<byte> data = packet.Report.AsSpan(right ? 18 : 2, 5);
        return new((data[2] >> 4) | ((data[3] & 63) << 4), (data[3] & 192) | (data[4] << 8),
            data[0] | ((data[1] & 3) << 8), ((data[1] & 252) << 4) | ((data[2] & 15) << 12));
    }

    private static (Pro2OutputPacket Packet, string Telemetry, int FirstActiveFrame) Feed(double frequency,
        double peak, double secondFrequency = 0, double secondPeak = 0, bool right = false, int chunk = 48)
    {
        var processor = new DualSenseHapticAudioProcessor(true);
        Pro2OutputPacket packet = null!;
        int firstActive = -1;
        for (int i = 0; i < 14400; i += chunk)
            if (processor.TryProcess(Wave(i, Math.Min(chunk, 14400 - i), frequency, peak, secondFrequency, secondPeak, right),
                out var next, out _))
            {
                packet = next;
                if (next.Active && firstActive < 0) firstActive = i + chunk;
            }
        return (packet, processor.ConversionTelemetry, firstActive);
    }

    private static void SpectralRange(Action<bool, string> check)
    {
        var pair = Feed(110, 7000, 180, 7000);
        Tones two = Decode(pair.Packet), silent = Decode(pair.Packet, true);
        Console.WriteLine("PS5 SAME_BAND " + pair.Telemetry);
        check(Math.Abs(two.FirstFrequency - 110) <= 10 && Math.Abs(two.SecondFrequency - 180) <= 10 &&
            two.FirstAmplitude > 0 && two.SecondAmplitude > 0, "same-band 110+180Hz retains two resolved spectral peaks");
        check(silent.FirstAmplitude == 0 && silent.SecondAmplitude == 0, "two peaks do not leak to silent actuator");
        double recoveredRms = Math.Sqrt(Math.Pow(two.FirstAmplitude / 29000.0 * 32768, 2) +
            Math.Pow(two.SecondAmplitude / 29000.0 * 32768, 2));
        check(recoveredRms > 7000 * .75 && recoveredRms < 7000 * 1.18,
            "two-peak RMS partition does not double-count band energy");
        check(pair.Telemetry.Contains("reconstruction=two_peak_lossy") &&
            pair.Telemetry.Contains("frequency_mapping=legacy_hz_proxy_unvalidated") &&
            pair.Telemetry.Contains("encoded_left_controls="), "PCM logs distinguish source Hz, encoded control and lossy reconstruction");
        int previous = 0;
        foreach (int amplitude in new[] { 500, 1000, 10000, 22000, 24000, 28000, 32767 })
        {
            var result = Feed(160, amplitude);
            Tones tone = Decode(result.Packet);
            check(result.Packet.Active && Math.Abs(tone.FirstFrequency - 160) <= 5,
                "full PCM range preserves frequency " + amplitude);
            check(tone.FirstAmplitude > previous && tone.FirstAmplitude < 29000,
                "no premature RMS saturation at full-scale sine " + amplitude);
            check(tone.SecondAmplitude <= tone.FirstAmplitude * .02, "single sine is not split into a false second oscillator " + amplitude);
            previous = tone.FirstAmplitude;
            check(result.FirstActiveFrame <= 576, "larger steady-state window preserves <=12ms initial response " + amplitude);
        }
        foreach (double frequency in new[] { 110.0, 160, 180, 237, 310, 400, 420, 490 })
        {
            var result = Feed(frequency, 10000, right: frequency >= 250);
            Tones tone = Decode(result.Packet, frequency >= 250);
            int hz = frequency >= 250 ? tone.SecondFrequency : tone.FirstFrequency;
            check(Math.Abs(hz - frequency) <= 5, "single-tone sub-bin estimate retained " + frequency);
        }
        foreach (int chunk in new[] { 24, 96, 192 })
        {
            var chunked = Feed(110, 7000, 180, 7000, chunk: chunk);
            Tones tones = Decode(chunked.Packet);
            Console.WriteLine("PS5 SAME_BAND chunk=" + chunk + " " + chunked.Telemetry);
            check(Math.Abs(tones.FirstFrequency - two.FirstFrequency) <= 2 &&
                Math.Abs(tones.SecondFrequency - two.SecondFrequency) <= 2,
                "same-band peak estimates do not depend on PCM fragmentation " + chunk);
        }
    }
}
