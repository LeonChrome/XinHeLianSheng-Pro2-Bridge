using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Y700Switch2V60Viiper;

// Independently implemented DSP; standard Butterworth/RBJ, Hann and Goertzel
// maths. This compresses PCM to two tones per actuator, not waveform passthrough.
public sealed class Pro2QualityHapticProcessor
{
    private const int SampleRate = 48000, Decimation = 16, AnalysisRate = 3000;
    private const int WindowSize = 96, StartupSize = 32, Hop = 36, MaxAmplitude = 29000;
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
        bool produced = false;
        for (int offset = 0; offset < pcm.Length; offset += 8)
        {
            // Ignore speaker channels 1/2. Preserve the actuator pair 3/4.
            short l = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 4, 2));
            short r = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 6, 2));
            bool rawActive = Math.Abs((int)l) >= 64 || Math.Abs((int)r) >= 64;
            silenceFrames = rawActive ? 0 : Math.Min(SilenceLimit, silenceFrames + 1);
            if (active && silenceFrames >= SilenceLimit)
            {
                Reset();
                packet = Pro2OutputPacketMapper.BuildOrdinaryPacket(0, 0, "dualsense-hd-silence");
                produced = true;
            }
            left.Add(l); right.Add(r);
            if (++decimationCount < Decimation) continue;
            decimationCount = 0;
            left.Commit(); right.Commit();
            samples = Math.Min(WindowSize, samples + 1);
            sinceOutput++;
            if (samples >= StartupSize && (!emitted || sinceOutput >= Hop))
            {
                // Analyse on the sample clock, not at the end of a transport
                // batch. USB fragmentation must not shift the spectral window.
                // For a batch containing several hops return only its freshest
                // generated state; no delayed PCM backlog is replayed.
                if (AnalyzeOutput(out var next, out string explanation))
                {
                    packet = next;
                    produced = true;
                }
                reason = explanation;
            }
        }
        if (!produced && reason.Length == 0) reason = "DualSense HD spectral window is accumulating";
        return produced;
    }

    private bool AnalyzeOutput(out Pro2OutputPacket packet, out string reason)
    {
        packet = default!; reason = "";
        var window = samples >= WindowSize ? FullWindow : StartupWindow;
        var lBands = left.Analyze(window);
        var rBands = right.Analyze(window);
        emitted = true; sinceOutput = 0;
        Telemetry = "processor=quality_stft reconstruction=two_peak_lossy window=" + window.Size +
            " analysis_ms=" + F(window.Size * 1000.0 / AnalysisRate) + " startup_ms=10.67 hop_ms=12" +
            " frequency_mapping=" + Pro2RumbleFrequencyPolicy.Mapping + " amplitude_reference=pcm_full_scale_32768" +
            " estimated_left_hz=" + F(lBands.LowFrequency) + "," + F(lBands.HighFrequency) +
            " estimated_right_hz=" + F(rBands.LowFrequency) + "," + F(rBands.HighFrequency) +
            " left_rms=" + F(lBands.LowRms) + "," + F(lBands.HighRms) +
            " right_rms=" + F(rBands.LowRms) + "," + F(rBands.HighRms) +
            " retained_peaks=" + lBands.Peaks + "," + rBands.Peaks +
            " residual_energy_fraction=" + F(lBands.ResidualFraction) + "," + F(rBands.ResidualFraction) +
            " encoded_left_controls=" + Frequency(lBands.LowFrequency) + "," + Frequency(lBands.HighFrequency) +
            " encoded_right_controls=" + Frequency(rBands.LowFrequency) + "," + Frequency(rBands.HighFrequency);
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
        int frequencyClamps =
            (la != 0 && Pro2RumbleFrequencyPolicy.WouldClamp(lBands.LowFrequency, 70, 511) ? 1 : 0) +
            (lh != 0 && Pro2RumbleFrequencyPolicy.WouldClamp(lBands.HighFrequency, 70, 511) ? 1 : 0) +
            (ra != 0 && Pro2RumbleFrequencyPolicy.WouldClamp(rBands.LowFrequency, 70, 511) ? 1 : 0) +
            (rh != 0 && Pro2RumbleFrequencyPolicy.WouldClamp(rBands.HighFrequency, 70, 511) ? 1 : 0);
        packet = Pro2OutputPacketMapper.BuildRaw02Packet(
            Frequency(lBands.LowFrequency), la, Frequency(lBands.HighFrequency), lh,
            Frequency(rBands.LowFrequency), ra, Frequency(rBands.HighFrequency), rh,
            "dualsense-hd-audio") with { FrequencyClamps = frequencyClamps };
        return true;
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    // A measured-Hz to physical oscillator-control calibration is not available.
    // Keep the bounded legacy proxy explicit rather than labelling it lossless.
    private static ushort Frequency(double value) => Pro2RumbleFrequencyPolicy.FromEstimatedHz(value, 70, 511);
    private static ushort Amplitude(double rms) => rms < 48 ? (ushort)0 :
        (ushort)Math.Clamp(Math.Round(rms / 32768.0 * MaxAmplitude), 0, MaxAmplitude);

    private readonly record struct Bands(double LowFrequency, double LowRms, double HighFrequency, double HighRms,
        int Peaks, double ResidualFraction);
    private sealed class Window
    {
        public int Size { get; }
        public readonly double[] Hann, Coefficient;
        public readonly double Energy;
        public Window(int size)
        {
            Size = size; Hann = new double[size]; Coefficient = new double[size / 2 + 1];
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
            Span<double> power = stackalloc double[WindowSize / 2 + 1];
            power.Clear();
            for (int i = 0; i < n; i++) data[i] = (ring[(start + i) % WindowSize] - mean) * window.Hann[i];
            int lastBin = Math.Min((int)Math.Ceiling(600.0 * n / AnalysisRate), n / 2 - 1);
            for (int bin = 1; bin <= lastBin; bin++)
            {
                double first = 0, second = 0, coefficient = window.Coefficient[bin];
                for (int i = 0; i < n; i++)
                { double current = data[i] + coefficient * first - second; second = first; first = current; }
                power[bin] = Math.Max(0, first * first + second * second - coefficient * first * second);
            }
            return SelectPeaks(power, lastBin, n, window.Energy);
        }
        private static Bands SelectPeaks(ReadOnlySpan<double> power, int lastBin, int n, double energy)
        {
            // Keep the two strongest resolved peaks, not one mandatory peak in
            // each band. Two genuine low-band tones must not collapse into a
            // false frequency between them. The finite window remains lossy.
            int first = -1, second = -1;
            for (int bin = 1; bin < lastBin; bin++)
            {
                if (!IsPeak(power, bin) || PeakHz(power, bin, n) < 70 || PeakHz(power, bin, n) > 550) continue;
                if (first < 0 || power[bin] > power[first]) first = bin;
            }
            if (first < 0) return new(0x112, 0, 0x187, 0, 0, 0);
            double firstHz = PeakHz(power, first, n);
            for (int bin = 1; bin < lastBin; bin++)
            {
                if (!IsPeak(power, bin) || bin == first || power[bin] < power[first] * .0025) continue;
                double hz = PeakHz(power, bin, n);
                // A Hann main lobe / side lobe is not a new oscillator.
                if (hz < 70 || hz > 550 || Math.Abs(hz - firstHz) < 1.75 * AnalysisRate / n) continue;
                if (second < 0 || power[bin] > power[second]) second = bin;
            }
            double secondHz = second >= 0 ? PeakHz(power, second, n) : 0;
            double firstPower = 0, secondPower = 0, total = 0;
            int end = Math.Min(lastBin, (int)Math.Ceiling(550.0 * n / AnalysisRate));
            for (int bin = 1; bin <= end; bin++)
            {
                total += power[bin];
                double firstDistance = Math.Abs(bin - firstHz * n / AnalysisRate);
                double secondDistance = second < 0 ? double.MaxValue : Math.Abs(bin - secondHz * n / AnalysisRate);
                // Assign each bin once. Two peaks must not each receive the
                // whole band's RMS, which would double-count signal energy.
                if (firstDistance <= secondDistance && firstDistance <= 2) firstPower += power[bin];
                else if (secondDistance < firstDistance && secondDistance <= 2) secondPower += power[bin];
            }
            double firstRms = Math.Sqrt(2 * firstPower / (n * energy));
            double secondRms = Math.Sqrt(2 * secondPower / (n * energy));
            double residual = total > 0 ? Math.Clamp(1 - (firstPower + secondPower) / total, 0, 1) : 0;
            if (second < 0)
                return firstHz < 250
                    ? new(firstHz, firstRms, 0x187, 0, 1, residual)
                    : new(0x112, 0, firstHz, firstRms, 1, residual);
            return firstHz < secondHz
                ? new(firstHz, firstRms, secondHz, secondRms, 2, residual)
                : new(secondHz, secondRms, firstHz, firstRms, 2, residual);
        }
        private static bool IsPeak(ReadOnlySpan<double> power, int peak) =>
            power[peak] > power[peak - 1] && power[peak] >= power[peak + 1] && power[peak] > 0;
        private static double PeakHz(ReadOnlySpan<double> power, int peak, int n)
        {
            double a = Math.Log(Math.Max(power[peak - 1], 1e-20));
            double b = Math.Log(Math.Max(power[peak], 1e-20));
            double c = Math.Log(Math.Max(power[peak + 1], 1e-20));
            double denominator = a - 2 * b + c;
            double delta = Math.Abs(denominator) > 1e-12 ? Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5) : 0;
            return (peak + delta) * AnalysisRate / n;
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
