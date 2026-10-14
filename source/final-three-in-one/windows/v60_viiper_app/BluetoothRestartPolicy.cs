using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Y700Switch2V60Viiper;

public sealed record BluetoothRestartTarget(string Name, string PnpId, string InterfaceId,
    string Provider, string DriverVersion, bool IsDefault)
{
    public string DisplayName => Name + (IsDefault ? "（系统默认）" : "");
    public string DriverSummary => Provider + " · " + DriverVersion;
    public bool CanRestart => BluetoothRestartPolicy.IsExactRadioTarget(this);
}

public static class BluetoothRestartPolicy
{
    private static readonly Guid RadioInterface = new("92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2");
    public static IReadOnlyList<BluetoothRestartTarget> Targets(SystemBluetoothSnapshot snapshot) => snapshot.Adapters
        .Select(r => new BluetoothRestartTarget(r.Driver.Name, r.Driver.PnpDeviceId, r.Driver.WinRtDeviceId,
            r.Driver.DriverProvider, r.Driver.DriverVersion, r.IsDefault))
        .GroupBy(r => r.InterfaceId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
        .OrderByDescending(r => r.IsDefault).ThenBy(r => r.Name).ToArray();
    public static bool IsExactRadioTarget(BluetoothRestartTarget target)
    {
        string id = target.InterfaceId;
        if (id.Length > 4096 || !id.StartsWith(@"\\?\", StringComparison.Ordinal) || target.PnpId.Length > 512 ||
            target.PnpId.Any(c => char.IsControl(c) || c is '"' or '*' or '?' or '/') ||
            target.PnpId.Split('\\').Length != 3) return false;
        string[] parts = id[4..].Split('#');
        if (parts.Length != 4 || parts.Take(3).Any(string.IsNullOrWhiteSpace) ||
            !Guid.TryParse(parts[3], out Guid guid) || guid != RadioInterface) return false;
        string bus = parts[0];
        if (bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
            new[] { "SWD", "ROOT", "HTREE", "HID" }.Contains(bus, StringComparer.OrdinalIgnoreCase)) return false;
        return string.Equals(string.Join("\\", parts.Take(3)), target.PnpId, StringComparison.OrdinalIgnoreCase);
    }
    public static string[] RestartArguments(BluetoothRestartTarget target)
    {
        if (!IsExactRadioTarget(target)) throw new ArgumentException("无法核实所选蓝牙适配器，未重启任何设备。");
        return new[] { "/restart-device", target.PnpId }; // Exact instance only. Never /deviceid, class, bus, force or reboot.
    }
    public static bool IsVerifiedSuccess(int exitCode, bool presentStarted, bool parametersUnchanged, bool driverUnchanged) =>
        exitCode == 0 && presentStarted && parametersUnchanged && driverUnchanged;
    public static string DecodeNativeOutput(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length > 65536) throw new ArgumentException("设备工具输出过长。");
        bool utf16 = bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE ||
            bytes.Length > 3 && Enumerable.Range(0, Math.Min(bytes.Length / 2, 128)).Count(i => bytes[i * 2 + 1] == 0) > Math.Min(bytes.Length / 2, 128) / 3;
        if (utf16) return Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF');
        if (bytes.Length > 1 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes).TrimStart('\uFEFF');
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes);
        }
    }
    public static string CompletionMessage(int? exitCode, bool verified) => verified
        ? "蓝牙已重启。请唤醒 Pro2，再点击“连接 Pro2”；输入频率需重新测量。"
        : exitCode == 3010 ? "蓝牙热重启未完成。请保存工作后重启 Windows。"
        : "未确认蓝牙重启成功。请查看诊断记录；程序不会强制禁用设备或重启 Windows。";
}
