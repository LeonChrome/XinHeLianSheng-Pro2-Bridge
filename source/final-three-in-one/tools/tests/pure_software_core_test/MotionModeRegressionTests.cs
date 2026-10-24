using System;
using System.Buffers.Binary;
using Y700Switch2V60Viiper;

// Receiver-side expectations are independent of the sender's rotation helper:
// SDL Switch 1 normalizes raw axes as (-Y,+Z,-X); DualSense exposes XYZ in
// 8192 accel counts/g and 16.384 gyro counts/dps. No radio/device is opened.
internal static class MotionModeRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        DataTransferAccuracyRegressionTests.Run(check);
        foreach (var axes in new[] { (1638, 0, 0), (0, 1638, 0), (0, 0, 1638),
            (111, 222, 333), (-111, -222, -333), (4096, -2048, -3396) })
        {
            for (int mask = 0; mask < 8; ++mask)
            {
                var inversion = new GyroAxisInversion((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
                var state = Motion(axes.Item1, axes.Item2, axes.Item3);
                byte[] pro1 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro1, state, inversion);
                byte[] pro2 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro2, state, inversion);
                byte[] ps5 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, state, inversion);
                var physical = (inversion.InvertX ? -axes.Item1 : axes.Item1,
                    inversion.InvertY ? -axes.Item2 : axes.Item2, inversion.InvertZ ? -axes.Item3 : axes.Item3);
                var expected = (physical.Item1, physical.Item3, -physical.Item2);
                check(Switch1Normalized(pro1, 18) == expected,
                    $"Pro1 source-axis inversion precedes protocol rotation axes={axes} mask={mask}");
                check(Read3(ps5, 21) == expected,
                    $"PS5 source-axis inversion matches Pro1 physical axes axes={axes} mask={mask}");
                check(Read3(pro2, 18) == physical,
                    $"Pro2 native raw coordinates unchanged axes={axes} mask={mask}");
                check(Switch1Normalized(pro1, 12) == (axes.Item1, axes.Item3, -axes.Item2),
                    "Pro1 gyro direction never inverts gravity");
                check(Read3(ps5, 27) == (axes.Item1 * 2, axes.Item3 * 2, -axes.Item2 * 2),
                    "PS5 gravity keeps physical axes and 8192/g scale");
                check(Read3(pro2, 12) == axes, "Pro2 gravity wire remains unrotated");
            }
        }

        // Exercise signs, rounding and scale across the normal calibrated range.
        for (int value = -8192; value <= 8192; value += 32)
        {
            var state = Motion(value, value / 2, -value / 3);
            byte[] pro1 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro1, state);
            byte[] ps5 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, state);
            var pro1Axes = Switch1Normalized(pro1, 18);
            var ps5Axes = Read3(ps5, 21);
            check(pro1Axes == ps5Axes, "Pro1/PS5 physical gain agreement " + value);
            check(pro1Axes.Item1 == value && pro1Axes.Item2 == -value / 3 && pro1Axes.Item3 == -value / 2,
                "Pro1 no accidental second gyro scaling " + value);
        }

        var tuningState = Motion(100, 200, 300);
        byte[] tuned = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, tuningState,
            new GyroAxisInversion(false, true, false), ps5OutputImuTuning: new Ps5OutputImuTuning(2, .5, 1.5));
        check(Read3(tuned, 21) == (200, 150, 300),
            "PS5 per-target pitch/yaw/roll scales apply after physical inversion and rotation");
        check(Read3(tuned, 27) == (200, 600, -400), "PS5 gyro tuning does not scale acceleration");

        foreach (short edge in new[] { short.MinValue, short.MaxValue })
        {
            var state = Motion(edge, edge, edge);
            byte[] p1 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro1, state);
            check(Read(p1, 18) == edge && Read(p1, 20) == (edge == short.MinValue ? short.MaxValue : -edge),
                "Pro1 signed-axis rotation saturates instead of wrapping " + edge);
            byte[] ps5 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, state,
                ps5OutputImuTuning: new Ps5OutputImuTuning(2, 2, 2));
            check(Read(ps5, 21) == edge && Read(ps5, 23) == edge &&
                Read(ps5, 25) == (edge == short.MinValue ? short.MaxValue : short.MinValue),
                "PS5 gyro gain clamps signed endpoints " + edge);
        }

        foreach (uint stamp in new uint[] { 0, 7500, 15000, 7500, uint.MaxValue - 3, 2 })
        {
            var state = Motion(111, 222, 333);
            state.MotionTimestampUs = stamp;
            state.Updates = 71;
            foreach (var profile in new[] { ViiperDeviceProfile.Pro1, ViiperDeviceProfile.Pro2 })
            {
                byte[] first = VirtualPadPackets.FromGamepad(profile, state);
                byte[] repeated = VirtualPadPackets.FromGamepad(profile, state);
                check(BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(24)) == stamp &&
                    first.AsSpan().SequenceEqual(repeated), "mapping preserves duplicate/wrapped source clock " + profile.Mode);
                check(state.Updates == 71 && state.MotionTimestampUs == stamp,
                    "mapping cannot manufacture physical samples or advance source counters " + profile.Mode);
            }
        }

        var invalid = Motion(123, 456, 789);
        invalid.GyroValid = invalid.AccelValid = false;
        byte[] invalidPro1 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro1, invalid);
        check(invalidPro1.AsSpan(12, 12).IndexOfAnyExcept((byte)0) < 0,
            "Pro1 invalid sensors do not leak stale nonzero motion");
        byte[] invalidPs5 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, invalid);
        check(Read3(invalidPs5, 21) == (0, 0, 0) && Read3(invalidPs5, 27) == (0, 8192, 0),
            "PS5 invalid motion falls back to neutral gravity, not stale samples");
        byte[] xbox = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Xbox, Motion(111, 222, 333));
        check(xbox.Length == 20 && xbox.AsSpan(14).IndexOfAnyExcept((byte)0) < 0,
            "Xbox 360 identity does not fabricate a native motion extension");
    }

    private static GamepadState Motion(int x, int y, int z) => new()
    {
        GyroValid = true, AccelValid = true,
        GyroX = (short)x, GyroY = (short)y, GyroZ = (short)z,
        AccelX = (short)x, AccelY = (short)y, AccelZ = (short)z
    };
    private static (int, int, int) Switch1Normalized(byte[] report, int offset) =>
        (-Read(report, offset + 2), Read(report, offset + 4), -Read(report, offset));
    private static (int, int, int) Read3(byte[] report, int offset) =>
        (Read(report, offset), Read(report, offset + 2), Read(report, offset + 4));
    private static short Read(byte[] report, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(offset, 2));
}
