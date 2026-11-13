using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Y700Switch2V60Viiper;

public enum NintendoInputKind { Pro1Bluetooth, Pro1Usb, Pro2Usb }

public sealed record NintendoHidDevice(string Path, string InstanceId, string PhysicalId,
    NintendoInputKind Kind, int InputLength, int OutputLength, string Product)
{
    public string Label => (Kind switch { NintendoInputKind.Pro1Bluetooth => "Pro1 · 蓝牙",
        NintendoInputKind.Pro1Usb => "Pro1 · USB", _ => "Pro2 · USB" }) + " · " + PhysicalId;
    public bool IsPro1 => Kind != NintendoInputKind.Pro2Usb;
    public static IReadOnlyList<Guid> ParseInterfaceGuids(params object?[] registryValues) => registryValues
        .SelectMany(value => value is string[] many ? many : value is string one ? [one] : Array.Empty<string>())
        .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
        .Where(value => value != Guid.Empty).Distinct().ToArray();
    // Identity must include the physical parent, not VID/PID (our outputs use those too).
    public static bool IsPhysicalChain(IEnumerable<string> nodes)
    {
        string[] ids = nodes.Select(n => n.ToUpperInvariant()).ToArray();
        return !ids.Any(n => n.Contains("USBIP") || n.Contains("VIIPER") || n.StartsWith("ROOT\\USB")) &&
            ids.Any(n => n.StartsWith("BTHENUM\\") || n.StartsWith("USB\\ROOT_HUB"));
    }
    public static IReadOnlyList<NintendoHidDevice> Enumerate()
    {
        HidNative.HidD_GetHidGuid(out Guid guid);
        var result = new List<NintendoHidDevice>();
        foreach (var entry in HidNative.Interfaces(guid))
        {
            using var h = HidNative.Open(entry.Path, 0, false);
            if (h.IsInvalid) continue;
            var attr = new HidNative.Attributes { Size = Marshal.SizeOf<HidNative.Attributes>() };
            if (!HidNative.HidD_GetAttributes(h, ref attr) || attr.Vendor != 0x057e ||
                attr.Product is not (0x2009 or 0x2069)) continue;
            string[] chain = HidNative.Ancestors(entry.DevInst);
            if (!IsPhysicalChain(chain) || !HidNative.HidD_GetPreparsedData(h, out IntPtr data)) continue;
            try
            {
                if (HidNative.HidP_GetCaps(data, out var caps) != 0x110000 || caps.UsagePage != 1 ||
                    caps.Usage is not (4 or 5) || caps.InputLength < 49 || caps.OutputLength < 32) continue;
                bool bt = chain.Any(n => n.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase));
                if (attr.Product == 0x2069 && bt) continue; // Pro2 BLE stays with the existing GATT source.
                string physical = chain.FirstOrDefault(n => n.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase)) ??
                    chain.LastOrDefault(n => n.StartsWith("USB\\VID_057E", StringComparison.OrdinalIgnoreCase)) ?? entry.InstanceId;
                var text = new byte[256];
                string name = HidNative.HidD_GetProductString(h, text, text.Length)
                    ? Encoding.Unicode.GetString(text).TrimEnd('\0') : "Nintendo Controller";
                result.Add(new(entry.Path, entry.InstanceId, physical,
                    attr.Product == 0x2069 ? NintendoInputKind.Pro2Usb : bt ? NintendoInputKind.Pro1Bluetooth : NintendoInputKind.Pro1Usb,
                    caps.InputLength, caps.OutputLength, name));
            }
            finally { HidNative.HidD_FreePreparsedData(data); }
        }
        return result.DistinctBy(d => d.PhysicalId, StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Kind).ToArray();
    }
}

internal static class HidNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes { public int Size; public ushort Vendor, Product, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InButton, InValue, InData, OutButton, OutValue, OutData, FeatButton, FeatValue, FeatData;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceInfo { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
    internal sealed record Entry(string Path, string InstanceId, uint DevInst);
    internal static SafeFileHandle Open(string path, uint access, bool async) => CreateFile(path, access, 3, IntPtr.Zero, 3, async ? 0x40000000u : 0, IntPtr.Zero);
    internal static string[] Ancestors(uint node)
    {
        var result = new List<string>();
        for (int depth = 0; depth < 20; depth++)
        {
            var id = new StringBuilder(1024);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0) break;
            result.Add(id.ToString());
            if (CM_Get_Parent(out node, node, 0) != 0) break;
        }
        return result.ToArray();
    }
    internal static IReadOnlyList<Entry> Interfaces(Guid guid)
    {
        var entries = new List<Entry>();
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return entries;
        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new InterfaceInfo { Size = Marshal.SizeOf<InterfaceInfo>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref info)) break;
                var dev = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                SetupDiGetDeviceInterfaceDetail(set, ref info, IntPtr.Zero, 0, out uint size, ref dev);
                if (size < 8 || size > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref info, detail, size, out _, ref dev)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    string id = Ancestors(dev.DevInst).FirstOrDefault() ?? "";
                    if (path.Length > 0) entries.Add(new(path, id, dev.DevInst));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return entries;
    }

    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes a);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr p, out Caps caps);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, int count);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID(uint node, StringBuilder buffer, int length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceInfo info, IntPtr detail, uint size, out uint needed, ref DeviceInfo device);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}

internal sealed class NintendoHidConnection : INintendoHidTransport
{
    private readonly FileStream reader, writer;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly int outputLength;
    public NintendoHidConnection(NintendoHidDevice device)
    {
        var input = HidNative.Open(device.Path, 0x80000000, true);
        if (input.IsInvalid) { input.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "不能打开手柄 HID 输入（可能被其他手柄软件独占或隐藏）"); }
        try
        {
            var output = HidNative.Open(device.Path, 0x40000000, true);
            if (output.IsInvalid) { output.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "不能打开手柄 HID 输出"); }
            writer = new(output, FileAccess.Write, 1, true);
            HidNative.HidD_SetNumInputBuffers(input, 64);
            reader = new(input, FileAccess.Read, 1, true);
            outputLength = device.OutputLength;
        }
        catch { input.Dispose(); throw; }
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => reader.ReadAsync(buffer, token);
    public async Task WriteAsync(ReadOnlyMemory<byte> report, CancellationToken token)
    {
        if (report.Length > outputLength) throw new IOException("HID 输出报告长度不匹配");
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] padded = new byte[outputLength]; report.CopyTo(padded);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(2));
            await writer.WriteAsync(padded, bounded.Token).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync(); await writer.DisposeAsync();
    }
}

// Uses only an already installed vendor-interface WinUSB binding. Never replaces
// the HID, Bluetooth or whole composite-device driver.
public interface INintendoUsbCommandChannel : IDisposable
{
    byte[] Send(byte command, byte subcommand, byte[] data);
}

internal sealed class Pro2UsbCommands : INintendoUsbCommandChannel
{
    private readonly SafeFileHandle handle;
    private readonly IntPtr usb;
    private readonly byte inputPipe, outputPipe;
    private readonly object gate = new();
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct InterfaceDescriptor { public byte Length, Type, Number, Alternate, Endpoints, Class, Subclass, Protocol, String; }
    [StructLayout(LayoutKind.Sequential)] private struct Pipe { public int Type; public byte Id; public ushort MaxPacket; public byte Interval; }
    private Pro2UsbCommands(SafeFileHandle handle, IntPtr usb, byte inputPipe, byte outputPipe)
    { this.handle = handle; this.usb = usb; this.inputPipe = inputPipe; this.outputPipe = outputPipe; }
    public static Pro2UsbCommands Open(NintendoHidDevice device, Action<string>? log = null)
    {
        log ??= _ => { };
        // The device's actual interface GUID is supplied by its installed INF/OS
        // descriptor, not by our virtual controller's private WinUSB GUID.
        var guids = new HashSet<Guid>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        foreach (string name in root?.GetSubKeyNames() ?? [])
        {
            if (!name.StartsWith("VID_057E&PID_2069", StringComparison.OrdinalIgnoreCase)) continue;
            using var group = root!.OpenSubKey(name);
            foreach (string instance in group?.GetSubKeyNames() ?? [])
            {
                using var parameters = group!.OpenSubKey(instance + @"\Device Parameters");
                // Microsoft OS descriptor bindings may publish REG_SZ singular;
                // INF-installed bindings commonly publish plural REG_MULTI_SZ.
                foreach (Guid guid in NintendoHidDevice.ParseInterfaceGuids(
                    parameters?.GetValue("DeviceInterfaceGUIDs"), parameters?.GetValue("DeviceInterfaceGUID")))
                    guids.Add(guid);
            }
        }
        log("[USB_INTERFACE_PROBE] registry_guids="+string.Join(",",guids));
        string failure="no_matching_device_interface";
        foreach (Guid guid in guids)
        foreach (var entry in HidNative.Interfaces(guid))
        {
            string[] chain = HidNative.Ancestors(entry.DevInst);
            bool matches=NintendoHidDevice.IsPhysicalChain(chain) && chain.Contains(device.PhysicalId, StringComparer.OrdinalIgnoreCase);
            log("[USB_INTERFACE_PROBE] interface="+entry.InstanceId+" physical_match="+matches+" path="+entry.Path);
            if (!matches) continue;
            var h = HidNative.Open(entry.Path, 0xc0000000, true);
            if (h.IsInvalid) { failure="CreateFile win32="+Marshal.GetLastWin32Error(); log("[USB_INTERFACE_PROBE] "+failure); h.Dispose(); continue; }
            if (!WinUsb_Initialize(h, out var usb)) { failure="WinUsb_Initialize win32="+Marshal.GetLastWin32Error(); log("[USB_INTERFACE_PROBE] "+failure); h.Dispose(); continue; }
            bool success = false;
            try
            {
                if (!WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor)) { failure="QueryInterfaceSettings win32="+Marshal.GetLastWin32Error(); log("[USB_INTERFACE_PROBE] "+failure); continue; }
                byte input = 0, output = 0;
                for (byte i = 0; i < descriptor.Endpoints; i++)
                    if (WinUsb_QueryPipe(usb, 0, i, out var pipe) && pipe.Type == 2)
                    { if ((pipe.Id & 0x80) != 0) input = pipe.Id; else output = pipe.Id; }
                log($"[USB_INTERFACE_PROBE] interface_number={descriptor.Number} endpoints={descriptor.Endpoints} input=0x{input:X2} output=0x{output:X2}");
                if (input == 0 || output == 0) { failure="bulk_endpoints_missing"; continue; }
                uint timeout = 1200;
                if (!WinUsb_SetPipePolicy(usb, input, 3, 4, ref timeout) || !WinUsb_SetPipePolicy(usb, output, 3, 4, ref timeout)) { failure="SetPipePolicy win32="+Marshal.GetLastWin32Error(); log("[USB_INTERFACE_PROBE] "+failure); continue; }
                success = true;
                return new(h, usb, input, output);
            }
            finally { if (!success) { WinUsb_Free(usb); h.Dispose(); } }
        }
        string advice=failure=="CreateFile win32=5"
            ? "请先正常退出 Steam 及其他手柄工具，再接入本程序；连接后可重新打开 Steam。若仍被拒绝，再检查设备访问权限。"
            : "请查看 USB_INTERFACE_PROBE 的接口与系统错误记录，核对对应厂商接口。";
        throw new IOException("Pro2 USB 命令接口未打开（"+failure+"）。"+advice);
    }
    public byte[] Send(byte command, byte subcommand, byte[] data)
    {
        lock (gate)
        {
            byte[] request = new byte[8 + data.Length];
            request[0] = command; request[1] = 0x91; request[3] = subcommand; request[5] = checked((byte)data.Length);
            data.CopyTo(request, 8);
            if (!WinUsb_WritePipe(usb, outputPipe, request, request.Length, out int written, IntPtr.Zero) || written != request.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Pro2 USB 命令发送失败");
            for (int i = 0; i < 8; i++)
            {
                byte[] response = new byte[512];
                if (!WinUsb_ReadPipe(usb, inputPipe, response, response.Length, out int length, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Pro2 USB 应答超时");
                if (length >= 8 && response[0] == command && response[1] == 1 && response[3] == subcommand)
                {
                    if (response[5] is not (0xf8 or 0x78)) throw new IOException($"Pro2 USB 命令被拒绝：{command:X2}/{subcommand:X2} status={response[5]:X2}");
                    return response[..length];
                }
            }
            throw new IOException("Pro2 USB 没有匹配的命令应答");
        }
    }
    internal static void Initialize(INintendoUsbCommandChannel channel, Action<string> log)
    {
        // Protocol facts from Nintendo USB captures / SDL; no pairing or flash writes.
        (byte c, byte s, string data)[] commands = [
            (7,1,""), (12,2,"27000000"), (17,1,""),
            (10,8,"01FFFFFFFFFFFFFFFF3500460000000000000000"),
            // NFC side-channel commands (01/0C and 01/01) are not required for
            // HID game input. Some real firmware does not ACK the empty 01/01;
            // do not make that optional peripheral initialization a prerequisite.
            (12,4,"27000000"),
            (3,10,"05000000"), (3,13,"0100FFFFFFFFFFFF")];
        foreach (var (c,s,data) in commands)
        { channel.Send(c,s,Convert.FromHexString(data)); log($"[NINTENDO_USB_INIT] command={c:X2}/{s:X2} ack=matched"); }
    }
    public void Dispose() { lock (gate) { WinUsb_Free(usb); handle.Dispose(); } }
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(SafeFileHandle handle, out IntPtr usb);
    [DllImport("winusb.dll")] private static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb.dll")] private static extern bool WinUsb_QueryInterfaceSettings(IntPtr usb, byte alternate, out InterfaceDescriptor descriptor);
    [DllImport("winusb.dll")] private static extern bool WinUsb_QueryPipe(IntPtr usb, byte alternate, byte index, out Pipe pipe);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint size, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte[] data, int length, out int written, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, byte[] data, int length, out int read, IntPtr overlapped);
}
