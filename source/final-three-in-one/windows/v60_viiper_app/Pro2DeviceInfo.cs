using System;

namespace Y700Switch2V60Viiper;

// Decode only the firmware reply already requested during initialization.
// Unknown/malformed replies must not invent a version or imply working audio.
public sealed record Pro2FirmwareInfo(string ControllerVersion, byte ControllerType,
    string BluetoothPatchVersion, string? DspVersion)
{
    public static bool TryDecode(ReadOnlySpan<byte> data, out Pro2FirmwareInfo? info)
    {
        info = null;
        if (data.Length < 16 || data[0] != 0x10 || data[1] != 1 || data[2] != 1 ||
            data[3] != 1 || data[5] != 0x78 || !data.Slice(8, 3).ContainsAnyExcept((byte)0xFF)) return false;
        static string Version(ReadOnlySpan<byte> bytes) => $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
        string patch = data.Slice(12, 3).ContainsAnyExcept((byte)0xFF) ? Version(data.Slice(12, 3)) : "未知";
        string? dsp = data.Length >= 19 && data.Slice(16, 3).ContainsAnyExcept((byte)0xFF)
            ? Version(data.Slice(16, 3)) : null;
        info = new(Version(data.Slice(8, 3)), data[11], patch, dsp);
        return true;
    }
}

public static class Pro2DevicePresentation
{
    public static string Firmware(Pro2FirmwareInfo? info, bool connected) =>
        !connected ? "未连接" : info?.ControllerVersion ?? "未知";
    public static string Headset(GamepadState state, bool fresh, bool connected) =>
        !connected ? "未连接" : !fresh || !state.HeadsetPresenceKnown ? "未知" :
            state.HeadsetPresent ? "已插入" : "未插入";
}
