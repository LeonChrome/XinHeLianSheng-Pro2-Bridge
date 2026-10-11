using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class RearButtonSafetyRegressionTests
{
    private static Action<bool, string> check = null!;
    private static long Tick(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
    private static readonly ViiperVirtualMode[] Modes = [ViiperVirtualMode.Xbox, ViiperVirtualMode.DualSenseLike, ViiperVirtualMode.Pro1];
    private static RearButtonConfiguration Config(RearButtonBinding binding, RearButtonBinding? right = null) =>
        new RearButtonConfiguration { Xbox = new() { Left = binding, Right = right ?? new() },
            Ps5 = new() { Left = binding, Right = right ?? new() }, Pro1 = new() { Left = binding, Right = right ?? new() } }.Normalize();
    private static GamepadState State(GamepadButtons buttons = GamepadButtons.None) => new()
    { Buttons = buttons, Lx = 1234, Ly = 2345, Rx = 3456, Ry = 987, Updates = 456, RawNotificationSequence = 789 };

    public static async Task RunAsync(Action<bool, string> assertion)
    {
        check = assertion;
        Deterministic(); EditorValidation(); Randomized();
        foreach (var mode in Modes)
        {
            await LiveConsumerAsync(mode);
            await LiveConsumerAsync(mode, cKey: true);
        }
    }

    private static void Deterministic()
    {
        foreach (var mode in Modes)
        {
            var config = Config(new() { Enabled = true, Action = RearButtonAction.Repeat,
                Buttons = GamepadButtons.South | GamepadButtons.L2, PressMs = 30, GapMs = 30 });
            var mapper = new RearButtonMapper();
            mapper.Apply(State(), mode, config, Tick(0));
            var physical = State(GamepadButtons.PaddleLeft | GamepadButtons.South | GamepadButtons.R2); physical.R2 = 512; physical.L2 = 128;
            check(mapper.Apply(physical, mode, config, Tick(7.5)).L2 == 4095, "repeat trigger active " + mode);
            physical.Buttons = GamepadButtons.West; physical.R2 = 999;
            check(mapper.TryCancelOutput(out var release) && release.Buttons == (GamepadButtons.South | GamepadButtons.R2) &&
                release.L2 == 128 && release.R2 == 512 && release.Lx == 1234 && release.Updates == 456,
                "cancel releases mapped contribution only, preserves real controls " + mode);
            physical.Buttons = GamepadButtons.PaddleLeft | GamepadButtons.South | GamepadButtons.R2; physical.R2 = 512;
            check(!mapper.TryCancelOutput(out _), "cancel is idempotent " + mode);
            for (int i = 0; i < 12; i++) check(mapper.Apply(physical, mode, config, Tick(15 + i * 7.5)).L2 == 128,
                "cancelled held rear cannot restart " + mode + "/" + i);
            mapper.Apply(State(), mode, config, Tick(110));
            check(mapper.Apply(State(GamepadButtons.PaddleLeft), mode, config, Tick(117.5)).IsPressed(GamepadButtons.South),
                "release and new press rearms " + mode);
            mapper.Reset(); mapper.Apply(State(), mode, config, Tick(0));
            check(mapper.Apply(State(GamepadButtons.PaddleLeft), mode, config, Tick(7.5)).IsPressed(GamepadButtons.South), "first pulse starts " + mode);
            check(!mapper.Apply(State(GamepadButtons.PaddleLeft), mode, config, Tick(67.5)).IsPressed(GamepadButtons.South),
                "skipped repeat gap still emits release " + mode);
            check(mapper.Apply(State(GamepadButtons.PaddleLeft), mode, config, Tick(75)).IsPressed(GamepadButtons.South),
                "current pulse follows release, not a backlog replay " + mode);
            mapper.Apply(State(GamepadButtons.PaddleLeft), mode, config, Tick(105));
            check(mapper.TryCancelOutput(out var forcedRelease, forceRelease: true) && forcedRelease.Buttons == GamepadButtons.None,
                "recovered stream can force release even if the computed repeat gap was never published " + mode);

            var sequence = Config(new() { Enabled = true, Action = RearButtonAction.Sequence, Buttons = GamepadButtons.East, PressMs = 30,
                Steps = [new() { Buttons = GamepadButtons.East, DelayMs = 0, PressMs = 30 }] });
            mapper.Reset(); mapper.Apply(State(), mode, sequence, Tick(0));
            mapper.Apply(State(GamepadButtons.PaddleLeft), mode, sequence, Tick(7.5));
            check(!mapper.Apply(State(), mode, sequence, Tick(37.5)).IsPressed(GamepadButtons.East), "adjacent identical sequence keys release " + mode);
            check(mapper.Apply(State(), mode, sequence, Tick(45)).IsPressed(GamepadButtons.East), "second same-key step creates distinct press " + mode);
            check(!mapper.Apply(State(), mode, sequence, Tick(67.5)).IsPressed(GamepadButtons.East), "sequence terminates " + mode);

            var delayed = Config(new() { Enabled = true, Action = RearButtonAction.Tap, Buttons = GamepadButtons.East, DelayMs = 60, PressMs = 30 });
            mapper.Reset(); mapper.Apply(State(), mode, delayed, Tick(0));
            mapper.Apply(State(GamepadButtons.PaddleLeft), mode, delayed, Tick(7.5));
            mapper.Apply(State(), mode, delayed, Tick(15));
            mapper.Apply(State(GamepadButtons.PaddleLeft), mode, delayed, Tick(22.5));
            check(mapper.Apply(State(GamepadButtons.PaddleLeft), mode, delayed, Tick(67.5)).IsPressed(GamepadButtons.East),
                "overlapping delayed taps do not restart or queue " + mode);
            check(!mapper.Apply(State(GamepadButtons.PaddleLeft), mode, delayed, Tick(97.5)).IsPressed(GamepadButtons.East),
                "held tap cannot retrigger on completion " + mode);
            mapper.Reset(); mapper.Apply(State(), mode, delayed, Tick(0));
            mapper.Apply(State(GamepadButtons.PaddleLeft), mode, delayed, Tick(7.5));
            check(mapper.HasPendingAction && !mapper.TryCancelOutput(out _) && !mapper.HasPendingAction,
                "pending delay cancels even before its first output " + mode);

            var shared = Config(new() { Enabled = true, Buttons = GamepadButtons.South }, new() { Enabled = true, Buttons = GamepadButtons.South });
            mapper.Reset(); mapper.Apply(State(), mode, shared, Tick(0));
            mapper.Apply(State(GamepadButtons.PaddleLeft | GamepadButtons.PaddleRight), mode, shared, Tick(7.5));
            check(mapper.Apply(State(GamepadButtons.PaddleRight), mode, shared, Tick(15)).IsPressed(GamepadButtons.South),
                "one rear release does not cancel the other rear's shared target " + mode);
            check(!mapper.Apply(State(), mode, shared, Tick(22.5)).IsPressed(GamepadButtons.South), "both rear releases clear shared target " + mode);

            var opposite = Config(new() { Enabled = true, Buttons = GamepadButtons.DPadUp }, new() { Enabled = true, Buttons = GamepadButtons.DPadDown });
            mapper.Reset(); mapper.Apply(State(), mode, opposite, Tick(0));
            check(mapper.Apply(State(GamepadButtons.PaddleLeft | GamepadButtons.PaddleRight), mode, opposite, Tick(7.5)).Buttons == GamepadButtons.None,
                "opposing rear directions neutralize " + mode);
            var direction = mapper.Apply(State(GamepadButtons.PaddleLeft | GamepadButtons.DPadDown), mode, opposite, Tick(15));
            check(direction.IsPressed(GamepadButtons.DPadDown) && !direction.IsPressed(GamepadButtons.DPadUp), "physical direction wins " + mode);
        }
    }

    private static void EditorValidation()
    {
        var editor = new RearButtonEditor("GL", false, new() { Enabled = true, Buttons = GamepadButtons.South });
        int changes = 0; editor.PropertyChanged += (_, _) => changes++;
        editor.Delay = "10"; editor.Duration = "90"; editor.Gap = "70"; editor.Count = "2";
        check(changes == 4, "all timing field edits notify draft tracking");
        editor.Action = "顺序按键"; editor.AddStepCommand.Execute(null); int before = changes;
        editor.Steps[0].Delay = "5"; editor.Steps[0].First = GamepadButtons.East;
        check(changes > before, "step timing and button edits notify parent draft");
        editor.Steps[0].Delay = "bad";
        check(!editor.TryBuild(out _, out _), "invalid visible sequence draft rejected");
        editor.Action = "按住映射"; editor.Duration = "bad"; editor.Gap = "bad"; editor.Count = "bad";
        check(editor.TryBuild(out var hold, out _) && hold.Steps.Length == 0 && hold.PressMs == 80 && hold.GapMs == 80,
            "invalid hidden fields cannot block another action or leak a stale sequence");
        editor.Buttons.First(b => b.Key == GamepadButtons.DPadUp).Selected = true;
        editor.Buttons.First(b => b.Key == GamepadButtons.DPadDown).Selected = true;
        check(!editor.TryBuild(out _, out _), "opposing directions cannot be saved enabled");
        editor.Enabled = false; editor.Delay = "bad";
        check(editor.TryBuild(out var disabled, out _) && !disabled.Enabled && disabled.DelayMs == 0,
            "disable remains available with malformed draft fields");
        var sanitized = new RearButtonBinding { Buttons = GamepadButtons.DPadUp | GamepadButtons.DPadDown | GamepadButtons.South }.Normalize();
        check(sanitized.Buttons == GamepadButtons.South, "malformed persisted opposite directions safely neutralized");
    }

    private static void Randomized()
    {
        var random = new Random(133137);
        foreach (var mode in Modes)
        foreach (bool cKey in new[] { false, true })
        {
            var mapper = new RearButtonMapper(); double time = 0;
            var binding = new RearButtonBinding { Enabled = true, Action = RearButtonAction.Repeat, Buttons = GamepadButtons.East | GamepadButtons.L2 };
            var config = !cKey ? Config(binding) : new RearButtonConfiguration { Xbox = new() { C = binding },
                Ps5 = new() { C = binding }, Pro1 = new() { C = binding } }.Normalize();
            for (int i = 0; i < 6000; i++)
            {
                time += new[] { 4.0, 7.5, 15.0, 60.0, 150.0 }[random.Next(5)];
                var physical = State((random.Next(2) == 0 ? cKey ? GamepadButtons.Aux : GamepadButtons.PaddleLeft : 0) | GamepadButtons.West);
                physical.Updates = (uint)i; physical.L2 = (ushort)random.Next(1000);
                if (i % 97 == 0) { mapper.TryCancelOutput(out _); }
                if (i % 71 == 0) config = config.Normalize(); // no-op snapshot replacement
                var output = mapper.Apply(physical, mode, config, Tick(time));
                check(output.IsPressed(GamepadButtons.West) && output.Updates == physical.Updates && output.Lx == physical.Lx &&
                    physical.L2 < 1000 && output.L2 >= physical.L2 && output.L2 <= 4095 && !RearButtonBinding.HasOppositeDirections(output.Buttons),
                    "random interleaved mapping safety invariant " + mode + (cKey ? "/C/" : "/GL/") + i);
            }
        }
    }

    private static async Task LiveConsumerAsync(ViiperVirtualMode mode, bool cKey = false)
    {
        RearButtonConfiguration LiveConfig(RearButtonBinding binding) => !cKey ? Config(binding) :
            new RearButtonConfiguration { Xbox = new() { C = binding }, Ps5 = new() { C = binding }, Pro1 = new() { C = binding } }.Normalize();
        var template = mode switch { ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox,
            ViiperVirtualMode.Pro1 => ViiperDeviceProfile.Pro1, _ => ViiperDeviceProfile.DualSenseLike };
        var profile = template with { SourcePaced = true };
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var writer = new TcpClient { NoDelay = true };
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting; reader.NoDelay = true;
        var stream = new ViiperDeviceStream(writer, writer.GetStream());
        var source = new FakeSource(); var logs = new ConcurrentQueue<string>();
        var config = LiveConfig(new() { Enabled = true, Buttons = GamepadButtons.South | GamepadButtons.L2 });
        await using var session = new ViiperBridgeSession(new("127.0.0.1", 1), profile,
            new InlineProgress<string>(logs.Enqueue), source, rearButtons: config);
        typeof(ViiperBridgeSession).GetMethod("SetStream", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [stream]);
        using var stop = new CancellationTokenSource();
        var physical = State(GamepadButtons.West | GamepadButtons.R2); physical.R2 = 512; physical.L2 = 128;
        source.Feed(physical);
        var loop = (Task)typeof(ViiperBridgeSession).GetMethod("InputLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [stop.Token])!;
        async Task<byte[]> Read() { var bytes = new byte[profile.InputSize]; using var timeout = new CancellationTokenSource(1000);
            await reader.GetStream().ReadExactlyAsync(bytes, timeout.Token); return bytes; }
        async Task Expect(GamepadState expected, string label) => check((await Read()).AsSpan().SequenceEqual(
            VirtualPadPackets.FromGamepad(profile, expected)), "actual output consumer " + mode + (cKey ? "/C/" : "/GL/") + label);
        var held = physical.Clone(); held.Buttons |= cKey ? GamepadButtons.Aux : GamepadButtons.PaddleLeft;
        GamepadState Mapped(GamepadButtons key) { var result = physical.Clone(); result.Buttons |= key | GamepadButtons.L2; result.L2 = 4095; return result; }
        try
        {
            await Expect(physical, "initial physical state");
            source.Feed(held); await Expect(Mapped(GamepadButtons.South), "mapping active");
            var other = mode == ViiperVirtualMode.Xbox ? config with { Ps5 = new() } : config with { Xbox = new() };
            session.SetRearButtons(other); source.Feed(held); await Expect(Mapped(GamepadButtons.South), "saving unrelated profile preserves held mapping");
            session.SetRearButtons(other.Normalize()); source.Feed(held); await Expect(Mapped(GamepadButtons.South), "no-op save preserves held mapping");
            uint updates = source.Updates;
            session.CancelRearButtons(); await Expect(physical, "cancel without fresh notify releases only synthetic controls");
            check(source.Updates == updates, "emergency release creates no source update " + mode);
            source.Feed(held); await Expect(physical, "cancelled held key suppressed");
            source.Feed(physical); await Expect(physical, "physical rear release");
            source.Feed(held); await Expect(Mapped(GamepadButtons.South), "rear rearmed");
            var changed = LiveConfig(new() { Enabled = true, Buttons = GamepadButtons.East | GamepadButtons.L2 });
            session.SetRearButtons(changed); await Expect(physical, "changed live profile releases without notify");
            source.Feed(held); await Expect(physical, "changed profile held key cannot fire");
            source.Feed(physical); await Expect(physical, "release after change");
            source.Feed(held); await Expect(Mapped(GamepadButtons.East), "new mapping starts on new physical press");
            updates = source.Updates;
            await Expect(physical, "stalled running source cancels mapped output");
            check(source.Updates == updates, "stall release creates no input update " + mode);
            await Task.Delay(70);
            check(!reader.GetStream().DataAvailable, "idle safety does not generate repeated virtual frames " + mode);
            source.Feed(held); await Expect(physical, "resumed held source cannot restart macro");
            source.Feed(physical); await Expect(physical, "release before simulated stream replacement");
            source.Feed(held); await Expect(Mapped(GamepadButtons.East), "mapped state before stream replacement");
            var setStream = typeof(ViiperBridgeSession).GetMethod("SetStream", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setStream.Invoke(session, [null]); source.Feed(held);
            await Task.Delay(20);
            check(!loop.IsCompleted, "temporary null API stream cannot silently end the input consumer " + mode);
            // Simulate the same force-cancel request made by RecoverStreamAsync.
            typeof(ViiperBridgeSession).GetField("rearCancelRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 3);
            setStream.Invoke(session, [stream]);
            await Expect(physical, "restored stream releases old mapped state before resuming input");
            await Expect(physical, "queued held state does not restart mapping after stream replacement");
            check(logs.Any(line => line.Contains("[REAR_SAFETY_RELEASE]") && line.Contains("counted_as_fd2=false")),
                "safety telemetry distinguishes release from actual input " + mode);
            source.Running = false; source.Wake(); await Expect(GamepadState.Neutral(), "disconnect clears all controls");
        }
        finally
        {
            stop.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
            listener.Stop();
        }
    }

    private sealed class InlineProgress<T>(Action<T> receive) : IProgress<T> { public void Report(T value) => receive(value); }
    private sealed class FakeSource : IGamepadInputSource, IGamepadRealtimeInputSource, IGamepadSignaledInputSource
    {
        private readonly object gate = new(); private readonly InputFrameSignal signal = new();
        private GamepadState? latest; private bool pending; private long timestamp;
        public bool Running { get; set; } = true;
        public bool IsRunning => Running; public string Status => "offline fake input";
        public uint Updates { get; private set; }
        public void Feed(GamepadState state) { lock (gate) { latest = state.Clone(); latest.Updates = ++Updates; timestamp = Stopwatch.GetTimestamp(); pending = true; } signal.Notify(); }
        public void Wake() => signal.Notify();
        public bool TryGetLatest(out GamepadState state, out TimeSpan age)
        { lock (gate) { state = latest ?? GamepadState.Neutral(); age = latest == null ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(timestamp); return latest != null; } }
        public bool TryGetNewest(out GamepadState state, out TimeSpan age, out int supersededCount)
        { lock (gate) { supersededCount = 0; TryGetLatest(out state, out age); bool result = pending; pending = false; return result; } }
        public Task<bool> WaitForInputAsync(TimeSpan timeout, CancellationToken token) => signal.WaitAsync(timeout, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
