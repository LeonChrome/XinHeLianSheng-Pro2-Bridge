using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Y700Switch2V60Viiper;

internal static class Program
{
    private static int checks;
    private static string output = "";
    [STAThread]
    public static int Main(string[] args)
    {
        output = args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "PRO2-core-qa");
        Directory.CreateDirectory(output);
        try
        {
            StartupRenderingRegressionTests.Run(Check);
            if (args.Contains("--native-rumble-only"))
            {
                NativeRumbleRegressionTests.Run(Check);
                NativeRumbleTimingRegressionTests.Run(Check);
                NativeRumbleMetricsRegressionTests.Run(Check);
                NativeRumblePacerRegressionTests.Run(Check);
                RecentSessionEvidenceRegressionTests.Run(Check);
                DualSenseFeedbackRoutingRegressionTests.Run(Check);
                ConvertedRumbleEncodingRegressionTests.Run(Check);
                ConvertedRumbleTimingRegressionTests.Run(Check);
                RumbleHoldAndPacingRegressionTests.Run(Check);
                Ps5HapticsRegressionTests.Run(Check);
                MotionModeRegressionTests.Run(Check);
                Console.WriteLine("PASS: " + checks + " native/PS5 rumble assertions; offline only.");
                return 0;
            }
            if (args.Contains("--capture-environment"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var environment = SystemBluetoothDiagnostics.CaptureAsync(timeout.Token).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(output, "actual-system-bluetooth.txt"), SystemBluetoothDiagnostics.Format(environment));
                Console.WriteLine(string.Join(Environment.NewLine, SystemBluetoothDiagnostics.LogLines(environment)));
                foreach (var target in BluetoothRestartPolicy.Targets(environment))
                    Console.WriteLine("[BLUETOOTH_RESTART_PLAN] name=\"" + target.Name + "\" pnp_id=\"" + target.PnpId +
                        "\" exact_radio=" + target.CanRestart + " present_started=" + BluetoothAdapterRestart.PresentStarted(target.PnpId) +
                        " restart_executed=false metadata_only=true");
                return 0;
            }
            TestReleasePackaging();
            UsbipDiscoveryRegressionTests.Run(Check, output);
            EnvironmentDiagnosticsRegressionTests.RunAsync(Check).GetAwaiter().GetResult();
            BleDiagnosticValidityRegressionTests.Run(Check, output);
            BleProtocolAuditRegressionTests.Run(Check);
            BluetoothRestartRegressionTests.Run(Check);
            CompatibleRateRegressionTests.Run(Check);
            BleRateStabilityRegressionTests.Run(Check);
            BleMeasuredTrialRegressionTests.Run(Check);
            BleAdaptivePresetRegressionTests.Run(Check);
            BleAdaptiveSessionRegressionTests.RunAsync(Check, output).GetAwaiter().GetResult();
            BleMeasurementWindowRegressionTests.RunAsync(Check).GetAwaiter().GetResult();
            BleAdaptiveDiagnosticsRegressionTests.Run(Check);
            BleAdaptiveDiagnosticStoreRegressionTests.Run(Check, output);
            BleAdaptiveAbiRegressionTests.Run(Check);
            Win10BlePresetRegressionTests.Run(Check);
            Win10GeneralizedStackRegressionTests.Run(Check);
            GeneralizedReportedEnvironmentRegressionTests.Run(Check);
            BleConfigurationOperationRegressionTests.Run(Check);
            Win10BleDiagnosticHistoryRegressionTests.Run(Check);
            BleOptimizationUiRegressionTests.Run(Check);
            BleGeneralizedUiRegressionTests.Run(Check);
            Win10HomeUiRegressionTests.Run(Check);
            BleHealthIntegrationRegressionTests.Run(Check);
            Pro1RegressionTests.Run(Check);
            ManualConnectionRegressionTests.RunAsync(Check).GetAwaiter().GetResult();
            RearButtonRegressionTests.Run(Check, output);
            RearButtonSafetyRegressionTests.RunAsync(Check).GetAwaiter().GetResult();
            CButtonDeviceRegressionTests.Run(Check);
            TestQueue();
            TestWin10StackPlan();
            TestCompatibilityMatrix();
            TestWindowsPreferencePolicy();
            TestWin11ValidatedProfile();
            TestBackupReader();
            TestExactRestoreOnDisposableKey();
            TestConnectionPresentation();
            TestRateLimitedRetentionAsync().GetAwaiter().GetResult();
            TestLiveRateTransitions();
            TestBattery();
            TestInitialBatterySnapshotAsync().GetAwaiter().GetResult();
            LiveBatteryReadRegressionTests.RunAsync(Check).GetAwaiter().GetResult();
            RuntimeBatteryRegressionTests.RunAsync(Check).GetAwaiter().GetResult();
            TestBatteryVisual();
            TestGapWindow();
            TestInputUpdateRateWindow();
            TestSignalAsync().GetAwaiter().GetResult();
            TestHapticsAsync().GetAwaiter().GetResult();
            DualSenseFeedbackRoutingRegressionTests.Run(Check);
            NativeRumbleRegressionTests.Run(Check);
            NativeRumbleTimingRegressionTests.Run(Check);
            NativeRumbleMetricsRegressionTests.Run(Check);
            NativeRumblePacerRegressionTests.Run(Check);
            RecentSessionEvidenceRegressionTests.Run(Check);
            ConvertedRumbleEncodingRegressionTests.Run(Check);
            ConvertedRumbleTimingRegressionTests.Run(Check);
            RumbleHoldAndPacingRegressionTests.Run(Check);
            Ps5HapticsRegressionTests.Run(Check);
            MotionModeRegressionTests.Run(Check);
            QualityRegressionTests.Run(Check, output);
            TestLogAsync().GetAwaiter().GetResult();
            TestUi();
            Console.WriteLine("PASS: " + checks + " assertions; offline only, no radio/driver changes.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        checks++;
    }

    private static void TestReleasePackaging()
    {
        var assembly = typeof(MainWindow).Assembly;
        Check(assembly.GetName().Version?.ToString() == "2.0.0.0", "v2 release assembly version");
        Check(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ==
            "2.0.0-data-fidelity", "v2 release metadata");
        Check(assembly.GetType("Y700Switch2V60Viiper.Pro2LiveBatteryReadPolicy") == null,
            "formal release excludes experimental battery query policy");
        Check(assembly.GetType("Y700Switch2V60Viiper.BleOutputIsolationExperiment") == null,
            "formal release excludes output isolation experiment");
        Check(assembly.GetType("Y700Switch2V60Viiper.BleNotifyLayerExperiment") == null &&
            assembly.GetType("Y700Switch2V60Viiper.BleNotifyLayerProbe") == null &&
            assembly.GetType("Y700Switch2V60Viiper.BleSchedulerLatencyProbe") == null &&
            assembly.GetType("Y700Switch2V60Viiper.BleRateWatchExperiment") == null &&
            assembly.GetType("Y700Switch2V60Viiper.BleRateFaultTrigger") == null,
            "formal release excludes minimal notify/time layer experiments");
        Check(typeof(Pro2BleInputSource).GetField("notifyLayerProbe", BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "formal notify path has no experimental callback probe or raw-only switch");
        Check(typeof(Pro2BleInputSource).GetMethod("RunLiveBatteryReadExperimentAsync") == null,
            "formal release excludes one-shot radio experiment");
        Check(typeof(Pro2BleInputSource).GetMethod("RunBatteryFollowupExperimentAsync") == null,
            "formal release excludes runtime Notify battery experiment");
        Check(typeof(MainViewModel).GetMethod("TestBatteryFollowupForLaunchAsync") == null &&
            typeof(MainViewModel).GetMethod("TestLiveBatteryReadForLaunchAsync") == null,
            "formal release has no automatic battery experiment entry");
        foreach (string name in new[] { "Embedded.viiper.haptic.exe", "Embedded.usbip.installer" })
        {
            using var binary = assembly.GetManifestResourceStream(name);
            Check(binary != null && binary.Length > 1000, "release includes offline setup binary " + name);
            Check(binary!.ReadByte() == 'M' && binary.ReadByte() == 'Z', "embedded setup PE signature " + name);
        }
        foreach (string name in new[] { "Embedded.viiper.haptic.license", "Embedded.usbip.license" })
        {
            using var license = assembly.GetManifestResourceStream(name);
            Check(license != null && license.Length > 100, "release retains dependency license " + name);
        }
        Check(ViiperDeviceProfile.Pro2.Mode == ViiperVirtualMode.Pro2 &&
            ViiperDeviceProfile.Xbox.Mode == ViiperVirtualMode.Xbox &&
            ViiperDeviceProfile.DualSenseLike.UsesDualSenseHaptics &&
            !ViiperDeviceProfile.Pro1.UsesDualSenseHaptics,
            "release mode capabilities match actual native audio support");
        if (!Environment.GetCommandLineArgs().Any(arg => arg.EndsWith("-off", StringComparison.OrdinalIgnoreCase) ||
            arg == Pro2InitialBatterySnapshot.DisableArgument))
            Check(Pro2QualityMode.GyroEnabled && Pro2QualityMode.HapticsEnabled &&
                Pro2InitialBatterySnapshot.IsRequested(Environment.GetCommandLineArgs()), "all release features active without flags");
    }

    private static void TestBattery()
    {
        var parser = new Pro2HidReportParser();
        byte[] primary = new byte[63];
        primary[2] = 1; // A: must remain parseable as power bits change.
        primary[6] = primary[9] = 8;
        primary[7] = primary[10] = 128;
        for (int power = 0; power < 256; power++)
        {
            primary[1] = (byte)power;
            Check(Pro2BatteryStatus.TryDecodePrimary(primary, out var battery), "power frame accepted " + power);
            int level = (power >> 2) & 15;
            Check(battery.HasLevel == (level <= 9) && battery.Level == level, "level bits only " + power);
            Check(battery.ExternalPower == ((power & 1) != 0) && battery.Charging == ((power & 2) != 0),
                "independent charging/external power flags " + power);
            Check(parser.TryParsePrimaryProPayload(primary, out var state, out _) &&
                state.IsPressed(GamepadButtons.South), "controls survive any power level " + power);
            Check(state.BatteryLevel == (level <= 9 ? level : GamepadState.BatteryUnknown) &&
                state.BatteryPercent == battery.ApproximatePercent && state.BatteryPowerKnown,
                "known/unknown SOC preserved " + power);
            Check(state.Clone().BatteryLevel == state.BatteryLevel &&
                state.Clone().BatteryExternalPower == state.BatteryExternalPower, "battery clone " + power);
            Check(Pro2BatteryStatus.Display(state, false) == "电量未知", "stale battery hidden " + power);
        }
        Check(!Pro2BatteryStatus.TryDecodePrimary(new byte[10], out _), "truncated status rejected");
        var empty = new Pro2BatteryStatus(0);
        var emptyState = GamepadState.Neutral(); empty.ApplyTo(emptyState);
        Check(Pro2BatteryStatus.Display(emptyState, true) == "0/9 格", "empty battery is not unknown");
        new Pro2BatteryStatus(0x23).ApplyTo(emptyState);
        Check(Pro2BatteryStatus.Display(emptyState, true) == "8/9 格 · 充电中", "charging battery presentation");
        Check(emptyState.BatteryPercentApproximate && emptyState.BatteryPercent == 89,
            "SOC conversion explicitly approximate; never shown as exact in UI");
        byte[] chargeReply = Convert.FromHexString("0B0101041078000000000300");
        Check(Pro2ChargeStatus.TryDecodeReply(chargeReply, out var unplugged) && unplugged.Known &&
            !unplugged.Charging && !unplugged.ExternalPower && unplugged.Flags == 3,
            "measured charge reply flags are NOT primary Power Info");
        foreach (ushort code in new ushort[] { 0, 0x20, 0x30, 0x31, 0x34, 0x3f, 0x21, 0x40, 0xffff })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(chargeReply.AsSpan(8, 2), code);
            Check(Pro2ChargeStatus.TryDecodeReply(chargeReply, out var charging), "charge reply structure " + code);
            charging.ApplyTo(emptyState);
            bool known = code == 0 || code == 0x20 || code >= 0x30 && code <= 0x3f;
            Check(emptyState.BatteryPowerKnown == known &&
                emptyState.BatteryCharging == (code >= 0x30 && code <= 0x3f), "charge code known/unknown " + code);
        }
        chargeReply[5] = 0x81;
        Check(!Pro2ChargeStatus.TryDecodeReply(chargeReply, out _), "negative battery ACK rejected");
        chargeReply[5] = 0x78; chargeReply[3] = 3;
        Check(!Pro2ChargeStatus.TryDecodeReply(chargeReply, out _), "voltage ACK not misparsed as charge status");
        Check(!Pro2ChargeStatus.TryDecodeReply(new byte[11], out _), "short charge reply rejected");
        new Pro2BatteryStatus(0x23).ApplyTo(emptyState);

        byte[] fd2 = new byte[60];
        fd2[1] = 0x23; // Counter, NOT power flags.
        fd2[11] = fd2[14] = 8; fd2[12] = fd2[15] = 128;
        BinaryPrimitives.WriteUInt16LittleEndian(fd2.AsSpan(31, 2), 4025);
        Check(parser.TryParseFd2Payload(fd2, out var fd2State, out _) &&
            fd2State.BatteryVoltageMillivolts == 4025 && fd2State.BatteryLevel == GamepadState.BatteryUnknown &&
            fd2State.BatteryPowerKnown && !fd2State.BatteryCharging,
            "FD2 voltage never guessed into SOC; charge is passive; counter is not power");
        for (int code = 0; code <= 255; code++)
        {
            fd2[33] = (byte)code;
            fd2[34] = 0xff; // Current/unknown byte must not become charge high byte.
            Check(Pro2ChargeStatus.TryDecodeFd2(fd2, out var passive) && passive.Code == code,
                "passive FD2 charge byte " + code);
            Check(parser.TryParseFd2Payload(fd2, out var passiveState, out _) &&
                passiveState.BatteryPowerKnown == passive.Known &&
                passiveState.BatteryCharging == passive.Charging &&
                passiveState.BatteryExternalPower == passive.ExternalPower &&
                passiveState.BatteryLevel == GamepadState.BatteryUnknown,
                "passive charge never manufactures SOC " + code);
        }
        Check(!Pro2ChargeStatus.TryDecodeFd2(new byte[59], out _), "truncated FD2 battery ignored");

        var source = new Pro2BleInputSource();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(Pro2BleInputSource).GetField(name, flags)!.SetValue(source, value);
        var log = new BatteryProgress();
        var update = (BatteryUpdate)typeof(Pro2BleInputSource).GetMethod("UpdateBatteryStatus", flags)!
            .CreateDelegate(typeof(BatteryUpdate), source);
        primary[1] = 0x23;
        for (int i = 0; i < 100; i++) Check(update(primary, "offline_fixture", log), "status-only update " + i);
        Check(log.Messages.Count == 1, "unchanged battery logs only once, not per frame");
        Check(source.GetCompactCounters().Raw == 0 && source.GetCompactCounters().Fd2 == 0 &&
            source.GetCompactCounters().Parsed == 0, "battery status does not inflate any input rate");
        Check(!update(new byte[10], "offline_fixture", log) &&
            source.GetBatterySnapshot().State.BatteryLevel == 8, "short read cannot overwrite last valid battery");
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
        Field("latest", emptyState); Field("batteryStatus", new Pro2BatteryStatus(0x23));
        Field("batteryStatusTicks", Stopwatch.GetTimestamp());
        Check(source.GetBatterySnapshot().Fresh && source.GetBatterySnapshot().State.BatteryLevel == 8,
            "fresh source battery snapshot");
        Field("batteryStatusTicks", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 96);
        Check(!source.GetBatterySnapshot().Fresh, "battery status expires after failed reads");
        Check(source.GetBatterySnapshot().HasConnectionLevel &&
            source.GetBatterySnapshot().State.BatteryLevel == 8, "old level only retained as explicit connection snapshot");
        Field("chargeStatus", new Pro2ChargeStatus(0x30, 0));
        Field("chargeStatusTicks", Stopwatch.GetTimestamp());
        var liveCharge = source.GetBatterySnapshot();
        Check(liveCharge.Fresh && liveCharge.State.BatteryCharging &&
            BatteryVisual.Create(liveCharge.State, true, true, liveCharge.Fresh).Charge == "充电中",
            "fresh charge remains visible after connection snapshot ages");
        Field("chargeStatusTicks", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 96);
        Check(!source.GetBatterySnapshot().Fresh && !source.GetBatterySnapshot().State.BatteryPowerKnown,
            "expired charge does not remain asserted");
        typeof(Pro2BleInputSource).GetMethod("ResetLiveState", flags)!.Invoke(source, null);
        Check(!source.GetBatterySnapshot().Fresh &&
            source.GetBatterySnapshot().State.BatteryLevel == GamepadState.BatteryUnknown,
            "reconnect cannot leak old controller battery");
        source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private delegate bool BatteryUpdate(ReadOnlySpan<byte> payload, string source, IProgress<string> progress);
    private sealed class BatteryProgress : IProgress<string>
    {
        public System.Collections.Generic.List<string> Messages { get; } = new();
        public void Report(string message) => Messages.Add(message);
    }

    private static async Task TestInitialBatterySnapshotAsync()
    {
        Check(Pro2InitialBatterySnapshot.IsRequested([]), "release EXE enables initial snapshot without a launcher");
        Check(!Pro2InitialBatterySnapshot.ResolveRequested([], false), "non-release snapshot stays opt-in");
        Check(Pro2InitialBatterySnapshot.ResolveRequested([Pro2InitialBatterySnapshot.EnableArgument], false),
            "legacy snapshot launcher remains compatible in non-release build");
        Check(!Pro2InitialBatterySnapshot.IsRequested([Pro2InitialBatterySnapshot.DisableArgument]),
            "snapshot diagnostic off switch overrides release default");
        Check(Pro2InitialBatterySnapshot.IsRequested(["--BATTERY-INIT-SNAPSHOT-TEST"]), "snapshot opt-in case insensitive");
        Check(!Pro2InitialBatterySnapshot.IsRequested([Pro2InitialBatterySnapshot.EnableArgument,
            Pro2InitialBatterySnapshot.DisableArgument]), "battery off overrides experimental opt-in");
        var valid = new byte[63]; valid[1] = 0x1c;
        Check(Pro2InitialBatterySnapshot.IsValid(valid), "real primary battery level accepted");
        Check(!Pro2InitialBatterySnapshot.IsValid(new byte[63]), "all-zero snapshot rejected");
        Check(!Pro2InitialBatterySnapshot.IsValid(new byte[10]), "truncated snapshot rejected");
        var invalidLevel = (byte[])valid.Clone(); invalidLevel[1] = 0x3c;
        Check(!Pro2InitialBatterySnapshot.IsValid(invalidLevel), "invalid nine-step snapshot rejected");
        var zeroLevel = (byte[])valid.Clone(); zeroLevel[1] = 0; zeroLevel[6] = 8;
        Check(Pro2InitialBatterySnapshot.IsValid(zeroLevel), "real empty battery accepted, no fake level");
        var calls = new System.Collections.Generic.List<string>();
        Task<bool> Enable(CancellationToken ct) { calls.Add("enable"); return Task.FromResult(true); }
        Task<byte[]> Receive(CancellationToken ct) { calls.Add("receive"); return Task.FromResult(valid); }
        Task<bool> Release(CancellationToken ct)
        {
            Check(!ct.IsCancellationRequested, "cleanup has independent live token");
            calls.Add("release"); return Task.FromResult(true);
        }
        var result = await Pro2InitialBatterySnapshot.CaptureAsync(Enable, Receive, Release, CancellationToken.None);
        Check(result.Status == "captured" && result.Payload![1] == 0x1c &&
            result.SubscriptionReleased && result.CleanupStatus == "none_verified", "snapshot success requires verified cleanup");
        Check(string.Join(",", calls) == "enable,receive,release", "initialization sequence; no gameplay action");
        calls.Clear();
        result = await Pro2InitialBatterySnapshot.CaptureAsync(ct => { calls.Add("reject"); return Task.FromResult(false); },
            Receive, Release, CancellationToken.None);
        Check(result.Status == "subscribe_rejected" && result.Payload == null && result.SubscriptionReleased,
            "subscription failure preserves unknown level");
        Check(string.Join(",", calls) == "reject,release", "rejected enable still cleans up and never receives");
        result = await Pro2InitialBatterySnapshot.CaptureAsync(Enable, ct => Task.FromResult(invalidLevel), Release, CancellationToken.None);
        Check(result.Status == "invalid_report" && result.Payload == null && result.SubscriptionReleased,
            "invalid snapshot still releases report stream");
        result = await Pro2InitialBatterySnapshot.CaptureAsync(Enable, Receive, ct => Task.FromResult(false), CancellationToken.None);
        Check(!result.SubscriptionReleased && result.CleanupStatus == "none_not_verified", "failed cleanup must block FD2 startup");
        result = await Pro2InitialBatterySnapshot.CaptureAsync(ct => throw new InvalidOperationException(), Receive,
            Release, CancellationToken.None);
        Check(result.Status == "error_InvalidOperationException" && result.Payload == null && result.SubscriptionReleased,
            "enable exception cannot leak report subscription");
        result = await Pro2InitialBatterySnapshot.CaptureAsync(Enable, ct => throw new InvalidOperationException(),
            Release, CancellationToken.None);
        Check(result.Status == "error_InvalidOperationException" && result.SubscriptionReleased,
            "receive exception cannot leak report subscription");
        result = await Pro2InitialBatterySnapshot.CaptureAsync(Enable, Receive,
            ct => throw new InvalidOperationException(), CancellationToken.None);
        Check(!result.SubscriptionReleased && result.CleanupStatus == "error_InvalidOperationException",
            "cleanup exception never claims release success");
        result = await Pro2InitialBatterySnapshot.CaptureAsync(Enable,
            async ct => { await Task.Delay(Timeout.Infinite, ct); return valid; }, Release,
            CancellationToken.None, TimeSpan.FromMilliseconds(25));
        Check(result.Status == "timeout" && result.Payload == null && result.SubscriptionReleased,
            "bounded timeout ends subscription, no background retry");
        result = await Pro2InitialBatterySnapshot.CaptureAsync(Enable, Receive,
            async ct => { await Task.Delay(Timeout.Infinite, ct); return true; },
            CancellationToken.None, TimeSpan.FromMilliseconds(25));
        Check(!result.SubscriptionReleased && result.CleanupStatus.Contains("CanceledException"),
            "cleanup deadline never permits FD2 continuation");
        using var cancel = new CancellationTokenSource();
        int cleanupCount = 0;
        try
        {
            await Pro2InitialBatterySnapshot.CaptureAsync(Enable,
                async ct => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); return valid; },
                ct => { cleanupCount++; Check(!ct.IsCancellationRequested, "cancel cannot cancel cleanup"); return Task.FromResult(true); }, cancel.Token);
            Check(false, "caller cancellation must propagate");
        }
        catch (OperationCanceledException) { Check(cleanupCount == 1, "caller cancellation cleans up exactly once"); }
        calls.Clear();
        try
        {
            await Pro2InitialBatterySnapshot.CaptureAsync(Enable, Receive, Release, cancel.Token);
            Check(false, "pre-canceled capture must propagate");
        }
        catch (OperationCanceledException) { Check(calls.Count == 0, "pre-canceled capture never touches controller"); }
    }

    private static void TestQueue()
    {
        var queue = new Pro2SequentialInputQueue();
        var idle = GamepadState.Neutral();
        var press = idle.Clone();
        press.Buttons = GamepadButtons.South;
        press.Lx = 2400;
        queue.Enqueue(idle, 1);
        queue.Enqueue(press, 2);
        press.Lx = 2500;
        queue.Enqueue(press, 3);
        queue.Enqueue(idle, 4);
        Check(queue.TryDequeueRealtimeEdges(out var first, out _, out _) &&
              first.Buttons == idle.Buttons, "neutral edge preserved");
        Check(queue.TryDequeueRealtimeEdges(out var second, out long ticks, out int skipped) &&
              second.Buttons == GamepadButtons.South && second.Lx == 2500 && ticks == 3 &&
              skipped == 1, "press retained; axes coalesced within press");
        Check(queue.TryDequeueRealtimeEdges(out var third, out _, out _) &&
              third.Buttons == idle.Buttons, "release retained in same BLE batch");
        Check(!queue.TryDequeueRealtimeEdges(out _, out _, out _), "no duplicated frame");
        press.Buttons = 0;
        press.L2 = 10;
        queue.Enqueue(idle, 5);
        queue.Enqueue(press, 6);
        queue.Enqueue(idle, 7);
        Check(queue.TryDequeueRealtimeEdges(out _, out _, out _) && queue.Count == 2,
            "trigger press not coalesced away");
        queue.Reset();
        for (int i = 0; i < 1000; i++) queue.Enqueue(press, i);
        Check(queue.Count == 64 && queue.OverflowDropCount == 936, "bounded source queue");
        Check(queue.TryDequeueRealtimeEdges(out _, out long newest, out _) && newest == 999,
            "newest axes survive overflow");
        queue.Reset();
        Check(queue.Count == 0, "session boundary clears stale frames");
    }

    private static void TestWin10StackPlan()
    {
        Check(Win10BleStackProfile.RegistrySubKey == @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters",
            "stack reader path, not adapter-level guessed path");
        Check(Win10BleStackProfile.Values.Count == 4, "only four audited global defaults");
        Check(Win10BleStackProfile.Values["LEConnectionIntervalMin"] == 6 &&
              Win10BleStackProfile.Values["LEConnectionIntervalMax"] == 6, "fixed 7.5ms");
        Check(Win10BleStackProfile.Values["LEConnectionLatency"] == 0 &&
              Win10BleStackProfile.Values["LEConnectionLSTO"] == 200, "zero latency and safe 2s supervision");
        Check(!Win10BleStackProfile.Values.ContainsKey("ConnectionIntervalMin"), "do not reuse ineffective adapter names");
        Check(Win10BleStackProfile.IsAudited(19045, Win10BleStackProfile.AuditedStackSha256), "audited Win10 accepted");
        Check(Win10BleStackProfile.IsAudited(19045, Win10BleStackProfile.AuditedStackSha256.ToLowerInvariant()), "hash case insensitive");
        Check(!Win10BleStackProfile.IsAudited(22000, Win10BleStackProfile.AuditedStackSha256), "Win11 never writes Win10 defaults");
        Check(!Win10BleStackProfile.IsAudited(19045, "unknown"), "unknown stack is experimental, not falsely labeled audited");
        Check(!Win10BleStackProfile.IsAudited(18362, Win10BleStackProfile.AuditedStackSha256), "old Win10 not silently assumed");
    }

 
    private static void TestWin11ValidatedProfile()
    {
        var valid = new BluetoothAdapterDiagnosticInfo("offline", "USB\\VID_8087&PID_0026\\offline", "8087", "0026",
            "Intel Corporation", Win11ValidatedConnectionProfile.ValidatedDriverVersion, "offline", "offline");
        var adapters = new BluetoothAdapterDiagnosticInfo?[] {
            valid, null, valid with { Vid = "0BDA" }, valid with { Pid = "0852" },
            valid with { DriverProvider = "Other" }, valid with { DriverVersion = "23.0.0.0" },
            valid with { DriverProvider = null! }, valid with { DriverVersion = null! }
        };
        foreach (bool windows in new[] { false, true })
        foreach (int build in new[] { 7601, 19045, 22000, 26100, 26200, 30000 })
        foreach (bool request in new[] { false, true })
        foreach (bool read in new[] { false, true })
        foreach (bool configured in new[] { false, true })
        foreach (bool explicitTest in new[] { false, true })
        foreach (string hash in new[] { Win11ValidatedConnectionProfile.ValidatedStackSha256, "unknown" })
        foreach (var adapter in adapters)
        {
            var facts = new BleEnvironmentFacts(windows, build, request, read, hash, configured);
            var route = Win11ValidatedConnectionProfile.Select(facts, adapter, explicitTest);
            bool allowed = windows && build >= 22000 && request && read &&
                (explicitTest || (build == 26200 && hash == Win11ValidatedConnectionProfile.ValidatedStackSha256 &&
                    ReferenceEquals(adapter, valid)));
            Check(Win11ValidatedConnectionProfile.AllowsCustomRequest(route) == allowed,
                "Win11 ABI enabled only for explicit diagnostic or measured stack/driver tuple");
            if (!windows || build < 22000)
                Check(route == Win11ParameterRoute.UnsupportedPlatform, "Win10 never enters Win11 custom request, even with explicit opt-in");
            if (route == Win11ParameterRoute.ValidatedAutomatic)
                Check(!explicitTest && build == 26200 && ReferenceEquals(adapter, valid), "automatic route has actual recorded adapter evidence");
        }
        var tested = new BleEnvironmentFacts(true, 26200, true, true, Win11ValidatedConnectionProfile.ValidatedStackSha256, false);
        Check(Win11ValidatedConnectionProfile.Select(tested, valid with { Vid = "8087", DriverProvider = "intel corporation" }, false)
            == Win11ParameterRoute.ValidatedAutomatic, "provider case does not change actual driver identity");
        Check(Win11ValidatedConnectionProfile.Select(tested with { StackSha256 = "unknown" }, valid, false)
            == Win11ParameterRoute.UnvalidatedEnvironment, "Windows stack update requires new evidence before automatic native call");
        var source = new Pro2BleInputSource();
        Check((string)typeof(Pro2BleInputSource).GetField("win11ParameterRouteStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source)! == "not_evaluated", "source constructor alone does not select/call any profile");
    }

    private static void TestBackupReader()
    {
        Type configurator = typeof(Win10BleStackProfile).Assembly.GetType("Y700Switch2V60Viiper.BleTurboConfigurator")!;
        MethodInfo reader = configurator.GetMethod("ReadState", BindingFlags.Static | BindingFlags.NonPublic)!;
        string fixture = Path.Combine(output, "stack-backup-fixture.json");
        var names = Win10BleStackProfile.Values.Keys.ToArray();
        var values = names.ToDictionary(name => name, _ => new { present = false, value = (long?)null });
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            capturedAt = "test", adapter = new { name = "test", pnpDeviceId = "USB\\TEST", driverProvider = "test",
                driverVersion = "test", driverDate = "test" }, registryPath = "HKEY_LOCAL_MACHINE\\" + Win10BleStackProfile.RegistrySubKey,
            values, backupReg = "test.reg"
        });
        File.WriteAllText(fixture, json);
        object state = reader.Invoke(null, new object?[] { fixture, names })!;
        var parsed = (System.Collections.IDictionary)state.GetType().GetProperty("Values")!.GetValue(state)!;
        Check(parsed.Count == 4, "restore reader keeps exactly four allowed values");
        foreach (string name in names)
        {
            object value = parsed[name]!;
            Check((bool)value.GetType().GetProperty("Present")!.GetValue(value)! == false,
                "absent baseline retained for " + name);
        }
        values.Remove(names[0]);
        File.WriteAllText(fixture, System.Text.Json.JsonSerializer.Serialize(new
        {
            capturedAt = "test", adapter = new { name = "test", pnpDeviceId = "USB\\TEST", driverProvider = "test",
                driverVersion = "test", driverDate = "test" }, registryPath = "HKEY_LOCAL_MACHINE\\" + Win10BleStackProfile.RegistrySubKey,
            values, backupReg = "test.reg"
        }));
        bool rejected = false;
        try { reader.Invoke(null, new object?[] { fixture, names }); }
        catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidDataException; }
        Check(rejected, "incomplete backup rejected rather than deleting unknown original value");
    }

    private static void TestCompatibilityMatrix()
    {
        foreach (bool windows in new[] { false, true })
        foreach (int build in new[] { 7601, 10240, 14393, 17763, 18362, 19041, 19042, 19043,
                     19044, 19045, 22000, 22621, 22631, 26000, 26100, 30000 })
        foreach (bool requestApi in new[] { false, true })
        foreach (bool readApi in new[] { false, true })
        foreach (bool configured in new[] { false, true })
        foreach (string hash in new[] { Win10BleStackProfile.AuditedStackSha256, "new_patch_unknown_hash" })
        {
            var facts = new BleEnvironmentFacts(windows, build, requestApi, readApi, hash, configured);
            var plan = BleCompatibilityPolicy.Select(facts);
            bool supported = windows && build >= 19041;
            Check(plan.CanRequestPreferredParameters == (supported && build >= 22000 && requestApi),
                "request API gated by actual capability and platform");
            Check(plan.CanReadConnectionParameters == (supported && build >= 22000 && readApi),
                "read API capability independent of request capability");
            Check(plan.CanConfigureWin10Stack == (supported && build < 22000),
                "all supported Win10 patches may explicitly experiment; Win11 never writes these defaults");
            Check(plan.Route != BleCompatibilityRoute.Windows11PublicApi || !plan.CanConfigureWin10Stack,
                "public route and internal route cannot be mixed");
            Check(!string.IsNullOrWhiteSpace(plan.Guidance), "every failure route has user guidance");
        }
        var current = BleCompatibilityInspector.Current;
        Check(ReferenceEquals(current, BleCompatibilityInspector.Current), "environment inspection cached, not on Notify path");
        File.WriteAllText(Path.Combine(output, "compatibility-current.txt"), current.Telemetry + "\n" + current.Plan.Guidance);
        var win10Configured = BleCompatibilityPolicy.Select(new(true, 19045, false, false, Win10BleStackProfile.AuditedStackSha256, true));
        Check(win10Configured.Guidance.Contains("不重复"), "known configured systems do not repeatedly restart Bluetooth");
        var unknown = BleCompatibilityPolicy.Select(new(true, 19045, false, false, "unknown", true));
        Check(unknown.CanConfigureWin10Stack && unknown.Route == BleCompatibilityRoute.Win10ExperimentalStack,
            "existing values do not certify a new patch, but unknown hash cannot block a reversible opt-in experiment");
    }

    private static void TestWindowsPreferencePolicy()
    {
        Check(WindowsBlePreferencePolicy.NativeObservationMilliseconds(true, true) == 10000,
            "Win11 untouched native baseline gets ten seconds, not a previously forced preset");
        Check(WindowsBlePreferencePolicy.NativeObservationMilliseconds(false, true) == 1800 &&
            WindowsBlePreferencePolicy.NativeObservationMilliseconds(true, false) == 1800,
            "Win10 and historical non-pure startup timing unchanged");
        foreach (string phase in new[] { "initial", "fd2_report_rate", "fallback" })
        foreach (bool pure in new[] { false, true })
        foreach (uint current in new uint[] { 0, 6, 12, 36 })
        foreach (uint min in new uint[] { 6, 12 })
        foreach (uint max in new uint[] { 12, 36 }.Append(min).Distinct().Where(v => v >= min))
        foreach (uint latency in new uint[] { 0, 1 })
        {
            var decision = WindowsBlePreferencePolicy.Decide(pure, phase, current, min, max, latency);
            if (pure && phase != "fallback")
                Check(decision == WindowsBlePreferenceDecision.DeferUntilMeasured,
                    "pure133 initial/descriptor phase never alters native link before measuring");
            else if (current != 0 && max > current)
                Check(decision == WindowsBlePreferenceDecision.SkipSlowerThanLive,
                    "no preset can replace an already shorter interval");
            else if (!pure)
                Check(decision == WindowsBlePreferenceDecision.Request, "historical non-pure request preserved");
            else if (latency != 0)
                Check(decision == WindowsBlePreferenceDecision.SkipIncompatiblePreset,
                    "pure133 rejects slave-latency preset");
            else if (current != 0 && max == current)
                Check(decision == WindowsBlePreferenceDecision.SkipNoMeasuredImprovement,
                    "do not churn an identical interval request");
            else if (min == 6 && max == 6)
                Check(decision == WindowsBlePreferenceDecision.Request, "fixed7.5ms measured fallback eligible");
            else if (current > max)
                Check(decision == WindowsBlePreferenceDecision.RequestPartialImprovement,
                    "45ms to15ms must remain available without being called133Hz");
            else
                Check(decision == WindowsBlePreferenceDecision.SkipNoMeasuredImprovement,
                    "no unknown-link downgrade without a native measurement");
        }
        Check(WindowsBlePreferencePolicy.Decide(true, "fallback", 12, 12, 12, 0) ==
            WindowsBlePreferenceDecision.SkipNoMeasuredImprovement, "do not re-request existing15ms link");
        Check(WindowsBlePreferencePolicy.Decide(true, "fallback", 36, 12, 12, 0) ==
            WindowsBlePreferenceDecision.RequestPartialImprovement, "actual v1.3.1 45ms22Hz regression must fall back");
        Check(WindowsBlePreferencePolicy.Decide(true, "fallback", 6, 12, 12, 0) ==
            WindowsBlePreferenceDecision.SkipSlowerThanLive, "never turn a 7.5ms Win11 link into 15ms");
        Check(WindowsBlePreferencePolicy.Decide(true, "fallback", 36, 6, 6, 0) ==
            WindowsBlePreferenceDecision.Request, "future fixed7.5ms preset can be requested but still needs Notify acceptance");
        Check(WindowsBlePreferencePolicy.Decide(true, "fallback", 0, 12, 12, 0, 22) ==
            WindowsBlePreferenceDecision.RequestPartialImprovement, "read API unavailable but native22Hz measured");
        Check(WindowsBlePreferencePolicy.Decide(true, "fallback", 0, 12, 12, 0, 100) ==
            WindowsBlePreferenceDecision.SkipNoMeasuredImprovement, "never impose66Hz preset on native100Hz");
        Check(WindowsBlePreferencePolicy.Decide(true, "fallback", 0, 12, 12, 0, double.NaN) ==
            WindowsBlePreferenceDecision.SkipNoMeasuredImprovement, "unknown native evidence cannot justify15ms request");
        Check(WindowsBlePreferencePolicy.Decide(true, "fallback", 36, 12, 6, 0) ==
            WindowsBlePreferenceDecision.SkipIncompatiblePreset, "reject reversed preset range");
    }

    private static void TestExactRestoreOnDisposableKey()
    {
        // Test the actual restore implementation on our own volatile HKCU key,
        // never on HKLM/BTHPORT and never restart or touch a Bluetooth adapter.
        string path = @"Software\PRO2ControllerAssistant\CoreTest-" + Guid.NewGuid().ToString("N");
        Type type = typeof(Win10BleStackProfile).Assembly.GetType("Y700Switch2V60Viiper.BleTurboConfigurator")!;
        MethodInfo snapshot = type.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo restore = type.GetMethod("RestoreSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
        string[] names = Win10BleStackProfile.Values.Keys.ToArray();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path, true, Microsoft.Win32.RegistryOptions.Volatile);
            key.SetValue(names[0], 24, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("unrelated", "leave this alone", Microsoft.Win32.RegistryValueKind.String);
            object original = snapshot.Invoke(null, new object?[] { key, names })!;
            foreach ((string name, int value) in Win10BleStackProfile.Values)
                key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
            restore.Invoke(null, new object?[] { key, original, names });
            Check((int)key.GetValue(names[0])! == 24, "restore exact original DWORD");
            foreach (string name in names.Skip(1))
                Check(key.GetValue(name) == null, "restore deletes only originally absent " + name);
            Check((string)key.GetValue("unrelated")! == "leave this alone", "restore preserves unrelated registry values");
            key.SetValue(names[0], "not DWORD", Microsoft.Win32.RegistryValueKind.String);
            bool rejected = false;
            try { snapshot.Invoke(null, new object?[] { key, names }); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidDataException; }
            Check(rejected, "non-DWORD values rejected before destructive conversion");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    private static void TestConnectionPresentation()
    {
        foreach (bool busy in new[] { false, true })
        {
            var init = Pro2ConnectionPresentation.Create(Pro2ConnectionPhase.Initializing, true, false, busy, false);
            Check(init.Badge.Contains("已连接"), "transport shown before input readiness, busy=" + busy);
            var measuring = Pro2ConnectionPresentation.Create(Pro2ConnectionPhase.Measuring, true, false, busy, false);
            Check(measuring.Badge.Contains("测速"), "measurement not hidden by global busy");
            var slow = Pro2ConnectionPresentation.Create(Pro2ConnectionPhase.RateLimited, true, false, busy, true);
            Check(slow.Badge.Contains("已连接") && slow.Badge.Contains("速率偏低"),
                "connected slow link distinguished from disconnected");
            var game = Pro2ConnectionPresentation.Create(Pro2ConnectionPhase.Ready, true, true, busy, true);
            Check(game.Badge == "游戏就绪", "busy does not mask ready input");
        }
        foreach (var phase in new[] { Pro2ConnectionPhase.Ready, Pro2ConnectionPhase.Measuring,
                     Pro2ConnectionPhase.RateLimited, Pro2ConnectionPhase.Initializing })
            Check(Pro2ConnectionPresentation.Create(phase, false, false, false, false).Badge == "连接已断开",
                "stale phase cannot invent transport connection " + phase);
        Check(!Pro2ConnectionPresentation.CanStartVirtual(true, true, false), "unusable slow BLE does not bypass output gate");
        Check(Pro2ConnectionPresentation.CanStartVirtual(true, true, false, true), "compatible real input allows virtual output without 133Hz");
        Check(!Pro2ConnectionPresentation.CanStartVirtual(true, false, false, true), "compatible rate cannot bypass disconnected source");
        Check(!Pro2ConnectionPresentation.CanStartVirtual(true, false, true), "disconnected input does not pass strict gate");
        Check(Pro2ConnectionPresentation.CanStartVirtual(true, true, true), "verified input may start virtual mode");
    }

    private static async Task TestRateLimitedRetentionAsync()
    {
        var source = new Pro2BleInputSource();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(Pro2BleInputSource).GetField(name, flags)!.SetValue(source, value);
        long last = Stopwatch.GetTimestamp();
        long first = last - (long)(60 / 16.4 * Stopwatch.Frequency);
        Field("fd2InputNotifyCount", 61u);
        Field("fd2InputParsedCount", 61u);
        Field("firstFd2InputTicks", first);
        Field("lastFd2InputTicks", last);
        bool retained = await (Task<bool>)typeof(Pro2BleInputSource)
            .GetMethod("EnsureUsableBlePerformanceAsync", flags)!
            .Invoke(source, new object[] { new DirectProgress<string>(_ => { }), CancellationToken.None })!;
        Check(retained && source.IsPerformanceDegraded && !source.IsHighRateReady,
            "reported 16.4Hz preflight keeps usable link, fails high-rate gate");
        await source.DisposeAsync();
    }

    private static void TestLiveRateTransitions()
    {
        var source = new Pro2BleInputSource();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(Pro2BleInputSource).GetField(name, flags)!.SetValue(source, value);
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
        Field("transportConnected", true);
        void Window(uint count)
        {
            long now = Stopwatch.GetTimestamp();
            Field("liveRateProbeTicks", now - 6 * Stopwatch.Frequency);
            Field("liveRateProbeRaw", 0u);
            Field("liveRateProbeParsed", 0u);
            Field("fd2InputNotifyCount", count);
            Field("fd2InputParsedCount", count);
            Field("lastParsedNotifyTicks", now);
            source.RecheckLiveRate();
        }
        Window(98);
        Check(!source.IsHighRateReady, "live 16.3Hz cannot be labeled high-rate");
        Check(!source.IsGameplayReady, "live 16.3Hz not eligible for compatible game start");
        Window(798);
        Check(source.CurrentParsedRateHz is > 130 and < 134, "source rate uses recent measured window");
        Check(source.IsHighRateReady && source.ConnectionPhase == Pro2ConnectionPhase.Ready,
            "live 133Hz recovery accepted without another connection");
        Field("transportConnected", false);
        Check(!source.IsHighRateReady, "actual transport disconnect immediately revokes high-rate readiness");
        Check(!source.IsGameplayReady, "actual transport disconnect also revokes compatible readiness");
        Field("transportConnected", true);
        Window(98);
        Check(source.CurrentParsedRateHz < 20, "lifetime 133Hz cannot conceal current downgrade");
        Check(!source.IsHighRateReady && source.ConnectionPhase == Pro2ConnectionPhase.RateLimited,
            "rate downgrade does not falsely preserve ready state");
        Check(source.IsRunning, "rate downgrade does not close live BLE source");
        var runningLow = Pro2ConnectionPresentation.Create(Pro2ConnectionPhase.RateLimited, true, true, false, true);
        Check(runningLow.Hint.Contains("按真实输入速率转发") && runningLow.Hint.Contains("不补帧"),
            "runtime downgrade explained without false shutdown or synthetic rate");
        Window(396);
        Check(source.IsGameplayReady && !source.IsHighRateReady && source.CurrentParsedRateHz is > 64 and < 67,
            "66Hz recovery permits gameplay but never labels 133Hz");
        Check(source.ConnectionPhase == Pro2ConnectionPhase.RateLimited, "compatible rate keeps truthful quality state");
        Window(798);
        Check(source.IsHighRateReady && source.IsGameplayReady, "133Hz recovery satisfies both independent gates");
        source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void TestGapWindow()
    {
        var gaps = new NotifyGapWindow();
        for (int i = 0; i < 600; i++) gaps.Record(7.5);
        for (int i = 0; i < 50; i++) gaps.Record(15);
        gaps.Record(double.NaN);
        string line = gaps.SummarizeAndReset(out var summary);
        Check(line.Contains("gap_samples=650") && line.Contains("gap_p50_ms=7.50") &&
            line.Contains("gap_p95_ms=15.00") && line.Contains("gap_near15=50"), "gap window quantiles/counters");
        Check(summary.Samples == 650 && summary.P50Ms == 7.5 && summary.P95Ms == 15 && summary.Near15 == 50,
            "structured cached gap evidence exactly matches log window");
        Check(gaps.SummarizeAndReset().Contains("gap_samples=0"), "gap window reset");
    }

    private static void TestInputUpdateRateWindow()
    {
        var window = new InputUpdateRateWindow(12000);
        Check(window.Measure(13000, 7.5) is > 133 and < 134,
            "first output window excludes pre-consumer BLE reports");
        Check(window.Measure(14000, 7.5) is > 133 and < 134, "later rate windows use deltas");
        Check(window.Measure(10, 1) == 10, "reconnect count reset does not inflate rate");
        Check(window.Measure(20, 0) == 0, "zero duration is not a fabricated rate");
        Check(window.Measure(30, double.NaN) == 0, "nonfinite duration is not a rate");
        Check(window.Measure(40, 1) == 10, "invalid duration still advances counter baseline");
    }

    private static void TestBatteryVisual()
    {
        for (byte level = 0; level <= 9; level++)
        {
            var state = GamepadState.Neutral(); state.BatteryLevel = level;
            state.BatteryVoltageMillivolts = 3520;
            var visual = BatteryVisual.Create(state, true, true, false);
            Check(visual.Segments.Count(s => s.Filled) == level && visual.Level == level + " / 9",
                "nine genuine battery segments " + level);
            Check(visual.Caption.Contains("连接时") && visual.Caption.Contains("3.52 V"), "snapshot/voltage labeled " + level);
        }
        var charged = GamepadState.Neutral(); new Pro2BatteryStatus(0x23).ApplyTo(charged);
        Check(BatteryVisual.Create(charged, true, true, true).Charge == "充电中", "fresh charge visual");
        Check(BatteryVisual.Create(charged, true, true, false).Charge == "", "stale charging not asserted");
        Check(BatteryVisual.Create(charged, false, true, true).Segments.All(s => !s.Filled), "disconnected bar empty/unknown");
        Check(BatteryVisual.Create(charged, true, false, true).Level == "未知", "unknown bar not fabricated");
    }

    private static async Task TestSignalAsync()
    {
        var signal = new InputFrameSignal();
        for (int i = 0; i < 10000; i++) signal.Notify();
        Check(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None), "pre-wait signal retained");
        Check(!await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None), "signals coalesced");
        var wait = signal.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        signal.Notify();
        Check(await wait, "waiting consumer wakes");
        using var cts = new CancellationTokenSource();
        wait = signal.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        cts.Cancel();
        bool cancelled = false;
        try { await wait; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancellation releases source wait");
        for (int i = 0; i < 2000; i++)
        {
            wait = signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            await Task.Run(signal.Notify);
            Check(await wait, "cross-thread wake " + i);
        }
        Check(!await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None),
            "idle timeout produces no input");
    }

    private static byte[] Pcm(int frames, double phase, bool left = true,
        bool right = true, bool speakers = false)
    {
        var bytes = new byte[frames * 8];
        for (int i = 0; i < frames; i++)
        {
            short value = (short)(12000 * Math.Sin(phase + i * 2 * Math.PI * 187.5 / 48000));
            if (left) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 8 + (speakers ? 0 : 4)), value);
            if (right) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 8 + (speakers ? 2 : 6)), value);
        }
        return bytes;
    }

    private static byte[] Wire(byte kind, byte[] payload)
    {
        var wire = new byte[388];
        wire[0] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(wire, 4);
        return wire;
    }

    private static void Warm(DualSenseHapticAudioProcessor processor)
    {
        bool active = false;
        for (int i = 0; i < 50; i++)
            if (processor.TryProcess(Pcm(48, i * 48 * 2 * Math.PI * 187.5 / 48000),
                    out var packet, out _)) active |= packet.Active;
        Check(active, "synthetic actuator audio generates haptic output");
    }

    private static async Task TestHapticsAsync()
    {
        var used = new DualSenseHapticAudioProcessor();
        Warm(used);
        used.ResetOutputState();
        var fresh = new DualSenseHapticAudioProcessor();
        for (int i = 0; i < 60; i++)
        {
            var pcm = Pcm(48, i * 48 * 2 * Math.PI * 187.5 / 48000, left: false);
            bool a = used.TryProcess(pcm, out var p, out _);
            bool b = fresh.TryProcess(pcm, out var q, out _);
            Check(a == b && (!a || p.Report.SequenceEqual(q.Report)), "full DSP reset " + i);
        }
        var speaker = new DualSenseHapticAudioProcessor();
        bool emitted = false;
        for (int i = 0; i < 100; i++)
            emitted |= speaker.TryProcess(Pcm(48, i, speakers: true), out _, out _);
        Check(!emitted, "speaker audio is never promoted to haptics");
        Check(!speaker.TryProcess(new byte[9], out _, out _), "misaligned PCM rejected");
        foreach (int chunk in new[] { 24, 48 })
        {
            var processor = new DualSenseHapticAudioProcessor();
            Warm(processor);
            int silentFrames = 0;
            bool stopped = false;
            for (int i = 0; i < 200 && !stopped; i++)
            {
                silentFrames += chunk;
                if (processor.TryProcess(new byte[chunk * 8], out var packet, out _) &&
                    packet.Source == "dualsense-hd-silence")
                    stopped = !packet.Active;
            }
            Check(stopped && silentFrames == 3840, "80 ms silence independent of chunk " + chunk);
        }
        var scheduler = new DualSenseHapticRumbleScheduler();
        Check(!scheduler.TryProcess(new byte[387], out _, out _, out _), "malformed wire rejected");
        for (int i = 0; i < 50; i++)
            scheduler.TryProcess(Wire(2, Pcm(48, i * 48 * 2 * Math.PI * 187.5 / 48000)),
                out _, out _, out _);
        Check(scheduler.HasNativeAudio && !scheduler.CompatibilityMode, "native PCM classified directly");
        await Task.Delay(150);
        Check(scheduler.TryStopIdle(out var stop) && !stop.Active, "missing audio triggers idle stop");
        Check(!scheduler.TryStopIdle(out _), "idle stop emitted once");
        var hid = new byte[48]; hid[0] = 2; hid[1] = 3; hid[3] = 100; hid[4] = 150;
        Check(scheduler.TryProcess(Wire(1, hid), out var ordinary, out _, out _) &&
              scheduler.CompatibilityMode && ordinary.Active, "ordinary rumble classified");
        await Task.Delay(150);
        Check(!scheduler.TryStopIdle(out _), "ordinary held rumble not stopped by PCM watchdog");
        hid[1] = 0;
        scheduler.TryProcess(Wire(1, hid), out _, out _, out _);
        Check(!scheduler.TryProcess(Wire(2, Pcm(48, 0)), out _, out _, out _),
            "mode switch clears old spectral window");
    }

    private static async Task TestLogAsync()
    {
        string directory = Path.Combine(output, "logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string old = Path.Combine(directory, "manager_previous.log");
        File.WriteAllText(old, "preserve me");
        var logger = new SessionLogWriter(directory);
        for (int i = 0; i < 100; i++) logger.Write("line " + i + "\n");
        await logger.DisposeAsync();
        Check(File.ReadAllText(old) == "preserve me", "previous logs preserved");
        Check(File.ReadAllText(logger.FilePath).Contains("line 99"), "dispose flushes pending logs");
        Check(logger.LastError == null, "background writer has no error");
        var stress = new SessionLogWriter(directory, 16);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 10000; i++) stress.Write(new string('x', 1024) + "\n");
        })));
        await stress.DisposeAsync();
        Check(stress.DroppedCount > 0, "log overload bounded and accounted");
        Check(new FileInfo(stress.FilePath).Length <= SessionLogWriter.MaxLogBytes + 1024,
            "persistent log bounded");
        Check(stress.LastError == null, "concurrent producers do not fail writer");
        await LongSessionRegressionTests.RunLogAsync(directory, Check);
    }

    private static void TestUi()
    {
        Environment.SetEnvironmentVariable("V60_UI_SMOKE", "1");
        using var bindingText = new StringWriter();
        var listener = new TextWriterTraceListener(bindingText);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow { ShowActivated = false };
        window.Show();
        Pump(window);
        var vm = (MainViewModel)window.DataContext;
        BleOptimizationUiRegressionTests.RunBindings(window, vm, Check);
        BleGeneralizedUiRegressionTests.RunBindings(window, vm, Check);
        Win10HomeUiRegressionTests.RunBindings(window, vm, Check, () => Pump(window));
        if (vm.ShowWin10BleHomeAction)
        {
            vm.Win10BleHomePanelExpanded = true; Pump(window); Pump(window);
            Save(window, "ui-home-win10-optimization-open.png");
            Check(Descendants<ScrollViewer>(window).Where(s => s.IsVisible).All(s =>
                s.ComputedHorizontalScrollBarVisibility != Visibility.Visible),
                "Win10 home optimization options fit horizontally at default window size");
            Check(Descendants<ScrollViewer>(window).Where(s => s.IsVisible).All(s =>
                s.ComputedVerticalScrollBarVisibility != Visibility.Visible),
                "ordinary Win10 home with optimization options open fits without default vertical scroll: " +
                string.Join("; ", Descendants<ScrollViewer>(window).Where(s => s.IsVisible).Select(s =>
                    $"extent={s.ExtentHeight:F1}, viewport={s.ViewportHeight:F1}, scroll={s.ComputedVerticalScrollBarVisibility}")));
            vm.Win10BleHomePanelExpanded = false; Pump(window);
            Save(window, "ui-home-win10-optimization-closed.png");
        }
        Check(vm.IsPro2Selected && !vm.Running, "new settings default Steam/Pro2, no hardware start");
        Check(vm.CompactSystemText.EndsWith("v2.0.0") &&
            (vm.CompactSystemText.Contains("Windows 10") || vm.CompactSystemText.Contains("Windows 11")), "UI system family/version follows runtime metadata");
        Check(vm.CompactCanConnect && vm.CompactConnectText == "连接 Pro2", "offline connection button remains usable");
        var settingsTabs = Descendants<TabControl>(window).First();
        settingsTabs.SelectedIndex = 3; Pump(window);
        BluetoothRestartRegressionTests.RunUi(window, vm, Check, () => Pump(window), (dialog, name) => Save(dialog, name));
        var selectUsbip = (Button)window.FindName("SelectUsbipPathButton");
        var resetUsbip = (Button)window.FindName("ResetUsbipPathButton");
        Check(selectUsbip.IsVisible && selectUsbip.IsEnabled && resetUsbip.Command == vm.ResetUsbipPathCommand,
            "USBIP file selection and automatic reset exposed in settings while offline");
        string usbipFixture = Path.Combine(Path.GetFullPath(output), "usbip custom 中文, folder", "usbip.exe");
        AwaitUi(vm.SelectUsbipPathAsync(usbipFixture), window);
        var usbipSettings = (V60UserSettings)typeof(MainViewModel).GetField("userSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Check(usbipSettings.UsbipExecutablePath == usbipFixture && vm.UsbipLocationText.Contains(usbipFixture) &&
            vm.UsbipLocationText.Contains("手动指定"), "selected file feeds actual VM lookup/display without modifying user settings in preview");
        Check(vm.LogText.Contains("executable_found_probe_failed") && !vm.LogText.Contains("installer="),
            "invalid executable fixture is not ready and selecting it never launches installer");
        AwaitUi(vm.SelectUsbipPathAsync(Path.Combine(output, "missing", "usbip.exe")), window);
        Check(usbipSettings.UsbipExecutablePath == usbipFixture && vm.Status.Contains("路径未保存"),
            "bad selection leaves last chosen runtime intact");
        vm.Running = true; Pump(window);
        Check(!selectUsbip.IsEnabled && !resetUsbip.IsEnabled, "changing runtime path blocked while virtual output runs");
        vm.Running = false; Pump(window);
        Save(window, "ui-usbip-custom-path.png");
        window.Dispatcher.Invoke(() => vm.ResetUsbipPathCommand.Execute(null));
        while (vm.Busy) { Pump(window); Thread.Sleep(10); }
        Check(usbipSettings.UsbipExecutablePath == "" && !vm.Busy && !vm.Running,
            "automatic lookup clears only explicit override and starts no controller");
        Save(window, "ui-usbip-auto-path.png");
        settingsTabs.SelectedIndex = 0; Pump(window);
        typeof(MainViewModel).GetMethod("ApplyEnvironmentSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [EnvironmentDiagnosticsRegressionTests.Fixture()]);
        var trialFixture = new BleAdaptiveMeasurement(new(10.01, 167, 167, 25, true, true, 60.56, 48), 0, 119.93);
        var trialResult = new BleAdaptiveRunResult(null, trialFixture, trialFixture, true,
            "final_low_rate_verified", new[] { new BleAdaptiveTrialRecord("win11_fixed_7p5", true, trialFixture, "no_meaningful_improvement") });
        var trialSnapshot = BleAdaptiveDiagnosticSnapshot.FromRun(trialResult, new[] {
            new BleAdaptiveRequestEvidence("win11_fixed_7p5", "apply", 6, 6, 0, 600, "Success", true,
                ApiCalled: true, ParameterSource: "own_abi") });
        typeof(Pro2BleInputSource).GetField("adaptiveDiagnosticSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm.Pro2Slots[0].InputSource, trialSnapshot);
        var infoButton = (Button)window.FindName("FooterEnvironmentInfoButton");
        infoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
        var infoWindow = window.OwnedWindows.OfType<EnvironmentInfoWindow>().Single();
        var infoBox = (System.Windows.Controls.TextBox)infoWindow.FindName("EnvironmentReportBox");
        Check(infoBox.IsReadOnly && infoBox.Text.Contains("22.250.0.2") && infoBox.Text.Contains("Windows 11") &&
            infoBox.Text.Contains("游戏输出：未启动"), "real information window renders driver and output independently");
        Check(infoBox.Text.Contains("API Status=Success") && infoBox.Text.Contains("实际间隔=60.00ms") &&
            infoBox.Text.Contains("win11_fixed_15") && infoBox.Text.Contains("没有本组请求或实测记录"),
            "real copyable information window renders exact request evidence and all preset outcomes");
        Check(!vm.Busy && !vm.Running && vm.Pro2Slots.All(s => !s.InputSource.IsRunning),
            "opening environment window does not run a connection or mode operation");
        infoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
        Check(window.OwnedWindows.OfType<EnvironmentInfoWindow>().Count() == 1, "info window single instance");
        Save(infoWindow, "ui-system-bluetooth.png");
        int presetSection = infoBox.Text.IndexOf("蓝牙预设测试记录", StringComparison.Ordinal);
        infoBox.ScrollToLine(infoBox.GetLineIndexFromCharacterIndex(presetSection)); Pump(infoWindow);
        Save(infoWindow, "ui-ble-presets-diagnostics.png");
        infoWindow.Width = 440; infoWindow.Height = 340; Pump(infoWindow);
        Check(infoBox.ActualHeight > 0 && Descendants<ScrollViewer>(infoWindow)
            .All(s => s.ComputedHorizontalScrollBarVisibility != Visibility.Visible),
            "info window has usable bounded scrolling at minimum dimensions");
        infoWindow.Close(); Pump(window);
        AwaitUi(vm.ExportEnvironmentInfoAsync(Path.Combine(output, "environment-export-fixture.txt")), window);
        Check(File.ReadAllText(Path.Combine(output, "environment-export-fixture.txt")).Contains("1.9.1051.3008"),
            "information export includes all adapters without connection or VIIPER dependency");
        Check(File.ReadAllText(Path.Combine(output, "environment-export-fixture.txt")).Contains("API Status=Success"),
            "existing save/export includes preset outcomes without requesting any new test");
        var source = vm.Pro2Slots[0].InputSource;
        var sourceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Pro2BleInputSource).GetField("transportConnected", sourceFlags)!.SetValue(source, true);
        typeof(Pro2BleInputSource).GetMethod("SetConnectionPhase", sourceFlags)!
            .Invoke(source, new object[] { Pro2ConnectionPhase.Measuring });
        Pump(window);
        Check(vm.CompactStateText == "已连接 · 正在测速", "real VM binding shows transport during verification");
        Check(!vm.CompactCanConnect && vm.CompactConnectText == "正在测速…" &&
            vm.CompactConnectionHint.Contains("协商"), "measurement UI cannot accidentally repeat the connect operation");
        typeof(Pro2BleInputSource).GetMethod("SetConnectionPhase", sourceFlags)!
            .Invoke(source, new object[] { Pro2ConnectionPhase.RateLimited });
        Pump(window);
        Check(vm.CompactStateText == "已连接 · 速率偏低", "real VM binding shows low rate without disconnected label");
        Check(vm.CompactConnectionHint.Contains("60Hz") && vm.CompactConnectionHint.Contains("恢复后自动继续"),
            "low rate UI explicitly explains lack of game output and automatic recovery");
        typeof(Pro2BleInputSource).GetField("transportConnected", sourceFlags)!.SetValue(source, false);
        typeof(Pro2BleInputSource).GetMethod("SetConnectionPhase", sourceFlags)!
            .Invoke(source, new object[] { Pro2ConnectionPhase.Offline });
        Pump(window);
        Check(vm.CompactInputRate == "—" && vm.CompactBattery == "电量未知", "unknown telemetry not fabricated");
        Check(vm.CompactFirmware == "未连接" && vm.CompactHeadset == "未连接", "offline device metadata not fabricated");
        Check(!vm.CompactFirmwareHint.Contains("1.1.5"), "no hardcoded firmware fallback");
        typeof(Pro2BleInputSource).GetField("batteryLevelReadStatus", sourceFlags)!.SetValue(source, "read_not_permitted");
        Check(vm.CompactBatteryHint.Contains("ATT 0x02") && !vm.CompactBatteryHint.Contains("等待真实电量"),
            "rejected battery read explained, not displayed as endlessly pending");
        Check(vm.CompactBattery == "电量未知", "read rejection does not invent battery percentage");
        typeof(Pro2BleInputSource).GetField("batteryLevelReadStatus", sourceFlags)!.SetValue(source, "not_attempted");
        Check(!vm.CompactCanStop, "idle stop unavailable");
        var uiBatteryState = GamepadState.Neutral();
        new Pro2BatteryStatus(0x23).ApplyTo(uiBatteryState);
        uiBatteryState.BatteryVoltageMillivolts = 4025;
        typeof(Pro2BleInputSource).GetField("latest", sourceFlags)!.SetValue(source, uiBatteryState);
        typeof(Pro2BleInputSource).GetField("batteryStatusTicks", sourceFlags)!.SetValue(source, Stopwatch.GetTimestamp());
        typeof(Pro2BleInputSource).GetField("batteryStatus", sourceFlags)!.SetValue(source, new Pro2BatteryStatus(0x23));
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
        Pro2FirmwareInfo.TryDecode(Convert.FromHexString("1001010110780000010105020C000000FFFFFFFF"), out var uiFirmware);
        typeof(Pro2BleInputSource).GetField("firmwareInfo", sourceFlags)!.SetValue(source, uiFirmware);
        typeof(Pro2BleInputSource).GetField("transportConnected", sourceFlags)!.SetValue(source, true);
        uiBatteryState.HeadsetPresenceKnown = true; uiBatteryState.HeadsetPresent = true;
        typeof(Pro2BleInputSource).GetField("latestAt", sourceFlags)!.SetValue(source, DateTimeOffset.UtcNow);
        vm.RefreshCompactTelemetry(); Pump(window);
        Check(vm.CompactFirmware == "1.1.5" && vm.CompactFirmwareHint.Contains("12.0.0") && vm.CompactHeadset == "已插入",
            "passive firmware and headset metadata reaches real WPF bindings");
        uiBatteryState.HeadsetPresent = false; vm.RefreshCompactTelemetry(); Pump(window);
        Check(vm.CompactHeadset == "未插入", "passive unplug updates UI");
        uiBatteryState.HeadsetPresent = true;
        typeof(Pro2BleInputSource).GetField("latestAt", sourceFlags)!.SetValue(source, DateTimeOffset.UtcNow.AddSeconds(-3));
        vm.RefreshCompactTelemetry(); Pump(window);
        Check(vm.CompactHeadset == "未知", "stalled input does not retain headset insertion claim");
        typeof(Pro2BleInputSource).GetField("latestAt", sourceFlags)!.SetValue(source, DateTimeOffset.UtcNow);
        vm.RefreshCompactTelemetry(); Pump(window);
        Check(vm.CompactBattery == "8/9 格（连接时） · 充电中" && vm.CompactBatteryHint.Contains("4.025 V"),
            "battery/charging/voltage reaches real WPF binding");
        Save(window, "ui-battery-charging.png");
        Check(vm.CompactBatteryVisual.Segments.Count(s => s.Filled) == 8, "live VM nine-cell battery binding");
        Check(vm.RuntimeBatteryRefreshEnabled && vm.Pro2Slots.All(s => s.InputSource.RuntimeBatteryRefreshEnabled),
            "normal release enables runtime battery updates on all slots");
        var runtimePayload = new byte[63]; runtimePayload[0] = 9; runtimePayload[1] = 0x18;
        var commitRuntime = typeof(Pro2BleInputSource).GetMethod("CommitRuntimeBattery", sourceFlags)!;
        long runtimeSequence = (long)typeof(Pro2BleInputSource).GetField("connectionSequence", sourceFlags)!.GetValue(source)!;
        Check((bool)commitRuntime.Invoke(source, [runtimePayload, runtimeSequence, Stopwatch.GetTimestamp(), new BatteryProgress()])!,
            "accepted runtime native level commits to UI source");
        vm.RefreshCompactTelemetry(); Pump(window);
        Check(vm.CompactBattery == "6/9 格" && vm.CompactBatteryVisual.Segments.Count(s => s.Filled) == 6 &&
            vm.CompactBatteryVisual.Caption.Contains("定时更新") && !vm.CompactBattery.Contains("连接时"),
            "new native level replaces connection snapshot in actual WPF bindings");
        Save(window, "ui-battery-runtime.png");
        typeof(Pro2BleInputSource).GetField("runtimeBatterySuspensionReason", sourceFlags)!.SetValue(source, "notify_gap_exceeded");
        vm.RefreshCompactTelemetry(); Pump(window);
        Check(vm.CompactBatteryVisual.Caption.Contains("更新已暂停") && vm.CompactBatteryHint.Contains("保护输入"),
            "automatic input guard is visible rather than pretending updates continue");
        typeof(Pro2BleInputSource).GetField("batteryStatusTicks", sourceFlags)!.SetValue(source,
            Stopwatch.GetTimestamp() - Stopwatch.Frequency * 721);
        vm.RefreshCompactTelemetry(); Pump(window);
        Check(vm.CompactBattery.Contains("上次读取"), "expired runtime level is explicitly historical");
        typeof(Pro2BleInputSource).GetField("runtimeBatterySuspensionReason", sourceFlags)!.SetValue(source, "");
        vm.RuntimeBatteryRefreshEnabled = false;
        Check(vm.Pro2Slots.All(s => !s.InputSource.RuntimeBatteryRefreshEnabled), "UI switch disables every slot worker");
        vm.RuntimeBatteryRefreshEnabled = true;
        var illustration = new Pro2Illustration();
        double X(string name) => Canvas.GetLeft((UIElement)illustration.FindName(name));
        double Y(string name) => Canvas.GetTop((UIElement)illustration.FindName(name));
        Check(Y("LeftStick") < Y("Dpad") && X("LeftStick") < X("Dpad"), "Pro2 left asymmetric layout");
        Check(Y("RightStick") > Y("AButton") && X("RightStick") < X("AButton"), "Pro2 right asymmetric layout");
        Check(Y("XButton") < Y("YButton") && Y("BButton") > Y("YButton") &&
            X("YButton") < X("AButton"), "Nintendo ABXY layout, not Xbox");
        TestCompatibleHome(window, vm, source);
        typeof(Pro2BleInputSource).GetMethod("ResetLiveState", sourceFlags)!.Invoke(source, null);
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, false);
        typeof(Pro2BleInputSource).GetField("transportConnected", sourceFlags)!.SetValue(source, false);
        vm.RefreshCompactTelemetry();
        Check(vm.CompactBattery == "电量未知", "offline WPF clears old battery");
        Check(source.FirmwareInfo == null && vm.CompactFirmware == "未连接" && vm.CompactHeadset == "未连接", "reconnect clears all device snapshots");
        vm.Pro2Slots[0].AutoReconnectEnabled = true;
        Check(vm.CompactCanStop, "stop available during disconnected slot search");
        vm.Pro2Slots[0].AutoReconnectEnabled = false;
        vm.SelectCompactModeCommand.Execute("Xbox");
        Pump(window);
        Check(vm.IsXboxSelected && !vm.Running, "pre-connect mode selection has no runtime side effects");
        vm.SelectCompactModeCommand.Execute("PS5");
        Pump(window);
        Check(vm.IsDualSenseSelected, "PS5 no longer activates Edge");
        vm.SelectCompactModeCommand.Execute("Pro1");
        Pump(window);
        Check(vm.IsPro1Selected && !vm.Running, "Pro1 can be selected offline");
        Check(!Descendants<CheckBox>(window).Any(c => c.Content?.ToString()?.Contains("Edge") == true),
            "removed Edge checkbox is absent from UI");
        vm.SelectCompactModeCommand.Execute("Pro2");
        var tabs = (TabControl)window.FindName("MainTabs");
        Check(tabs.Items.Count == 4 && ((TabItem)tabs.Items[2]).Header.ToString() == "背键 / C 键",
            "four functional tabs, dedicated rear-button page");
        Check(vm.RearEditors.Count == 3 && vm.RearEditors[0].Title.Contains("GL") && vm.RearEditors[2].Title == "C 键", "rear keys and C editable");
        vm.RearEditingMode = "PS5";
        Check(vm.RearEditors[0].Buttons.Any(b => b.Label == "×") &&
            vm.RearEditors[0].Buttons.Any(b => b.Label == "触摸板点击"), "PS5 target labels");
        vm.RearEditors[0].Enabled = true;
        vm.RearEditors[0].Action = "顺序按键";
        vm.RearEditors[0].Buttons.First(b => b.Key == GamepadButtons.South).Selected = true;
        vm.RearEditors[0].AddStepCommand.Execute(null);
        vm.ApplyRearButtonsCommand.Execute(null);
        Check(vm.RearSettingsStatus == "已保存并应用。", "WPF editors feed actual settings command without hardware");
        vm.RearEditingMode = "Xbox";
        Check(!vm.RearEditors[0].Enabled && vm.RearEditors[0].Buttons.Any(b => b.Label == "A"), "Xbox and PS5 profiles independent");
        vm.RearEditingMode = "Pro1";
        Check(vm.RearEditors[0].Buttons.First(b => b.Key == GamepadButtons.East).Label == "A" &&
            vm.RearEditors[0].Buttons.First(b => b.Key == GamepadButtons.South).Label == "B" &&
            vm.RearEditors[0].Buttons.Any(b => b.Label == "ZL"), "Pro1 editor uses Nintendo labels offline");
        vm.RearEditors[0].Enabled = true;
        vm.RearEditors[0].Buttons.First(b => b.Key == GamepadButtons.East).Selected = true;
        vm.RearEditors[0].Action = "按住连发";
        vm.ApplyRearButtonsCommand.Execute(null);
        Check(vm.RearSettingsStatus == "已保存并应用。", "Pro1 independent rear mapping can be saved offline");
        var cPersisted = (V60UserSettings)typeof(MainViewModel).GetField("userSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        foreach (string cMode in vm.RearModes)
        {
            vm.RearEditingMode = cMode; vm.SelectedRearIndex = 2;
            Check(vm.SelectedRearEditor.Title == "C 键" && !vm.SelectedRearEditor.Enabled, "C disabled by default and selectable offline " + cMode);
            vm.SelectedRearEditor.Enabled = true;
            vm.SelectedRearEditor.Buttons.First(b => b.Key == GamepadButtons.R1).Selected = true;
            vm.SelectedRearEditor.Action = "按住连发"; vm.SelectedRearEditor.Delay = "35";
            vm.ApplyRearButtonsCommand.Execute(null);
            Check(vm.RearSettingsStatus == "已保存并应用。", "C draft save without physical controller " + cMode);
            var cProfile = cMode switch { "PS5" => cPersisted.RearButtons.Ps5, "Pro1" => cPersisted.RearButtons.Pro1, _ => cPersisted.RearButtons.Xbox };
            Check(cProfile.C.Enabled && cProfile.C.Action == RearButtonAction.Repeat && cProfile.C.DelayMs == 35 && cProfile.C.Buttons == GamepadButtons.R1,
                "C UI writes real independent mode configuration " + cMode);
        }
        vm.RearEditingMode = "Pro1"; vm.SelectedRearIndex = 2;
        tabs.SelectedIndex = 2; Pump(window); Save(window, "ui-c-key-pro1.png");
        vm.SelectedRearIndex = 0;
        tabs.SelectedIndex = 2;
        Pump(window);
        var rearList = (ItemsControl)window.FindName("RearKeyList");
        Check(vm.SelectedRearIndex == 0 && vm.RearEditors[0].IsSelected && !vm.RearEditors[1].IsSelected,
            "GL selector visibly selected and matches editor after mode change");
        var rearSelectors = Descendants<Button>(rearList).ToArray();
        Check(rearSelectors.Length == 3, "GL/GR/C all rendered as real UI selectors");
        rearSelectors[2].Command.Execute(rearSelectors[2].CommandParameter); Pump(window);
        Check(vm.SelectedRearIndex == 2 && vm.SelectedRearEditor.Title == "C 键" && vm.RearEditors[2].IsSelected,
            "real C selector opens only the C editor");
        Save(window, "ui-c-selector.png");
        rearSelectors[1].Command.Execute(rearSelectors[1].CommandParameter); Pump(window);
        Check(vm.SelectedRearIndex == 1 && vm.SelectedRearEditor.Title.Contains("GR"), "GR selection opens only right rear editor");
        vm.RearEditingMode = "PS5"; Pump(window);
        Check(vm.SelectedRearIndex == 1 && vm.SelectedRearEditor.Ps5 && vm.SelectedRearEditor.IsSelected,
            "mode switch preserves selected physical rear key");
        vm.RearEditingMode = "Xbox"; vm.SelectedRearIndex = 0;
        Pump(window);
        var rearScroll = (ScrollViewer)window.FindName("RearEditorScroll");
        var choices = Descendants<CheckBox>(rearScroll).Where(c => c.DataContext is RearButtonChoice).ToArray();
        Check(choices.Length == 17 && choices.All(c => c.IsEnabled), "all target keys editable offline even when mapping disabled");
        var aChoice = choices.First(c => ((RearButtonChoice)c.DataContext).Key == GamepadButtons.South);
        aChoice.IsChecked = true; Pump(window);
        Check(vm.SelectedRearEditor.Buttons.First(c => c.Key == GamepadButtons.South).Selected &&
            vm.SelectedRearEditor.MappingSummary == "A", "actual UI selection updates disabled/offline profile");
        vm.ApplyRearButtonsCommand.Execute(null);
        Check(vm.RearSettingsStatus == "已保存并应用。" && !vm.Running && !source.IsRunning,
            "save offline profile neither connects controller nor creates output");
        Check(vm.RearDraftStatus == "已保存", "current save clears only current profile dirty marker");
        vm.SelectedRearEditor.Delay = "25";
        Check(vm.RearDraftStatus.StartsWith("未保存"), "timing change marks current profile unsaved");
        vm.RearEditingMode = "PS5"; vm.SelectedRearIndex = 0;
        vm.SelectedRearEditor.Delay = "bad";
        Check(vm.RearDraftStatus.StartsWith("未保存"), "sequence profile draft independent");
        vm.RearEditingMode = "Xbox";
        Check(vm.SelectedRearEditor.Delay == "25" && vm.RearDraftStatus.StartsWith("未保存"), "profile switch retains numeric draft without applying it");
        var settingsField = typeof(MainViewModel).GetField("userSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var persisted = (V60UserSettings)settingsField.GetValue(vm)!;
        var beforePs5 = persisted.RearButtons.Ps5;
        vm.ApplyRearButtonsCommand.Execute(null);
        Check(vm.RearSettingsStatus == "已保存并应用。" && persisted.RearButtons.Xbox.Left.DelayMs == 25 &&
            RearButtonMapper.ProfilesEquivalent(beforePs5, persisted.RearButtons.Ps5), "invalid unrelated draft cannot block current save or replace another live profile");
        var busyProperty = typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!;
        busyProperty.SetValue(vm, true); Pump(window);
        var saveButton = Descendants<Button>(window).First(button => ReferenceEquals(button.Command, vm.ApplyRearButtonsCommand));
        Check(!saveButton.IsEnabled && !vm.ApplyRearButtonsCommand.CanExecute(null), "save disabled during connection/mode-switch transaction");
        vm.ApplyRearButtonsCommand.Execute(null);
        Check(persisted.RearButtons.Xbox.Left.DelayMs == 25, "busy command invocation cannot overwrite a session's configuration");
        busyProperty.SetValue(vm, false); Pump(window);
        Check(saveButton.IsEnabled && vm.ApplyRearButtonsCommand.CanExecute(null), "save availability refreshes after transition without reopening page");
        vm.RearEditingMode = "PS5";
        Check(vm.SelectedRearEditor.Delay == "bad" && vm.RearDraftStatus.StartsWith("未保存"), "saving other profile does not discard unsaved invalid draft");
        vm.SelectedRearEditor.Delay = "0"; vm.ApplyRearButtonsCommand.Execute(null);
        vm.CancelRearButtonsCommand.Execute(null);
        Check(vm.RearSettingsStatus.Contains("停止背键") && !vm.Running && !source.IsRunning, "offline emergency button never starts a controller session");
        vm.RearEditingMode = "Xbox";
        vm.ResetRearButtonsCommand.Execute(null);
        Check(vm.RearDraftStatus.StartsWith("未保存") && persisted.RearButtons.Xbox.Left.DelayMs == 25,
            "reset creates a visible draft, no silent live apply");
        vm.ApplyRearButtonsCommand.Execute(null);
        window.Width = 1040; window.Height = 820;
        vm.SelectCompactModeCommand.Execute("Pro1");
        tabs.SelectedIndex = 0; Pump(window); Save(window, "ui-pro1-controller.png");
        vm.RearEditingMode = "Pro1";
        tabs.SelectedIndex = 2; Pump(window); Save(window, "ui-pro1-rear.png");
        vm.SelectCompactModeCommand.Execute("Pro2");
        vm.RearEditingMode = "Xbox";
        for (int tab = 0; tab < tabs.Items.Count; tab++)
        {
            tabs.SelectedIndex = tab; vm.ShowDiagnostics = false; Pump(window);
            Save(window, "ui-default-tab-" + tab + ".png");
            var scrolls = Descendants<ScrollViewer>(tabs).Where(s => s.IsVisible).ToArray();
            Check(scrolls.All(s => s.ComputedHorizontalScrollBarVisibility != Visibility.Visible), "no default horizontal scroll " + tab);
            Check(scrolls.All(s => s.ComputedVerticalScrollBarVisibility != Visibility.Visible), "no default vertical scroll " + tab);
        }
        for (int size = 0; size < 2; size++)
        {
            window.Width = size == 0 ? 820 : 720;
            window.Height = size == 0 ? 700 : 560;
            for (int tab = 0; tab < tabs.Items.Count; tab++)
            {
                tabs.SelectedIndex = tab;
                vm.ShowDiagnostics = tab == 3;
                Pump(window);
                Save(window, "ui-" + size + "-tab-" + tab + ".png");
                Check(window.ActualWidth > 0 && window.ActualHeight > 0, "WPF layout " + size + "/" + tab);
            }
        }
        tabs.SelectedIndex = 2; vm.RearEditingMode = "PS5"; window.Width = 720; window.Height = 560;
        Pump(window); Save(window, "ui-rear-sequence.png");
        var selectorScroll = (ScrollViewer)window.FindName("RearSelectorsScroll");
        Check(selectorScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
            "small window exposes scrolling instead of clipping C selector");
        selectorScroll.ScrollToEnd(); Pump(window);
        var cSelector = Descendants<Button>(selectorScroll).Single(b => b.DataContext is RearButtonEditor editor && editor.Title == "C 键");
        var cBounds = cSelector.TransformToAncestor(selectorScroll).TransformBounds(new Rect(cSelector.RenderSize));
        Check(cBounds.Top >= 0 && cBounds.Bottom <= selectorScroll.ActualHeight + 1,
            "C selector reachable at minimum window dimensions");
        cSelector.Command.Execute(cSelector.CommandParameter); Pump(window); Save(window, "ui-min-c-key.png");
        Check(vm.SelectedRearIndex == 2 && vm.SelectedRearEditor.Title == "C 键", "C editor works at minimum dimensions");
        TestManualDialogUi(window);
        LongSessionRegressionTests.RunUi(window, vm, Check, () => Pump(window));
        listener.Flush();
        File.WriteAllText(Path.Combine(output, "binding-trace.txt"), bindingText.ToString());
        Check(string.IsNullOrWhiteSpace(bindingText.ToString()), "zero binding errors/warnings");
        window.Close();
        Pump(window);
        app.Shutdown();
        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
    }

    private static void TestCompatibleHome(Window window, MainViewModel vm, Pro2BleInputSource source)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Source(string name, object value) => typeof(Pro2BleInputSource).GetField(name, flags)!.SetValue(source, value);
        void Vm(string name, object value) => typeof(MainViewModel).GetField(name, flags)!.SetValue(vm, value);
        typeof(MainViewModel).GetMethod("ApplyEnvironmentSnapshot", flags)!.Invoke(vm,
            [Environment.OSVersion.Version.Build < 22000 ? CompatibleRateRegressionTests.Win10Fixture(false) :
                EnvironmentDiagnosticsRegressionTests.Fixture()]);
        void Rate(double hz, bool accepted)
        {
            long now = Stopwatch.GetTimestamp();
            Source("transportConnected", true);
            Source("connectionPhase", accepted ? Pro2ConnectionPhase.Ready : Pro2ConnectionPhase.RateLimited);
            Source("connectionIntervalUnits", accepted ? (ushort)6 : (ushort)12);
            Source("fd2ReportRateStatus", "write_success"); Source("connectionPreferenceStatus", "Success");
            Source("win11ParameterRouteStatus", "ValidatedEnvironment");
            Source("liveRateMeasuredTicks", now); Source("liveRateProbeTicks", now);
            Source("liveFd2RateHz", hz); Source("liveParsedRateHz", hz); Source("rateAccepted", accepted);
            Source("lastParsedNotifyTicks", now); Source("lastRawNotifyTicks", now);
            Source("fd2InputNotifyCount", (uint)(hz * 10)); Source("fd2InputParsedCount", (uint)(hz * 10));
            Source("updates", (uint)(hz * 10)); Source("rawNotifyCount", (uint)(hz * 10));
            Source("lastLiveGapSummary", new NotifyGapSummary((int)(hz * 5), accepted ? 7.5 : 15,
                accepted ? 8 : 15.7, accepted ? 8.3 : 30, .2, accepted ? 15 : 31, accepted ? 650 : 0, accepted ? 2 : 320, 10));
            var health = (BleRateStabilityMonitor)typeof(Pro2BleInputSource).GetField("rateStability", flags)!.GetValue(source)!;
            health.Reset();
            int windowCount = accepted ? 3 : 2;
            long windowStart = now - windowCount * 5 * Stopwatch.Frequency;
            for (int i = 0; i < windowCount; i++)
            {
                long end = windowStart + 5 * Stopwatch.Frequency;
                health.ObserveWindow(new(windowStart, end, (ulong)(hz * 5), (ulong)(hz * 5)));
                windowStart = end;
            }
            Vm("compactSampleTicks", now - 10 * Stopwatch.Frequency);
            Vm("compactRaw", 0u); Vm("compactFd2", 0u); Vm("compactParsed", 0u);
            vm.RefreshCompactTelemetry(); Pump(window);
        }
        window.Width = 1040; window.Height = 820;
        var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedIndex = 0;
        var notice = (Border)window.FindName("LowRateNotice");
        Rate(66.4, false);
        Check(vm.CompactCanConnect && vm.CompactConnectText == "进入游戏" && source.IsGameplayReady && !source.IsHighRateReady,
            "real 66Hz source binding unlocks game entry without high-rate claim");
        Check(vm.HasLowRateWarning && notice.IsVisible && vm.CompactStateText == "可用 · 建议优化",
            "home renders compatible warning and truthful 66Hz status");
        Check(vm.BleStabilitySummary.Contains("持续低速") && vm.LowRateEnvironmentSummary.Contains("Build"),
            "home shows ongoing low-rate health and cross-machine environment summary");
        Check(vm.EnvironmentReportText.Contains("低速判定") && vm.LowRateEvidence.Contains("66.4") && vm.LowRateEvidence.Contains("15.00"),
            "home and exported diagnostics share actual cached evidence");
        Check(Descendants<ScrollViewer>(tabs).Where(s => s.IsVisible).All(s =>
            s.ComputedHorizontalScrollBarVisibility != Visibility.Visible), "66Hz warning never introduces horizontal scroll");
        Save(window, "ui-home-66hz-entry.png");
        Check(Descendants<ScrollViewer>(tabs).Where(s => s.IsVisible).All(s =>
            s.ComputedVerticalScrollBarVisibility != Visibility.Visible), "default home fits 66Hz notice and modes without scrolling");
        var sessionBefore = vm.Pro2Slots[0].Session;
        vm.Running = true; Rate(66.4, false);
        Check(vm.CompactConnectionTitle.Contains("可以游玩") && vm.CompactConnectionHint.Contains("不补帧"),
            "running compatible mode explained without claiming missing game output");
        Save(window, "ui-home-66hz-running.png");
        var beforeCounters = source.GetCompactCounters();
        for (int i = 0; i < 50; i++) vm.RefreshCompactTelemetry();
        Check(source.GetCompactCounters() == beforeCounters && vm.Pro2Slots[0].Session == sessionBefore,
            "diagnostic refresh never generates reports or rebuilds virtual session");
        Rate(133.3, true);
        Check(!vm.HasLowRateWarning && !notice.IsVisible && vm.CompactStateText == "游戏就绪" && vm.Running &&
            ReferenceEquals(source, vm.Pro2Slots[0].InputSource) && vm.Pro2Slots[0].Session == sessionBefore,
            "133Hz recovery hides home warning while retaining source and session identity");
        Check(vm.BleStabilitySummary.Contains("高速稳定"), "home confirmed high-rate health comes from independent valid windows");
        Check(!vm.EnvironmentReportText.Contains("低速判定"), "recovered diagnostics no longer export obsolete warning");
        Save(window, "ui-home-133hz-running.png");
        vm.Running = false; Rate(20, false);
        Check(!vm.CompactCanConnect && vm.CompactConnectText == "等待输入恢复" && vm.HasLowRateWarning,
            "20Hz remains measured and waits instead of inventing playable 66Hz");
        Save(window, "ui-home-20hz-waiting.png");
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        // Run queued layout/render work as a real dispatcher loop would. Same-thread
        // Invoke alone can leave ScrollViewer's computed bars from a previous size.
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static void AwaitUi(Task task, Window window)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed.TotalSeconds < 5) { Pump(window); Thread.Sleep(5); }
        Check(task.IsCompleted, "UI operation finishes within deadline");
        task.GetAwaiter().GetResult(); Pump(window);
    }
    private static void TestManualDialogUi(Window owner)
    {
        var high = new Pro2ManualCandidate(0x38C6CE27FC2D, Windows.Devices.Bluetooth.BluetoothAddressType.Public,
            "Pro2 Controller", -41, true, 90);
        var unknown = new Pro2ManualCandidate(0xDEADBEEF1201, Windows.Devices.Bluetooth.BluetoothAddressType.Random,
            "Unknown peripheral", -20, false, 0);
        var finish = new TaskCompletionSource<ManualConnectionResult>(); int calls = 0;
        using var model = new ManualConnectionViewModel((_, _) => Task.FromResult<System.Collections.Generic.IReadOnlyList<Pro2ManualCandidate>>(
            new[] { unknown, high }), (_, token) => { calls++; return finish.Task.WaitAsync(token); }, CancellationToken.None);
        var dialog = new ManualConnectWindow(model) { Owner = owner, ShowActivated = false };
        dialog.Show(); Pump(dialog);
        Check(model.Candidates.Count == 2 && model.Candidates[0] == high, "real manual dialog renders sorted data");
        Save(dialog, "ui-manual-candidates.png");
        var buttons = Descendants<Button>((DependencyObject)dialog.Content).Where(b => b.Tag is Pro2ManualCandidate).ToArray();
        Check(buttons.Length == 2, "manual addresses rendered as click targets");
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(dialog);
        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(dialog);
        Check(calls == 1 && model.IsConnecting && model.Status.Contains("等待"), "real click handler guards repeated clicks");
        Save(dialog, "ui-manual-wait.png");
        finish.SetResult(new(false, "未通过 Pro2 身份验证，请尝试其他候选。"));
        AwaitUi(Task.Delay(50), dialog);
        Check(dialog.IsVisible && !dialog.Connected && !model.IsConnecting, "failed connection leaves actual window open");
        Save(dialog, "ui-manual-failure.png");
        finish = new TaskCompletionSource<ManualConnectionResult>();
        var connecting = model.ConnectAsync(high);
        finish.SetResult(new(true, "真实 FD2 输入已确认。"));
        AwaitUi(connecting, owner);
        Check(dialog.Connected && !dialog.IsVisible, "confirmed connection auto-closes actual manual window");
        AwaitUi(model.StopAsync(), owner);

        using var scanModel = new ManualConnectionViewModel(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return Array.Empty<Pro2ManualCandidate>(); },
            (_, _) => Task.FromResult(new ManualConnectionResult(false, "unused")), CancellationToken.None);
        var cancelling = new ManualConnectWindow(scanModel) { Owner = owner, ShowActivated = false };
        cancelling.Show(); Pump(cancelling); Check(scanModel.IsScanning, "actual dialog starts scan on load");
        cancelling.Close(); AwaitUi(scanModel.StopAsync(), owner);
        Check(!cancelling.IsVisible && !scanModel.IsScanning, "closing actual scan window awaits cancellation and releases watcher task");
    }

    private static void Save(Window window, string name)
    {
        var content = (FrameworkElement)window.Content;
        double width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        double height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content), null,
                new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
    }
}
