using System;
using System.Collections.Generic;
using System.Linq;

namespace Y700Switch2V60Viiper;

public enum ViiperPushRateMode
{
    SourceAdaptive,
    Hz66,
    Hz125,
    Hz250
}

public enum ViiperGyroMode
{
    Hold250Hz,
    Source60Hz,
    Scaled250Hz,
    Filtered250Hz
}

public enum GyroDirectionMode
{
    Reference,
    InvertHorizontal
}

public readonly record struct GyroAxisInversion(
    bool InvertX,
    bool InvertY,
    bool InvertZ)
{
    public string TelemetryValue =>
        "x" + (InvertX ? "1" : "0") +
        ",y" + (InvertY ? "1" : "0") +
        ",z" + (InvertZ ? "1" : "0");

    public string DisplayValue =>
        "X" + (InvertX ? "-" : "+") +
        " Y" + (InvertY ? "-" : "+") +
        " Z" + (InvertZ ? "-" : "+");
}

public enum ImuAxisSource
{
    X,
    Y,
    Z
}

public readonly record struct ImuAxisMap(
    ImuAxisSource Source,
    bool Invert)
{
    public string DisplayValue => (Invert ? "-" : "+") + Source;
}

public readonly record struct Ps5ImuMapping(
    ImuAxisMap GyroX,
    ImuAxisMap GyroY,
    ImuAxisMap GyroZ,
    ImuAxisMap AccelX,
    ImuAxisMap AccelY,
    ImuAxisMap AccelZ)
{
    public string TelemetryValue =>
        "g=" + GyroX.DisplayValue + "," + GyroY.DisplayValue + "," + GyroZ.DisplayValue +
        ";a=" + AccelX.DisplayValue + "," + AccelY.DisplayValue + "," + AccelZ.DisplayValue;

    public string DisplayValue =>
        "G " + GyroX.DisplayValue + "," + GyroY.DisplayValue + "," + GyroZ.DisplayValue +
        " / A " + AccelX.DisplayValue + "," + AccelY.DisplayValue + "," + AccelZ.DisplayValue;
}

public sealed record Ps5ImuMappingOption(
    string Label,
    Ps5ImuMapping Mapping,
    string Description)
{
    private static ImuAxisMap P(ImuAxisSource source) => new(source, false);
    private static ImuAxisMap N(ImuAxisSource source) => new(source, true);

    public static IReadOnlyList<Ps5ImuMappingOption> All { get; } =
    [
        new(
            "PS5 标准物理映射 G=+X,+Z,-Y A=+X,+Z,-Y / Pro2 4096g -> DS 8192g",
            new Ps5ImuMapping(
                P(ImuAxisSource.X), P(ImuAxisSource.Z), N(ImuAxisSource.Y),
                P(ImuAxisSource.X), P(ImuAxisSource.Z), N(ImuAxisSource.Y)),
            "PS5 / Edge 使用 parser 校准后的 Pro2 输入，按 G=+X,+Z,-Y / A=+X,+Z,-Y 换算为 DualSense HID raw。")
    ];

    public static Ps5ImuMappingOption Default => All[0];

    public static Ps5ImuMappingOption FromLabel(string? label)
    {
        return All.FirstOrDefault(o => string.Equals(o.Label, label, StringComparison.Ordinal)) ?? Default;
    }
}

public readonly record struct Ps5OutputImuTuning(
    double GyroScalePitch,
    double GyroScaleYaw,
    double GyroScaleRoll)
{
    public static Ps5OutputImuTuning Default { get; } = new(1.0, 1.0, 1.0);

    public string TelemetryValue =>
        "accel=pro2_4096g_to_ds8192_+x+z-y;gyro=calibrated_state_to_ds16.384_+x+z-y;scale_pitch=" + GyroScalePitch.ToString("0.###") +
        ",scale_yaw=" + GyroScaleYaw.ToString("0.###") +
        ",scale_roll=" + GyroScaleRoll.ToString("0.###");

    public string DisplayValue =>
        "Gyro " + GyroScalePitch.ToString("0.##") + "x / " +
        GyroScaleYaw.ToString("0.##") + "x / " +
        GyroScaleRoll.ToString("0.##") + "x";

    public Ps5OutputImuTuning Normalize() => new(
        V60UserSettings.NormalizePs5GyroScale(GyroScalePitch),
        V60UserSettings.NormalizePs5GyroScale(GyroScaleYaw),
        V60UserSettings.NormalizePs5GyroScale(GyroScaleRoll));
}

public enum VirtualBackendMode
{
    ViiperServer,
    LibViiperExperimental,
    EmbeddedUsbipExperimental
}

public enum StickProcessingMode
{
    RawDirect,
    StabilityGuard
}

public sealed record ViiperPushRateOption(
    ViiperPushRateMode Mode,
    string Label,
    double Hz,
    TimeSpan Interval,
    bool SourcePaced)
{
    public static IReadOnlyList<ViiperPushRateOption> All { get; } =
    [
        new(
            ViiperPushRateMode.SourceAdaptive,
            "跟随真实 Pro2 BLE（推荐）",
            0.0,
            TimeSpan.FromMilliseconds(4),
            true),
        new(ViiperPushRateMode.Hz66, "固定 66Hz（诊断）", 1000.0 / 15.0, TimeSpan.FromMilliseconds(15), false),
        new(ViiperPushRateMode.Hz125, "固定 125Hz（诊断）", 125.0, TimeSpan.FromMilliseconds(8), false),
        new(ViiperPushRateMode.Hz250, "固定 250Hz（诊断）", 250.0, TimeSpan.FromMilliseconds(4), false)
    ];

    public static ViiperPushRateOption Default => All[0];

    public static ViiperPushRateOption FromLabel(string? label)
    {
        if (string.Equals(label, "125Hz（推荐）", StringComparison.Ordinal) ||
            string.Equals(label, "66Hz / source-paced", StringComparison.Ordinal) ||
            string.Equals(label, "250Hz（高性能）", StringComparison.Ordinal))
        {
            return Default;
        }

        return All.FirstOrDefault(o => string.Equals(o.Label, label, StringComparison.Ordinal)) ?? Default;
    }
}

public sealed record ViiperGyroModeOption(
    ViiperGyroMode Mode,
    string Label,
    string Description)
{
    public static IReadOnlyList<ViiperGyroModeOption> All { get; } =
    [
        new(
            ViiperGyroMode.Hold250Hz,
            "hold_latest（推荐）",
            "BLE 仍按真实采样率更新；USB 每帧零阶保持 latest gyro/accel，避免重复帧把 motion 清零。"),
        new(
            ViiperGyroMode.Source60Hz,
            "source_60hz_zero（诊断）",
            "仅在 BLE 新样本到达时输出 IMU，重复帧清空 gyro/accel；用于复现旧版不丝滑问题。"),
        new(
            ViiperGyroMode.Filtered250Hz,
            "filtered_hold（实验）",
            "在 latest-hold 基础上对 gyro/accel 做低通，降低噪声但会增加一点延迟。"),
        new(
            ViiperGyroMode.Scaled250Hz,
            "scaled_250hz（实验）",
            "重复高刷新时按 BLE/Push 比例缩放 gyro；只用于 A/B 验证，不作为默认手感路径。")
    ];

    public static ViiperGyroModeOption Default => All[0];

    public static ViiperGyroModeOption FromLabel(string? label)
    {
        if (string.Equals(label, "source_60hz（推荐）", StringComparison.Ordinal))
        {
            return Default;
        }

        if (string.Equals(label, "hold_250hz", StringComparison.Ordinal))
        {
            return All.First(o => o.Mode == ViiperGyroMode.Hold250Hz);
        }

        return All.FirstOrDefault(o => string.Equals(o.Label, label, StringComparison.Ordinal)) ?? Default;
    }
}

public sealed record GyroDirectionOption(
    GyroDirectionMode Mode,
    string Label,
    string Description)
{
    public static IReadOnlyList<GyroDirectionOption> All { get; } =
    [
        new(
            GyroDirectionMode.Reference,
            "标准方向（推荐）",
            "参考 VIIPER / Tommy / JSL 的 report-space 映射；适合已确认方向正常的机器与多数游戏。"),
        new(
            GyroDirectionMode.InvertHorizontal,
            "左右反向修正",
            "仅当游戏内左右陀螺仪相反时使用；只翻水平 gyro/Yaw，不改变加速度和垂直轴。")
    ];

    public static GyroDirectionOption Default => All[0];

    public static GyroDirectionOption FromLabel(string? label)
    {
        return All.FirstOrDefault(o => string.Equals(o.Label, label, StringComparison.Ordinal)) ?? Default;
    }
}

public sealed record VirtualBackendOption(
    VirtualBackendMode Mode,
    string Label,
    string Description)
{
    public static IReadOnlyList<VirtualBackendOption> All { get; } =
    [
        new(
            VirtualBackendMode.ViiperServer,
            "VIIPER server（稳定）",
            "当前稳定三模路径：内置 VIIPER 进程 + usbip-win2，保留 PS5 HD haptic。"),
        new(
            VirtualBackendMode.LibViiperExperimental,
            "libVIIPER（实验）",
            "V6.2 短链路目标：同进程 libVIIPER；需要 libVIIPER.dll 与 GPL 兼容发布策略。"),
        new(
            VirtualBackendMode.EmbeddedUsbipExperimental,
            "Embedded USBIP（研究）",
            "V6.2 研究目标：C# 内嵌 USBIP server；仍需要 usbip-win2 内核驱动。")
    ];

    public static VirtualBackendOption Default => All[0];

    public static VirtualBackendOption FromLabel(string? label)
    {
        return All.FirstOrDefault(o => string.Equals(o.Label, label, StringComparison.Ordinal)) ?? Default;
    }
}

public sealed record StickProcessingOption(
    StickProcessingMode Mode,
    string Label,
    string Description)
{
    public static IReadOnlyList<StickProcessingOption> All { get; } =
    [
        new(
            StickProcessingMode.RawDirect,
            "Raw Direct（推荐）",
            "真实 Pro2 摇杆原始值直通到虚拟手柄，不做 hold/ramp/filter，最低延迟。"),
        new(
            StickProcessingMode.StabilityGuard,
            "Stability Guard（诊断）",
            "仅在怀疑真实 BLE 轴值坏跳时使用，会记录并短暂保护可疑单帧尖峰。")
    ];

    public static StickProcessingOption Default => All[0];

    public static StickProcessingOption FromLabel(string? label)
    {
        return All.FirstOrDefault(o => string.Equals(o.Label, label, StringComparison.Ordinal)) ?? Default;
    }
}
