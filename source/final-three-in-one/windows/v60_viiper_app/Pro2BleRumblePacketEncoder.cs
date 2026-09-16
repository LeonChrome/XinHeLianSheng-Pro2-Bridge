using System;

namespace Y700Switch2V60Viiper;

public static class Pro2BleRumblePacketEncoder
{
    public const int Raw02ReportSize = 64;
    public const int BlePacketSize = 33;

    private const int LeftFrameOffset = 2;
    private const int RightFrameOffset = 0x12;
    private const int RumbleFrameBytes = 5;
    public static bool TryEncodeRaw02(
        ReadOnlySpan<byte> report,
        byte packetId,
        Span<byte> packet,
        out bool active,
        out string error,
        double gain = 1.0)
    {
        active = false;
        error = "";
        gain = ClampGain(gain);

        if (packet.Length < BlePacketSize)
        {
            error = "BLE rumble packet buffer too small";
            return false;
        }

        if (report.Length != Raw02ReportSize)
        {
            error = "raw02 HID report length must be 64 bytes";
            return false;
        }

        if (report[0] != 0x02)
        {
            error = "raw02 HID report id must be 0x02";
            return false;
        }

        if (!IsSwitch2HidRumbleReport(report))
        {
            error = "raw02 HID report does not contain a 0x50 rumble block";
            return false;
        }

        Span<byte> left = stackalloc byte[RumbleFrameBytes];
        Span<byte> right = stackalloc byte[RumbleFrameBytes];
        active = gain > 0 &&
                 (SwitchFrameHasEffect(report.Slice(LeftFrameOffset, RumbleFrameBytes)) ||
                  SwitchFrameHasEffect(report.Slice(RightFrameOffset, RumbleFrameBytes)));

        if (active && !IsNeutralSwitchRumble(report))
        {
            EncodeBleVibrationFromSwitchFrame(report, LeftFrameOffset, gain, left);
            EncodeBleVibrationFromSwitchFrame(report, RightFrameOffset, gain, right);
        }
        else
        {
            BuildZeroBleVibration(left);
            BuildZeroBleVibration(right);
        }

        BuildPro2HdPacket(packetId, left, right, packet);
        return true;
    }

    private static bool IsSwitch2HidRumbleReport(ReadOnlySpan<byte> report)
    {
        return report.Length >= 7 &&
               report[0] == 0x02 &&
               (report[1] & 0xf0) == 0x50;
    }

    private static bool IsNeutralSwitchRumble(ReadOnlySpan<byte> report)
    {
        return HasNeutralRumbleFrame(report, LeftFrameOffset) &&
               HasNeutralRumbleFrame(report, RightFrameOffset);
    }

    private static bool HasNeutralRumbleFrame(ReadOnlySpan<byte> report, int offset)
    {
        return report.Length >= offset + RumbleFrameBytes &&
               report[offset] == 0x87 &&
               report[offset + 1] == 0x01 &&
               report[offset + 2] == 0x20 &&
               report[offset + 3] == 0x11 &&
               report[offset + 4] == 0x00;
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

    private static int MapSwitchAmpToBle(int value, double gain)
    {
        double mapped = value * 1023.0 * gain / 29000.0;
        if (mapped <= 0)
        {
            return 0;
        }
        if (mapped >= 1023)
        {
            return 1023;
        }
        return Clamp((int)Math.Round(mapped), 0, 1023);
    }

    private static void EncodeBleVibrationFromSwitchFrame(
        ReadOnlySpan<byte> report,
        int offset,
        double gain,
        Span<byte> output)
    {
        if (report.Length < offset + RumbleFrameBytes)
        {
            BuildZeroBleVibration(output);
            return;
        }

        int b0 = report[offset];
        int b1 = report[offset + 1];
        int b2 = report[offset + 2];
        int b3 = report[offset + 3];
        int b4 = report[offset + 4];

        int highFreq = b0 | ((b1 & 0x03) << 8);
        int highAmp = ((b1 & 0xfc) << 4) | ((b2 & 0x0f) << 12);
        int lowFreq = ((b2 & 0xf0) >> 4) | ((b3 & 0x3f) << 4);
        int lowAmp = (b3 & 0xc0) | (b4 << 8);

        BuildBleVibrationData(
            (ushort)lowFreq,
            false,
            (ushort)MapSwitchAmpToBle(lowAmp, gain),
            (ushort)highFreq,
            false,
            (ushort)MapSwitchAmpToBle(highAmp, gain),
            output);
    }

    private static void BuildZeroBleVibration(Span<byte> output)
    {
        BuildBleVibrationData(0x0e1, false, 0, 0x1e1, false, 0, output);
    }

    private static void BuildBleVibrationData(
        ushort lowFreq,
        bool lowTone,
        ushort lowAmp,
        ushort highFreq,
        bool highTone,
        ushort highAmp,
        Span<byte> output)
    {
        ulong value = 0;
        value |= lowFreq & 0x01ffUL;
        value |= (lowTone ? 1UL : 0UL) << 9;
        value |= ((ulong)lowAmp & 0x03ffUL) << 10;
        value |= ((ulong)highFreq & 0x01ffUL) << 20;
        value |= (highTone ? 1UL : 0UL) << 29;
        value |= ((ulong)highAmp & 0x03ffUL) << 30;

        for (int i = 0; i < RumbleFrameBytes; i++)
        {
            output[i] = (byte)((value >> (8 * i)) & 0xff);
        }
    }

    private static void BuildPro2HdPacket(
        byte packetId,
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right,
        Span<byte> output)
    {
        Span<byte> zero = stackalloc byte[RumbleFrameBytes];
        BuildZeroBleVibration(zero);

        output.Slice(0, BlePacketSize).Clear();
        output[0] = 0x00;
        WriteMotorBlock(output, 1, packetId, left, zero);
        WriteMotorBlock(output, 17, packetId, right, zero);
    }

    private static void WriteMotorBlock(
        Span<byte> output,
        int offset,
        byte packetId,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> zero)
    {
        output[offset] = (byte)(0x50 | (packetId & 0x0f));
        first.CopyTo(output.Slice(offset + 1, RumbleFrameBytes));
        zero.CopyTo(output.Slice(offset + 6, RumbleFrameBytes));
        zero.CopyTo(output.Slice(offset + 11, RumbleFrameBytes));
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static double ClampGain(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1.0;
        }
        return Math.Clamp(value, 0.0, 3.0);
    }
}
