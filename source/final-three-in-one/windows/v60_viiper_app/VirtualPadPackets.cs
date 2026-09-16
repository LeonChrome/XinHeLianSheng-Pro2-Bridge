using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Y700Switch2V60Viiper;

public enum ViiperVirtualMode
{
    DualSenseLike,
    DualSenseEdge,
    Pro2,
    Xbox,
    DualSenseProfessionalImuTest,
    XboxProfessionalImuTest
}

public sealed record ViiperDeviceProfile(
    ViiperVirtualMode Mode,
    string Label,
    string DeviceType,
    string ExpectedVid,
    string ExpectedPid,
    int InputSize,
    int FeedbackSize,
    TimeSpan SendInterval)
{
    public string DeviceSpecificSerialNumber { get; init; } = "";
    public bool SourcePaced { get; init; }

    public static ViiperDeviceProfile DualSenseLike { get; } = new(
        ViiperVirtualMode.DualSenseLike,
        "新和联胜 / PS5",
        "dualsensehaptic",
        "054c",
        "0ce6",
        33,
        DualSenseHapticFrame.WireSize,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile DualSenseEdge { get; } = new(
        ViiperVirtualMode.DualSenseEdge,
        "PS5 Edge / 背键",
        "dualsenseedge",
        "054c",
        "0df2",
        33,
        6,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile Pro2 { get; } = new(
        ViiperVirtualMode.Pro2,
        "Pro2 / Nintendo",
        "ns2pro",
        "057e",
        "2069",
        28,
        34,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile Xbox { get; } = new(
        ViiperVirtualMode.Xbox,
        "Xbox / XInput",
        "xbox360",
        "045e",
        "028e",
        20,
        2,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile DualSenseProfessionalImuTest { get; } = new(
        ViiperVirtualMode.DualSenseProfessionalImuTest,
        "PS5 / Professional IMU Test",
        "dualsensehaptic",
        "054c",
        "0ce6",
        33,
        DualSenseHapticFrame.WireSize,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile XboxProfessionalImuTest { get; } = new(
        ViiperVirtualMode.XboxProfessionalImuTest,
        "Xbox / Professional IMU Test",
        "xbox360",
        "045e",
        "028e",
        20,
        2,
        TimeSpan.FromMilliseconds(4));

    public bool IsPs5Family =>
        Mode is ViiperVirtualMode.DualSenseLike
            or ViiperVirtualMode.DualSenseEdge
            or ViiperVirtualMode.DualSenseProfessionalImuTest;

    public bool UsesDualSenseHaptics =>
        Mode is ViiperVirtualMode.DualSenseLike
            or ViiperVirtualMode.DualSenseProfessionalImuTest;

    public bool IsProfessionalImuTest =>
        Mode is ViiperVirtualMode.DualSenseProfessionalImuTest
            or ViiperVirtualMode.XboxProfessionalImuTest;

    public bool MatchesIdentity(ViiperDevice device)
    {
        bool typeOk = string.Equals(
            DeviceType,
            device.Type,
            StringComparison.OrdinalIgnoreCase);
        bool vidOk = string.Equals(
            NormalizeHex(ExpectedVid),
            NormalizeHex(device.Vid),
            StringComparison.OrdinalIgnoreCase);
        bool pidOk = string.Equals(
            NormalizeHex(ExpectedPid),
            NormalizeHex(device.Pid),
            StringComparison.OrdinalIgnoreCase);
        return typeOk && vidOk && pidOk;
    }

    public IReadOnlyDictionary<string, object?> DeviceSpecificArguments()
    {
        var arguments = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(DeviceSpecificSerialNumber))
        {
            arguments["serial_number"] = DeviceSpecificSerialNumber.Trim();
        }

        if (Mode == ViiperVirtualMode.Pro2)
        {
            arguments["input_interval_ms"] = Math.Clamp(
                (int)Math.Round(SendInterval.TotalMilliseconds),
                1,
                255);
            arguments["source_paced"] = SourcePaced;
        }

        return arguments;
    }

    public static string SlotSerialNumber(ViiperVirtualMode mode, int slotIndex)
    {
        int normalizedSlot = Math.Clamp(slotIndex, 1, 4);
        return mode switch
        {
            ViiperVirtualMode.Pro2 => "LC-V624-NS2PRO-S" + normalizedSlot,
            ViiperVirtualMode.DualSenseLike => "LC-V624-DS5-S" + normalizedSlot,
            ViiperVirtualMode.DualSenseEdge => "LC-V624-EDGE-S" + normalizedSlot,
            ViiperVirtualMode.Xbox => "LC-V624-XBOX-S" + normalizedSlot,
            _ => "LC-V624-VPAD-S" + normalizedSlot
        };
    }

    private static string NormalizeHex(string value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        if (text.StartsWith("0x", StringComparison.Ordinal))
        {
            text = text[2..];
        }
        if (text.StartsWith("vid_", StringComparison.Ordinal) ||
            text.StartsWith("pid_", StringComparison.Ordinal))
        {
            text = text[4..];
        }
        return text.PadLeft(4, '0');
    }
}

public static class VirtualPadPackets
{
    private const ushort AxisCenter = GamepadState.AxisCenter;
    private const ushort AxisMax = GamepadState.AxisMax;
    private const ushort TriggerMax = GamepadState.TriggerMax;
    private const double Pro2AccelRawPerG = ProfessionalImuConverter.SwitchAccelRawPerG;
    private const double Pro2GyroRawPerDps = ProfessionalImuConverter.SwitchGyroRawPerDps;
    private static readonly MotionVector Ps5NeutralAccel = new(0, 0, -8192);

    public static byte[] NeutralInput(ViiperDeviceProfile profile)
    {
        return profile.Mode switch
        {
            ViiperVirtualMode.DualSenseLike => DualSenseNeutral(),
            ViiperVirtualMode.DualSenseEdge => DualSenseNeutral(),
            ViiperVirtualMode.DualSenseProfessionalImuTest => DualSenseNeutral(),
            ViiperVirtualMode.Pro2 => Ns2ProNeutral(),
            ViiperVirtualMode.Xbox => new byte[20],
            ViiperVirtualMode.XboxProfessionalImuTest => new byte[20],
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }

    public static byte[] FromGamepad(
        ViiperDeviceProfile profile,
        GamepadState? state,
        GyroAxisInversion gyroAxisInversion = default,
        Ps5ImuMapping? ps5ImuMapping = null,
        Ps5OutputImuTuning? ps5OutputImuTuning = null,
        DualSenseImuRawSample? professionalDualSenseImu = null,
        GamepadState? professionalXboxState = null)
    {
        state ??= GamepadState.Neutral();
        return profile.Mode switch
        {
            ViiperVirtualMode.DualSenseLike => DualSenseFromGamepad(
                state,
                gyroAxisInversion,
                ps5ImuMapping ?? Ps5ImuMappingOption.Default.Mapping,
                ps5OutputImuTuning ?? Ps5OutputImuTuning.Default,
                includeEdgePaddles: false,
                professionalImu: null),
            ViiperVirtualMode.DualSenseEdge => DualSenseFromGamepad(
                state,
                gyroAxisInversion,
                ps5ImuMapping ?? Ps5ImuMappingOption.Default.Mapping,
                ps5OutputImuTuning ?? Ps5OutputImuTuning.Default,
                includeEdgePaddles: true,
                professionalImu: null),
            ViiperVirtualMode.DualSenseProfessionalImuTest => DualSenseFromGamepad(
                state,
                gyroAxisInversion,
                ps5ImuMapping ?? Ps5ImuMappingOption.Default.Mapping,
                ps5OutputImuTuning ?? Ps5OutputImuTuning.Default,
                includeEdgePaddles: false,
                professionalImu: professionalDualSenseImu),
            ViiperVirtualMode.Pro2 => Ns2ProFromGamepad(state, gyroAxisInversion),
            ViiperVirtualMode.Xbox => XboxFromGamepad(state),
            ViiperVirtualMode.XboxProfessionalImuTest => XboxFromGamepad(professionalXboxState ?? state),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }

    public static string FeedbackSummary(ViiperDeviceProfile profile, byte[] data)
    {
        return profile.Mode switch
        {
            ViiperVirtualMode.DualSenseLike when
                DualSenseHapticFrame.TryParse(data, out DualSenseHapticFrame frame, out _) =>
                $"DualSense haptic frame: kind={frame.Kind}, bytes={frame.Payload.Length}",
            ViiperVirtualMode.DualSenseProfessionalImuTest when
                DualSenseHapticFrame.TryParse(data, out DualSenseHapticFrame frame, out _) =>
                $"DualSense Professional haptic frame: kind={frame.Kind}, bytes={frame.Payload.Length}",
            ViiperVirtualMode.DualSenseEdge when data.Length >= 6 =>
                $"DualSense Edge output: small={data[0]}, large={data[1]}, led=({data[2]},{data[3]},{data[4]}), player={data[5]}",
            ViiperVirtualMode.Pro2 when data.Length >= 34 =>
                $"NS2Pro output: flags=0x{data[32]:X2}, player_led=0x{data[33]:X2}, L={Hex(data.AsSpan(0, 6))}, R={Hex(data.AsSpan(16, 6))}",
            ViiperVirtualMode.Xbox when data.Length >= 2 =>
                $"XInput output: left={data[0]}, right={data[1]}",
            ViiperVirtualMode.XboxProfessionalImuTest when data.Length >= 2 =>
                $"XInput Professional output: left={data[0]}, right={data[1]}",
            _ => profile.Label + " output: " + Hex(data)
        };
    }

    private static byte[] DualSenseNeutral()
    {
        byte[] b = new byte[33];
        WriteI16(b, 27, Ps5NeutralAccel.X);
        WriteI16(b, 29, Ps5NeutralAccel.Y);
        WriteI16(b, 31, Ps5NeutralAccel.Z);
        return b;
    }

    private static byte[] Ns2ProNeutral()
    {
        byte[] b = new byte[28];
        WriteU16(b, 4, 0x0800);
        WriteU16(b, 6, 0x0800);
        WriteU16(b, 8, 0x0800);
        WriteU16(b, 10, 0x0800);
        return b;
    }

    private static byte[] DualSenseFromGamepad(
        GamepadState state,
        GyroAxisInversion gyroAxisInversion,
        Ps5ImuMapping ps5ImuMapping,
        Ps5OutputImuTuning ps5OutputImuTuning,
        bool includeEdgePaddles,
        DualSenseImuRawSample? professionalImu)
    {
        byte[] b = new byte[33];
        b[0] = unchecked((byte)Axis12ToI8(state.Lx, invert: false));
        b[1] = unchecked((byte)Axis12ToI8(state.Ly, invert: true));
        b[2] = unchecked((byte)Axis12ToI8(state.Rx, invert: false));
        b[3] = unchecked((byte)Axis12ToI8(state.Ry, invert: true));
        WriteU32(b, 4, DualSenseButtons(state, includeEdgePaddles));
        b[8] = DualSenseDpad(state);
        b[9] = Trigger12ToU8(state.L2, state.IsPressed(GamepadButtons.L2));
        b[10] = Trigger12ToU8(state.R2, state.IsPressed(GamepadButtons.R2));

        if (professionalImu is { Valid: true } proImu)
        {
            WriteI16(b, 21, proImu.GyroX);
            WriteI16(b, 23, proImu.GyroY);
            WriteI16(b, 25, proImu.GyroZ);
            WriteI16(b, 27, proImu.AccelX);
            WriteI16(b, 29, proImu.AccelY);
            WriteI16(b, 31, proImu.AccelZ);
        }
        else
        {
            DualSenseImuRawSample dualSenseImu = ConvertPs5FamilyImuToDualSenseRaw(
                state,
                gyroAxisInversion,
                ps5OutputImuTuning.Normalize());
            WriteI16(b, 21, state.GyroValid ? dualSenseImu.GyroX : (short)0);
            WriteI16(b, 23, state.GyroValid ? dualSenseImu.GyroY : (short)0);
            WriteI16(b, 25, state.GyroValid ? dualSenseImu.GyroZ : (short)0);
            WriteI16(b, 27, state.AccelValid ? dualSenseImu.AccelX : Ps5NeutralAccel.X);
            WriteI16(b, 29, state.AccelValid ? dualSenseImu.AccelY : Ps5NeutralAccel.Y);
            WriteI16(b, 31, state.AccelValid ? dualSenseImu.AccelZ : Ps5NeutralAccel.Z);
        }
        return b;
    }

    private static byte[] Ns2ProFromGamepad(
        GamepadState state,
        GyroAxisInversion gyroAxisInversion)
    {
        byte[] b = new byte[28];
        WriteU32(b, 0, Ns2ProButtons(state));
        WriteU16(b, 4, SnapAxisCenter(state.Lx));
        WriteU16(b, 6, SnapAxisCenter(state.Ly));
        WriteU16(b, 8, SnapAxisCenter(state.Rx));
        WriteU16(b, 10, SnapAxisCenter(state.Ry));
        MotionVector ns2ProAccel = MapNs2ProAccel(state);
        MotionVector ns2ProGyro = MapNs2ProGyro(state, gyroAxisInversion);
        WriteI16(b, 12, state.AccelValid ? ns2ProAccel.X : (short)0);
        WriteI16(b, 14, state.AccelValid ? ns2ProAccel.Y : (short)0);
        WriteI16(b, 16, state.AccelValid ? ns2ProAccel.Z : (short)0);
        WriteI16(b, 18, state.GyroValid ? ns2ProGyro.X : (short)0);
        WriteI16(b, 20, state.GyroValid ? ns2ProGyro.Y : (short)0);
        WriteI16(b, 22, state.GyroValid ? ns2ProGyro.Z : (short)0);
        WriteU32(b, 24, state.MotionTimestampUs);
        return b;
    }

    private static byte[] XboxFromGamepad(GamepadState state)
    {
        byte[] b = new byte[20];
        WriteU32(b, 0, XboxButtons(state));
        b[4] = Trigger12ToU8(state.L2, state.IsPressed(GamepadButtons.L2));
        b[5] = Trigger12ToU8(state.R2, state.IsPressed(GamepadButtons.R2));
        WriteI16(b, 6, Axis12ToI16(state.Lx, invert: false));
        WriteI16(b, 8, Axis12ToI16(state.Ly, invert: false));
        WriteI16(b, 10, Axis12ToI16(state.Rx, invert: false));
        WriteI16(b, 12, Axis12ToI16(state.Ry, invert: false));
        return b;
    }

    private static uint DualSenseButtons(GamepadState state, bool includeEdgePaddles)
    {
        uint buttons = 0;
        if (state.IsPressed(GamepadButtons.West)) buttons |= 0x00000010;
        if (state.IsPressed(GamepadButtons.South)) buttons |= 0x00000020;
        if (state.IsPressed(GamepadButtons.East)) buttons |= 0x00000040;
        if (state.IsPressed(GamepadButtons.North)) buttons |= 0x00000080;
        if (state.IsPressed(GamepadButtons.L1)) buttons |= 0x00000100;
        if (state.IsPressed(GamepadButtons.R1)) buttons |= 0x00000200;
        if (state.IsPressed(GamepadButtons.L2)) buttons |= 0x00000400;
        if (state.IsPressed(GamepadButtons.R2)) buttons |= 0x00000800;
        if (state.IsPressed(GamepadButtons.Back)) buttons |= 0x00001000;
        if (state.IsPressed(GamepadButtons.Start)) buttons |= 0x00002000;
        if (state.IsPressed(GamepadButtons.LeftStick)) buttons |= 0x00004000;
        if (state.IsPressed(GamepadButtons.RightStick)) buttons |= 0x00008000;
        if (state.IsPressed(GamepadButtons.Home)) buttons |= 0x00010000;
        if (state.IsPressed(GamepadButtons.Capture)) buttons |= 0x00020000;
        if (includeEdgePaddles && state.IsPressed(GamepadButtons.PaddleLeft)) buttons |= 0x00400000;
        if (includeEdgePaddles && state.IsPressed(GamepadButtons.PaddleRight)) buttons |= 0x00800000;
        return buttons;
    }

    private static byte DualSenseDpad(GamepadState state)
    {
        byte dpad = 0;
        if (state.IsPressed(GamepadButtons.DPadUp)) dpad |= 0x01;
        if (state.IsPressed(GamepadButtons.DPadDown)) dpad |= 0x02;
        if (state.IsPressed(GamepadButtons.DPadLeft)) dpad |= 0x04;
        if (state.IsPressed(GamepadButtons.DPadRight)) dpad |= 0x08;
        return dpad;
    }

    private static uint Ns2ProButtons(GamepadState state)
    {
        uint buttons = 0;
        if (state.IsPressed(GamepadButtons.South)) buttons |= 0x00000001;
        if (state.IsPressed(GamepadButtons.East)) buttons |= 0x00000002;
        if (state.IsPressed(GamepadButtons.West)) buttons |= 0x00000004;
        if (state.IsPressed(GamepadButtons.North)) buttons |= 0x00000008;
        if (state.IsPressed(GamepadButtons.R1)) buttons |= 0x00000010;
        if (state.IsPressed(GamepadButtons.R2)) buttons |= 0x00000020;
        if (state.IsPressed(GamepadButtons.Start)) buttons |= 0x00000040;
        if (state.IsPressed(GamepadButtons.RightStick)) buttons |= 0x00000080;
        if (state.IsPressed(GamepadButtons.DPadDown)) buttons |= 0x00000100;
        if (state.IsPressed(GamepadButtons.DPadRight)) buttons |= 0x00000200;
        if (state.IsPressed(GamepadButtons.DPadLeft)) buttons |= 0x00000400;
        if (state.IsPressed(GamepadButtons.DPadUp)) buttons |= 0x00000800;
        if (state.IsPressed(GamepadButtons.L1)) buttons |= 0x00001000;
        if (state.IsPressed(GamepadButtons.L2)) buttons |= 0x00002000;
        if (state.IsPressed(GamepadButtons.Back)) buttons |= 0x00004000;
        if (state.IsPressed(GamepadButtons.LeftStick)) buttons |= 0x00008000;
        if (state.IsPressed(GamepadButtons.Home)) buttons |= 0x00010000;
        if (state.IsPressed(GamepadButtons.Capture)) buttons |= 0x00020000;
        if (state.IsPressed(GamepadButtons.PaddleRight)) buttons |= 0x00040000;
        if (state.IsPressed(GamepadButtons.PaddleLeft)) buttons |= 0x00080000;
        if (state.IsPressed(GamepadButtons.Aux)) buttons |= 0x00100000;
        return buttons;
    }

    private static uint XboxButtons(GamepadState state)
    {
        uint buttons = 0;
        if (state.IsPressed(GamepadButtons.DPadUp)) buttons |= 0x0001;
        if (state.IsPressed(GamepadButtons.DPadDown)) buttons |= 0x0002;
        if (state.IsPressed(GamepadButtons.DPadLeft)) buttons |= 0x0004;
        if (state.IsPressed(GamepadButtons.DPadRight)) buttons |= 0x0008;
        if (state.IsPressed(GamepadButtons.Start)) buttons |= 0x0010;
        if (state.IsPressed(GamepadButtons.Back)) buttons |= 0x0020;
        if (state.IsPressed(GamepadButtons.LeftStick)) buttons |= 0x0040;
        if (state.IsPressed(GamepadButtons.RightStick)) buttons |= 0x0080;
        if (state.IsPressed(GamepadButtons.L1)) buttons |= 0x0100;
        if (state.IsPressed(GamepadButtons.R1)) buttons |= 0x0200;
        if (state.IsPressed(GamepadButtons.Home)) buttons |= 0x0400;
        if (state.IsPressed(GamepadButtons.South)) buttons |= 0x1000;
        if (state.IsPressed(GamepadButtons.East)) buttons |= 0x2000;
        if (state.IsPressed(GamepadButtons.West)) buttons |= 0x4000;
        if (state.IsPressed(GamepadButtons.North)) buttons |= 0x8000;
        return buttons;
    }

    private static ushort SnapAxisCenter(ushort value)
    {
        value = ClampAxis(value);
        int delta = value - AxisCenter;
        if (delta < 0) delta = -delta;
        return delta <= 64 ? AxisCenter : value;
    }

    private static MotionVector MapNs2ProGyro(
        GamepadState state,
        GyroAxisInversion gyroAxisInversion)
    {
        return ApplyGyroAxisInversion(new MotionVector(
            state.GyroX,
            state.GyroY,
            state.GyroZ), gyroAxisInversion);
    }

    private static MotionVector MapNs2ProAccel(GamepadState state)
    {
        return new MotionVector(
            state.AccelX,
            state.AccelY,
            state.AccelZ);
    }

    private static MotionVector ApplyGyroAxisInversion(
        MotionVector vector,
        GyroAxisInversion gyroAxisInversion)
    {
        return new MotionVector(
            gyroAxisInversion.InvertX ? NegateI16(vector.X) : vector.X,
            gyroAxisInversion.InvertY ? NegateI16(vector.Y) : vector.Y,
            gyroAxisInversion.InvertZ ? NegateI16(vector.Z) : vector.Z);
    }

    private static MotionVector ApplyPs5AccelOutputTuning(MotionVector mappedAccel)
    {
        // Switch raw accel is 4096/g in the Professional path; DualSense final
        // HID raw is 8192/g. Axis sign is carried by the mapping itself.
        return new MotionVector(
            ScaleI16(mappedAccel.X, 2.0),
            ScaleI16(mappedAccel.Y, 2.0),
            ScaleI16(mappedAccel.Z, 2.0));
    }

    private static DualSenseImuRawSample ConvertPs5FamilyImuToDualSenseRaw(
        GamepadState state,
        GyroAxisInversion gyroAxisInversion,
        Ps5OutputImuTuning tuning)
    {
        // Switch reports carry three chronological 5 ms IMU samples, not
        // three sensors. The last sample is the freshest and is already
        // calibrated by Pro2HidReportParser. Averaging the batch would add
        // about 5 ms of group delay and attenuate fast reversals.
        Ps5SourceMotion source = new(
            state.AccelX,
            state.AccelY,
            state.AccelZ,
            state.GyroX,
            state.GyroY,
            state.GyroZ);
        double accelXG = source.AccelX / Pro2AccelRawPerG;
        double accelYG = source.AccelZ / Pro2AccelRawPerG;
        double accelZG = -source.AccelY / Pro2AccelRawPerG;

        // DualSense target axes: +source X, +source Z, -source Y.
        double gyroX = source.GyroX / Pro2GyroRawPerDps;
        double gyroY = source.GyroZ / Pro2GyroRawPerDps;
        double gyroZ = -source.GyroY / Pro2GyroRawPerDps;
        if (gyroAxisInversion.InvertX) gyroX = -gyroX;
        if (gyroAxisInversion.InvertY) gyroY = -gyroY;
        if (gyroAxisInversion.InvertZ) gyroZ = -gyroZ;

        return new DualSenseImuRawSample(
            ClampRoundToInt16(accelXG * ProfessionalImuConverter.DualSenseAccelRawPerG),
            ClampRoundToInt16(accelYG * ProfessionalImuConverter.DualSenseAccelRawPerG),
            ClampRoundToInt16(accelZG * ProfessionalImuConverter.DualSenseAccelRawPerG),
            ClampRoundToInt16(gyroX * ProfessionalImuConverter.DualSenseGyroRawPerDps * tuning.GyroScalePitch),
            ClampRoundToInt16(gyroY * ProfessionalImuConverter.DualSenseGyroRawPerDps * tuning.GyroScaleYaw),
            ClampRoundToInt16(gyroZ * ProfessionalImuConverter.DualSenseGyroRawPerDps * tuning.GyroScaleRoll),
            Valid: true);
    }

    private static short ClampRoundToInt16(double value)
    {
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < short.MinValue) return short.MinValue;
        if (rounded > short.MaxValue) return short.MaxValue;
        return (short)rounded;
    }

    private static short ScaleI16(short value, double scale)
    {
        double scaled = Math.Round(value * scale, MidpointRounding.AwayFromZero);
        if (scaled < short.MinValue) return short.MinValue;
        if (scaled > short.MaxValue) return short.MaxValue;
        return (short)scaled;
    }

    private static short NegateI16(short value)
    {
        return value == short.MinValue ? short.MaxValue : (short)-value;
    }

    private static sbyte Axis12ToI8(ushort value, bool invert)
    {
        short i16 = Axis12ToI16(value, invert);
        int scaled = i16 >= 0 ? (i16 * 127 + 16383) / 32767 : (i16 * 128 - 16384) / 32768;
        if (scaled < -128) scaled = -128;
        if (scaled > 127) scaled = 127;
        return (sbyte)scaled;
    }

    private static short Axis12ToI16(ushort value, bool invert)
    {
        value = SnapAxisCenter(value);
        int centered = value - AxisCenter;
        int scaled = centered >= 0
            ? (centered * 32767) / (AxisMax - AxisCenter)
            : (centered * 32768) / AxisCenter;
        if (invert) scaled = -scaled;
        if (scaled < short.MinValue) return short.MinValue;
        if (scaled > short.MaxValue) return short.MaxValue;
        return (short)scaled;
    }

    private static byte Trigger12ToU8(ushort value, bool pressed)
    {
        if (value == 0 && pressed)
        {
            return 255;
        }
        uint scaled = ((uint)value * 255u + TriggerMax / 2u) / TriggerMax;
        return (byte)Math.Min(255u, scaled);
    }

    private static ushort ClampAxis(ushort value)
    {
        return value > AxisMax ? AxisMax : value;
    }

    private static void WriteU16(byte[] b, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset, 2), value);
    }

    private static void WriteI16(byte[] b, int offset, short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(offset, 2), value);
    }

    private static void WriteU32(byte[] b, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset, 4), value);
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private readonly record struct MotionVector(short X, short Y, short Z);

    private readonly record struct Ps5SourceMotion(
        double AccelX,
        double AccelY,
        double AccelZ,
        double GyroX,
        double GyroY,
        double GyroZ);
}
