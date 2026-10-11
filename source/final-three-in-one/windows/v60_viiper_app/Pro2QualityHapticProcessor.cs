using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Y700Switch2V60Viiper;

// Independently implemented DSP; standard Butterworth/RBJ, Hann and Goertzel
// maths. This compresses PCM to two tones per actuator, not waveform passthrough.
public sealed class Pro2QualityHapticProcessor
{
    private const int SampleRate = 48000, Decimation = 16, AnalysisRate = 3000;
    private const int WindowSize = 64, StartupSize = 32, Hop = 36, MaxAmplitude = 29000;
    private const int SilenceLimit = SampleRate * 80 / 1000;
    private static readonly Window FullWindow = new(WindowSize), StartupWindow = new(StartupSize);
    private readonly Channel left = new(), right = new();
    private int decimationCount, samples, sinceOutput, silenceFrames;
    private bool active, emitted;
    public string Telemetry { get; private set; } = "processor=quality_stft status=accumulating";

    public void Reset()
    {
        left.Reset(); right.Reset();
        decimationCount = samples = sinceOutput = silenceFrames = 0;
        active = emitted = false;
        Telemetry = "processor=quality_stft status=reset";
    }

    public bool TryProcess(ReadOnlySpan<byte> pcm, out Pro2OutputPacket packet, out string reason)
    {
        packet = default!; reason = "";
        if (pcm.Length == 0 || pcm.Length % 8 != 0)
        { reason = "DualSense HD audio packet has no complete 4-channel PCM frame"; return false; }
        bool rawActive = false, ready = false;
        for (int offset = 0; offset < pcm.Length; offset += 8)
        {
            // Ignore speaker channels 1/2. Preserve the actuator pair 3/4.
            short l = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 4, 2));
            short r = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 6, 2));
            rawActive |= Math.Abs((int)l) >= 64 || Math.Abs((int)r) >= 64;
            left.Add(l); right.Add(r);
            if (++decimationCount < Decimation) continue;
            decimationCount = 0;
            left.Commit(); right.Commit();
            samples = Math.Min(WindowSize, samples + 1);
            sinceOutput++;
            ready |= samples >= StartupSize && (!emitted || sinceOutput >= Hop);
        }
        silenceFrames = rawActive ? 0 : Math.Min(SilenceLimit, silenceFrames + pcm.Length / 8);
        if (active && silenceFrames >= SilenceLimit)
        {
            Reset();
            packet = Pro2OutputPacketMapper.BuildOrdinaryPacket(0, 0, "dualsense-hd-silence");
            return true;
        }
        if (!ready)
        { reason = "DualSense HD spectral window is accumulating"; return false; }
        // Consume all input, even if a transport batch spans multiple hops.
        // Emit only the freshest analysis; no delayed haptic backlog.
        var window = samples >= WindowSize ? FullWindow : StartupWindow;
        var lBands = left.Analyze(window);
        var rBands = right.Analyze(window);
        emitted = true; sinceOutput = 0;
        Telemetry = "processor=quality_stft window=" + window.Size + " startup_ms=10.67 hop_ms=12" +
            " estimated_left_hz=" + F(lBands.LowFrequency) + "," + F(lBands.HighFrequency) +
            " estimated_right_hz=" + F(rBands.LowFrequency) + "," + F(rBands.HighFrequency) +
            " left_rms=" + F(lBands.LowRms) + "," + F(lBands.HighRms) +
            " right_rms=" + F(rBands.LowRms) + "," + F(rBands.HighRms);
        ushort la = Amplitude(lBands.LowRms), lh = Amplitude(lBands.HighRms);
        ushort ra = Amplitude(rBands.LowRms), rh = Amplitude(rBands.HighRms);
        bool hasEnergy = la != 0 || lh != 0 || ra != 0 || rh != 0;
        if (!hasEnergy)
        {
            reason = "DualSense HD spectral energy is below the software noise threshold";
            if (!active) return false;
            active = false;
            packet = Pro2OutputPacketMapper.BuildOrdinaryPacket(0, 0, "dualsense-hd-quality-release");
            return true;
        }
        active = true;
        packet = Pro2OutputPacketMapper.BuildRaw02Packet(
            Frequency(lBands.LowFrequency, 70, 300), la, Frequency(lBands.HighFrequency, 250, 511), lh,
            Frequency(rBands.LowFrequency, 70, 300), ra, Frequency(rBands.HighFrequency, 250, 511), rh,
            "dualsense-hd-audio");
        return true;
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static ushort Frequency(double value, int minimum, int maximum) =>
        (ushort)Math.Clamp(Math.Round(value), minimum, maximum);
    private static ushort Amplitude(double rms) => rms < 48 ? (ushort)0 :
        (ushort)Math.Clamp(Math.Round(rms / 16384 * MaxAmplitude), 0, MaxAmplitude);

    private readonly record struct Bands(double LowFrequency, double LowRms, double HighFrequency, double HighRms);
    private sealed class Window
    {
        public int Size { get; }
        public readonly double[] Hann, Coefficient;
        public readonly double Energy;
        public Window(int size)
        {
            Size = size; Hann = new double[size]; Coefficient = new double[17];
            for (int i = 0; i < size; i++)
            { Hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1)); Energy += Hann[i] * Hann[i]; }
            for (int bin = 0; bin < Coefficient.Length; bin++)
                Coefficient[bin] = 2 * Math.Cos(2 * Math.PI * bin / size);
        }
    }
    private sealed class Channel
    {
        // Four-pole anti-alias filter BEFORE decimation; a box-average alone
        // can alias an out-of-band tone into a false low-frequency sensation.
        private readonly Biquad highPass = new(35, Math.Sqrt(0.5), true);
        private readonly Biquad lowPass1 = new(800, 0.5411961001, false);
        private readonly Biquad lowPass2 = new(800, 1.306562965, false);
        private readonly double[] ring = new double[WindowSize];
        private int next;
        private double sum;
        public void Add(double sample) => sum += lowPass2.Process(lowPass1.Process(highPass.Process(sample)));
        public void Commit() { ring[next] = sum / Decimation; next = (next + 1) % WindowSize; sum = 0; }
        public void Reset()
        { highPass.Reset(); lowPass1.Reset(); lowPass2.Reset(); Array.Clear(ring); next = 0; sum = 0; }
        public Bands Analyze(Window window)
        {
            int n = window.Size, start = (next - n + WindowSize) % WindowSize;
            double mean = 0;
            for (int i = 0; i < n; i++) mean += ring[(start + i) % WindowSize];
            mean /= n;
            Span<double> data = stackalloc double[WindowSize];
            Span<double> power = stackalloc double[17];
            power.Clear();
            for (int i = 0; i < n; i++) data[i] = (ring[(start + i) % WindowSize] - mean) * window.Hann[i];
            int lastBin = Math.Min(15, n / 2 - 1);
            for (int bin = 1; bin <= lastBin; bin++)
            {
                double first = 0, second = 0, coefficient = window.Coefficient[bin];
                for (int i = 0; i < n; i++)
                { double current = data[i] + coefficient * first - second; second = first; first = current; }
                power[bin] = Math.Max(0, first * first + second * second - coefficient * first * second);
            }
            int split = (int)Math.Floor(250.0 * n / AnalysisRate);
            int highEnd = Math.Min(lastBin - 1, (int)Math.Ceiling(550.0 * n / AnalysisRate));
            var low = Band(power, 1, split, n, window.Energy);
            var high = Band(power, split + 1, highEnd, n, window.Energy);
            return new(low.Frequency, low.Rms, high.Frequency, high.Rms);
        }
        private static (double Frequency, double Rms) Band(ReadOnlySpan<double> power, int begin, int end, int n, double energy)
        {
            int peak = begin; double total = 0;
            for (int bin = begin; bin <= end; bin++)
            { total += power[bin]; if (power[bin] > power[peak]) peak = bin; }
            // A main lobe crossing the arbitrary band boundary is not a
            // second oscillator. Reject its descending shoulder rather than
            // turning a single 237 Hz tone into a spurious high-band effect.
            if (power[peak - 1] >= power[peak] || power[peak + 1] > power[peak])
                return (peak * AnalysisRate / (double)n, 0);
            double a = Math.Log(Math.Max(power[peak - 1], 1e-20));
            double b = Math.Log(Math.Max(power[peak], 1e-20));
            double c = Math.Log(Math.Max(power[peak + 1], 1e-20));
            double denominator = a - 2 * b + c;
            double delta = Math.Abs(denominator) > 1e-12 ? Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5) : 0;
            return ((peak + delta) * AnalysisRate / n, Math.Sqrt(2 * total / (n * energy)));
        }
    }
    private sealed class Biquad
    {
        private readonly double b0, b1, b2, a1, a2;
        private double z1, z2;
        public Biquad(double frequency, double q, bool highPass)
        {
            double w = 2 * Math.PI * frequency / SampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            b0 = (highPass ? 1 + cos : 1 - cos) / (2 * a0);
            b1 = (highPass ? -(1 + cos) : 1 - cos) / a0; b2 = b0;
            a1 = -2 * cos / a0; a2 = (1 - alpha) / a0;
        }
        public double Process(double sample)
        {
            double value = b0 * sample + z1;
            z1 = b1 * sample - a1 * value + z2; z2 = b2 * sample - a2 * value;
            return value;
        }
        public void Reset() { z1 = z2 = 0; }
    }
}
