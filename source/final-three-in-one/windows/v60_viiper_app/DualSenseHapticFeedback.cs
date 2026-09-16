using System;
using System.Buffers.Binary;

namespace Y700Switch2V60Viiper;

public enum DualSenseHapticFrameKind : byte
{
    HidOutput = 1,
    AudioPcm = 2
}

public readonly record struct DualSenseHapticFrame(
    DualSenseHapticFrameKind Kind,
    ReadOnlyMemory<byte> Payload)
{
    public const int WireSize = 388;
    private const int PayloadOffset = 4;
    private const int MaximumPayloadSize = 384;

    public static bool TryParse(
        ReadOnlySpan<byte> wire,
        out DualSenseHapticFrame frame,
        out string error)
    {
        frame = default;
        error = "";
        if (wire.Length != WireSize)
        {
            error = "DualSense haptic frame size must be " + WireSize;
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(wire.Slice(1, 2));
        if (length < 0 || length > MaximumPayloadSize)
        {
            error = "DualSense haptic payload length is invalid: " + length;
            return false;
        }

        byte[] payload = wire.Slice(PayloadOffset, length).ToArray();
        frame = new DualSenseHapticFrame(
            (DualSenseHapticFrameKind)wire[0],
            payload);
        return true;
    }
}

public sealed class DualSenseHapticRumbleScheduler
{
    private readonly DualSenseHapticAudioProcessor audioProcessor = new();
    private bool compatibilityMode;
    private bool modeKnown;

    public bool TryProcess(
        ReadOnlySpan<byte> wire,
        out Pro2OutputPacket packet,
        out string summary,
        out string reason)
    {
        packet = default!;
        summary = "";
        reason = "";
        if (!DualSenseHapticFrame.TryParse(
                wire,
                out DualSenseHapticFrame frame,
                out reason))
        {
            return false;
        }

        if (frame.Kind == DualSenseHapticFrameKind.HidOutput)
        {
            return ProcessHid(frame.Payload.Span, out packet, out summary, out reason);
        }
        if (frame.Kind == DualSenseHapticFrameKind.AudioPcm)
        {
            summary = "DualSense HD audio: bytes=" + frame.Payload.Length +
                      " host_mode=" + (compatibilityMode ? "compatibility" : "audio_haptics");
            if (compatibilityMode)
            {
                reason = "HD audio blocked while DualSense compatibility vibration is selected";
                return false;
            }

            return audioProcessor.TryProcess(
                frame.Payload.Span,
                out packet,
                out reason);
        }

        summary = "DualSense haptic frame: unknown kind=" + (byte)frame.Kind;
        reason = "unknown DualSense haptic frame kind";
        return false;
    }

    private bool ProcessHid(
        ReadOnlySpan<byte> report,
        out Pro2OutputPacket packet,
        out string summary,
        out string reason)
    {
        packet = default!;
        reason = "";
        if (report.Length < 5 || report[0] != 0x02)
        {
            summary = "DualSense HID output: invalid";
            reason = "DualSense HID output report is too short or has the wrong report id";
            return false;
        }

        ReadOnlySpan<byte> payload = report.Slice(1);
        byte validFlag0 = payload[0];
        byte validFlag2 = payload.Length > 38 ? payload[38] : (byte)0;
        byte weak = payload[2];
        byte strong = payload[3];
        bool compatibilityV1 = (validFlag0 & 0x01) != 0;
        bool hapticsSelect = (validFlag0 & 0x02) != 0;
        bool compatibilityV2 = (validFlag2 & 0x04) != 0;
        bool selectedCompatibility =
            hapticsSelect || compatibilityV1 || compatibilityV2;
        bool modeChanged = !modeKnown || compatibilityMode != selectedCompatibility;
        compatibilityMode = selectedCompatibility;
        modeKnown = true;
        summary =
            "DualSense HID output: host_mode=" +
            (compatibilityMode ? "compatibility" : "audio_haptics") +
            " small=" + weak +
            " large=" + strong +
            " flag0=0x" + validFlag0.ToString("X2") +
            " flag2=0x" + validFlag2.ToString("X2");

        if (compatibilityMode)
        {
            packet = Pro2OutputPacketMapper.BuildOrdinaryPacket(
                weak,
                strong,
                "dualsense-ordinary");
            return true;
        }

        if (modeChanged)
        {
            audioProcessor.ResetOutputState();
            packet = Pro2OutputPacketMapper.BuildOrdinaryPacket(
                0,
                0,
                "dualsense-hd-transition-stop");
            return true;
        }

        reason = "ordinary motor bytes ignored while DualSense audio haptics is selected";
        return false;
    }
}

public sealed class DualSenseHapticAudioProcessor
{
    private const int Channels = 4;
    private const int BytesPerSample = 2;
    private const int FrameBytes = Channels * BytesPerSample;
    private const int DownsampleFactor = 16;
    private const int SpectralRate = 3000;
    private const int SpectralWindow = 64;
    private const int SpectralHop = 36;
    private const int LowBinMin = 2;
    private const int LowBinMax = 5;
    private const int HighBinMin = 6;
    private const int HighBinMax = 13;
    private const int ActivityEnvelopeThreshold = 512;
    private const int ActivityPeakThreshold = 2048;
    private const int SilencePacketsBeforeStop = 80;
    private const int SafeMaximumAmplitude = 29000;

    private readonly short[] spectralLeft = new short[SpectralWindow];
    private readonly short[] spectralRight = new short[SpectralWindow];
    private int spectralCount;
    private int downsampleSumLeft;
    private int downsampleSumRight;
    private int downsampleCount;
    private int envelopeLeft;
    private int envelopeRight;
    private int silencePackets;
    private bool outputActive;

    public void ResetOutputState()
    {
        outputActive = false;
        silencePackets = 0;
    }

    public bool TryProcess(
        ReadOnlySpan<byte> pcm,
        out Pro2OutputPacket packet,
        out string reason)
    {
        packet = default!;
        reason = "";
        int frames = pcm.Length / FrameBytes;
        if (frames == 0)
        {
            reason = "DualSense HD audio packet has no complete 4-channel PCM frame";
            return false;
        }

        long sumAbsLeft = 0;
        long sumAbsRight = 0;
        int peakLeft = 0;
        int peakRight = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * FrameBytes;
            int absLeft = Absolute(BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 4, 2)));
            int absRight = Absolute(BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 6, 2)));
            sumAbsLeft += absLeft;
            sumAbsRight += absRight;
            peakLeft = Math.Max(peakLeft, absLeft);
            peakRight = Math.Max(peakRight, absRight);
        }

        bool spectralReady = false;
        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * FrameBytes;
            // DualSense channels 3/4 are the actuator pair. Channels 1/2 are
            // ordinary audio and must never be promoted to haptics.
            short left = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 4, 2));
            short right = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 6, 2));
            downsampleSumLeft += left;
            downsampleSumRight += right;
            downsampleCount++;
            if (downsampleCount < DownsampleFactor)
            {
                continue;
            }

            if (spectralCount < SpectralWindow)
            {
                spectralLeft[spectralCount] =
                    (short)(downsampleSumLeft / DownsampleFactor);
                spectralRight[spectralCount] =
                    (short)(downsampleSumRight / DownsampleFactor);
                spectralCount++;
            }
            downsampleSumLeft = 0;
            downsampleSumRight = 0;
            downsampleCount = 0;
            spectralReady |= spectralCount == SpectralWindow;
        }

        int meanLeft = (int)(sumAbsLeft / frames);
        int meanRight = (int)(sumAbsRight / frames);
        envelopeLeft = SmoothEnvelope(envelopeLeft, meanLeft);
        envelopeRight = SmoothEnvelope(envelopeRight, meanRight);
        bool activity = HasActivity(envelopeLeft, envelopeRight, peakLeft, peakRight);
        if (!activity)
        {
            silencePackets++;
            if (outputActive && silencePackets >= SilencePacketsBeforeStop)
            {
                outputActive = false;
                packet = Pro2OutputPacketMapper.BuildOrdinaryPacket(
                    0,
                    0,
                    "dualsense-hd-silence");
                return true;
            }
            reason = "DualSense HD audio is silent";
            return false;
        }

        silencePackets = 0;
        if (!spectralReady)
        {
            reason = "DualSense HD spectral window is accumulating";
            return false;
        }

        (ushort lowFrequencyLeft, ushort lowRmsLeft) =
            AnalyzeBand(spectralLeft, LowBinMin, LowBinMax, 0x112);
        (ushort highFrequencyLeft, ushort highRmsLeft) =
            AnalyzeBand(spectralLeft, HighBinMin, HighBinMax, 0x187);
        (ushort lowFrequencyRight, ushort lowRmsRight) =
            AnalyzeBand(spectralRight, LowBinMin, LowBinMax, 0x112);
        (ushort highFrequencyRight, ushort highRmsRight) =
            AnalyzeBand(spectralRight, HighBinMin, HighBinMax, 0x187);

        ShiftSpectralWindow(spectralLeft);
        ShiftSpectralWindow(spectralRight);
        spectralCount = SpectralWindow - SpectralHop;

        ushort lowAmplitudeLeft = MapSpectralAmplitude(lowRmsLeft);
        ushort highAmplitudeLeft = MapSpectralAmplitude(highRmsLeft);
        ushort lowAmplitudeRight = MapSpectralAmplitude(lowRmsRight);
        ushort highAmplitudeRight = MapSpectralAmplitude(highRmsRight);
        if (lowAmplitudeLeft == 0 &&
            highAmplitudeLeft == 0 &&
            lowAmplitudeRight == 0 &&
            highAmplitudeRight == 0)
        {
            reason = "DualSense HD spectral energy is below the physical threshold";
            return false;
        }

        outputActive = true;
        packet = Pro2OutputPacketMapper.BuildRaw02Packet(
            ClampFrequency(lowFrequencyLeft, 70, 300),
            lowAmplitudeLeft,
            ClampFrequency(highFrequencyLeft, 250, 511),
            highAmplitudeLeft,
            ClampFrequency(lowFrequencyRight, 70, 300),
            lowAmplitudeRight,
            ClampFrequency(highFrequencyRight, 250, 511),
            highAmplitudeRight,
            "dualsense-hd-audio");
        return true;
    }

    private static (ushort Frequency, ushort Rms) AnalyzeBand(
        short[] samples,
        int minimumBin,
        int maximumBin,
        ushort fallbackFrequency)
    {
        double bandPower = 0;
        double peakPower = 0;
        int peakBin = 0;
        for (int bin = minimumBin; bin <= maximumBin; bin++)
        {
            double coefficient = 2 * Math.Cos(2 * Math.PI * bin / SpectralWindow);
            double first = 0;
            double second = 0;
            for (int i = 0; i < SpectralWindow; i++)
            {
                double current = samples[i] / 16.0 + coefficient * first - second;
                second = first;
                first = current;
            }
            double power =
                first * first +
                second * second -
                coefficient * first * second;
            power = Math.Max(0, power);
            bandPower += power;
            if (power > peakPower)
            {
                peakPower = power;
                peakBin = bin;
            }
        }

        if (peakBin == 0 || bandPower <= 0)
        {
            return (fallbackFrequency, 0);
        }

        double rms = Math.Sqrt(bandPower * 2.0) / SpectralWindow * 16.0;
        return (
            (ushort)Math.Round(peakBin * SpectralRate / (double)SpectralWindow),
            (ushort)Math.Clamp(Math.Round(rms), 0, ushort.MaxValue));
    }

    private static ushort MapSpectralAmplitude(ushort rms)
    {
        double mapped = rms / 16384.0 * SafeMaximumAmplitude;
        return (ushort)Math.Clamp(Math.Round(mapped), 0, SafeMaximumAmplitude);
    }

    private static void ShiftSpectralWindow(short[] samples)
    {
        Array.Copy(
            samples,
            SpectralHop,
            samples,
            0,
            SpectralWindow - SpectralHop);
    }

    private static int SmoothEnvelope(int previous, int value)
    {
        return value > previous
            ? (previous * 2 + value * 6 + 4) / 8
            : (previous * 7 + value + 4) / 8;
    }

    private static bool HasActivity(
        int leftEnvelope,
        int rightEnvelope,
        int leftPeak,
        int rightPeak)
    {
        return leftEnvelope >= ActivityEnvelopeThreshold ||
               rightEnvelope >= ActivityEnvelopeThreshold ||
               leftPeak >= ActivityPeakThreshold ||
               rightPeak >= ActivityPeakThreshold;
    }

    private static int Absolute(short value)
    {
        return value == short.MinValue ? 32768 : Math.Abs(value);
    }

    private static ushort ClampFrequency(ushort value, ushort minimum, ushort maximum)
    {
        return value < minimum
            ? minimum
            : value > maximum
                ? maximum
                : value;
    }
}
