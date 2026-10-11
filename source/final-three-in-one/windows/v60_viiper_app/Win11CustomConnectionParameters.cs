using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Bluetooth;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Own-object ABI route, validated on one Win11 stack/adapter tuple. Other
/// environments require explicit in-app or isolated CLI opt-in. NOT an official constructor.
/// It supplies the documented WinRT default interface, without modifying a
/// preset's memory, registry, system DLL, driver, or pairing state. The real
/// Win11 request may reject the object or clamp its values; only FD2 proves success.
/// IID / getter order: Microsoft's windows-rs Devices/Bluetooth metadata.
/// </summary>
public sealed class Win11CustomConnectionParameters
{
    public const string EnableArgument = "--win11-7p5-abi-test";
    public const ushort IntervalUnits = 6;
    public const ushort Latency = 0;
    public const ushort TimeoutUnits = 600; // 6 seconds; avoid the aggressive preset's 2-second timeout.
    public static readonly Guid ParametersIid = new("F2F44344-7372-5F7B-9B34-29C944F5A715");
    public static bool IsCliRequested(System.Collections.Generic.IReadOnlyList<string> args) =>
        System.Linq.Enumerable.Any(args, value => string.Equals(value, EnableArgument, StringComparison.OrdinalIgnoreCase)) &&
        System.Linq.Enumerable.Any(args, value => string.Equals(value, "--ble-diagnostic", StringComparison.OrdinalIgnoreCase));

    private readonly ParameterObject source;
    private readonly BluetoothLEPreferredConnectionParameters projected;

    // Projection of our own IInspectable object only; no Win11 OS API is invoked here.
    // This permits an offline ABI round-trip on Win10, distinct from a real Win11 request.
#pragma warning disable CA1416
    public Win11CustomConnectionParameters(ushort minUnits = IntervalUnits, ushort maxUnits = IntervalUnits,
        ushort latency = Latency, ushort timeoutUnits = TimeoutUnits)
    {
        // Only the small, bounded gameplay preset arsenal is accepted here.
        // This is not a general arbitrary-parameter constructor exposed to users.
        if (minUnits < 6 || maxUnits < minUnits || maxUnits > 12 || latency != 0 ||
            timeoutUnits < 200 || timeoutUnits > 600)
            throw new ArgumentOutOfRangeException(nameof(minUnits), "Unsupported bounded BLE preset.");
        source = new ParameterObject(minUnits, maxUnits, latency, timeoutUnits);
        IntPtr pointer = Marshal.GetComInterfaceForObject(source, typeof(ICustomBleParameters));
        try { projected = BluetoothLEPreferredConnectionParameters.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        if (projected.MinConnectionInterval != minUnits ||
            projected.MaxConnectionInterval != maxUnits || projected.ConnectionLatency != latency ||
            projected.LinkTimeout != timeoutUnits)
            throw new InvalidOperationException("WinRT parameter ABI round-trip does not match the selected preset.");
        source.ResetCounters();
    }

    public BluetoothLEPreferredConnectionParameters Parameters => projected;
    public ushort[] ReadBackForOfflineTest() =>
        [projected.LinkTimeout, projected.ConnectionLatency, projected.MinConnectionInterval, projected.MaxConnectionInterval];
#pragma warning restore CA1416
    public string GetterTelemetry => "get_timeout=" + source.TimeoutReads + " get_latency=" + source.LatencyReads +
        " get_min=" + source.MinReads + " get_max=" + source.MaxReads;

    [ComVisible(true), Guid("F2F44344-7372-5F7B-9B34-29C944F5A715"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICustomBleParameters
    {
        [PreserveSig] int GetIids(out uint count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);
        [PreserveSig] int GetLinkTimeout(out ushort value);
        [PreserveSig] int GetConnectionLatency(out ushort value);
        [PreserveSig] int GetMinConnectionInterval(out ushort value);
        [PreserveSig] int GetMaxConnectionInterval(out ushort value);
    }

    [ComVisible(true), Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IParameterInspectable
    {
        [PreserveSig] int GetIids(out uint count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ParameterObject : ICustomBleParameters, IParameterInspectable
    {
        private readonly ushort minUnits, maxUnits, latency, timeoutUnits;
        public ParameterObject(ushort minUnits = IntervalUnits, ushort maxUnits = IntervalUnits,
            ushort latency = Latency, ushort timeoutUnits = TimeoutUnits)
        {
            this.minUnits = minUnits; this.maxUnits = maxUnits;
            this.latency = latency; this.timeoutUnits = timeoutUnits;
        }
        private int timeoutReads, latencyReads, minReads, maxReads;
        public int TimeoutReads => Volatile.Read(ref timeoutReads);
        public int LatencyReads => Volatile.Read(ref latencyReads);
        public int MinReads => Volatile.Read(ref minReads);
        public int MaxReads => Volatile.Read(ref maxReads);
        public void ResetCounters() { timeoutReads = latencyReads = minReads = maxReads = 0; }
        public int GetIids(out uint count, out IntPtr iids)
        {
            count = 1;
            iids = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(ParametersIid, iids, false);
            return 0;
        }
        public int GetRuntimeClassName(out IntPtr name)
        {
            const string runtimeName = "Windows.Devices.Bluetooth.BluetoothLEPreferredConnectionParameters";
            return WindowsCreateString(runtimeName, (uint)runtimeName.Length, out name);
        }
        public int GetTrustLevel(out int level) { level = 0; return 0; }
        public int GetLinkTimeout(out ushort value) { Interlocked.Increment(ref timeoutReads); value = timeoutUnits; return 0; }
        public int GetConnectionLatency(out ushort value) { Interlocked.Increment(ref latencyReads); value = latency; return 0; }
        public int GetMinConnectionInterval(out ushort value) { Interlocked.Increment(ref minReads); value = minUnits; return 0; }
        public int GetMaxConnectionInterval(out ushort value) { Interlocked.Increment(ref maxReads); value = maxUnits; return 0; }
        [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsCreateString(string text, uint length, out IntPtr hstring);
    }
}
