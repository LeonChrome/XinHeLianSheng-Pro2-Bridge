using System;

namespace Y700Switch2V60Viiper;

public sealed record Pro2OutputPacket(byte[] Report, string Source, bool Active, byte? PlayerLedMask,
    bool PreserveNativePayload = false, string ConversionTelemetry = "", int FrequencyClamps = 0);

public static class Pro2OutputPacketMapper
{
    private const byte Ns2ProOutputFlagRumble = 0x01;
    private const byte Ns2ProOutputFlagLed = 0x02;
    private const int Switch2OutputReportSize = 64;
    private const int RumbleSideOffsetLeft = 1;
    private const int RumbleSideOffsetRight = 17;
    private const int RumbleFrameOffsetLeft = 2;
    private const int RumbleFrameOffsetRight = 18;
    private const int RumbleFrameBytes = 5;
    private const ushort OrdinaryMaxAmplitude = 29000;

    public static bool TryMapFeedback(
        ViiperDeviceProfile profile,
        ReadOnlySpan<byte> feedback,
        out Pro2OutputPacket packet,
        out string reason)
    {
        packet = default!;
        reason = "";

        switch (profile.Mode)
        {
            case ViiperVirtualMode.Pro2:
                return TryMapNs2Pro(feedback, out packet, out reason);
            case ViiperVirtualMode.Pro1:
                return Switch1RumbleConverter.TryMap(feedback, out packet, out reason);
            case ViiperVirtualMode.Xbox:
            case ViiperVirtualMode.XboxProfessionalImuTest:
                if (feedback.Length < 2)
                {
                    reason = "xinput feedback too short";
                    return false;
                }
                packet = BuildOrdinaryPacket(feedback[1], feedback[0], "xinput");
                return true;
            case ViiperVirtualMode.DualSenseLike:
            case ViiperVirtualMode.DualSenseProfessionalImuTest:
                if (feedback.Length < 2)
                {
                    reason = "dualsense feedback too short";
                    return false;
                }
                packet = BuildOrdinaryPacket(feedback[0], feedback[1], "dualsense-ordinary");
                return true;
            default:
                reason = "unsupported profile";
                return false;
        }
    }

    private static bool TryMapNs2Pro(
        ReadOnlySpan<byte> feedback,
        out Pro2OutputPacket packet,
        out string reason)
    {
        packet = default!;
        reason = "";
        if (feedback.Length < 34)
        {
            reason = "ns2pro feedback too short";
            return false;
        }

        byte flags = feedback[32];
        byte? playerLed = (flags & Ns2ProOutputFlagLed) != 0 ? feedback[33] : null;
        if ((flags & Ns2ProOutputFlagRumble) == 0)
        {
            reason = playerLed.HasValue
                ? "led-only feedback has no physical Pro2 rumble payload"
                : "feedback has no rumble flag";
            return false;
        }

        byte[] report = NewSwitch2OutputReport();
        feedback.Slice(0, 16).CopyTo(report.AsSpan(RumbleSideOffsetLeft, 16));
        feedback.Slice(16, 16).CopyTo(report.AsSpan(RumbleSideOffsetRight, 16));

        bool active = Pro2NativeRumbleEncoder.HasEffect(report);
        packet = new Pro2OutputPacket(report, "ns2pro-hd", active, playerLed, PreserveNativePayload: true);
        return true;
    }

    public static Pro2OutputPacket BuildOrdinaryPacket(
        byte weak,
        byte strong,
        string source)
    {
        byte[] report = NewSwitch2OutputReport();
        // XInput and DualSense legacy rumble specify a strong/low-frequency
        // LEFT motor and a weak/high-frequency RIGHT motor. Do not mix both
        // values into both physical sides: that discards source spatial cues.
        BuildOrdinarySide(0, strong, report.AsSpan(RumbleSideOffsetLeft, 16));
        BuildOrdinarySide(weak, 0, report.AsSpan(RumbleSideOffsetRight, 16));
        return new Pro2OutputPacket(report, source, weak != 0 || strong != 0, null);
    }

    public static Pro2OutputPacket BuildRaw02Packet(
        ushort lowFrequencyLeft,
        ushort lowAmplitudeLeft,
        ushort highFrequencyLeft,
        ushort highAmplitudeLeft,
        ushort lowFrequencyRight,
        ushort lowAmplitudeRight,
        ushort highFrequencyRight,
        ushort highAmplitudeRight,
        string source)
    {
        byte[] report = NewSwitch2OutputReport();
        EncodeSwitchRumbleFrame(
            highFrequencyLeft,
            highAmplitudeLeft,
            lowFrequencyLeft,
            lowAmplitudeLeft,
            report.AsSpan(RumbleFrameOffsetLeft, RumbleFrameBytes));
        EncodeSwitchRumbleFrame(
            highFrequencyRight,
            highAmplitudeRight,
            lowFrequencyRight,
            lowAmplitudeRight,
            report.AsSpan(RumbleFrameOffsetRight, RumbleFrameBytes));
        // Inputs smaller than one packed amplitude step quantise to zero.
        // Report actual encoded activity, not the unquantised input value.
        bool active = Pro2NativeRumbleEncoder.HasEffect(report);
        return new Pro2OutputPacket(report, source, active, null);
    }

    private static byte[] NewSwitch2OutputReport()
    {
        byte[] report = new byte[Switch2OutputReportSize];
        report[0] = 0x02;
        report[RumbleSideOffsetLeft] = 0x50;
        report[RumbleSideOffsetRight] = 0x50;
        return report;
    }

    private static void BuildOrdinarySide(byte weak, byte strong, Span<byte> side)
    {
        side.Clear();
        side[0] = 0x50;
        BuildSwitchRumbleFrame(weak, strong, side.Slice(1, RumbleFrameBytes));
    }

    private static void BuildSwitchRumbleFrame(byte weak, byte strong, Span<byte> outFrame)
    {
        ushort lowAmp = ScaleAmplitude(strong, OrdinaryMaxAmplitude);
        ushort highAmp = ScaleAmplitude(weak, OrdinaryMaxAmplitude);
        // Ordinary rumble supplies intensity only. Reference controls are not
        // measured Hz, and do not change timbre when the intensity changes.
        ushort lowFreq = Pro2RumbleFrequencyPolicy.ReferenceLowControl;
        ushort highFreq = Pro2RumbleFrequencyPolicy.ReferenceHighControl;

        EncodeSwitchRumbleFrame(highFreq, highAmp, lowFreq, lowAmp, outFrame);
    }

    private static void EncodeSwitchRumbleFrame(
        ushort highFreq,
        ushort highAmp,
        ushort lowFreq,
        ushort lowAmp,
        Span<byte> outFrame)
    {
        highFreq = (ushort)Math.Min(highFreq, (ushort)0x03ff);
        lowFreq = (ushort)Math.Min(lowFreq, (ushort)0x03ff);
        // Clamp before masking: an out-of-range amplitude must not wrap.
        // Quantise once; final transport applies user gain only to this code.
        highAmp = (ushort)(Math.Min(highAmp, OrdinaryMaxAmplitude) & 0xffc0);
        lowAmp = (ushort)(Math.Min(lowAmp, OrdinaryMaxAmplitude) & 0xffc0);

        outFrame[0] = (byte)(highFreq & 0xff);
        outFrame[1] = (byte)(((highFreq >> 8) & 0x03) | ((highAmp >> 4) & 0xfc));
        outFrame[2] = (byte)(((highAmp >> 12) & 0x0f) | ((lowFreq & 0x0f) << 4));
        outFrame[3] = (byte)(((lowFreq >> 4) & 0x3f) | (lowAmp & 0xc0));
        outFrame[4] = (byte)((lowAmp >> 8) & 0xff);
    }

    private static ushort ScaleAmplitude(byte value, ushort maxAmplitude)
    {
        if (maxAmplitude == 0)
        {
            return 0;
        }

        return (ushort)((value * (uint)maxAmplitude + 127u) / 255u);
    }

    private static bool SwitchFrameHasEffect(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < RumbleFrameBytes)
        {
            return false;
        }

        int highAmp = ((frame[1] & 0xfc) << 4) | ((frame[2] & 0x0f) << 12);
        int lowAmp = (frame[3] & 0xc0) | (frame[4] << 8);
        return highAmp != 0 || lowAmp != 0;
    }
}
