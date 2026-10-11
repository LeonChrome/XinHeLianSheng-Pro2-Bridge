using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class RuntimeBatteryRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        BatteryRefreshRate baseline = new(132.7, 132.7, 7.5, 8.1, 22.5);
        BatteryRefreshRate after = new(132.5, 132.5, 7.5, 8.1, 37);
        byte[] payload = new byte[63]; payload[0] = 10; payload[1] = 28;
        Pro2InitialBatterySnapshotResult Valid(byte[]? data = null, bool clean = true) =>
            new(data ?? payload, clean, "captured", clean ? "none_verified" : "none_not_verified");
        check(Pro2RuntimeBatteryPolicy.CanProbe(baseline), "stable 133Hz baseline permits rare battery tap");
        foreach (double hz in new[] { double.NaN, double.PositiveInfinity, -1, 0, 20, 66, 120, 127.99 })
        {
            check(!Pro2RuntimeBatteryPolicy.CanProbe(baseline with { Fd2Hz = hz }), "insufficient or unknown FD2 blocks query");
            check(!Pro2RuntimeBatteryPolicy.CanProbe(baseline with { ParsedHz = hz }), "insufficient parsed baseline blocks query");
        }
        check(!Pro2RuntimeBatteryPolicy.CanProbe(baseline with { P50Ms = 15 }), "15ms BLE baseline does not receive extra queries");
        check(Pro2RuntimeBatteryPolicy.Evaluate(baseline, after, Valid(), true, 37).Accepted,
            "previously observed short jitter is bounded, not advertised as zero latency");
        check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after, Valid(), false, 7.5).Accepted, "old session never accepted");
        check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after, Valid(clean: false), true, 7.5).Accepted, "unverified CCCD never accepted");
        foreach (double hz in new[] { 20d, 66, 120, 127, double.NaN })
            check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after with { Fd2Hz = hz }, Valid(), true, 7.5).Accepted,
                "rate drop suspends updates");
        check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after with { P50Ms = 15 }, Valid(), true, 7.5).Accepted,
            "cadence change fails even with misleading average rate");
        check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after with { P95Ms = 30 }, Valid(), true, 7.5).Accepted,
            "bad tail latency suspends updates");
        foreach (double gap in new[] { 45.01, 60, 1000, double.NaN, -1 })
            check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after, Valid(), true, gap).Accepted,
                "operation-correlated gap has an explicit limit");
        check(Pro2RuntimeBatteryPolicy.Evaluate(baseline, after, Valid(), true, 45).Accepted, "explicit 45ms inclusive boundary");
        check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after with { MaxGapMs = 121 }, Valid(), true, 7.5).Accepted,
            "large input stall never hidden in average");
        check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after, Valid(new byte[63]), true, 7.5).Accepted, "zero cache rejected");
        check(!Pro2RuntimeBatteryPolicy.Evaluate(baseline, after, new(null, true, "timeout", "none_verified"), true, 7.5).Accepted,
            "timeout retains last value, never invents level");
        check(new V60UserSettings().RuntimeBatteryRefreshEnabled, "new install enables battery refresh by default");
        check(JsonSerializer.Deserialize<V60UserSettings>("{\"SelectedModeKey\":\"xbox\"}")!.RuntimeBatteryRefreshEnabled,
            "old settings without battery property adopt enabled default");
        check(!JsonSerializer.Deserialize<V60UserSettings>("{\"RuntimeBatteryRefreshEnabled\":false}")!.RuntimeBatteryRefreshEnabled,
            "explicit off survives settings migration");
        check(Pro2RuntimeBatteryPolicy.IntervalSeconds >= 120 && Pro2RuntimeBatteryPolicy.FirstRefreshSeconds >= 30,
            "battery operations do not run in the startup or per-frame path");
        check(Pro2RuntimeBatteryPolicy.IsFreshRuntimeLevel(true, 300) &&
            !Pro2RuntimeBatteryPolicy.IsFreshRuntimeLevel(true, 300.01) &&
            !Pro2RuntimeBatteryPolicy.IsFreshRuntimeLevel(false, 0) &&
            !Pro2RuntimeBatteryPolicy.IsFreshRuntimeLevel(true, double.NaN), "runtime freshness and snapshot distinction");
        long Tick(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
        check(Math.Abs(Pro2BleInputSource.OperationGap([Tick(37), Tick(44.5), Tick(52)], Tick(1), Tick(38), Tick(0.1)) - 36.9) < 0.01,
            "first gap crossing operation start is not lost by capture");
        check(Pro2BleInputSource.OperationGap([Tick(7.5), Tick(15), Tick(22.5), Tick(30), Tick(200), Tick(207.5)],
            Tick(1), Tick(8), Tick(0.1)) < 8, "unrelated late jitter is not mislabeled as query latency");

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var source = new Pro2BleInputSource();
        source.SetRuntimeBatteryRefreshEnabled(true);
        void Field(string name, object value) => typeof(Pro2BleInputSource).GetField(name, flags)!.SetValue(source, value);
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
        Field("transportConnected", true); Field("connectionSequence", 9L);
        var state = GamepadState.Neutral(); state.Buttons = GamepadButtons.West;
        Field("latest", state);
        var progress = new SilentProgress();
        bool Commit(byte[] report, long seq) => (bool)typeof(Pro2BleInputSource).GetMethod("CommitRuntimeBattery", flags)!
            .Invoke(source, [report, seq, Stopwatch.GetTimestamp(), progress])!;
        for (int level = 0; level <= 9; level++)
        {
            payload[0]++; payload[1] = (byte)(level << 2); payload[2] = 0xff;
            check(Commit(payload, 9), "fresh native level accepted " + level);
            var snapshot = source.GetBatterySnapshot();
            check(snapshot.State.BatteryLevel == level && snapshot.RuntimeLevel && snapshot.LevelFresh,
                "runtime origin and native level preserved " + level);
            check(snapshot.State.Buttons == GamepadButtons.West && source.GetCompactCounters().Fd2 == 0 &&
                source.GetCompactCounters().Parsed == 0, "battery does not route controls or inflate frequency " + level);
        }
        payload[1] = 0x18;
        check(!Commit(payload, 8) && source.GetBatterySnapshot().State.BatteryLevel == 9,
            "delayed old-session status cannot overwrite newest level");
        payload[1] = 0x3c;
        check(!Commit(payload, 9) && source.GetBatterySnapshot().State.BatteryLevel == 9, "invalid level cannot overwrite battery");
        source.SetRuntimeBatteryRefreshEnabled(false); payload[1] = 0x18;
        check(!Commit(payload, 9), "disabling in-flight update prevents commit");
        source.SetRuntimeBatteryRefreshEnabled(true); Field("disconnectSignalCaptured", true);
        check(!Commit(payload, 9), "disconnect in-flight update prevents commit");
        Field("disconnectSignalCaptured", false);
        Field("runtimeBatteryTask", Task.FromException(new InvalidOperationException("fixture")));
        await source.StopAsync();
        check(!source.IsRunning && !source.GetBatterySnapshot().RuntimeLevel, "faulted worker cannot block normal owner cleanup");
        check(source.GetBatterySnapshot().State.BatteryLevel == GamepadState.BatteryUnknown, "stop clears old native level");
        await source.DisposeAsync();
    }
    private sealed class SilentProgress : IProgress<string> { public void Report(string _) { } }
}
