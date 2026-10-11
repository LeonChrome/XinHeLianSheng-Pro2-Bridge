using System;
using Y700Switch2V60Viiper;

// Synthetic counter/timestamp windows only: no Bluetooth, registry, process,
// sleeping, wall clock, transport construction or device access.
internal static class BleRateStabilityRegressionTests
{
    private const long Frequency = 1000;

    public static void Run(Action<bool, string> check)
    {
        var monitor = new BleRateStabilityMonitor(Frequency);
        BleRateMeasurementWindow Window(int index, ulong raw = 665, ulong parsed = 665,
            NotifyGapSummary gaps = default) => new(index * 5000L, (index + 1) * 5000L, raw, parsed, gaps);
        BleRateStabilitySnapshot Read(int completedWindows) => monitor.Snapshot(completedWindows * 5000L);

        var initial = Read(0);
        check(initial.State == BleRateStabilityState.Observing && !initial.HasValidWindow &&
            !initial.IsRecent && !initial.IsStableHighRate && !initial.ShouldWarnLowRate &&
            initial.SessionMinimumHz == null && initial.SessionMaximumHz == null,
            "stability initialization is unmeasured, not a zero-rate failure or invented 133Hz");
        check(initial.HighRateThresholdHz == 128 && Pro2GameplayRatePolicy.MinimumGameplayHz == 60 &&
            Pro2GameplayRatePolicy.HighRateAcceptanceHz == 120,
            "128Hz sustained health threshold stays separate from 60Hz gameplay and 120Hz preflight");
        check(!monitor.ObserveWindow(new(0, 999, 0, 0)) && Read(1).ValidWindowCount == 0,
            "short initialization/instantaneous sample is not a completed rate window");

        check(monitor.ObserveWindow(Window(0)), "first independent 5s window accepted");
        for (int i = 0; i < 1000; i++)
        {
            monitor.Snapshot(5000 + i);
            monitor.ObserveWindow(Window(0));
        }
        check(Read(1).ValidWindowCount == 1 && Read(1).ConsecutiveGoodWindows == 1 &&
            !Read(1).IsStableHighRate && !Read(1).ShouldWarnLowRate,
            "per-frame reads and duplicate windows cannot fabricate multiple good windows");
        check(!monitor.ObserveWindow(new(4000, 9000, 665, 665)) && Read(1).ValidWindowCount == 1,
            "overlapping sliding windows do not count the same observations twice");
        monitor.ObserveWindow(Window(1, 650, 650));
        check(!Read(2).IsStableHighRate && Read(2).ConsecutiveGoodWindows == 2,
            "two good independent windows still require the third for stability");
        monitor.ObserveWindow(Window(2, 675, 675));
        var stable = Read(3);
        check(stable.State == BleRateStabilityState.StableHighRate && stable.IsStableHighRate &&
            stable.ValidWindowCount == 3 && stable.SessionMinimumHz == 130 && stable.SessionMaximumHz == 135,
            "three real 130/133/135Hz windows establish measured stable high rate");
        check(stable.LastWindow is { RawNotifications: 675, ParsedInputs: 675, Seconds: 5,
                RawHz: 135, ParsedHz: 135, WindowStartTicks: 10000, WindowEndTicks: 15000 } &&
            stable.ValidMeasuredSeconds == 15,
            "last completed window preserves actual raw/parsed counts, duration and monotonic boundaries");

        monitor.ObserveWindow(Window(3, 332, 332));
        check(!Read(4).ShouldWarnLowRate && Read(4).ConsecutiveLowWindows == 1 && !Read(4).IsStableHighRate,
            "one low 66Hz window revokes current stable label without premature low-rate alarm");
        for (int i = 0; i < 1000; i++) monitor.Snapshot(20000 + i);
        check(!Read(4).ShouldWarnLowRate && Read(4).ConsecutiveLowWindows == 1,
            "frame age changes and repeated UI reads do not manufacture a second low window");
        monitor.ObserveWindow(Window(4, 332, 332));
        check(Read(5).State == BleRateStabilityState.LowRate && Read(5).ShouldWarnLowRate &&
            Read(5).ConsecutiveLowWindows == 2 && Read(5).LowWindowCount == 2,
            "two independent low windows confirm sustained low input rate");
        monitor.ObserveWindow(Window(5));
        check(Read(6).State == BleRateStabilityState.Recovering && Read(6).ShouldWarnLowRate &&
            !Read(6).IsStableHighRate && Read(6).ConsecutiveGoodWindows == 1,
            "one recovery window cannot clear a confirmed sustained warning");
        monitor.ObserveWindow(Window(6));
        check(Read(7).ShouldWarnLowRate && Read(7).State == BleRateStabilityState.Recovering,
            "two recovery windows retain the warning until sufficient independent evidence");
        monitor.ObserveWindow(Window(7));
        var recovered = Read(8);
        check(recovered.IsStableHighRate && !recovered.ShouldWarnLowRate &&
            recovered.State == BleRateStabilityState.StableHighRate && recovered.RecentLowWindow?.WindowEndTicks == 25000 &&
            recovered.SessionMinimumHz == 66.4 && recovered.SessionMaximumHz == 135,
            "three recovery windows clear warning while preserving session extrema and latest low evidence");
        check(monitor.Snapshot(55000).IsRecent && monitor.Snapshot(55001).State == BleRateStabilityState.Stale &&
            !monitor.Snapshot(55001).IsStableHighRate && !monitor.Snapshot(55001).ShouldWarnLowRate &&
            monitor.Snapshot(39999).State == BleRateStabilityState.Stale,
            "15-second recent boundary is monotonic and cannot reuse expired or future-window success");

        monitor.Reset();
        check(Read(0).ValidWindowCount == 0 && Read(0).LowWindowCount == 0 &&
            Read(0).RecentLowWindow == null && Read(0).SessionMinimumHz == null &&
            Read(0).ConsecutiveGoodWindows == 0 && Read(0).ConsecutiveLowWindows == 0,
            "disconnect/counter-clear reset removes all prior connection health and history");
        check(monitor.ObserveWindow(Window(0, 0, 0)) && !Read(1).ShouldWarnLowRate &&
            Read(1).SessionMinimumHz == 0 && Read(1).LastWindow?.RawHz == 0,
            "a complete no-input window is a real 0Hz low sample, not skipped or good");
        monitor.ObserveWindow(Window(1, 0, 0));
        check(Read(2).ShouldWarnLowRate && Read(2).SessionMaximumHz == 0,
            "two complete no-input windows are a sustained failure");

        monitor.Reset();
        monitor.ObserveWindow(Window(0, 665, 330));
        monitor.ObserveWindow(Window(1, 665, 330));
        var parseLow = Read(2);
        check(parseLow.ShouldWarnLowRate && parseLow.LastWindow?.RawHz == 133 &&
            parseLow.LastWindow?.ParsedHz == 66 && parseLow.SessionMinimumRawHz == 133 &&
            parseLow.SessionMinimumParsedHz == 66 && parseLow.SessionMinimumHz == 66,
            "parsed losses remain valid evidence and cannot be hidden by healthy raw notification rate");
        check(!monitor.ObserveWindow(Window(2, 10, 11)) && Read(2).ValidWindowCount == 2,
            "more parsed inputs than notifications is invalid source evidence");

        monitor.Reset();
        foreach (var invalid in new[]
        {
            new BleRateMeasurementWindow(-1, 5000, 665, 665),
            new BleRateMeasurementWindow(1000, 1000, 665, 665),
            new BleRateMeasurementWindow(6000, 1000, 665, 665),
            new BleRateMeasurementWindow(0, 3999, 665, 665),
            new BleRateMeasurementWindow(0, 10001, 665, 665)
        }) check(!monitor.ObserveWindow(invalid), "invalid/reversed/partial/overlong window is not health evidence");
        check(Read(0).ValidWindowCount == 0 && Read(0).LowWindowCount == 0,
            "invalid windows do not contaminate streaks, extrema or counters");
        check(monitor.ObserveWindow(new(0, 4000, 512, 512)) &&
            monitor.ObserveWindow(new(4000, 14000, 1280, 1280)) &&
            monitor.Snapshot(14000).LastWindow?.EffectiveHz == 128,
            "window-duration limits accept their exact inclusive 4s/10s boundaries");

        monitor.Reset();
        monitor.ObserveWindow(Window(0));
        monitor.ObserveWindow(Window(1));
        monitor.ObserveWindow(Window(5));
        check(Read(6).ConsecutiveGoodWindows == 1 && !Read(6).IsStableHighRate,
            "an unmeasured time gap cannot join old and new good-window streaks");
        monitor.Reset();
        monitor.ObserveWindow(Window(0, 332, 332));
        monitor.ObserveWindow(Window(3, 332, 332));
        check(Read(4).ConsecutiveLowWindows == 1 && !Read(4).ShouldWarnLowRate,
            "low windows separated by unmeasured time are not consecutive alarms");

        foreach (ulong count in new ulong[] { 600, 605, 639, 640 })
        {
            var boundary = new BleRateStabilityMonitor(Frequency);
            for (int i = 0; i < 3; i++) boundary.ObserveWindow(Window(i, count, count));
            check(boundary.Snapshot(15000).IsStableHighRate == (count >= 640) &&
                boundary.Snapshot(15000).ShouldWarnLowRate == (count < 640),
                "health threshold does not hide 120/121/127.8Hz as stable: count=" + count);
        }
        var stricter = new BleRateStabilityMonitor(Frequency, new() { HighRateThresholdHz = 130 });
        for (int i = 0; i < 3; i++) stricter.ObserveWindow(Window(i, 645, 645));
        check(stricter.Snapshot(15000).ShouldWarnLowRate && stricter.Snapshot(15000).HighRateThresholdHz == 130,
            "custom health tolerance is applied to measured 129Hz rather than hardcoded target rate");
        var slowerConfirmation = new BleRateStabilityMonitor(Frequency,
            new() { GoodWindowsRequired = 4, LowWindowsRequired = 3 });
        for (int i = 0; i < 3; i++) slowerConfirmation.ObserveWindow(Window(i));
        check(!slowerConfirmation.Snapshot(15000).IsStableHighRate, "custom multiple-good confirmation is respected");
        slowerConfirmation.ObserveWindow(Window(3));
        for (int i = 4; i < 6; i++) slowerConfirmation.ObserveWindow(Window(i, 332, 332));
        check(!slowerConfirmation.Snapshot(30000).ShouldWarnLowRate, "custom three-low confirmation does not alarm after two");
        slowerConfirmation.ObserveWindow(Window(6, 332, 332));
        check(slowerConfirmation.Snapshot(35000).ShouldWarnLowRate, "custom three-low confirmation alarms at requested threshold");

        var fixed15 = new NotifyGapSummary(332, 15, 15.7, 30, .2, 31, 0, 320, 10);
        var mixed = new NotifyGapSummary(365, 7.73, 22.6, 23.11, 1.37, 30.01, 190, 3, 169);
        check(BleRateStabilityMonitor.ClassifyCadence(fixed15) == BleNotifyCadenceHint.Predominantly15Ms &&
            BleRateStabilityMonitor.ClassifyCadence(mixed) == BleNotifyCadenceHint.MixedShortAndLong,
            "fixed 15ms and mixed 7.5/15/22.5ms delivery have distinct evidence-only hints");
        check(BleRateStabilityMonitor.ClassifyCadence(default) == BleNotifyCadenceHint.Unknown &&
            BleRateStabilityMonitor.ClassifyCadence(fixed15 with { P50Ms = double.NaN }) == BleNotifyCadenceHint.Unknown &&
            BleRateStabilityMonitor.ClassifyCadence(fixed15 with { Near15 = 333 }) == BleNotifyCadenceHint.Unknown,
            "missing or malformed cadence evidence stays unavailable");
        monitor.Reset();
        monitor.ObserveWindow(Window(0, 332, 332, fixed15));
        monitor.ObserveWindow(Window(1, 343, 343, mixed));
        check(Read(2).CadenceHint == BleNotifyCadenceHint.MixedShortAndLong && !Read(2).CauseProven &&
            Read(2).LastWindow?.Gaps == mixed && Read(2).RecentLowWindow?.Gaps == mixed,
            "mode hints preserve raw summary and never claim a proven Bluetooth/driver/radio cause");

        var longSession = new BleRateStabilityMonitor(Frequency);
        for (int i = 0; i < 20000; i++) longSession.ObserveWindow(Window(i));
        check(longSession.Snapshot(100000000).ValidWindowCount == 20000 &&
            longSession.Snapshot(100000000).LastWindow?.WindowStartTicks == 99995000 &&
            longSession.Snapshot(100000000).RecentLowWindow == null &&
            longSession.Snapshot(100000000).SessionMinimumHz == 133,
            "long sessions keep aggregate counters/extrema and fixed last-window storage");

        void RejectPolicy(BleRateStabilityOptions options, string reason)
        {
            bool rejected = false;
            try { _ = new BleRateStabilityMonitor(Frequency, options); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "invalid monitor policy rejected: " + reason);
        }
        RejectPolicy(new() { HighRateThresholdHz = double.NaN }, "NaN threshold");
        RejectPolicy(new() { HighRateThresholdHz = double.PositiveInfinity }, "infinite threshold");
        RejectPolicy(new() { HighRateThresholdHz = 0 }, "zero threshold");
        RejectPolicy(new() { GoodWindowsRequired = 1 }, "single good window");
        RejectPolicy(new() { LowWindowsRequired = 1 }, "single low window");
        RejectPolicy(new() { MinimumWindowSeconds = 0 }, "zero minimum duration");
        RejectPolicy(new() { MaximumWindowSeconds = 3 }, "reversed duration range");
        RejectPolicy(new() { RecentWindowSeconds = 9 }, "freshness shorter than maximum duration");
        RejectPolicy(new() { MaximumContinuityGapSeconds = -1 }, "negative continuity gap");
        bool badFrequency = false;
        try { _ = new BleRateStabilityMonitor(0); }
        catch (ArgumentOutOfRangeException) { badFrequency = true; }
        check(badFrequency, "timestamp frequency must be positive");
    }
}
