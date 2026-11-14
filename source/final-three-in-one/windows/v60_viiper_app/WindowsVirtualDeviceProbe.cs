using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Y700Switch2V60Viiper;

public sealed record VirtualPnpDevice(int Port, string InstanceId, uint Problem, bool Started, bool InputReady, string Service, string ConfigurationDiagnostic = "");
public sealed record VirtualPnpSnapshot(bool Readable, IReadOnlyList<VirtualPnpDevice> Devices, string Error);

// Read-only Config Manager metadata. No device opens, driver changes or GATT I/O.
public static class WindowsVirtualDeviceProbe
{
    private const uint Present = 0x100, Started = 8, HasProblem = 0x400;
    private static readonly Regex PortPattern = new(@"Port_#0*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static VirtualPnpSnapshot Capture(string vid, string pid)
    {
        try {
            if (!OperatingSystem.IsWindows()) return new(false, [], "Windows required");
            if (CM_Get_Device_ID_List_SizeW(out uint length, null, Present) != 0 || length is < 1 or > 1048576)
                return new(false, [], "PnP device list unavailable");
            var list = new char[length];
            if (CM_Get_Device_ID_ListW(null, list, length, Present) != 0) return new(false, [], "PnP enumeration failed");
            string token = "USB\\VID_" + vid.Replace("0x", "", StringComparison.OrdinalIgnoreCase) + "&PID_" + pid.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            var result = new List<VirtualPnpDevice>();
            foreach (string id in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
                if (!id.StartsWith(token, StringComparison.OrdinalIgnoreCase) || id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;
                if (CM_Locate_DevNodeW(out uint node, id, 0) != 0 || !IsUsbipTree(node)) continue;
                Match match = PortPattern.Match(Property(node, 0xE));
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int port)) continue;
                bool statusRead = CM_Get_DevNode_Status(out uint status, out uint problem, node, 0) == 0;
                bool running = statusRead && (status & Started) != 0 && (status & HasProblem) == 0 && problem == 0;
                string service = Property(node, 5);
                bool input = running && InputReady(node, 0);
                result.Add(new(port, id, statusRead ? problem : uint.MaxValue, running, input, service,
                    statusRead && problem==19 ? VirtualDeviceConfigurationDiagnosis.Capture(id) : ""));
            }
            return new(true, result, "");
        }
        catch (Exception ex) { return new(false, [], ex.GetType().Name + ": " + ex.Message); }
    }
    private static bool IsUsbipTree(uint node)
    {
        for (int depth = 0; depth < 10; depth++) {
            string service = Property(node, 5);
            if ((service.Contains("usbip", StringComparison.OrdinalIgnoreCase) && !service.Contains("filter", StringComparison.OrdinalIgnoreCase))) return true;
            if (CM_Get_Parent(out uint parent, node, 0) != 0) break;
            node = parent;
        }
        return false;
    }
    private static bool InputReady(uint node, int depth)
    {
        if (depth > 6) return false;
        string service = Property(node, 5);
        if (CM_Get_DevNode_Status(out uint status, out uint problem, node, 0) == 0 && problem == 0 &&
            (status & Started) != 0 && (status & HasProblem) == 0 &&
            (service.Equals("HidUsb", StringComparison.OrdinalIgnoreCase) || service.StartsWith("xusb", StringComparison.OrdinalIgnoreCase))) return true;
        if (CM_Get_Child(out uint child, node, 0) != 0) return false;
        for (int count = 0; count < 64; count++) {
            if (InputReady(child, depth + 1)) return true;
            if (CM_Get_Sibling(out uint sibling, child, 0) != 0) break;
            child = sibling;
        }
        return false;
    }
    private static string Property(uint node, uint property)
    {
        byte[] buffer = new byte[4096]; uint length = (uint)buffer.Length;
        return CM_Get_DevNode_Registry_PropertyW(node, property, out _, buffer, ref length, 0) == 0
            ? Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(length, buffer.Length)).TrimEnd('\0') : "";
    }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] list, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Child(out uint child, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Sibling(out uint sibling, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, [Out] byte[] buffer, ref uint length, uint flags);
}
