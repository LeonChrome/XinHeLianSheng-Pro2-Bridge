using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

internal static class BleDiagnosticCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "v6_logs",
            "ble_turbo");
        Directory.CreateDirectory(directory);
        string runLog = Path.Combine(
            directory,
            "ble-cli-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        string latestMarker = Path.Combine(directory, "ble-cli-latest.txt");
        var logGate = new object();
        void WriteLine(string line)
        {
            string stamped = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line;
            lock (logGate)
            {
                File.AppendAllText(runLog, stamped + Environment.NewLine, new UTF8Encoding(false));
            }
        }

        File.WriteAllText(latestMarker, runLog, new UTF8Encoding(false));
        int durationSeconds = ParseDurationSeconds(args);
        string? preferredAddress = ParseOption(args, "--preferred-address");
        bool explicitPreferredAddress = !string.IsNullOrWhiteSpace(preferredAddress);
        bool customWin11 = Win11CustomConnectionParameters.IsCliRequested(args);
#if PRO2_OUTPUT_ISOLATION_EXPERIMENT
        bool outputIsolation = Array.Exists(args, a => string.Equals(a, "--ble-output-isolation", StringComparison.OrdinalIgnoreCase));
        bool notifyLayer = Array.Exists(args, a => string.Equals(a, "--ble-notify-layer", StringComparison.OrdinalIgnoreCase));
        bool rateWatch = Array.Exists(args, a => string.Equals(a, "--ble-rate-watch", StringComparison.OrdinalIgnoreCase));
        int watchSeconds = 2700;
        bool hasWatchSeconds = args.Contains("--watch-seconds", StringComparer.OrdinalIgnoreCase);
        if (hasWatchSeconds && !rateWatch)
        {
            WriteLine("[BLE_RATE_WATCH] refused=watch_seconds_requires_watch_mode radio_opened=false");
            return 7;
        }
        if (rateWatch)
        {
            string? watchText = ParseOption(args, "--watch-seconds");
            if (customWin11 || outputIsolation || notifyLayer ||
                !args.Contains(Pro2InitialBatterySnapshot.DisableArgument, StringComparer.OrdinalIgnoreCase) ||
                (hasWatchSeconds && (watchText == null || !int.TryParse(watchText, out watchSeconds) || watchSeconds < 60 || watchSeconds > 3600)))
            {
                WriteLine("[BLE_RATE_WATCH] refused=isolated_mode_requires_battery_snapshot_off_and_60_to_3600_seconds radio_opened=false");
                return 7;
            }
        }
        bool layerSelfTest = Array.Exists(args, a => string.Equals(a, "--ble-layer-self-test", StringComparison.OrdinalIgnoreCase));
        bool schedulerOnly = Array.Exists(args, a => string.Equals(a, "--ble-scheduler-only", StringComparison.OrdinalIgnoreCase));
        if (layerSelfTest)
        {
            try { return BleNotifyLayerExperiment.SelfTest(WriteLine); }
            catch (Exception ex) { WriteLine("[BLE_NOTIFY_LAYER_SELF_TEST] FAIL " + ex); return 5; }
        }
        if (schedulerOnly)
        {
            if (customWin11 || notifyLayer || outputIsolation || rateWatch)
            {
                WriteLine("[BLE_SCHEDULER_DIAG] refused=do_not_combine_radio_and_no_radio_modes");
                return 7;
            }
            try
            {
                using var schedulerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                return await BleSchedulerLatencyProbe.RunStandaloneAsync(directory, WriteLine, schedulerTimeout.Token);
            }
            catch (OperationCanceledException) { WriteLine("[BLE_SCHEDULER_DIAG] timeout no_success_claimed=true"); return 4; }
            catch (Exception ex) { WriteLine("[BLE_SCHEDULER_DIAG] failed " + ex); return 5; }
        }
        if ((outputIsolation && customWin11) || (notifyLayer && (customWin11 || outputIsolation)))
        {
            WriteLine("[BLE_OUTPUT_ISOLATION] refused=do_not_combine_parameter_experiments");
            return 7;
        }
#else
        bool outputIsolation = false;
        bool notifyLayer = false;
        bool rateWatch = false;
        int watchSeconds = 0;
        if (Array.Exists(args, a => new[] { "--ble-output-isolation", "--ble-notify-layer", "--ble-layer-self-test", "--ble-scheduler-only", "--direct-known-target", "--ble-rate-watch", "--watch-seconds" }
            .Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            WriteLine("[BLE_DIAGNOSTIC] refused=source_only_option_not_available_in_normal_build radio_opened=false");
            return 7;
        }
#endif
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(rateWatch ? watchSeconds + 240 : outputIsolation ? 600 : notifyLayer ? 240 : durationSeconds + (customWin11 ? 100 : 45)));
        IProgress<string> progress = customWin11 ? new DirectProgress<string>(WriteLine) : new Progress<string>(WriteLine);
        var source = new Pro2BleInputSource();
        try
        {
            WriteLine("[BLE_TURBO_CLI] begin duration_s=" + durationSeconds +
                      " preferred_address=" + (preferredAddress ?? "none"));
            if (customWin11)
            {
                WriteLine("[WIN11_7P5_ABI] diagnostic_only=true virtual_device_started=false system_writes=false");
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
                    !BleCompatibilityInspector.Current.Plan.CanRequestPreferredParameters ||
                    !BleCompatibilityInspector.Current.Plan.CanReadConnectionParameters)
                {
                    WriteLine("[WIN11_7P5_ABI] unsupported_runtime=true; no BLE connection or parameter request attempted.");
                    return 7;
                }
                preferredAddress = BleDiagnosticTargetResolver.Resolve(preferredAddress,
                    V60UserSettings.Load().LastConnectedPro2Addresses);
                WriteLine("[BLE_TURBO_TARGET] address=" + (string.IsNullOrEmpty(preferredAddress) ? "none" : preferredAddress) +
                    " source=" + (explicitPreferredAddress ? "argument" : string.IsNullOrEmpty(preferredAddress) ? "new_scan" : "saved_settings") +
                    " saved_address_is_not_connection_proof=true settings_written=false");
            }
            IReadOnlySet<string>? preferred = string.IsNullOrWhiteSpace(preferredAddress)
                ? null
                : new HashSet<string>(
                    [preferredAddress],
                    StringComparer.OrdinalIgnoreCase);
#if PRO2_OUTPUT_ISOLATION_EXPERIMENT
            if (Array.Exists(args, a => string.Equals(a, "--direct-known-target", StringComparison.OrdinalIgnoreCase)))
            {
                if (!BleDiagnosticTargetResolver.TryParsePublicAddress(preferredAddress, out ulong target))
                    throw new InvalidOperationException("Direct diagnostic connect requires an explicit valid public Pro2 address.");
                WriteLine("[BLE_DIRECT_DIAGNOSTIC] direct_known_target=" + preferredAddress + " exact_uuid_gate=true retries=0");
                await source.StartManualAsync(new Pro2ManualCandidate(target, Windows.Devices.Bluetooth.BluetoothAddressType.Public,
                    "<previously-confirmed-Pro2>", 0, false, 0), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    null, progress, timeout.Token);
            }
            else
#endif
            await source.StartAsync(
                progress,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                preferred,
                onlyPreferredAddresses: customWin11 ? explicitPreferredAddress : preferred != null,
                timeout.Token);
            if (!source.IsRunning)
            {
                WriteLine("[BLE_TURBO_CLI] no_live_fd2 status=\"" + source.Status + "\"");
                WriteLine("[BLE_TURBO_CLI] scan_diagnostic=\"" + source.LastScanDiagnostic + "\"");
                if (customWin11)
                    foreach (string line in await BleEnvironmentDiagnostics.CaptureAsync(timeout.Token)) WriteLine(line);
                return 2;
            }

#if PRO2_OUTPUT_ISOLATION_EXPERIMENT
            if (rateWatch)
                return await BleRateWatchExperiment.RunAsync(source, watchSeconds, directory, WriteLine, timeout.Token);
            if (notifyLayer)
                return await BleNotifyLayerExperiment.RunAsync(source, directory, WriteLine, timeout.Token);
            if (outputIsolation)
                return await BleOutputIsolationExperiment.RunAsync(source, args, directory, progress, WriteLine, timeout.Token);
#endif

            Win10BleDiagnosticReport report =
                await source.RunWin10BleDiagnosticAsync(
                    TimeSpan.FromSeconds(durationSeconds),
                    progress,
                    timeout.Token);
            (string jsonPath, string textPath) =
                await Win10BleDiagnostic.SaveAsync(report, timeout.Token);
            if (customWin11)
                foreach (string line in await BleEnvironmentDiagnostics.CaptureAsync(timeout.Token)) WriteLine(line);
            WriteLine("[BLE_TURBO_CLI] complete json=\"" + jsonPath +
                      "\" text=\"" + textPath + "\"");
            // The isolated Win11 experiment uses a tighter full-window threshold
            // than the historical startup guard. Always export the actual value.
            double requiredHz = customWin11 ? 128 : PureSoftware133Mode.IsEnabled ? PureSoftware133Mode.MinimumAcceptedHz : 60;
            bool passed = report.Fd2NotifyHz >= requiredHz && report.ParsedHz >= requiredHz;
            WriteLine("[BLE_TURBO_CLI] acceptance_required_hz=" + requiredHz +
                " target_hz=" + (PureSoftware133Mode.IsEnabled ? 133 : 60) +
                " passed=" + passed.ToString().ToLowerInvariant());
            return passed ? 0 : 3;
        }
        catch (OperationCanceledException)
        {
            WriteLine("[BLE_TURBO_CLI] timeout_or_cancelled");
            return 4;
        }
        catch (Exception ex)
        {
            WriteLine("[BLE_TURBO_CLI] failed " + ex);
            return 5;
        }
        finally
        {
            try
            {
                WriteLine("[BLE_TURBO_CLI] cleanup_begin");
                Task cleanup = source.DisposeAsync().AsTask();
                Task completed = await Task.WhenAny(
                    cleanup,
                    Task.Delay(TimeSpan.FromSeconds(3)));
                if (ReferenceEquals(completed, cleanup))
                {
                    await cleanup;
                    WriteLine("[BLE_TURBO_CLI] cleanup_complete");
                }
                else
                {
                    WriteLine("[BLE_TURBO_CLI] cleanup_timeout_3s process_will_exit");
                }
            }
            catch (Exception ex)
            {
                WriteLine("[BLE_TURBO_CLI] cleanup_warning " + ex.Message);
            }
        }
    }

    private static int ParseDurationSeconds(string[] args)
    {
        string? text = ParseOption(args, "--duration");
        return int.TryParse(text, out int value)
            ? Math.Clamp(value, 10, 60)
            : 15;
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
