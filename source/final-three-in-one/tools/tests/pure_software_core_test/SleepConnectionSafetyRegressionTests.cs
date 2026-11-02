using System.Threading.Channels;
using Y700Switch2V60Viiper;

internal static class SleepConnectionSafetyRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var gate = new SemaphoreSlim(1, 1);
        int inFlight = 0, maxInFlight = 0;
        var committed = new bool[4];
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Pro2SleepSafety.EnterAsync(gate,
            () => i != 1, async () =>
            {
                maxInFlight = Math.Max(maxInFlight, ++inFlight);
                await Task.Delay(3);
                inFlight--;
                return i != 3;
            }, () => Task.FromResult(i != 2), () => { committed[i] = true; return Task.CompletedTask; }, default)));
        check(maxInFlight == 1 && gate.CurrentCount == 1, "sleep preparation serialized across four controllers");
        check(results.SequenceEqual(new[] { Pro2SleepResult.Sleeping, Pro2SleepResult.Deferred,
            Pro2SleepResult.CommandFailed, Pro2SleepResult.PairingFailed }), "sleep failures and active peer are distinct");
        check(committed.SequenceEqual(new[] { true, false, false, false }), "only confirmed sleep stops its own source");

        bool idle = true; int writes = 0, commits = 0;
        await gate.WaitAsync();
        var queued = Pro2SleepSafety.EnterAsync(gate, () => idle, () => Task.FromResult(true),
            () => { writes++; return Task.FromResult(true); }, () => { commits++; return Task.CompletedTask; }, default);
        idle = false; gate.Release();
        check(await queued == Pro2SleepResult.Deferred && writes == 0, "new input while queued cancels sleep");
        idle = true;
        var changedDuringPair = await Pro2SleepSafety.EnterAsync(gate, () => idle,
            () => { idle = false; return Task.FromResult(true); }, () => { writes++; return Task.FromResult(true); },
            () => { commits++; return Task.CompletedTask; }, default);
        check(changedDuringPair == Pro2SleepResult.Deferred && writes == 0 && commits == 0,
            "new activity or changed settings during handshake cancel sleep before write");
        using (var acceptedCancel = new CancellationTokenSource())
        {
            var accepted = await Pro2SleepSafety.EnterAsync(gate, () => true, () => Task.FromResult(true),
                () => { acceptedCancel.Cancel(); return Task.FromResult(true); },
                () => { commits++; return Task.CompletedTask; }, acceptedCancel.Token);
            check(accepted == Pro2SleepResult.Sleeping && commits == 1,
                "cancellation after confirmed sleep still commits wake state");
            commits = 0;
        }
        using (var cancel = new CancellationTokenSource())
        {
            await gate.WaitAsync();
            var cancelled = Pro2SleepSafety.EnterAsync(gate, () => true, () => Task.FromResult(true),
                () => Task.FromResult(true), () => { commits++; return Task.CompletedTask; }, cancel.Token);
            cancel.Cancel();
            try { await cancelled; check(false, "queued sleep cancels"); } catch (OperationCanceledException) { }
            check(gate.CurrentCount == 0 && commits == 0, "cancelled waiter cannot release another operation's gate");
            gate.Release();
        }

        int listens = 0, retries = 0;
        await Pro2SleepSafety.WaitForWakeAsync(_ => ++listens switch
        {
            1 => Task.FromResult(false),
            2 => Task.FromException<bool>(new InvalidOperationException("RadioNotAvailable")),
            _ => Task.FromResult(true)
        }, (_, _) => { retries++; return Task.CompletedTask; }, default);
        check(listens == 3 && retries == 2, "watcher failure retries rather than pretending to wake");

        var channels = Enumerable.Range(0, 4).Select(_ => Channel.CreateUnbounded<ulong>()).ToArray();
        using (var cancel = new CancellationTokenSource())
        {
            Task[] waits = channels.Select((channel, index) => Pro2SleepSafety.WaitForWakeAsync(async token =>
            {
                while (true)
                {
                    ulong actual = await channel.Reader.ReadAsync(token);
                    if (Pro2SleepSafety.IsWakeAdvertisement((ulong)(index + 1), actual, true)) return true;
                }
            }, (_, token) => Task.Delay(1, token), cancel.Token)).ToArray();
            foreach (var channel in channels) channel.Writer.TryWrite(2);
            await waits[1].WaitAsync(TimeSpan.FromSeconds(2));
            check(!waits[0].IsCompleted && !waits[2].IsCompleted && !waits[3].IsCompleted,
                "controller 2 wake advertisement does not wake sleeping controllers 1 3 4");
            cancel.Cancel();
            try { await Task.WhenAll(waits); } catch (OperationCanceledException) { }
            check(waits.Where((_, i) => i != 1).All(t => t.IsCanceled), "stop listener cancels only waiters, no invented wake");
        }
        check(!Pro2SleepSafety.IsWakeAdvertisement(1, 1, false), "non Nintendo advertisement cannot wake slot");
        const ulong a = 0xBCC746A58DD2, b = 0xA8595F197E53;
        check(Pro2SleepSafety.ResolveHost("", [a]) == a, "single exposed radio resolves host");
        check(Pro2SleepSafety.ResolveHost("BluetoothLE#BluetoothLEa8:59:5f:19:7e:53-38:c6:ce:27:fc:2d", [a, b]) == b,
            "multi radio uses local address field rather than default adapter");
        check(Pro2SleepSafety.ResolveHost("BluetoothLE#38:c6:ce:27:fc:2d", [a, b]) == 0,
            "ambiguous multi radio cannot overwrite wake pairing");
        check(Pro2SleepSafety.ResolveHost("BluetoothLE#BluetoothLEbc:c7:46:a5:8d:d2-a8:59:5f:19:7e:53", [b]) == 0,
            "contradictory local radio ID is not silently replaced by another radio");
        check(Pro2SleepSafety.ResolveHost("BluetoothLE#BluetoothLEa8595f197e53-38c6ce27fc2d", [a, b]) == b,
            "compact Windows local address field resolves correctly");

        var baseline = new BleConnectionAttemptEvidence { Adapters = 1, CapableAdapters = 1, DefaultRadioState = "On",
            Outcome = "no_candidate", ScanModes = "active passive", Advertisements = 50, Stage = "scan" };
        void Diagnosis(BleConnectionAttemptEvidence e, string code) => check(BleConnectionDiagnosis.Evaluate(e).Code == code,
            "connection diagnosis " + code);
        Diagnosis(baseline, "no_pro2");
        Diagnosis(baseline with { Advertisements = 0 }, "no_advertisements");
        Diagnosis(baseline with { Adapters = 0, CapableAdapters = 0 }, "no_adapter");
        Diagnosis(baseline with { CapableAdapters = 0 }, "no_central");
        Diagnosis(baseline with { DefaultRadioState = "Off", Advertisements = 0 }, "radio_off");
        Diagnosis(baseline with { DefaultRadioState = "Off", Outcome = "Unreachable" }, "unreachable");
        Diagnosis(baseline with { Adapters = 2, DefaultRadioState = "Off" }, "no_pro2");
        Diagnosis(baseline with { Outcome = "AccessDenied" }, "access_denied");
        Diagnosis(baseline with { ScanError = "RadioNotAvailable" }, "scan_error");
        Diagnosis(baseline with { Outcome = "exception", Error = "COMException 0x80070005" }, "access_denied");
        Diagnosis(baseline with { Outcome = "reserved" }, "reserved");
        Diagnosis(baseline with { Outcome = "saved_not_seen" }, "saved_not_seen");
        Diagnosis(baseline with { Outcome = "Unreachable", Stage = "GATT 服务发现" }, "unreachable");
        Diagnosis(baseline with { Outcome = "channels_missing" }, "channels_missing");
        Diagnosis(baseline with { Outcome = "notify_timeout" }, "notify_timeout");
        Diagnosis(baseline with { Outcome = "timeout" }, "operation_failed");
        foreach (string outcome in new[] { "running", "connected", "cancelled" })
            check(!BleConnectionDiagnosis.Evaluate(baseline with { Outcome = outcome }).Visible, "healthy or cancelled hidden " + outcome);
        string diagnosisText = BleConnectionDiagnosis.Evaluate(baseline with { Outcome = "Unreachable" }).Advice;
        check(diagnosisText.Contains("不能据此认定") && !diagnosisText.Contains("驱动过旧导致"), "no fabricated driver or protocol root cause");
    }
}
