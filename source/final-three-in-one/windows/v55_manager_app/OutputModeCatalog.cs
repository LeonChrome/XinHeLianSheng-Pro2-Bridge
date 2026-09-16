using System;
using System.Collections.Generic;

namespace Y700Switch2V55Manager;

public enum OutputModeId
{
    Unknown,
    Recovery,
    DualSenseStandard,
    Pro2,
    DualSenseLike,
    Xbox,
    MacOs,
    DualPro2Probe,
    XboxElite
}

public sealed record OutputModeProfile(
    string ProfileId,
    OutputModeId ModeId,
    string Label,
    string ExpectedUsbMarker,
    bool ManagerReady,
    string Notes);

public static class OutputModeCatalog
{
    public static readonly OutputModeProfile Recovery = new(
        "hid_only",
        OutputModeId.Recovery,
        "HID 纯恢复",
        "VID_054C&PID_0DF2",
        true,
        "用于安全重刷和 USB 枚举恢复的最小恢复固件。");

    public static readonly OutputModeProfile Pro2 = new(
        "pro2_bridge_v5_5",
        OutputModeId.Pro2,
        "Pro2 / Nintendo",
        "VID_057E&PID_2069",
        true,
        "稳定的 BLE Pro2 到 Nintendo 风格 USB 桥接。");

    public static readonly OutputModeProfile DualSenseStandard = new(
        "hid_audio_uac1_4ch_dualsense",
        OutputModeId.DualSenseStandard,
        "新和联胜 / PS5",
        "VID_054C&PID_0CE6",
        true,
        "标准 DualSense 身份，保留 HD 音频转震动、普通震动调度和 PS5 IMU 路线。");

    public static readonly OutputModeProfile DualSenseLike = new(
        "hid_audio_uac1_4ch_edge",
        OutputModeId.DualSenseLike,
        "新和联胜 / PS5 Edge",
        "VID_054C&PID_0DF2",
        true,
        "DualSense Edge 方向，支持 L4/R4 背键、四声道 HD 震动音频与普通震动共同调度。");

    public static readonly OutputModeProfile Xbox = new(
        "xinput_bridge_v5_8",
        OutputModeId.Xbox,
        "Xbox / XInput",
        "VID_045E&PID_028E",
        true,
        "真实 Xbox 360 / XInput 风格 USB 后端，普通震动回传到 Pro2 BLE。");

    public static readonly OutputModeProfile MacOs = new(
        "macos_hid_bridge_v5_9",
        OutputModeId.MacOs,
        "macOS / 标准 HID",
        "VID_CAFE&PID_4037",
        true,
        "标准 USB HID Gamepad，不依赖 Windows XInput 驱动，可供 macOS 游戏与浏览器识别。");

    public static readonly OutputModeProfile DualPro2Probe = new(
        "dual_pro2_probe_v5_9",
        OutputModeId.DualPro2Probe,
        "Dual Pro2 Probe",
        "VID_057E&PID_2069",
        true,
        "第四实验模式：ESP32-S3 同时连接两只 Pro2，实测 BLE 通知频率、去重率和断连情况。");

    public static IReadOnlyList<OutputModeProfile> All { get; } = new[]
    {
        Recovery,
        DualSenseStandard,
        DualSenseLike,
        Pro2,
        Xbox,
        MacOs,
        DualPro2Probe
    };

    public static OutputModeProfile? FindByProfileId(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        foreach (OutputModeProfile profile in All)
        {
            if (string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }
}
