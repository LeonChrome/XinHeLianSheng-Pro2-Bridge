using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class RearButtonRegressionTests
{
    private static Action<bool, string> check = null!;
    private static long Tick(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
    private static GamepadState State(GamepadButtons buttons = GamepadButtons.None) => new()
    {
        Buttons = buttons, Lx = 1790, Ly = 3100, Rx = 890, Ry = 2450,
        GyroValid = true, AccelValid = true, GyroX = 11, AccelZ = 4096,
        Updates = 123, RawNotificationSequence = 456, SourceTimestampTicks = 789, MotionTimestampUs = 1000
    };
    private static RearButtonConfiguration Config(RearButtonBinding left, RearButtonBinding? right = null) =>
        new RearButtonConfiguration { Xbox = new() { Left = left, Right = right ?? new() },
            Ps5 = new() { Left = left, Right = right ?? new() },
            Pro1 = new() { Left = left, Right = right ?? new() } }.Normalize();

    public static void Run(Action<bool, string> assertion, string output)
    {
        check = assertion;
        Mapping(); Timed(); Settings(); Calibrations();
        File.WriteAllText(Path.Combine(output, "rear-and-calibration-tests.txt"),
            "Offline: actual FD2 parser -> rear mapper -> Xbox/PS5 wire builders.\r\n" +
            "Timing state machines and shared calibration algorithms verified; no physical Win11/USBIP session claimed.\r\n");
    }

    private static void Mapping()
    {
        foreach (var mode in new[] { ViiperVirtualMode.Xbox, ViiperVirtualMode.DualSenseLike, ViiperVirtualMode.Pro1 })
        foreach (var choice in RearButtonChoice.Choices(mode == ViiperVirtualMode.DualSenseLike, mode == ViiperVirtualMode.Pro1).Where(c => c.Key != GamepadButtons.None))
        {
            var config = Config(new() { Enabled = true, Buttons = choice.Key });
            var mapper = new RearButtonMapper();
            mapper.Apply(State(), mode, config, Tick(0));
            var physical = State(GamepadButtons.PaddleLeft);
            var mapped = mapper.Apply(physical, mode, config, Tick(7.5));
            check(mapped.IsPressed(choice.Key) && !mapped.IsPressed(GamepadButtons.PaddleLeft), "rear single mapping " + mode + "/" + choice.Label);
            check(physical.Buttons == GamepadButtons.PaddleLeft && mapped.Updates == physical.Updates &&
                mapped.RawNotificationSequence == 456 && mapped.SourceTimestampTicks == 789 &&
                mapped.MotionTimestampUs == 1000 && mapped.Lx == 1790 && mapped.GyroX == 11,
                "mapping preserves physical input/motion/rate identity");
            byte[] wire = VirtualPadPackets.FromGamepad(mode switch
            { ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox, ViiperVirtualMode.Pro1 => ViiperDeviceProfile.Pro1,
                _ => ViiperDeviceProfile.DualSenseLike }, mapped);
            check(wire.Length > 10, "mapped key reaches real report builder");
            var released = mapper.Apply(State(), mode, config, Tick(15));
            check(released.Buttons == GamepadButtons.None, "hold mapping releases on rear release");
        }
        var combo = Config(new() { Enabled = true, Buttons = GamepadButtons.South | GamepadButtons.L2 },
            new() { Enabled = true, Buttons = GamepadButtons.South | GamepadButtons.R1 });
        var engine = new RearButtonMapper();
        engine.Apply(State(), ViiperVirtualMode.Xbox, combo, Tick(0));
        var both = engine.Apply(State(GamepadButtons.PaddleLeft | GamepadButtons.PaddleRight | GamepadButtons.North),
            ViiperVirtualMode.Xbox, combo, Tick(7.5));
        check(both.IsPressed(GamepadButtons.South | GamepadButtons.L2) && both.IsPressed(GamepadButtons.North) &&
            both.IsPressed(GamepadButtons.R1) && both.L2 == 4095, "simultaneous rear chords OR physical keys and triggers");
        var partial = engine.Apply(State(GamepadButtons.PaddleRight | GamepadButtons.South),
            ViiperVirtualMode.Xbox, combo, Tick(15));
        check(partial.IsPressed(GamepadButtons.South) && partial.IsPressed(GamepadButtons.R1) &&
            !partial.IsPressed(GamepadButtons.L2), "one rear release preserves overlapping other rear/physical buttons");
        byte[] xbox = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Xbox, both);
        check((BinaryPrimitives.ReadUInt32LittleEndian(xbox) & 0x9000) == 0x9000 && xbox[4] == 255,
            "Xbox A/Y and LT exact wire bits");
        byte[] ps5 = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, both);
        check((BinaryPrimitives.ReadUInt32LittleEndian(ps5.AsSpan(4)) & 0x4A0) == 0x4A0 && ps5[9] == 255,
            "PS5 cross/triangle/L2 exact wire bits");
        check(ReferenceEquals(engine.Apply(both, ViiperVirtualMode.Pro2, combo, Tick(22.5)), both),
            "native Pro2 mode bypasses all mappings");
        var defaultConfig = new RearButtonConfiguration();
        var unmapped = State(GamepadButtons.PaddleLeft | GamepadButtons.PaddleRight);
        check(ReferenceEquals(engine.Apply(unmapped, ViiperVirtualMode.Pro1, defaultConfig, Tick(30)), unmapped),
            "unconfigured Pro1 does not invent a mapping");
        var pro1Wire = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro1, unmapped);
        check((BinaryPrimitives.ReadUInt32LittleEndian(pro1Wire) & ~0x3FFFFu) == 0,
            "Pro1 never advertises unsupported native paddles");
        var fd2 = Payload(2048, 2048, 2048, 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(fd2.AsSpan(4), 0x03000000);
        var parser = new Pro2HidReportParser(true);
        check(parser.TryParseFd2Payload(fd2, out var parsed, out _) &&
            parsed.IsPressed(GamepadButtons.PaddleLeft) && parsed.IsPressed(GamepadButtons.PaddleRight),
            "actual FD2 bytes expose independent GL/GR inputs");
        var trigger = Config(new() { Enabled = true, Buttons = GamepadButtons.L2 });
        engine.Reset(); engine.Apply(State(), ViiperVirtualMode.Xbox, trigger, Tick(0));
        var analog = State(GamepadButtons.L2 | GamepadButtons.PaddleLeft); analog.L2 = 12;
        check(engine.Apply(analog, ViiperVirtualMode.Xbox, trigger, Tick(7.5)).L2 == 4095 && analog.L2 == 12,
            "mapped trigger raises output only, preserves physical analog value");
        var professional = State(); professional.Rx = 3333;
        var merged = RearButtonMapper.MergeProfessionalControls(both, professional)!;
        check(merged.Buttons == both.Buttons && merged.L2 == 4095 && merged.Rx == 3333 &&
            professional.Buttons == GamepadButtons.None, "professional output preserves calibrated axes while merging mapped controls");
    }

    private static void Timed()
    {
        foreach (var action in Enum.GetValues<RearButtonAction>())
        {
            var config = Config(new() { Enabled = true, Action = action, Buttons = GamepadButtons.South,
                DelayMs = 30, PressMs = 30, GapMs = 30, RepeatCount = 2,
                Steps = [new() { Buttons = GamepadButtons.East | GamepadButtons.R1, DelayMs = 30, PressMs = 30 }] });
            var engine = new RearButtonMapper();
            engine.Apply(State(), ViiperVirtualMode.Xbox, config, Tick(0));
            GamepadState Frame(double time, bool down = true) =>
                engine.Apply(State(down ? GamepadButtons.PaddleLeft : GamepadButtons.None), ViiperVirtualMode.Xbox, config, Tick(time));
            check(!Frame(7.5).IsPressed(GamepadButtons.South) && !Frame(30).IsPressed(GamepadButtons.South),
                "initial delay enforced " + action);
            check(Frame(37.5).IsPressed(GamepadButtons.South) && Frame(60).IsPressed(GamepadButtons.South),
                "scheduled first press " + action);
            if (action == RearButtonAction.Hold)
            {
                check(Frame(67.5).IsPressed(GamepadButtons.South) && !Frame(75, false).IsPressed(GamepadButtons.South),
                    "hold persists until release");
            }
            else
            {
                check(!Frame(67.5).IsPressed(GamepadButtons.South), "timed key released by duration " + action);
                var second = Frame(97.5);
                if (action == RearButtonAction.Repeat)
                    check(second.IsPressed(GamepadButtons.South) && !Frame(127.5).IsPressed(GamepadButtons.South) &&
                        !Frame(157.5).IsPressed(GamepadButtons.South), "finite repeat count and release gap");
                else if (action == RearButtonAction.Sequence)
                    check(second.IsPressed(GamepadButtons.East) && second.IsPressed(GamepadButtons.R1) &&
                        Frame(127.5).Buttons == GamepadButtons.None, "sequential delayed combination and release");
                else check(second.Buttons == GamepadButtons.None, "tap does not refire while held");
            }
        }
        var repeat = Config(new() { Enabled = true, Action = RearButtonAction.Repeat, Buttons = GamepadButtons.South, PressMs = 30, GapMs = 30 });
        var e = new RearButtonMapper(); e.Apply(State(), ViiperVirtualMode.Xbox, repeat, Tick(0));
        check(e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, repeat, Tick(7.5)).IsPressed(GamepadButtons.South),
            "unlimited repeat begins");
        check(e.Apply(State(), ViiperVirtualMode.Xbox, repeat, Tick(15)).Buttons == GamepadButtons.None,
            "repeat cancels immediately on physical release");
        e.Reset();
        check(e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, repeat, Tick(22.5)).Buttons == GamepadButtons.None,
            "disconnect/session reset suppresses held-key phantom trigger");
        e.Apply(State(), ViiperVirtualMode.Xbox, repeat, Tick(30));
        e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, repeat, Tick(37.5));
        check(e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, repeat.Normalize(), Tick(45)).IsPressed(GamepadButtons.South),
            "identical runtime profile replacement does not interrupt a held mapping");
        var changedRepeat = repeat with { Xbox = repeat.Xbox with { Left = repeat.Xbox.Left with { GapMs = 60 } } };
        check(e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, changedRepeat, Tick(47.5)).Buttons == GamepadButtons.None,
            "actual runtime profile change cancels and requires new physical edge");
        e.Apply(State(), ViiperVirtualMode.Xbox, repeat, Tick(50));
        e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, repeat, Tick(57.5));
        check(e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, repeat, Tick(1000)).Buttons == GamepadButtons.None,
            "long input gap cancels rather than replaying missed pulses");
        var delayedTap = Config(new() { Enabled = true, Action = RearButtonAction.Tap,
            Buttons = GamepadButtons.East, DelayMs = 60, PressMs = 45 });
        e.Reset(); e.Apply(State(), ViiperVirtualMode.Xbox, delayedTap, Tick(0));
        e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, delayedTap, Tick(7.5));
        check(e.Apply(State(), ViiperVirtualMode.Xbox, delayedTap, Tick(15)).Buttons == GamepadButtons.None &&
            e.Apply(State(), ViiperVirtualMode.Xbox, delayedTap, Tick(67.5)).IsPressed(GamepadButtons.East) &&
            e.Apply(State(), ViiperVirtualMode.Xbox, delayedTap, Tick(112.5)).Buttons == GamepadButtons.None,
            "quick rear tap schedules one delayed press and duration without needing key held");
        e.Reset(); e.Apply(State(), ViiperVirtualMode.Xbox, repeat, Tick(0));
        e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, repeat, Tick(7.5));
        check(e.Apply(State(GamepadButtons.PaddleLeft), ViiperVirtualMode.DualSenseLike, repeat, Tick(15)).Buttons == GamepadButtons.None,
            "changing output mode while held cannot replay a macro");
        // 133.33 Hz fresh-frame drive for ten seconds. No background clock/scheduler.
        e.Reset(); var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1334; i++)
        {
            var p = State(i == 0 ? GamepadButtons.None : GamepadButtons.PaddleLeft); p.Updates = (uint)i;
            var mapped = e.Apply(p, ViiperVirtualMode.Xbox, repeat, Tick(i * 7.5));
            check(mapped.Updates == i && p.Buttons == (i == 0 ? GamepadButtons.None : GamepadButtons.PaddleLeft),
                "rear macros do not create or mutate source frames " + i);
        }
        watch.Stop(); Console.WriteLine("REAR: ten seconds/1334 fresh frames in " + watch.Elapsed.TotalMilliseconds.ToString("F2") + "ms offline");
    }

    private static void Settings()
    {
        var editor = new RearButtonEditor("GL", false, new());
        editor.Enabled = true; check(!editor.TryBuild(out _, out _), "empty enabled assignment rejected");
        editor.Buttons.First(c => c.Key == GamepadButtons.South).Selected = true;
        editor.Delay = "-1"; check(!editor.TryBuild(out _, out _), "negative delay rejected");
        editor.Delay = "0"; editor.Action = "单次按键"; editor.Duration = "bad"; check(!editor.TryBuild(out _, out _), "invalid visible numeric text rejected");
        editor.Duration = "80"; editor.Action = "顺序按键"; editor.AddStepCommand.Execute(null);
        check(editor.TryBuild(out var binding, out _) && binding.Steps.Length == 1, "sequence editor produces runtime profile");
        var config = Config(binding);
        var settings = JsonSerializer.Deserialize<V60UserSettings>(JsonSerializer.Serialize(new V60UserSettings { RearButtons = config }))!;
        check(settings.RearButtons.Xbox.Left.Steps[0].DelayMs == 100, "mapping profiles persist in shared settings JSON");
        check(JsonSerializer.Deserialize<V60UserSettings>("{}")!.RearButtons.Xbox.Left.Enabled == false, "old settings migrate with mappings disabled");
        check(new RearButtonConfiguration { Xbox = null!, Ps5 = new() { Left = null! } }.Normalize().Ps5.Left.Enabled == false,
            "malformed nullable settings safely normalized");
        var bad = new RearButtonBinding { DelayMs = int.MinValue, PressMs = int.MaxValue, GapMs = -9,
            RepeatCount = 5000, Buttons = GamepadButtons.PaddleLeft | GamepadButtons.South,
            Action = (RearButtonAction)99, Steps = null! }.Normalize();
        check(bad.DelayMs == 0 && bad.PressMs == 30000 && bad.GapMs == 30 && bad.RepeatCount == 1000 &&
            bad.Buttons == GamepadButtons.South && bad.Action == RearButtonAction.Hold, "persisted unsafe values clamped");
        for (int i = 0; i < 30; i++) editor.AddStepCommand.Execute(null);
        check(editor.Steps.Count == 16, "bounded sequence editor");
        editor.Steps[0].RemoveCommand.Execute(null); editor.AddStepCommand.Execute(null);
        check(editor.Steps.Count == 16, "sequence can add again after removal");
    }

    private static byte[] Payload(ushort lx, ushort ly, ushort rx, ushort ry)
    {
        var b = new byte[63];
        void Axis(int offset, ushort x, ushort y)
        { b[offset] = (byte)x; b[offset + 1] = (byte)((x >> 8) | ((y & 15) << 4)); b[offset + 2] = (byte)(y >> 4); }
        Axis(10, lx, ly); Axis(13, rx, ry);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(52), 4096);
        return b;
    }
    private static void Calibrations()
    {
        // Both OS routes feed this exact same parser/calibration code. OS names
        // below label policy cases only; they do not impersonate a Win11 machine.
        foreach (int build in new[] { 19045, 22631, 26100, 26200 })
        {
            var parser = new Pro2HidReportParser(true);
            check(parser.StartManualStickRangeCalibration().Contains("rejected"), "range requires center first " + build);
            parser.StartManualStickCenterCalibration();
            for (int i = 0; i < 270; i++)
                check(parser.TryParseFd2Payload(Payload(2060, 2020, 2070, 2030), out _, out _), "center FD2 fixture " + build + "/" + i);
            var center = parser.CompleteManualStickCenterCalibration();
            check(center.Success && center.Profile!.CenterLx == 2060 && center.Profile.CenterRy == 2030,
                "shared center calibration completes " + build);
            parser.TryParseFd2Payload(Payload(2060, 2020, 2070, 2030), out var neutral, out _);
            check(neutral.Lx == 2048 && neutral.Ly == 2048 && neutral.Rx == 2048 && neutral.Ry == 2048,
                "calibrated center reaches parsed controls " + build);
            parser.StartManualStickRangeCalibration();
            for (int i = 0; i < 1070; i++)
            {
                ushort endpoint = (ushort)(i % 2 == 0 ? 448 : 3648);
                parser.TryParseFd2Payload(Payload(endpoint, endpoint, endpoint, endpoint), out _, out _);
            }
            var range = parser.CompleteManualStickRangeCalibration();
            check(range.Success && range.Profile!.RangeCalibrated, "shared eight-second range fixture " + build);
            var saved = parser.StickCalibrationProfile;
            parser.StartManualStickCenterCalibration();
            for (int i = 0; i < 25; i++) parser.TryParseFd2Payload(Payload((ushort)(1900 + i * 10), 2048, 2048, 2048), out _, out _);
            check(!parser.CompleteManualStickCenterCalibration().Success && parser.StickCalibrationProfile == saved,
                "moving center rejects without replacing old calibration " + build);
            parser.StartManualStickRangeCalibration(); parser.TryParseFd2Payload(Payload(2048, 2048, 2048, 2048), out _, out _);
            check(!parser.CompleteManualStickRangeCalibration().Success && parser.StickCalibrationProfile == saved,
                "incomplete range preserves previous configuration " + build);
            var gyro = new Pro2GyroBiasCalibration();
            gyro.StartManualCalibration();
            for (int i = 0; i < 402; i++)
                gyro.Apply(new() { GyroValid = true, AccelValid = true, GyroX = 4, GyroY = -3, GyroZ = 2, AccelZ = 4096 }, Tick(i * 7.5));
            check(gyro.Calibrated && gyro.LastResult == "manual_committed_3s" && gyro.BiasY == -3,
                "shared three-second gyro fixture " + build);
            var moved = new GamepadState { GyroValid = true, AccelValid = true, GyroX = 1500, AccelZ = 4096 };
            gyro.StartManualCalibration(); gyro.Apply(moved, Tick(3100));
            check(gyro.LastResult == "manual_rejected_motion" && gyro.BiasX == 4,
                "gyro movement rejects without losing old bias " + build);
        }
    }
}
