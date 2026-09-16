using System;

namespace Y700Switch2V60Viiper;

public sealed record Pro2OutputPacket(byte[] Report, string Source, bool Active, byte? PlayerLedMask);

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
            case ViiperVirtualMode.DualSenseEdge:
                if (feedback.Length < 2)
                {
                    reason = "dualsense edge feedback too short";
                    return false;
                }
                packet = BuildOrdinaryPacket(feedback[0], feedback[1], "dualsense-edge-ordinary");
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

        bool active =
            SwitchFrameHasEffect(report.AsSpan(RumbleFrameOffsetLeft, RumbleFrameBytes)) ||
            SwitchFrameHasEffect(report.AsSpan(RumbleFrameOffsetRight, RumbleFrameBytes));
        packet = new Pro2OutputPacket(report, "ns2pro-hd", active, playerLed);
        return true;
    }

    public static Pro2OutputPacket BuildOrdinaryPacket(
        byte weak,
        byte strong,
        string source)
    {
        byte[] report = NewSwitch2OutputReport();
        BuildOrdinarySide(weak, strong, report.AsSpan(RumbleSideOffsetLeft, 16));
        BuildOrdinarySide(weak, strong, report.AsSpan(RumbleSideOffsetRight, 16));
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
        bool active =
            lowAmplitudeLeft != 0 ||
            highAmplitudeLeft != 0 ||
            lowAmplitudeRight != 0 ||
            highAmplitudeRight != 0;
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
        ushort lowFreq = lowAmp == 0 ? (ushort)0x0e1 : MixFrequency(0x0b8, 0x122, strong);
        ushort highFreq = highAmp == 0 ? (ushort)0x1e1 : MixFrequency(0x160, 0x1f0, weak);

        EncodeSwitchRumbleFrame(highFreq, highAmp, lowFreq, lowAmp, outFrame);
    }

    private static void EncodeSwitchRumbleFrame(
        ushort highFreq,
        ushort highAmp,
        ushort lowFreq,
        ushort lowAmp,
        Span<byte> outFrame)
    {
        highFreq &= 0x03ff;
        lowFreq &= 0x03ff;
        highAmp &= 0xffc0;
        lowAmp &= 0xffc0;

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

    private static ushort MixFrequency(ushort low, ushort high, byte value)
    {
        return (ushort)(low + (((uint)(high - low) * value + 127u) / 255u));
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
