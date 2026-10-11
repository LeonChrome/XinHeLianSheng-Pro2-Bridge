using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class CompatibleRateRegressionTests
{
    private static BleCompatibilitySnapshot Compatibility(bool win10, bool configured = false, bool audited = true, bool api = true)
    {
        var facts = new BleEnvironmentFacts(true, win10 ? 19045 : 26200, !win10 && api, !win10 && api,
            audited ? Win10BleStackProfile.AuditedStackSha256 : "different-stack", configured);
        return new(facts, BleCompatibilityPolicy.Select(facts), "");
    }

    public static SystemBluetoothSnapshot Win10Fixture(bool configured, string elevated = "False", bool readable = true,
        bool restart = false, bool multiple = false)
    {
        var fixture = EnvironmentDiagnosticsRegressionTests.Fixture();
        return fixture with
        {
            System = fixture.System with { Family = "Windows 10", Version = "10.0.19045.6466", Release = "22H2" },
            Adapters = multiple ? fixture.Adapters : [fixture.Adapters[1] with { IsDefault = true }],
            CompatibilityTelemetry = Compatibility(true, configured).Telemetry,
            Win10Configuration = new(readable, configured, configured
                ? "LEConnectionIntervalMin=6 LEConnectionIntervalMax=6 LEConnectionLatency=0 LEConnectionLSTO=200"
                : "LEConnectionIntervalMin=absent LEConnectionIntervalMax=absent LEConnectionLatency=absent LEConnectionLSTO=absent",
                configured, restart, elevated)
        };
    }

    private static Pro2RateEvidence Rate(double hz = 66.4) => new(Pro2ConnectionPhase.RateLimited, true, true, false,
        hz, hz, 1000, 1000, 4, 12, "write_success", "fallback_Success", "ValidatedEnvironment",
        new(332, 15, 15.7, 30, .2, 31, 0, 320, 10), true);

    public static void Run(Action<bool, string> check)
    {
        bool Play(double raw, double parsed, double age = 4, uint count = 1000) =>
            Pro2GameplayRatePolicy.CanPlay(true, true, raw, parsed, count, count, age);
        foreach (double hz in new[] { 60, 63.96, 66.4, 100, 119.9, 120, 133.3 })
            check(Play(hz, hz), "real compatible rate playable " + hz);
        foreach (double hz in new[] { 0, 20, 30, 59.99, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            check(!Play(hz, hz), "invalid/insufficient rate not invented " + hz);
        foreach (double age in new[] { -1, 500, 2000, double.NaN, double.PositiveInfinity })
            check(!Play(66, 66, age), "stale/invalid age revokes start " + age);
        check(Play(66, 66, 499.9) && !Play(66, 66, 4, 19) && Play(66, 66, 4, 20), "freshness and minimum sample boundaries");
        check(!Play(133, 66) && !Play(66, 59) && !Play(double.NaN, 66), "raw/parsed integrity both necessary");
        check(!Pro2GameplayRatePolicy.CanPlay(false, true, 66, 66, 100, 100, 4) &&
            !Pro2GameplayRatePolicy.CanPlay(true, false, 66, 66, 100, 100, 4), "rate alone does not invent live transport");
        var pureMode = typeof(MainViewModel).Assembly.GetType("Y700Switch2V60Viiper.PureSoftware133Mode")!;
        check(Pro2GameplayRatePolicy.HighRateAcceptanceHz == (double)pureMode.GetField("MinimumAcceptedHz")!.GetValue(null)! &&
            (bool)pureMode.GetProperty("IsEnabled")!.GetValue(null)!, "compatibility does not relax 133Hz quality or fresh-source-only mode");

        var rate = Rate();
        var win10 = Compatibility(true);
        var missing = Win10Fixture(false);
        var diagnosis = BleLowRateDiagnoser.Evaluate(rate, win10, missing);
        check(diagnosis.Visible && diagnosis.Code == "win10_configuration_missing" && diagnosis.CanOfferWin10Configuration && rate.GameplayReady,
            "fresh Win10 PC can play 66Hz while offering explicit audited optimization");
        check(diagnosis.Evidence.Contains("absent") && diagnosis.Evidence.Contains("False") && diagnosis.Advice.Contains("提权") &&
            diagnosis.Advice.Contains("备份"), "missing system settings distinguished from current process privilege");
        check(BleLowRateDiagnoser.Evaluate(rate, win10, Win10Fixture(false, "True")).Code == "win10_configuration_missing",
            "administrator EXE alone never proves parameters applied");
        check(BleLowRateDiagnoser.Evaluate(rate, win10, Win10Fixture(false, multiple: true)).CanOfferWin10Configuration,
            "multiple radios may opt into global experiment without choosing an ambiguous hot-restart target");
        check(BleLowRateDiagnoser.Evaluate(rate, win10, null).CanOfferWin10Configuration,
            "global experiment does not pretend missing adapter inventory proves a per-link owner");
        check(BleLowRateDiagnoser.Evaluate(rate, Compatibility(true, audited: false), missing).CanOfferWin10Configuration,
            "unknown Win10 stack may explicitly consent to reversible experiment rather than hash rejection");
        check(BleLowRateDiagnoser.Evaluate(rate, win10, Win10Fixture(false, readable: false)).Code == "win10_configuration_unreadable",
            "read failure does not pretend configuration missing");
        check(BleLowRateDiagnoser.Evaluate(rate, win10, Win10Fixture(true, restart: true)).Code == "win10_restart_pending",
            "pending restart reported only with operation evidence");
        check(BleLowRateDiagnoser.Evaluate(rate, win10, Win10Fixture(true)).Code == "connection_interval_matches_rate",
            "configured Win10 with actual 15ms explains measured cadence rather than false missing privilege");
        check(BleLowRateDiagnoser.Evaluate(rate with { IntervalUnits = 0, Gaps = default }, win10, Win10Fixture(true)).Code == "win10_configured_but_slow",
            "configured defaults do not promise adoption by current link");
        check(!BleLowRateDiagnoser.Evaluate(rate, win10, Win10Fixture(true)).CanOfferWin10Configuration,
            "already configured Win10 not prompted to repeatedly write defaults");
        string configLog = SystemBluetoothDiagnostics.LogLines(Win10Fixture(true)).Last();
        check(configLog.Contains("[WIN10_BLE_CONFIGURATION]") && configLog.Contains("configured=True") &&
            configLog.Contains("process_elevated=False") && configLog.Contains("elevation_alone_does_not_apply_parameters=true"),
            "configuration and administrator state exported independently");

        var win11 = Compatibility(false);
        var env = EnvironmentDiagnosticsRegressionTests.Fixture();
        var interval = BleLowRateDiagnoser.Evaluate(rate, win11, env);
        check(interval.Code == "connection_interval_matches_rate" && interval.Summary.Contains("15.00") &&
            !interval.CanOfferWin10Configuration, "Win11 measured 15ms never offers Win10 registry route");
        check(BleLowRateDiagnoser.Evaluate(rate with { IntervalUnits = 0, Win11Route = "UnvalidatedEnvironment" }, win11, env).Code ==
            "win11_profile_unvalidated", "unknown custom Win11 profile explained honestly");
        check(BleLowRateDiagnoser.Evaluate(rate with { IntervalUnits = 0 }, Compatibility(false, api: false), env).Code ==
            "win11_parameter_api_unavailable", "Win11 missing API reported without private registry fallback");
        var inferred = BleLowRateDiagnoser.Evaluate(rate with { IntervalUnits = 0 }, win11, env);
        check(inferred.Code == "notify_15ms_inferred" && inferred.Summary.Contains("不是直接读取"), "timestamp inference is not claimed as measured connection interval");
        check(BleLowRateDiagnoser.Evaluate(rate with { IntervalUnits = 6, Gaps = new(400, 7.5, 22.5, 45, 2, 100, 300, 50, 50) }, win11, env).Code ==
            "notify_irregular", "long gap evidence distinguishes irregular notification from fixed 15ms");
        var mixed = rate with { IntervalUnits = 0, Fd2Hz = 68.69, ParsedHz = 68.69,
            Gaps = new(365, 7.73, 22.60, 23.11, 1.37, 30.01, 190, 3, 169), BatteryRefreshStatus = "measuring" };
        var mixedDiagnosis = BleLowRateDiagnoser.Evaluate(mixed, Compatibility(true, true), Win10Fixture(true));
        check(mixedDiagnosis.Code == "notify_mixed_short_and_skipped" && mixedDiagnosis.Evidence.Contains("190 / 3 / 169") &&
            mixedDiagnosis.Evidence.Contains("measuring"), "actual 23:50 Win10 trace reports mixed cadence, not missing privilege or fixed 15ms");
        check(BleLowRateDiagnoser.Evaluate(mixed with { HandlerSamples = 100, HandlerAverageUs = 4500 }, win11, env).Code ==
            "notify_handler_slow", "high measured local handler cost distinguished from unmeasured RF delay");
        check(BleLowRateDiagnoser.Evaluate(rate with { ParsedHz = 30 }, win11, env).Code == "parsed_below_notify",
            "parse loss takes priority over guessing radio fault");
        check(BleLowRateDiagnoser.Evaluate(rate with { DescriptorStatus = "write_ProtocolError" }, win11, env).Code == "fd2_request_unconfirmed",
            "unsuccessful report request diagnosed");
        check(BleLowRateDiagnoser.Evaluate(rate, win11, env).Code != "fd2_request_unconfirmed",
            "successful write does not depend on often unsupported descriptor readback");
        check(BleLowRateDiagnoser.Evaluate(rate with { LastInputAgeMs = 600, HighRate = true }, win11, env).Code == "input_stalled",
            "historical high-rate acceptance cannot hide stalled current input");
        var invalid = BleLowRateDiagnoser.Evaluate(rate with { Fd2Hz = double.NaN, ParsedHz = double.NaN }, win11, env);
        check(!invalid.Evidence.Contains("NaN") && !invalid.Evidence.Contains("Infinity"), "invalid evidence uses explicit unavailable text");
        var healthy = Rate(133.3) with { IntervalUnits = 6, HighRate = true, Phase = Pro2ConnectionPhase.Ready };
        check(!BleLowRateDiagnoser.Evaluate(healthy, win11, env).Visible, "verified live 133Hz completely hides warning");
        check(BleLowRateDiagnoser.Evaluate(Rate(119.9), win11, env).Visible, "sub-quality rate not silently reported as 133Hz");
        foreach (var phase in new[] { Pro2ConnectionPhase.Initializing, Pro2ConnectionPhase.Connecting,
                     Pro2ConnectionPhase.Scanning, Pro2ConnectionPhase.Measuring })
            check(!BleLowRateDiagnoser.Evaluate(rate with { Phase = phase }, win11, env).Visible,
                "negotiation does not prematurely diagnose failure " + phase);
        check(!BleLowRateDiagnoser.Evaluate(rate with { Connected = false }, win11, env).Visible &&
            !BleLowRateDiagnoser.Evaluate(rate with { Running = false }, win11, env).Visible, "disconnected/stopped warning cleared");

        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var boot = now.AddHours(-1);
        string Marker(string time, string operation = "apply", bool needed = true) =>
            "{\"restartRequired\":" + (needed ? "true" : "false") + ",\"operation\":\"" + operation + "\",\"completedAt\":\"" + time + "\"}";
        check(Win10ConfigurationAudit.RequiresRestart(Marker(now.AddMinutes(-10).ToString("O")), boot, now), "recent incomplete adapter restart marker detected");
        check(!Win10ConfigurationAudit.RequiresRestart(Marker(boot.AddMinutes(-10).ToString("O")), boot, now), "reboot invalidates prior restart-needed marker");
        check(!Win10ConfigurationAudit.RequiresRestart(Marker(now.AddMinutes(10).ToString("O")), boot, now), "future operation not credible restart evidence");
        check(!Win10ConfigurationAudit.RequiresRestart(Marker(now.ToString("O"), "restore"), boot, now) &&
            !Win10ConfigurationAudit.RequiresRestart(Marker(now.ToString("O"), needed: false), boot, now), "restore/success marker not pending apply");
        foreach (string marker in new[] { "", "not-json", "[]", "{}", "{\"operation\":1}", Marker("bad-date"), new string('x', 65537) })
            check(!Win10ConfigurationAudit.RequiresRestart(marker, boot, now), "bad optional marker harmless " + marker.Length);

        using var source = new SourceScope();
        long ticks = Stopwatch.GetTimestamp();
        source.Set("liveRateMeasuredTicks", ticks);
        source.Set("liveFd2RateHz", 66.4); source.Set("liveParsedRateHz", 66.4);
        source.Set("fd2InputNotifyCount", 332u); source.Set("fd2InputParsedCount", 332u);
        source.Set("lastParsedNotifyTicks", ticks); source.Set("transportConnected", true);
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source.Value, true);
        var counters = source.Value.GetCompactCounters();
        for (int i = 0; i < 500; i++) check(source.Value.IsGameplayReady && !source.Value.IsHighRateReady,
            "cached 66Hz view-model policy never manufactures 133Hz " + i);
        check(source.Value.GetCompactCounters() == counters, "diagnostic reads do not produce input or subscribe anything");
        source.Set("liveRateMeasuredTicks", ticks - 16 * Stopwatch.Frequency);
        check(!source.Value.IsGameplayReady && !source.Value.GetRateEvidence().RecentWindow, "expired window cannot retain old compatible frequency");
    }

    private sealed class SourceScope : IDisposable
    {
        public Pro2BleInputSource Value { get; } = new();
        public void Set(string name, object value) => typeof(Pro2BleInputSource)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Value, value);
        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
