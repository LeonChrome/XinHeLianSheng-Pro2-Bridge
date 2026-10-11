using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class Pro1RegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (string old in new[] { "edge", "ps5edge", "dualsenseedge", "dualsense_edge" })
            check(V60UserSettings.NormalizeModeKey(old) == "dualsense", "retired Edge safely migrates " + old);
        check(V60UserSettings.NormalizeModeKey("nspro") == "pro1" &&
            !Enum.GetNames<ViiperVirtualMode>().Any(n => n.Contains("Edge")), "Pro1 is real mode, Edge removed");
        var profile = ViiperDeviceProfile.Pro1 with { SourcePaced = true };
        check(profile.DeviceType == "nspro" && profile.ExpectedVid == "057e" && profile.ExpectedPid == "2009" &&
            profile.InputSize == 28 && profile.FeedbackSize == 10 && !profile.IsPs5Family, "Switch 1 identity and wire contract");
        check((bool)profile.DeviceSpecificArguments()["source_paced"]! &&
            VirtualPadPackets.NeutralInput(profile).Length == 28, "Pro1 shares fresh-source scheduling contract");
        var state = new GamepadState { Buttons = GamepadButtons.East | GamepadButtons.PaddleLeft | GamepadButtons.Aux,
            AccelValid = true, GyroValid = true, AccelZ = 4096, GyroX = 1638, MotionTimestampUs = 7500, Updates = 9 };
        byte[] wire = VirtualPadPackets.FromGamepad(profile, state);
        check(BinaryPrimitives.ReadUInt32LittleEndian(wire) == 2 &&
            BinaryPrimitives.ReadInt16LittleEndian(wire.AsSpan(18)) == 1638 &&
            BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(24)) == 7500 && state.Updates == 9,
            "Pro1 forwards true source units/clock, drops unsupported raw keys");
        var nintendo = RearButtonChoice.Choices(false, true);
        check(nintendo.Single(c => c.Key == GamepadButtons.East).Label == "A" &&
            nintendo.Single(c => c.Key == GamepadButtons.South).Label == "B" &&
            nintendo.Single(c => c.Key == GamepadButtons.Capture).Label == "截图", "Nintendo target labels match protocol bits");
        var config = new RearButtonConfiguration { Pro1 = new() { Left = new() { Enabled = true,
            Buttons = GamepadButtons.East | GamepadButtons.L2, Action = RearButtonAction.Repeat, DelayMs = 30 } } };
        var restored = JsonSerializer.Deserialize<RearButtonConfiguration>(JsonSerializer.Serialize(config))!.Normalize();
        check(restored.Pro1.Left.Enabled && restored.Pro1.Left.DelayMs == 30 &&
            !restored.Ps5.Left.Enabled && !restored.Xbox.Left.Enabled, "Pro1 configuration independent and persistent");
        check(!JsonSerializer.Deserialize<RearButtonConfiguration>("{}")!.Normalize().Pro1.Left.Enabled,
            "old settings migrate with Pro1 macros disabled");
        foreach (int code in Enumerable.Range(0, 256))
        {
            var decoded = Switch1RumbleConverter.Decode(new byte[] { 0, (byte)code, 0x40, (byte)code });
            check(decoded.LowAmplitude <= 29000 && decoded.HighAmplitude <= 29000 &&
                decoded.LowHz is >= 40 and <= 511 && decoded.HighHz is >= 40 and <= 511,
                "bounded legacy rumble decoding " + code);
        }
        var neutral = Switch1RumbleConverter.Decode(new byte[] { 0, 1, 0x40, 0x40 });
        check(neutral.LowHz == 160 && neutral.HighHz == 320 && neutral.LowAmplitude == 0 && neutral.HighAmplitude == 0,
            "known Nintendo neutral frequencies/amplitudes");
        byte[] f = [0, 0xC8, 0x40, 0x72, 0, 1, 0x40, 0x40, 1, 0];
        check(Pro2OutputPacketMapper.TryMapFeedback(profile, f, out var packet, out _) && packet.Active &&
            packet.Source == "switch1-hd-converted" && packet.Report.Length == 64, "Pro1 feedback reaches physical Pro2 encoding");
        var left = packet.Report.AsSpan(2, 5); var right = packet.Report.AsSpan(18, 5);
        check(((left[1] & 252) | (left[2] & 15) | (left[3] & 192) | left[4]) != 0 &&
            ((right[1] & 252) | (right[2] & 15) | (right[3] & 192) | right[4]) == 0, "stereo feedback never bleeds into opposite actuator");
        check(!Switch1RumbleConverter.TryMap(new byte[9], out _, out _) &&
            !Switch1RumbleConverter.TryMap(new byte[10], out _, out _), "short/no-rumble feedback rejected");
        f = new byte[10]; f[8] = 1;
        check(Switch1RumbleConverter.TryMap(f, out packet, out _) && !packet.Active, "all-zero feedback stops physical vibration");
    }
}
