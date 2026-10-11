using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Source-only diagnostic. Normal releases exclude this entire file and branch.
// Reuses the production receiver, server, stream and orderly detach paths.
internal static class BleOutputIsolationExperiment
{
    private const int ApiPort = 3252;
    // Production USBIP attachment derives the USB port as API port minus one.
    private const int UsbPort = ApiPort - 1;
    private const int SecondsPerPhase = 30;

    public static async Task<int> RunAsync(Pro2BleInputSource source, string[] args, string directory,
        IProgress<string> progress, Action<string> log, CancellationToken token)
    {
        string? serverPath = Option(args, "--viiper-exe");
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
            throw new InvalidOperationException("Explicit local VIIPER executable required; nothing will be downloaded or installed.");
        UsbipRuntime runtime = UsbipRuntimeLocator.Find() ??
            throw new InvalidOperationException("USBIP is not installed; experiment does not install drivers.");
        UsbipProbeResult probe = await UsbipRuntimeLocator.ProbeAsync(runtime, token);
        if (!probe.Ready) throw new InvalidOperationException(probe.Detail);
        AssertPortUnused(ApiPort);
        AssertPortUnused(UsbPort);
        if (source.RuntimeBatteryRefreshEnabled)
            throw new InvalidOperationException("Runtime battery refresh must remain off for isolation.");

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string summaryPath = Path.Combine(directory, "output-isolation-" + stamp + ".json");
        var reports = new List<Win10BleDiagnosticReport>();
        Process? server = null;
        ViiperBridgeSession? session = null;
        Exception? sessionFault = null;
        var client = new ViiperProtocolClient("127.0.0.1", ApiPort);
        string address = source.ConnectedAddress;
        log("[BLE_OUTPUT_ISOLATION] begin physical_connections=1 reconnect=false runtime_battery=false system_config_writes=false phase_seconds=" + SecondsPerPhase);
        log("[BLE_OUTPUT_ISOLATION] server_exe=" + serverPath + " usbip=" + runtime.ExePath + " dedicated_ports=" + ApiPort + "/" + UsbPort);

        async Task Measure(string phase)
        {
            if (sessionFault != null) throw new InvalidOperationException("Virtual session failed; phase is not a valid comparison.", sessionFault);
            if (!source.GetRateEvidence().Connected || source.ConnectedAddress != address)
                throw new InvalidOperationException("Physical BLE connection changed; abort rather than silently reconnect.");
            log("[BLE_OUTPUT_PHASE] begin phase=" + phase + " physical_address=" + address + " virtual=" + (session != null));
            var previous = source.GetCompactCounters();
            long previousTicks = Stopwatch.GetTimestamp();
            Task<Win10BleDiagnosticReport> measurement = source.RunWin10BleDiagnosticAsync(TimeSpan.FromSeconds(SecondsPerPhase), progress, token);
            while (!measurement.IsCompleted)
            {
                await Task.WhenAny(measurement, Task.Delay(5000, token));
                token.ThrowIfCancellationRequested();
                long nowTicks = Stopwatch.GetTimestamp();
                double seconds = Stopwatch.GetElapsedTime(previousTicks, nowTicks).TotalSeconds;
                var current = source.GetCompactCounters();
                if (seconds >= .2)
                {
                    bool countersValid = current.Fd2 >= previous.Fd2 && current.Parsed >= previous.Parsed;
                    log("[BLE_OUTPUT_WINDOW] phase=" + phase + " seconds=" + seconds.ToString("F2") +
                        " counters_valid=" + countersValid + " fd2_hz=" +
                        (countersValid ? ((current.Fd2 - previous.Fd2) / seconds).ToString("F2") : "invalid") +
                        " parsed_hz=" + (countersValid ? ((current.Parsed - previous.Parsed) / seconds).ToString("F2") : "invalid") +
                        " last_input_age_ms=" + source.GetRateEvidence().LastInputAgeMs.ToString("F1"));
                }
                previous = current;
                previousTicks = nowTicks;
            }
            Win10BleDiagnosticReport report = await measurement;
            if (sessionFault != null || !source.GetRateEvidence().Connected || source.ConnectedAddress != address)
                throw new InvalidOperationException("Connection/session changed during measurement; no successful result claimed.", sessionFault);
            report = report with { ExperimentProfile = "OUTPUT_ISOLATION_" + phase };
            reports.Add(report);
            var saved = await Win10BleDiagnostic.SaveAsync(report, token);
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }), token);
            log("[BLE_OUTPUT_PHASE] complete phase=" + phase + " fd2_hz=" + report.Fd2NotifyHz.ToString("F2") +
                " parsed_hz=" + report.ParsedHz.ToString("F2") + " p50_ms=" + report.Fd2NotifyGaps.P50Ms.ToString("F2") +
                " p95_ms=" + report.Fd2NotifyGaps.P95Ms.ToString("F2") + " accepted_133=" + report.Input133HzAccepted +
                " json=" + saved.JsonPath);
        }

        async Task StopServer()
        {
            if (server == null) return;
            // This exact Process object was created here, never kill by name.
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await server.WaitForExitAsync(deadline.Token);
            }
            server.Dispose();
            server = null;
            log("[BLE_OUTPUT_ISOLATION] owned_server_stopped=true");
        }

        try
        {
            await Measure("A0_NATIVE_ONLY");
            var start = new ProcessStartInfo
            {
                FileName = serverPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(serverPath))!,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "server", "--api.addr=127.0.0.1:" + ApiPort,
                "--usb.addr=127.0.0.1:" + UsbPort, "--api.device-handler-connect-timeout=60s",
                "--usb.write-batch-flush-interval=0ms", "--update-notify=none", "--log.level=info",
                "--log.file=" + Path.Combine(directory, "output-isolation-server-" + stamp + ".log") })
                start.ArgumentList.Add(argument);
            start.Environment["PATH"] = UsbipRuntimeLocator.BuildPathWithUsbipDirectory(
                Environment.GetEnvironmentVariable("PATH") ?? "", runtime);
            server = Process.Start(start) ?? throw new InvalidOperationException("VIIPER process did not start.");
            server.OutputDataReceived += (_, e) => { if (e.Data != null) log("[ISOLATION_SERVER_STDOUT] " + e.Data); };
            server.ErrorDataReceived += (_, e) => { if (e.Data != null) log("[ISOLATION_SERVER_STDERR] " + e.Data); };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            bool ready = false;
            for (int attempt = 0; attempt < 25; attempt++)
            {
                if (server.HasExited) throw new InvalidOperationException("Owned VIIPER server exited: " + server.ExitCode);
                try
                {
                    using var ping = CancellationTokenSource.CreateLinkedTokenSource(token);
                    ping.CancelAfter(650);
                    await client.PingAsync(ping.Token);
                    ready = true;
                    break;
                }
                catch (Exception) when (!token.IsCancellationRequested) { await Task.Delay(200, token); }
            }
            if (!ready) throw new InvalidOperationException("Owned VIIPER server was not ready.");
            await Measure("A1_SERVER_IDLE");
            foreach (ViiperDeviceProfile profile in new[] { ViiperDeviceProfile.DualSenseLike, ViiperDeviceProfile.Xbox, ViiperDeviceProfile.Pro2 })
            {
                sessionFault = null;
                var paced = profile with { SourcePaced = true, DeviceSpecificSerialNumber = "PRO2-ISOLATION-" + profile.Mode };
                session = new ViiperBridgeSession(client, paced, progress, source, source,
                    new DirectProgress<Exception>(e => sessionFault = e), gyroMode: ViiperGyroMode.Hold250Hz);
                await session.StartAsync(token);
                // No synthetic motion, rumble or audio is generated. Host feedback
                // uses the same production path; output counters are logged.
                await Measure("B_" + profile.Mode);
                await session.DisposeAsync();
                session = null;
                await Task.Delay(2000, token); // allow PnP removal, not a BLE reconfiguration
                await Measure("A_AFTER_" + profile.Mode);
            }
            await StopServer();
            await Measure("A_FINAL_NATIVE_ONLY");
            bool allHigh = reports.All(r => r.Input133HzAccepted);
            log("[BLE_OUTPUT_ISOLATION] complete phases=" + reports.Count + " all_133_accepted=" + allHigh +
                " virtual_output_cause_proven=false summary=" + summaryPath);
            return allHigh ? 0 : 3;
        }
        finally
        {
            if (session != null)
            {
                try { await session.DisposeAsync(); } catch (Exception e) { log("[BLE_OUTPUT_ISOLATION] session_cleanup_warning=" + e.Message); }
            }
            try { await StopServer(); } catch (Exception e) { log("[BLE_OUTPUT_ISOLATION] server_cleanup_warning=" + e.Message); }
            log("[BLE_OUTPUT_ISOLATION] cleanup_complete summary=" + summaryPath);
        }
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void AssertPortUnused(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        finally { listener.Stop(); }
    }
}
