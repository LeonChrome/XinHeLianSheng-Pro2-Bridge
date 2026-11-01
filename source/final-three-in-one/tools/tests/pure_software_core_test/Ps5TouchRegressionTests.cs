using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class Ps5TouchRegressionTests
{
    private static Action<bool, string> check = null!;
    private static long T(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
    private static GamepadState Physical(GamepadButtons buttons = 0) => new()
    { Buttons = buttons, Updates = 123, RawNotificationSequence = 456, MotionTimestampUs = 789, Lx = 1234, GyroValid = true, GyroX = 42 };
    private static RearButtonConfiguration Config(RearButtonBinding binding, int channel = 0) => new()
    { Ps5 = new() { Left = channel == 0 ? binding : new(), Right = channel == 1 ? binding : new(), C = channel == 2 ? binding : new() } };
    private static GamepadButtons Key(int channel) => channel == 0 ? GamepadButtons.PaddleLeft : channel == 1 ? GamepadButtons.PaddleRight : GamepadButtons.Aux;
    public static void Run(Action<bool, string> assertion, string output)
    {
        check = assertion;
        Geometry(); PersistenceAndEditor(); Lifetimes(); Safety();
        Directory.CreateDirectory(output);
        var wire = new List<byte>();
        foreach (var gesture in Enum.GetValues<Ps5TouchGesture>().Where(g => g != Ps5TouchGesture.None))
        {
            var mapper = new RearButtonMapper(); var config = Config(new() { Enabled = true, TouchGesture = gesture, PressMs = 400 });
            mapper.Apply(Physical(), ViiperVirtualMode.DualSenseLike, config, T(0));
            for (int i = 1; i <= 70; i++)
                wire.AddRange(VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike,
                    mapper.Apply(Physical(GamepadButtons.PaddleLeft), ViiperVirtualMode.DualSenseLike, config, T(i * 7.5))));
        }
        File.WriteAllBytes(Path.Combine(output, "ps5-touch-wire.bin"), wire.ToArray());
        File.WriteAllText(Path.Combine(output, "ps5-touch-tests.txt"), "Offline: mapper -> actual 33-byte PS5 API packets. No controller/radio/driver changes. Seven gesture streams, 70 fresh frames each.\n");
    }
    private static void Geometry()
    {
        foreach (var gesture in Enum.GetValues<Ps5TouchGesture>())
        for (int i = 0; i <= 100; i++)
        {
            var p = Ps5TouchGestures.PointAt(gesture, i / 100.0);
            check(p.X <= 1919 && p.Y <= 1079 && p.Active == (gesture != Ps5TouchGesture.None), "touch bounds/active " + gesture);
        }
        var up0 = Ps5TouchGestures.PointAt(Ps5TouchGesture.SwipeUp, 0); var up1 = Ps5TouchGestures.PointAt(Ps5TouchGesture.SwipeUp, 1);
        check(up0.X == up1.X && up0.Y > up1.Y, "up means decreasing screen Y");
        check(Ps5TouchGestures.PointAt(Ps5TouchGesture.SwipeDown, 0) == up1 && Ps5TouchGestures.PointAt(Ps5TouchGesture.SwipeDown, 1) == up0, "down reverses up");
        check(Ps5TouchGestures.PointAt(Ps5TouchGesture.SwipeLeft, 0).X > Ps5TouchGestures.PointAt(Ps5TouchGesture.SwipeLeft, 1).X, "left decreasing X");
        check(Ps5TouchGestures.PointAt(Ps5TouchGesture.SwipeRight, 0).X < Ps5TouchGestures.PointAt(Ps5TouchGesture.SwipeRight, 1).X, "right increasing X");
        check(Ps5TouchGestures.PointAt(Ps5TouchGesture.CircleClockwise, .25).X > 960 &&
            Ps5TouchGestures.PointAt(Ps5TouchGesture.CircleCounterClockwise, .25).X < 960, "circle direction in screen coordinates");
        check(Ps5TouchGestures.PointAt(Ps5TouchGesture.CircleClockwise, 0) == Ps5TouchGestures.PointAt(Ps5TouchGesture.CircleClockwise, 1), "circle closes");
    }
    private static void PersistenceAndEditor()
    {
        check(JsonSerializer.Deserialize<RearButtonConfiguration>("{}")!.Normalize().Ps5.C.TouchGesture == Ps5TouchGesture.None, "old settings have no synthetic touch");
        var binding = new RearButtonBinding { Enabled = true, TouchGesture = Ps5TouchGesture.CircleClockwise,
            Action = RearButtonAction.Sequence, PressMs = 400, Steps = [new() { TouchGesture = Ps5TouchGesture.SwipeLeft, PressMs = 500 }] };
        var config = JsonSerializer.Deserialize<RearButtonConfiguration>(JsonSerializer.Serialize(Config(binding)))!.Normalize();
        check(config.Ps5.Left.TouchGesture == Ps5TouchGesture.CircleClockwise && config.Ps5.Left.Steps[0].TouchGesture == Ps5TouchGesture.SwipeLeft, "gesture profile and sequence JSON roundtrip");
        check(new RearButtonBinding { TouchGesture = (Ps5TouchGesture)999 }.Normalize().TouchGesture == Ps5TouchGesture.None, "invalid gesture neutralized");
        var editor = new RearButtonEditor("C", true, new() { Enabled = true });
        editor.TouchAction = Ps5TouchGesture.SwipeUp;
        check(editor.IsDurationVisible && editor.Duration == "400" && editor.TryBuild(out var built, out _) && built.Buttons == 0 && built.TouchGesture == Ps5TouchGesture.SwipeUp, "gesture-only editor and sensible duration");
        editor.Duration = "30"; check(!editor.TryBuild(out _, out _), "undersampled gesture rejected by editor");
        editor.Duration = "400"; editor.Action = "顺序按键"; editor.AddStepCommand.Execute(null);
        var step = editor.Steps[0]; step.First = step.Second = GamepadButtons.None; step.TouchAction = Ps5TouchGesture.CircleCounterClockwise;
        check(editor.TryBuild(out var seq, out _) && seq.Steps[0].Buttons == 0 && seq.Steps[0].TouchGesture == Ps5TouchGesture.CircleCounterClockwise, "gesture-only sequence step accepted");
        var loaded = new RearButtonEditor("C", true, seq);
        check(loaded.Steps[0].First == GamepadButtons.None && loaded.TryBuild(out var again, out _) && again.Steps[0] == seq.Steps[0], "opening a gesture-only sequence does not silently add cross");
        var xbox = new RearButtonEditor("C", false, binding); xbox.TouchAction = Ps5TouchGesture.SwipeLeft;
        check(xbox.TouchAction == Ps5TouchGesture.None, "non-PS5 editor cannot enable gesture");
    }
    private static void Lifetimes()
    {
        foreach (int channel in new[] { 0, 1, 2 })
        foreach (double cadence in new[] { 7.5, 15.0, 1000.0 / 60 })
        foreach (var gesture in Enum.GetValues<Ps5TouchGesture>().Where(g => g != Ps5TouchGesture.None))
        foreach (var action in new[] { RearButtonAction.Hold, RearButtonAction.Tap, RearButtonAction.Repeat })
        {
            var mapper = new RearButtonMapper();
            var config = Config(new() { Enabled = true, Action = action, TouchGesture = gesture, PressMs = 400, GapMs = 80, RepeatCount = 2 }, channel);
            mapper.Apply(Physical(), ViiperVirtualMode.DualSenseLike, config, T(0));
            bool wasActive = false; int starts = 0, lifts = 0; Ps5TouchPoint last = default;
            for (int i = 1; i <= (int)(2000 / cadence); i++)
            {
                var physical = Physical(Key(channel) | GamepadButtons.West);
                var mapped = mapper.Apply(physical, ViiperVirtualMode.DualSenseLike, config, T(i * cadence));
                var p = mapped.Ps5Touch; var packet = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, mapped);
                check(physical.Ps5Touch == default && mapped.Updates == 123 && mapped.RawNotificationSequence == 456 &&
                    mapped.GyroX == 42 && mapped.MotionTimestampUs == 789 && mapped.IsPressed(GamepadButtons.West), "touch preserves input identity and real controls");
                check(packet.Length == 33 && packet[15] == (p.Active ? 1 : 0) && packet[20] == 0 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(11)) == p.X && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(13)) == p.Y,
                    "touch reaches actual PS5 wire offsets");
                if (p.Active && !wasActive) { starts++; check(p == Ps5TouchGestures.PointAt(gesture, 0), "each contact starts at origin"); }
                if (!p.Active && wasActive) { lifts++; check(last == Ps5TouchGestures.PointAt(gesture, 1), "natural completion exposes endpoint before lift"); }
                wasActive = p.Active; last = p;
            }
            int expected = action == RearButtonAction.Repeat ? 2 : 1;
            check(starts == expected && lifts == expected && !wasActive, "finite touch lifetime/no held retrigger " + channel + "/" + cadence + "/" + gesture + "/" + action);
        }
        var sequence = Config(new() { Enabled = true, Action = RearButtonAction.Sequence, TouchGesture = Ps5TouchGesture.SwipeUp, PressMs = 200,
            Steps = [new() { TouchGesture = Ps5TouchGesture.SwipeDown, PressMs = 200 }] });
        var engine = new RearButtonMapper(); engine.Apply(Physical(), ViiperVirtualMode.DualSenseLike, sequence, T(0));
        bool previous = false; int contacts = 0;
        for (int i = 1; i < 90; i++)
        {
            var p = engine.Apply(Physical(i == 1 ? GamepadButtons.PaddleLeft : 0), ViiperVirtualMode.DualSenseLike, sequence, T(i * 7.5)).Ps5Touch;
            if (p.Active && !previous) contacts++; previous = p.Active;
        }
        check(contacts == 2 && !previous, "adjacent sequence gestures have a real finger-up boundary");
    }
    private static void Safety()
    {
        var binding = new RearButtonBinding { Enabled = true, TouchGesture = Ps5TouchGesture.SwipeRight, PressMs = 400 };
        var config = Config(binding); var mapper = new RearButtonMapper();
        mapper.Apply(Physical(), ViiperVirtualMode.DualSenseLike, config, T(0));
        check(mapper.Apply(Physical(GamepadButtons.PaddleLeft), ViiperVirtualMode.DualSenseLike, config, T(7.5)).Ps5Touch.Active, "touch starts");
        check(mapper.HasPendingAction && mapper.TryCancelOutput(out var release) && !release.Ps5Touch.Active && release.Updates == 123, "cancel without notify lifts finger without fake input");
        check(!mapper.Apply(Physical(GamepadButtons.PaddleLeft), ViiperVirtualMode.DualSenseLike, config, T(15)).Ps5Touch.Active, "cancelled held key does not restart");
        mapper.Apply(Physical(), ViiperVirtualMode.DualSenseLike, config, T(22.5));
        check(mapper.Apply(Physical(GamepadButtons.PaddleLeft), ViiperVirtualMode.DualSenseLike, config, T(30)).Ps5Touch.Active, "release/new press rearms");
        check(!mapper.Apply(Physical(GamepadButtons.PaddleLeft), ViiperVirtualMode.DualSenseLike, config, T(200)).Ps5Touch.Active, "long source stall drops gesture");
        mapper.Reset(); mapper.Apply(Physical(), ViiperVirtualMode.DualSenseLike, config, T(0));
        mapper.Apply(Physical(GamepadButtons.PaddleLeft), ViiperVirtualMode.DualSenseLike, config, T(7.5));
        var changed = config with { Ps5 = config.Ps5 with { Left = binding with { TouchGesture = Ps5TouchGesture.SwipeLeft } } };
        check(!mapper.Apply(Physical(GamepadButtons.PaddleLeft), ViiperVirtualMode.DualSenseLike, changed, T(15)).Ps5Touch.Active, "profile edit lifts finger and suppresses held trigger");
        check(!mapper.Apply(Physical(GamepadButtons.PaddleLeft), ViiperVirtualMode.Xbox, config, T(22.5)).Ps5Touch.Active, "mode switch lifts finger");
        var wronglyAssigned = new RearButtonConfiguration { Xbox = config.Ps5, Pro1 = config.Ps5 };
        foreach (var mode in new[] { ViiperVirtualMode.Xbox, ViiperVirtualMode.Pro1, ViiperVirtualMode.Pro2 })
        { mapper.Reset(); mapper.Apply(Physical(), mode, wronglyAssigned, T(0)); check(!mapper.Apply(Physical(GamepadButtons.PaddleLeft), mode, wronglyAssigned, T(7.5)).Ps5Touch.Active, "touch disabled outside PS5 " + mode); }
        // A concurrent C gesture cannot replace/continue GL's finger halfway.
        config = config with { Ps5 = config.Ps5 with { C = binding with { TouchGesture = Ps5TouchGesture.SwipeLeft } } };
        mapper.Reset(); mapper.Apply(Physical(), ViiperVirtualMode.DualSenseLike, config, T(0));
        for (int i = 1; i < 120; i++)
        {
            var p = mapper.Apply(Physical(GamepadButtons.PaddleLeft | (i >= 10 ? GamepadButtons.Aux : 0)), ViiperVirtualMode.DualSenseLike, config, T(i * 7.5)).Ps5Touch;
            check(!p.Active || p.Y == 540 && p.X >= 260, "concurrent contact cannot splice direction");
            if (i > 60) check(!p.Active, "discarded conflict never starts later");
        }
        var physicalTouch = Physical(); physicalTouch.Ps5Touch = new(true, 300, 400);
        var pro = Physical(); pro.Rx = 3456;
        check(RearButtonMapper.MergeProfessionalControls(physicalTouch, pro) is { Ps5Touch.Active: true, Rx: 3456 }, "professional calibrated state retains mapped touch");
    }
}
