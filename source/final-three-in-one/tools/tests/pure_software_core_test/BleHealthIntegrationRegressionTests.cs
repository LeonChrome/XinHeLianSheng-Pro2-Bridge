using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class BleHealthIntegrationRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var fixture = EnvironmentDiagnosticsRegressionTests.Fixture();
        check(Win11ValidatedConnectionProfile.SingleAuditedAdapter(fixture) == null,
            "multiple radios cannot grant automatic ABI permission from default-radio metadata");
        var single = fixture with { Adapters = [fixture.Adapters[0]] };
        check(Win11ValidatedConnectionProfile.SingleAuditedAdapter(single) == fixture.Adapters[0].Driver,
            "one fully enumerated central BLE radio permits exact tuple evaluation");
        foreach (var invalid in new[] { single with { Warning = "enumeration incomplete" },
            single with { Adapters = Array.Empty<RadioDiagnosticInfo>() },
            single with { Adapters = [single.Adapters[0] with { Central = false }] },
            single with { Adapters = [single.Adapters[0] with { LowEnergy = false }] } })
            check(Win11ValidatedConnectionProfile.SingleAuditedAdapter(invalid) == null,
                "partial/absent/incapable radio metadata never certifies automatic custom route");

        BleCompatibilitySnapshot Compatibility(int build, bool configured = false, bool api = true,
            bool apiOk = true, bool configOk = true) {
            var facts = new BleEnvironmentFacts(true, build, api, api,
                Win10BleStackProfile.AuditedStackSha256, configured);
            return new(facts, BleCompatibilityPolicy.Select(facts), "", apiOk, configOk);
        }
        var rate = new Pro2RateEvidence(Pro2ConnectionPhase.RateLimited, true, true, false,
            66.5, 66.5, 1000, 1000, 1, 12, "write_success", "fallback_Success", "UnvalidatedEnvironment",
            new(350, 15, 15.35, 29.96, .2, 29.96, 0, 348, 2), true);
        var external = BleLowRateDiagnoser.Evaluate(rate, Compatibility(26200), single);
        check(external.Code == "connection_interval_matches_rate" && external.Evidence.Contains("66.5") &&
            external.Evidence.Contains("15.00") && external.Advice.Contains("尝试一次"),
            "user's external Intel trace retains actual 15ms/66.5Hz and offers bounded compatibility trial");
        var unknownConfig = BleLowRateDiagnoser.Evaluate(rate with { IntervalUnits = 0 },
            Compatibility(19045, api: false, configOk: false), null);
        check(unknownConfig.Code == "win10_configuration_unknown" && !unknownConfig.CanOfferWin10Configuration,
            "failed configuration probe is unknown, not permission to overwrite missing defaults");
        var unknownApi = BleLowRateDiagnoser.Evaluate(rate with { IntervalUnits = 0, Win11Route = "ApiUnavailable" },
            Compatibility(26200, api: false, apiOk: false), fixture);
        check(unknownApi.Code == "win11_parameter_api_unknown", "API probe failure is not proven unavailable API");
        var noDefault = fixture with { Adapters = fixture.Adapters.Select(a => a with { IsDefault = false }).ToArray() };
        check(BleLowRateDiagnoser.Evaluate(rate, Compatibility(26200), noDefault).Evidence.Contains("未识别"),
            "diagnostic never substitutes first enumerated device as the known default");

        var monitor = new BleRateStabilityMonitor(1000);
        long end = 0;
        BleRateStabilitySnapshot Window(int count) {
            monitor.ObserveWindow(new(end, end + 5000, (ulong)count, (ulong)count)); end += 5000;
            return monitor.Snapshot(end);
        }
        var firstLow = rate with { Stability = Window(333) };
        check(!BleLowRateDiagnoser.Evaluate(firstLow, Compatibility(26200), fixture).Visible,
            "one complete low window does not oscillate home warning");
        var secondLow = rate with { Stability = Window(333) };
        check(BleLowRateDiagnoser.Evaluate(secondLow, Compatibility(26200), fixture).Visible,
            "two independent low windows trigger home diagnosis");
        var high = rate with { Fd2Hz = 133, ParsedHz = 133, HighRate = true };
        var recovering = high with { Stability = Window(665) };
        check(BleLowRateDiagnoser.Evaluate(recovering, Compatibility(26200), fixture).Code == "rate_recovering",
            "single good window cannot hide confirmed low-rate history");
        Window(665);
        check(!BleLowRateDiagnoser.Evaluate(high with { Stability = Window(665) }, Compatibility(26200), fixture).Visible,
            "three independent good windows hide warning without reconnect");
        check(BleLowRateDiagnoser.Evaluate(high with { Stability = monitor.Snapshot(end + 16000) },
            Compatibility(26200), fixture).Code == "measurement_stale", "stale stable history cannot masquerade as current measurement");
        check(!BleLowRateDiagnoser.Evaluate(rate with { Connected = false }, Compatibility(26200), fixture).Visible,
            "disconnect removes obsolete low-rate notice");

        var source = new Pro2BleInputSource();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(Pro2BleInputSource).GetField(name, flags)!.SetValue(source, value);
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
        long now = Stopwatch.GetTimestamp();
        Field("transportConnected", true); Field("connectionIntervalUnits", (ushort)36);
        Field("liveRateProbeTicks", now - 5 * Stopwatch.Frequency);
        Field("fd2InputNotifyCount", 665u); Field("fd2InputParsedCount", 665u);
        Field("lastParsedNotifyTicks", now);
        source.RecheckLiveRate();
        check(source.IsHighRateReady && source.GetRateEvidence().Fd2Hz > 130,
            "several real reports per connection event pass actual input gate despite 45ms system interval");
        var evidence = source.GetRateEvidence();
        for (int i = 0; i < 50; i++) source.GetRateEvidence();
        check(source.GetRateEvidence().Stability!.ValidWindowCount == evidence.Stability!.ValidWindowCount,
            "presentation polls do not create measurement windows");
        source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Environment.OSVersion.Version.Build < 22000)
        {
            var stopped = new Pro2BleInputSource();
            var outcome = stopped.TryWindows11CompatibilityAsync(new Progress<string>(_ => { }), default).GetAwaiter().GetResult();
            check(outcome.Code == "not_eligible" && !outcome.Retained, "Win10 refuses in-app Win11 call before any transport access");
            stopped.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
