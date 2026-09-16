using System.IO.Ports;
using System.Text;
using Microsoft.Win32;

namespace XinHeLianSheng.Pico2WFlasher;

public static class PicoSerialDevice
{
    private const string UsbEnumKey = @"SYSTEM\CurrentControlSet\Enum\USB";
    private const string PicoProductPrefix = "VID_CAFE&PID_4021";

    public static IReadOnlyList<string> FindConnectedPorts()
    {
        string[] portNames;
        try { portNames = SerialPort.GetPortNames(); }
        catch { return Array.Empty<string>(); }
        var active = new HashSet<string>(portNames, StringComparer.OrdinalIgnoreCase);
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using RegistryKey? usb = Registry.LocalMachine.OpenSubKey(UsbEnumKey);
            if (usb is null) return result.ToArray();
            foreach (string productName in usb.GetSubKeyNames().Where(name =>
                         name.StartsWith(PicoProductPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                using RegistryKey? product = usb.OpenSubKey(productName);
                if (product is null) continue;
                foreach (string instanceName in product.GetSubKeyNames())
                {
                    using RegistryKey? instance = product.OpenSubKey(instanceName);
                    using RegistryKey? parameters = instance?.OpenSubKey("Device Parameters");
                    string? portName = parameters?.GetValue("PortName") as string;
                    if (!string.IsNullOrWhiteSpace(portName) && active.Contains(portName))
                        result.Add(portName);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        return result.ToArray();
    }

    public static void RebootToBootsel(string portName)
    {
        using var port = Open(portName);
        port.Write("bootsel\n");
        port.BaseStream.Flush();
    }

    public static string ReadFirmwareIdentity(string portName, TimeSpan timeout)
    {
        using var port = Open(portName);
        port.DiscardInBuffer();
        port.Write("version\n");

        var result = new StringBuilder();
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string line = port.ReadLine();
                result.AppendLine(line.Trim());
                if (line.Contains("firmware_revision=", StringComparison.Ordinal)) break;
            }
            catch (TimeoutException) { }
        }
        return result.ToString();
    }

    public static string ExecuteCommand(string portName, string command,
                                        TimeSpan timeout)
    {
        using var port = Open(portName);
        port.DiscardInBuffer();
        Thread.Sleep(80);
        port.Write(command.Trim() + "\n");
        port.BaseStream.Flush();

        var result = new StringBuilder();
        DateTime deadline = DateTime.UtcNow + timeout;
        DateTime lastData = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            string chunk = port.ReadExisting();
            if (!string.IsNullOrEmpty(chunk))
            {
                result.Append(chunk);
                lastData = DateTime.UtcNow;
            }
            else if (result.Length > 0 &&
                     DateTime.UtcNow - lastData > TimeSpan.FromMilliseconds(350))
            {
                break;
            }
            Thread.Sleep(40);
        }
        return result.ToString();
    }

    private static SerialPort Open(string portName)
    {
        var port = new SerialPort(portName, 115200)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 250,
            WriteTimeout = 1500,
            NewLine = "\n",
            Encoding = Encoding.ASCII,
        };
        port.Open();
        return port;
    }
}
