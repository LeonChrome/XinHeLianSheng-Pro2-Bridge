using Y700Switch2V60Viiper;

internal static class MixedSlotModeRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var modes = Pro2SlotModes.Options.Select(o => o.Mode).ToArray();
        foreach (string legacy in new[] { "pro2", "pro1", "xbox", "dualsense" })
            check(Pro2SlotModes.Normalize(null, legacy).SequenceEqual(Enumerable.Repeat(legacy, 4)),
                "legacy shared mode migrates to all four independent slots " + legacy);
        var settings = new V60UserSettings { SlotModeKeys = ["pro2", "xbox", "pro1", "dualsense"] };
        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<V60UserSettings>(json)!;
        check(loaded.SlotModeKeys.SequenceEqual(settings.SlotModeKeys), "independent modes survive settings round trip");
        for (int a = 0; a < 4; a++) for (int b = 0; b < 4; b++) for (int c = 0; c < 4; c++) for (int d = 0; d < 4; d++)
        {
            ViiperVirtualMode[] choice = [modes[a], modes[b], modes[c], modes[d]];
            var profiles = choice.Select((mode, i) => Pro2SlotModes.Profile(mode, i + 1, ViiperPushRateOption.Default)).ToArray();
            check(profiles.Select(p => p.Mode).SequenceEqual(choice), "all 256 mixed mode combinations retain independent selection");
            check(profiles.Select(p => p.DeviceSpecificSerialNumber).Distinct().Count() == 4,
                "all 256 mixed combinations have four independent USB identities");
            check(profiles.All(p => p.SourcePaced && p.InputSize == VirtualPadPackets.FromGamepad(p, new()).Length),
                "each mixed profile retains its own protocol width and physical source cadence");
        }
        foreach (var from in modes) foreach (var to in modes.Where(m => m != from))
        {
            var actual = Enumerable.Repeat(from, 4).Cast<ViiperVirtualMode?>().ToArray();
            var stopped = new int[4]; var created = new List<ViiperVirtualMode>();
            var result = await Pro2SlotModeSwitch.ReplaceAsync(from, to, () =>
            { stopped[1]++; actual[1] = null; return Task.CompletedTask; }, (mode, _) =>
            { created.Add(mode); actual[1] = mode; return Task.FromResult(true); }, default);
            check(result == Pro2SlotModeChangeResult.Changed && actual[1] == to && created.SequenceEqual(new[] { to }),
                "single slot mode replacement " + from + " to " + to);
            check(actual.Where((_, i) => i != 1).All(m => m == from) && stopped.SequenceEqual(new[] { 0, 1, 0, 0 }),
                "mode change leaves all three siblings untouched");
        }
        int stops = 0; var requested = new List<ViiperVirtualMode>();
        var failed = await Pro2SlotModeSwitch.ReplaceAsync(ViiperVirtualMode.Pro2, ViiperVirtualMode.Xbox,
            () => { stops++; return Task.CompletedTask; }, (mode, _) =>
            { requested.Add(mode); return Task.FromResult(mode == ViiperVirtualMode.Pro2); }, default);
        check(failed == Pro2SlotModeChangeResult.RestoredPrevious && stops == 1 &&
            requested.SequenceEqual(new[] { ViiperVirtualMode.Xbox, ViiperVirtualMode.Pro2 }), "failed create restores only the previous slot mode");
        var noPrevious = await Pro2SlotModeSwitch.ReplaceAsync(null, ViiperVirtualMode.Xbox, () => Task.CompletedTask,
            (_, _) => Task.FromResult(false), default);
        check(noPrevious == Pro2SlotModeChangeResult.OutputStopped, "failed new output does not invent a running mode");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try
        {
            await Pro2SlotModeSwitch.ReplaceAsync(null, ViiperVirtualMode.Xbox, () => { stops++; return Task.CompletedTask; },
                (_, _) => Task.FromResult(true), cancelled.Token);
            check(false, "cancelled switch must not start");
        }
        catch (OperationCanceledException) { check(stops == 1, "cancelled switch leaves existing output untouched"); }
    }
}
