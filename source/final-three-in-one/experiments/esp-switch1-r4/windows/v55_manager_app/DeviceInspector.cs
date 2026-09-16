using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Y700Switch2V55Manager;

public sealed record PortItem(
    string PortName,
    string Name,
    string Manufacturer,
    string DeviceId,
    bool LikelyCh343,
    bool CanOpen,
    string Availability)
{
    public string DisplayName
    {
        get
        {
            string prefix = LikelyCh343
                ? PortName + "  CH343/USB 串口  "
                : PortName + "  ";
            string suffix = string.IsNullOrWhiteSpace(Availability)
                ? ""
                : "  [" + Availability + "]";
            return prefix + Name + suffix;
        }
    }
}

public sealed record PortScanResult(IReadOnlyList<PortItem> Ports, bool MetadataTimedOut, bool MetadataFailed);
public sealed record UsbProbeResult(string Summary, bool TimedOut, bool Failed);
public sealed record PortDriverInfo(
    string PortName,
    string DeviceId,
    string DeviceName,
    string Provider,
    string Version,
    string InfName)
{
    public int WindowsBuild => Environment.OSVersion.Version.Build;

    public bool IsKnownKernelHangRisk =>
        DeviceInspector.IsKnownKernelHangRisk(
            WindowsBuild, Provider, Version);

    public string Summary =>
        "port=" + PortName +
        " provider=" + (string.IsNullOrWhiteSpace(Provider) ? "unknown" : Provider) +
        " version=" + (string.IsNullOrWhiteSpace(Version) ? "unknown" : Version) +
        " inf=" + (string.IsNullOrWhiteSpace(InfName) ? "unknown" : InfName) +
        " os_build=" + WindowsBuild;
}

public static class DeviceInspector
{
    internal static bool IsKnownKernelHangRisk(
        int windowsBuild,
        string provider,
        string version)
    {
        return windowsBuild >= 26300 &&
               provider.Contains("wch", StringComparison.OrdinalIgnoreCase) &&
               version.StartsWith(
                   "2.1.2025.7",
                   StringComparison.OrdinalIgnoreCase);
    }

    public static PortScanResult ScanPorts(int metadataTimeoutMs = 900)
    {
        string[] names = GetPortNamesSafe();
        Dictionary<string, (string Name, string Manufacturer, string DeviceId)> map = new(StringComparer.OrdinalIgnoreCase);
        bool metadataTimedOut = false;
        bool metadataFailed = false;

        if (names.Length > 0)
        {
            try
            {
                Task<Dictionary<string, (string Name, string Manufacturer, string DeviceId)>> task =
                    Task.Run(() => QueryPortMetadata(names));
                if (task.Wait(Math.Max(200, metadataTimeoutMs)))
                {
                    map = task.Result;
                }
                else
                {
                    metadataTimedOut = true;
                }
            }
            catch
            {
                metadataFailed = true;
            }
        }

        PortItem[] ports = names.Select(port =>
        {
            map.TryGetValue(port, out var info);
            string name = string.IsNullOrWhiteSpace(info.Name) ? port : info.Name;
            string manufacturer = info.Manufacturer ?? "";
            string deviceId = info.DeviceId ?? "";
            bool likely = name.Contains("CH343", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("CH340", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("USB-SERIAL", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("USB Serial", StringComparison.OrdinalIgnoreCase) ||
                          manufacturer.Contains("WCH", StringComparison.OrdinalIgnoreCase) ||
                          manufacturer.Contains("QinHeng", StringComparison.OrdinalIgnoreCase) ||
                          deviceId.Contains("VID_1A86&PID_55D3", StringComparison.OrdinalIgnoreCase);

            bool canOpen = true;
            string availability = likely ? "未主动打开" : "";

            return new PortItem(port, name, manufacturer, deviceId, likely, canOpen, availability);
        }).ToArray();

        return new PortScanResult(ports, metadataTimedOut, metadataFailed);
    }

    public static UsbProbeResult ProbeUsb(int timeoutMs = 1200)
    {
        try
        {
            Task<string> task = Task.Run(QueryUsbSummary);
            if (task.Wait(Math.Max(300, timeoutMs)))
            {
                return new UsbProbeResult(task.Result, false, false);
            }

            return new UsbProbeResult(
                "USB 检查超时。界面会保持可操作；可以稍后再点一次“USB 检查”，或者先继续离线操作。",
                true,
                false);
        }
        catch (Exception ex)
        {
            return new UsbProbeResult("USB 检查失败：" + ex.Message, false, true);
        }
    }

    public static PortDriverInfo? QueryPortDriver(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        using RegistryKey? usbRoot = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\USB");
        if (usbRoot == null)
        {
            return null;
        }

        foreach (string hardwareKeyName in usbRoot.GetSubKeyNames())
        {
            using RegistryKey? hardwareKey =
                usbRoot.OpenSubKey(hardwareKeyName);
            if (hardwareKey == null)
            {
                continue;
            }

            foreach (string instanceKeyName in hardwareKey.GetSubKeyNames())
            {
                using RegistryKey? instanceKey =
                    hardwareKey.OpenSubKey(instanceKeyName);
                string friendlyName =
                    Convert.ToString(instanceKey?.GetValue("FriendlyName")) ?? "";
                if (!friendlyName.Contains(
                        "(" + portName + ")",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string driverKeyPath =
                    Convert.ToString(instanceKey?.GetValue("Driver")) ?? "";
                string deviceId =
                    @"USB\" + hardwareKeyName + @"\" + instanceKeyName;
                if (string.IsNullOrWhiteSpace(driverKeyPath))
                {
                    return new PortDriverInfo(
                        portName, deviceId, friendlyName, "", "", "");
                }

                using RegistryKey? driverKey = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\" + driverKeyPath);
                return new PortDriverInfo(
                    portName,
                    deviceId,
                    Convert.ToString(driverKey?.GetValue("DriverDesc")) ??
                        friendlyName,
                    Convert.ToString(driverKey?.GetValue("ProviderName")) ?? "",
                    Convert.ToString(driverKey?.GetValue("DriverVersion")) ?? "",
                    Convert.ToString(driverKey?.GetValue("InfPath")) ?? "");
            }
        }

        return null;
    }

    private static string[] GetPortNamesSafe()
    {
        try
        {
            return SerialPort.GetPortNames().OrderBy(ParsePortNumber).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static Dictionary<string, (string Name, string Manufacturer, string DeviceId)> QueryPortMetadata(string[] names)
    {
        var map = new Dictionary<string, (string Name, string Manufacturer, string DeviceId)>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT Name,Manufacturer,DeviceID FROM Win32_PnPEntity");
        foreach (ManagementObject item in searcher.Get())
        {
            string name = Convert.ToString(item["Name"]) ?? "";
            string manufacturer = Convert.ToString(item["Manufacturer"]) ?? "";
            string deviceId = Convert.ToString(item["DeviceID"]) ?? "";
            foreach (string port in names)
            {
                if (name.Contains("(" + port + ")", StringComparison.OrdinalIgnoreCase))
                {
                    map[port] = (name, manufacturer, deviceId);
                }
            }
        }
        return map;
    }

    private static string QueryUsbSummary()
    {
        var lines = new List<string>();
        using var searcher = new ManagementObjectSearcher("SELECT Name,Status,PNPClass,DeviceID FROM Win32_PnPEntity");
        foreach (ManagementObject item in searcher.Get())
        {
            string deviceId = Convert.ToString(item["DeviceID"]) ?? "";
            string identity;
            if (deviceId.Contains("VID_054C&PID_0DF2", StringComparison.OrdinalIgnoreCase))
            {
                identity = "新和联胜 / PS5 Edge VID_054C&PID_0DF2";
            }
            else if (deviceId.Contains("VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase))
            {
                identity = "新和联胜 / PS5 VID_054C&PID_0CE6";
            }
            else if (deviceId.Contains("VID_057E&PID_2069", StringComparison.OrdinalIgnoreCase))
            {
                identity = "Nintendo/Pro2 VID_057E&PID_2069";
            }
            else if (deviceId.Contains("VID_057E&PID_2009", StringComparison.OrdinalIgnoreCase))
            {
                identity = "Switch 1 Pro Controller bridge VID_057E&PID_2009";
            }
            else if (deviceId.Contains("VID_045E&PID_028E", StringComparison.OrdinalIgnoreCase))
            {
                identity = "Xbox / XInput VID_045E&PID_028E";
            }
            else if (deviceId.Contains("VID_045E&PID_0B00", StringComparison.OrdinalIgnoreCase))
            {
                identity = "Xbox Elite 2 / GIP VID_045E&PID_0B00";
            }
            else if (deviceId.Contains("VID_045E&PID_02E3", StringComparison.OrdinalIgnoreCase))
            {
                identity = "Xbox One S legacy test VID_045E&PID_02E3";
            }
            else
            {
                continue;
            }

            lines.Add((Convert.ToString(item["PNPClass"]) ?? "device") + ": " +
                      identity + " / " +
                      (Convert.ToString(item["Name"]) ?? "unknown") + " / " +
                      (Convert.ToString(item["Status"]) ?? "unknown"));
        }

        if (lines.Count == 0)
        {
            return "没有检测到新和联胜 / PS5 Edge 054C:0DF2、兼容 PS5 054C:0CE6、Nintendo/Pro2 057E:2069、Switch 1 Pro 057E:2009 或 Xbox/XInput 045E:028E 设备。如果刚刷完固件，请重新插拔原生 USB / OTG。";
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int ParsePortNumber(string port)
    {
        string digits = new(port.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int value) ? value : int.MaxValue;
    }
}
