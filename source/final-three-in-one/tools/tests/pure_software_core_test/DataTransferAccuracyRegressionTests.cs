using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class DataTransferAccuracyRegressionTests
{
    private static readonly ViiperDeviceProfile[] Profiles =
        [ViiperDeviceProfile.Pro2, ViiperDeviceProfile.Pro1, ViiperDeviceProfile.DualSenseLike, ViiperDeviceProfile.Xbox];

    public static void Run(Action<bool, string> check)
    {
        TestNeutralAndInvalidMotion(check);
        TestSessionLocalInvalidGravity(check);
        TestPhysicalButtonsAndDpad(check);
        TestEntireStickRange(check);
        TestEntireTriggerRange(check);
        TestSignedMotionBounds(check);
        foreach (var profile in Profiles)
        foreach (bool paced in new[] { false, true })
        {
            var configured = profile with { SourcePaced = paced };
            var arguments = configured.DeviceSpecificArguments();
            check(arguments.TryGetValue("source_paced", out var value) && value is bool actual && actual == paced,
                "all modes pass the actual source-paced report policy: " + profile.Mode);
            check(arguments.ContainsKey("input_interval_ms") == (profile.Mode is ViiperVirtualMode.Pro1 or ViiperVirtualMode.Pro2),
                "only Nintendo protocols consume their input interval metadata: " + profile.Mode);
            check(arguments.ContainsKey("protocol_clock_resample") == (profile.Mode == ViiperVirtualMode.Pro2),
                "only Pro2 enables SDL-compatible endpoint clock resampling: " + profile.Mode);
            if (profile.Mode == ViiperVirtualMode.Pro2)
                check(arguments["protocol_clock_resample"] is bool resample && resample == paced,
                    "Pro2 USB-only resampling must not disable source-paced physical input");
        }
    }

    private static void TestSessionLocalInvalidGravity(Action<bool, string> check)
    {
        var first = new MotionValidityPolicy();
        var second = new MotionValidityPolicy();
        var tilted = Motion(1024, 2048, 3072);
        tilted.Buttons = GamepadButtons.East | GamepadButtons.L2;
        tilted.Lx = 1234;
        tilted.Ry = 3210;
        tilted.L2 = 4095;
        tilted.Updates = 71;
        tilted.MotionTimestampUs = 123456;
        tilted.SourceTimestampTicks = 777;
        tilted.RawNotificationSequence = 888;
        first.Resolve(tilted);
        second.Resolve(Motion(-1000, -2000, -3000));
        var missing = tilted.Clone();
        missing.AccelValid = missing.GyroValid = false;
        missing.AccelX = 99;
        missing.GyroX = 777;
        GamepadState heldFirst = first.Resolve(missing);
        GamepadState heldSecond = second.Resolve(missing);
        check((heldFirst.AccelX, heldFirst.AccelY, heldFirst.AccelZ) == (1024, 2048, 3072) &&
            (heldSecond.AccelX, heldSecond.AccelY, heldSecond.AccelZ) == (-1000, -2000, -3000),
            "two session policies never share tilted gravity caches");
        check(!heldFirst.GyroValid && (heldFirst.GyroX, heldFirst.GyroY, heldFirst.GyroZ) == (0, 0, 0),
            "invalid gyro stops immediately instead of holding a nonzero angular rate");
        check(heldFirst.Buttons == missing.Buttons && heldFirst.Lx == missing.Lx && heldFirst.Ry == missing.Ry &&
            heldFirst.L2 == missing.L2 && heldFirst.Updates == 71 && heldFirst.MotionTimestampUs == 123456 &&
            heldFirst.SourceTimestampTicks == 777 && heldFirst.RawNotificationSequence == 888,
            "held gravity cannot change controls or manufacture source counters/timestamps");
        check(!missing.AccelValid && !missing.GyroValid && missing.AccelX == 99 && missing.GyroX == 777 &&
            missing.Updates == 71 && !ReferenceEquals(missing, heldFirst),
            "invalid gravity resolution only changes an output copy, never the actual source frame");
        first.Resolve(Motion(100, 200, 300));
        check((first.Resolve(missing).AccelX, first.Resolve(missing).AccelY, first.Resolve(missing).AccelZ) == (100, 200, 300),
            "fresh valid acceleration replaces the old held tilt immediately");
        GamepadState disconnected = first.Resolve(null);
        check(disconnected.Buttons == GamepadButtons.None && disconnected.Lx == 2048 && disconnected.L2 == 0 &&
            !disconnected.GyroValid && disconnected.GyroX == 0 && disconnected.Updates == 0 &&
            disconnected.MotionTimestampUs == 0 &&
            (disconnected.AccelX, disconnected.AccelY, disconnected.AccelZ) == (100, 200, 300),
            "disconnect neutral releases controls and gyro while retaining only local gravity");
        first.Reset();
        GamepadState initial = first.Resolve(null);
        check(!first.HasAcceleration && initial.AccelValid &&
            (initial.AccelX, initial.AccelY, initial.AccelZ) == (0, 0, 4096),
            "cleared/no-cache policy uses normal source gravity +Z -> SDL +Y without treating fallback as a measurement");

        // Exercise the actual session integration without opening a stream,
        // creating a virtual device or touching the radio.
        var client = new ViiperProtocolClient("127.0.0.1", 1);
        var sessionOne = new ViiperBridgeSession(client, ViiperDeviceProfile.DualSenseLike, new Progress<string>());
        var sessionTwo = new ViiperBridgeSession(client, ViiperDeviceProfile.DualSenseLike, new Progress<string>());
        MethodInfo map = typeof(ViiperBridgeSession).GetMethod("MapInputPacket", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] Map(ViiperBridgeSession session, GamepadState? state, DualSenseImuRawSample? professional = null) =>
            (byte[])map.Invoke(session, new object?[] { state, professional, null })!;
        byte[] valid = Map(sessionOne, tilted);
        Map(sessionTwo, Motion(-1000, -2000, -3000));
        var staleProfessional = new DualSenseImuRawSample(55, 66, 77, 888, 999, 111, true);
        byte[] invalid = Map(sessionOne, missing, staleProfessional);
        check(Read3(valid, 27) == Read3(invalid, 27) && Read3(invalid, 21) == (0, 0, 0),
            "real PS5 session maps tilted invalid frame without gravity jump or professional raw gyro bypass");
        check(Read3(Map(sessionTwo, missing), 27) == (-2000, -6000, 4000),
            "actual independent PS5 session has its own gravity history");
        check(Read3(Map(sessionOne, null), 27) == Read3(valid, 27) &&
            Read3(Map(sessionOne, null), 21) == (0, 0, 0),
            "actual disconnect neutral uses the same session-local gravity policy");
        sessionOne.StopAsync().GetAwaiter().GetResult();
        check(Read3(Map(sessionOne, null), 27) == (0, 8192, 0),
            "session Stop clears the gravity cache before reuse");
        sessionTwo.StopAsync().GetAwaiter().GetResult();
    }

    private static void TestNeutralAndInvalidMotion(Action<bool, string> check)
    {
        byte[] neutral = VirtualPadPackets.NeutralInput(ViiperDeviceProfile.DualSenseLike);
        check(Read3(neutral, 21) == (0, 0, 0) && Read3(neutral, 27) == (0, 8192, 0),
            "PS5 startup uses normal SDL gamepad gravity +Y, never artificial -Z");
        var flat = Motion(0, 0, 4096);
        byte[] live = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, flat);
        flat.GyroValid = flat.AccelValid = false;
        byte[] unavailable = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, flat);
        check(Read3(live, 27) == Read3(unavailable, 27),
            "PS5 stationary live -> unavailable does not rotate gravity 90 degrees");
        check(Read3(unavailable, 21) == (0, 0, 0), "unavailable PS5 gyro is zero, never stale angular velocity");
        var unrelated = Motion(999, 777, -888);
        _ = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, unrelated);
        check(VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, null).AsSpan().SequenceEqual(neutral),
            "stateless PS5 mapper cannot leak one controller's last gravity into another slot");
        foreach (var profile in Profiles)
            check(VirtualPadPackets.FromGamepad(profile, null).AsSpan().SequenceEqual(VirtualPadPackets.NeutralInput(profile)),
                "null source equals that profile's documented startup state: " + profile.Mode);
    }

    private static void TestPhysicalButtonsAndDpad(Action<bool, string> check)
    {
        // Protocol bit locations, independently enumerated by physical button
        // position. GL/GR/C have no native slots outside the Pro2 identity.
        uint[] ns = [1, 2, 4, 8, 0x1000, 0x10, 0x2000, 0x20, 0x4000, 0x40, 0x8000, 0x80,
            0x100, 0x200, 0x400, 0x800, 0x10000, 0x20000, 0x40000, 0x80000, 0x100000];
        uint[] ps = [0x20, 0x40, 0x10, 0x80, 0x100, 0x200, 0x400, 0x800, 0x1000, 0x2000,
            0x4000, 0x8000, 0, 0, 0, 0, 0x10000, 0x20000, 0, 0, 0];
        uint[] xbox = [0x1000, 0x2000, 0x4000, 0x8000, 0x100, 0x200, 0, 0, 0x20, 0x10,
            0x40, 0x80, 2, 8, 4, 1, 0x400, 0, 0, 0, 0];
        byte[] hats = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 8, 4, 1, 0, 0, 0, 0, 0];
        for (int sourceBit = 0; sourceBit < 21; ++sourceBit)
        foreach (var profile in Profiles)
        {
            var state = new GamepadState { Buttons = (GamepadButtons)(1UL << sourceBit) };
            byte[] data = VirtualPadPackets.FromGamepad(profile, state);
            uint actual = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(profile.IsPs5Family ? 4 : 0));
            uint expected = profile.Mode switch
            {
                ViiperVirtualMode.Pro2 => ns[sourceBit],
                ViiperVirtualMode.Pro1 => ns[sourceBit] & 0x0003ffff,
                ViiperVirtualMode.Xbox => xbox[sourceBit],
                _ => ps[sourceBit]
            };
            check(actual == expected, "single physical button has correct bit and no ghosts " + profile.Mode + "/" + sourceBit);
            if (profile.IsPs5Family)
                check(data[8] == hats[sourceBit], "PS5 dpad flags do not overlap face buttons");
            if (profile.IsPs5Family || profile.Mode == ViiperVirtualMode.Xbox)
            {
                int offset = profile.IsPs5Family ? 9 : 4;
                check(data[offset] == (sourceBit == 6 ? 255 : 0) && data[offset + 1] == (sourceBit == 7 ? 255 : 0),
                    "physical digital triggers retain full strength without cross-triggering");
            }
        }
        foreach (int flags in new[] { 0, 1, 2, 4, 8, 9, 5, 10, 6 })
        {
            GamepadButtons buttons = 0;
            if ((flags & 1) != 0) buttons |= GamepadButtons.DPadUp;
            if ((flags & 2) != 0) buttons |= GamepadButtons.DPadDown;
            if ((flags & 4) != 0) buttons |= GamepadButtons.DPadLeft;
            if ((flags & 8) != 0) buttons |= GamepadButtons.DPadRight;
            byte[] data = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, new() { Buttons = buttons });
            check(data[8] == flags, "all nine legal PS5 dpad positions including diagonals " + flags);
        }
    }

    private static void TestEntireStickRange(Action<bool, string> check)
    {
        foreach (var profile in Profiles)
        for (int axis = 0; axis < 4; ++axis)
        {
            int previous = int.MinValue;
            var outputs = new HashSet<int>();
            for (ushort value = 0; value <= GamepadState.AxisMax; ++value)
            {
                var state = new GamepadState();
                switch (axis) { case 0: state.Lx = value; break; case 1: state.Ly = value; break;
                    case 2: state.Rx = value; break; default: state.Ry = value; break; }
                byte[] data = VirtualPadPackets.FromGamepad(profile, state);
                int actual = Axis(data, profile, axis);
                int ordered = profile.IsPs5Family && (axis == 1 || axis == 3) ? -actual : actual;
                check(ordered >= previous, "stick conversion monotonic " + profile.Mode + "/" + axis + "/" + value);
                previous = ordered;
                outputs.Add(actual);
                if (profile.Mode is ViiperVirtualMode.Pro1 or ViiperVirtualMode.Pro2)
                    check(actual == value, "calibrated Nintendo 12-bit axis preserved without second center deadzone " + value);
                else if (profile.Mode == ViiperVirtualMode.Xbox)
                {
                    // Independent piecewise full-range XInput convention.
                    int expected = value >= 2048 ? (value - 2048) * 32767 / 2047 : (value - 2048) * 32768 / 2048;
                    check(actual == expected, "XInput source micro-movements preserved " + value);
                }
                for (int other = 0; other < 4; ++other)
                    if (other != axis)
                        check(Axis(data, profile, other) == (profile.IsPs5Family || profile.Mode == ViiperVirtualMode.Xbox ? 0 : 2048),
                            "one stick axis never moves another axis");
            }
            check(outputs.Count == (profile.IsPs5Family ? 256 : 4096),
                "all representable stick steps survive: " + profile.Mode + "/" + axis);
            var invalid = new GamepadState { Lx = ushort.MaxValue, Ly = ushort.MaxValue, Rx = ushort.MaxValue, Ry = ushort.MaxValue };
            var maximum = new GamepadState { Lx = 4095, Ly = 4095, Rx = 4095, Ry = 4095 };
            check(VirtualPadPackets.FromGamepad(profile, invalid).AsSpan().SequenceEqual(VirtualPadPackets.FromGamepad(profile, maximum)),
                "out-of-range axis clamps rather than wrapping " + profile.Mode);
        }
    }

    private static void TestEntireTriggerRange(Action<bool, string> check)
    {
        foreach (var profile in new[] { ViiperDeviceProfile.Xbox, ViiperDeviceProfile.DualSenseLike })
        for (int trigger = 0; trigger < 2; ++trigger)
        {
            int previous = -1;
            var outputs = new HashSet<byte>();
            for (ushort value = 0; value <= GamepadState.TriggerMax; ++value)
            {
                var state = new GamepadState();
                if (trigger == 0) state.L2 = value; else state.R2 = value;
                byte[] data = VirtualPadPackets.FromGamepad(profile, state);
                int offset = profile.IsPs5Family ? 9 : 4;
                byte actual = data[offset + trigger];
                check(actual >= previous && data[offset + 1 - trigger] == 0, "trigger monotonic without channel crosstalk");
                previous = actual;
                outputs.Add(actual);
            }
            check(outputs.Count == 256, "target trigger uses full eight-bit range");
        }
    }

    private static void TestSignedMotionBounds(Action<bool, string> check)
    {
        foreach (short value in new short[] { short.MinValue, -32767, -16385, -16384, -8192, -1, 0, 1, 8192, 16383, 16384, 32766, short.MaxValue })
        for (int axis = 0; axis < 3; ++axis)
        for (int mask = 0; mask < 8; ++mask)
        {
            short[] values = [0, 0, 0]; values[axis] = value;
            var state = Motion(values[0], values[1], values[2]);
            var inversion = new GyroAxisInversion((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
            int[] source = [inversion.InvertX ? Negate(values[0]) : values[0], inversion.InvertY ? Negate(values[1]) : values[1],
                inversion.InvertZ ? Negate(values[2]) : values[2]];
            byte[] pro2 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro2, state, inversion);
            byte[] pro1 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro1, state, inversion);
            byte[] ps5 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, state, inversion);
            check(Read3(pro2, 18) == (source[0], source[1], source[2]), "native Pro2 sensor axes remain source coordinates");
            check(Read3(pro1, 18) == (source[1], Negate(source[0]), source[2]), "Pro1 source reversal precedes protocol pre-rotation at signed endpoints");
            check(Read3(ps5, 21) == (Clamp(source[0]), Clamp(source[2]), Clamp(-source[1])),
                "PS5 sensor axes and source reversals do not wrap at negative endpoints");
            check(Read3(ps5, 27) == (Clamp(values[0] * 2), Clamp(values[2] * 2), Clamp(-values[1] * 2)),
                "PS5 accel uses correct 8192/g scale and explicit signed saturation");
        }
        // The standard target range is genuinely finite. This check prevents
        // a later 'fix' from wrapping 6g into the wrong direction or lying
        // about the published unit scale to hide saturation.
        byte[] highG = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, Motion(24576, 0, 0));
        check(Read(highG, 27) == short.MaxValue, "PS5 acceleration above four g saturates explicitly, never wraps");
    }

    private static int Axis(byte[] data, ViiperDeviceProfile profile, int axis) => profile.IsPs5Family
        ? (sbyte)data[axis]
        : profile.Mode == ViiperVirtualMode.Xbox ? Read(data, 6 + 2 * axis)
        : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4 + 2 * axis));
    private static int Clamp(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);
    private static int Negate(int value) => value == short.MinValue ? short.MaxValue : -value;
    private static GamepadState Motion(short x, short y, short z) => new()
    { GyroValid = true, AccelValid = true, AccelX = x, AccelY = y, AccelZ = z, GyroX = x, GyroY = y, GyroZ = z };
    private static short Read(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
    private static (int, int, int) Read3(byte[] data, int offset) => (Read(data, offset), Read(data, offset + 2), Read(data, offset + 4));
}
