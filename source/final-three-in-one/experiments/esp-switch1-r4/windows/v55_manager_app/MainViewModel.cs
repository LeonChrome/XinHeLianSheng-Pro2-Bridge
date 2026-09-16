using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Y700Switch2V55Manager;

public sealed record BleScanItem(string Target, string Address, string Name, int Rssi, bool Candidate)
{
    public string DisplayName =>
        Target + "  " + Address + "  " + Name + "  RSSI=" + Rssi + (Candidate ? "  候选" : "");
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private enum DeviceUiMode
    {
        Unknown,
        DualSense,
        Pro2,
        DualPro2Probe,
        Switch1Pro,
        Xbox,
        XboxElite,
        Recovery
    }

    private readonly Window owner;
    private readonly FirmwareFlasher flasher = new();
    private readonly ManagerSettings settings = ManagerSettingsStore.Load();
    private readonly StringBuilder log = new();
    private const int UiLogTrimThreshold = 180000;
    private const int UiLogRetainedChars = 100000;
    private const int UiLogMaxLineChars = 4096;
    private readonly object logSync = new();
    private readonly SemaphoreSlim serialLock = new(1, 1);
    private readonly SemaphoreSlim flashLock = new(1, 1);
    private readonly DispatcherTimer stateTimer = new();
    private readonly DispatcherTimer logUiTimer = new();
    private CancellationTokenSource? gameMonitorCts;
    private StreamWriter? diagnosticWriter;
    private string diagnosticLogPath = "";
    private readonly HashSet<string> firmwareCriticalLinesSeen =
        new(StringComparer.Ordinal);
    private bool stateRefreshInProgress;
    private DateTime nextUsbAutoCheck = DateTime.MinValue;
    private DateTime nextSerialAutoProbeAt = DateTime.MinValue;

    private PortItem? selectedPort;
    private string portStatus = "尚未检测到 ESP32 串口。请连接 CH343P/WCH 控制板，或手动选择正确的 COM 口。";
    private string usbStatus = "尚未执行 USB 检查。";
    private string audioStatus = "尚未执行音频检查。";
    private string bleStatus = "尚未读取 BLE 状态。";
    private string bleCandidates = "尚未加载 BLE 候选列表。";
    private string hapticStatus = "默认状态：实时转发已关闭，试运行已开启。";
    private string nextAction = "请先从五模式烧录台选择目标模式。即使没有连接 ESP32，程序也会保持离线可操作。";
    private string overallStatus = "就绪";
    private string modeSwitchStatus = "尚未执行模式切换。";
    private string customCommand = "status";
    private string bleTarget = "";
    private string audioPattern = "both_tick";
    private string audioIntensity = "48";
    private string audioDurationMs = "600";
    private string audioDeviceName = "Wireless Controller";
    private string gameMonitorSeconds = "900";
    private string xInputProbeSeconds = "8";
    private string xInputProbeLow = "32000";
    private string xInputProbeHigh = "52000";
    private bool xboxPaddleLeftEnabled;
    private bool xboxPaddleRightEnabled;
    private string xboxPaddleLeftMode = "hold";
    private string xboxPaddleRightMode = "hold";
    private string xboxPaddleLeftTargets = "B";
    private string xboxPaddleRightTargets = "A";
    private string xboxPaddleLeftTapMs = "70";
    private string xboxPaddleRightTapMs = "70";
    private string xboxPaddleLeftTurboOnMs = "45";
    private string xboxPaddleLeftTurboOffMs = "45";
    private string xboxPaddleRightTurboOnMs = "45";
    private string xboxPaddleRightTurboOffMs = "45";
    private string pro2RumbleScale = "140";
    private string pro2RumbleHoldMs = "220";
    private string pro2RumbleTickMs = "12";
    private string pro2RumbleStopPackets = "3";
    private string monitorStatus = "诊断未启动。开始后会持续记录 BLE、USB、HID、音频、震动和 Windows PnP 状态，并自动保存到日志文件。";
    private string xboxStatus = "Xbox / XInput 模式会枚举为 045E:028E，并将普通双马达震动回传到真实 Pro2。";
    private string pro2RumbleStatus = "这里提供 Pro2 / Nintendo 固件的普通震动自检。先确认 BLE 已连接，再做轻震、重震和停止。";
    private string dualNs2ProHostStatus = "主机侧双二代 Pro2 未启动。这个入口会用 VIIPER/USBIP 创建两个独立 057E:2069 设备地址。";
    private Process? dualNs2ProHostProcess;
    private string? dualNs2ProHostStopFile;
    private DeviceUiMode currentMode = DeviceUiMode.Unknown;
    private OutputModeId desiredMode = OutputModeId.Pro2;
    private bool usbDetected;
    private bool bleConnected;
    private string bleTransportState = "unknown";
    private bool bleInputHealthy;
    private string bleSavedTarget = "";
    private bool serialBoardReady;
    private DeviceUiMode serialReportedMode = DeviceUiMode.Unknown;
    private string lastExternalViiperConflict = "";
    private bool desiredModeAutoAligned;
    private bool busy;
    private bool flashInProgress;
    private bool gameMonitorRunning;
    private bool shutdownStarted;
    private bool logUiDirty;
    private Task? initializeTask;
    private BleScanItem? selectedBleDevice;
    private bool? lastFlashSucceeded;
    private string lastFlashProfileId = "";
    private string lastFlashFailureCategory = "";

    public ObservableCollection<PortItem> Ports { get; } = new();
    public ObservableCollection<BleScanItem> BleDevices { get; } = new();
    public ObservableCollection<string> AudioPatterns { get; } = new(new[]
    {
        "ch2_tick", "ch3_tick", "both_tick", "ch2_punch", "ch3_punch", "both_punch", "texture", "continuous", "sweep", "silence"
    });
    public ObservableCollection<string> XboxPaddleModes { get; } = new(new[]
    {
        "hold", "tap", "turbo"
    });

    public PortItem? SelectedPort
    {
        get => selectedPort;
        set
        {
            string oldPort = selectedPort?.PortName ?? "";
            string newPort = value?.PortName ?? "";
            if (!string.Equals(oldPort, newPort, StringComparison.OrdinalIgnoreCase))
            {
                SerialCommandClient.CloseInBackground();
                serialBoardReady = false;
                serialReportedMode = DeviceUiMode.Unknown;
                nextSerialAutoProbeAt = DateTime.UtcNow;
            }
            selectedPort = value;
            settings.LastPortName = value?.PortName ?? "";
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUsableSerialCandidate));
            OnPropertyChanged(nameof(CanUseBleButtons));
            OnPropertyChanged(nameof(CanUsePro2ToolButtons));
            OnPropertyChanged(nameof(CanUseDualSenseToolButtons));
            OnPropertyChanged(nameof(CanUseXboxToolButtons));
            OnPropertyChanged(nameof(CanUseXboxPaddleButtons));
            OnPropertyChanged(nameof(CanUseMonitorButtons));
            OnPropertyChanged(nameof(CanStartMonitorButton));
            OnPropertyChanged(nameof(CanStopMonitorButton));
            OnPropertyChanged(nameof(CanUseAudioPatternButton));
            OnPropertyChanged(nameof(CanSendCustomSerialCommand));
            OnPropertyChanged(nameof(CanRepairCh343Driver));
            OnPropertyChanged(nameof(DualSenseToolStateText));
            OnPropertyChanged(nameof(Pro2ToolStateText));
        }
    }
    public string PortStatus { get => portStatus; set { portStatus = value; OnPropertyChanged(); } }
    public string UsbStatus { get => usbStatus; set { usbStatus = value; OnPropertyChanged(); } }
    public string AudioStatus { get => audioStatus; set { audioStatus = value; OnPropertyChanged(); } }
    public string BleStatus { get => bleStatus; set { bleStatus = value; OnPropertyChanged(); } }
    public string BleCandidates { get => bleCandidates; set { bleCandidates = value; OnPropertyChanged(); } }
    public string HapticStatus { get => hapticStatus; set { hapticStatus = value; OnPropertyChanged(); } }
    public string NextAction { get => nextAction; set { nextAction = value; OnPropertyChanged(); } }
    public string ModeSwitchStatus { get => modeSwitchStatus; set { modeSwitchStatus = value; OnPropertyChanged(); } }
    public string OverallStatus
    {
        get => overallStatus;
        set
        {
            overallStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverallBrush));
            OnPropertyChanged(nameof(OverallStatusBackgroundBrush));
            OnPropertyChanged(nameof(OverallStatusForegroundBrush));
            NotifyModeStateChanged();
        }
    }
    public string CustomCommand { get => customCommand; set { customCommand = value; OnPropertyChanged(); } }
    public string BleTarget
    {
        get => bleTarget;
        set
        {
            bleTarget = value;
            settings.LastBleTarget = value ?? "";
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public BleScanItem? SelectedBleDevice
    {
        get => selectedBleDevice;
        set
        {
            selectedBleDevice = value;
            OnPropertyChanged();
            if (value != null)
            {
                BleTarget = !string.IsNullOrWhiteSpace(value.Target) ? value.Target : value.Address;
            }
        }
    }
    public string AudioPattern { get => audioPattern; set { audioPattern = value; OnPropertyChanged(); } }
    public string AudioIntensity { get => audioIntensity; set { audioIntensity = value; OnPropertyChanged(); } }
    public string AudioDurationMs { get => audioDurationMs; set { audioDurationMs = value; OnPropertyChanged(); } }
    public string AudioDeviceName
    {
        get => audioDeviceName;
        set
        {
            audioDeviceName = value;
            settings.LastAudioDeviceName = value ?? "";
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string GameMonitorSeconds { get => gameMonitorSeconds; set { gameMonitorSeconds = value; OnPropertyChanged(); } }
    public string XInputProbeSeconds { get => xInputProbeSeconds; set { xInputProbeSeconds = value; OnPropertyChanged(); } }
    public string XInputProbeLow { get => xInputProbeLow; set { xInputProbeLow = value; OnPropertyChanged(); } }
    public string XInputProbeHigh { get => xInputProbeHigh; set { xInputProbeHigh = value; OnPropertyChanged(); } }
    public bool XboxPaddleLeftEnabled
    {
        get => xboxPaddleLeftEnabled;
        set
        {
            xboxPaddleLeftEnabled = value;
            settings.XboxPaddleLeftEnabled = value;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public bool XboxPaddleRightEnabled
    {
        get => xboxPaddleRightEnabled;
        set
        {
            xboxPaddleRightEnabled = value;
            settings.XboxPaddleRightEnabled = value;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleLeftMode
    {
        get => xboxPaddleLeftMode;
        set
        {
            xboxPaddleLeftMode = NormalizeXboxPaddleMode(value);
            settings.XboxPaddleLeftMode = xboxPaddleLeftMode;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleRightMode
    {
        get => xboxPaddleRightMode;
        set
        {
            xboxPaddleRightMode = NormalizeXboxPaddleMode(value);
            settings.XboxPaddleRightMode = xboxPaddleRightMode;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleLeftTargets
    {
        get => xboxPaddleLeftTargets;
        set
        {
            xboxPaddleLeftTargets = value ?? "";
            settings.XboxPaddleLeftTargets = xboxPaddleLeftTargets;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleRightTargets
    {
        get => xboxPaddleRightTargets;
        set
        {
            xboxPaddleRightTargets = value ?? "";
            settings.XboxPaddleRightTargets = xboxPaddleRightTargets;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleLeftTapMs
    {
        get => xboxPaddleLeftTapMs;
        set
        {
            xboxPaddleLeftTapMs = value ?? "";
            settings.XboxPaddleLeftTapMs = xboxPaddleLeftTapMs;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleRightTapMs
    {
        get => xboxPaddleRightTapMs;
        set
        {
            xboxPaddleRightTapMs = value ?? "";
            settings.XboxPaddleRightTapMs = xboxPaddleRightTapMs;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleLeftTurboOnMs
    {
        get => xboxPaddleLeftTurboOnMs;
        set
        {
            xboxPaddleLeftTurboOnMs = value ?? "";
            settings.XboxPaddleLeftTurboOnMs = xboxPaddleLeftTurboOnMs;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleLeftTurboOffMs
    {
        get => xboxPaddleLeftTurboOffMs;
        set
        {
            xboxPaddleLeftTurboOffMs = value ?? "";
            settings.XboxPaddleLeftTurboOffMs = xboxPaddleLeftTurboOffMs;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleRightTurboOnMs
    {
        get => xboxPaddleRightTurboOnMs;
        set
        {
            xboxPaddleRightTurboOnMs = value ?? "";
            settings.XboxPaddleRightTurboOnMs = xboxPaddleRightTurboOnMs;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string XboxPaddleRightTurboOffMs
    {
        get => xboxPaddleRightTurboOffMs;
        set
        {
            xboxPaddleRightTurboOffMs = value ?? "";
            settings.XboxPaddleRightTurboOffMs = xboxPaddleRightTurboOffMs;
            ManagerSettingsStore.Save(settings);
            OnPropertyChanged();
        }
    }
    public string Pro2RumbleScale { get => pro2RumbleScale; set { pro2RumbleScale = value; OnPropertyChanged(); } }
    public string Pro2RumbleHoldMs { get => pro2RumbleHoldMs; set { pro2RumbleHoldMs = value; OnPropertyChanged(); } }
    public string Pro2RumbleTickMs { get => pro2RumbleTickMs; set { pro2RumbleTickMs = value; OnPropertyChanged(); } }
    public string Pro2RumbleStopPackets { get => pro2RumbleStopPackets; set { pro2RumbleStopPackets = value; OnPropertyChanged(); } }
    public string MonitorStatus { get => monitorStatus; set { monitorStatus = value; OnPropertyChanged(); } }
    public string XboxStatus { get => xboxStatus; set { xboxStatus = value; OnPropertyChanged(); } }
    public string Pro2RumbleStatus { get => pro2RumbleStatus; set { pro2RumbleStatus = value; OnPropertyChanged(); } }
    public string DualNs2ProHostStatus { get => dualNs2ProHostStatus; set { dualNs2ProHostStatus = value; OnPropertyChanged(); } }
    public bool Busy
    {
        get => busy;
        set
        {
            busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverallBrush));
            OnPropertyChanged(nameof(OverallStatusBackgroundBrush));
            OnPropertyChanged(nameof(OverallStatusForegroundBrush));
            NotifyModeStateChanged();
        }
    }
    public bool GameMonitorRunning
    {
        get => gameMonitorRunning;
        set
        {
            gameMonitorRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverallBrush));
            OnPropertyChanged(nameof(OverallStatusBackgroundBrush));
            OnPropertyChanged(nameof(OverallStatusForegroundBrush));
            NotifyModeStateChanged();
        }
    }

    public string FirmwareSummary => "V5.9.18 Switch 1 连接与震动测试 r4 内置五个烧录模式；修复 NimBLE 扫描取消竞态、无限重连和模式误判。";
    public string SafetySummary => "Live 转发默认不自动开启。游戏监听会保持 HD-only 过滤，普通 PCM 只计入 blocked_pcm，不会被盲目推送。";
    public string LogText
    {
        get
        {
            lock (logSync)
            {
                return log.ToString();
            }
        }
    }
    public Brush OverallBrush => Busy || GameMonitorRunning
        ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
        : OverallStatus.Contains("错误", StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.FromRgb(248, 113, 113))
            : OverallStatus.Contains("离线", StringComparison.OrdinalIgnoreCase) || OverallStatus.Contains("预留", StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(148, 163, 184))
                : new SolidColorBrush(Color.FromRgb(74, 222, 128));
    public Brush OverallStatusBackgroundBrush => Busy || GameMonitorRunning
        ? new SolidColorBrush(Color.FromRgb(146, 64, 14))
        : OverallStatus.Contains("错误", StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.FromRgb(153, 27, 27))
            : OverallStatus.Contains("离线", StringComparison.OrdinalIgnoreCase) || OverallStatus.Contains("预留", StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(51, 65, 85))
                : new SolidColorBrush(Color.FromRgb(22, 101, 52));
    public Brush OverallStatusForegroundBrush => Brushes.White;
    public bool HasUsableSerialCandidate => SelectedPort != null && SelectedPort.CanOpen;
    public bool IsDualSenseToolsEnabled => IsPs5FamilyMode(desiredMode);
    public bool IsPro2ToolsEnabled => desiredMode == OutputModeId.Pro2;
    public bool IsXboxToolsEnabled => IsXboxLikeMode(desiredMode);
    public bool IsUnknownMode => currentMode == DeviceUiMode.Unknown || currentMode == DeviceUiMode.Recovery;
    public bool CanSwitchModes => !Busy && !flashInProgress && !GameMonitorRunning;
    public bool CanUseBleButtons => HasUsableSerialCandidate && !Busy && !flashInProgress && !GameMonitorRunning;
    public bool CanUsePro2ToolButtons => HasUsableSerialCandidate && desiredMode == OutputModeId.Pro2 && currentMode == DeviceUiMode.Pro2 && !Busy && !flashInProgress && !GameMonitorRunning;
    public bool CanUseDualSenseToolButtons => HasUsableSerialCandidate && IsPs5FamilyMode(desiredMode) && currentMode == DeviceUiMode.DualSense && !Busy && !flashInProgress && !GameMonitorRunning;
    public bool CanUseXboxToolButtons => HasUsableSerialCandidate &&
        desiredMode == OutputModeId.Xbox && currentMode == DeviceUiMode.Xbox &&
        !Busy && !flashInProgress && !GameMonitorRunning;
    public bool CanUseXboxPaddleButtons => CanUseXboxToolButtons;
    public bool CanUseDualProbeButtons => HasUsableSerialCandidate &&
        desiredMode == OutputModeId.DualPro2Probe &&
        !Busy && !flashInProgress && !GameMonitorRunning;
    public bool DualNs2ProHostRunning => dualNs2ProHostProcess is { HasExited: false };
    public bool CanStartDualNs2ProHost => !Busy && !flashInProgress && !GameMonitorRunning && !DualNs2ProHostRunning;
    public bool CanStopDualNs2ProHost => DualNs2ProHostRunning;
    public bool CanUseMonitorButtons => HasUsableSerialCandidate && IsPs5FamilyMode(desiredMode);
    public bool CanStartMonitorButton => HasUsableSerialCandidate && IsPs5FamilyMode(desiredMode) && currentMode == DeviceUiMode.DualSense && !Busy && !flashInProgress && !GameMonitorRunning;
    public bool CanStopMonitorButton => HasUsableSerialCandidate && IsPs5FamilyMode(desiredMode) && GameMonitorRunning;
    public bool CanUseAudioPatternButton => HasUsableSerialCandidate && IsPs5FamilyMode(desiredMode) && currentMode == DeviceUiMode.DualSense && !Busy && !flashInProgress && !GameMonitorRunning;
    public bool CanSendCustomSerialCommand => HasUsableSerialCandidate && !Busy && !flashInProgress && !GameMonitorRunning;
    public bool CanRepairCh343Driver => HasUsableSerialCandidate && !Busy && !flashInProgress && !GameMonitorRunning;
    public string DesiredModeLabel => GetModeLabel(desiredMode);
    public string DesiredModeDescription => GetModeDescription(desiredMode);
    public string ModeDeckHint => desiredMode switch
    {
        OutputModeId.DualSenseStandard => "点击卡片会刷入标准 PS5 / DualSense 身份。刷写后管理器会等待 054C:0CE6 重新枚举。",
        OutputModeId.DualSenseLike => "点击卡片会刷入 PS5 Edge / DualSense Edge 身份。刷写后管理器会等待 054C:0DF2 重新枚举。",
        OutputModeId.Pro2 => "点击卡片可切换到 Pro2 / Nintendo 模式。这是当前最稳定的原始 HID 0x02 震动优先路线。",
        OutputModeId.Switch1Pro => "刷入 Switch 1 Pro USB 身份 057E:2009。连接初代 Switch 前，请在主机开启 Pro Controller Wired Communication。",
        OutputModeId.Xbox => "点击卡片可切换到 Xbox / XInput 模式。刷写后 USB 应枚举为 045E:028E，普通震动会回传到 Pro2。",
        OutputModeId.DualPro2Probe => "Dual Pro2 Probe 是高级探针，不属于四个日常烧录模式。它只用于验证 ESP32-S3 双 BLE 吞吐与稳定性。",
        _ => "点击五个模式卡片即可烧录对应固件；如果校验失败，界面会保留回退提示。"
    };
    public string CurrentModeLabel => currentMode switch
    {
        DeviceUiMode.DualSense => "新和联胜 / PS5 系模式",
        DeviceUiMode.Pro2 => "Pro2 / Nintendo 模式",
        DeviceUiMode.Switch1Pro => "Switch 1 / Pro 有线转无线模式",
        DeviceUiMode.DualPro2Probe => "Dual Pro2 Probe 模式",
        DeviceUiMode.Xbox => "Xbox / XInput 模式",
        DeviceUiMode.XboxElite => "旧版 Xbox Elite 2 实验固件",
        DeviceUiMode.Recovery => "HID 纯恢复模式",
        _ => "USB 模式未知"
    };
    public string CurrentModeDescription => currentMode switch
    {
        DeviceUiMode.DualSense => "USB 当前已枚举为 PS5 / PS5 Edge 家族，可使用新和联胜 HD 震动、普通震动调度与 PS5 IMU 路线。",
        DeviceUiMode.Pro2 => "USB 当前已枚举为 Pro2 / Nintendo，适合稳定输入和原始/普通震动测试。",
        DeviceUiMode.Switch1Pro => "USB 当前已枚举为初代 Switch Pro Controller 057E:2009。输入、三帧 IMU 与震动由真实 Pro2 BLE 桥接。",
        DeviceUiMode.DualPro2Probe => "串口状态确认当前是 Dual Pro2 Probe。USB 仍复用 Pro2/Nintendo 身份，重点观察双 BLE 能力指标。",
        DeviceUiMode.Xbox => "USB 当前已枚举为 Xbox / XInput，适合 Steam、Apex 和普通双马达震动兼容性测试。",
        DeviceUiMode.XboxElite => "检测到旧版 Elite 2 实验固件。V5.9.17 不再提供这个模式，请切换到新和联胜或 Xbox。",
        DeviceUiMode.Recovery => "当前看起来是最小化 HID 恢复固件，用于救援重刷和枚举恢复。",
        _ => "请先执行 USB 检查。在确认模式前，所有真实震动发送都会保持保守策略。"
    };
    public string UsbLightText => usbDetected
        ? CurrentModeLabel
        : currentMode == DeviceUiMode.Unknown ? "USB 未检测到" : "USB 未校验：" + CurrentModeLabel;
    public string BleLightText => bleConnected ? "BLE 已连接" : "BLE 离线";
    public string DualSenseToolStateText => DescribeToolState(
        desiredMode,
        currentMode == DeviceUiMode.DualSense,
        "当前 USB 身份支持新和联胜的 PS5 HID、普通震动与四声道 HD 震动链路。",
        "已选 PS5 系面板，但 USB 还没有切到对应 PS5 身份。");
    public string Pro2ToolStateText => DescribeToolState(
        OutputModeId.Pro2,
        currentMode == DeviceUiMode.Pro2,
        "当前 USB 身份就是 Pro2 / Nintendo 原始 0x02 + 普通兼容震动桥接。",
        "已选 Pro2 / Nintendo 面板，但 USB 还没有切到 Pro2 / Nintendo。");
    public string DualSenseCardStateText => GetModeStateText(OutputModeId.DualSenseStandard, managerReady: true);
    public string DualSenseEdgeCardStateText => GetModeStateText(OutputModeId.DualSenseLike, managerReady: true);
    public string Pro2CardStateText => GetModeStateText(OutputModeId.Pro2, managerReady: true);
    public string XboxCardStateText => GetModeStateText(OutputModeId.Xbox, managerReady: true);
    public string Switch1ProCardStateText => GetModeStateText(OutputModeId.Switch1Pro, managerReady: true);
    public string DualPro2ProbeCardStateText => GetModeStateText(OutputModeId.DualPro2Probe, managerReady: true);
    public string DualSenseCardTooltip => "点击刷入新和联胜 / PS5 标准 DualSense 模式";
    public string DualSenseEdgeCardTooltip => "点击刷入新和联胜 / PS5 Edge 模式";
    public string Pro2CardTooltip => "点击切换到 Pro2 / Nintendo 模式";
    public string XboxCardTooltip => "点击切换到 Xbox / XInput 模式";
    public string Switch1ProCardTooltip => "点击刷入 Switch 1 / Pro 有线转无线测试模式";
    public string DualPro2ProbeCardTooltip => "点击刷入 Dual Pro2 BLE 能力探针";
    public Brush UsbIndicatorBrush => currentMode switch
    {
        DeviceUiMode.DualSense => new SolidColorBrush(Color.FromRgb(29, 78, 216)),
        DeviceUiMode.Pro2 => new SolidColorBrush(Color.FromRgb(190, 24, 93)),
        DeviceUiMode.Switch1Pro => new SolidColorBrush(Color.FromRgb(234, 88, 12)),
        DeviceUiMode.DualPro2Probe => new SolidColorBrush(Color.FromRgb(126, 34, 206)),
        DeviceUiMode.Xbox => new SolidColorBrush(Color.FromRgb(22, 101, 52)),
        DeviceUiMode.XboxElite => new SolidColorBrush(Color.FromRgb(21, 128, 61)),
        DeviceUiMode.Recovery => new SolidColorBrush(Color.FromRgb(71, 85, 105)),
        _ => new SolidColorBrush(Color.FromRgb(51, 65, 85))
    };
    public Brush BleIndicatorBrush => bleConnected ? Brushes.White : Brushes.Gray;
    public string BleDisplayText => bleTransportState switch
    {
        "connected" when bleInputHealthy => "BLE 已连接",
        "connected" => "BLE 已连接 / 输入不新鲜",
        "connecting" => "BLE 连接中",
        "scanning" => "BLE 扫描中",
        _ => "BLE 离线"
    };
    public Brush BleDisplayBrush => bleTransportState switch
    {
        "connected" when bleInputHealthy => Brushes.White,
        "connected" => new SolidColorBrush(Color.FromRgb(253, 186, 116)),
        "connecting" => new SolidColorBrush(Color.FromRgb(253, 224, 71)),
        "scanning" => new SolidColorBrush(Color.FromRgb(253, 224, 71)),
        _ => new SolidColorBrush(Color.FromRgb(203, 213, 225))
    };
    public Brush BleDisplayBackgroundBrush => bleTransportState switch
    {
        "connected" when bleInputHealthy => new SolidColorBrush(Color.FromRgb(15, 118, 110)),
        "connected" => new SolidColorBrush(Color.FromRgb(146, 64, 14)),
        "connecting" => new SolidColorBrush(Color.FromRgb(113, 63, 18)),
        "scanning" => new SolidColorBrush(Color.FromRgb(120, 53, 15)),
        _ => new SolidColorBrush(Color.FromRgb(51, 65, 85))
    };
    public Brush BleDisplayBorderBrush => bleTransportState switch
    {
        "connected" when bleInputHealthy => new SolidColorBrush(Color.FromRgb(45, 212, 191)),
        "connected" => new SolidColorBrush(Color.FromRgb(251, 146, 60)),
        "connecting" => new SolidColorBrush(Color.FromRgb(250, 204, 21)),
        "scanning" => new SolidColorBrush(Color.FromRgb(250, 204, 21)),
        _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
    };
    public Brush BleDisplayForegroundBrush => Brushes.White;
    public Brush ModeCardBrush => currentMode switch
    {
        DeviceUiMode.DualSense => new SolidColorBrush(Color.FromRgb(235, 245, 255)),
        DeviceUiMode.Pro2 => new SolidColorBrush(Color.FromRgb(255, 241, 242)),
        DeviceUiMode.Switch1Pro => new SolidColorBrush(Color.FromRgb(255, 247, 237)),
        DeviceUiMode.DualPro2Probe => new SolidColorBrush(Color.FromRgb(250, 245, 255)),
        DeviceUiMode.Xbox => new SolidColorBrush(Color.FromRgb(236, 252, 233)),
        DeviceUiMode.XboxElite => new SolidColorBrush(Color.FromRgb(236, 253, 245)),
        _ => Brushes.White
    };
    public Brush ModeCardBorderBrush => currentMode switch
    {
        DeviceUiMode.DualSense => new SolidColorBrush(Color.FromRgb(29, 78, 216)),
        DeviceUiMode.Pro2 => new SolidColorBrush(Color.FromRgb(190, 24, 93)),
        DeviceUiMode.Switch1Pro => new SolidColorBrush(Color.FromRgb(234, 88, 12)),
        DeviceUiMode.DualPro2Probe => new SolidColorBrush(Color.FromRgb(126, 34, 206)),
        DeviceUiMode.Xbox => new SolidColorBrush(Color.FromRgb(22, 101, 52)),
        DeviceUiMode.XboxElite => new SolidColorBrush(Color.FromRgb(21, 128, 61)),
        _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
    };
    public Brush DualSenseCardBackground => GetModeCardBackground(OutputModeId.DualSenseStandard);
    public Brush DualSenseEdgeCardBackground => GetModeCardBackground(OutputModeId.DualSenseLike);
    public Brush Pro2CardBackground => GetModeCardBackground(OutputModeId.Pro2);
    public Brush XboxCardBackground => GetModeCardBackground(OutputModeId.Xbox);
    public Brush Switch1ProCardBackground => GetModeCardBackground(OutputModeId.Switch1Pro);
    public Brush DualPro2ProbeCardBackground => GetModeCardBackground(OutputModeId.DualPro2Probe);
    public Brush DualSenseCardBorderBrush => GetModeCardBorderBrush(OutputModeId.DualSenseStandard, managerReady: true);
    public Brush DualSenseEdgeCardBorderBrush => GetModeCardBorderBrush(OutputModeId.DualSenseLike, managerReady: true);
    public Brush Pro2CardBorderBrush => GetModeCardBorderBrush(OutputModeId.Pro2, managerReady: true);
    public Brush XboxCardBorderBrush => GetModeCardBorderBrush(OutputModeId.Xbox, managerReady: true);
    public Brush Switch1ProCardBorderBrush => GetModeCardBorderBrush(OutputModeId.Switch1Pro, managerReady: true);
    public Brush DualPro2ProbeCardBorderBrush => GetModeCardBorderBrush(OutputModeId.DualPro2Probe, managerReady: true);
    public Brush DualSenseCardBadgeBrush => GetModeBadgeBrush(OutputModeId.DualSenseStandard, managerReady: true);
    public Brush DualSenseEdgeCardBadgeBrush => GetModeBadgeBrush(OutputModeId.DualSenseLike, managerReady: true);
    public Brush Pro2CardBadgeBrush => GetModeBadgeBrush(OutputModeId.Pro2, managerReady: true);
    public Brush XboxCardBadgeBrush => GetModeBadgeBrush(OutputModeId.Xbox, managerReady: true);
    public Brush Switch1ProCardBadgeBrush => GetModeBadgeBrush(OutputModeId.Switch1Pro, managerReady: true);
    public Brush DualPro2ProbeCardBadgeBrush => GetModeBadgeBrush(OutputModeId.DualPro2Probe, managerReady: true);
    public Visibility DualSenseLabVisibility => IsPs5FamilyMode(desiredMode) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Pro2LabVisibility => desiredMode == OutputModeId.Pro2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DualSenseLabDisabledVisibility => desiredMode == OutputModeId.Unknown || desiredMode == OutputModeId.Recovery ? Visibility.Visible : Visibility.Collapsed;
    public Visibility XboxLabVisibility => desiredMode == OutputModeId.Xbox ? Visibility.Visible : Visibility.Collapsed;
    public string XboxToolStateText => DescribeToolState(
            OutputModeId.Xbox,
            currentMode == DeviceUiMode.Xbox,
            "当前 USB 身份是 Xbox / XInput。BLE 输入走同一份 Pro2 state，主机普通震动会被解析并回传到 Pro2。",
            "已选 Xbox 面板，但 USB 还没有切到 045E:028E。");
    public string ConnectionGuideTitle => bleTransportState switch
    {
        "connected" when bleInputHealthy => "手柄连接完成",
        "connected" => "手柄已连上，正在恢复实时输入",
        "connecting" => "正在连接手柄",
        "scanning" => "正在寻找手柄",
        _ when string.IsNullOrWhiteSpace(bleSavedTarget) => "尚未绑定 Pro2 手柄",
        _ => "已记住手柄，当前等待唤醒"
    };
    public string ConnectionGuideDetail => bleTransportState switch
    {
        "connected" when bleInputHealthy => "实时输入正常。以后手柄休眠或短暂断联，固件会优先寻找这个已保存地址并自动恢复。",
        "connected" => "BLE 链路存在，但输入暂时不新鲜。请保持手柄唤醒，固件会继续自动修复。",
        "connecting" => "请保持目标手柄唤醒，不要让它同时连接电脑、手机或其他主机。",
        "scanning" => "正在扫描附近候选设备。首次连接时请只开启要绑定的那一只 Pro2。",
        _ when string.IsNullOrWhiteSpace(bleSavedTarget) => "全新使用请点“首次连接”；已有手柄休眠后重连请点“重连已配对”；换另一只手柄请点“更换手柄”。",
        _ => "已保存目标 " + bleSavedTarget + "。唤醒原手柄会自动连接，也可以点“重连已配对”。"
    };
    public string ConnectionTargetText => string.IsNullOrWhiteSpace(bleSavedTarget)
        ? "当前没有保存手柄地址"
        : "已保存手柄：" + bleSavedTarget;

    public ICommand RefreshPortsCommand { get; }
    public ICommand FlashHapticCommand { get; }
    public ICommand FlashHidOnlyCommand { get; }
    public ICommand FlashPro2Command { get; }
    public ICommand EraseFirmwareCommand { get; }
    public ICommand ActivateDualSenseModeCommand { get; }
    public ICommand ActivateDualSenseEdgeModeCommand { get; }
    public ICommand ActivatePro2ModeCommand { get; }
    public ICommand ActivateXboxModeCommand { get; }
    public ICommand ActivateSwitch1ProModeCommand { get; }
    public ICommand ActivateDualPro2ProbeModeCommand { get; }
    public ICommand CheckUsbCommand { get; }
    public ICommand ListAudioCommand { get; }
    public ICommand OpenJoyCommand { get; }
    public ICommand OpenDeviceManagerCommand { get; }
    public ICommand RepairCh343DriverCommand { get; }
    public ICommand BleScanCommand { get; }
    public ICommand BleListCommand { get; }
    public ICommand BleReconnectCommand { get; }
    public ICommand BleFirstPairCommand { get; }
    public ICommand BleReplaceControllerCommand { get; }
    public ICommand BleAutoOnCommand { get; }
    public ICommand BleAutoOffCommand { get; }
    public ICommand BleDisconnectCommand { get; }
    public ICommand BleConnectCommand { get; }
    public ICommand InputRecalibrateCommand { get; }
    public ICommand HapticStatusCommand { get; }
    public ICommand DryRunOnCommand { get; }
    public ICommand LiveOffCommand { get; }
    public ICommand LiveOnCommand { get; }
    public ICommand AudioParserRearCommand { get; }
    public ICommand AudioParserFrontCommand { get; }
    public ICommand AudioParserStrongestCommand { get; }
    public ICommand HapticTickCommand { get; }
    public ICommand HapticPunchCommand { get; }
    public ICommand HapticStopCommand { get; }
    public ICommand SafeHapticTestCommand { get; }
    public ICommand SendAudioPatternCommand { get; }
    public ICommand StartGameMonitorCommand { get; }
    public ICommand StopGameMonitorCommand { get; }
    public ICommand RunXInputProbeCommand { get; }
    public ICommand DualProbeRealCommand { get; }
    public ICommand DualProbeMirrorCommand { get; }
    public ICommand DualProbeSyntheticCommand { get; }
    public ICommand DualProbeStatusCommand { get; }
    public ICommand StartDualNs2ProHostCommand { get; }
    public ICommand StartDualNs2ProHostSyntheticCommand { get; }
    public ICommand StopDualNs2ProHostCommand { get; }
    public ICommand XboxPaddleRefreshCommand { get; }
    public ICommand XboxPaddleApplyCommand { get; }
    public ICommand XboxPaddleResetCommand { get; }
    public ICommand Pro2RumbleStatusCommand { get; }
    public ICommand Pro2RumbleLightCommand { get; }
    public ICommand Pro2RumbleStrongCommand { get; }
    public ICommand Pro2RumbleApplyTuneCommand { get; }
    public ICommand Pro2RumbleManualHoldCommand { get; }
    public ICommand Pro2RumbleStopCommand { get; }
    public ICommand SendCustomCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand SaveLogCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public MainViewModel(Window owner)
    {
        this.owner = owner;
        desiredMode = ParseDesiredMode(settings.DesiredModeId, settings.LastSuccessfulProfileId);
        if (string.IsNullOrWhiteSpace(settings.DesiredModeId))
        {
            settings.DesiredModeId = desiredMode.ToString();
            ManagerSettingsStore.Save(settings);
        }
        xboxPaddleLeftEnabled = settings.XboxPaddleLeftEnabled;
        xboxPaddleRightEnabled = settings.XboxPaddleRightEnabled;
        xboxPaddleLeftMode = NormalizeXboxPaddleMode(settings.XboxPaddleLeftMode);
        xboxPaddleRightMode = NormalizeXboxPaddleMode(settings.XboxPaddleRightMode);
        xboxPaddleLeftTargets = string.IsNullOrWhiteSpace(settings.XboxPaddleLeftTargets) ? "B" : settings.XboxPaddleLeftTargets;
        xboxPaddleRightTargets = string.IsNullOrWhiteSpace(settings.XboxPaddleRightTargets) ? "A" : settings.XboxPaddleRightTargets;
        xboxPaddleLeftTapMs = string.IsNullOrWhiteSpace(settings.XboxPaddleLeftTapMs) ? "70" : settings.XboxPaddleLeftTapMs;
        xboxPaddleRightTapMs = string.IsNullOrWhiteSpace(settings.XboxPaddleRightTapMs) ? "70" : settings.XboxPaddleRightTapMs;
        xboxPaddleLeftTurboOnMs = string.IsNullOrWhiteSpace(settings.XboxPaddleLeftTurboOnMs) ? "45" : settings.XboxPaddleLeftTurboOnMs;
        xboxPaddleLeftTurboOffMs = string.IsNullOrWhiteSpace(settings.XboxPaddleLeftTurboOffMs) ? "45" : settings.XboxPaddleLeftTurboOffMs;
        xboxPaddleRightTurboOnMs = string.IsNullOrWhiteSpace(settings.XboxPaddleRightTurboOnMs) ? "45" : settings.XboxPaddleRightTurboOnMs;
        xboxPaddleRightTurboOffMs = string.IsNullOrWhiteSpace(settings.XboxPaddleRightTurboOffMs) ? "45" : settings.XboxPaddleRightTurboOffMs;
        RefreshPortsCommand = new RelayCommand(async _ => await RefreshPortsAsync());
        FlashHapticCommand = new RelayCommand(async _ => await FlashAsync("hid_audio_uac1_4ch_dualsense", FlashMode.Upgrade));
        FlashHidOnlyCommand = new RelayCommand(async _ => await FlashAsync("hid_only", FlashMode.Repair));
        FlashPro2Command = new RelayCommand(async _ => await FlashAsync("pro2_bridge_v5_5", FlashMode.Upgrade));
        EraseFirmwareCommand = new RelayCommand(async _ => await EraseFirmwareAsync());
        ActivateDualSenseModeCommand = new RelayCommand(async _ => await ActivateModeAsync(OutputModeCatalog.DualSenseStandard));
        ActivateDualSenseEdgeModeCommand = new RelayCommand(async _ => await ActivateModeAsync(OutputModeCatalog.DualSenseLike));
        ActivatePro2ModeCommand = new RelayCommand(async _ => await ActivateModeAsync(OutputModeCatalog.Pro2));
        ActivateXboxModeCommand = new RelayCommand(async _ => await ActivateModeAsync(OutputModeCatalog.Xbox));
        ActivateSwitch1ProModeCommand = new RelayCommand(async _ => await ActivateModeAsync(OutputModeCatalog.Switch1Pro));
        ActivateDualPro2ProbeModeCommand = new RelayCommand(async _ => await ActivateModeAsync(OutputModeCatalog.DualPro2Probe));
        CheckUsbCommand = new RelayCommand(async _ => await CheckUsbAsync());
        ListAudioCommand = new RelayCommand(async _ => await ListAudioAsync());
        OpenJoyCommand = new RelayCommand(_ => StartShell("joy.cpl"));
        OpenDeviceManagerCommand = new RelayCommand(_ => StartShell("devmgmt.msc"));
        RepairCh343DriverCommand = new RelayCommand(async _ => await RepairCh343DriverAsync());
        BleScanCommand = new RelayCommand(async _ => await ScanBleAsync());
        BleListCommand = new RelayCommand(async _ => await ListBleAsync());
        BleReconnectCommand = new RelayCommand(async _ => await ReconnectBleAsync());
        BleFirstPairCommand = new RelayCommand(async _ => await StartFreshPairingAsync(replacing: false));
        BleReplaceControllerCommand = new RelayCommand(async _ => await StartFreshPairingAsync(replacing: true));
        BleAutoOnCommand = new RelayCommand(async _ => await SendSerialAsync("ble auto on", 5, s => BleStatus = "BLE 自动重连：已开启。"));
        BleAutoOffCommand = new RelayCommand(async _ => await SendSerialAsync("ble auto off", 5, s => BleStatus = "BLE 自动重连：已关闭。"));
        BleDisconnectCommand = new RelayCommand(async _ => await SendSerialAsync("ble disconnect", 5, s => BleStatus = "已请求断开 BLE。"));
        BleConnectCommand = new RelayCommand(async _ => await ConnectBleAsync());
        InputRecalibrateCommand = new RelayCommand(async _ => await RecalibrateInputAsync());
        HapticStatusCommand = new RelayCommand(async _ => await SendSerialAsync("haptic status", 5, s => HapticStatus = SummarizeHaptic(s)));
        DryRunOnCommand = new RelayCommand(async _ => await SendSerialAsync("haptic dryrun on", 4, _ => HapticStatus = "试运行已开启。"));
        LiveOffCommand = new RelayCommand(async _ => await TurnLiveOffAsync());
        LiveOnCommand = new RelayCommand(async _ => await TurnLiveOnAsync());
        AudioParserRearCommand = new RelayCommand(async _ => await SetAudioParserAsync("rear"));
        AudioParserFrontCommand = new RelayCommand(async _ => await SetAudioParserAsync("front"));
        AudioParserStrongestCommand = new RelayCommand(async _ => await SetAudioParserAsync("strongest"));
        HapticTickCommand = new RelayCommand(async _ => await SendLiveHapticPulseAsync("tick", "轻击"));
        HapticPunchCommand = new RelayCommand(async _ => await SendLiveHapticPulseAsync("punch", "重击"));
        HapticStopCommand = new RelayCommand(async _ => await SendSerialAsync("haptic test live stop", 4, _ => HapticStatus = "已发送实时转发停止命令。"));
        SafeHapticTestCommand = new RelayCommand(async _ => await RunSafeHapticTestAsync());
        SendAudioPatternCommand = new RelayCommand(async _ => await SendAudioPatternAsync());
        StartGameMonitorCommand = new RelayCommand(async _ => await StartGameMonitorAsync());
        StopGameMonitorCommand = new RelayCommand(async _ => await StopGameMonitorAsync());
        RunXInputProbeCommand = new RelayCommand(async _ => await RunXInputProbeAsync());
        DualProbeRealCommand = new RelayCommand(async _ => await SendSerialAsync("dual sim off", 5, s => BleStatus = SummarizeDualProbeStatus(s)));
        DualProbeMirrorCommand = new RelayCommand(async _ => await SendSerialAsync("dual sim mirror", 5, s => BleStatus = SummarizeDualProbeStatus(s)));
        DualProbeSyntheticCommand = new RelayCommand(async _ => await SendSerialAsync("dual sim synthetic 133", 5, s => BleStatus = SummarizeDualProbeStatus(s)));
        DualProbeStatusCommand = new RelayCommand(async _ => await SendSerialAsync("dual status", 5, s => BleStatus = SummarizeDualProbeStatus(s)));
        StartDualNs2ProHostCommand = new RelayCommand(async _ => await StartDualNs2ProHostAsync(synthetic: false));
        StartDualNs2ProHostSyntheticCommand = new RelayCommand(async _ => await StartDualNs2ProHostAsync(synthetic: true));
        StopDualNs2ProHostCommand = new RelayCommand(async _ => await StopDualNs2ProHostAsync());
        XboxPaddleRefreshCommand = new RelayCommand(async _ => await RefreshXboxPaddleMappingAsync());
        XboxPaddleApplyCommand = new RelayCommand(async _ => await ApplyXboxPaddleMappingAsync());
        XboxPaddleResetCommand = new RelayCommand(async _ => await ResetXboxPaddleMappingAsync());
        Pro2RumbleStatusCommand = new RelayCommand(async _ => await RefreshPro2RumbleStatusAsync());
        Pro2RumbleLightCommand = new RelayCommand(async _ => await RunPro2RumblePresetAsync("轻震", 120, 160, 10, 2));
        Pro2RumbleStrongCommand = new RelayCommand(async _ => await RunPro2RumblePresetAsync("重震", 185, 280, 12, 4));
        Pro2RumbleApplyTuneCommand = new RelayCommand(async _ => await ApplyPro2RumbleTuneAsync());
        Pro2RumbleManualHoldCommand = new RelayCommand(async _ => await RunPro2ManualHoldAsync());
        Pro2RumbleStopCommand = new RelayCommand(async _ => await StopPro2RumbleAsync());
        SendCustomCommand = new RelayCommand(async _ => await SendSerialAsync(CustomCommand, 6, _ => { }));
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        SaveLogCommand = new RelayCommand(_ => SaveLog());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());

        logUiTimer.Interval = TimeSpan.FromMilliseconds(180);
        logUiTimer.Tick += (_, _) => FlushLogTextToUi();
        logUiTimer.Start();

        AppendLog("PRO2 手柄无线接收器控制板 V5.9.18 Switch 1 连接与震动测试 r4 已就绪。BLE GAP 由单一监督器调度，扫描取消完成前不会重复重连。");
        AppendLog("[STARTUP] desired_mode=" + desiredMode);
        if (!string.IsNullOrWhiteSpace(settings.LastBleTarget))
        {
            bleTarget = settings.LastBleTarget;
        }
        if (!string.IsNullOrWhiteSpace(settings.LastAudioDeviceName))
        {
            audioDeviceName = settings.LastAudioDeviceName;
        }

        initializeTask = InitializeAsync();
        stateTimer.Interval = TimeSpan.FromSeconds(3);
        stateTimer.Tick += async (_, _) => await AutoRefreshStateAsync();
        stateTimer.Start();
    }

    private async Task InitializeAsync()
    {
        await RefreshPortsAsync(logResult: false);
        await CheckUsbAsync(logResult: false);
    }

    public Task WaitForInitializationAsync() => initializeTask ?? Task.CompletedTask;

    public bool ShouldOfferFirstRunGuide =>
        !settings.FirstRunGuideOffered && !settings.FirstRunGuideCompleted;

    public void MarkFirstRunGuideOffered()
    {
        settings.FirstRunGuideOffered = true;
        ManagerSettingsStore.Save(settings);
    }

    public void MarkFirstRunGuideCompleted()
    {
        settings.FirstRunGuideOffered = true;
        settings.FirstRunGuideCompleted = true;
        ManagerSettingsStore.Save(settings);
        AppendLog("[FIRST_RUN_GUIDE] completed=1");
    }

    public async Task<GuideDetectionResult> RunGuideDetectionAsync()
    {
        await WaitForInitializationAsync();
        await RefreshPortsAsync();
        await CheckUsbAsync();
        bool serialReady = HasUsableSerialCandidate;
        string summary = serialReady
            ? "已识别 ESP 控制口：" + SelectedPort!.DisplayName
            : "没有识别到可用的 CH343/COM 控制口。";
        summary += usbDetected
            ? " 原生 USB/OTG 当前枚举为：" + CurrentModeLabel + "。"
            : " 原生 USB/OTG 尚未枚举成手柄；全新或已擦除控制板在刷写前可以出现这种状态。";

        return new GuideDetectionResult(
            serialReady,
            usbDetected,
            summary,
            serialReady
                ? "检测通过，可以选择目标手柄模式并刷写。"
                : "请确认 CH343/COM 控制线已插入；若设备管理器有黄色感叹号，先修复 CH343 驱动。此步骤不要选择蓝牙虚拟串口。");
    }

    public async Task<GuideFlashResult> RunGuideFlashAsync(OutputModeId mode)
    {
        OutputModeProfile profile = GuideProfileForMode(mode);
        lastFlashSucceeded = null;
        lastFlashProfileId = profile.ProfileId;
        lastFlashFailureCategory = "";
        await ActivateModeAsync(profile);
        bool succeeded = lastFlashSucceeded == true &&
                         string.Equals(lastFlashProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase);
        return new GuideFlashResult(
            succeeded,
            profile,
            succeeded ? "" : string.IsNullOrWhiteSpace(lastFlashFailureCategory) ? "刷写未启动" : lastFlashFailureCategory,
            succeeded ? "固件刷写完成：" + profile.Label : ModeSwitchStatus,
            succeeded
                ? "请重新插拔原生 USB/OTG 数据线，然后进入下一步验证 " + profile.ExpectedUsbMarker + "。"
                : NextAction);
    }

    public async Task<GuideUsbVerificationResult> RunGuideUsbVerificationAsync(OutputModeId mode)
    {
        OutputModeProfile profile = GuideProfileForMode(mode);
        await CheckUsbAsync();
        bool succeeded = UsbStatus.Contains(profile.ExpectedUsbMarker, StringComparison.OrdinalIgnoreCase);
        return new GuideUsbVerificationResult(
            succeeded,
            profile,
            succeeded
                ? "Windows 已识别 " + profile.Label + "（" + profile.ExpectedUsbMarker + "）。"
                : "尚未识别目标手柄身份。期望 " + profile.ExpectedUsbMarker + "，当前结果：" + OneLine(UsbStatus),
            succeeded
                ? "可以在 joy.cpl、Steam 或兼容 tester 中确认按键，然后开始配对 Pro2。"
                : "重新插拔原生 USB/OTG 数据线后再检查；不要拔掉 CH343/COM 控制线，因为下一步还要通过它下发 BLE 配对命令。");
    }

    public async Task<GuidePairingResult> RunGuideFirstPairingAsync()
    {
        await StartFreshPairingAsync(replacing: false);
        return BuildGuidePairingResult();
    }

    public async Task<GuidePairingResult> RunGuideBleScanAsync()
    {
        await ScanBleAsync();
        bool found = BleDevices.Count > 0;
        return new GuidePairingResult(
            found,
            found ? "扫描到 " + BleDevices.Count + " 个 BLE 设备，请选择目标 Pro2。" : BleStatus,
            found
                ? "选择名称、地址或信号强度正确的 Pro2，再点击“连接所选”。"
                : "让 Pro2 进入配对模式并靠近 ESP32-S3；确认它没有同时连接电脑、Switch、手机或另一块桥接板。");
    }

    public async Task<GuidePairingResult> RunGuideConnectSelectedAsync()
    {
        await ConnectBleAsync();
        return BuildGuidePairingResult();
    }

    public Task RunGuideRepairCh343DriverAsync() => RepairCh343DriverAsync();

    public void OpenControllerPanel() => StartShell("joy.cpl");

    private GuidePairingResult BuildGuidePairingResult()
    {
        bool succeeded = bleConnected && bleInputHealthy;
        return new GuidePairingResult(
            succeeded,
            succeeded ? "Pro2 已连接，实时输入已确认，手柄地址已保存。" : BleStatus,
            succeeded
                ? "配对完成。以后只保留原生 USB/OTG 数据线，ESP 固件也会自动寻找已保存的 Pro2。"
                : NextAction);
    }

    private static OutputModeProfile GuideProfileForMode(OutputModeId mode) => mode switch
    {
        OutputModeId.DualSenseStandard => OutputModeCatalog.DualSenseStandard,
        OutputModeId.DualSenseLike => OutputModeCatalog.DualSenseLike,
        OutputModeId.Pro2 => OutputModeCatalog.Pro2,
        OutputModeId.Xbox => OutputModeCatalog.Xbox,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "首次运行向导只支持四个正式模式。")
    };

    private async Task RefreshPortsAsync(bool logResult = true)
    {
        PortScanResult scan = await Task.Run(() => DeviceInspector.ScanPorts());
        Ports.Clear();
        foreach (PortItem item in scan.Ports) Ports.Add(item);
        SelectedPort = PreferredPort();
        serialBoardReady = false;
        nextSerialAutoProbeAt = DateTime.UtcNow;
        PortStatus = Ports.Count == 0
            ? "当前没有检测到 COM 串口。程序会保持离线状态，直到出现 ESP32 控制板串口。"
            : SelectedPort == null
                ? "检测到了串口，但还没有可自动使用的 ESP32 控制板候选项。你仍然可以手动选择。"
                : "已发现 " + Ports.Count + " 个串口，并自动选中了 CH343P/WCH 控制板候选项。为避免串口热插拔导致 ESP32 复位，刷新列表不会主动打开 COM 口。";
        if (scan.MetadataTimedOut)
        {
            PortStatus += " 设备详情查询超时，已先返回快照结果。";
        }
        else if (scan.MetadataFailed)
        {
            PortStatus += " 设备详情查询失败，已退回到基础串口列表。";
        }
        if (logResult)
        {
            AppendLog(PortStatus);
        }
    }

    private PortItem? FirstLikelyPort()
    {
        foreach (PortItem port in Ports)
        {
            if (port.LikelyCh343 && port.CanOpen) return port;
        }
        return null;
    }

    private PortItem? PreferredPort()
    {
        if (!string.IsNullOrWhiteSpace(settings.LastPortName))
        {
            foreach (PortItem port in Ports)
            {
                if (string.Equals(port.PortName, settings.LastPortName, StringComparison.OrdinalIgnoreCase) &&
                    (port.CanOpen || !port.LikelyCh343))
                {
                    return port;
                }
            }
        }

        return FirstLikelyPort();
    }

    private async Task RepairCh343DriverAsync()
    {
        if (Busy || flashInProgress || GameMonitorRunning)
        {
            PortStatus = "已有刷写、BLE 操作或诊断监听正在进行，暂时不能修复 CH343 驱动。";
            return;
        }

        try
        {
            Busy = true;
            OverallStatus = "修复驱动中";
            if (SelectedPort == null)
            {
                await RefreshPortsAsync(logResult: false);
            }
            if (SelectedPort == null)
            {
                throw new InvalidOperationException("当前没有可用的 CH343/ESP32 控制板串口。");
            }

            PortItem port = SelectedPort;
            PortStatus = "正在读取 " + port.PortName + " 的 CH343 驱动信息。";
            PortDriverInfo? driver = await Task.Run(() => DeviceInspector.QueryPortDriver(port.PortName));
            if (driver == null)
            {
                throw new InvalidOperationException(
                    "没有读到 " + port.PortName +
                    " 的驱动信息。请确认选择的是 CH343P 控制口，而不是原生 USB 手柄口。");
            }

            AppendLog("[CH343_DRIVER_CHECK] " + driver.Summary + " device_id=" + driver.DeviceId);
            bool looksLikeCh343 =
                port.LikelyCh343 ||
                driver.DeviceId.Contains("VID_1A86&PID_55D3", StringComparison.OrdinalIgnoreCase) ||
                driver.DeviceName.Contains("CH343", StringComparison.OrdinalIgnoreCase) ||
                driver.DeviceName.Contains("WCH", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeCh343)
            {
                throw new InvalidOperationException(
                    "当前选中的 " + port.PortName +
                    " 看起来不是 CH343P 控制口。为避免误卸载其他串口驱动，已停止。");
            }

            if (string.Equals(driver.InfName, "usbser.inf", StringComparison.OrdinalIgnoreCase) ||
                driver.Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                PortStatus = port.PortName + " 已经使用 Microsoft USB 串行设备驱动。";
                ModeSwitchStatus = "CH343 驱动检查通过：" + driver.Summary;
                NextAction = "驱动层已经是 Microsoft usbser；如果刷写仍卡住，请按住 BOOT 重试下载模式，或拔插 CH343P 控制口。";
                AppendLog("[CH343_DRIVER_REPAIR] already_usbser " + driver.Summary);
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                owner,
                "即将修复 " + port.PortName + " 的 CH343 驱动。\n\n" +
                "当前驱动：" + driver.Provider + " " + driver.Version + " / " + driver.InfName + "\n\n" +
                "程序会请求 UAC 管理员权限，备份当前第三方 WCH 驱动，然后卸载它，让 Windows 重新绑定 Microsoft “USB 串行设备 (usbser.inf)”。\n\n" +
                "修复完成后请拔插一次 CH343P 控制口，再回来刷机。",
                "修复 CH343 驱动",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK)
            {
                PortStatus = "已取消 CH343 驱动修复。";
                return;
            }

            if (!await serialLock.WaitAsync(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("上一条串口操作仍未释放，暂时无法修复 CH343 驱动。请稍后重试。");
            }
            try
            {
                if (!await SerialCommandClient.CloseAsync(1500))
                {
                    AppendLog("[CH343_DRIVER_REPAIR] serial close timed out; continuing because repair runs out-of-process");
                }
            }
            finally
            {
                serialLock.Release();
            }

            PortStatus = "正在启动管理员驱动修复脚本，请确认 UAC 弹窗。";
            NextAction = "请确认 UAC 管理员授权。脚本完成后拔插 CH343P 控制口，再点击“刷新串口”。";
            AppendLog("[CH343_DRIVER_REPAIR] start " + driver.Summary + " device_id=" + driver.DeviceId);
            Ch343DriverRepairResult result = await Ch343DriverRepair.RunAsync(driver);
            AppendLog("[CH343_DRIVER_REPAIR] " + result.Message);

            if (result.Completed && result.ExitCode == 0)
            {
                PortStatus = "CH343 驱动修复脚本已完成。";
                ModeSwitchStatus = "CH343 驱动已尝试切换到 Microsoft usbser。";
                NextAction = "请拔插 CH343P 控制口，然后点击“刷新串口”和“USB 检查”；若仍不是 usbser，请打开日志查看原因。";
                OverallStatus = "就绪";
                await Task.Delay(1200);
                await RefreshPortsAsync(logResult: false);
                return;
            }

            OverallStatus = "错误";
            PortStatus = result.Completed
                ? "CH343 驱动修复脚本失败，exit=" + result.ExitCode + "。"
                : "CH343 驱动修复脚本仍在运行，界面已停止等待。";
            ModeSwitchStatus = PortStatus;
            NextAction = "查看日志：" + result.LogPath + "。如果脚本已经完成，请拔插 CH343P 控制口后刷新串口。";
        }
        catch (OperationCanceledException ex)
        {
            OverallStatus = "就绪";
            PortStatus = "CH343 驱动修复已取消：" + FirstLine(ex.Message);
            AppendLog("[CH343_DRIVER_REPAIR] canceled " + ex.Message);
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            PortStatus = "CH343 驱动修复失败：" + FirstLine(ex.Message);
            ModeSwitchStatus = "CH343 驱动修复失败：" + FirstLine(ex.Message);
            NextAction = "可先打开“设备管理器”手动把 CH343 切换为 Microsoft “USB 串行设备”，或者拔插 CH343P 控制口后再点“修复 CH343 驱动”。";
            AppendLog("ERROR ch343 driver repair: " + ex);
        }
        finally
        {
            Busy = false;
            NotifyModeStateChanged();
        }
    }

    private async Task TryCaptureFirmwareIdentityAfterFlashAsync(string requestedProfile)
    {
        try
        {
            await Task.Delay(1200);
            string status = await SendSerialCoreAsync("status lite", 2, logOutput: false);
            string identity = DescribeFirmwareIdentity(status);
            ModeSwitchStatus = "串口固件回报：" + identity + "；现在还需要确认新的 USB 枚举。";
            AppendLog("[MODE_SWITCH_FIRMWARE] requested_profile=" + requestedProfile + " " + identity);
            string modeCommand = ModeCommandForProfile(requestedProfile);
            if (!string.IsNullOrWhiteSpace(modeCommand))
            {
                string modeReply = await SendSerialCoreAsync(modeCommand, 2, logOutput: false);
                AppendLog("[MODE_SWITCH_FIRMWARE_MODE] requested_profile=" + requestedProfile +
                          " command=" + modeCommand +
                          " reply=" + OneLine(modeReply));
                await SendSerialCoreAsync("reboot", 1, logOutput: false);
                serialBoardReady = false;
                if (!await SerialCommandClient.CloseAsync(750))
                {
                    AppendLog("[SERIAL] reboot close timed out; keeping UI responsive");
                }
                ModeSwitchStatus = "已写入 " + GetModeLabel(OutputModeCatalog.FindByProfileId(requestedProfile)?.ModeId ?? desiredMode) +
                                   " 固件模式并重启，等待新的 USB 枚举。";
            }
        }
        catch (Exception ex)
        {
            AppendLog("[MODE_SWITCH_FIRMWARE] requested_profile=" + requestedProfile +
                      " probe_failed=" + FirstLine(ex.Message));
        }
    }

    private static string ModeCommandForProfile(string requestedProfile)
    {
        return requestedProfile switch
        {
            "pro2_bridge_v5_5" => "mode nintendo",
            "xinput_bridge_v5_8" => "mode xinput",
            _ => ""
        };
    }

    private async Task ActivateModeAsync(OutputModeProfile profile)
    {
        if (Busy || flashInProgress)
        {
            ModeSwitchStatus = "已有刷写、切换或设备操作正在进行。请等待当前任务结束后再切换模式。";
            NextAction = "等待当前任务结束，不要连续点击模式卡片。";
            AppendLog("[MODE_DECK_BUSY] requested=" + profile.ModeId);
            return;
        }

        SetDesiredMode(profile.ModeId);

        if (!profile.ManagerReady)
        {
            OverallStatus = "预留";
            NextAction = profile.Label + " 目前还是预留位，暂时没有接入完整后端。";
            AppendLog("[MODE_DECK] requested=" + profile.ModeId + " status=staged manager_ready=false");
            MessageBox.Show(
                owner,
                profile.Label + " 目前还是预留位，暂时没有接入完整后端。\n\n管理器已经保留好了模式位和切换语义，后续补固件时不需要再重做界面。",
                "V5.9.18 测试版模式预留",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        FlashMode flashMode = profile.ModeId == OutputModeId.Recovery ? FlashMode.Repair : FlashMode.Upgrade;
        if (!HasUsableSerialCandidate)
        {
            OverallStatus = "离线";
            ModeSwitchStatus = "已切到 " + profile.Label + " 面板，但当前没有可用的控制板串口，所以没有执行实际刷写。";
            NextAction = "请先连接可用的 ESP32 控制板，再点一次当前模式卡片完成真正切换。";
            AppendLog("[MODE_DECK] requested=" + profile.ModeId + " selected_panel_only=true reason=no_usable_serial_candidate");
            NotifyModeStateChanged();
            return;
        }

        ModeSwitchStatus = "正在请求切换到 " + profile.Label + "，目标 USB 标识=" + profile.ExpectedUsbMarker;
        NextAction = "正在切换到 " + GetModeLabel(profile.ModeId) + "。刷写后请重新插拔原生 USB / OTG。";
        AppendLog("[MODE_DECK] requested=" + profile.ModeId + " profile=" + profile.ProfileId);
        await FlashAsync(profile.ProfileId, flashMode);
    }

    private void SetDesiredMode(OutputModeId mode)
    {
        if (desiredMode == mode)
        {
            return;
        }

        desiredMode = mode;
        settings.DesiredModeId = mode.ToString();
        ManagerSettingsStore.Save(settings);
        NotifyModeStateChanged();
    }

    private static OutputModeId ParseDesiredMode(string? stored, string? fallbackProfileId)
    {
        if (!string.IsNullOrWhiteSpace(stored) &&
            Enum.TryParse(stored, ignoreCase: true, out OutputModeId parsed) &&
            parsed != OutputModeId.Unknown)
        {
            return parsed == OutputModeId.XboxElite ? OutputModeId.Xbox : parsed;
        }

        if (string.Equals(fallbackProfileId, "xinput_elite_bridge_v5_9", StringComparison.OrdinalIgnoreCase))
        {
            return OutputModeId.Xbox;
        }

        if (string.Equals(fallbackProfileId, "hid_audio_uac1_4ch_ds5like", StringComparison.OrdinalIgnoreCase))
        {
            return OutputModeId.DualSenseLike;
        }

        return OutputModeCatalog.FindByProfileId(fallbackProfileId)?.ModeId ?? OutputModeId.Pro2;
    }

#pragma warning disable CS0162
    private async Task FlashAsync(string profile, FlashMode mode)
    {
        lastFlashProfileId = profile;
        lastFlashSucceeded = null;
        lastFlashFailureCategory = "";
        if (Busy || flashInProgress || GameMonitorRunning)
        {
            ModeSwitchStatus = "已有刷写、模式切换、游戏监听或设备操作正在进行，已忽略新的刷写请求。";
            NextAction = "请等待当前任务结束后再切换或刷写。";
            AppendLog("[FLASH_BUSY] ignored_profile=" + profile + " busy=" + Busy + " flashing=" + flashInProgress + " monitor=" + GameMonitorRunning);
            lastFlashSucceeded = false;
            lastFlashFailureCategory = "已有任务正在运行";
            return;
        }

        if (!await flashLock.WaitAsync(0))
        {
            ModeSwitchStatus = "已有刷写或模式切换正在进行，已忽略新的刷写请求。";
            NextAction = "请等待当前刷写结束后再切换。";
            AppendLog("[MODE_SWITCH_BUSY] ignored_profile=" + profile);
            lastFlashSucceeded = false;
            lastFlashFailureCategory = "已有刷写正在运行";
            return;
        }

        flashInProgress = true;
        NotifyModeStateChanged();
        try
        {
            Busy = true;
            OutputModeProfile? requestedMode = OutputModeCatalog.FindByProfileId(profile);
            if (requestedMode != null)
            {
                SetDesiredMode(requestedMode.ModeId);
            }
            if (false)
            {
            string status = await SendSerialCoreAsync("status lite", 2, logOutput: false);
            string ble = ReadJsonString(status, "ble");
            if (string.Equals(ble, "connected", StringComparison.OrdinalIgnoreCase) &&
                HasReadyBleInput(status))
            {
                string cachedList = await SendSerialCoreAsync("ble list", 4, logOutput: false);
                ApplyBleScanResults(cachedList);
                BleStatus = "Pro2 已连接。为避免打断 Steam 或 tester 输入，本次跳过扫描；如果确实要重扫，请先断开 BLE。";
                return;
            }
            if (string.Equals(ble, "connecting", StringComparison.OrdinalIgnoreCase))
            {
                BleStatus = "BLE 仍在连接中。请等待连接结束，或先断开后再重新扫描。";
                return;
            }
            }
            OverallStatus = "刷写中";
            if (SelectedPort == null) await RefreshPortsAsync(logResult: false);
            if (SelectedPort == null) throw new InvalidOperationException("当前没有可用的 ESP32 串口。请连接控制板，或手动选择正确的 COM 口。");
            if (SelectedPort.LikelyCh343 && !SelectedPort.CanOpen)
            {
                throw new InvalidOperationException("当前选中的串口 " + SelectedPort.PortName + " 处于“" + SelectedPort.Availability + "”状态，暂时不能刷写。");
            }

            PortStatus = "正在将 " + profile + " 刷写到 " + SelectedPort.PortName;
            NextAction = "请等待刷写完成。";
            ModeSwitchStatus = "刷写开始：profile=" + profile + ", port=" + SelectedPort.PortName;
            AppendLog("[MODE_SWITCH_FLASH] profile=" + profile +
                      " port=" + SelectedPort.PortName +
                      " desired=" + desiredMode);
            using var flashTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var progress = new Progress<string>(AppendLog);
            await serialLock.WaitAsync(flashTimeout.Token);
            try
            {
                AppendLog("[SERIAL] closing persistent " + SelectedPort.PortName + " before esptool");
                if (!await SerialCommandClient.CloseAsync(5000))
                {
                    throw new InvalidOperationException("刷写前无法释放 " + SelectedPort.PortName + "。请关闭串口监视器、旧版 Manager、PowerShell send_command/monitor，或拔插 CH343P 控制口后重试。");
                }
                string flashPort = SelectedPort.PortName;
                await Task.Delay(250, flashTimeout.Token);
                await Task.Run(
                    async () => await flasher.FlashAsync(flashPort, profile, mode, progress, flashTimeout.Token),
                    flashTimeout.Token);
            }
            finally
            {
                serialLock.Release();
            }
            settings.LastPortName = SelectedPort.PortName;
            settings.PreviousSuccessfulProfileId = settings.LastSuccessfulProfileId;
            settings.LastSuccessfulProfileId = profile;
            settings.PendingProfileId = profile;
            settings.PendingExpectedUsbMarker = OutputModeCatalog.FindByProfileId(profile)?.ExpectedUsbMarker ?? "";
            settings.PendingRequestedUtc = DateTime.UtcNow;
            ManagerSettingsStore.Save(settings);
            serialBoardReady = false;
            nextSerialAutoProbeAt = DateTime.UtcNow.AddSeconds(2);
            OverallStatus = "刷写完成";
            PortStatus = "刷写完成。请重新插拔原生 USB / OTG。";
            NextAction = "重新插拔原生 USB / OTG 后，请执行一次 USB 检查。";
            ModeSwitchStatus = "刷写完成：profile=" + profile + "，等待新的 USB 枚举。";
            await TryCaptureFirmwareIdentityAfterFlashAsync(profile);
            lastFlashSucceeded = true;
        }
        catch (Exception ex)
        {
            lastFlashSucceeded = false;
            lastFlashFailureCategory = ex switch
            {
                DriverCompatibilityException => "CH343 驱动不兼容",
                DownloadModeException => "ESP32-S3 未进入下载模式",
                _ when IsBoardUnavailableException(ex) => "控制板串口不可用",
                _ => "刷写器失败"
            };
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            PortStatus = "刷写失败：" + FirstLine(ex.Message);
            ModeSwitchStatus = "刷写失败：profile=" + profile + "，error=" + FirstLine(ex.Message);
            if (ex is DriverCompatibilityException)
            {
                AppendLog("[FLASH_DRIVER_BLOCK] " + ex.Message);
                NextAction = "点击“修复 CH343 驱动”自动切换到 Microsoft “USB 串行设备”，完成后拔插 CH343P 控制口再刷写。也可以打开“设备管理器”手动处理。";
            }
            else if (ex is DownloadModeException)
            {
                AppendLog("[FLASH_DOWNLOAD_MODE] " + ex.Message);
                NextAction = "按住 ESP32-S3 的 BOOT 键后重试刷机；日志出现 Connecting... 时点按 EN/RST，看到 Chip is ESP32-S3 后松开 BOOT。";
            }
            else if (IsBoardUnavailableException(ex))
            {
                AppendLog("[离线] 刷写 " + profile + "： " + FirstLine(ex.Message));
                NextAction = "请先连接可用的 ESP32 控制板串口，再重试模式切换。";
            }
            else
            {
                AppendLog("ERROR flash: " + ex);
            }
        }
        finally
        {
            Busy = false;
            flashInProgress = false;
            flashLock.Release();
            NotifyModeStateChanged();
        }
    }
#pragma warning restore CS0162

    private async Task EraseFirmwareAsync()
    {
        MessageBoxResult confirmation = MessageBox.Show(
            owner,
            "这会完整擦除 ESP32-S3 的整片 Flash。\n\n" +
            "固件、USB 手柄伪装、NVS 设置、BLE 手柄地址和模式记录都会被删除。擦除后控制板不会再作为手柄工作，直到重新刷入固件。\n\n" +
            "确定要把控制板清理成“全新 ESP32-S3”状态吗？",
            "确认清理固件",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        if (Busy || flashInProgress || GameMonitorRunning)
        {
            ModeSwitchStatus = "已有刷写、模式切换、游戏监听或设备操作正在进行，暂时不能清理固件。";
            return;
        }
        if (!await flashLock.WaitAsync(0))
        {
            ModeSwitchStatus = "已有刷写或模式切换正在进行，暂时不能清理固件。";
            return;
        }

        flashInProgress = true;
        Busy = true;
        NotifyModeStateChanged();
        try
        {
            OverallStatus = "清理中";
            if (SelectedPort == null)
            {
                await RefreshPortsAsync(logResult: false);
            }
            if (SelectedPort == null)
            {
                throw new InvalidOperationException(
                    "当前没有可用的 ESP32 串口。请连接控制板，或手动选择正确的 COM 口。");
            }

            string erasePort = SelectedPort.PortName;
            PortStatus = "正在完整擦除 " + erasePort + " 的 Flash。";
            ModeSwitchStatus = "清理固件开始：port=" + erasePort;
            NextAction = "请保持 CH343P 控制口连接，等待整片擦除完成。";
            AppendLog("[FIRMWARE_ERASE] start port=" + erasePort);

            using var eraseTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var progress = new Progress<string>(AppendLog);
            await serialLock.WaitAsync(eraseTimeout.Token);
            try
            {
                AppendLog("[SERIAL] closing persistent " + erasePort + " before erase_flash");
                if (!await SerialCommandClient.CloseAsync(5000))
                {
                    throw new InvalidOperationException(
                        "清理前无法释放 " + erasePort + "。请等待当前串口操作结束后重试。");
                }
                await flasher.EraseFlashAsync(
                    erasePort, progress, eraseTimeout.Token);
            }
            finally
            {
                serialLock.Release();
            }

            settings.PendingProfileId = "";
            settings.PendingExpectedUsbMarker = "";
            settings.PendingRequestedUtc = default;
            ManagerSettingsStore.Save(settings);
            serialBoardReady = false;
            currentMode = DeviceUiMode.Unknown;
            usbDetected = false;
            OverallStatus = "已清理";
            PortStatus = erasePort + " 已完整擦除，当前没有应用固件。";
            ModeSwitchStatus = "固件、NVS、BLE 配对与 USB 伪装已全部清理。";
            NextAction = "现在可以作为全新 ESP32-S3 演示；需要恢复时，选择任一模式重新刷写。";
            AppendLog("[FIRMWARE_ERASE] completed port=" + erasePort);
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            PortStatus = "清理固件失败：" + FirstLine(ex.Message);
            ModeSwitchStatus = "清理固件失败：" + FirstLine(ex.Message);
            AppendLog("ERROR firmware erase: " + ex);
            if (ex is DriverCompatibilityException)
            {
                NextAction = "点击“修复 CH343 驱动”自动切换到 Microsoft “USB 串行设备”，完成后拔插 CH343P 控制口再清理。也可以打开“设备管理器”手动处理。";
            }
            else if (ex is DownloadModeException)
            {
                NextAction = "按住 ESP32-S3 的 BOOT 键后重试清理；日志出现 Connecting... 时点按 EN/RST，看到 Chip is ESP32-S3 后松开 BOOT。";
            }
        }
        finally
        {
            Busy = false;
            flashInProgress = false;
            flashLock.Release();
            NotifyModeStateChanged();
        }
    }

    private async Task CheckUsbAsync(bool logResult = true)
    {
        UsbProbeResult usb = await Task.Run(() => DeviceInspector.ProbeUsb());
        UsbStatus = usb.Summary;
        AppendLog("[USB CHECK RAW]");
        AppendLog(UsbStatus);
        UpdateStateFromUsbSummary(UsbStatus);
        ApplyPendingUsbModeVerification();
        if (logResult)
        {
            AppendLog("[USB CHECK]");
            AppendLog("desired=" + desiredMode + " current=" + currentMode + " pending_profile=" + settings.PendingProfileId +
                      " pending_marker=" + settings.PendingExpectedUsbMarker);
        }
        if (UsbStatus.Contains("VID_045E&PID_0B00", StringComparison.OrdinalIgnoreCase) ||
            UsbStatus.Contains("VID_045E&PID_02E3", StringComparison.OrdinalIgnoreCase))
        {
            NextAction = "当前是 Elite 2 GIP 枚举 bring-up。请检查 UsbTreeView 的 0xEE/XGIP10、设备管理器的 xboxgip.sys，以及串口是否进入 Active；暂不测试背键和震动。";
        }
        else if (UsbStatus.Contains("VID_045E&PID_028E", StringComparison.OrdinalIgnoreCase) ||
            UsbStatus.Contains("XInput", StringComparison.OrdinalIgnoreCase))
        {
            NextAction = "当前是 Xbox / XInput 模式。可以先用网页 tester 或 Steam 验证输入，再运行 XInput 普通震动探针。";
        }
        else if (UsbStatus.Contains("VID_054C&PID_0DF2", StringComparison.OrdinalIgnoreCase) ||
                 UsbStatus.Contains("VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase) ||
                 UsbStatus.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
        {
            NextAction = "如果音频端点显示为 4 声道，可以先做一次 dry-run 图样测试。";
        }
        else
        {
            NextAction = "如果刚刷完板子，请重新插拔原生 USB / OTG。";
        }
    }

    private void ApplyPendingUsbModeVerification()
    {
        if (string.IsNullOrWhiteSpace(settings.PendingProfileId) ||
            string.IsNullOrWhiteSpace(settings.PendingExpectedUsbMarker))
        {
            return;
        }

        if (UsbStatus.Contains(settings.PendingExpectedUsbMarker, StringComparison.OrdinalIgnoreCase))
        {
            AppendLog("[MODE_SWITCH] confirmed profile=" + settings.PendingProfileId +
                      " marker=" + settings.PendingExpectedUsbMarker);
            ModeSwitchStatus = "USB 已确认切换成功：profile=" + settings.PendingProfileId +
                               "，marker=" + settings.PendingExpectedUsbMarker;
            settings.PendingProfileId = "";
            settings.PendingExpectedUsbMarker = "";
            settings.PendingRequestedUtc = null;
            ManagerSettingsStore.Save(settings);
            return;
        }

        AppendLog("[MODE_SWITCH] pending profile=" + settings.PendingProfileId +
                  " expected_marker=" + settings.PendingExpectedUsbMarker +
                  " current_usb_summary=" + UsbStatus.Replace(Environment.NewLine, " | "));
        ModeSwitchStatus = "USB 仍未确认到目标模式：期望 " + settings.PendingExpectedUsbMarker +
                           "，当前看到的是 " + CurrentModeLabel;

        if (settings.PendingRequestedUtc is DateTime requestedUtc &&
            DateTime.UtcNow - requestedUtc > TimeSpan.FromMinutes(5) &&
            !string.IsNullOrWhiteSpace(settings.PreviousSuccessfulProfileId))
        {
            NextAction = "USB 还没有确认目标模式。如果你想回退，可以重新刷写 " +
                         settings.PreviousSuccessfulProfileId + "。";
        }
    }

    private async Task AutoRefreshStateAsync()
    {
        if (stateRefreshInProgress || Busy || GameMonitorRunning) return;
        stateRefreshInProgress = true;
        try
        {
            if (SelectedPort != null)
            {
                string output = "";
                if (serialBoardReady)
                {
                    output = await SendSerialCoreAsync("status lite", 1, logOutput: false);
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    BleStatus = SummarizeBle(output, "auto");
                    if (ReadJsonCounter(output, "raw02_ble_writes") >= 0)
                    {
                        HapticStatus = SummarizeHaptic(output);
                    }
                }
            }

            if (DateTime.UtcNow >= nextUsbAutoCheck)
            {
                UsbStatus = (await Task.Run(() => DeviceInspector.ProbeUsb())).Summary;
                UpdateStateFromUsbSummary(UsbStatus);
                nextUsbAutoCheck = DateTime.UtcNow.AddSeconds(10);
            }
        }
        catch (Exception ex)
        {
            serialBoardReady = false;
            nextSerialAutoProbeAt = DateTime.UtcNow.AddSeconds(8);
            PortStatus = "自动刷新失败：" + FirstLine(ex.Message);
        }
        finally
        {
            stateRefreshInProgress = false;
        }
    }

    private async Task ScanBleAsync()
    {
        try
        {
            Busy = true;
            EnsureNoExternalViiperBleOwner("BLE 扫描");
            string status = await SendSerialCoreAsync("status lite", 2, logOutput: false);
            string ble = ReadJsonString(status, "ble");
            if (string.Equals(ble, "connected", StringComparison.OrdinalIgnoreCase) &&
                HasReadyBleInput(status))
            {
                string cachedList = await SendSerialCoreAsync("ble list", 4, logOutput: false);
                ApplyBleScanResults(cachedList);
                BleCandidates = SummarizeBleList(cachedList);
                BleStatus = "Pro2 已连接。为避免打断 Steam 或 tester 输入，本次跳过扫描；如果确实要重扫，请先断开 BLE。";
                return;
            }
            if (string.Equals(ble, "connecting", StringComparison.OrdinalIgnoreCase))
            {
                BleStatus = "BLE 仍在连接中。请等待连接结束，或先断开后再重新扫描。";
                return;
            }
            BleStatus = "正在扫描 BLE Pro2...";
            BleCandidates = "扫描进行中。请保持 Pro2 唤醒，并在配对灯闪烁期间保持稳定。";
            await SendSerialCoreAsync("ble scan", 18);
            string list = await SendSerialCoreAsync("ble list", 6);
            ApplyBleScanResults(list);
            BleCandidates = SummarizeBleList(list);
            BleStatus = SummarizeBle(list, "scan");
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            BleStatus = "BLE 扫描失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("BLE 扫描", ex);
            }
            else
            {
                AppendLog("ERROR ble scan: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ListBleAsync()
    {
        try
        {
            Busy = true;
            string list = await SendSerialCoreAsync("ble list", 6);
            ApplyBleScanResults(list);
            BleCandidates = SummarizeBleList(list);
            BleStatus = SummarizeBle(list, "list");
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            BleStatus = "读取 BLE 列表失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("BLE 列表", ex);
            }
            else
            {
                AppendLog("ERROR ble list: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private void ApplyBleScanResults(string output)
    {
        string selectedTarget = SelectedBleDevice?.Target ?? BleTarget.Trim();
        MatchCollection matches = Regex.Matches(
            output,
            "\\{\"index\":(\\d+),\"target\":\"([^\"]+)\",\"addr\":\"([^\"]+)\",\"name\":\"([^\"]*)\",\"rssi\":(-?\\d+),\"candidate\":(true|false)\\}",
            RegexOptions.IgnoreCase);

        BleDevices.Clear();
        BleScanItem? matchedItem = null;
        foreach (Match match in matches)
        {
            string target = match.Groups[2].Value;
            string address = match.Groups[3].Value;
            string name = match.Groups[4].Value;
            int rssi = int.Parse(match.Groups[5].Value);
            bool candidate = string.Equals(match.Groups[6].Value, "true", StringComparison.OrdinalIgnoreCase);
            var item = new BleScanItem(target, address, name, rssi, candidate);
            BleDevices.Add(item);
            if (!string.IsNullOrWhiteSpace(selectedTarget) &&
                (string.Equals(selectedTarget, target, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(selectedTarget, address, StringComparison.OrdinalIgnoreCase)))
            {
                matchedItem = item;
            }
        }

        if (matchedItem != null)
        {
            SelectedBleDevice = matchedItem;
        }
        else if (BleDevices.Count == 0)
        {
            SelectedBleDevice = null;
        }

        OnPropertyChanged(nameof(BleDevices));
    }

    private async Task ReconnectBleAsync()
    {
        try
        {
            Busy = true;
            EnsureNoExternalViiperBleOwner("重连已配对 Pro2");
            BleStatus = "正在唤醒并重连已保存的 Pro2 手柄...";
            string status = await SendSerialCoreAsync("status lite", 2, logOutput: false);
            BleInputStatus input = BleInputStatusParser.Parse(status);
            if (input.Ready)
            {
                BleStatus = "BLE 已连接且输入新鲜，无需重新连接。";
                return;
            }

            if (input.Connected && input.HasMetrics)
            {
                BleStatus = "BLE 已连接但输入通知停滞，正在重新建立连接...";
                await SendSerialCoreAsync("ble disconnect", 4);
                await WaitForBleTransportToSettleAsync(5);
            }
            await SendSerialCoreAsync("ble auto on", 4);
            string refreshed = await WaitForBleInputReadyAsync(25);
            BleStatus = SummarizeBle(refreshed, "reconnect");
            NextAction = "已恢复已配对手柄。以后普通休眠或短暂断联会继续自动重连。";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            BleStatus = "BLE 重连失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("BLE 重连", ex);
            }
            else
            {
                AppendLog("ERROR ble reconnect: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ConnectBleAsync()
    {
        try
        {
            Busy = true;
            EnsureNoExternalViiperBleOwner("连接 Pro2");
            string target = !string.IsNullOrWhiteSpace(BleTarget)
                ? BleTarget.Trim()
                : SelectedBleDevice != null
                    ? (!string.IsNullOrWhiteSpace(SelectedBleDevice.Target) ? SelectedBleDevice.Target : SelectedBleDevice.Address)
                    : "last";
            await SendSerialCoreAsync("ble connect " + target, 8);
            string refreshed = await WaitForBleInputReadyAsync(25);
            BleStatus = SummarizeBle(refreshed, "manual-connect");
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            BleStatus = "BLE 连接失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("BLE 连接", ex);
            }
            else
            {
                AppendLog("ERROR ble connect: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task SetAudioParserAsync(string parser)
    {
        try
        {
            Busy = true;
            await SendSerialCoreAsync("audio parser " + parser, 4);
            string status = await SendSerialCoreAsync("status lite", 2, logOutput: false);
            string output = status;
            HapticStatus = "控制器音频解析器已切换到 " + parser + "。 " + SummarizeHaptic(output);
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            HapticStatus = "切换音频解析器失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("音频解析器切换", ex);
            }
            else
            {
                AppendLog("ERROR audio parser: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task SendAudioPatternAsync()
    {
        if (!CanUseAudioPatternButton)
        {
            AudioStatus = "当前不是可用的新和联胜 / PS5 / PS5 Edge 模式，或已有任务正在运行，已拒绝发送 PCM / 图样自测。";
            NextAction = "请先切到新和联胜，确认 USB 和串口状态后再发送图样。";
            return;
        }

        string args = "-DeviceName \"" + AudioDeviceName.Replace("\"", "") + "\" -Pattern " +
                      AudioPattern + " -DurationMs " + AudioDurationMs + " -Intensity " + AudioIntensity;
        try
        {
            Busy = true;
            OverallStatus = "音频震动";
            await WaitForBleInputReadyAsync(20);

            await SendSerialCoreAsync("haptic mode auto", 2);
            await SendSerialCoreAsync("haptic source pcm", 2);
            await SendSerialCoreAsync("haptic interval 20", 2);
            await SendSerialCoreAsync("haptic max 64", 2);
            await SendSerialCoreAsync("haptic raw02 on", 2);
            await SendSerialCoreAsync("haptic dryrun off", 2);

            string output = await RunAudioSenderCoreAsync(args);
            AppendLog(output);
            await Task.Delay(300);

            string status = await SendSerialCoreAsync("status", 3);
            long active = ReadJsonCounter(status, "audio_active");
            long livePackets = ReadJsonCounter(status, "raw02_live_packets");
            long writes = ReadJsonCounter(status, "raw02_ble_writes");
            long errors = ReadJsonCounter(status, "raw02_ble_errors");
            string left = ReadJsonString(status, "raw02_left");
            string right = ReadJsonString(status, "raw02_right");

            AudioStatus = $"已发送音频图样。source=pcm 自检，active={active}, raw02={livePackets}, BLE writes={writes}, errors={errors}";
            HapticStatus = $"PCM 自检 -> raw02 -> Pro2：left={ShortHex(left)}, right={ShortHex(right)}";
            OverallStatus = errors == 0 && writes > 0 ? "震动正常" : "检查震动";
            NextAction = errors == 0 && writes > 0
                ? "传输计数看起来正常；接下来仍需要真实游戏输出 DualSense 震动音频。"
                : "如果 BLE 已连接但 writes 一直不增长，请检查日志里的 raw02_error。";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            AudioStatus = "音频震动测试失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("PCM / 图样自测", ex);
            }
            else
            {
                AppendLog("ERROR audio haptic: " + ex);
            }
        }
        finally
        {
            foreach (string command in new[] { "haptic test live stop", "haptic source hd_only", "haptic dryrun on", "haptic raw02 off" })
            {
                try
                {
                    await SendSerialCoreAsync(command, 2);
                }
                catch (Exception cleanupError)
                {
                    AppendLog("WARN haptic cleanup: " + cleanupError.Message);
                }
            }
            Busy = false;
        }
    }

    private async Task StartGameMonitorAsync()
    {
        if (GameMonitorRunning)
        {
            MonitorStatus = "游戏监听已经在运行。";
            return;
        }
        if (!CanStartMonitorButton)
        {
            MonitorStatus = "当前不是可用的新和联胜 / PS5 / PS5 Edge 模式，或已有任务正在运行，已拒绝开始监听。";
            NextAction = "请先切到新和联胜，确认 USB 和串口状态后再开始监听。";
            return;
        }

        int seconds = ParseIntOrDefault(GameMonitorSeconds, 900, 30, 3600);
        gameMonitorCts = new CancellationTokenSource();
        CancellationToken token = gameMonitorCts.Token;

        bool monitorPrepared = false;
        try
        {
            Busy = true;
            OverallStatus = "游戏监听";
            BeginDiagnosticCapture();
            AppendDiagnosticHeader(seconds);
            MonitorStatus = "正在准备断联诊断：检查 BLE、开启 Live raw02，并启用 HD-only 过滤。";

            await WaitForBleInputReadyAsync(20, token);

            await SendSerialCoreAsync("haptic mode auto", 2);
            await SendSerialCoreAsync("haptic source hd_only", 2);
            await SendSerialCoreAsync("haptic interval 10", 2);
            await SendSerialCoreAsync("haptic max 96", 2);
            await SendSerialCoreAsync("haptic gain 2.0", 2);
            await SendSerialCoreAsync("haptic transient_gain 1.5", 2);
            await SendSerialCoreAsync("haptic threshold 256", 2);
            await SendSerialCoreAsync("haptic raw02 on", 2);
            await SendSerialCoreAsync("haptic dryrun off", 2);
            monitorPrepared = true;

            Busy = false;
            GameMonitorRunning = true;
            OverallStatus = "监听中";
            NextAction = "现在开始游戏测试。诊断日志会每秒刷盘；出现断联后先不要拔控制口，回到这里点“停止监听”。";
            AppendLog("[GAME_MONITOR_START] seconds=" + seconds +
                      " live_forwarding=true dry_run=false source=hd_only interval_ms=10 max=96 gain=2.0 transient_gain=1.5 threshold=256 status=lite" +
                      " log=" + diagnosticLogPath);

            await RunGameMonitorLoopAsync(seconds, token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("[GAME_MONITOR_CANCELLED]");
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            MonitorStatus = "游戏监听失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("游戏监听", ex);
            }
            else
            {
                AppendLog("ERROR game monitor: " + ex);
            }
        }
        finally
        {
            if (monitorPrepared)
            {
                try
                {
                    MonitorStatus = "正在停止监听并关闭 Live raw02...";
                    await DisableLiveForwardingAfterMonitorAsync();
                }
                catch (Exception cleanupError)
                {
                    AppendLog("[GAME_MONITOR_CLEANUP_WARN] " + FirstLine(cleanupError.Message));
                    SerialCommandClient.CloseInBackground();
                }
            }
            Busy = false;
            GameMonitorRunning = false;
            gameMonitorCts?.Dispose();
            gameMonitorCts = null;
            EndDiagnosticCapture("monitor_finished");
        }
    }

    private async Task DisableLiveForwardingAfterMonitorAsync()
    {
        await SendSerialCoreAsync("haptic test live stop", 2, logOutput: false);
        await SendSerialCoreAsync("haptic source hd_only", 2, logOutput: false);
        await SendSerialCoreAsync("haptic dryrun on", 2, logOutput: false);
        await SendSerialCoreAsync("haptic raw02 off", 2, logOutput: false);
        AppendLog("[GAME_MONITOR_END_SAFE_OFF] live_forwarding=false dry_run=true source=hd_only");
    }

    private async Task RecalibrateInputAsync()
    {
        try
        {
            Busy = true;
            string output = await SendSerialCoreAsync("input recalibrate", 4);
            AppendLog("[INPUT_RECALIBRATE] keep_sticks_centered=true");
            BleStatus = "摇杆中心校准已重置。请让两个摇杆静止 1 秒。";
            HapticStatus = SummarizeHaptic(output);
            OverallStatus = "输入已重置";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            BleStatus = "摇杆中心重置失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("重置摇杆中心", ex);
            }
            else
            {
                AppendLog("ERROR input recalibrate: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RunGameMonitorLoopAsync(int seconds, CancellationToken token)
    {
        string previous = await SendSerialCoreAsync("status lite", 2, logOutput: false, token);
        LogCriticalFirmwareLines(previous, "baseline");
        AppendLog("[DIAG_FIRMWARE_RX] phase=baseline " + ExtractLastJsonLine(previous));
        MonitorSnapshot baseline = MonitorSnapshot.FromStatus(previous);
        MonitorSnapshot last = baseline;
        int samples = 0;
        int activeSamples = 0;
        int writeSamples = 0;
        int hidStallSamples = 0;
        bool boardRestarted = false;
        string lastHostUsb = OneLine((await Task.Run(() => DeviceInspector.ProbeUsb())).Summary);

        AppendLog("[GAME_MONITOR_BASELINE] " + baseline.ToLogString());
        AppendLog("[DIAG_HOST_USB] t=0s " + lastHostUsb);

        for (int elapsed = 1; elapsed <= seconds; elapsed++)
        {
            await Task.Delay(1000, token);

            string status = await SendSerialCoreAsync("status lite", 2, logOutput: false, token);
            LogCriticalFirmwareLines(status, elapsed + "s");
            AppendLog("[DIAG_FIRMWARE_RX] t=" + elapsed + "s " + ExtractLastJsonLine(status));
            MonitorSnapshot current = MonitorSnapshot.FromStatus(status);
            if (current.UptimeMs + 1000 < last.UptimeMs)
            {
                boardRestarted = true;
            }
            MonitorDelta delta = current.DeltaFrom(last);
            MonitorDelta total = current.DeltaFrom(baseline);
            samples++;
            if (delta.AudioActive > 0) activeSamples++;
            if (delta.BleWrites > 0) writeSamples++;

            string sampleLine =
                "[GAME_MONITOR_SAMPLE] " +
                "t=" + elapsed + "s " +
                current.ToLogString() + " " +
                "delta=(" + delta.ToLogString() + ") " +
                "total=(" + total.ToLogString() + ")";
            AppendLog(sampleLine);
            LogDiagnosticEvents(last, current, delta, elapsed, ref hidStallSamples);

            if (elapsed % 10 == 0)
            {
                string hostUsb = OneLine((await Task.Run(() => DeviceInspector.ProbeUsb())).Summary);
                AppendLog("[DIAG_HOST_USB] t=" + elapsed + "s " + hostUsb);
                if (!string.Equals(hostUsb, lastHostUsb, StringComparison.Ordinal))
                {
                    AppendLog("[DIAG_EVENT] t=" + elapsed + "s type=host_usb_changed previous=\"" +
                              EscapeLogValue(lastHostUsb) + "\" current=\"" + EscapeLogValue(hostUsb) + "\"");
                    lastHostUsb = hostUsb;
                }
            }

            MonitorStatus =
                $"诊断中 {elapsed}/{seconds}s：BLE={current.Ble}, 输入时延={current.InputAgeMs}ms, HID={current.HidReportSent}, 断线={current.BleDisconnects}, 音频 +{total.AudioPackets}, raw02 +{total.Raw02Live}, errors={current.BleErrors}";
            HapticStatus = SummarizeHaptic(status);
            last = current;
        }

        MonitorDelta finalDelta = last.DeltaFrom(baseline);
        bool noControllerAudioStream = finalDelta.AudioPackets == 0 &&
                                       baseline.AudioPackets == 0 &&
                                       last.AudioAlt == 0 &&
                                       !last.AudioStreaming;
        bool gameAudioDetected = finalDelta.AudioPackets > 0 && finalDelta.AudioActive > 0;
        bool hdCandidateDetected = finalDelta.HdCandidates > 0;
        bool liveForwarded = finalDelta.Raw02Live > 0 && finalDelta.BleWrites > 0;
        bool ordinaryHidRumbleForwarded =
            finalDelta.HidRumbleActiveUpdates > 0 && finalDelta.HidRumbleBleWrites > 0;
        bool invalidHidRumbleSeen =
            finalDelta.HidRumbleIgnoredNonzero > 0 && finalDelta.HidRumbleActiveUpdates == 0;
        bool transportDrop = finalDelta.BleDisconnects > 0 ||
                             boardRestarted ||
                             finalDelta.BleConnectFailures > 0 ||
                             finalDelta.HidReportFailed > 0 ||
                             !last.UsbMounted ||
                             last.UsbSuspended ||
                             (string.Equals(last.Ble, "connected", StringComparison.OrdinalIgnoreCase) &&
                              (!last.InputLive || last.InputAgeMs > 1000));
        bool mostlyPcm = finalDelta.DroppedPcm > 0 &&
                         finalDelta.AudioActive > 0 &&
                         finalDelta.DroppedPcm >= (finalDelta.AudioActive * 3 / 4);
        string conclusion = transportDrop
            ? "transport_instability_detected"
            : gameAudioDetected && mostlyPcm
            ? "ordinary_pcm_audio_blocked_not_hd"
            : gameAudioDetected && hdCandidateDetected && liveForwarded
            ? "game_haptic_forwarded"
            : gameAudioDetected
            ? "game_audio_seen_but_no_hd_candidate"
            : ordinaryHidRumbleForwarded
            ? "ordinary_hid_rumble_forwarded"
            : invalidHidRumbleSeen
            ? "host_output_without_valid_rumble_enable"
            : noControllerAudioStream
            ? "no_controller_audio_stream_opened"
            : "no_game_haptic_audio_detected";

        AppendLog("[GAME_MONITOR_RESULT] conclusion=" + conclusion +
                  " samples=" + samples +
                  " active_samples=" + activeSamples +
                  " write_samples=" + writeSamples +
                  " no_controller_audio_stream=" + noControllerAudioStream.ToString().ToLowerInvariant() +
                  " transport_drop=" + transportDrop.ToString().ToLowerInvariant() +
                  " board_restarted=" + boardRestarted.ToString().ToLowerInvariant() +
                  " reset_reason=" + last.ResetReasonName +
                  " mostly_pcm=" + mostlyPcm.ToString().ToLowerInvariant() +
                  " " + finalDelta.ToLogString());
        MonitorStatus = conclusion switch
        {
            "no_controller_audio_stream_opened" =>
                "监听完成：游戏从未打开 Wireless Controller Audio，因此没有 DualSense 震动音频进入开发板。",
            "host_output_without_valid_rumble_enable" =>
                "监听完成：游戏发来了非零马达字节，但没有设置 DualSense 震动有效位，固件按真实手柄规则拒绝了这些无效数据。",
            "ordinary_hid_rumble_forwarded" =>
                "监听完成：普通 DualSense HID 双马达震动已成功写入 Pro2。",
            _ => "监听完成：" + conclusion + "。日志里已经记录了 GAME_MONITOR_RESULT。"
        };
        if (conclusion == "no_controller_audio_stream_opened")
        {
            NextAction = "请先换一个真正支持 PC DualSense 震动音频的标题；本次会话没有向控制器音频端点发送任何数据。";
        }
        else if (conclusion == "host_output_without_valid_rumble_enable")
        {
            NextAction = "请关闭该游戏的 Steam Input、DS4Windows 或其他手柄转译层后重测；真实 DualSense 也会忽略没有 rumble enable 位的马达字节。";
        }
        else if (boardRestarted)
        {
            NextAction = "检测到 ESP32 在游戏中重启，reset_reason=" + last.ResetReasonName +
                         "。请保留本次日志，重点查看 firmware_critical 和 audio_queue 字段。";
        }
        OverallStatus = conclusion == "game_haptic_forwarded"
            || conclusion == "ordinary_hid_rumble_forwarded"
            ? "监听正常"
            : conclusion == "transport_instability_detected" ? "发现断联证据" : "缺少上游源";
    }

    private Task StopGameMonitorAsync()
    {
        CancellationTokenSource? cts = gameMonitorCts;
        if (cts == null || cts.IsCancellationRequested) return Task.CompletedTask;
        cts.Cancel();
        MonitorStatus = "已请求停止，正在取消串口轮询并执行一次安全清理...";
        OverallStatus = "正在停止监听";
        AppendLog("[GAME_MONITOR_STOP_REQUESTED]");
        return Task.CompletedTask;
    }

    private async Task ListAudioAsync()
    {
        await RunAudioSenderAsync("-ListDevices");
        DualSenseAudioCompatProbeResult probe = DualSenseAudioCompatProbe.Run();
        string summary = DualSenseAudioCompatProbe.FormatSummary(probe);
        AudioStatus = "音频兼容探针：" + probe.MatchMode;
        AppendLog("[DS5_AUDIO_COMPAT]");
        AppendLog(summary);
    }

    private async Task RunAudioSenderAsync(string arguments)
    {
        try
        {
            Busy = true;
            string output = await RunAudioSenderCoreAsync(arguments);
            AudioStatus = output.Contains("channels=4", StringComparison.OrdinalIgnoreCase)
                ? "音频辅助工具检测到了 4 声道，或已成功发送。"
                : "音频辅助工具已运行；如果你始终只看到 2 声道，请确认已刷入新和联胜 4 声道固件，并重新插拔原生 USB。";
            AppendLog(output);
        }
        catch (Exception ex)
        {
            AudioStatus = "音频测试失败：" + FirstLine(ex.Message);
            AppendLog("ERROR audio: " + ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<string> RunAudioSenderCoreAsync(string arguments)
    {
        FirmwarePackage package = EmbeddedAssets.EnsurePackage();
        return await RunProcessAsync(package.AudioSenderPath, arguments);
    }

    private async Task<bool> EnsureSerialBoardReadyAsync(string actionName)
    {
        if (SelectedPort == null)
        {
            await RefreshPortsAsync(logResult: false);
        }

        if (SelectedPort == null)
        {
            serialBoardReady = false;
            OverallStatus = "离线";
            PortStatus = "没有检测到可用的 ESP32 控制板串口，暂时无法执行“" + actionName + "”。";
            NextAction = "请先连接控制板，或手动选择正确的串口。";
            return false;
        }

        if (SelectedPort.LikelyCh343 && !SelectedPort.CanOpen)
        {
            serialBoardReady = false;
            OverallStatus = "离线";
            PortStatus = "串口 " + SelectedPort.PortName + " 当前为“" + SelectedPort.Availability + "”，暂时无法执行“" + actionName + "”。";
            NextAction = "请先关闭占用串口的程序，或重新选择正确的控制板串口。";
            return false;
        }

        if (serialBoardReady)
        {
            return true;
        }

        string probe = await ProbeSelectedPortAsync(logOutput: false);
        if (!string.IsNullOrWhiteSpace(probe))
        {
            return true;
        }

        OverallStatus = "离线";
        NextAction = "串口已发现，但控制板尚未响应。请检查 ESP32 是否已连接、是否处于正常运行状态。";
        return false;
    }

    private async Task<string> ProbeSelectedPortAsync(bool logOutput)
    {
        PortItem? port = SelectedPort;
        if (port == null)
        {
            return "";
        }

        if (!await serialLock.WaitAsync(TimeSpan.FromSeconds(3)))
        {
            PortStatus = "上一条串口操作仍未释放，已跳过本次状态探测。";
            return "";
        }
        try
        {
            IProgress<string> progress = logOutput
                ? new Progress<string>(AppendLog)
                : new Progress<string>(_ => { });
            string output = await SerialCommandClient.SendAsync(port.PortName, "status lite", 1, progress);
            if (LooksLikeBoardStatus(output))
            {
                serialBoardReady = true;
                nextSerialAutoProbeAt = DateTime.UtcNow;
                PortStatus = "控制板串口已就绪：" + port.PortName;
                UpdateStateFromText(output);
                return output;
            }

            serialBoardReady = false;
            nextSerialAutoProbeAt = DateTime.UtcNow.AddSeconds(8);
            await SerialCommandClient.CloseAsync(500);
            PortStatus = "已发现串口 " + port.PortName + "，但没有读到控制板状态回包，当前保持离线安全态。";
            return "";
        }
        catch (Exception ex)
        {
            serialBoardReady = false;
            nextSerialAutoProbeAt = DateTime.UtcNow.AddSeconds(8);
            await SerialCommandClient.CloseAsync(500);
            PortStatus = "串口 " + port.PortName + " 当前未响应控制板状态请求：" + FirstLine(ex.Message);
            if (logOutput)
            {
                AppendLog("WARN serial probe: " + ex.Message);
            }
            return "";
        }
        finally
        {
            serialLock.Release();
        }
    }

    private static bool LooksLikeBoardStatus(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        return output.Contains("\"ble\"", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("\"haptic\"", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("\"cmd\"", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("\"ok\":", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBoardUnavailableException(Exception ex)
    {
        string message = ex.Message ?? "";
        return ex is TimeoutException ||
               ex is IOException ||
               ex is UnauthorizedAccessException ||
               message.Contains("Access to the port", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("上一条串口", StringComparison.OrdinalIgnoreCase) ||
               ex is InvalidOperationException &&
               (message.Contains("当前没有可用的 ESP32 控制板串口", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("暂时无法执行", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("暂时不能刷写", StringComparison.OrdinalIgnoreCase));
    }

    private void LogBoardUnavailable(string scope, Exception ex)
    {
        AppendLog("[离线] " + scope + "： " + FirstLine(ex.Message));
        NextAction = "请拔插 CH343P 控制口，确认只选中 CH343/USB 串口后重试。原生 USB 可以保持连接；如果仍拒绝访问，请关闭旧版 Manager、串口监视器或测试脚本。";
    }

    private async Task SendSerialAsync(string command, int readSeconds, Action<string> after)
    {
        if (string.Equals(command, "ble reconnect", StringComparison.OrdinalIgnoreCase))
        {
            await ReconnectBleAsync();
            return;
        }
        if (command.StartsWith("ble connect ", StringComparison.OrdinalIgnoreCase))
        {
            await ConnectBleAsync();
            return;
        }

        try
        {
            Busy = true;
            string output = await SendSerialCoreAsync(command, readSeconds);
            after(output);
        }
        catch (Exception ex)
        {
            if (IsBoardUnavailableException(ex))
            {
                OverallStatus = "离线";
                LogBoardUnavailable(LabelForCommand(command), ex);
            }
            else
            {
                AppendLog("ERROR serial: " + ex);
                OverallStatus = "错误";
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<string> SendSerialCoreAsync(string command, int readSeconds)
    {
        return await SendSerialCoreAsync(command, readSeconds, true);
    }

    private Task<string> SendSerialCoreAsync(string command, int readSeconds, bool logOutput)
    {
        return SendSerialCoreAsync(command, readSeconds, logOutput, CancellationToken.None);
    }

    private async Task<string> SendSerialCoreAsync(
        string command, int readSeconds, bool logOutput, CancellationToken cancellationToken)
    {
        if (!await EnsureSerialBoardReadyAsync(LabelForCommand(command)))
        {
            throw new InvalidOperationException("当前没有可用的 ESP32 控制板串口。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await serialLock.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken))
        {
            throw new TimeoutException(
                "上一条串口操作仍未释放，暂时无法执行“" +
                LabelForCommand(command) + "”。请等待几秒后重试；如果连续出现，请拔插 CH343P 控制口。");
        }
        try
        {
            if (SelectedPort == null)
            {
                throw new InvalidOperationException("当前没有可用的 ESP32 控制板串口。");
            }
            IProgress<string> progress = logOutput
                ? new Progress<string>(AppendLog)
                : new Progress<string>(_ => { });
            string output = await SerialCommandClient.SendAsync(
                SelectedPort.PortName,
                command,
                readSeconds,
                progress,
                cancellationToken);
            serialBoardReady = true;
            nextSerialAutoProbeAt = DateTime.UtcNow;
            UpdateStateFromText(output);
            return output;
        }
        catch
        {
            serialBoardReady = false;
            nextSerialAutoProbeAt = DateTime.UtcNow.AddSeconds(8);
            await SerialCommandClient.CloseAsync(500);
            throw;
        }
        finally
        {
            serialLock.Release();
        }
    }

    private static string LabelForCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "串口命令";
        }

        if (command.StartsWith("ble ", StringComparison.OrdinalIgnoreCase))
        {
            return "BLE 操作";
        }

        if (command.StartsWith("haptic ", StringComparison.OrdinalIgnoreCase))
        {
            return "震动操作";
        }

        if (command.StartsWith("audio ", StringComparison.OrdinalIgnoreCase))
        {
            return "音频操作";
        }

        if (command.StartsWith("input ", StringComparison.OrdinalIgnoreCase))
        {
            return "输入校准";
        }

        if (command.StartsWith("status", StringComparison.OrdinalIgnoreCase))
        {
            return "状态查询";
        }

        return command;
    }

    private async Task RunSafeHapticTestAsync()
    {
        MessageBoxResult result = MessageBox.Show(owner,
            "这会执行一次短促、低强度的全链路实体震动测试：4 声道 DualSense 音频 -> raw02 -> BLE Pro2。测试会自动停止，并在结束后恢复 Dry-run。",
            "安全实体震动测试",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.OK) return;

        Busy = true;
        OverallStatus = "震动测试";
        HapticStatus = "正在准备安全实体震动测试。";
        try
        {
            await WaitForBleInputReadyAsync(20);

            await SendSerialCoreAsync("haptic defaults", 3);
            await SendSerialCoreAsync("haptic source pcm", 3);
            await SendSerialCoreAsync("haptic max 48", 3);
            await SendSerialCoreAsync("haptic interval 50", 3);
            await SendSerialCoreAsync("haptic raw02 on", 3);
            await SendSerialCoreAsync("haptic dryrun off", 3);

            FirmwarePackage package = EmbeddedAssets.EnsurePackage();
            string audioOutput = await RunProcessAsync(
                package.AudioSenderPath,
                "-DeviceName \"Wireless Controller\" -Pattern both_tick -DurationMs 350 -Intensity 28");
            AppendLog(audioOutput);
            await Task.Delay(300);

            string status = await SendSerialCoreAsync("haptic status", 3);
            long writes = ReadJsonCounter(status, "raw02_ble_writes");
            long livePackets = ReadJsonCounter(status, "raw02_live_packets");
            long errors = ReadJsonCounter(status, "raw02_ble_errors");
            if (writes <= 0 || livePackets <= 0 || errors != 0)
            {
                throw new InvalidOperationException(
                    $"全链路未确认：BLE writes={writes}, live packets={livePackets}, errors={errors}。");
            }

            HapticStatus = $"传输链路已通过：BLE writes={writes}, errors={errors}。请手上确认手柄是否真的震动。";
            OverallStatus = "确认体感";
            NextAction = "传输链路已经通过，接下来还需要用手感确认实体震动是否正常。";
            AppendLog("[SAFE_HAPTIC_TEST] result=transport_passed physical_confirmation=required ble_writes=" +
                      writes + " live_packets=" + livePackets + " errors=" + errors);
        }
        catch (Exception ex)
        {
            HapticStatus = "安全实体震动测试失败：" + FirstLine(ex.Message);
            OverallStatus = "错误";
            AppendLog("ERROR safe haptic test: " + ex);
        }
        finally
        {
            foreach (string command in new[] { "haptic test live stop", "haptic source hd_only", "haptic dryrun on", "haptic raw02 off" })
            {
                try
                {
                    await SendSerialCoreAsync(command, 2);
                }
                catch (Exception cleanupError)
                {
                    AppendLog("WARN haptic cleanup: " + cleanupError.Message);
                }
            }
            Busy = false;
        }
    }

    private void UpdateStateFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        string lower = text.ToLowerInvariant();
        string mode = ReadJsonString(text, "mode");
        string profile = ReadJsonString(text, "profile");
        DeviceUiMode previousMode = currentMode;
        if (string.Equals(mode, "switch1_pro", StringComparison.OrdinalIgnoreCase) ||
            profile.Contains("switch1_pro", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("vid_057e&pid_2009"))
        {
            serialReportedMode = DeviceUiMode.Switch1Pro;
            SetCurrentMode(DeviceUiMode.Switch1Pro);
        }
        else if (string.Equals(mode, "dual_pro2", StringComparison.OrdinalIgnoreCase) ||
            profile.Contains("dual_pro2", StringComparison.OrdinalIgnoreCase))
        {
            serialReportedMode = DeviceUiMode.DualPro2Probe;
            SetCurrentMode(DeviceUiMode.DualPro2Probe);
        }
        else if (string.Equals(mode, "dualsense", StringComparison.OrdinalIgnoreCase) ||
            profile.Contains("ds5", StringComparison.OrdinalIgnoreCase) ||
            profile.Contains("dualsense", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("vid_054c&pid_0df2") ||
            lower.Contains("vid_054c&pid_0ce6"))
        {
            serialReportedMode = DeviceUiMode.DualSense;
            SetCurrentMode(DeviceUiMode.DualSense);
        }
        else if (string.Equals(mode, "nintendo", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "pro2", StringComparison.OrdinalIgnoreCase) ||
                 profile.Contains("pro2", StringComparison.OrdinalIgnoreCase) ||
                 profile.Contains("switch2", StringComparison.OrdinalIgnoreCase) ||
                 lower.Contains("vid_057e&pid_2069"))
        {
            serialReportedMode = DeviceUiMode.Pro2;
            SetCurrentMode(DeviceUiMode.Pro2);
        }
        else if (string.Equals(mode, "xinput", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "xbox", StringComparison.OrdinalIgnoreCase) ||
                 profile.Contains("xinput", StringComparison.OrdinalIgnoreCase) ||
                 profile.Contains("xbox", StringComparison.OrdinalIgnoreCase) ||
                 lower.Contains("vid_045e&pid_0b00") ||
                 lower.Contains("vid_045e&pid_02e3") ||
                 lower.Contains("vid_045e&pid_028e"))
        {
            serialReportedMode = profile.Contains("elite", StringComparison.OrdinalIgnoreCase) ||
                                 lower.Contains("vid_045e&pid_0b00") ||
                                 lower.Contains("vid_045e&pid_02e3")
                ? DeviceUiMode.XboxElite
                : DeviceUiMode.Xbox;
            SetCurrentMode(serialReportedMode);
        }
        else if (profile.Contains("hid_only", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "hid_only", StringComparison.OrdinalIgnoreCase))
        {
            serialReportedMode = DeviceUiMode.Recovery;
            SetCurrentMode(DeviceUiMode.Recovery);
        }

        if (previousMode != currentMode)
        {
            AppendLog("[MODE_DETECT_SERIAL] previous=" + previousMode +
                      " current=" + currentMode +
                      " reason=" + DescribeFirmwareIdentity(text));
            ModeSwitchStatus = "串口识别到当前固件模式：" + CurrentModeLabel +
                               "；详细信息见日志。";
        }

        TryAlignDesiredModeWithDetectedUsb();

        string ble = ReadJsonString(text, "ble");
        if (Regex.IsMatch(text, "\"ble_target\"\\s*:", RegexOptions.IgnoreCase))
        {
            SetBleSavedTarget(ReadJsonString(text, "ble_target"));
        }
        if (!string.IsNullOrWhiteSpace(ble))
        {
            BleInputStatus input = BleInputStatusParser.Parse(text);
            SetBleVisualState(ble, input.Ready);
        }
    }

    private void UpdateStateFromUsbSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            SetUsbDetected(false);
            return;
        }

        bool dualSense = summary.Contains("VID_054C&PID_0DF2", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase);
        bool pro2 = summary.Contains("VID_057E&PID_2069", StringComparison.OrdinalIgnoreCase);
        bool switch1Pro = summary.Contains("VID_057E&PID_2009", StringComparison.OrdinalIgnoreCase);
        bool xboxElite = summary.Contains("VID_045E&PID_0B00", StringComparison.OrdinalIgnoreCase) ||
                         summary.Contains("VID_045E&PID_02E3", StringComparison.OrdinalIgnoreCase);
        bool xbox = summary.Contains("VID_045E&PID_028E", StringComparison.OrdinalIgnoreCase);
        DeviceUiMode previousMode = currentMode;

        // The USB scan is machine-wide. A concurrently running V6 instance can
        // expose a virtual 057E:2069 even though this ESP is locked to 057E:2009.
        if (serialBoardReady && serialReportedMode == DeviceUiMode.Switch1Pro && !switch1Pro)
        {
            SetUsbDetected(false);
            SetCurrentMode(DeviceUiMode.Switch1Pro);
            string[] conflicts = FindExternalViiperProcesses();
            if (pro2 && conflicts.Length > 0)
            {
                string signature = string.Join(", ", conflicts);
                if (!string.Equals(lastExternalViiperConflict, signature, StringComparison.Ordinal))
                {
                    lastExternalViiperConflict = signature;
                    AppendLog("[USB_IDENTITY_IGNORED] serial_mode=switch1_pro usb=057e:2069 owner=external_viiper processes=" + signature);
                }
                ModeSwitchStatus = "串口已确认 Switch 1 固件；057E:2069 来自仍在运行的 VIIPER 虚拟手柄，未覆盖当前模式。";
                NextAction = "请从系统托盘彻底退出 V6 VIIPER 版，再连接真实 Pro2；同一只手柄不能同时由 Windows BLE 和 ESP BLE 占用。";
            }
            else if (pro2)
            {
                ModeSwitchStatus = "串口已确认 Switch 1 固件；电脑上的 057E:2069 是另一设备或已退出 VIIPER 留下的虚拟实例，未覆盖当前模式。";
                NextAction = "如果没有另接 Pro2，请先在 V6 版执行停止/退出以移除残留虚拟手柄；这不改变 ESP 当前的 Switch 1 固件。";
            }
            else
            {
                ModeSwitchStatus = "串口已确认 Switch 1 固件，但尚未在当前电脑看到其 057E:2009 原生 USB；接到 Switch 时这是正常现象。";
            }
            return;
        }

        SetUsbDetected(dualSense || pro2 || switch1Pro || xbox || xboxElite);
        if (dualSense)
        {
            SetCurrentMode(DeviceUiMode.DualSense);
        }
        else if (pro2)
        {
            SetCurrentMode(DeviceUiMode.Pro2);
        }
        else if (switch1Pro)
        {
            SetCurrentMode(DeviceUiMode.Switch1Pro);
        }
        else if (xboxElite)
        {
            SetCurrentMode(DeviceUiMode.XboxElite);
        }
        else if (xbox)
        {
            SetCurrentMode(DeviceUiMode.Xbox);
        }
        else if (summary.Contains("HID-only", StringComparison.OrdinalIgnoreCase))
        {
            SetCurrentMode(DeviceUiMode.Recovery);
        }

        if (previousMode != currentMode)
        {
            AppendLog("[MODE_DETECT_USB] previous=" + previousMode +
                      " current=" + currentMode +
                      " summary=" + summary.Replace(Environment.NewLine, " | "));
            ModeSwitchStatus = "USB 当前识别为：" + CurrentModeLabel;
        }

        TryAlignDesiredModeWithDetectedUsb();
    }

    private void SetCurrentMode(DeviceUiMode mode)
    {
        if (currentMode == mode) return;
        currentMode = mode;
        NotifyModeStateChanged();
    }

    private void SetUsbDetected(bool value)
    {
        if (usbDetected == value) return;
        usbDetected = value;
        NotifyModeStateChanged();
    }

    private void SetBleVisualState(string state, bool healthy)
    {
        string normalized = string.IsNullOrWhiteSpace(state) ? "unknown" : state.ToLowerInvariant();
        bool changed = !string.Equals(bleTransportState, normalized, StringComparison.Ordinal) ||
                       bleInputHealthy != healthy;
        bleTransportState = normalized;
        bleInputHealthy = healthy;
        SetBleConnected(healthy);
        if (changed)
        {
            NotifyModeStateChanged();
        }
    }

    private void SetBleConnected(bool value)
    {
        if (bleConnected == value) return;
        bleConnected = value;
        NotifyModeStateChanged();
    }

    private void SetBleSavedTarget(string? value)
    {
        string normalized = value?.Trim() ?? "";
        if (string.Equals(bleSavedTarget, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bleSavedTarget = normalized;
        OnPropertyChanged(nameof(ConnectionGuideTitle));
        OnPropertyChanged(nameof(ConnectionGuideDetail));
        OnPropertyChanged(nameof(ConnectionTargetText));
    }

    private string GetModeLabel(OutputModeId mode)
    {
        return mode switch
        {
            OutputModeId.DualSenseStandard => "新和联胜 / PS5",
            OutputModeId.DualSenseLike => "新和联胜 / PS5 Edge",
            OutputModeId.Pro2 => "Pro2 / Nintendo",
            OutputModeId.Switch1Pro => "Switch 1 / Pro 有线转无线",
            OutputModeId.Xbox => "Xbox / XInput",
            OutputModeId.DualPro2Probe => "Dual Pro2 Probe",
            OutputModeId.XboxElite => "旧版 Xbox Elite 2 实验固件",
            OutputModeId.Recovery => "HID 纯恢复",
            _ => "未知"
        };
    }

    private string GetModeDescription(OutputModeId mode)
    {
        return mode switch
        {
            OutputModeId.DualSenseStandard => "标准 DualSense USB 身份，协调普通震动、四声道 HD 音频转震动链路和 PS5 IMU。",
            OutputModeId.DualSenseLike => "PS5 Edge / DualSense Edge USB 身份，协调普通震动、四声道 HD 音频转震动链路和 Edge 背键。",
            OutputModeId.Pro2 => "面向原始 HID 0x02 震动优先路线调好的 Nintendo-like / Pro2 桥接。",
            OutputModeId.Switch1Pro => "面向初代 Switch 的 057E:2009 单实例 USB 桥接；8ms 主机轮询，输入跟随真实 Pro2 BLE，支持 IMU 与震动近似转换，不支持 NFC。",
            OutputModeId.Xbox => "真实 Xbox 360 / XInput 风格 USB 后端，普通震动会回传到 Pro2 BLE。",
            OutputModeId.DualPro2Probe => "第四实验固件。ESP32-S3 同时连接两只 Pro2，只测 BLE 双连接通知频率、去重率、断连和总吞吐。",
            OutputModeId.XboxElite => "旧版 Elite 2 枚举实验固件，V5.9.17 已停止发行。",
            OutputModeId.Recovery => "用于重刷与 USB 救援的最小恢复固件。",
            _ => "尚未选择目标模式。"
        };
    }

    private string DescribeToolState(OutputModeId selectedMode, bool currentUsbMatches, string readyText, string pendingText)
    {
        if (desiredMode != selectedMode)
        {
            return "当前没有选中这个模式面板。";
        }

        if (!HasUsableSerialCandidate)
        {
            return "当前没有可用的控制板串口。你可以先查看界面和日志，但真实串口/BLE/震动操作会保持离线。";
        }

        if (!currentUsbMatches)
        {
            return pendingText + " 请先完成刷写、重插 USB，再执行一次“USB 检查”。";
        }

        return "可用：" + readyText;
    }

    private string GetModeStateText(OutputModeId mode, bool managerReady)
    {
        if (!managerReady)
        {
            return "预留";
        }

        if (desiredMode == mode && IsCurrentMode(mode))
        {
            return "在线";
        }

        if (desiredMode == mode)
        {
            return "目标";
        }

        if (IsCurrentMode(mode))
        {
            return "当前";
        }

        return "切换";
    }

    private Brush GetModeCardBackground(OutputModeId mode)
    {
        if (desiredMode == mode && IsCurrentMode(mode))
        {
            return mode switch
            {
                OutputModeId.DualSenseStandard => new SolidColorBrush(Color.FromRgb(231, 244, 255)),
                OutputModeId.DualSenseLike => new SolidColorBrush(Color.FromRgb(238, 242, 255)),
                OutputModeId.Pro2 => new SolidColorBrush(Color.FromRgb(255, 241, 242)),
                OutputModeId.Switch1Pro => new SolidColorBrush(Color.FromRgb(255, 247, 237)),
                OutputModeId.Xbox => new SolidColorBrush(Color.FromRgb(236, 252, 233)),
                OutputModeId.DualPro2Probe => new SolidColorBrush(Color.FromRgb(250, 245, 255)),
                OutputModeId.XboxElite => new SolidColorBrush(Color.FromRgb(236, 253, 245)),
                _ => Brushes.White
            };
        }

        if (desiredMode == mode)
        {
            return new SolidColorBrush(Color.FromRgb(248, 250, 252));
        }

        return new SolidColorBrush(Color.FromRgb(255, 255, 255));
    }

    private Brush GetModeCardBorderBrush(OutputModeId mode, bool managerReady)
    {
        if (!managerReady)
        {
            return new SolidColorBrush(Color.FromRgb(120, 53, 15));
        }

        if (desiredMode == mode && IsCurrentMode(mode))
        {
            return GetModeAccentBrush(mode);
        }

        if (desiredMode == mode)
        {
            return new SolidColorBrush(Color.FromRgb(59, 130, 246));
        }

        if (IsCurrentMode(mode))
        {
            return GetModeAccentBrush(mode);
        }

        return new SolidColorBrush(Color.FromRgb(206, 214, 224));
    }

    private Brush GetModeBadgeBrush(OutputModeId mode, bool managerReady)
    {
        if (!managerReady)
        {
            return new SolidColorBrush(Color.FromRgb(120, 53, 15));
        }

        if (desiredMode == mode && IsCurrentMode(mode))
        {
            return new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }

        if (desiredMode == mode)
        {
            return new SolidColorBrush(Color.FromRgb(59, 130, 246));
        }

        if (IsCurrentMode(mode))
        {
            return GetModeAccentBrush(mode);
        }

        return new SolidColorBrush(Color.FromRgb(100, 116, 139));
    }

    private Brush GetModeAccentBrush(OutputModeId mode)
    {
        return mode switch
        {
            OutputModeId.DualSenseStandard => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            OutputModeId.DualSenseLike => new SolidColorBrush(Color.FromRgb(79, 70, 229)),
            OutputModeId.Pro2 => new SolidColorBrush(Color.FromRgb(225, 29, 72)),
            OutputModeId.Switch1Pro => new SolidColorBrush(Color.FromRgb(234, 88, 12)),
            OutputModeId.Xbox => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
            OutputModeId.DualPro2Probe => new SolidColorBrush(Color.FromRgb(126, 34, 206)),
            OutputModeId.XboxElite => new SolidColorBrush(Color.FromRgb(21, 128, 61)),
            _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
        };
    }

    private static bool IsXboxLikeMode(OutputModeId mode)
    {
        return mode == OutputModeId.Xbox;
    }

    private static bool IsPs5FamilyMode(OutputModeId mode)
    {
        return mode == OutputModeId.DualSenseStandard ||
               mode == OutputModeId.DualSenseLike;
    }

    private bool IsXboxLikeCurrentMode()
    {
        return currentMode == DeviceUiMode.Xbox || currentMode == DeviceUiMode.XboxElite;
    }

    private bool IsCurrentMode(OutputModeId mode)
    {
        return mode switch
        {
            OutputModeId.DualSenseStandard => currentMode == DeviceUiMode.DualSense,
            OutputModeId.DualSenseLike => currentMode == DeviceUiMode.DualSense,
            OutputModeId.Pro2 => currentMode == DeviceUiMode.Pro2,
            OutputModeId.Switch1Pro => currentMode == DeviceUiMode.Switch1Pro,
            OutputModeId.Xbox => currentMode == DeviceUiMode.Xbox,
            OutputModeId.DualPro2Probe => currentMode == DeviceUiMode.DualPro2Probe,
            OutputModeId.XboxElite => currentMode == DeviceUiMode.XboxElite,
            OutputModeId.Recovery => currentMode == DeviceUiMode.Recovery,
            _ => false
        };
    }

    private OutputModeId OutputModeFromCurrentMode()
    {
        return currentMode switch
        {
            DeviceUiMode.DualSense => desiredMode == OutputModeId.DualSenseLike
                ? OutputModeId.DualSenseLike
                : OutputModeId.DualSenseStandard,
            DeviceUiMode.Pro2 => OutputModeId.Pro2,
            DeviceUiMode.Switch1Pro => OutputModeId.Switch1Pro,
            DeviceUiMode.DualPro2Probe => OutputModeId.DualPro2Probe,
            DeviceUiMode.Xbox => OutputModeId.Xbox,
            DeviceUiMode.XboxElite => OutputModeId.Xbox,
            DeviceUiMode.Recovery => OutputModeId.Recovery,
            _ => OutputModeId.Unknown
        };
    }

    private void TryAlignDesiredModeWithDetectedUsb()
    {
        if (desiredModeAutoAligned ||
            HasUsableSerialCandidate ||
            !string.IsNullOrWhiteSpace(settings.PendingProfileId))
        {
            return;
        }

        OutputModeId detected = OutputModeFromCurrentMode();
        if (detected == OutputModeId.Unknown || detected == desiredMode)
        {
            return;
        }

        desiredModeAutoAligned = true;
        desiredMode = detected;
        settings.DesiredModeId = detected.ToString();
        ManagerSettingsStore.Save(settings);
        ModeSwitchStatus = "已根据当前 USB 身份自动对齐到 " + GetModeLabel(detected) + " 面板。";
        NotifyModeStateChanged();
    }

    private void NotifyModeStateChanged()
    {
        OnPropertyChanged(nameof(IsDualSenseToolsEnabled));
        OnPropertyChanged(nameof(IsPro2ToolsEnabled));
        OnPropertyChanged(nameof(IsXboxToolsEnabled));
        OnPropertyChanged(nameof(IsUnknownMode));
        OnPropertyChanged(nameof(HasUsableSerialCandidate));
        OnPropertyChanged(nameof(CanSwitchModes));
        OnPropertyChanged(nameof(CanUseBleButtons));
        OnPropertyChanged(nameof(CanUsePro2ToolButtons));
        OnPropertyChanged(nameof(CanUseDualSenseToolButtons));
        OnPropertyChanged(nameof(CanUseXboxToolButtons));
        OnPropertyChanged(nameof(CanUseXboxPaddleButtons));
        OnPropertyChanged(nameof(CanUseDualProbeButtons));
        OnPropertyChanged(nameof(DualNs2ProHostRunning));
        OnPropertyChanged(nameof(CanStartDualNs2ProHost));
        OnPropertyChanged(nameof(CanStopDualNs2ProHost));
        OnPropertyChanged(nameof(CanUseMonitorButtons));
        OnPropertyChanged(nameof(CanStartMonitorButton));
        OnPropertyChanged(nameof(CanStopMonitorButton));
        OnPropertyChanged(nameof(CanUseAudioPatternButton));
        OnPropertyChanged(nameof(CanSendCustomSerialCommand));
        OnPropertyChanged(nameof(CanRepairCh343Driver));
        OnPropertyChanged(nameof(DesiredModeLabel));
        OnPropertyChanged(nameof(DesiredModeDescription));
        OnPropertyChanged(nameof(ModeDeckHint));
        OnPropertyChanged(nameof(CurrentModeLabel));
        OnPropertyChanged(nameof(CurrentModeDescription));
        OnPropertyChanged(nameof(UsbLightText));
        OnPropertyChanged(nameof(BleLightText));
        OnPropertyChanged(nameof(BleDisplayText));
        OnPropertyChanged(nameof(XboxToolStateText));
        OnPropertyChanged(nameof(DualSenseToolStateText));
        OnPropertyChanged(nameof(Pro2ToolStateText));
        OnPropertyChanged(nameof(Pro2LabVisibility));
        OnPropertyChanged(nameof(DualSenseCardStateText));
        OnPropertyChanged(nameof(DualSenseEdgeCardStateText));
        OnPropertyChanged(nameof(Pro2CardStateText));
        OnPropertyChanged(nameof(XboxCardStateText));
        OnPropertyChanged(nameof(Switch1ProCardStateText));
        OnPropertyChanged(nameof(DualPro2ProbeCardStateText));
        OnPropertyChanged(nameof(DualSenseCardBackground));
        OnPropertyChanged(nameof(DualSenseEdgeCardBackground));
        OnPropertyChanged(nameof(Pro2CardBackground));
        OnPropertyChanged(nameof(XboxCardBackground));
        OnPropertyChanged(nameof(Switch1ProCardBackground));
        OnPropertyChanged(nameof(DualPro2ProbeCardBackground));
        OnPropertyChanged(nameof(DualSenseCardBorderBrush));
        OnPropertyChanged(nameof(DualSenseEdgeCardBorderBrush));
        OnPropertyChanged(nameof(Pro2CardBorderBrush));
        OnPropertyChanged(nameof(XboxCardBorderBrush));
        OnPropertyChanged(nameof(Switch1ProCardBorderBrush));
        OnPropertyChanged(nameof(DualPro2ProbeCardBorderBrush));
        OnPropertyChanged(nameof(DualSenseCardBadgeBrush));
        OnPropertyChanged(nameof(DualSenseEdgeCardBadgeBrush));
        OnPropertyChanged(nameof(Pro2CardBadgeBrush));
        OnPropertyChanged(nameof(XboxCardBadgeBrush));
        OnPropertyChanged(nameof(Switch1ProCardBadgeBrush));
        OnPropertyChanged(nameof(DualPro2ProbeCardBadgeBrush));
        OnPropertyChanged(nameof(DualSenseLabVisibility));
        OnPropertyChanged(nameof(DualSenseLabDisabledVisibility));
        OnPropertyChanged(nameof(XboxLabVisibility));
        OnPropertyChanged(nameof(UsbIndicatorBrush));
        OnPropertyChanged(nameof(BleIndicatorBrush));
        OnPropertyChanged(nameof(BleDisplayBrush));
        OnPropertyChanged(nameof(BleDisplayBackgroundBrush));
        OnPropertyChanged(nameof(BleDisplayBorderBrush));
        OnPropertyChanged(nameof(BleDisplayForegroundBrush));
        OnPropertyChanged(nameof(ConnectionGuideTitle));
        OnPropertyChanged(nameof(ConnectionGuideDetail));
        OnPropertyChanged(nameof(ConnectionTargetText));
        OnPropertyChanged(nameof(ModeCardBrush));
        OnPropertyChanged(nameof(ModeCardBorderBrush));
        OnPropertyChanged(nameof(OverallStatusBackgroundBrush));
        OnPropertyChanged(nameof(OverallStatusForegroundBrush));
    }

    private static long ReadJsonCounter(string text, string name)
    {
        MatchCollection matches = Regex.Matches(
            text,
            "\"" + Regex.Escape(name) + "\"\\s*:\\s*(-?\\d+)",
            RegexOptions.IgnoreCase);
        return matches.Count == 0 ? -1 : long.Parse(matches[^1].Groups[1].Value);
    }

    private static string ReadJsonString(string text, string name)
    {
        MatchCollection matches = Regex.Matches(
            text,
            "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]*)\"",
            RegexOptions.IgnoreCase);
        return matches.Count == 0 ? "" : matches[^1].Groups[1].Value;
    }

    private static string ReadJsonBoolString(string text, string name)
    {
        MatchCollection matches = Regex.Matches(
            text,
            "\"" + Regex.Escape(name) + "\"\\s*:\\s*(true|false)",
            RegexOptions.IgnoreCase);
        return matches.Count == 0 ? "" : matches[^1].Groups[1].Value.ToLowerInvariant();
    }

    private static string NormalizeXboxPaddleMode(string? value)
    {
        string mode = (value ?? "").Trim().ToLowerInvariant();
        return mode is "tap" or "single" or "once" ? "tap" :
               mode is "turbo" or "repeat" or "rapid" ? "turbo" :
               "hold";
    }

    private static string ShortHex(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "无";
        return text.Length <= 12 ? text : text[..12] + "...";
    }

    private static string SummarizeDualProbeStatus(string status)
    {
        string mode = ReadJsonString(status, "dual_sim_mode");
        string probe = ReadJsonString(status, "dual_pro2");
        long simHz = ReadJsonCounter(status, "dual_sim_rate_hz");
        long targets = ReadJsonCounter(status, "dual_targets");
        long totalHz = ReadJsonCounter(status, "dual_total_notify_hz");
        long p0Hz = ReadJsonCounter(status, "dual_pad0_notify_hz");
        long p1Hz = ReadJsonCounter(status, "dual_pad1_notify_hz");
        long p0Unique = ReadJsonCounter(status, "dual_pad0_unique_hz");
        long p1Unique = ReadJsonCounter(status, "dual_pad1_unique_hz");
        string p0Sim = ReadJsonBoolString(status, "dual_pad0_simulated") == "true" ? "模拟" : "真实";
        string p1Sim = ReadJsonBoolString(status, "dual_pad1_simulated") == "true" ? "模拟" : "真实";
        return $"Dual Probe：state={BlankDash(probe)}, sim={BlankDash(mode)}" +
               (simHz >= 0 ? $"@{simHz}Hz" : "") +
               $", targets={targets}, total={totalHz}Hz, " +
               $"P0({p0Sim})={p0Hz}Hz/u{p0Unique}, P1({p1Sim})={p1Hz}Hz/u{p1Unique}";
    }

    private static string BlankDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string DescribeFirmwareIdentity(string statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return "没有收到任何固件状态。";
        }

        string mode = ReadJsonString(statusText, "mode");
        string profile = ReadJsonString(statusText, "profile");
        string version = ReadJsonString(statusText, "version");
        string ble = ReadJsonString(statusText, "ble");
        return "mode=" + (string.IsNullOrWhiteSpace(mode) ? "?" : mode) +
               ", profile=" + (string.IsNullOrWhiteSpace(profile) ? "-" : profile) +
               ", version=" + (string.IsNullOrWhiteSpace(version) ? "-" : version) +
               ", ble=" + (string.IsNullOrWhiteSpace(ble) ? "-" : ble);
    }

    private static void RequireBleConnected(string statusText)
    {
        string ble = ReadJsonString(statusText, "ble");
        if (!string.Equals(ble, "connected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pro2 BLE 尚未连接，因此已拒绝真实震动发送。");
        }
    }

    private async Task SendLiveHapticPulseAsync(string testName, string label)
    {
        try
        {
            Busy = true;
            OverallStatus = "震动脉冲";
            await WaitForBleInputReadyAsync(15);

            await SendSerialCoreAsync("haptic test live " + testName, 4);
            if (testName.Equals("punch", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(90);
                await SendSerialCoreAsync("haptic test live " + testName, 4);
                await Task.Delay(160);
            }
            else
            {
                await Task.Delay(110);
            }
            await SendSerialCoreAsync("haptic test live stop", 3);

            string status = await SendSerialCoreAsync("status", 3);
            long writes = ReadJsonCounter(status, "raw02_ble_writes");
            long errors = ReadJsonCounter(status, "raw02_ble_errors");
            string left = ReadJsonString(status, "raw02_left");
            string right = ReadJsonString(status, "raw02_right");
            HapticStatus = $"{label} 已发送一次实体震动；BLE writes={writes}, errors={errors}, left={ShortHex(left)}, right={ShortHex(right)}";
            OverallStatus = errors == 0 ? "震动正常" : "检查震动";
        }
        catch (Exception ex)
        {
            OverallStatus = "错误";
            HapticStatus = label + " 实体震动失败：" + FirstLine(ex.Message);
            AppendLog("ERROR haptic pulse: " + ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task TurnLiveOnAsync()
    {
        MessageBoxResult result = MessageBox.Show(owner,
            "这会在 HD-only 过滤后开启真实 haptic audio / raw02 转发。普通音频会被阻断，只有疑似专用触觉通道的内容才会转发到 BLE Pro2。",
            "确认开启实时 raw02（HD-only）",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;
        try
        {
            Busy = true;
            await WaitForBleInputReadyAsync(15);
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            HapticStatus = "开启 Live raw02 失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("开启实时 raw02", ex);
            }
            else
            {
                AppendLog("ERROR live on: " + ex);
            }
            return;
        }
        finally
        {
            Busy = false;
        }
        await SendSerialAsync("haptic mode auto", 3, _ => { });
        await SendSerialAsync("haptic source hd_only", 3, _ => { });
        await SendSerialAsync("haptic interval 20", 3, _ => { });
        await SendSerialAsync("haptic max 64", 3, _ => { });
        await SendSerialAsync("haptic raw02 on", 4, _ => { });
        await SendSerialAsync("haptic dryrun off", 4, _ => HapticStatus = "实时 raw02 已开启，试运行已关闭，source=hd_only。");
    }

    private async Task TurnLiveOffAsync()
    {
        await SendSerialAsync("haptic test live stop", 3, _ => { });
        await SendSerialAsync("haptic source hd_only", 3, _ => { });
        await SendSerialAsync("haptic dryrun on", 3, _ => { });
        await SendSerialAsync("haptic raw02 off", 3, _ => HapticStatus = "实时 raw02 已关闭，试运行已开启。");
    }

    private string SummarizeHaptic(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "已请求读取震动状态，但没有收到任何输出。";
        string ble = ReadJsonString(output, "ble");
        string haptic = ReadJsonString(output, "haptic");
        string mode = ReadJsonString(output, "haptic_mode");
        string source = ReadJsonString(output, "haptic_source");
        string audioStreaming = ReadJsonBoolString(output, "audio_streaming");
        string audioParser = ReadJsonString(output, "audio_parser");
        string audioPair = ReadJsonString(output, "audio_pair");
        string lastMode = ReadJsonString(output, "raw02_last_mode");
        string left = ReadJsonString(output, "raw02_left");
        string right = ReadJsonString(output, "raw02_right");
        string hdCandidate = ReadJsonBoolString(output, "hd_candidate");
        long audioPackets = ReadJsonCounter(output, "audio_packets");
        long audioActive = ReadJsonCounter(output, "audio_active");
        long hdPackets = ReadJsonCounter(output, "raw02_hd_candidate_packets");
        long droppedPcm = ReadJsonCounter(output, "raw02_dropped_pcm");
        long livePackets = ReadJsonCounter(output, "raw02_live_packets");
        long writes = ReadJsonCounter(output, "raw02_ble_writes");
        long errors = ReadJsonCounter(output, "raw02_ble_errors");
        if (!string.IsNullOrWhiteSpace(ble) || writes >= 0)
        {
            return $"BLE={ble}, 震动={haptic}, 模式={mode}, 源={source}, 音频流={audioStreaming}, 音频包={audioPackets}, 活跃包={audioActive}, 解析器={audioParser}, 声道对={audioPair}, HD 候选={hdCandidate}, HD 包={hdPackets}, 屏蔽 PCM={droppedPcm}, raw02_live={livePackets}, BLE writes={writes}, errors={errors}, last={lastMode}, L={ShortHex(left)}, R={ShortHex(right)}";
        }
        string compact = output.Replace("\r", " ").Replace("\n", " ");
        return compact.Length > 220 ? compact[..220] + "..." : compact;
    }

    private string SummarizePro2Rumble(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "没有收到 Pro2 震动状态。";
        }

        string ble = ReadJsonString(status, "ble");
        string rumble = ReadJsonString(status, "rumble");
        long updates = ReadJsonCounter(status, "rumble_updates");
        long writes = ReadJsonCounter(status, "rumble_writes");
        long stops = ReadJsonCounter(status, "rumble_stops");
        long errors = ReadJsonCounter(status, "rumble_errors");
        long presetIgnored = ReadJsonCounter(status, "rumble_preset_ignored");
        long scale = ReadJsonCounter(status, "rumble_scale_percent");
        long holdMs = ReadJsonCounter(status, "rumble_hold_ms");
        long tickMs = ReadJsonCounter(status, "rumble_tick_ms");
        long stopPackets = ReadJsonCounter(status, "rumble_stop_packets");
        BleInputStatus input = BleInputStatusParser.Parse(status);
        string inputText = input.HasMetrics
            ? $"{input.Schema}:{(input.InputLive ? "live" : "stale")}, input_updates={input.Updates}, input_age_ms={input.AgeMs}"
            : "legacy";
        return $"BLE={ble}, input={inputText}, rumble={rumble}, updates={updates}, writes={writes}, stops={stops}, errors={errors}, preset_ignored={presetIgnored}, scale={scale}, hold_ms={holdMs}, tick_ms={tickMs}, stop_packets={stopPackets}";
    }

    private async Task<string> ReadPro2BridgeStatusAsync(bool logOutput)
    {
        string status = await SendSerialCoreAsync("status", 3, logOutput);
        if (ReadJsonCounter(status, "rumble_writes") < 0)
        {
            throw new InvalidOperationException("当前串口回包看起来不是 Pro2 / Nintendo 桥接固件。请先切换到 Pro2 模式并执行一次 USB 检查。");
        }
        return status;
    }

    private async Task EnsurePro2BridgeReadyAsync()
    {
        await WaitForBleInputReadyAsync(15);
        await ReadPro2BridgeStatusAsync(logOutput: false);
    }

    private async Task RefreshPro2RumbleStatusAsync()
    {
        try
        {
            Busy = true;
            string status = await ReadPro2BridgeStatusAsync(logOutput: true);
            Pro2RumbleStatus = SummarizePro2Rumble(status);
            BleInputStatus input = BleInputStatusParser.Parse(status);
            OverallStatus = input.Ready ? "Pro2 震动" : "Pro2 状态";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            Pro2RumbleStatus = "读取 Pro2 震动状态失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("读取 Pro2 震动状态", ex);
            }
            else
            {
                AppendLog("ERROR pro2 rumble status: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ApplyPro2RumbleTuneAsync()
    {
        try
        {
            Busy = true;
            await ReadPro2BridgeStatusAsync(logOutput: false);
            int scale = ParseIntOrDefault(Pro2RumbleScale, 140, 10, 250);
            int holdMs = ParseIntOrDefault(Pro2RumbleHoldMs, 220, 50, 1000);
            int tickMs = ParseIntOrDefault(Pro2RumbleTickMs, 12, 5, 50);
            int stopPackets = ParseIntOrDefault(Pro2RumbleStopPackets, 3, 1, 8);

            string output = await SendSerialCoreAsync($"rumble tune {scale} {holdMs} {tickMs} {stopPackets}", 3);
            Pro2RumbleScale = scale.ToString();
            Pro2RumbleHoldMs = holdMs.ToString();
            Pro2RumbleTickMs = tickMs.ToString();
            Pro2RumbleStopPackets = stopPackets.ToString();
            Pro2RumbleStatus = "已应用 Pro2 震动参数。 " + SummarizePro2Rumble(output);
            OverallStatus = "Pro2 震动";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            Pro2RumbleStatus = "应用 Pro2 震动参数失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("应用 Pro2 震动参数", ex);
            }
            else
            {
                AppendLog("ERROR pro2 rumble tune: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RunPro2RumblePresetAsync(string label, int scale, int holdMs, int tickMs, int stopPackets)
    {
        try
        {
            Busy = true;
            OverallStatus = "Pro2 震动";
            await EnsurePro2BridgeReadyAsync();

            await SendSerialCoreAsync($"rumble tune {scale} {holdMs} {tickMs} {stopPackets}", 3);
            string output = await SendSerialCoreAsync($"rumble hold {holdMs}", 3);
            await Task.Delay(Math.Min(holdMs + 120, 1200));
            string status = await SendSerialCoreAsync("status", 3);
            Pro2RumbleStatus = $"{label} 已发送。 {SummarizePro2Rumble(status)}";
            AppendLog("[PRO2_RUMBLE_PRESET] label=" + label + " tune=" + scale + "/" + holdMs + "/" + tickMs + "/" + stopPackets);
            AppendLog(output);
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            Pro2RumbleStatus = label + " 失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("发送 " + label, ex);
            }
            else
            {
                AppendLog("ERROR pro2 rumble preset: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RunPro2ManualHoldAsync()
    {
        try
        {
            Busy = true;
            OverallStatus = "Pro2 震动";
            await EnsurePro2BridgeReadyAsync();
            int holdMs = ParseIntOrDefault(Pro2RumbleHoldMs, 220, 100, 1000);
            string output = await SendSerialCoreAsync($"rumble hold {holdMs}", 3);
            await Task.Delay(Math.Min(holdMs + 120, 1200));
            string status = await SendSerialCoreAsync("status", 3);
            Pro2RumbleStatus = "已发送手动脉冲。 " + SummarizePro2Rumble(status);
            AppendLog(output);
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            Pro2RumbleStatus = "发送手动脉冲失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("发送手动脉冲", ex);
            }
            else
            {
                AppendLog("ERROR pro2 rumble hold: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task StopPro2RumbleAsync()
    {
        try
        {
            Busy = true;
            string output = await SendSerialCoreAsync("rumble stop", 3);
            string status = await SendSerialCoreAsync("status", 3);
            Pro2RumbleStatus = "已发送停止。 " + SummarizePro2Rumble(status);
            OverallStatus = "Pro2 震动";
            AppendLog(output);
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            Pro2RumbleStatus = "停止 Pro2 震动失败：" + FirstLine(ex.Message);
            if (IsBoardUnavailableException(ex))
            {
                LogBoardUnavailable("停止 Pro2 震动", ex);
            }
            else
            {
                AppendLog("ERROR pro2 rumble stop: " + ex);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RunXInputProbeAsync()
    {
        if (!CanUseXboxToolButtons)
        {
            XboxStatus = "当前不是可用的 Xbox / XInput 模式，或已有任务正在运行，已拒绝运行探针。";
            NextAction = "请先切到 Xbox / XInput，确认 USB 已枚举为 045E:028E 后再运行探针。";
            return;
        }

        try
        {
            Busy = true;
            OverallStatus = "XInput 探针";
            XboxStatus = "正在运行 XInput 探针，请稍候。结束后会写出日志。";

            int seconds = ParseIntOrDefault(XInputProbeSeconds, 18, 5, 180);
            int low = ParseIntOrDefault(XInputProbeLow, 32000, 0, 65535);
            int high = ParseIntOrDefault(XInputProbeHigh, 52000, 0, 65535);
            string logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PRO2WirelessReceiverControlBoard",
                "logs");
            Directory.CreateDirectory(logRoot);
            string logPath = Path.Combine(logRoot, "xinput_probe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

            FirmwarePackage package = EmbeddedAssets.EnsurePackage();
            AppendLog("[XINPUT_PROBE_START] seconds=" + seconds + " low=" + low + " high=" + high + " log=" + logPath);
            string output = await RunProcessAsync(
                package.XInputProbePath,
                "--seconds " + seconds +
                " --pulse-ms 420 --gap-ms 280 --low " + low +
                " --high " + high +
                " --log \"" + logPath + "\"");

            XboxStatus = "XInput 探针已完成。日志：" + logPath;
            NextAction = "请对照日志里的 XInput 识别、输入和震动调用结果。如果主机发出普通震动，固件会尝试回传到 Pro2。";
            OverallStatus = "探针完成";
            AppendLog("[XINPUT_PROBE]");
            AppendLog(string.IsNullOrWhiteSpace(output) ? "(probe produced no console output)" : output);
            AppendLog("XInput 探针日志已保存：" + logPath);
        }
        catch (Exception ex)
        {
            OverallStatus = "错误";
            XboxStatus = "XInput 探针失败：" + FirstLine(ex.Message);
            AppendLog("ERROR xinput probe: " + ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RefreshXboxPaddleMappingAsync()
    {
        if (!CanUseXboxPaddleButtons)
        {
            XboxStatus = "当前不是可用的 Xbox / XInput 模式，或串口/任务状态不允许读取背键映射。";
            NextAction = "请先切到 Xbox / XInput，并确认 USB 已枚举为 045E:028E。";
            return;
        }

        try
        {
            Busy = true;
            string output = await SendSerialCoreAsync("xbox paddle status", 4);
            ApplyXboxPaddleStatusFromJson(output);
            XboxStatus = "已读取固件侧 Pro2 背键映射：" + SummarizeXboxPaddleStatus(output);
            OverallStatus = "背键映射";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            XboxStatus = "读取背键映射失败：" + FirstLine(ex.Message);
            AppendLog("ERROR xbox paddle refresh: " + ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ApplyXboxPaddleMappingAsync()
    {
        if (!CanUseXboxPaddleButtons)
        {
            XboxStatus = "当前不是可用的 Xbox / XInput 模式，或串口/任务状态不允许应用背键映射。";
            NextAction = "请先切到 Xbox / XInput，并确认 USB 已枚举为 045E:028E。";
            return;
        }

        try
        {
            Busy = true;
            OverallStatus = "背键映射";
            string leftCommand = BuildXboxPaddleCommand(
                "left",
                XboxPaddleLeftEnabled,
                XboxPaddleLeftMode,
                XboxPaddleLeftTargets,
                XboxPaddleLeftTapMs,
                XboxPaddleLeftTurboOnMs,
                XboxPaddleLeftTurboOffMs);
            string rightCommand = BuildXboxPaddleCommand(
                "right",
                XboxPaddleRightEnabled,
                XboxPaddleRightMode,
                XboxPaddleRightTargets,
                XboxPaddleRightTapMs,
                XboxPaddleRightTurboOnMs,
                XboxPaddleRightTurboOffMs);

            AppendLog("[XBOX_PADDLE_APPLY] " + leftCommand);
            await SendSerialCoreAsync(leftCommand, 4);
            AppendLog("[XBOX_PADDLE_APPLY] " + rightCommand);
            await SendSerialCoreAsync(rightCommand, 4);
            string status = await SendSerialCoreAsync("xbox paddle status", 4);
            ApplyXboxPaddleStatusFromJson(status);
            XboxStatus = "已应用 Pro2 背键映射：" + SummarizeXboxPaddleStatus(status);
            NextAction = "现在按 Pro2 的 GL/GR 背键即可在 Xbox 模式下输出配置好的目标键。";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            XboxStatus = "应用背键映射失败：" + FirstLine(ex.Message);
            AppendLog("ERROR xbox paddle apply: " + ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ResetXboxPaddleMappingAsync()
    {
        if (!CanUseXboxPaddleButtons)
        {
            XboxStatus = "当前不是可用的 Xbox / XInput 模式，或串口/任务状态不允许关闭背键映射。";
            NextAction = "请先切到 Xbox / XInput，并确认 USB 已枚举为 045E:028E。";
            return;
        }

        try
        {
            Busy = true;
            string status = await SendSerialCoreAsync("xbox paddle reset", 4);
            ApplyXboxPaddleStatusFromJson(status);
            XboxStatus = "已关闭固件侧 Pro2 背键映射：" + SummarizeXboxPaddleStatus(status);
            OverallStatus = "背键关闭";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            XboxStatus = "关闭背键映射失败：" + FirstLine(ex.Message);
            AppendLog("ERROR xbox paddle reset: " + ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private static string BuildXboxPaddleCommand(
        string side,
        bool enabled,
        string mode,
        string targets,
        string tapMs,
        string turboOnMs,
        string turboOffMs)
    {
        if (!enabled)
        {
            return "xbox paddle " + side + " off";
        }

        string normalizedMode = NormalizeXboxPaddleMode(mode);
        string normalizedTargets = NormalizeXboxPaddleTargets(targets);
        if (string.IsNullOrWhiteSpace(normalizedTargets))
        {
            throw new InvalidOperationException("已开启背键映射，但目标键为空。可填写 B、A、ZR、B+A 等。");
        }

        return normalizedMode switch
        {
            "tap" => "xbox paddle " + side + " tap " + normalizedTargets + " " +
                     ParseIntOrDefault(tapMs, 70, 20, 1000),
            "turbo" => "xbox paddle " + side + " turbo " + normalizedTargets + " " +
                       ParseIntOrDefault(turboOnMs, 45, 20, 1000) + " " +
                       ParseIntOrDefault(turboOffMs, 45, 20, 1000),
            _ => "xbox paddle " + side + " hold " + normalizedTargets
        };
    }

    private static string NormalizeXboxPaddleTargets(string? text)
    {
        string compact = Regex.Replace(text ?? "", "\\s+", "").Trim().ToUpperInvariant();
        if (compact.Length == 0)
        {
            return "";
        }
        if (!Regex.IsMatch(compact, "^[A-Z0-9_+,&|\\-]+$"))
        {
            throw new InvalidOperationException("背键目标键只能使用字母数字以及 + , | & - _ 组合符。");
        }
        return compact;
    }

    private void ApplyXboxPaddleStatusFromJson(string status)
    {
        string leftEnabled = ReadJsonBoolString(status, "xbox_paddle_left_enabled");
        string rightEnabled = ReadJsonBoolString(status, "xbox_paddle_right_enabled");
        if (!string.IsNullOrWhiteSpace(leftEnabled)) XboxPaddleLeftEnabled = leftEnabled == "true";
        if (!string.IsNullOrWhiteSpace(rightEnabled)) XboxPaddleRightEnabled = rightEnabled == "true";

        string leftMode = ReadJsonString(status, "xbox_paddle_left_mode");
        string rightMode = ReadJsonString(status, "xbox_paddle_right_mode");
        if (!string.IsNullOrWhiteSpace(leftMode)) XboxPaddleLeftMode = leftMode;
        if (!string.IsNullOrWhiteSpace(rightMode)) XboxPaddleRightMode = rightMode;

        string leftTargets = ReadJsonString(status, "xbox_paddle_left_targets");
        string rightTargets = ReadJsonString(status, "xbox_paddle_right_targets");
        if (!string.IsNullOrWhiteSpace(leftTargets)) XboxPaddleLeftTargets = leftTargets;
        if (!string.IsNullOrWhiteSpace(rightTargets)) XboxPaddleRightTargets = rightTargets;

        long leftTap = ReadJsonCounter(status, "xbox_paddle_left_tap_ms");
        long rightTap = ReadJsonCounter(status, "xbox_paddle_right_tap_ms");
        long leftOn = ReadJsonCounter(status, "xbox_paddle_left_turbo_on_ms");
        long leftOff = ReadJsonCounter(status, "xbox_paddle_left_turbo_off_ms");
        long rightOn = ReadJsonCounter(status, "xbox_paddle_right_turbo_on_ms");
        long rightOff = ReadJsonCounter(status, "xbox_paddle_right_turbo_off_ms");
        if (leftTap >= 0) XboxPaddleLeftTapMs = leftTap.ToString();
        if (rightTap >= 0) XboxPaddleRightTapMs = rightTap.ToString();
        if (leftOn >= 0) XboxPaddleLeftTurboOnMs = leftOn.ToString();
        if (leftOff >= 0) XboxPaddleLeftTurboOffMs = leftOff.ToString();
        if (rightOn >= 0) XboxPaddleRightTurboOnMs = rightOn.ToString();
        if (rightOff >= 0) XboxPaddleRightTurboOffMs = rightOff.ToString();
    }

    private static string SummarizeXboxPaddleStatus(string status)
    {
        string leftEnabled = ReadJsonBoolString(status, "xbox_paddle_left_enabled") == "true" ? "开" : "关";
        string rightEnabled = ReadJsonBoolString(status, "xbox_paddle_right_enabled") == "true" ? "开" : "关";
        string leftMode = ReadJsonString(status, "xbox_paddle_left_mode");
        string rightMode = ReadJsonString(status, "xbox_paddle_right_mode");
        string leftTargets = ReadJsonString(status, "xbox_paddle_left_targets");
        string rightTargets = ReadJsonString(status, "xbox_paddle_right_targets");
        return "GL=" + leftEnabled + "/" + (string.IsNullOrWhiteSpace(leftMode) ? "-" : leftMode) +
               "/" + (string.IsNullOrWhiteSpace(leftTargets) ? "NONE" : leftTargets) +
               "；GR=" + rightEnabled + "/" + (string.IsNullOrWhiteSpace(rightMode) ? "-" : rightMode) +
               "/" + (string.IsNullOrWhiteSpace(rightTargets) ? "NONE" : rightTargets);
    }

    private Task StartDualNs2ProHostAsync(bool synthetic)
    {
        if (DualNs2ProHostRunning)
        {
            DualNs2ProHostStatus = "主机侧双二代 Pro2 已经在运行。";
            return Task.CompletedTask;
        }

        Busy = true;
        try
        {
            FirmwarePackage package = EmbeddedAssets.EnsurePackage();
            if (!File.Exists(package.DualNs2ProHostPath))
            {
                throw new FileNotFoundException(
                    "内置 DualNs2ProHost.exe 不存在。请重新打包 V5.9 管理器。",
                    package.DualNs2ProHostPath);
            }

            Directory.CreateDirectory(DualNs2ProHostLogRootDirectory);
            string stopFile = Path.Combine(
                DualNs2ProHostLogRootDirectory,
                "dual_ns2pro_stop_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".signal");
            TryDeleteFile(stopFile);

            string viiperExe = Path.Combine(package.ToolsRoot, "viiper", "haptic-v0.8.0", "viiper-haptic.exe");
            string arguments =
                "--hz 125 " +
                (synthetic ? "--synthetic " : "--neutral ") +
                "--stop-file " + QuoteArgument(stopFile) + " " +
                "--log-root " + QuoteArgument(DualNs2ProHostLogRootDirectory);
            if (File.Exists(viiperExe))
            {
                arguments += " --viiper " + QuoteArgument(viiperExe);
            }

            var psi = new ProcessStartInfo(package.DualNs2ProHostPath, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(package.DualNs2ProHostPath) ?? package.ToolsRoot,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process process = Process.Start(psi) ??
                              throw new InvalidOperationException("无法启动 DualNs2ProHost.exe。");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnDualNs2ProHostExited(process);
            dualNs2ProHostProcess = process;
            dualNs2ProHostStopFile = stopFile;
            NotifyModeStateChanged();

            _ = Task.Run(() => PumpDualNs2ProHostOutputAsync(process.StandardOutput, "[DUAL_NS2PRO_HOST]"));
            _ = Task.Run(() => PumpDualNs2ProHostOutputAsync(process.StandardError, "[DUAL_NS2PRO_HOST_ERR]"));

            string mode = synthetic ? "合成压力测试" : "中性保持";
            DualNs2ProHostStatus = "主机侧双二代 Pro2 正在启动：" + mode + "，目标 125Hz。";
            AppendLog("[DUAL_NS2PRO_HOST_START] pid=" + process.Id +
                      " mode=" + (synthetic ? "synthetic" : "neutral") +
                      " exe=" + package.DualNs2ProHostPath +
                      " stop_file=" + stopFile);
        }
        catch (Exception ex)
        {
            DualNs2ProHostStatus = "主机侧双二代 Pro2 启动失败：" + FirstLine(ex.Message);
            AppendLog("ERROR dual ns2pro host start: " + ex);
        }
        finally
        {
            Busy = false;
            NotifyModeStateChanged();
        }

        return Task.CompletedTask;
    }

    private async Task StopDualNs2ProHostAsync()
    {
        Process? process = dualNs2ProHostProcess;
        if (process == null || process.HasExited)
        {
            DualNs2ProHostStatus = "主机侧双二代 Pro2 没有运行。";
            dualNs2ProHostProcess = null;
            dualNs2ProHostStopFile = null;
            NotifyModeStateChanged();
            return;
        }

        try
        {
            string stopFile = dualNs2ProHostStopFile ??
                              Path.Combine(DualNs2ProHostLogRootDirectory, "dual_ns2pro_stop_manual.signal");
            Directory.CreateDirectory(Path.GetDirectoryName(stopFile)!);
            File.WriteAllText(stopFile, "stop " + DateTime.Now.ToString("O"), Encoding.UTF8);
            DualNs2ProHostStatus = "正在停止主机侧双二代 Pro2，并等待 helper 清理两个 USBIP 设备。";
            AppendLog("[DUAL_NS2PRO_HOST_STOP] stop_file=" + stopFile);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                AppendLog("[DUAL_NS2PRO_HOST_STOP] graceful stop timed out; killing process tree.");
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            ClearDualNs2ProHostProcess(process, "主机侧双二代 Pro2 已停止，两个 057E:2069 设备已请求清理。");
        }
        catch (Exception ex)
        {
            DualNs2ProHostStatus = "停止主机侧双二代 Pro2 失败：" + FirstLine(ex.Message);
            AppendLog("ERROR dual ns2pro host stop: " + ex);
        }
        finally
        {
            NotifyModeStateChanged();
        }
    }

    private async Task PumpDualNs2ProHostOutputAsync(StreamReader reader, string prefix)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                AppendLog(prefix + " " + line);
                if (line.Contains("[READY]", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("VID_057E PID_2069", StringComparison.OrdinalIgnoreCase))
                {
                    SetDualNs2ProHostStatusFromAnyThread(
                        "主机侧双二代 Pro2 已立起：A/B 两个独立 057E:2069，Steam 应按两只 Switch 2 Pro 管理。");
                }
                else if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
                {
                    SetDualNs2ProHostStatusFromAnyThread("主机侧双二代 Pro2 报错：" + FirstLine(line));
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            AppendLog(prefix + " reader failed: " + ex.Message);
        }
    }

    private void OnDualNs2ProHostExited(Process process)
    {
        try
        {
            owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(dualNs2ProHostProcess, process))
                {
                    return;
                }

                string status = process.ExitCode == 0
                    ? "主机侧双二代 Pro2 已退出。"
                    : "主机侧双二代 Pro2 异常退出，exit=" + process.ExitCode + "。请看 [DUAL_NS2PRO_HOST] 日志。";
                ClearDualNs2ProHostProcess(process, status);
            }));
        }
        catch
        {
        }
    }

    private void ClearDualNs2ProHostProcess(Process process, string status)
    {
        if (!ReferenceEquals(dualNs2ProHostProcess, process))
        {
            return;
        }

        dualNs2ProHostProcess = null;
        TryDeleteFile(dualNs2ProHostStopFile);
        dualNs2ProHostStopFile = null;
        DualNs2ProHostStatus = status;
        NotifyModeStateChanged();
        try
        {
            process.Dispose();
        }
        catch
        {
        }
    }

    private void SetDualNs2ProHostStatusFromAnyThread(string status)
    {
        if (owner.Dispatcher.CheckAccess())
        {
            DualNs2ProHostStatus = status;
            return;
        }

        try
        {
            owner.Dispatcher.BeginInvoke(new Action(() => DualNs2ProHostStatus = status));
        }
        catch
        {
        }
    }

    private void StopDualNs2ProHostOnShutdown()
    {
        Process? process = dualNs2ProHostProcess;
        if (process == null || process.HasExited)
        {
            return;
        }

        try
        {
            string stopFile = dualNs2ProHostStopFile ??
                              Path.Combine(DualNs2ProHostLogRootDirectory, "dual_ns2pro_stop_shutdown.signal");
            Directory.CreateDirectory(Path.GetDirectoryName(stopFile)!);
            File.WriteAllText(stopFile, "shutdown " + DateTime.Now.ToString("O"), Encoding.UTF8);
            if (!process.WaitForExit(2500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
        }
        catch
        {
        }
    }

    private async Task<string> RunProcessAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动进程：" + fileName);
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = stdout + stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(output);
        return output;
    }

    private void StartShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("ERROR 打开 " + target + ": " + ex.Message);
        }
    }

    private static string LogRootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "logs");

    private static string DualNs2ProHostLogRootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "dual_ns2pro_logs");

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void BeginDiagnosticCapture()
    {
        firmwareCriticalLinesSeen.Clear();
        lock (logSync)
        {
            diagnosticWriter?.Dispose();
            Directory.CreateDirectory(LogRootDirectory);
            diagnosticLogPath = Path.Combine(
                LogRootDirectory,
                "xin_heliansheng_v5.9.18_switch1_connect_rumble_r4_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            diagnosticWriter = new StreamWriter(
                diagnosticLogPath,
                append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true
            };
        }
    }

    private void AppendDiagnosticHeader(int seconds)
    {
        FirmwarePackage package = EmbeddedAssets.EnsurePackage();
        FirmwareProfile profile = package.GetProfile(desiredMode == OutputModeId.DualSenseLike
            ? "hid_audio_uac1_4ch_edge"
            : "hid_audio_uac1_4ch_dualsense");
        FirmwareAsset? app = profile.Assets.FirstOrDefault(asset =>
            asset.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) &&
            !asset.Path.Contains("bootloader", StringComparison.OrdinalIgnoreCase) &&
            !asset.Path.Contains("partition", StringComparison.OrdinalIgnoreCase));
        AppendLog("[DIAG_SESSION_START] app=5.9.18-switch1-connect-rumble-test-r4 duration_seconds=" + seconds +
                  " local_time=" + DateTime.Now.ToString("O") +
                  " utc_time=" + DateTime.UtcNow.ToString("O"));
        AppendLog("[DIAG_PACKAGE] package=" + package.Manifest.PackageVersion +
                  " firmware=" + package.Manifest.FirmwareVersion +
                  " profile=" + profile.Id +
                  " app_sha256=" + (app?.Sha256 ?? "unknown"));
        AppendLog("[DIAG_HOST] os=\"" + EscapeLogValue(Environment.OSVersion.VersionString) +
                  "\" clr=" + Environment.Version +
                  " machine=\"" + EscapeLogValue(Environment.MachineName) + "\"");
        AppendLog("[DIAG_SERIAL] port=" + (SelectedPort?.PortName ?? "none") +
                  " device=\"" + EscapeLogValue(SelectedPort?.Name ?? "unknown") + "\"");
    }

    private void EndDiagnosticCapture(string reason)
    {
        if (diagnosticWriter == null || string.IsNullOrWhiteSpace(diagnosticLogPath))
        {
            return;
        }

        AppendLog("[DIAG_SESSION_END] reason=" + reason + " log=" + diagnosticLogPath);
        lock (logSync)
        {
            diagnosticWriter?.Dispose();
            diagnosticWriter = null;
        }
        MonitorStatus = "诊断已结束。自动日志：" + diagnosticLogPath;
    }

    private void LogDiagnosticEvents(
        MonitorSnapshot previous,
        MonitorSnapshot current,
        MonitorDelta delta,
        int elapsed,
        ref int hidStallSamples)
    {
        string prefix = "[DIAG_EVENT] t=" + elapsed + "s ";
        if (current.UptimeMs + 1000 < previous.UptimeMs)
        {
            AppendLog(prefix + "type=board_restart previous_uptime_ms=" + previous.UptimeMs +
                      " current_uptime_ms=" + current.UptimeMs +
                      " reset_reason=" + current.ResetReason +
                      " reset_reason_name=" + current.ResetReasonName);
        }
        if (!string.Equals(previous.Ble, current.Ble, StringComparison.OrdinalIgnoreCase))
        {
            AppendLog(prefix + "type=ble_state previous=" + previous.Ble + " current=" + current.Ble);
        }
        if (delta.BleDisconnects > 0)
        {
            AppendLog(prefix + "type=ble_disconnect count=+" + delta.BleDisconnects +
                      " reason=" + current.BleDisconnectReason +
                      " age_ms=" + current.BleDisconnectAgeMs);
        }
        if (delta.BleConnectFailures > 0)
        {
            AppendLog(prefix + "type=ble_connect_failure count=+" + delta.BleConnectFailures +
                      " status=" + current.BleConnectLastStatus +
                      " start_rc=" + current.BleConnectLastRc);
        }
        if (delta.BleConnectSuccesses > 0)
        {
            AppendLog(prefix + "type=ble_connect_success count=+" + delta.BleConnectSuccesses +
                      " interval_us=" + current.BleConnIntervalUs);
        }
        if (delta.BleReconnectAttempts > 0 || delta.BleScanStarts > 0)
        {
            AppendLog(prefix + "type=ble_reconnect_progress attempts=+" + delta.BleReconnectAttempts +
                      " scans=+" + delta.BleScanStarts +
                      " reconnect_task=" + current.BleReconnectTask.ToString().ToLowerInvariant());
        }
        if (previous.InputLive != current.InputLive)
        {
            AppendLog(prefix + "type=input_live previous=" + previous.InputLive.ToString().ToLowerInvariant() +
                      " current=" + current.InputLive.ToString().ToLowerInvariant() +
                      " age_ms=" + current.InputAgeMs);
        }
        if (string.Equals(current.Ble, "connected", StringComparison.OrdinalIgnoreCase) &&
            current.InputAgeMs > 1000 &&
            previous.InputAgeMs <= 1000)
        {
            AppendLog(prefix + "type=ble_connected_but_input_stale input_age_ms=" + current.InputAgeMs +
                      " notify_age_ms=" + current.BleNotifyParsedAgeMs);
        }
        if (previous.UsbMounted != current.UsbMounted ||
            previous.UsbSuspended != current.UsbSuspended ||
            previous.UsbConfigurationReady != current.UsbConfigurationReady)
        {
            AppendLog(prefix + "type=usb_state mounted=" + current.UsbMounted.ToString().ToLowerInvariant() +
                      " suspended=" + current.UsbSuspended.ToString().ToLowerInvariant() +
                      " configuration_ready=" + current.UsbConfigurationReady.ToString().ToLowerInvariant() +
                      " mounts=" + current.UsbMountCount +
                      " umounts=" + current.UsbUmountCount +
                      " suspends=" + current.UsbSuspendCount +
                      " resumes=" + current.UsbResumeCount);
        }
        if (delta.UsbConfigurationResetCount > 0)
        {
            AppendLog(prefix + "type=usb_configuration_reset count=+" +
                      delta.UsbConfigurationResetCount +
                      " bus_resets=" + current.UsbBusResetCount +
                      " age_ms=" + current.UsbConfigurationResetAgeMs +
                      " configuration_ready=" +
                      current.UsbConfigurationReady.ToString().ToLowerInvariant());
        }
        if (delta.HidReportFailed > 0 || delta.HidReportNotReady > 0)
        {
            AppendLog(prefix + "type=hid_backpressure failed=+" + delta.HidReportFailed +
                      " submit_failed=+" + delta.HidReportSubmitFailed +
                      " xfer_failed=+" + delta.HidReportXferFailed +
                      " submit_streak=" + current.HidReportSubmitFailureStreak +
                      " submit_failure_age_ms=" + current.HidReportSubmitFailureAgeMs +
                      " not_ready=+" + delta.HidReportNotReady +
                      " report_age_ms=" + current.HidReportAgeMs);
        }
        if (delta.HidEndpointKicks > 0 || delta.UsbRecoveryCount > 0)
        {
            AppendLog(prefix + "type=hid_recovery endpoint_kicks=+" + delta.HidEndpointKicks +
                      " usb_reenumerations=+" + delta.UsbRecoveryCount +
                      " report_age_ms=" + current.HidReportAgeMs);
        }
        if (delta.AudioDropped > 0)
        {
            AppendLog(prefix + "type=audio_queue_drop dropped=+" + delta.AudioDropped +
                      " depth=" + current.AudioQueueDepth +
                      " high=" + current.AudioQueueHigh);
        }
        if (current.UsbMounted && !current.UsbSuspended && delta.HidReportCompleted == 0)
        {
            hidStallSamples++;
            if (hidStallSamples == 2)
            {
                AppendLog(prefix + "type=hid_report_stall samples=" + hidStallSamples +
                          " report_age_ms=" + current.HidReportAgeMs);
            }
        }
        else
        {
            if (hidStallSamples >= 2 && delta.HidReportCompleted > 0)
            {
                AppendLog(prefix + "type=hid_report_recovered completed=+" + delta.HidReportCompleted);
            }
            hidStallSamples = 0;
        }
        if (previous.AudioStreaming != current.AudioStreaming)
        {
            AppendLog(prefix + "type=audio_stream previous=" +
                      previous.AudioStreaming.ToString().ToLowerInvariant() +
                      " current=" + current.AudioStreaming.ToString().ToLowerInvariant() +
                      " alt=" + current.AudioAlt);
        }
        if (delta.HidOutputCount > 0)
        {
            AppendLog(prefix + "type=host_output packets=+" + delta.HidOutputCount +
                      " output_age_ms=" + current.HidOutputAgeMs);
        }
        if (delta.HidRumbleActiveUpdates > 0 || delta.HidRumbleBleWrites > 0)
        {
            AppendLog(prefix + "type=valid_hid_rumble updates=+" + delta.HidRumbleActiveUpdates +
                      " ble_writes=+" + delta.HidRumbleBleWrites +
                      " motors=" + current.HidRumbleLeft + "/" + current.HidRumbleRight);
        }
        if (delta.HidRumbleIgnoredNonzero > 0)
        {
            AppendLog(prefix + "type=invalid_hid_rumble_bytes ignored=+" + delta.HidRumbleIgnoredNonzero +
                      " flags=" + current.HidRumbleValid0 + "/" + current.HidRumbleValid1 + "/" + current.HidRumbleValid2 +
                      " motors=" + current.HidRumbleLeft + "/" + current.HidRumbleRight +
                      " preview=" + current.HidRumblePreview);
        }
    }

    private static string EscapeLogValue(string text)
    {
        return (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private void ClearLog()
    {
        lock (logSync)
        {
            log.Clear();
        }
        logUiDirty = false;
        OnPropertyChanged(nameof(LogText));
    }

    private void SaveLog()
    {
        var dialog = new SaveFileDialog
        {
            FileName = "v5_9_1_diagnostic_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log",
            Filter = "Log file|*.log|Text file|*.txt"
        };
        if (dialog.ShowDialog(owner) == true)
        {
            string snapshot;
            lock (logSync)
            {
                snapshot = log.ToString();
            }
            File.WriteAllText(dialog.FileName, snapshot, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            AppendLog("日志已保存：" + dialog.FileName);
        }
    }

    private void OpenLogFolder()
    {
        Directory.CreateDirectory(LogRootDirectory);
        StartShell(LogRootDirectory);
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (logSync)
        {
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string stamped = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] ";
                string uiLine = line.Length <= UiLogMaxLineChars
                    ? line
                    : line[..UiLogMaxLineChars] + "... [ui truncated]";
                log.AppendLine(stamped + uiLine);
                diagnosticWriter?.WriteLine(stamped + line);
            }
            if (log.Length > UiLogTrimThreshold)
            {
                int remove = log.Length - UiLogRetainedChars;
                log.Remove(0, remove);
                log.Insert(0, "[UI LOG TRIMMED; complete diagnostic remains in the auto-saved file]\r\n");
            }
            logUiDirty = true;
        }
    }

    private void FlushLogTextToUi()
    {
        if (!logUiDirty)
        {
            return;
        }

        logUiDirty = false;
        OnPropertyChanged(nameof(LogText));
    }

    public void Shutdown()
    {
        if (shutdownStarted)
        {
            return;
        }

        shutdownStarted = true;
        gameMonitorCts?.Cancel();
        StopDualNs2ProHostOnShutdown();
        EndDiagnosticCapture("application_closed");
        stateTimer.Stop();
        logUiTimer.Stop();
        FlushLogTextToUi();
        SerialCommandClient.Shutdown();
    }

    private static string FirstLine(string text)
    {
        return text.Replace("\r", "").Split('\n')[0];
    }

    private static string OneLine(string text)
    {
        return Regex.Replace(text.Replace("\r", " ").Replace("\n", " "), "\\s+", " ").Trim();
    }

    private static string ExtractLastJsonLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no response)";
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            int jsonStart = line.IndexOf("{\"ok\":", StringComparison.Ordinal);
            if (jsonStart >= 0)
            {
                return line.Substring(jsonStart);
            }
        }
        string compact = OneLine(text);
        return compact.Length <= 512 ? compact : compact.Substring(compact.Length - 512);
    }

    private void LogCriticalFirmwareLines(string text, string phase)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string[] markers =
        {
            "guru meditation", "panic", "watchdog", "brownout",
            "rst:", "abort()", "backtrace:", "assert failed",
            "rebooting", "stack overflow"
        };
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 ||
                line.Contains("{\"ok\":", StringComparison.Ordinal))
            {
                continue;
            }
            bool critical = false;
            foreach (string marker in markers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    critical = true;
                    break;
                }
            }
            if (!critical || !firmwareCriticalLinesSeen.Add(line))
            {
                continue;
            }
            string compact = line.Length <= 768 ? line : line[..768];
            AppendLog("[DIAG_FIRMWARE_CRITICAL] phase=" + phase + " " + compact);
        }
    }

    private async Task StartFreshPairingAsync(bool replacing)
    {
        try
        {
            EnsureNoExternalViiperBleOwner(replacing ? "更换 Pro2 手柄" : "首次连接 Pro2");
        }
        catch (Exception ex)
        {
            OverallStatus = "BLE 被占用";
            BleStatus = ex.Message;
            NextAction = "请从系统托盘彻底退出 V6 VIIPER 版及独立 viiper.exe，再让目标 Pro2 进入配对状态后重试。";
            MessageBox.Show(owner, ex.Message, "Pro2 BLE 链路冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!replacing && !string.IsNullOrWhiteSpace(bleSavedTarget))
        {
            MessageBoxResult answer = MessageBox.Show(
                owner,
                "控制板已经保存了手柄 " + bleSavedTarget + "。\n\n继续“首次连接”会清除这个地址并重新寻找手柄。普通断联请改用“重连已配对”。",
                "重新执行首次连接",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }
        }

        if (replacing)
        {
            MessageBoxResult answer = MessageBox.Show(
                owner,
                "更换手柄会清除控制板内保存的旧 Pro2 地址。\n\n请先关闭旧手柄，让新手柄保持唤醒并进入可连接状态，然后继续。",
                "更换 Pro2 手柄",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }
        }

        try
        {
            Busy = true;
            OverallStatus = "连接中";
            BleStatus = replacing
                ? "正在清除旧手柄并寻找新的 Pro2..."
                : "正在建立首次 Pro2 连接...";
            NextAction = "请只开启要连接的 Pro2，并避免它同时连接电脑、手机或其他主机。";
            SelectedBleDevice = null;
            BleTarget = "";

            await SendSerialCoreAsync("ble auto off", 4);
            await SendSerialCoreAsync("ble forget", 5);
            SetBleSavedTarget("");
            await WaitForBleTransportToSettleAsync(5);
            await SendSerialCoreAsync("ble auto on", 4);
            string ready = await WaitForBleInputReadyAsync(35);

            BleStatus = SummarizeBle(ready, replacing ? "replace" : "first-pair");
            OverallStatus = "就绪";
            NextAction = replacing
                ? "新手柄已经保存。以后断联或休眠后会优先自动重连这只手柄。"
                : "首次连接完成。控制板已保存手柄地址，后续可自动重连。";
        }
        catch (Exception ex)
        {
            OverallStatus = IsBoardUnavailableException(ex) ? "离线" : "错误";
            BleStatus = (replacing ? "更换手柄" : "首次连接") + "尚未完成：" + FirstLine(ex.Message);
            NextAction = "保持目标手柄唤醒，打开“高级连接工具”查看扫描结果；如果附近有多只手柄，请选择正确目标后手动连接。";
            try
            {
                string list = await SendSerialCoreAsync("ble list", 3, logOutput: false);
                ApplyBleScanResults(list);
                BleCandidates = SummarizeBleList(list);
            }
            catch (Exception listError)
            {
                AppendLog("WARN ble pairing fallback list: " + listError.Message);
            }
            AppendLog("ERROR ble fresh pairing: " + ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private static int ParseIntOrDefault(string text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, out int value))
        {
            value = fallback;
        }
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string[] FindExternalViiperProcesses()
    {
        var matches = new List<string>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                string name = process.ProcessName ?? "";
                if (name.Contains("viiper", StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(name + "(PID " + process.Id + ")");
                }
            }
            catch
            {
                // Processes may exit while the snapshot is being inspected.
            }
            finally
            {
                process.Dispose();
            }
        }
        return matches.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray();
    }

    private void EnsureNoExternalViiperBleOwner(string operation)
    {
        string[] conflicts = FindExternalViiperProcesses();
        if (conflicts.Length == 0)
        {
            lastExternalViiperConflict = "";
            return;
        }

        string signature = string.Join(", ", conflicts);
        if (!string.Equals(lastExternalViiperConflict, signature, StringComparison.Ordinal))
        {
            lastExternalViiperConflict = signature;
            AppendLog("[BLE_OWNER_CONFLICT] operation=" + operation + " processes=" + signature);
        }
        throw new InvalidOperationException(
            "检测到 VIIPER 版仍在运行：" + signature + "。它可能已经占用真实 Pro2 的 Windows BLE，ESP 无法同时连接。请先从系统托盘彻底退出该程序及 viiper.exe。");
    }

    private static bool HasReadyBleInput(string statusText)
    {
        return BleInputStatusParser.Parse(statusText).Ready;
    }

    private async Task WaitForBleTransportToSettleAsync(int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            string status = await SendSerialCoreAsync("status lite", 2, logOutput: false);
            string state = ReadJsonString(status, "ble");
            if (!string.Equals(state, "stopping", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await Task.Delay(150);
        }
        throw new InvalidOperationException(
            "BLE GAP 停止过程超时；固件没有收到 DISC_COMPLETE，已停止继续重连以避免破坏 NimBLE 状态。");
    }

    private async Task<string> WaitForBleInputReadyAsync(int timeoutSeconds, CancellationToken token = default)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, timeoutSeconds));
        DateTime nextReconnectAt = DateTime.MinValue;
        string lastStatus = "";
        BleInputStatus lastInput = BleInputStatusParser.Parse("");
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            lastStatus = await SendSerialCoreAsync("status lite", 2, logOutput: false);
            if (!string.IsNullOrWhiteSpace(lastStatus))
            {
                BleStatus = SummarizeBle(lastStatus, "wait");
                lastInput = BleInputStatusParser.Parse(lastStatus);
                if (lastInput.Ready)
                {
                    return lastStatus;
                }
            }

            bool firmwareAutoReconnect = string.Equals(
                ReadJsonString(lastStatus, "ble_auto"),
                "on",
                StringComparison.OrdinalIgnoreCase);
            if (DateTime.UtcNow >= nextReconnectAt &&
                !firmwareAutoReconnect &&
                !lastInput.Connected &&
                !string.Equals(lastInput.TransportState, "connecting", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(lastInput.TransportState, "scanning", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(lastInput.TransportState, "stopping", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("[BLE_WAIT] 已请求重连，正在等待 Pro2 实时输入。");
                await SendSerialCoreAsync("ble reconnect", 6);
                nextReconnectAt = DateTime.UtcNow.AddSeconds(4);
            }

            await Task.Delay(1000, token);
        }

        if (lastInput.Connected && lastInput.HasMetrics)
        {
            throw new InvalidOperationException(
                "Pro2 BLE 已连接，但没有收到新鲜输入通知。状态协议=" + lastInput.Schema +
                "，updates=" + lastInput.Updates +
                "，age_ms=" + lastInput.AgeMs +
                "。请保持手柄唤醒后使用“重连已配对”。");
        }

        throw new InvalidOperationException(
            "Pro2 BLE 尚未完成连接。请确认手柄只连接到桥接板，并保持手柄唤醒。");
    }

    private string SummarizeBle(string output, string source)
    {
        if (string.IsNullOrWhiteSpace(output)) return "没有收到 BLE 状态回包。";
        string ble = ReadJsonString(output, "ble");
        string auto = ReadJsonString(output, "ble_auto");
        string target = ReadJsonString(output, "ble_target");
        if (Regex.IsMatch(output, "\"ble_target\"\\s*:", RegexOptions.IgnoreCase))
        {
            SetBleSavedTarget(target);
        }
        BleInputStatus input = BleInputStatusParser.Parse(output);
        long scanSeen = ReadJsonCounter(output, "scan_seen");

        if (!string.IsNullOrWhiteSpace(ble))
        {
            string rateText = input.RateMilliHz > 0
                ? (input.RateMilliHz / 1000.0).ToString("0.0") + " Hz"
                : "0 Hz";
            string ageText = input.AgeMs >= 0 ? input.AgeMs + " ms" : "无数据";
            string targetText = string.IsNullOrWhiteSpace(target) ? "无" : target;
            string autoText = string.IsNullOrWhiteSpace(auto) ? "?" : auto;
            string schemaText = input.HasMetrics ? input.Schema : "legacy";
            return $"BLE={ble}, 自动重连={autoText}, 目标={targetText}, 输入协议={schemaText}, 输入计数={Math.Max(0, input.Updates)}, 输入时延={ageText}, 输入频率={rateText}, 来源={source}";
        }

        if (scanSeen >= 0)
        {
            return $"已读取 BLE 列表，scan_seen={scanSeen}。";
        }

        return "BLE 状态已更新。";
    }

    private static string SummarizeBleList(string output)
    {
        MatchCollection matches = Regex.Matches(
            output,
            "\\{\"index\":(\\d+),\"target\":\"([^\"]+)\",\"addr\":\"([^\"]+)\",\"name\":\"([^\"]*)\",\"rssi\":(-?\\d+),\"candidate\":(true|false)\\}",
            RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            long scanSeen = ReadJsonCounter(output, "scan_seen");
            return scanSeen > 0
                ? $"本次扫描了 {scanSeen} 个设备，但没有发现候选项。请保持 Pro2 唤醒后再试一次。"
                : "暂时没有找到 BLE 候选设备。请点击“扫描”，并保持 Pro2 处于唤醒/配对状态。";
        }

        var builder = new StringBuilder();
        foreach (Match match in matches)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append('#').Append(match.Groups[1].Value)
                .Append("  ").Append(match.Groups[3].Value);
            string name = match.Groups[4].Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                builder.Append("  ").Append(name);
            }
            builder.Append("  RSSI=").Append(match.Groups[5].Value);
            builder.Append("  候选=").Append(match.Groups[6].Value);
        }
        return builder.ToString();
    }

    private sealed class MonitorSnapshot
    {
        public long AudioSubmitted { get; init; }
        public long AudioDropped { get; init; }
        public long AudioQueueDepth { get; init; }
        public long AudioQueueHigh { get; init; }
        public long AudioPackets { get; init; }
        public long AudioActive { get; init; }
        public long HdCandidates { get; init; }
        public long Raw02Live { get; init; }
        public long BleWrites { get; init; }
        public long BleErrors { get; init; }
        public long DroppedRate { get; init; }
        public long DroppedSilence { get; init; }
        public long DroppedPcm { get; init; }
        public long InputUpdates { get; init; }
        public long InputAgeMs { get; init; }
        public long InputRateMilliHz { get; init; }
        public long InputLx { get; init; }
        public long InputLy { get; init; }
        public long InputRx { get; init; }
        public long InputRy { get; init; }
        public bool InputLive { get; init; }
        public string Ble { get; init; } = "";
        public long BleConnIntervalUs { get; init; }
        public long BleFastDropCount { get; init; }
        public long BleFastBlockMs { get; init; }
        public bool BleReconnectTask { get; init; }
        public long BleScanStarts { get; init; }
        public long BleReconnectAttempts { get; init; }
        public long BleConnectSuccesses { get; init; }
        public long BleConnectFailures { get; init; }
        public long BleConnectLastRc { get; init; }
        public long BleConnectLastStatus { get; init; }
        public long BleDisconnects { get; init; }
        public long BleDisconnectReason { get; init; }
        public long BleDisconnectAgeMs { get; init; }
        public long BleNotifyRx { get; init; }
        public long BleNotifyParsed { get; init; }
        public long BleNotifyAgeMs { get; init; }
        public long BleNotifyParsedAgeMs { get; init; }
        public long BleNotifyActualMilliHz { get; init; }
        public long BleNotifyLastGapUs { get; init; }
        public long BleNotifyMaxGapUs { get; init; }
        public long BleNotifyMinGapUs { get; init; }
        public long BleNotifyShortGapCount { get; init; }
        public long BleNotifySubIntervalCount { get; init; }
        public long BleNotifyGapLt3Ms { get; init; }
        public long BleNotifyGap3To6P5Ms { get; init; }
        public long BleNotifyGap6P5To10Ms { get; init; }
        public long BleNotifyGapGe10Ms { get; init; }
        public long BleNotifySubRawUnique { get; init; }
        public long BleNotifySubControlUnique { get; init; }
        public long BleNotifySubMotionUnique { get; init; }
        public long BleNotifyRawUnique { get; init; }
        public long BleNotifyRawRepeat { get; init; }
        public long BleNotifyRawRepeatStreak { get; init; }
        public long BleNotifyRawRepeatStreakMax { get; init; }
        public long BleNotifyRawUniqueMilliHz { get; init; }
        public long BleNotifyRawUniqueGapUs { get; init; }
        public long BleNotifyRawUniqueMaxGapUs { get; init; }
        public long BleNotifyParsedActualMilliHz { get; init; }
        public long BleNotifyParsedLastGapUs { get; init; }
        public long BleNotifyParsedMaxGapUs { get; init; }
        public long BleNotifyControlUnique { get; init; }
        public long BleNotifyControlRepeat { get; init; }
        public long BleNotifyControlUniqueMilliHz { get; init; }
        public long BleNotifyControlUniqueGapUs { get; init; }
        public long BleNotifyControlUniqueMaxGapUs { get; init; }
        public long BleNotifyMotionUnique { get; init; }
        public long BleNotifyMotionRepeat { get; init; }
        public long BleNotifyMotionUniqueMilliHz { get; init; }
        public long BleNotifyMotionUniqueGapUs { get; init; }
        public long BleNotifyMotionUniqueMaxGapUs { get; init; }
        public bool BleReadPollActive { get; init; }
        public long BleReadPollRateHz { get; init; }
        public long BleReadPollStartRc { get; init; }
        public long BleReadPollStatus { get; init; }
        public long BleReadPollStarts { get; init; }
        public long BleReadPollStartFails { get; init; }
        public long BleReadPollResponses { get; init; }
        public long BleReadPollErrors { get; init; }
        public long BleReadPollActualMilliHz { get; init; }
        public long BleReadPollLastGapUs { get; init; }
        public long BleReadPollMaxGapUs { get; init; }
        public long BleReadPollRawUnique { get; init; }
        public long BleReadPollRawRepeat { get; init; }
        public long BleReadPollControlUnique { get; init; }
        public long BleReadPollControlRepeat { get; init; }
        public long BleReadPollControlUniqueMilliHz { get; init; }
        public long BleReadPollControlUniqueGapUs { get; init; }
        public long BleReadPollControlUniqueMaxGapUs { get; init; }
        public long BleReadPollMotionUnique { get; init; }
        public long BleReadPollMotionRepeat { get; init; }
        public long BleReadPollMotionUniqueMilliHz { get; init; }
        public bool UsbMounted { get; init; }
        public bool UsbSuspended { get; init; }
        public bool UsbConfigurationReady { get; init; }
        public long UsbMountCount { get; init; }
        public long UsbUmountCount { get; init; }
        public long UsbBusResetCount { get; init; }
        public long UsbConfigurationResetCount { get; init; }
        public long UsbConfigurationResetAgeMs { get; init; }
        public long UsbSuspendCount { get; init; }
        public long UsbResumeCount { get; init; }
        public long HidReportSent { get; init; }
        public long HidReportCompleted { get; init; }
        public long HidReportFailed { get; init; }
        public long HidReportSubmitFailed { get; init; }
        public long HidReportXferFailed { get; init; }
        public long HidReportSubmitFailureStreak { get; init; }
        public long HidReportSubmitFailureAgeMs { get; init; }
        public long HidReportNotReady { get; init; }
        public long HidEndpointKicks { get; init; }
        public long UsbRecoveryCount { get; init; }
        public long HidReportLastGapUs { get; init; }
        public long HidReportMaxGapUs { get; init; }
        public long HidReportAgeMs { get; init; }
        public long HidOutputCount { get; init; }
        public long HidOutputAgeMs { get; init; }
        public long HidRumbleUpdates { get; init; }
        public long HidRumbleActiveUpdates { get; init; }
        public long HidRumbleIgnoredNonzero { get; init; }
        public long HidRumbleBleWrites { get; init; }
        public long HidRumbleBleErrors { get; init; }
        public bool HidRumbleEnabled { get; init; }
        public bool HidRumbleActive { get; init; }
        public long HidRumbleValid0 { get; init; }
        public long HidRumbleValid1 { get; init; }
        public long HidRumbleValid2 { get; init; }
        public long HidRumbleRight { get; init; }
        public long HidRumbleLeft { get; init; }
        public string HidRumblePreview { get; init; } = "";
        public long UptimeMs { get; init; }
        public long ResetReason { get; init; }
        public string ResetReasonName { get; init; } = "";
        public string Version { get; init; } = "";
        public string Haptic { get; init; } = "";
        public string Source { get; init; } = "";
        public bool AudioStreaming { get; init; }
        public long AudioAlt { get; init; }
        public string AudioParser { get; init; } = "";
        public string AudioPair { get; init; } = "";
        public string HdCandidate { get; init; } = "";
        public string LastMode { get; init; } = "";
        public string Left { get; init; } = "";
        public string Right { get; init; } = "";
        public string Error { get; init; } = "";

        public static MonitorSnapshot FromStatus(string status)
        {
            return new MonitorSnapshot
            {
                AudioSubmitted = Counter(status, "audio_submitted"),
                AudioDropped = Counter(status, "audio_dropped"),
                AudioQueueDepth = Counter(status, "audio_queue_depth"),
                AudioQueueHigh = Counter(status, "audio_queue_high"),
                AudioPackets = Counter(status, "audio_packets"),
                AudioActive = Counter(status, "audio_active"),
                HdCandidates = Counter(status, "raw02_hd_candidate_packets"),
                Raw02Live = Counter(status, "raw02_live_packets"),
                BleWrites = Counter(status, "raw02_ble_writes"),
                BleErrors = Counter(status, "raw02_ble_errors"),
                DroppedRate = Counter(status, "raw02_dropped_rate"),
                DroppedSilence = Counter(status, "raw02_dropped_silence"),
                DroppedPcm = Counter(status, "raw02_dropped_pcm"),
                InputUpdates = Counter(status, "input_updates"),
                InputAgeMs = ReadJsonCounter(status, "input_age_ms"),
                InputRateMilliHz = Counter(status, "input_rate_millihz"),
                InputLx = Counter(status, "input_lx"),
                InputLy = Counter(status, "input_ly"),
                InputRx = Counter(status, "input_rx"),
                InputRy = Counter(status, "input_ry"),
                InputLive = Bool(status, "input_live"),
                Ble = ReadJsonString(status, "ble"),
                BleConnIntervalUs = Counter(status, "ble_conn_interval_us"),
                BleFastDropCount = Counter(status, "ble_fast_drop_count"),
                BleFastBlockMs = Counter(status, "ble_fast_block_ms"),
                BleReconnectTask = Bool(status, "ble_reconnect_task"),
                BleScanStarts = Counter(status, "ble_scan_starts"),
                BleReconnectAttempts = Counter(status, "ble_reconnect_attempts"),
                BleConnectSuccesses = Counter(status, "ble_connect_successes"),
                BleConnectFailures = Counter(status, "ble_connect_failures"),
                BleConnectLastRc = ReadJsonCounter(status, "ble_connect_last_rc"),
                BleConnectLastStatus = ReadJsonCounter(status, "ble_connect_last_status"),
                BleDisconnects = Counter(status, "ble_disconnects"),
                BleDisconnectReason = ReadJsonCounter(status, "ble_disconnect_reason"),
                BleDisconnectAgeMs = ReadJsonCounter(status, "ble_disconnect_age_ms"),
                BleNotifyRx = Counter(status, "ble_notify_rx"),
                BleNotifyParsed = Counter(status, "ble_notify_parsed"),
                BleNotifyAgeMs = ReadJsonCounter(status, "ble_notify_age_ms"),
                BleNotifyParsedAgeMs = ReadJsonCounter(status, "ble_notify_parsed_age_ms"),
                BleNotifyActualMilliHz = Counter(status, "ble_notify_actual_mhz"),
                BleNotifyLastGapUs = Counter(status, "ble_notify_last_gap_us"),
                BleNotifyMaxGapUs = Counter(status, "ble_notify_max_gap_us"),
                BleNotifyMinGapUs = Counter(status, "ble_notify_min_gap_us"),
                BleNotifyShortGapCount = Counter(status, "ble_notify_short_gap_count"),
                BleNotifySubIntervalCount = Counter(status, "ble_notify_sub_interval_count"),
                BleNotifyGapLt3Ms = Counter(status, "ble_notify_gap_lt3ms"),
                BleNotifyGap3To6P5Ms = Counter(status, "ble_notify_gap_3_6p5ms"),
                BleNotifyGap6P5To10Ms = Counter(status, "ble_notify_gap_6p5_10ms"),
                BleNotifyGapGe10Ms = Counter(status, "ble_notify_gap_ge10ms"),
                BleNotifySubRawUnique = Counter(status, "ble_notify_sub_raw_unique"),
                BleNotifySubControlUnique = Counter(status, "ble_notify_sub_control_unique"),
                BleNotifySubMotionUnique = Counter(status, "ble_notify_sub_motion_unique"),
                BleNotifyRawUnique = Counter(status, "ble_notify_raw_unique"),
                BleNotifyRawRepeat = Counter(status, "ble_notify_raw_repeat"),
                BleNotifyRawRepeatStreak = Counter(status, "ble_notify_raw_repeat_streak"),
                BleNotifyRawRepeatStreakMax = Counter(status, "ble_notify_raw_repeat_streak_max"),
                BleNotifyRawUniqueMilliHz = Counter(status, "ble_notify_raw_unique_mhz"),
                BleNotifyRawUniqueGapUs = Counter(status, "ble_notify_raw_unique_gap_us"),
                BleNotifyRawUniqueMaxGapUs = Counter(status, "ble_notify_raw_unique_max_gap_us"),
                BleNotifyParsedActualMilliHz = Counter(status, "ble_notify_parsed_actual_mhz"),
                BleNotifyParsedLastGapUs = Counter(status, "ble_notify_parsed_last_gap_us"),
                BleNotifyParsedMaxGapUs = Counter(status, "ble_notify_parsed_max_gap_us"),
                BleNotifyControlUnique = Counter(status, "ble_notify_control_unique"),
                BleNotifyControlRepeat = Counter(status, "ble_notify_control_repeat"),
                BleNotifyControlUniqueMilliHz = Counter(status, "ble_notify_control_unique_mhz"),
                BleNotifyControlUniqueGapUs = Counter(status, "ble_notify_control_unique_gap_us"),
                BleNotifyControlUniqueMaxGapUs = Counter(status, "ble_notify_control_unique_max_gap_us"),
                BleNotifyMotionUnique = Counter(status, "ble_notify_motion_unique"),
                BleNotifyMotionRepeat = Counter(status, "ble_notify_motion_repeat"),
                BleNotifyMotionUniqueMilliHz = Counter(status, "ble_notify_motion_unique_mhz"),
                BleNotifyMotionUniqueGapUs = Counter(status, "ble_notify_motion_unique_gap_us"),
                BleNotifyMotionUniqueMaxGapUs = Counter(status, "ble_notify_motion_unique_max_gap_us"),
                BleReadPollActive = Bool(status, "ble_read_poll_active"),
                BleReadPollRateHz = Counter(status, "ble_read_poll_rate_hz"),
                BleReadPollStartRc = ReadJsonCounter(status, "ble_read_poll_start_rc"),
                BleReadPollStatus = ReadJsonCounter(status, "ble_read_poll_status"),
                BleReadPollStarts = Counter(status, "ble_read_poll_starts"),
                BleReadPollStartFails = Counter(status, "ble_read_poll_start_fails"),
                BleReadPollResponses = Counter(status, "ble_read_poll_rsp"),
                BleReadPollErrors = Counter(status, "ble_read_poll_errors"),
                BleReadPollActualMilliHz = Counter(status, "ble_read_poll_actual_mhz"),
                BleReadPollLastGapUs = Counter(status, "ble_read_poll_gap_us"),
                BleReadPollMaxGapUs = Counter(status, "ble_read_poll_max_gap_us"),
                BleReadPollRawUnique = Counter(status, "ble_read_poll_raw_unique"),
                BleReadPollRawRepeat = Counter(status, "ble_read_poll_raw_repeat"),
                BleReadPollControlUnique = Counter(status, "ble_read_poll_control_unique"),
                BleReadPollControlRepeat = Counter(status, "ble_read_poll_control_repeat"),
                BleReadPollControlUniqueMilliHz = Counter(status, "ble_read_poll_control_unique_mhz"),
                BleReadPollControlUniqueGapUs = Counter(status, "ble_read_poll_control_unique_gap_us"),
                BleReadPollControlUniqueMaxGapUs = Counter(status, "ble_read_poll_control_unique_max_gap_us"),
                BleReadPollMotionUnique = Counter(status, "ble_read_poll_motion_unique"),
                BleReadPollMotionRepeat = Counter(status, "ble_read_poll_motion_repeat"),
                BleReadPollMotionUniqueMilliHz = Counter(status, "ble_read_poll_motion_unique_mhz"),
                UsbMounted = Bool(status, "usb_mounted"),
                UsbSuspended = Bool(status, "usb_suspended"),
                UsbConfigurationReady = Bool(status, "usb_configuration_ready"),
                UsbMountCount = Counter(status, "usb_mount_count"),
                UsbUmountCount = Counter(status, "usb_umount_count"),
                UsbBusResetCount = Counter(status, "usb_bus_reset_count"),
                UsbConfigurationResetCount = Counter(status, "usb_configuration_reset_count"),
                UsbConfigurationResetAgeMs = ReadJsonCounter(status, "usb_configuration_reset_age_ms"),
                UsbSuspendCount = Counter(status, "usb_suspend_count"),
                UsbResumeCount = Counter(status, "usb_resume_count"),
                HidReportSent = Counter(status, "hid_report_sent"),
                HidReportCompleted = Counter(status, "hid_report_completed"),
                HidReportFailed = Counter(status, "hid_report_failed"),
                HidReportSubmitFailed = Counter(status, "hid_report_submit_failed"),
                HidReportXferFailed = Counter(status, "hid_report_xfer_failed"),
                HidReportSubmitFailureStreak = Counter(status, "hid_report_submit_failure_streak"),
                HidReportSubmitFailureAgeMs = ReadJsonCounter(status, "hid_report_submit_failure_age_ms"),
                HidReportNotReady = Counter(status, "hid_report_not_ready"),
                HidEndpointKicks = Counter(status, "hid_endpoint_kicks"),
                UsbRecoveryCount = Counter(status, "usb_recovery_count"),
                HidReportLastGapUs = Counter(status, "hid_report_last_gap_us"),
                HidReportMaxGapUs = Counter(status, "hid_report_max_gap_us"),
                HidReportAgeMs = ReadJsonCounter(status, "hid_report_age_ms"),
                HidOutputCount = Counter(status, "hid_output_count"),
                HidOutputAgeMs = ReadJsonCounter(status, "hid_output_age_ms"),
                HidRumbleUpdates = Counter(status, "hid_rumble_updates"),
                HidRumbleActiveUpdates = Counter(status, "hid_rumble_active_updates"),
                HidRumbleIgnoredNonzero = Counter(status, "hid_rumble_ignored_nonzero"),
                HidRumbleBleWrites = Counter(status, "hid_rumble_ble_writes"),
                HidRumbleBleErrors = Counter(status, "hid_rumble_ble_errors"),
                HidRumbleEnabled = Bool(status, "hid_rumble_enabled"),
                HidRumbleActive = Bool(status, "hid_rumble_active"),
                HidRumbleValid0 = Counter(status, "hid_rumble_valid0"),
                HidRumbleValid1 = Counter(status, "hid_rumble_valid1"),
                HidRumbleValid2 = Counter(status, "hid_rumble_valid2"),
                HidRumbleRight = Counter(status, "hid_rumble_right"),
                HidRumbleLeft = Counter(status, "hid_rumble_left"),
                HidRumblePreview = ReadJsonString(status, "hid_rumble_preview"),
                UptimeMs = Counter(status, "uptime_ms"),
                ResetReason = ReadJsonCounter(status, "reset_reason"),
                ResetReasonName = ReadJsonString(status, "reset_reason_name"),
                Version = ReadJsonString(status, "version"),
                Haptic = ReadJsonString(status, "haptic"),
                Source = ReadJsonString(status, "haptic_source"),
                AudioStreaming = string.Equals(ReadJsonBoolString(status, "audio_streaming"), "true", StringComparison.OrdinalIgnoreCase),
                AudioAlt = Counter(status, "audio_alt"),
                AudioParser = ReadJsonString(status, "audio_parser"),
                AudioPair = ReadJsonString(status, "audio_pair"),
                HdCandidate = ReadJsonBoolString(status, "hd_candidate"),
                LastMode = ReadJsonString(status, "raw02_last_mode"),
                Left = ReadJsonString(status, "raw02_left"),
                Right = ReadJsonString(status, "raw02_right"),
                Error = ReadJsonString(status, "raw02_error")
            };
        }

        public MonitorDelta DeltaFrom(MonitorSnapshot previous)
        {
            return new MonitorDelta
            {
                AudioSubmitted = Math.Max(0, AudioSubmitted - previous.AudioSubmitted),
                AudioDropped = Math.Max(0, AudioDropped - previous.AudioDropped),
                AudioPackets = Math.Max(0, AudioPackets - previous.AudioPackets),
                AudioActive = Math.Max(0, AudioActive - previous.AudioActive),
                HdCandidates = Math.Max(0, HdCandidates - previous.HdCandidates),
                Raw02Live = Math.Max(0, Raw02Live - previous.Raw02Live),
                BleWrites = Math.Max(0, BleWrites - previous.BleWrites),
                BleErrors = Math.Max(0, BleErrors - previous.BleErrors),
                DroppedRate = Math.Max(0, DroppedRate - previous.DroppedRate),
                DroppedSilence = Math.Max(0, DroppedSilence - previous.DroppedSilence),
                DroppedPcm = Math.Max(0, DroppedPcm - previous.DroppedPcm),
                UsbConfigurationResetCount = Math.Max(
                    0,
                    UsbConfigurationResetCount - previous.UsbConfigurationResetCount),
                HidReportSent = Math.Max(0, HidReportSent - previous.HidReportSent),
                HidReportCompleted = Math.Max(0, HidReportCompleted - previous.HidReportCompleted),
                HidReportFailed = Math.Max(0, HidReportFailed - previous.HidReportFailed),
                HidReportSubmitFailed = Math.Max(
                    0,
                    HidReportSubmitFailed - previous.HidReportSubmitFailed),
                HidReportXferFailed = Math.Max(
                    0,
                    HidReportXferFailed - previous.HidReportXferFailed),
                HidReportNotReady = Math.Max(0, HidReportNotReady - previous.HidReportNotReady),
                HidEndpointKicks = Math.Max(0, HidEndpointKicks - previous.HidEndpointKicks),
                UsbRecoveryCount = Math.Max(0, UsbRecoveryCount - previous.UsbRecoveryCount),
                HidOutputCount = Math.Max(0, HidOutputCount - previous.HidOutputCount),
                HidRumbleUpdates = Math.Max(0, HidRumbleUpdates - previous.HidRumbleUpdates),
                HidRumbleActiveUpdates = Math.Max(0, HidRumbleActiveUpdates - previous.HidRumbleActiveUpdates),
                HidRumbleIgnoredNonzero = Math.Max(0, HidRumbleIgnoredNonzero - previous.HidRumbleIgnoredNonzero),
                HidRumbleBleWrites = Math.Max(0, HidRumbleBleWrites - previous.HidRumbleBleWrites),
                HidRumbleBleErrors = Math.Max(0, HidRumbleBleErrors - previous.HidRumbleBleErrors),
                BleScanStarts = Math.Max(0, BleScanStarts - previous.BleScanStarts),
                BleReconnectAttempts = Math.Max(0, BleReconnectAttempts - previous.BleReconnectAttempts),
                BleFastDropCount = Math.Max(0, BleFastDropCount - previous.BleFastDropCount),
                BleConnectSuccesses = Math.Max(0, BleConnectSuccesses - previous.BleConnectSuccesses),
                BleConnectFailures = Math.Max(0, BleConnectFailures - previous.BleConnectFailures),
                BleDisconnects = Math.Max(0, BleDisconnects - previous.BleDisconnects),
                BleNotifyRx = Math.Max(0, BleNotifyRx - previous.BleNotifyRx),
                BleNotifyParsed = Math.Max(0, BleNotifyParsed - previous.BleNotifyParsed)
            };
        }

        public string ToLogString()
        {
            return "version=" + Version +
                   " uptime_ms=" + UptimeMs +
                   " reset_reason=" + ResetReason + "/" + ResetReasonName +
                   " ble=" + Ble +
                   " ble_interval_us=" + BleConnIntervalUs +
                   " ble_fast_drops=" + BleFastDropCount +
                   " ble_fast_block_ms=" + BleFastBlockMs +
                   " reconnect_task=" + BleReconnectTask.ToString().ToLowerInvariant() +
                   " reconnect_attempts=" + BleReconnectAttempts +
                   " scan_starts=" + BleScanStarts +
                   " connect_ok=" + BleConnectSuccesses +
                   " connect_fail=" + BleConnectFailures +
                   " disconnects=" + BleDisconnects +
                   " disconnect_reason=" + BleDisconnectReason +
                   " disconnect_age_ms=" + BleDisconnectAgeMs +
                   " notify_rx=" + BleNotifyRx +
                   " notify_parsed=" + BleNotifyParsed +
                   " notify_age_ms=" + BleNotifyAgeMs +
                   " notify_parsed_age_ms=" + BleNotifyParsedAgeMs +
                   " notify_rate_millihz=" + BleNotifyActualMilliHz +
                   " notify_gap_us=" + BleNotifyLastGapUs + "/" + BleNotifyMaxGapUs +
                   " notify_min_gap_us=" + BleNotifyMinGapUs +
                   " notify_short_gap_count=" + BleNotifyShortGapCount +
                   " notify_sub_interval_count=" + BleNotifySubIntervalCount +
                   " notify_gap_buckets=" + BleNotifyGapLt3Ms + "/" + BleNotifyGap3To6P5Ms + "/" + BleNotifyGap6P5To10Ms + "/" + BleNotifyGapGe10Ms +
                   " sub_unique_raw/control/motion=" + BleNotifySubRawUnique + "/" + BleNotifySubControlUnique + "/" + BleNotifySubMotionUnique +
                   " raw_unique=" + BleNotifyRawUnique +
                   " raw_repeat=" + BleNotifyRawRepeat +
                   " raw_repeat_streak=" + BleNotifyRawRepeatStreak +
                   " raw_repeat_streak_max=" + BleNotifyRawRepeatStreakMax +
                   " raw_unique_millihz=" + BleNotifyRawUniqueMilliHz +
                   " raw_unique_gap_us=" + BleNotifyRawUniqueGapUs + "/" + BleNotifyRawUniqueMaxGapUs +
                   " parsed_notify_rate_millihz=" + BleNotifyParsedActualMilliHz +
                   " parsed_notify_gap_us=" + BleNotifyParsedLastGapUs + "/" + BleNotifyParsedMaxGapUs +
                   " control_unique=" + BleNotifyControlUnique +
                   " control_repeat=" + BleNotifyControlRepeat +
                   " control_unique_millihz=" + BleNotifyControlUniqueMilliHz +
                   " control_unique_gap_us=" + BleNotifyControlUniqueGapUs + "/" + BleNotifyControlUniqueMaxGapUs +
                   " motion_unique=" + BleNotifyMotionUnique +
                   " motion_repeat=" + BleNotifyMotionRepeat +
                   " motion_unique_millihz=" + BleNotifyMotionUniqueMilliHz +
                   " motion_unique_gap_us=" + BleNotifyMotionUniqueGapUs + "/" + BleNotifyMotionUniqueMaxGapUs +
                   " usb_mounted=" + UsbMounted.ToString().ToLowerInvariant() +
                   " usb_suspended=" + UsbSuspended.ToString().ToLowerInvariant() +
                   " usb_configuration_ready=" + UsbConfigurationReady.ToString().ToLowerInvariant() +
                   " usb_bus_resets=" + UsbBusResetCount +
                   " usb_configuration_resets=" + UsbConfigurationResetCount +
                   " usb_configuration_reset_age_ms=" + UsbConfigurationResetAgeMs +
                   " usb_events=" + UsbMountCount + "/" + UsbUmountCount + "/" + UsbSuspendCount + "/" + UsbResumeCount +
                   " hid_sent=" + HidReportSent +
                   " hid_completed=" + HidReportCompleted +
                   " hid_failed=" + HidReportFailed +
                   " hid_submit_failed=" + HidReportSubmitFailed +
                   " hid_xfer_failed=" + HidReportXferFailed +
                   " hid_submit_streak=" + HidReportSubmitFailureStreak +
                   " hid_submit_failure_age_ms=" + HidReportSubmitFailureAgeMs +
                   " hid_not_ready=" + HidReportNotReady +
                   " hid_endpoint_kicks=" + HidEndpointKicks +
                   " usb_recoveries=" + UsbRecoveryCount +
                   " hid_gap_us=" + HidReportLastGapUs + "/" + HidReportMaxGapUs +
                   " hid_age_ms=" + HidReportAgeMs +
                   " hid_output=" + HidOutputCount +
                   " hid_output_age_ms=" + HidOutputAgeMs +
                   " hid_rumble_updates=" + HidRumbleUpdates +
                   " hid_rumble_active_updates=" + HidRumbleActiveUpdates +
                   " hid_rumble_ignored_nonzero=" + HidRumbleIgnoredNonzero +
                   " hid_rumble_ble=" + HidRumbleBleWrites + "/" + HidRumbleBleErrors +
                   " hid_rumble_state=" + HidRumbleEnabled.ToString().ToLowerInvariant() + "/" + HidRumbleActive.ToString().ToLowerInvariant() +
                   " hid_rumble_flags=" + HidRumbleValid0 + "/" + HidRumbleValid1 + "/" + HidRumbleValid2 +
                   " hid_rumble_motors=" + HidRumbleLeft + "/" + HidRumbleRight +
                   " hid_rumble_preview=" + HidRumblePreview +
                   " haptic=" + Haptic +
                   " source=" + Source +
                   " audio_streaming=" + (AudioStreaming ? "true" : "false") +
                   " audio_alt=" + AudioAlt +
                   " audio_submitted=" + AudioSubmitted +
                   " audio_dropped=" + AudioDropped +
                   " audio_queue=" + AudioQueueDepth + "/" + AudioQueueHigh +
                   " parser=" + AudioParser +
                   " pair=" + AudioPair +
                   " audio_packets=" + AudioPackets +
                   " audio_active=" + AudioActive +
                   " hd_candidate=" + HdCandidate +
                   " hd_packets=" + HdCandidates +
                   " raw02_live=" + Raw02Live +
                   " ble_writes=" + BleWrites +
                   " ble_errors=" + BleErrors +
                   " dropped_rate=" + DroppedRate +
                   " dropped_silence=" + DroppedSilence +
                   " dropped_pcm=" + DroppedPcm +
                   " input_updates=" + InputUpdates +
                   " input_live=" + InputLive.ToString().ToLowerInvariant() +
                   " input_age_ms=" + InputAgeMs +
                   " input_rate_millihz=" + InputRateMilliHz +
                   " input_axes=(" + InputLx + "," + InputLy + "," + InputRx + "," + InputRy + ")" +
                   " last=" + LastMode +
                   " left=" + ShortHex(Left) +
                   " right=" + ShortHex(Right) +
                   " error=" + Error;
        }

        private static long Counter(string status, string name)
        {
            return Math.Max(0, ReadJsonCounter(status, name));
        }

        private static bool Bool(string status, string name)
        {
            return string.Equals(
                ReadJsonBoolString(status, name),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class MonitorDelta
    {
        public long AudioSubmitted { get; init; }
        public long AudioDropped { get; init; }
        public long AudioPackets { get; init; }
        public long AudioActive { get; init; }
        public long HdCandidates { get; init; }
        public long Raw02Live { get; init; }
        public long BleWrites { get; init; }
        public long BleErrors { get; init; }
        public long DroppedRate { get; init; }
        public long DroppedSilence { get; init; }
        public long DroppedPcm { get; init; }
        public long UsbConfigurationResetCount { get; init; }
        public long HidReportSent { get; init; }
        public long HidReportCompleted { get; init; }
        public long HidReportFailed { get; init; }
        public long HidReportSubmitFailed { get; init; }
        public long HidReportXferFailed { get; init; }
        public long HidReportNotReady { get; init; }
        public long HidEndpointKicks { get; init; }
        public long UsbRecoveryCount { get; init; }
        public long HidOutputCount { get; init; }
        public long HidRumbleUpdates { get; init; }
        public long HidRumbleActiveUpdates { get; init; }
        public long HidRumbleIgnoredNonzero { get; init; }
        public long HidRumbleBleWrites { get; init; }
        public long HidRumbleBleErrors { get; init; }
        public long BleScanStarts { get; init; }
        public long BleReconnectAttempts { get; init; }
        public long BleFastDropCount { get; init; }
        public long BleConnectSuccesses { get; init; }
        public long BleConnectFailures { get; init; }
        public long BleDisconnects { get; init; }
        public long BleNotifyRx { get; init; }
        public long BleNotifyParsed { get; init; }

        public string ToLogString()
        {
            return "audio_submitted=+" + AudioSubmitted +
                   " audio_dropped=+" + AudioDropped +
                   " audio_packets=+" + AudioPackets +
                   " audio_active=+" + AudioActive +
                   " hd_packets=+" + HdCandidates +
                   " raw02_live=+" + Raw02Live +
                   " ble_writes=+" + BleWrites +
                   " ble_errors=+" + BleErrors +
                   " dropped_rate=+" + DroppedRate +
                   " dropped_silence=+" + DroppedSilence +
                   " dropped_pcm=+" + DroppedPcm +
                   " usb_configuration_resets=+" + UsbConfigurationResetCount +
                   " hid_sent=+" + HidReportSent +
                   " hid_completed=+" + HidReportCompleted +
                   " hid_failed=+" + HidReportFailed +
                   " hid_submit_failed=+" + HidReportSubmitFailed +
                   " hid_xfer_failed=+" + HidReportXferFailed +
                   " hid_not_ready=+" + HidReportNotReady +
                   " hid_endpoint_kicks=+" + HidEndpointKicks +
                   " usb_recoveries=+" + UsbRecoveryCount +
                   " hid_output=+" + HidOutputCount +
                   " hid_rumble_updates=+" + HidRumbleUpdates +
                   " hid_rumble_active=+" + HidRumbleActiveUpdates +
                   " hid_rumble_ignored=+" + HidRumbleIgnoredNonzero +
                   " hid_rumble_writes=+" + HidRumbleBleWrites +
                   " hid_rumble_errors=+" + HidRumbleBleErrors +
                   " scans=+" + BleScanStarts +
                   " reconnect_attempts=+" + BleReconnectAttempts +
                   " fast_drops=+" + BleFastDropCount +
                   " connect_ok=+" + BleConnectSuccesses +
                   " connect_fail=+" + BleConnectFailures +
                   " disconnects=+" + BleDisconnects +
                   " notify_rx=+" + BleNotifyRx +
                   " notify_parsed=+" + BleNotifyParsed;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}



