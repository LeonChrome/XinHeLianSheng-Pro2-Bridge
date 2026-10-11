using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class CButtonDeviceRegressionTests
{
    private static long Tick(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
    private static RearButtonConfiguration Config(RearButtonBinding c, RearButtonBinding? left = null) =>
        new RearButtonConfiguration { Xbox = new() { C = c, Left = left ?? new() },
            Ps5 = new() { C = c, Left = left ?? new() }, Pro1 = new() { C = c, Left = left ?? new() } }.Normalize();
    private static GamepadState State(GamepadButtons buttons = 0) => new()
    { Buttons = buttons, Lx = 1234, GyroX = 52, Updates = 42, RawNotificationSequence = 73 };
    public static void Run(Action<bool, string> check)
    {
        foreach (var mode in new[] { ViiperVirtualMode.Xbox, ViiperVirtualMode.DualSenseLike, ViiperVirtualMode.Pro1 })
        {
            var profile = mode switch { ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox,
                ViiperVirtualMode.Pro1 => ViiperDeviceProfile.Pro1, _ => ViiperDeviceProfile.DualSenseLike };
            foreach (var choice in RearButtonChoice.Choices(mode == ViiperVirtualMode.DualSenseLike, mode == ViiperVirtualMode.Pro1).Where(c => c.Key != 0))
            {
                var config = Config(new() { Enabled = true, Buttons = choice.Key });
                var engine = new RearButtonMapper(); engine.Apply(State(), mode, config, Tick(0));
                var physical = State(GamepadButtons.Aux);
                var result = engine.Apply(physical, mode, config, Tick(7.5));
                check(result.IsPressed(choice.Key) && !result.IsPressed(GamepadButtons.Aux), "C remapped " + mode + "/" + choice.Label);
                var expected = State(choice.Key);
                if (choice.Key == GamepadButtons.L2) expected.L2 = 4095;
                if (choice.Key == GamepadButtons.R2) expected.R2 = 4095;
                check(VirtualPadPackets.FromGamepad(profile, result).AsSpan().SequenceEqual(VirtualPadPackets.FromGamepad(profile, expected)),
                    "C reaches exact virtual packet " + mode + "/" + choice.Label);
                check(physical.Buttons == GamepadButtons.Aux && result.Updates == 42 && result.RawNotificationSequence == 73 && result.Lx == 1234 && result.GyroX == 52,
                    "C preserves real input identity");
                check(engine.Apply(State(), mode, config, Tick(15)).Buttons == 0, "C hold releases " + mode);
            }
            foreach (var action in Enum.GetValues<RearButtonAction>())
            {
                var binding = new RearButtonBinding { Enabled = true, Action = action, Buttons = GamepadButtons.South | GamepadButtons.L2,
                    DelayMs = 30, PressMs = 30, GapMs = 30, RepeatCount = 2,
                    Steps = [new() { Buttons = GamepadButtons.East | GamepadButtons.R1, DelayMs = 30, PressMs = 30 }] };
                var config = Config(binding); var engine = new RearButtonMapper(); engine.Apply(State(), mode, config, Tick(0));
                GamepadState Frame(double ms, bool down = true) => engine.Apply(State(down ? GamepadButtons.Aux : 0), mode, config, Tick(ms));
                check(Frame(7.5).Buttons == 0 && Frame(30).Buttons == 0, "C delay " + mode + "/" + action);
                check(Frame(37.5).IsPressed(GamepadButtons.South) && Frame(60).L2 == 4095, "C delayed chord " + mode + "/" + action);
                if (action == RearButtonAction.Hold)
                    check(Frame(67.5).IsPressed(GamepadButtons.South) && Frame(75, false).Buttons == 0, "C hold end");
                else
                {
                    check(Frame(67.5).Buttons == 0, "C timed release");
                    var second = Frame(97.5);
                    check(action switch { RearButtonAction.Repeat => second.IsPressed(GamepadButtons.South),
                        RearButtonAction.Sequence => second.IsPressed(GamepadButtons.East) && second.IsPressed(GamepadButtons.R1),
                        _ => second.Buttons == 0 }, "C second pulse or step " + action);
                    check(Frame(127.5).Buttons == 0 && Frame(157.5).Buttons == 0, "C finite completion no held restart");
                }
                engine.Reset(); engine.Apply(State(), mode, config, Tick(0)); Frame(7.5);
                check(engine.HasPendingAction, "C pending delay tracked"); engine.TryCancelOutput(out _);
                check(!engine.HasPendingAction && Frame(15).Buttons == 0 && Frame(75).Buttons == 0, "C cancel before delay and suppress held source");
                Frame(82.5, false); Frame(90);
                check(Frame(120).IsPressed(GamepadButtons.South), "C rearms after release");
            }
            var shared = Config(new() { Enabled = true, Buttons = GamepadButtons.South }, new() { Enabled = true, Buttons = GamepadButtons.South });
            var e = new RearButtonMapper(); e.Apply(State(), mode, shared, Tick(0));
            e.Apply(State(GamepadButtons.Aux | GamepadButtons.PaddleLeft), mode, shared, Tick(7.5));
            check(e.Apply(State(GamepadButtons.Aux), mode, shared, Tick(15)).IsPressed(GamepadButtons.South), "C survives GL release shared target");
            check(e.Apply(State(GamepadButtons.South), mode, shared, Tick(22.5)).IsPressed(GamepadButtons.South), "C release preserves physical target");
            check(e.Apply(State(), mode, shared, Tick(30)).Buttons == 0, "all contributors released");
            check(!RearButtonMapper.ProfilesEquivalent(shared.ForMode(mode), Config(shared.ForMode(mode)!.C with { DelayMs = 5 }, shared.ForMode(mode)!.Left).ForMode(mode)),
                "C-only edits detected by profile comparison");
        }
        var native = State(GamepadButtons.Aux | GamepadButtons.PaddleLeft);
        check(ReferenceEquals(new RearButtonMapper().Apply(native, ViiperVirtualMode.Pro2, Config(new() { Enabled = true, Buttons = GamepadButtons.South }), Tick(0)), native),
            "native Steam Pro2 C never hijacked");
        var old = JsonSerializer.Deserialize<RearButtonConfiguration>("{\"Xbox\":{\"Left\":{\"Enabled\":true,\"Buttons\":1}}}")!.Normalize();
        check(old.Xbox.Left.Enabled && !old.Xbox.C.Enabled && !old.Pro1.C.Enabled, "old rear settings migrate C disabled");
        var saved = Config(new() { Enabled = true, Action = RearButtonAction.Repeat, Buttons = GamepadButtons.East, DelayMs = 12 });
        var loaded = JsonSerializer.Deserialize<RearButtonConfiguration>(JsonSerializer.Serialize(saved))!.Normalize();
        check(RearButtonMapper.ProfilesEquivalent(saved.Pro1, loaded.Pro1), "C settings roundtrip");
        check((new RearButtonBinding { Buttons = GamepadButtons.Aux | GamepadButtons.PaddleLeft | GamepadButtons.South }).Normalize().Buttons == GamepadButtons.South,
            "no recursive C/rear mapping targets");

        var raw = new byte[63]; raw[11] = raw[14] = 0x08; raw[12] = raw[15] = 0x80;
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(52), 4096);
        var parser = new Pro2HidReportParser(true);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 0x10004000);
        check(parser.TryParseFd2Payload(raw, out var plugged, out _) && plugged.IsPressed(GamepadButtons.Aux) && plugged.HeadsetPresenceKnown && plugged.HeadsetPresent,
            "real FD2 C + headset bits independently decoded");
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 0);
        check(parser.TryParseFd2Payload(raw, out var unplugged, out _) && !unplugged.HeadsetPresent && !unplugged.IsPressed(GamepadButtons.Aux), "FD2 unplug and C release");
        check(Pro2DevicePresentation.Headset(plugged, true, true) == "已插入" && Pro2DevicePresentation.Headset(unplugged, true, true) == "未插入" &&
            Pro2DevicePresentation.Headset(plugged, false, true) == "未知" && Pro2DevicePresentation.Headset(plugged, true, false) == "未连接", "headset status never stale or audio claim");
        foreach (var hex in new[] { "1001010110780000010105020C000000FFFFFFFF", "1001010110780000020000020C00000000020001", "100101011078000001000E020C000000" })
        {
            var bytes = Convert.FromHexString(hex);
            check(Pro2FirmwareInfo.TryDecode(bytes, out var info) && info!.ControllerVersion == $"{bytes[8]}.{bytes[9]}.{bytes[10]}" && info.ControllerType == 2,
                "actual firmware layouts old/new no hardcode " + hex);
            check(Pro2DevicePresentation.Firmware(info, true) == info!.ControllerVersion && Pro2DevicePresentation.Firmware(info, false) == "未连接", "firmware display connection scoped");
        }
        var fixture = Convert.FromHexString("1001010110780000010105020C000000FFFFFFFF");
        for (int len = 0; len < 16; len++) check(!Pro2FirmwareInfo.TryDecode(fixture.AsSpan(0, len), out _), "truncated firmware safe " + len);
        fixture[5] = 0xF8; check(!Pro2FirmwareInfo.TryDecode(fixture, out _), "firmware NACK not a version");
        check(!Pro2FirmwareInfo.TryDecode(Convert.FromHexString("1001010110780000FFFFFF020C000000"), out _), "placeholder firmware unknown");
    }
}
