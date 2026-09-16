using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public static class ControllerEnumerationDiagnostics
{
    private static readonly Regex PortRegex =
        new(@"^\s*Port\s+(\d+)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VidPidRegex =
        new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MiRegex =
        new(@"MI_([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CollectionRegex =
        new(@"COL(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] TargetTokens =
    [
        "054c", "0ce6", "0df2", "057e", "2069", "045e", "028e",
        "dualsense", "wireless controller", "nintendo", "xbox", "if_hid"
    ];

    public static async Task<IReadOnlyList<string>> DumpAsync(
        ViiperProtocolClient client,
        string sessionLogPath,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            "[DIAG] kind=controller_enumeration version=6.2.32-stick-calibration-test-r2 session_log=\"" + sessionLogPath + "\""
        };
        await AppendViiperDumpAsync(client, lines, cancellationToken);
        await AppendUsbipPortDumpAsync(lines, cancellationToken);
        await AppendPnpDumpAsync(lines, cancellationToken);
        return lines;
    }

    public static async Task<IReadOnlyList<string>> CleanupStaleVirtualDevicesAsync(
        ViiperProtocolClient client,
        CancellationToken cancellationToken,
        bool includePnpDump = true)
    {
        var lines = new List<string>
        {
            "[CLEANUP] begin scope=local_viiper_buses_and_matching_usbip_ports order=usbip_detach_then_viiper_remove"
        };

        // Keep the server-side device alive until Windows has processed the USB unplug.
        // Removing the VIIPER device first turns the unplug into an abrupt HID read failure,
        // which can leave a stale SDL controller slot inside a long-running Steam process.
        await AppendUsbipDetachAsync(lines, cancellationToken);
        await Task.Delay(250, cancellationToken);

        try
        {
            IReadOnlyList<uint> buses = await client.BusListAsync(cancellationToken);
            lines.Add("[CLEANUP_VIIPER] buses_before=" + string.Join(",", buses));
            foreach (uint bus in buses)
            {
                try
                {
                    IReadOnlyList<ViiperDevice> devices =
                        await client.BusDevicesAsync(bus, cancellationToken);
                    foreach (ViiperDevice device in devices)
                    {
                        lines.Add("[CLEANUP_VIIPER] remove_device bus=" + bus +
                                  " dev=" + device.DevId +
                                  " type=" + device.Type +
                                  " vid=" + device.Vid +
                                  " pid=" + device.Pid);
                        if (!string.IsNullOrWhiteSpace(device.DevId))
                        {
                            await client.RemoveDeviceAsync(bus, device.DevId, cancellationToken);
                        }
                    }

                    await client.RemoveBusAsync(bus, cancellationToken);
                    lines.Add("[CLEANUP_VIIPER] removed_bus=" + bus);
                }
                catch (Exception ex)
                {
                    lines.Add("[CLEANUP_VIIPER] bus=" + bus +
                              " warning=" + OneLine(ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add("[CLEANUP_VIIPER] skipped=" + OneLine(ex.Message));
        }

        await AppendUsbipPortDumpAsync(lines, cancellationToken);
        if (includePnpDump)
        {
            await AppendPnpDumpAsync(lines, cancellationToken);
        }
        else
        {
            lines.Add("[PNP_DUMP] skipped=automatic_virtual_device_guard");
        }
        lines.Add("[CLEANUP] complete");
        return lines;
    }

    public static async Task<IReadOnlyList<string>> DetachViiperDeviceAsync(
        ViiperDevice device,
        string host,
        int apiPort,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        if (!IsLoopbackHost(host))
        {
            lines.Add("[USBIP_ORDERLY_DETACH] skipped=remote_host host=" + OneLine(host));
            return lines;
        }

        UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
        if (runtime == null)
        {
            lines.Add("[USBIP_ORDERLY_DETACH] skipped=usbip.exe_not_found");
            return lines;
        }

        int usbServerPort = DeriveUsbPort(apiPort);
        ProcessResult portResult = await RunProcessAsync(
            runtime.ExePath,
            ["port"],
            runtime.DirectoryPath,
            TimeSpan.FromSeconds(6),
            cancellationToken);
        if (portResult.ExitCode != 0)
        {
            lines.Add("[USBIP_ORDERLY_DETACH] skipped=port_dump_failed exit=" +
                      portResult.ExitCode + " output=" + OneLine(portResult.CombinedOutput));
            return lines;
        }

        int[] matchingPorts = FindUsbipPortsForRemoteDevice(
                portResult.CombinedOutput,
                usbServerPort,
                device.BusId,
                device.DevId)
            .Distinct()
            .ToArray();
        if (matchingPorts.Length == 0)
        {
            lines.Add("[USBIP_ORDERLY_DETACH] state=already_absent" +
                      " usb_server_port=" + usbServerPort +
                      " remote_bus_dev=" + device.BusId + "/" + device.DevId);
            return lines;
        }

        foreach (int port in matchingPorts)
        {
            ProcessResult detach = await RunProcessAsync(
                runtime.ExePath,
                ["detach", "-p", port.ToString()],
                runtime.DirectoryPath,
                TimeSpan.FromSeconds(8),
                cancellationToken);
            lines.Add("[USBIP_ORDERLY_DETACH] port=" + port +
                      " usb_server_port=" + usbServerPort +
                      " remote_bus_dev=" + device.BusId + "/" + device.DevId +
                      " exit=" + detach.ExitCode +
                      " output=" + OneLine(detach.CombinedOutput));
        }

        bool absent = await WaitForRemoteDeviceAbsentAsync(
            runtime,
            usbServerPort,
            device.BusId,
            device.DevId,
            cancellationToken);
        lines.Add("[USBIP_ORDERLY_DETACH] confirmed_absent=" + absent.ToString().ToLowerInvariant() +
                  " remote_bus_dev=" + device.BusId + "/" + device.DevId +
                  " policy=detach_client_before_remove_server_device");
        return lines;
    }

    private static async Task AppendViiperDumpAsync(
        ViiperProtocolClient client,
        List<string> lines,
        CancellationToken cancellationToken)
    {
        try
        {
            string ping = await client.PingAsync(cancellationToken);
            lines.Add("[VIIPER_DUMP] ping=" + OneLine(ping));
            IReadOnlyList<uint> buses = await client.BusListAsync(cancellationToken);
            lines.Add("[VIIPER_DUMP] buses=" + string.Join(",", buses));
            foreach (uint bus in buses)
            {
                IReadOnlyList<ViiperDevice> devices =
                    await client.BusDevicesAsync(bus, cancellationToken);
                if (devices.Count == 0)
                {
                    lines.Add("[VIIPER_DUMP] bus=" + bus + " devices=0");
                    continue;
                }

                foreach (ViiperDevice device in devices)
                {
                    lines.Add("[VIIPER_DUMP] bus=" + bus +
                              " dev=" + device.DevId +
                              " type=" + device.Type +
                              " vid=" + device.Vid +
                              " pid=" + device.Pid +
                              " classification=" + ClassifyViiperDevice(device));
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add("[VIIPER_DUMP] unavailable=" + OneLine(ex.Message));
        }
    }

    private static async Task AppendUsbipPortDumpAsync(
        List<string> lines,
        CancellationToken cancellationToken)
    {
        UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
        if (runtime == null)
        {
            lines.Add("[USBIP_PORT] unavailable=usbip.exe_not_found");
            return;
        }

        ProcessResult result = await RunProcessAsync(
            runtime.ExePath,
            ["port"],
            runtime.DirectoryPath,
            TimeSpan.FromSeconds(6),
            cancellationToken);
        lines.Add("[USBIP_PORT] exe=\"" + runtime.ExePath + "\" exit=" + result.ExitCode);
        foreach (string line in SplitLines(result.CombinedOutput))
        {
            lines.Add("[USBIP_PORT] " + line);
        }
    }

    private static async Task AppendUsbipDetachAsync(
        List<string> lines,
        CancellationToken cancellationToken)
    {
        UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
        if (runtime == null)
        {
            lines.Add("[USBIP_DETACH] skipped=usbip.exe_not_found");
            return;
        }

        ProcessResult portResult = await RunProcessAsync(
            runtime.ExePath,
            ["port"],
            runtime.DirectoryPath,
            TimeSpan.FromSeconds(6),
            cancellationToken);
        if (portResult.ExitCode != 0)
        {
            lines.Add("[USBIP_DETACH] skipped=port_dump_failed exit=" +
                      portResult.ExitCode + " output=" + OneLine(portResult.CombinedOutput));
            return;
        }

        int[] matchingPorts = MatchingUsbipPorts(portResult.CombinedOutput).Distinct().ToArray();
        foreach (int port in matchingPorts)
        {
            ProcessResult detach = await RunProcessAsync(
                runtime.ExePath,
                ["detach", "-p", port.ToString()],
                runtime.DirectoryPath,
                TimeSpan.FromSeconds(8),
                cancellationToken);
            lines.Add("[USBIP_DETACH] port=" + port +
                      " exit=" + detach.ExitCode +
                      " output=" + OneLine(detach.CombinedOutput));
        }

        if (matchingPorts.Length > 0)
        {
            await Task.Delay(250, cancellationToken);
        }
    }

    private static async Task AppendPnpDumpAsync(
        List<string> lines,
        CancellationToken cancellationToken)
    {
        string script = """
$patterns = @('VID_054C&PID_0CE6','VID_054C&PID_0DF2','VID_057E&PID_2069','VID_045E&PID_028E','If_Hid','Wireless Controller','Nintendo','XBOX')
$items = Get-CimInstance Win32_PnPEntity | Where-Object {
    $text = (@($_.Name,$_.PNPDeviceID,$_.Manufacturer,$_.PNPClass,$_.Service,$_.HardwareID,$_.CompatibleID) -join ' ')
    foreach ($p in $patterns) {
        if ($text -match [regex]::Escape($p)) { return $true }
    }
    return $false
} | Select-Object Name,PNPDeviceID,PNPClass,Service,Manufacturer,Status,HardwareID,CompatibleID
$items | ConvertTo-Json -Depth 5 -Compress
""";

        ProcessResult result = await RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script],
            AppContext.BaseDirectory,
            TimeSpan.FromSeconds(12),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            lines.Add("[PNP_DUMP] unavailable exit=" + result.ExitCode +
                      " output=" + OneLine(result.CombinedOutput));
            return;
        }

        string json = result.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            lines.Add("[PNP_SUMMARY] matched=0");
            return;
        }

        int count = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : [doc.RootElement];
            foreach (JsonElement item in items)
            {
                count++;
                string name = GetJsonText(item, "Name");
                string instance = GetJsonText(item, "PNPDeviceID");
                string pnpClass = GetJsonText(item, "PNPClass");
                string service = GetJsonText(item, "Service");
                string manufacturer = GetJsonText(item, "Manufacturer");
                string status = GetJsonText(item, "Status");
                string hardwareIds = GetJsonText(item, "HardwareID");
                string compatibleIds = GetJsonText(item, "CompatibleID");
                string combined = name + " " + instance + " " + hardwareIds + " " + compatibleIds;
                lines.Add("[PNP_HID] name=\"" + OneLine(name) + "\"" +
                          " instance=\"" + OneLine(instance) + "\"" +
                          " vid_pid=" + VidPid(combined) +
                          " mi=" + Capture(MiRegex, combined, "none") +
                          " collection=" + Capture(CollectionRegex, combined, "none") +
                          " class=\"" + OneLine(pnpClass) + "\"" +
                          " service=\"" + OneLine(service) + "\"" +
                          " manufacturer=\"" + OneLine(manufacturer) + "\"" +
                          " status=\"" + OneLine(status) + "\"" +
                          " usage=\"" + HidUsageHint(combined) + "\"" +
                          " classification=\"" + ClassifyPnp(combined, pnpClass, service) + "\"" +
                          " hardware_ids=\"" + OneLine(hardwareIds) + "\"" +
                          " compatible_ids=\"" + OneLine(compatibleIds) + "\"");
            }

            lines.Add("[PNP_SUMMARY] matched=" + count +
                      " note=\"If_Hid entries are classified by interface/usage; they are not removed automatically.\"");
        }
        catch (Exception ex)
        {
            lines.Add("[PNP_DUMP] parse_failed=" + OneLine(ex.Message) +
                      " raw=" + OneLine(json));
        }
    }

    public static IReadOnlyList<int> FindUsbipPortsForRemoteDevice(
        string output,
        int usbServerPort,
        uint busId,
        string devId)
    {
        string endpointToken = ":" + usbServerPort + "/" + busId + "-" + devId;
        string remoteBusDevPattern = @"remote\s+bus/dev\s+0*" + busId + @"/0*" +
                                     Regex.Escape(devId) + @"(?:\D|$)";
        return ParseUsbipPortBlocks(output)
            .Where(block =>
                block.Text.Contains(endpointToken, StringComparison.OrdinalIgnoreCase) ||
                (block.Text.Contains(":" + usbServerPort + "/", StringComparison.OrdinalIgnoreCase) &&
                 Regex.IsMatch(block.Text, remoteBusDevPattern, RegexOptions.IgnoreCase)))
            .Select(block => block.Port)
            .ToArray();
    }

    private static IEnumerable<int> MatchingUsbipPorts(string output)
    {
        return ParseUsbipPortBlocks(output)
            .Where(block => TargetTokens.Any(
                token => block.Text.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Select(block => block.Port);
    }

    private static IReadOnlyList<UsbipPortBlock> ParseUsbipPortBlocks(string output)
    {
        var blocks = new List<UsbipPortBlock>();
        int? currentPort = null;
        var current = new StringBuilder();

        foreach (string line in SplitLines(output))
        {
            Match match = PortRegex.Match(line);
            if (match.Success)
            {
                if (currentPort.HasValue)
                {
                    blocks.Add(new UsbipPortBlock(currentPort.Value, current.ToString()));
                }
                currentPort = int.Parse(match.Groups[1].Value);
                current.Clear();
            }
            current.AppendLine(line);
        }

        if (currentPort.HasValue)
        {
            blocks.Add(new UsbipPortBlock(currentPort.Value, current.ToString()));
        }

        return blocks;
    }

    private static async Task<bool> WaitForRemoteDeviceAbsentAsync(
        UsbipRuntime runtime,
        int usbServerPort,
        uint busId,
        string devId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            ProcessResult result = await RunProcessAsync(
                runtime.ExePath,
                ["port"],
                runtime.DirectoryPath,
                TimeSpan.FromSeconds(3),
                cancellationToken);
            if (result.ExitCode == 0 &&
                FindUsbipPortsForRemoteDevice(
                    result.CombinedOutput,
                    usbServerPort,
                    busId,
                    devId).Count == 0)
            {
                return true;
            }

            await Task.Delay(125, cancellationToken);
        }

        return false;
    }

    private static int DeriveUsbPort(int apiPort) => apiPort == 3242 ? 3241 : apiPort == 1 ? 2 : apiPort - 1;

    private static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyViiperDevice(ViiperDevice device)
    {
        string type = device.Type.ToLowerInvariant();
        string id = (device.Vid + " " + device.Pid).ToLowerInvariant();
        if (type.Contains("dualsenseedge") || id.Contains("0xdf2"))
        {
            return "ps5_edge_virtual_dualsenseedge_l4_r4";
        }
        if (type.Contains("dualsensehaptic") || id.Contains("0xce6"))
        {
            return "ps5_virtual_dualsensehaptic_hd_audio";
        }
        if (type.Contains("ns2pro") || id.Contains("2069"))
        {
            return "nintendo_pro2_virtual";
        }
        if (type.Contains("xbox") || id.Contains("028e"))
        {
            return "xinput_virtual";
        }
        return "unknown_viiper_device";
    }

    private static string ClassifyPnp(string combined, string pnpClass, string service)
    {
        if (combined.Contains("PID_0DF2", StringComparison.OrdinalIgnoreCase))
        {
            return "ps5_edge_virtual_or_physical";
        }
        if (combined.Contains("PID_0CE6", StringComparison.OrdinalIgnoreCase))
        {
            return "ps5_dualsense_virtual_or_physical";
        }
        if (combined.Contains("PID_2069", StringComparison.OrdinalIgnoreCase))
        {
            return combined.Contains("If_Hid", StringComparison.OrdinalIgnoreCase)
                ? "nintendo_interface_if_hid_descriptor_name"
                : "nintendo_pro_controller_interface";
        }
        if (combined.Contains("PID_028E", StringComparison.OrdinalIgnoreCase))
        {
            return "xinput_compat_interface";
        }
        if (combined.Contains("If_Hid", StringComparison.OrdinalIgnoreCase))
        {
            return "if_hid_needs_descriptor_usage_review";
        }
        if (string.Equals(pnpClass, "HIDClass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service, "HidUsb", StringComparison.OrdinalIgnoreCase))
        {
            return "hid_interface";
        }
        return "matched_non_hid_or_parent";
    }

    private static string HidUsageHint(string text)
    {
        if (text.Contains("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("_U:0005", StringComparison.OrdinalIgnoreCase))
        {
            return "game_controller";
        }
        if (text.Contains("_UP:0001", StringComparison.OrdinalIgnoreCase))
        {
            return "generic_desktop";
        }
        return "unknown_from_pnp";
    }

    private static string VidPid(string text)
    {
        Match match = VidPidRegex.Match(text);
        return match.Success
            ? match.Groups[1].Value.ToUpperInvariant() + ":" +
              match.Groups[2].Value.ToUpperInvariant()
            : "none";
    }

    private static string Capture(Regex regex, string text, string fallback)
    {
        Match match = regex.Match(text);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : fallback;
    }

    private static string GetJsonText(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.Undefined)
        {
            return "";
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return string.Join(";", value.EnumerateArray().Select(ElementToText));
        }

        return ElementToText(value);
    }

    private static string ElementToText(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ToString();
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        return (value ?? "")
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string OneLine(string value)
    {
        return (value ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\"", "'");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? AppContext.BaseDirectory
                : workingDirectory
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(startInfo);
        if (process == null)
        {
            return new ProcessResult(-1, "", "Process.Start returned null");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            return new ProcessResult(-2, "", "timeout after " + timeout.TotalSeconds.ToString("F0") + "s");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public string CombinedOutput => (Stdout + "\n" + Stderr).Trim();
    }

    private sealed record UsbipPortBlock(int Port, string Text);
}


