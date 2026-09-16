using System.IO;

namespace XinHeLianSheng.ControlCenter;

public sealed record BackendDescriptor(
    string Key,
    string Route,
    string Name,
    string Version,
    string Badge,
    string Description,
    string PayloadKey,
    string FileName,
    string Sha256,
    string LogDirectory,
    IReadOnlyList<string> Capabilities,
    string Guidance);

public static class BackendCatalog
{
    private static string LocalData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static readonly BackendDescriptor Pico = new(
        "pico-r40", "Pico 2 W 背挂", "Pico 2 W 一键刷机", "R40 候选",
        "轻量硬件",
        "把 Pro2 的 USB 输入转换为 PS5、PS5 Edge、Xbox 或 Switch 1 无线手柄。",
        "pico-r40",
        "新和联胜-Pico2W一键刷机工具-r40.exe",
        "fae2630d0497ad744c5ee49534247eaae11ca442c4ac3f7460dfb9a42fb748f7",
        Path.Combine(LocalData, "XinHeLianSheng", "Pico2WFlasher", "logs"),
        new[]
        {
            "内置并校验 R40 UF2，无需安装 Pico SDK",
            "默认 PS5 Edge，保留四种无线身份与组合键切换",
            "保留 Pro2 停报后的端点重挂、总线复位和退避恢复",
            "重连等待 BTstack 正常结束 page，避免多次失败后耗尽蓝牙通道",
            "PS5 / Edge 支持兼容马达释放，主机停振后立即停止实体 Pro2",
            "NS1 Pro 使用真实 0x3F 握手流程，再切换到 0x30 完整报告"
        },
        "适合背挂原型和最终轻量硬件。首次刷写需要让 Pico 进入 BOOTSEL。" );

    public static readonly BackendDescriptor Esp = new(
        "esp-v5919", "ESP32-S3 接收板", "ESP32-S3 固件管理", "V5.9.19 候选",
        "高上限",
        "通过 CH343 控制口刷写多模式固件，并由 ESP32-S3 直接桥接 Pro2。",
        "esp-v5919",
        "新和联胜版本-aio-v5.9.19.exe",
        "0c438194dd9b3f1a2e39dbb5843486239c14650e583c5809746832f812b5aeba",
        Path.Combine(LocalData, "PRO2WirelessReceiverControlBoard", "logs"),
        new[]
        {
            "PS5、PS5 Edge、Pro2、Xbox 与 macOS 输入候选路线",
            "保留 COM 口刷写、连接检测、BLE 控制和日志台",
            "输入边沿与模式身份稳定性修订已纳入 V5.9.19",
            "Switch 1 固件仍作为独立实验入口，避免污染稳定候选"
        },
        "刷写时连接 COM 控制口；刷好并完成 Pro2 配对后，日常通常只需原生 USB/OTG 口。" );

    public static readonly BackendDescriptor EspSwitch = new(
        "esp-switch-r4", "ESP32-S3 接收板", "Switch 1 实验固件", "V5.9.18 R4",
        "实验入口",
        "ESP32-S3 的 Switch 1 Pro 身份、连接恢复与普通震动实验分支。",
        "esp-switch-r4",
        "新和联胜版本-aio-v5.9.18-switch1-connect-rumble-test-r4.exe",
        "97d9730d472adac2226d49e068f97234883f0e133e6a337036e4408ad3b0dce7",
        Path.Combine(LocalData, "PRO2WirelessReceiverControlBoard", "logs"),
        new[]
        {
            "Switch 1 Pro USB 身份实验",
            "NimBLE 重连崩溃修正",
            "Switch 震动到 Pro2 普通震动的实验转换",
            "与 V5.9.19 并行保留，当前尚未冒险合并"
        },
        "只在需要连接 Switch 1 主机时使用。它是实验分支，不等同于 V5.9.19 稳定候选。" );

    public static readonly BackendDescriptor Viiper = new(
        "viiper-v6232-r2", "纯软件直连", "VIIPER 无开发板版", "V6.2.32 Test R2",
        "零开发板",
        "Windows 直接通过 BLE 读取 Pro2，再由 VIIPER/USBIP 建立虚拟手柄。",
        "viiper-v6232-r2",
        "新和联胜VIIPER版本-aio-v6.2.32-stick-calibration-test-r2.exe",
        "4025a1cce1a013f84f129d8957076bc51246655428c7207f8dc1a459a8e8a1ee",
        Path.Combine(LocalData, "PRO2WirelessReceiverControlBoard", "v6_logs"),
        new[]
        {
            "无需 ESP32/Pico，Windows BLE central 直接连接 Pro2",
            "PS5、PS5 Edge、Pro2、Xbox 多实例虚拟设备",
            "推送节奏按真实 BLE 输入源驱动，避免重复按键和摇杆瞬移",
            "包含摇杆零位与完整行程校准测试功能"
        },
        "需要已正确安装 USBIP/VIIPER 驱动。Windows 11 的实际 BLE 输入通常约 60–80 Hz。" );

    public static IReadOnlyList<BackendDescriptor> All { get; } =
        new[] { Pico, Esp, EspSwitch, Viiper };
}
