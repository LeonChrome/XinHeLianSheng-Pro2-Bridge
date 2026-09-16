using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string UsbipPortableInstallHint =
        "如果使用的是非标准绿色版/便携版，并且它既不写安装注册表、不加入 PATH、也不位于常见 USBIP 目录，程序无法自动发现；建议直接使用本 EXE 内置的正式 USBIP 安装器。";
    private const int MaxUiLogCharacters = 120000;
    private const int TrimmedUiLogCharacters = 70000;
    private readonly StringBuilder log = new();
    private readonly Pro2ControllerSlot[] pro2Slots =
    [
        new(1, true),
        new(2, false),
        new(3, false),
        new(4, false)
    ];
    private readonly long[] lastDisconnectNoticeSequence = [-1, -1, -1, -1];
    private readonly SynchronizationContext? uiContext = SynchronizationContext.Current;
    private readonly Pro2BleInputSource inputSource;
    private readonly ProfessionalHidAuditController professionalHidAuditController = new();
    private readonly SessionLogWriter sessionLog = new();
    private readonly V60UserSettings userSettings = V60UserSettings.Load();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim bleOperationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCts = new();
    private CancellationTokenSource? autoReconnectCts;
    private Task? autoReconnectTask;
    private Process? viiperProcess;
    private int? localViiperApiPort;
    private ViiperBridgeSession? session;
    private string host = "127.0.0.1";
    private string port = "3242";
    private string status = "V6.2.32 Test r2 已就绪。完整行程采样与完成提示修复版。";
    private string inputStatus = "真实 Pro2 BLE 输入未连接。";
    private string selectedPushRateLabel = ViiperPushRateOption.Default.Label;
    private string selectedBackendLabel = VirtualBackendOption.Default.Label;
    private string selectedStickProcessingLabel = StickProcessingOption.Default.Label;
    private bool audioEndpointGuardEnabled = true;
    private bool running;
    private bool busy;
    private bool shuttingDown;
    private bool autoReconnectEnabled;
    private bool launchAtLoginEnabled;
    private bool autoReconnectOnStartupEnabled;
    private bool startupAutomationActive;
    private bool startupAutomationPaused;
    private bool startupAutomationRan;
    private bool startupAutomationNoticeSent;
    private bool steamGhostNoticeSent;
    private DateTimeOffset? startupAutomationDeadlineUtc;
    private string runtimeReadinessText = "";
    private string professionalBiasStatusText =
        "Bias: NotCalibrated / source=none / applied=false / updates=0 / raw=0,0,0";
    private string professionalRawGyroText = "Raw Gyro raw: X=0 Y=0 Z=0";
    private string professionalCorrectedGyroText = "Corrected Gyro Preview (°/s): Pitch Rate=0 Yaw Rate=0 Roll Rate=0";
    private string professionalOutputGyroText = "Output Gyro (°/s): Pitch Rate=0 Yaw Rate=0 Roll Rate=0";
    private string professionalIntegratedAngleText = "Integrated Angle (°): Pitch Angle=0 Yaw Angle=0 Roll Angle=0 · integral_state=Disabled · integral_running=false";
    private string professionalNinetyDegreeTestText = "90° test idle.";
    private string selectedProfessionalHidAuditMode = ProfessionalHidAuditMode.Normal.ToString();
    private string professionalStaticGyroXRaw = "0";
    private string professionalStaticGyroYRaw = "0";
    private string professionalStaticGyroZRaw = "0";
    private string professionalHidAuditStatusText = "HID Audit: Normal · final report gyro follows selected_output_ds_raw.";
    private double rumbleMultiplier = 1.0;
    private double ps5GyroScale = 1.0;
    private bool professionalInvertGyroPitch;
    private bool professionalInvertGyroYaw;
    private bool professionalInvertGyroRoll;
    private ViiperVirtualMode selectedMode = ViiperVirtualMode.DualSenseLike;
    private ViiperVirtualMode? activeMode;
    private const int LostInputTimeoutMilliseconds = 2000;

    public MainViewModel()
    {
        inputSource = pro2Slots[0].InputSource;
        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            int slotIndex = slot.Index;
            slot.InputSource.DisconnectDetected +=
                signal => OnPro2DisconnectDetected(slotIndex, signal);
        }
        PingCommand = new RelayCommand(_ => RunExclusiveAsync("Ping VIIPER", PingAsync));
        InstallUsbipCommand = new RelayCommand(_ => RunExclusiveAsync("安装/修复 usbip-win2", InstallUsbipAsync));
        StartViiperServerCommand = new RelayCommand(_ => RunExclusiveAsync("启动本地 VIIPER", StartLocalViiperServerAsync));
        ScanPro2InputCommand = new RelayCommand(_ => RunExclusiveAsync("扫描 Pro2 BLE", ScanPro2InputAsync));
        ConnectPro2InputCommand = new RelayCommand(_ => RunExclusiveAsync("进入游戏", EnterGameAsync));
        DisconnectPro2InputCommand = new RelayCommand(_ => RunExclusiveAsync("断开 Pro2 BLE", DisconnectPro2InputAsync));
        CalibrateSourceGyroCommand = new RelayCommand(_ => RunExclusiveAsync("校准真实 Pro2 陀螺仪", CalibrateSourceGyroAsync));
        FixAudioDefaultsCommand = new RelayCommand(_ => RunExclusiveAsync("修复音频默认设备", FixAudioDefaultsAsync));
        DumpControllerEnumerationCommand = new RelayCommand(_ => RunExclusiveAsync("设备枚举诊断", DumpControllerEnumerationAsync));
        CleanupStaleVirtualDevicesCommand = new RelayCommand(_ => RunExclusiveAsync("清理残留虚拟设备", CleanupStaleVirtualDevicesAsync));
        RefreshSteamControllerCacheCommand = new RelayCommand(_ => RunExclusiveAsync("刷新 Steam 控制器缓存", RefreshSteamControllerCacheAsync));
        ExportDiagnosticsLogCommand = new RelayCommand(_ => RunExclusiveAsync("导出诊断包", ExportDiagnosticsLogAsync));
        ScanPro2SlotCommand = new RelayCommand(parameter => RunExclusiveAsync(
            "扫描 Pro2 Slot",
            token => ScanPro2SlotAsync(SlotFromParameter(parameter), token)));
        ConnectPro2SlotCommand = new RelayCommand(parameter => RunExclusiveAsync(
            "连接 Pro2 Slot",
            token => StartPro2SlotAutoReconnectAsync(SlotFromParameter(parameter), token)));
        DisconnectPro2SlotCommand = new RelayCommand(parameter => RunExclusiveAsync(
            "断开 Pro2 Slot",
            token => DisconnectPro2SlotAsync(SlotFromParameter(parameter), token)));
        CalibrateStickCenterSlotCommand = new RelayCommand(parameter => RunExclusiveAsync(
            "校准 Pro2 摇杆零位",
            token => CalibrateStickCenterSlotAsync(SlotFromParameter(parameter), token)));
        CalibrateStickRangeSlotCommand = new RelayCommand(parameter => RunExclusiveAsync(
            "校准 Pro2 摇杆完整行程",
            token => CalibrateStickRangeSlotAsync(SlotFromParameter(parameter), token)));
        StartDualSenseCommand = new RelayCommand(_ => RunExclusiveAsync(
            "切换 新和联胜 / PS5",
            token => SwitchModeAsync(ViiperDeviceProfile.DualSenseLike, token)));
        StartDualSenseEdgeCommand = new RelayCommand(_ => RunExclusiveAsync(
            "切换 PS5 Edge / 背键",
            token => SwitchModeAsync(ViiperDeviceProfile.DualSenseEdge, token)));
        StartDualSenseProfessionalImuCommand = new RelayCommand(_ => RunExclusiveAsync(
            "切换 PS5 Professional IMU Test",
            token => SwitchModeAsync(ViiperDeviceProfile.DualSenseProfessionalImuTest, token)));
        StartPro2Command = new RelayCommand(_ => RunExclusiveAsync(
            "切换 Pro2 / Nintendo",
            token => SwitchModeAsync(ViiperDeviceProfile.Pro2, token)));
        StartXboxCommand = new RelayCommand(_ => RunExclusiveAsync(
            "切换 Xbox / XInput",
            token => SwitchModeAsync(ViiperDeviceProfile.Xbox, token)));
        StartXboxProfessionalImuCommand = new RelayCommand(_ => RunExclusiveAsync(
            "切换 Xbox Professional IMU Test",
            token => SwitchModeAsync(ViiperDeviceProfile.XboxProfessionalImuTest, token)));
        CalibrateGyroBiasCommand = new RelayCommand(_ =>
            ExecuteProfessionalImuAction("Calibrate Gyro Bias 3s", s => s.StartGyroBiasCalibration()));
        ResetGyroBiasCommand = new RelayCommand(_ =>
            ExecuteProfessionalImuAction("Reset Gyro Bias", s => s.ResetGyroBias()));
        ResetIntegralCommand = new RelayCommand(_ =>
            ExecuteProfessionalImuAction("Reset Integral", s => s.ResetProfessionalIntegral()));
        StartPitch90TestCommand = new RelayCommand(_ =>
            ExecuteProfessionalImuAction("Start Pitch 90° Test", s => s.StartNinetyDegreeTest(ProfessionalImuTestAxis.Pitch)));
        StartYaw90TestCommand = new RelayCommand(_ =>
            ExecuteProfessionalImuAction("Start Yaw 90° Test", s => s.StartNinetyDegreeTest(ProfessionalImuTestAxis.Yaw)));
        StartRoll90TestCommand = new RelayCommand(_ =>
            ExecuteProfessionalImuAction("Start Roll 90° Test", s => s.StartNinetyDegreeTest(ProfessionalImuTestAxis.Roll)));
        Stop90TestCommand = new RelayCommand(_ =>
            ExecuteProfessionalImuAction("Stop 90° Test", s => s.StopNinetyDegreeTest()));
        PulseGyroXCommand = new RelayCommand(_ => StartProfessionalHidPulse("X"));
        PulseGyroYCommand = new RelayCommand(_ => StartProfessionalHidPulse("Y"));
        PulseGyroZCommand = new RelayCommand(_ => StartProfessionalHidPulse("Z"));
        StopCommand = new RelayCommand(_ => RunExclusiveAsync("停止虚拟设备", _ => StopAsync()));
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        rumbleMultiplier =
            V60UserSettings.NormalizeRumbleMultiplier(
                userSettings.RumbleMultiplier);
        ps5GyroScale =
            V60UserSettings.NormalizePs5GyroScale(
                userSettings.Ps5GyroScalePitch);
        professionalInvertGyroPitch = userSettings.ProfessionalInvertGyroPitch;
        professionalInvertGyroYaw = userSettings.ProfessionalInvertGyroYaw;
        professionalInvertGyroRoll = userSettings.ProfessionalInvertGyroRoll;
        selectedPushRateLabel =
            ViiperPushRateOption.FromLabel(userSettings.PushRateLabel).Label;
        selectedBackendLabel =
            VirtualBackendOption.FromLabel(userSettings.BackendLabel).Label;
        selectedStickProcessingLabel =
            StickProcessingOption.FromLabel(userSettings.StickProcessingLabel).Label;
        audioEndpointGuardEnabled = userSettings.AudioEndpointGuardEnabled;
        launchAtLoginEnabled = userSettings.LaunchAtLoginEnabled && StartupLaunchRegistration.IsEnabled();
        autoReconnectOnStartupEnabled = userSettings.AutoReconnectOnStartupEnabled;
        if (userSettings.LaunchAtLoginEnabled != launchAtLoginEnabled)
        {
            userSettings.LaunchAtLoginEnabled = launchAtLoginEnabled;
            SaveUserSettings("[STARTUP]");
        }
        selectedMode = ModeFromKey(userSettings.SelectedModeKey);
        inputSource.SetRumbleGain(rumbleMultiplier);
        inputSource.SetStickProcessingMode(SelectedStickProcessingOption.Mode);
        ApplyPro2SlotRuntimeOptions();
        RefreshRuntimeReadiness();
        _ = RefreshRuntimeReadinessAsync();
        AppendLog("[SESSION_LOG] " + sessionLog.FilePath);
        if (!string.IsNullOrWhiteSpace(StartupProcessGuard.LastSummary))
        {
            AppendLog(StartupProcessGuard.LastSummary);
        }
        AppendLog("V6.2.32 Test r2 说明：修复后台重复加载零位配置会中止完整行程采样的问题；校准期间显示倒计时，结束后明确提示成功、失败原因或离线中断。");
        AppendLog("[LOG_POLICY] previous v6 logs are cleaned at startup; manager log limit=" +
                  (SessionLogWriter.MaxLogBytes / 1024 / 1024) + "MB; VIIPER server log level=info.");
        AppendLog("[RUNTIME] " + RuntimeReadinessText);
        AppendLog("[RUMBLE_GAIN] multiplier=" + rumbleMultiplier.ToString("F1"));
        AppendLog("[STARTUP] launch_at_login=" + launchAtLoginEnabled +
                  " auto_reconnect_on_startup=" + autoReconnectOnStartupEnabled +
                  " selected_mode=" + ModeKey(selectedMode));
        AppendLog("[LINK_TUNING] cadence=" + PushCadenceTelemetry(SelectedPushRateOption) +
                  " gyro_mode=" + ViiperGyroModeOption.Default.Label +
                  " ps5_imu_map=" + Ps5ImuMappingOption.Default.Mapping.TelemetryValue +
                  " ps5_output_imu=" + SelectedPs5OutputImuTuning.TelemetryValue +
                  " gyro_axis_inv=" + default(GyroAxisInversion).TelemetryValue +
                  " stick=" + SelectedStickProcessingOption.Label +
                  " audio_guard=" + audioEndpointGuardEnabled +
                  " backend=" + SelectedBackendOption.Label);
    }

    public string Host
    {
        get => host;
        set { host = value; OnPropertyChanged(); }
    }

    public string Port
    {
        get => port;
        set { port = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => status;
        set { status = value; OnPropertyChanged(); }
    }

    public string InputStatus
    {
        get => inputStatus;
        set { inputStatus = value; OnPropertyChanged(); }
    }

    public bool Running
    {
        get => running;
        set
        {
            running = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(CanManageRuntime));
            OnPropertyChanged(nameof(ModeHeadline));
        }
    }

    public bool Busy
    {
        get => busy;
        private set
        {
            busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(CanOperate));
            OnPropertyChanged(nameof(CanManageRuntime));
            OnPropertyChanged(nameof(CanManualBleControl));
        }
    }

    public bool CanStart => !Busy;
    public bool CanStop => Running && !Busy;
    public bool CanOperate => !Busy;
    public bool CanManageRuntime => !Running && !Busy;
    public bool CanManualBleControl =>
        !Busy &&
        !AutoReconnectEnabled &&
        pro2Slots.All(slot => !slot.AutoReconnectEnabled);

    public bool AutoReconnectEnabled
    {
        get => autoReconnectEnabled;
        private set
        {
            if (autoReconnectEnabled == value)
            {
                return;
            }

            autoReconnectEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanManualBleControl));
            RaiseConnectionStateChanged();
        }
    }

    public bool LaunchAtLoginEnabled
    {
        get => launchAtLoginEnabled;
        set
        {
            if (launchAtLoginEnabled == value)
            {
                return;
            }

            try
            {
                StartupLaunchRegistration.SetEnabled(value);
                launchAtLoginEnabled = StartupLaunchRegistration.IsEnabled();
                userSettings.LaunchAtLoginEnabled = launchAtLoginEnabled;
                SaveUserSettings("[STARTUP]");
                AppendLog("[STARTUP] launch_at_login=" + launchAtLoginEnabled);
            }
            catch (Exception ex)
            {
                AppendLog("[STARTUP] launch_at_login failed: " + ex.Message);
                launchAtLoginEnabled = StartupLaunchRegistration.IsEnabled();
                userSettings.LaunchAtLoginEnabled = launchAtLoginEnabled;
                SaveUserSettings("[STARTUP]");
                RequestUserNotification("开机自启动设置失败", FirstLine(ex.Message));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(StartupAutomationSummary));
        }
    }

    public bool AutoReconnectOnStartupEnabled
    {
        get => autoReconnectOnStartupEnabled;
        set
        {
            if (autoReconnectOnStartupEnabled == value)
            {
                return;
            }

            autoReconnectOnStartupEnabled = value;
            userSettings.AutoReconnectOnStartupEnabled = value;
            SaveUserSettings("[STARTUP]");
            AppendLog("[STARTUP] auto_reconnect_on_startup=" + value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartupAutomationSummary));
        }
    }

    public string StartupAutomationSummary =>
        LaunchAtLoginEnabled || AutoReconnectOnStartupEnabled
            ? "开机自启=" + (LaunchAtLoginEnabled ? "开" : "关") +
              " · 启动自动进入=" + (AutoReconnectOnStartupEnabled ? "开" : "关") +
              " · 上次模式=" + SelectedModeLabel
            : "开机自动化未启用。";

    public string RuntimeReadinessText
    {
        get => runtimeReadinessText;
        private set
        {
            runtimeReadinessText = value;
            OnPropertyChanged();
        }
    }

    public double RumbleMultiplier
    {
        get => rumbleMultiplier;
        set
        {
            double normalized =
                V60UserSettings.NormalizeRumbleMultiplier(value);
            if (Math.Abs(rumbleMultiplier - normalized) < 0.001)
            {
                return;
            }

            rumbleMultiplier = normalized;
            ApplyPro2SlotRuntimeOptions();
            userSettings.RumbleMultiplier = normalized;
            try
            {
                userSettings.Save();
            }
            catch (Exception ex)
            {
                AppendLog("[RUMBLE_GAIN] settings save warning: " + ex.Message);
            }
            AppendLog("[RUMBLE_GAIN] multiplier=" + normalized.ToString("F1"));
            OnPropertyChanged();
            OnPropertyChanged(nameof(RumbleMultiplierText));
        }
    }

    public string RumbleMultiplierText =>
        rumbleMultiplier.ToString("F1") + "x";

    public double Ps5GyroScale
    {
        get => ps5GyroScale;
        set
        {
            double normalized =
                V60UserSettings.NormalizePs5GyroScale(value);
            if (Math.Abs(ps5GyroScale - normalized) < 0.001)
            {
                return;
            }

            ps5GyroScale = normalized;
            userSettings.Ps5GyroScalePitch = normalized;
            userSettings.Ps5GyroScaleYaw = normalized;
            userSettings.Ps5GyroScaleRoll = normalized;
            SaveUserSettings("[PS5_IMU]");
            AppendLog("[PS5_IMU] gyro_scale_pitch=" + normalized.ToString("0.##") +
                      " gyro_scale_yaw=" + normalized.ToString("0.##") +
                      " gyro_scale_roll=" + normalized.ToString("0.##") +
                      (Running ? " apply=next_session" : " apply=next_start"));
            if (session is { HasProfessionalImuRuntime: true })
            {
                AppendLog("[PRO_IMU] " + session.ResetProfessionalIntegral() +
                          " reason=ps5_gyro_scale_changed");
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(Ps5GyroScaleText));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string Ps5GyroScaleText =>
        ps5GyroScale.ToString("0.00") + "x";

    public bool ProfessionalInvertGyroPitch
    {
        get => professionalInvertGyroPitch;
        set
        {
            if (professionalInvertGyroPitch == value)
            {
                return;
            }

            professionalInvertGyroPitch = value;
            ApplyProfessionalGyroInversionSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfessionalGyroInversionSummary));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public bool ProfessionalInvertGyroYaw
    {
        get => professionalInvertGyroYaw;
        set
        {
            if (professionalInvertGyroYaw == value)
            {
                return;
            }

            professionalInvertGyroYaw = value;
            ApplyProfessionalGyroInversionSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfessionalGyroInversionSummary));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public bool ProfessionalInvertGyroRoll
    {
        get => professionalInvertGyroRoll;
        set
        {
            if (professionalInvertGyroRoll == value)
            {
                return;
            }

            professionalInvertGyroRoll = value;
            ApplyProfessionalGyroInversionSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfessionalGyroInversionSummary));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string ProfessionalGyroInversionSummary =>
        "Pitch=" + (professionalInvertGyroPitch ? "invert" : "normal") +
        ", Yaw=" + (professionalInvertGyroYaw ? "invert" : "normal") +
        ", Roll=" + (professionalInvertGyroRoll ? "invert" : "normal");

    public IReadOnlyList<string> PushRateChoices =>
        ViiperPushRateOption.All.Select(o => o.Label).ToArray();

    public IReadOnlyList<string> BackendChoices =>
        VirtualBackendOption.All.Select(o => o.Label).ToArray();

    public IReadOnlyList<string> StickProcessingChoices =>
        StickProcessingOption.All.Select(o => o.Label).ToArray();

    public string SelectedPushRate
    {
        get => selectedPushRateLabel;
        set
        {
            string normalized = ViiperPushRateOption.FromLabel(value).Label;
            if (selectedPushRateLabel == normalized)
            {
                return;
            }

            selectedPushRateLabel = normalized;
            userSettings.PushRateLabel = normalized;
            SaveUserSettings("[LINK_TUNING]");
            AppendLog("[LINK_TUNING] cadence=" + PushCadenceTelemetry(SelectedPushRateOption) +
                      " interval_ms=" + SelectedPushRateOption.Interval.TotalMilliseconds.ToString("F1") +
                      (Running ? " apply=next_session" : " apply=next_start"));
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string SelectedBackend
    {
        get => selectedBackendLabel;
        set
        {
            string normalized = VirtualBackendOption.FromLabel(value).Label;
            if (selectedBackendLabel == normalized)
            {
                return;
            }

            selectedBackendLabel = normalized;
            userSettings.BackendLabel = normalized;
            SaveUserSettings("[BACKEND]");
            AppendLog("[BACKEND] selected=" + SelectedBackendOption.Label +
                      " mode=" + SelectedBackendOption.Mode +
                      (Running ? " apply=next_session" : " apply=next_start"));
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string SelectedStickProcessing
    {
        get => selectedStickProcessingLabel;
        set
        {
            string normalized = StickProcessingOption.FromLabel(value).Label;
            if (selectedStickProcessingLabel == normalized)
            {
                return;
            }

            selectedStickProcessingLabel = normalized;
            userSettings.StickProcessingLabel = normalized;
            ApplyPro2SlotRuntimeOptions();
            SaveUserSettings("[STICK_MODE]");
            AppendLog("[STICK_MODE] selected=" + SelectedStickProcessingOption.Label +
                      " mode=" + SelectedStickProcessingOption.Mode +
                      " apply=immediate");
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public bool AudioEndpointGuardEnabled
    {
        get => audioEndpointGuardEnabled;
        set
        {
            if (audioEndpointGuardEnabled == value)
            {
                return;
            }

            audioEndpointGuardEnabled = value;
            userSettings.AudioEndpointGuardEnabled = value;
            SaveUserSettings("[AUDIO_GUARD]");
            AppendLog("[AUDIO_GUARD] enabled=" + value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioEndpointGuardText));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string AudioEndpointGuardText => AudioEndpointGuardEnabled
        ? "音频保护已开启：PS5 模式会自动阻止 DualSense 成为默认播放/通信/麦克风。"
        : "音频保护已关闭：Windows 可能把 DualSense 设为默认音频或麦克风。";

    public string LinkTuningSummary =>
        "push=" + (SelectedPushRateOption.SourcePaced
            ? "真实 BLE 源节奏"
            : SelectedPushRateOption.Hz.ToString("F1") + "Hz") +
        " · ps5_imu=+x+z-y calibrated_state->dsraw scale=" +
        SelectedPs5OutputImuTuning.GyroScalePitch.ToString("0.##") + "x" +
        " · stick=" + SelectedStickProcessingOption.Label +
        " · audio_guard=" + (AudioEndpointGuardEnabled ? "on" : "off") +
        " · backend=" + SelectedBackendOption.Label;

    public IReadOnlyList<Pro2ControllerSlot> Pro2Slots => pro2Slots;

    public bool IsDualSenseSelected => selectedMode == ViiperVirtualMode.DualSenseLike;
    public bool IsDualSenseEdgeSelected => selectedMode == ViiperVirtualMode.DualSenseEdge;
    public bool IsDualSenseProfessionalImuSelected => selectedMode == ViiperVirtualMode.DualSenseProfessionalImuTest;
    public bool IsPro2Selected => selectedMode == ViiperVirtualMode.Pro2;
    public bool IsXboxSelected => selectedMode == ViiperVirtualMode.Xbox;
    public bool IsXboxProfessionalImuSelected => selectedMode == ViiperVirtualMode.XboxProfessionalImuTest;
    public bool IsDualSenseActive => Running && activeMode == ViiperVirtualMode.DualSenseLike;
    public bool IsDualSenseEdgeActive => Running && activeMode == ViiperVirtualMode.DualSenseEdge;
    public bool IsDualSenseProfessionalImuActive => Running && activeMode == ViiperVirtualMode.DualSenseProfessionalImuTest;
    public bool IsPro2Active => Running && activeMode == ViiperVirtualMode.Pro2;
    public bool IsXboxActive => Running && activeMode == ViiperVirtualMode.Xbox;
    public bool IsXboxProfessionalImuActive => Running && activeMode == ViiperVirtualMode.XboxProfessionalImuTest;
    public bool IsInputConnected =>
        inputSource.IsRunning &&
        inputSource.TryGetLatest(out _, out TimeSpan age) &&
        age <= TimeSpan.FromMilliseconds(LostInputTimeoutMilliseconds);
    public string BleButtonText => IsInputConnected
        ? "PRO2 已连接 · 自动守护中"
        : AutoReconnectEnabled
            ? "正在寻找 PRO2 · 自动重连中"
            : "连接 PRO2 · 进入游戏";
    public string BleStateText => IsInputConnected
        ? inputSource.IsPerformanceDegraded
            ? "BLE LIVE · DEGRADED · " + inputSource.LinkRateClass.ToUpperInvariant()
            : "BLE LIVE · INPUT ONLINE · " + inputSource.LinkRateClass.ToUpperInvariant()
        : AutoReconnectEnabled
            ? "BLE SEARCHING · AUTO RECONNECT"
            : "BLE STANDBY · WAITING FOR PLAYER";
    public string SelectedModeLabel => SelectedProfile.Label;
    public string SelectedHeroName => selectedMode switch
    {
        ViiperVirtualMode.DualSenseLike => "KRATOS",
        ViiperVirtualMode.DualSenseEdge => "KRATOS EDGE",
        ViiperVirtualMode.DualSenseProfessionalImuTest => "KRATOS · LAB",
        ViiperVirtualMode.Pro2 => "MARIO",
        ViiperVirtualMode.Xbox => "MASTER CHIEF",
        ViiperVirtualMode.XboxProfessionalImuTest => "MASTER CHIEF · LAB",
        _ => ""
    };
    public string SelectedModeSubtitle => selectedMode switch
    {
        ViiperVirtualMode.DualSenseLike => "新和联胜 · DUALSENSE IDENTITY · 054C:0CE6",
        ViiperVirtualMode.DualSenseEdge => "PS5 EDGE · L4/R4 PADDLES · 054C:0DF2",
        ViiperVirtualMode.DualSenseProfessionalImuTest => "PROFESSIONAL IMU TEST · RAW→G/DPS→DS RAW · HD HAPTIC",
        ViiperVirtualMode.Pro2 => "NINTENDO PROTOCOL · HD RUMBLE · 057E:2069",
        ViiperVirtualMode.Xbox => "XINPUT PROTOCOL · 045E:028E",
        ViiperVirtualMode.XboxProfessionalImuTest => "PROFESSIONAL IMU TEST · DIAGNOSTIC ONLY · XINPUT",
        _ => ""
    };
    public string ModeHeadline => Running && activeMode.HasValue
        ? "ACTIVE LOADOUT · " + ActiveProfile.Label
        : "SELECTED LOADOUT · " + SelectedModeLabel;

    public string ProfessionalBiasStatusText
    {
        get => professionalBiasStatusText;
        private set { professionalBiasStatusText = value; OnPropertyChanged(); }
    }

    public string ProfessionalRawGyroText
    {
        get => professionalRawGyroText;
        private set { professionalRawGyroText = value; OnPropertyChanged(); }
    }

    public string ProfessionalCorrectedGyroText
    {
        get => professionalCorrectedGyroText;
        private set { professionalCorrectedGyroText = value; OnPropertyChanged(); }
    }

    public string ProfessionalOutputGyroText
    {
        get => professionalOutputGyroText;
        private set { professionalOutputGyroText = value; OnPropertyChanged(); }
    }

    public string ProfessionalIntegratedAngleText
    {
        get => professionalIntegratedAngleText;
        private set { professionalIntegratedAngleText = value; OnPropertyChanged(); }
    }

    public string ProfessionalNinetyDegreeTestText
    {
        get => professionalNinetyDegreeTestText;
        private set { professionalNinetyDegreeTestText = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<string> ProfessionalHidAuditModeChoices =>
        Enum.GetNames<ProfessionalHidAuditMode>();

    public string SelectedProfessionalHidAuditMode
    {
        get => selectedProfessionalHidAuditMode;
        set
        {
            if (!Enum.TryParse(value, out ProfessionalHidAuditMode mode))
            {
                return;
            }

            string normalized = mode.ToString();
            if (selectedProfessionalHidAuditMode == normalized)
            {
                return;
            }

            selectedProfessionalHidAuditMode = normalized;
            string summary = professionalHidAuditController.SetMode(mode);
            ProfessionalHidAuditStatusText = "HID Audit: " + summary;
            AppendLog("[PRO_IMU_AUDIT] mode changed " + summary);
            OnPropertyChanged();
        }
    }

    public string ProfessionalStaticGyroXRaw
    {
        get => professionalStaticGyroXRaw;
        set
        {
            professionalStaticGyroXRaw = value;
            ApplyProfessionalStaticGyroRaw();
            OnPropertyChanged();
        }
    }

    public string ProfessionalStaticGyroYRaw
    {
        get => professionalStaticGyroYRaw;
        set
        {
            professionalStaticGyroYRaw = value;
            ApplyProfessionalStaticGyroRaw();
            OnPropertyChanged();
        }
    }

    public string ProfessionalStaticGyroZRaw
    {
        get => professionalStaticGyroZRaw;
        set
        {
            professionalStaticGyroZRaw = value;
            ApplyProfessionalStaticGyroRaw();
            OnPropertyChanged();
        }
    }

    public string ProfessionalHidAuditStatusText
    {
        get => professionalHidAuditStatusText;
        private set { professionalHidAuditStatusText = value; OnPropertyChanged(); }
    }

    public string LogText => log.ToString();

    public ICommand PingCommand { get; }
    public ICommand InstallUsbipCommand { get; }
    public ICommand StartViiperServerCommand { get; }
    public ICommand ScanPro2InputCommand { get; }
    public ICommand ConnectPro2InputCommand { get; }
    public ICommand DisconnectPro2InputCommand { get; }
    public ICommand CalibrateSourceGyroCommand { get; }
    public ICommand FixAudioDefaultsCommand { get; }
    public ICommand DumpControllerEnumerationCommand { get; }
    public ICommand CleanupStaleVirtualDevicesCommand { get; }
    public ICommand RefreshSteamControllerCacheCommand { get; }
    public ICommand ExportDiagnosticsLogCommand { get; }
    public ICommand ScanPro2SlotCommand { get; }
    public ICommand ConnectPro2SlotCommand { get; }
    public ICommand DisconnectPro2SlotCommand { get; }
    public ICommand CalibrateStickCenterSlotCommand { get; }
    public ICommand CalibrateStickRangeSlotCommand { get; }
    public ICommand StartDualSenseCommand { get; }
    public ICommand StartDualSenseEdgeCommand { get; }
    public ICommand StartDualSenseProfessionalImuCommand { get; }
    public ICommand StartPro2Command { get; }
    public ICommand StartXboxCommand { get; }
    public ICommand StartXboxProfessionalImuCommand { get; }
    public ICommand CalibrateGyroBiasCommand { get; }
    public ICommand ResetGyroBiasCommand { get; }
    public ICommand ResetIntegralCommand { get; }
    public ICommand StartPitch90TestCommand { get; }
    public ICommand StartYaw90TestCommand { get; }
    public ICommand StartRoll90TestCommand { get; }
    public ICommand Stop90TestCommand { get; }
    public ICommand PulseGyroXCommand { get; }
    public ICommand PulseGyroYCommand { get; }
    public ICommand PulseGyroZCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ClearLogCommand { get; }

    private void ExecuteProfessionalImuAction(
        string operation,
        Func<ViiperBridgeSession, string> action)
    {
        if (session is not { HasProfessionalImuRuntime: true } activeSession)
        {
            string message = "[PRO_IMU] " + operation +
                             " ignored: start PS5 / Professional IMU Test or Xbox / Professional IMU Test first.";
            AppendLog(message);
            Status = "请先进入 Professional IMU Test 模式。";
            return;
        }

        string result = action(activeSession);
        AppendLog("[PRO_IMU_UI] " + operation + ": " + result);
        Status = result;
    }

    private void UpdateProfessionalImuSnapshot(ProfessionalImuUiSnapshot snapshot)
    {
        ProfessionalBiasStatusText = snapshot.BiasStatusText;
        ProfessionalRawGyroText = snapshot.RawGyroText;
        ProfessionalCorrectedGyroText = snapshot.CorrectedGyroText;
        ProfessionalOutputGyroText = snapshot.OutputGyroText;
        ProfessionalIntegratedAngleText = snapshot.IntegratedAngleText;
        ProfessionalNinetyDegreeTestText = snapshot.NinetyDegreeTestText;
    }

    private void ApplyProfessionalGyroInversionSettings()
    {
        userSettings.ProfessionalInvertGyroPitch = professionalInvertGyroPitch;
        userSettings.ProfessionalInvertGyroYaw = professionalInvertGyroYaw;
        userSettings.ProfessionalInvertGyroRoll = professionalInvertGyroRoll;
        SaveUserSettings("[PRO_IMU]");
        string summary = ProfessionalGyroInversionSummary;
        AppendLog("[PRO_IMU] gyro_output_inversion " + summary +
                  (session is { HasProfessionalImuRuntime: true }
                      ? " apply=immediate"
                      : " apply=next_professional_session"));
        if (session is { HasProfessionalImuRuntime: true } activeSession)
        {
            AppendLog("[PRO_IMU] " + activeSession.SetProfessionalGyroInversion(
                professionalInvertGyroPitch,
                professionalInvertGyroYaw,
                professionalInvertGyroRoll));
        }
    }

    private void ApplyPro2SlotRuntimeOptions()
    {
        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            slot.InputSource.SetRumbleGain(rumbleMultiplier);
            slot.InputSource.SetStickProcessingMode(SelectedStickProcessingOption.Mode);
            ApplySavedStickCalibration(
                slot,
                LastKnownPro2AddressForSlot(slot),
                logChange: false);
            slot.RefreshFromSource();
        }
    }

    private void ApplyProfessionalStaticGyroRaw()
    {
        if (!short.TryParse(professionalStaticGyroXRaw, out short x) ||
            !short.TryParse(professionalStaticGyroYRaw, out short y) ||
            !short.TryParse(professionalStaticGyroZRaw, out short z))
        {
            ProfessionalHidAuditStatusText = "HID Audit: static raw 输入无效，范围应为 -32768..32767。";
            return;
        }

        string summary = professionalHidAuditController.SetStaticRaw(x, y, z);
        ProfessionalHidAuditStatusText = "HID Audit: " + summary;
        AppendLog("[PRO_IMU_AUDIT] static raw changed " + summary);
    }

    private void StartProfessionalHidPulse(string axis)
    {
        string summary = professionalHidAuditController.StartPulse(
            axis,
            8192,
            TimeSpan.FromSeconds(2),
            Stopwatch.GetTimestamp());
        selectedProfessionalHidAuditMode = ProfessionalHidAuditMode.ForceFinalGyroSyntheticPulse.ToString();
        OnPropertyChanged(nameof(SelectedProfessionalHidAuditMode));
        ProfessionalHidAuditStatusText = "HID Audit: synthetic pulse " + axis + " +8192 raw for 2s · " + summary;
        AppendLog("[PRO_IMU_AUDIT] synthetic pulse start axis=" + axis +
                  " raw=8192 duration_seconds=2 " + summary);
    }

    private async Task PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            string response = await PingCoreAsync(cancellationToken);
            Status = "VIIPER server 已响应。";
            AppendLog("[PING] " + response);
        }
        catch (Exception ex)
        {
            Status = "VIIPER server 未就绪：" + FirstLine(ex.Message);
            AppendLog("ERROR ping: " + ex);
        }
    }

    private async Task<string> PingCoreAsync(CancellationToken cancellationToken)
    {
        return await PingCoreAsync(ParsePort(), cancellationToken);
    }

    private async Task<string> PingCoreAsync(int apiPort, CancellationToken cancellationToken)
    {
        var client = new ViiperProtocolClient(Host, apiPort);
        return await client.PingAsync(cancellationToken);
    }

    private async Task StartLocalViiperServerAsync(CancellationToken cancellationToken)
    {
        int requestedApiPort = ParsePort();
        if (!IsLoopbackHost(Host))
        {
            throw new InvalidOperationException("启动本地 VIIPER 时 Host 必须是 localhost、127.0.0.1 或 ::1。");
        }

        DisposeExitedViiperProcess();
        if (viiperProcess is { HasExited: false })
        {
            if (localViiperApiPort.HasValue && localViiperApiPort.Value != requestedApiPort)
            {
                Port = localViiperApiPort.Value.ToString();
                requestedApiPort = localViiperApiPort.Value;
                AppendLog("[VIIPER_SERVER] restored active local API port " + requestedApiPort + ".");
            }
            Status = "本地 VIIPER server 已在运行，pid=" + viiperProcess.Id;
            AppendLog("[VIIPER_SERVER] already_running pid=" + viiperProcess.Id);
            string response = await PingCoreAsync(cancellationToken);
            AppendLog("[PING] " + response);
            return;
        }

        string? exe = FindLocalViiperExe();
        if (exe == null)
        {
            Status = "没有找到本地 VIIPER server，且内置 runtime 释放失败。请确认 EXE 完整。";
            AppendLog("ERROR viiper server: local runtime not found and embedded extraction failed.");
            return;
        }

        UsbipRuntime? usbip = UsbipRuntimeLocator.Find();
        if (usbip == null)
        {
            UsbipInstaller? installer = UsbipRuntimeLocator.FindBundledInstaller();
            Status = installer == null
                ? "未找到 usbip-win2 的 usbip.exe，也没有找到随包安装器。请确认发布包完整。" + UsbipPortableInstallHint
                : "未找到可用的 usbip-win2。请点击“安装/修复 usbip-win2”，使用 EXE 内置的正式安装器完成安装。" + UsbipPortableInstallHint;
            AppendLog("[USBIP] usbip.exe not found. VIIPER can answer ping without it, but all three modes will fail during auto-attach.");
            if (installer != null)
            {
                AppendLog("[USBIP] bundled installer available: " + installer.InstallerPath);
            }
            else
            {
                AppendLog("[USBIP] bundled installer missing. Keep usbip-win2 next to the EXE under usbip-win2\\" + UsbipRuntimeLocator.BundledVersion + ".");
            }
            RefreshRuntimeReadiness();
            throw new InvalidOperationException(Status);
        }
        UsbipProbeResult usbipProbe = await UsbipRuntimeLocator.ProbeAsync(
            usbip,
            cancellationToken);
        if (!usbipProbe.Ready)
        {
            Status = "已找到 usbip.exe，但 USBIP 内核驱动尚未就绪。若刚安装完成，通常需要重启 Windows；重启后仍失败再点击“安装/修复 USBIP”。";
            AppendLog("[USBIP] driver probe failed: " + usbipProbe.Detail);
            throw new InvalidOperationException(Status);
        }

        string logRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "v6_logs");
        Directory.CreateDirectory(logRoot);

        IReadOnlyList<ViiperPortPair> candidates = BuildViiperPortCandidates(requestedApiPort);
        var failures = new List<string>();
        AppendLog("[VIIPER_PREFLIGHT] requested_api=127.0.0.1:" + requestedApiPort +
                  " candidates=" + string.Join(",", candidates.Select(c => c.ApiPort + "/" + c.UsbPort)));
        foreach (ViiperPortPair candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool portsReady = await PrepareViiperPortCandidateAsync(
                candidate,
                failures,
                cancellationToken);
            if (!portsReady)
            {
                continue;
            }

            ViiperStartupFailure? failure = await TryStartLocalViiperServerAsync(
                exe,
                usbip,
                candidate,
                logRoot,
                cancellationToken);
            if (failure == null)
            {
                return;
            }

            failures.Add(failure.Summary);
            if (failure.PortConflict)
            {
                AppendLog("[VIIPER_PREFLIGHT] retrying alternate port pair after port conflict. failed_api=" +
                          candidate.ApiPort + " failed_usb=" + candidate.UsbPort);
                continue;
            }

            throw new InvalidOperationException(failure.UserMessage);
        }

        throw new InvalidOperationException(
            "VIIPER server 启动失败：所有候选端口都不可用或启动失败。诊断：" +
            string.Join(" | ", failures.Take(8)));
    }

    private async Task<bool> PrepareViiperPortCandidateAsync(
        ViiperPortPair candidate,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        bool apiFree = IsLoopbackTcpPortAvailable(candidate.ApiPort, out string apiDetail);
        bool usbFree = IsLoopbackTcpPortAvailable(candidate.UsbPort, out string usbDetail);
        AppendLog("[VIIPER_PREFLIGHT] candidate source=" + candidate.Source +
                  " api=127.0.0.1:" + candidate.ApiPort + " " + apiDetail +
                  " usb=127.0.0.1:" + candidate.UsbPort + " " + usbDetail);

        if (!apiFree)
        {
            string? ping = await TryPingViiperOnPortAsync(
                candidate.ApiPort,
                TimeSpan.FromMilliseconds(850),
                cancellationToken);
            if (ping != null && LooksLikeViiperPing(ping))
            {
                AppendLog("[VIIPER_PREFLIGHT] api port " + candidate.ApiPort +
                          " is occupied by a VIIPER server; cleaning stale process before retry. " + ping);
                AppendLog(StartupProcessGuard.CleanupConflictingProcesses());
                await Task.Delay(900, cancellationToken);
                apiFree = IsLoopbackTcpPortAvailable(candidate.ApiPort, out apiDetail);
                usbFree = IsLoopbackTcpPortAvailable(candidate.UsbPort, out usbDetail);
                AppendLog("[VIIPER_PREFLIGHT] after cleanup api=127.0.0.1:" + candidate.ApiPort + " " + apiDetail +
                          " usb=127.0.0.1:" + candidate.UsbPort + " " + usbDetail);
            }
        }

        if (!apiFree)
        {
            string summary = "api " + candidate.ApiPort + " unavailable: " + apiDetail;
            failures.Add(summary);
            AppendLog("[VIIPER_PREFLIGHT] " + summary + " auto_fallback=1");
            return false;
        }

        if (!usbFree)
        {
            string summary = "usb " + candidate.UsbPort + " unavailable: " + usbDetail;
            failures.Add(summary);
            AppendLog("[VIIPER_PREFLIGHT] " + summary + " auto_fallback=1");
            return false;
        }

        return true;
    }

    private async Task<ViiperStartupFailure?> TryStartLocalViiperServerAsync(
        string exe,
        UsbipRuntime usbip,
        ViiperPortPair ports,
        string logRoot,
        CancellationToken cancellationToken)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string logPath = Path.Combine(
            logRoot,
            "viiper_server_" + stamp + "_api" + ports.ApiPort + "_usb" + ports.UsbPort + ".log");
        string args = "server --api.addr=127.0.0.1:" + ports.ApiPort +
                      " --usb.addr=127.0.0.1:" + ports.UsbPort +
                      " --api.device-handler-connect-timeout=60s" +
                      " --usb.write-batch-flush-interval=0ms" +
                      " --update-notify=none" +
                      " --log.level=info" +
                      " --log.file=\"" + logPath + "\"";
        ProcessStartInfo startInfo = new()
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["PATH"] = UsbipRuntimeLocator.BuildPathWithUsbipDirectory(
            startInfo.Environment.TryGetValue("PATH", out string? path)
                ? path ?? ""
                : Environment.GetEnvironmentVariable("PATH") ?? "",
            usbip);
        startInfo.WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;

        ProcessOutputTail processOutput = new(80);
        Process? started = Process.Start(startInfo);
        if (started == null)
        {
            return new ViiperStartupFailure(
                false,
                "process_start returned null",
                "无法启动 VIIPER server：Process.Start 返回空进程。");
        }

        processOutput.Attach(started);
        viiperProcess = started;
        localViiperApiPort = ports.ApiPort;
        Port = ports.ApiPort.ToString();

        Status = "正在启动本地 VIIPER server，pid=" + started.Id;
        AppendLog("[USBIP] using " + usbip.ExePath);
        AppendLog("[VIIPER_SERVER] launch pid=" + started.Id +
                  " api=127.0.0.1:" + ports.ApiPort +
                  " usb=127.0.0.1:" + ports.UsbPort +
                  " exe=" + exe +
                  " args=" + args +
                  " log=" + logPath);

        ViiperStartupFailure? failure = await WaitForViiperStartupAsync(
            started,
            ports,
            logPath,
            processOutput,
            cancellationToken);
        if (failure != null)
        {
            await CleanupFailedViiperProcessAsync(started);
            if (ReferenceEquals(viiperProcess, started))
            {
                viiperProcess = null;
                localViiperApiPort = null;
            }
            return failure;
        }

        return null;
    }

    private async Task<ViiperStartupFailure?> WaitForViiperStartupAsync(
        Process process,
        ViiperPortPair ports,
        string logPath,
        ProcessOutputTail processOutput,
        CancellationToken cancellationToken)
    {
        string lastPingError = "";
        for (int attempt = 1; attempt <= 20; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            if (process.HasExited)
            {
                return BuildViiperStartupFailure(
                    process,
                    ports,
                    logPath,
                    processOutput,
                    "early_exit",
                    lastPingError);
            }

            using var pingTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pingTimeout.CancelAfter(TimeSpan.FromMilliseconds(650));
            try
            {
                string ping = await PingCoreAsync(ports.ApiPort, pingTimeout.Token);
                Status = "本地 VIIPER server 已就绪，pid=" + process.Id + "。";
                AppendLog("[PING] " + ping);
                if (ports.ApiPort != 3242)
                {
                    AppendLog("[VIIPER_SERVER] using alternate ports api=127.0.0.1:" +
                              ports.ApiPort + " usb=127.0.0.1:" + ports.UsbPort +
                              " reason=preferred_port_unavailable_or_failed");
                }
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastPingError = "ping timeout attempt=" + attempt;
            }
            catch (Exception ex)
            {
                lastPingError = FirstLine(ex.Message);
            }
        }

        return BuildViiperStartupFailure(
            process,
            ports,
            logPath,
            processOutput,
            "api_ping_timeout",
            lastPingError);
    }

    private ViiperStartupFailure BuildViiperStartupFailure(
        Process process,
        ViiperPortPair ports,
        string logPath,
        ProcessOutputTail processOutput,
        string stage,
        string lastPingError)
    {
        int? exitCode = null;
        if (process.HasExited)
        {
            exitCode = process.ExitCode;
        }

        string logTail = ReadTailText(logPath, 40);
        string processTail = processOutput.Snapshot();
        string combined = logTail + "\n" + processTail + "\n" + lastPingError;
        bool portConflict = LooksLikePortConflict(combined);
        string category = portConflict
            ? "port_conflict"
            : LooksLikeUsbipDriverIssue(combined)
                ? "usbip_driver_or_permission"
                : stage == "api_ping_timeout"
                    ? "api_unreachable"
                    : "early_exit_unknown";
        string exitText = exitCode.HasValue ? exitCode.Value.ToString() : "running";
        AppendLog("[VIIPER_DIAG] category=" + category +
                  " stage=" + stage +
                  " exit=" + exitText +
                  " api=127.0.0.1:" + ports.ApiPort +
                  " usb=127.0.0.1:" + ports.UsbPort +
                  " last_ping=\"" + SanitizeForLog(lastPingError) + "\"" +
                  " log=" + logPath);
        if (!string.IsNullOrWhiteSpace(logTail))
        {
            AppendLog("[VIIPER_LOG_TAIL]\n" + logTail);
        }
        if (!string.IsNullOrWhiteSpace(processTail))
        {
            AppendLog("[VIIPER_PROCESS_TAIL]\n" + processTail);
        }

        string hint = category switch
        {
            "port_conflict" =>
                "端口被占用、保留或权限拒绝。程序会自动尝试下一组 API/USBIP 端口；若全部失败，请关闭旧 VIIPER/USBIP/占用端口程序或重启 Windows。",
            "usbip_driver_or_permission" =>
                "更像 USBIP 驱动、权限或安全软件问题。请安装/修复 usbip-win2；若刚安装完成，请重启 Windows 后再试。",
            "api_unreachable" =>
                "VIIPER 进程未退出，但 API 端口一直无法响应。程序已清理该进程，避免留下半启动状态。",
            _ =>
                "VIIPER 在写出明确错误前退出。EXE 已回灌 viiper_server 日志尾部和 stdout/stderr 尾部，请按上方 category 与 tail 定位。"
        };
        string summary = "category=" + category +
                         " exit=" + exitText +
                         " api=" + ports.ApiPort +
                         " usb=" + ports.UsbPort +
                         (string.IsNullOrWhiteSpace(lastPingError) ? "" : " last_ping=" + lastPingError);
        string userMessage =
            "VIIPER server 启动失败：" + summary + "。日志：" + logPath + "。" + hint;
        return new ViiperStartupFailure(portConflict, summary, userMessage);
    }

    private async Task CleanupFailedViiperProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog("[VIIPER_DIAG] failed startup cleanup warning: " + ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task InstallUsbipAsync(CancellationToken cancellationToken)
    {
        UsbipInstaller? installer = UsbipRuntimeLocator.FindBundledInstaller();
        if (installer == null)
        {
            Status = "没有找到随包 usbip-win2 安装器。请确认发布包完整。";
            AppendLog("[USBIP] bundled installer not found: " + UsbipRuntimeLocator.InstallerFileName);
            return;
        }

        UsbipRuntime? existing = UsbipRuntimeLocator.Find();
        if (existing != null)
        {
            AppendLog("[USBIP] existing usbip.exe found: " + existing.ExePath);
            AppendLog("[USBIP] launching bundled installer anyway for repair/update.");
        }

        Status = "正在启动 usbip-win2 安装器，请在 UAC/安装向导中确认。安装期间 USB 设备可能会短暂重启。";
        AppendLog("[USBIP] installer=" + installer.InstallerPath);
        if (!string.IsNullOrWhiteSpace(installer.LicensePath))
        {
            AppendLog("[USBIP] license=" + installer.LicensePath);
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = installer.InstallerPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(installer.InstallerPath) ?? AppContext.BaseDirectory
            });

            if (process == null)
            {
                Status = "未能启动 usbip-win2 安装器。";
                AppendLog("[USBIP] installer process returned null.");
                return;
            }

            await process.WaitForExitAsync(cancellationToken);
            int installerExitCode = process.ExitCode;
            AppendLog("[USBIP] installer exited code=" + installerExitCode);
            if (installerExitCode is 1641 or 3010)
            {
                Status = "USBIP 驱动安装成功，但 Windows 要求重启。请重启后再启动角色，不要反复重装。";
                AppendLog("[USBIP] reboot_required=1 installer_exit=" + installerExitCode);
            }
            else if (installerExitCode != 0)
            {
                Status = "USBIP 安装器返回错误 exit=" + installerExitCode + "。请查看安装向导提示，或再次点击“安装/修复 USBIP”。";
                AppendLog("[USBIP] installer_failed exit=" + installerExitCode);
            }
            await Task.Delay(1000, cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Status = "已取消 usbip-win2 安装。";
            AppendLog("[USBIP] installer cancelled by user.");
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Status = "启动 usbip-win2 安装器失败：" + FirstLine(ex.Message);
            AppendLog("ERROR usbip installer: " + ex);
            return;
        }

        UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
        if (runtime != null)
        {
            UsbipProbeResult probe = await UsbipRuntimeLocator.ProbeAsync(
                runtime,
                cancellationToken);
            if (probe.Ready)
            {
                Status = "usbip-win2 已就绪：" + runtime.ExePath;
                AppendLog("[USBIP] ready " + runtime.ExePath + " / " + probe.Detail);
            }
            else
            {
                Status = "安装器已结束，但 USBIP 内核驱动检测未通过。大概率是安装后尚未重启；请重启 Windows 后再打开本 EXE，避免反复安装。";
                AppendLog("[USBIP] driver probe failed after installer: " + probe.Detail);
                AppendLog("[USBIP] reboot_required_suspected=1 reason=probe_failed_after_installer");
            }
        }
        else
        {
            Status = "usbip-win2 安装器已结束，但还没找到 usbip.exe。若安装器提示重启，请重启后再打开本 EXE。" + UsbipPortableInstallHint;
            AppendLog("[USBIP] usbip.exe still not found after installer exit; reboot may be required.");
        }
        await RefreshRuntimeReadinessAsync(cancellationToken);
    }

    private async Task ScanPro2InputAsync(CancellationToken cancellationToken)
    {
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            Status = "正在扫描 Pro2 BLE，窗口仍可正常响应，请等待约 8 秒。";
            InputStatus = "正在扫描未配对的 Pro2 BLE 广播...";
            var progress = new Progress<string>(AppendLog);
            var candidates = await inputSource.ScanAsync(progress, TimeSpan.FromSeconds(8), cancellationToken);
            if (candidates.Count == 0)
            {
                string diagnostic = inputSource.LastScanDiagnostic;
                InputStatus = string.IsNullOrWhiteSpace(diagnostic)
                    ? "没有扫描到真实 Pro2 BLE。唤醒手柄并确保没有被 ESP32、Switch、手机或旧进程占用。"
                    : "没有扫描到真实 Pro2 BLE：" + diagnostic;
                Status = InputStatus;
                AppendLog("[PRO2_BLE] scan none");
                return;
            }

            InputStatus = "发现 " + candidates.Count + " 个 Pro2 BLE 候选。";
            foreach (string candidate in candidates)
            {
                AppendLog("[PRO2_BLE] candidate " + candidate);
            }
        }
        finally
        {
            bleOperationGate.Release();
        }
    }

    public async Task RunStartupAutomationAsync()
    {
        if (shuttingDown || startupAutomationRan)
        {
            return;
        }

        startupAutomationRan = true;
        if (!AutoReconnectOnStartupEnabled)
        {
            return;
        }

        startupAutomationActive = true;
        startupAutomationPaused = false;
        startupAutomationNoticeSent = false;
        startupAutomationDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        OnPropertyChanged(nameof(StartupAutomationSummary));
        AppendLog("[STARTUP_AUTO] begin mode=" + ModeKey(selectedMode) +
                  " label=\"" + SelectedModeLabel + "\" deadline_seconds=300");
        RequestUserNotification(
            "新和联胜自动启动",
            "已自动切换至 " + SelectedModeLabel +
            "，并开始检索上次的 Pro2 手柄。若 5 分钟内没有连上，会暂停并等待人工操作。");

        await RunExclusiveAsync("启动自动进入游戏", EnterGameAsync, isStartupAutomationOperation: true);
    }

    private async Task TryConnectPro2InputOnceAsync(CancellationToken cancellationToken)
    {
        Status = "正在扫描并连接 Pro2 BLE；将依次验证 GATT、初始化命令和 live 输入。";
        InputStatus = "正在连接 Pro2 BLE...";
        var progress = new Progress<string>(AppendLog);
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            Pro2ControllerSlot primarySlot = pro2Slots[0];
            IReadOnlySet<string> preferred = PreferredAddressSetForSlot(primarySlot);
            bool onlyPreferred = startupAutomationActive && preferred.Count > 0;
            await inputSource.StartAsync(
                progress,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                preferred,
                onlyPreferred,
                ResolveStickCalibration,
                cancellationToken);
        }
        finally
        {
            bleOperationGate.Release();
        }
        InputStatus = inputSource.Status;
        if (inputSource.IsRunning)
        {
            pro2Slots[0].RefreshFromSource();
            RememberConnectedAddress(pro2Slots[0]);
            MarkStartupAutomationConnected("primary");
            Status = inputSource.IsPerformanceDegraded
                ? "真实 Pro2 BLE 已接入并保持可用，但当前通知速率低于 66.7 Hz 目标；不会再强制断开。"
                : Running
                    ? "真实 Pro2 BLE 已接入，当前虚拟模式将自动切换为 live 输入和 rumble 写回。"
                    : "真实 Pro2 BLE 已连接，可以启动任一虚拟模式。";
        }
        else
        {
            Status = "真实 Pro2 BLE 未连接。请唤醒手柄，并确认没有被其他设备占用。";
        }
        RaiseConnectionStateChanged();
    }

    private async Task DisconnectPro2InputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StopAutoReconnectAsync();
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            await inputSource.StopAsync();
        }
        finally
        {
            bleOperationGate.Release();
        }
        InputStatus = inputSource.Status;
        RaiseConnectionStateChanged();
        if (Running)
        {
            Status = "已停止自动重连并断开真实 Pro2；虚拟设备继续运行 neutral 输入。再次点击“进入游戏”即可恢复持续连接。";
        }
    }

    private async Task CalibrateSourceGyroAsync(CancellationToken cancellationToken)
    {
        Pro2BleInputSource[] liveSources = pro2Slots
            .Where(slot => slot.InputSource.IsRunning)
            .Select(slot => slot.InputSource)
            .Distinct()
            .ToArray();
        if (liveSources.Length == 0)
        {
            Status = "没有已连接的真实 Pro2，无法校准陀螺仪。";
            AppendLog("[PRO2_GYRO_CAL] rejected=no_live_source");
            return;
        }

        foreach (Pro2BleInputSource source in liveSources)
        {
            AppendLog("[PRO2_GYRO_CAL] " + source.StartManualGyroCalibration());
        }
        Status = "正在校准真实 Pro2 陀螺仪，请将手柄静置三秒。按键和摇杆仍保持连接。";
        InputStatus = "陀螺仪零偏校准中，请勿移动手柄...";
        await Task.Delay(TimeSpan.FromSeconds(3.5), cancellationToken);

        string[] summaries = liveSources
            .Select((source, index) => "slot=" + (index + 1) + " " + source.GyroCalibrationSummary)
            .ToArray();
        foreach (string summary in summaries)
        {
            AppendLog("[PRO2_GYRO_CAL] " + summary);
        }
        bool complete = summaries.All(summary => summary.Contains("status=calibrated", StringComparison.Ordinal));
        Status = complete
            ? "真实 Pro2 陀螺仪零偏校准完成。未使用死区或滤波。"
            : "陀螺仪校准尚未提交，检测到移动；请保持静置后重试。";
        InputStatus = inputSource.Status + " · " + inputSource.GyroCalibrationSummary;
    }

    private async Task EnterGameAsync(CancellationToken cancellationToken)
    {
        if (!Running || activeMode != selectedMode)
        {
            await SwitchModeAsync(SelectedProfile, cancellationToken);
        }

        if (!Running)
        {
            Status = "当前虚拟模式启动失败，尚未进入自动连接。请根据上方提示修复 USBIP/VIIPER 后重试。";
            return;
        }

        if (activeMode.HasValue && IsMultiSlotMode(activeMode.Value))
        {
            await StartEnabledSlotsAutoReconnectAsync(cancellationToken);
        }
        else
        {
            StartAutoReconnect();
        }
    }

    private async Task SwitchModeAsync(
        ViiperDeviceProfile profile,
        CancellationToken cancellationToken)
    {
        SelectMode(profile.Mode);
        if (Running && activeMode == profile.Mode)
        {
            Status = profile.Label + " 已经是当前角色。点击“连接 PRO2 · 进入游戏”接入真实手柄。";
            return;
        }

        bool resumePrimaryAutoReconnect = AutoReconnectEnabled && !IsMultiSlotMode(profile.Mode);
        bool resumeSlotAutoReconnect =
            IsMultiSlotMode(profile.Mode) &&
            (AutoReconnectEnabled || pro2Slots.Any(slot => slot.AutoReconnectEnabled));
        if (AutoReconnectEnabled || pro2Slots.Any(slot => slot.AutoReconnectEnabled))
        {
            AppendLog("[MODE_SWITCH] pausing Pro2 auto reconnect before virtual USB rebuild.");
            await StopAutoReconnectAsync();
        }

        await EnsureViiperReadyAsync(cancellationToken);
        if (Running)
        {
            AppendLog("[MODE_SWITCH] from=" + ActiveProfile.Label + " to=" + profile.Label);
            await StopSessionAsync(updateStatus: false);
            await Task.Delay(650, cancellationToken);
        }

        await CleanupVirtualDeviceResidueAsync(
            "before_start_" + profile.Mode,
            cancellationToken,
            includePnpDump: false);

        await StartAsync(profile, cancellationToken);
        if (resumeSlotAutoReconnect && Running && activeMode == profile.Mode)
        {
            AppendLog("[MODE_SWITCH] resuming multi-slot auto reconnect after virtual USB rebuild.");
            await StartEnabledSlotsAutoReconnectAsync(cancellationToken);
        }
        else if (resumePrimaryAutoReconnect && Running && activeMode == profile.Mode)
        {
            AppendLog("[MODE_SWITCH] resuming Pro2 auto reconnect after virtual USB rebuild.");
            StartAutoReconnect();
        }
    }

    private async Task EnsureViiperReadyAsync(CancellationToken cancellationToken)
    {
        if (IsLoopbackHost(Host))
        {
            UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
            UsbipProbeResult? probe = runtime == null
                ? null
                : await UsbipRuntimeLocator.ProbeAsync(runtime, cancellationToken);
            if (runtime == null)
            {
                Status = "首次运行需要安装 USBIP 内核驱动，正在打开内置安装向导。完成后将继续启动所选角色。";
                AppendLog("[FIRST_RUN] usbip preflight failed: usbip.exe not found" +
                          "; launching embedded installer.");
                await InstallUsbipAsync(cancellationToken);
                runtime = UsbipRuntimeLocator.Find();
                if (runtime == null)
                {
                    throw new InvalidOperationException(
                        "USBIP 驱动尚未就绪。请完成 EXE 内置的正式安装向导；若安装器要求重启，请重启 Windows 后再次选择角色。" +
                        UsbipPortableInstallHint);
                }

                probe = await UsbipRuntimeLocator.ProbeAsync(runtime, cancellationToken);
                if (!probe.Ready)
                {
                    throw new InvalidOperationException(
                        "USBIP 内核驱动检测未通过：" + probe.Detail +
                        "。若刚完成安装，请重启 Windows 后再次选择角色。");
                }
            }
            else if (probe is not { Ready: true })
            {
                Status = "USBIP 程序已安装，但内核驱动未就绪。若刚安装/修复过，请重启 Windows；重启后仍失败再点“安装/修复 USBIP”。";
                AppendLog("[FIRST_RUN] usbip.exe found but driver probe failed; not relaunching installer automatically. detail=" +
                          (probe?.Detail ?? "unknown"));
                throw new InvalidOperationException(
                    "USBIP 已安装但驱动未就绪：" + (probe?.Detail ?? "unknown") +
                    "。通常是刚安装后尚未重启 Windows；请重启后再打开本 EXE。");
            }
        }

        try
        {
            await PingCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (IsLoopbackHost(Host))
        {
            await StartLocalViiperServerAsync(cancellationToken);
            await PingCoreAsync(cancellationToken);
        }
    }

    private void StartAutoReconnect()
    {
        if (autoReconnectTask is { IsCompleted: false })
        {
            Status = IsInputConnected
                ? "Pro2 已连接，自动重连守护正在运行。"
                : "自动连接已在持续寻找 Pro2，请保持手柄唤醒。";
            return;
        }

        autoReconnectCts?.Dispose();
        autoReconnectCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
        AutoReconnectEnabled = true;
        InputStatus = "自动连接已启动：将持续扫描最匹配的 Pro2，并在断联后自动恢复。";
        Status = "已进入游戏连接流程。无需重复点击；唤醒 Pro2 后程序会持续尝试，断联也会自动重连。";
        AppendLog("[PRO2_AUTO] enabled stale_timeout_ms=" + LostInputTimeoutMilliseconds);
        autoReconnectTask = AutoReconnectLoopAsync(autoReconnectCts.Token);
    }

    private async Task AutoReconnectLoopAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;
        int failedAttemptStreak = 0;
        bool previouslyLive = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (inputSource.TryConsumeDisconnectSignal(
                        out Pro2BleDisconnectSignal lowLevelDisconnect))
                {
                    previouslyLive = false;
                    NotifyPro2Offline(
                        slotIndex: 1,
                        lowLevelDisconnect,
                        autoReconnectContinues: true);
                    InputStatus = "Windows 已确认 Pro2 离线，正在清理连接并自动重连...";
                    await bleOperationGate.WaitAsync(cancellationToken);
                    try
                    {
                        await inputSource.StopAsync();
                    }
                    finally
                    {
                        bleOperationGate.Release();
                    }
                    RaiseConnectionStateChanged();
                }

                bool live = inputSource.IsRunning &&
                            inputSource.TryGetLatest(out _, out TimeSpan age) &&
                            age <= TimeSpan.FromMilliseconds(LostInputTimeoutMilliseconds);
                if (live)
                {
                    failedAttemptStreak = 0;
                    pro2Slots[0].RefreshFromSource();
                    RememberConnectedAddress(pro2Slots[0]);
                    MarkStartupAutomationConnected("primary_loop");
                    if (!previouslyLive)
                    {
                        previouslyLive = true;
                        InputStatus = inputSource.Status;
                        Status = "真实 Pro2 BLE 已连接。自动守护会在意外断联后继续寻找并恢复连接。";
                        AppendLog("[PRO2_AUTO] live; reconnect guard active.");
                        RaiseConnectionStateChanged();
                    }

                    await Task.Delay(750, cancellationToken);
                    continue;
                }

                if (TryPauseStartupAutomationAfterTimeout("primary_loop", currentSlot: null))
                {
                    break;
                }

                if (inputSource.IsRunning)
                {
                    previouslyLive = false;
                    TimeSpan staleAge = inputSource.TryGetLatest(out _, out TimeSpan measuredAge)
                        ? measuredAge
                        : TimeSpan.MaxValue;
                    NotifyPro2Offline(
                        slotIndex: 1,
                        inputSource.CreateInputTimeoutDisconnectSignal(staleAge),
                        autoReconnectContinues: true);
                    AppendLog("[PRO2_AUTO] input stale age_ms=" +
                              (staleAge == TimeSpan.MaxValue ? "unknown" : staleAge.TotalMilliseconds.ToString("F0")) +
                              "; recycling BLE session.");
                    InputStatus = "检测到 Pro2 输入中断，正在清理旧连接并自动重连...";
                    await bleOperationGate.WaitAsync(cancellationToken);
                    try
                    {
                        await inputSource.StopAsync();
                    }
                    finally
                    {
                        bleOperationGate.Release();
                    }
                    RaiseConnectionStateChanged();
                }

                attempt++;
                previouslyLive = false;
                InputStatus = "自动连接第 " + attempt + " 次：正在扫描最匹配的 Pro2，请保持手柄唤醒...";
                Status = "自动重连持续运行中。没有找到手柄时会等待片刻后继续，无需手动重试。";
                AppendLog("[PRO2_AUTO] attempt=" + attempt + " begin.");
                try
                {
                    await TryConnectPro2InputOnceAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedAttemptStreak++;
                    TimeSpan retryDelay = Pro2ReconnectDelayForAttempt(failedAttemptStreak);
                    AppendLog("[PRO2_AUTO] attempt=" + attempt + " failed: " + ex);
                    InputStatus = "本轮连接异常：" + FirstLine(ex.Message) + "。" +
                                  retryDelay.TotalSeconds.ToString("F1") +
                                  " 秒后自动继续，连续失败会退避以避免手柄进入冷却。";
                    Status = "Pro2 自动重连仍在运行；失败退避中，不会反复猛连手柄。";
                    AppendLog("[PRO2_AUTO] attempt=" + attempt +
                              " retry_delay_ms=" + retryDelay.TotalMilliseconds.ToString("F0") +
                              " failed_streak=" + failedAttemptStreak +
                              " reason=connect_exception cooldown_guard=1.");
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }
                RaiseConnectionStateChanged();

                if (!inputSource.IsRunning)
                {
                    failedAttemptStreak++;
                    TimeSpan retryDelay = Pro2ReconnectDelayForAttempt(failedAttemptStreak);
                    string diagnostic = inputSource.LastScanDiagnostic;
                    InputStatus = string.IsNullOrWhiteSpace(diagnostic)
                        ? "本轮未连接到 Pro2，" + retryDelay.TotalSeconds.ToString("F1") +
                          " 秒后自动继续扫描。可在手动控制区停止自动重连。"
                        : "本轮未连接到 Pro2：" + diagnostic + " " +
                          retryDelay.TotalSeconds.ToString("F1") + " 秒后自动继续扫描。";
                    Status = failedAttemptStreak >= 4
                        ? "自动重连持续运行中。已进入温和退避，避免连续连接触发手柄冷却。"
                        : "自动重连持续运行中。本轮没有找到可用 Pro2，将自动开始下一轮。";
                    AppendLog("[PRO2_AUTO] attempt=" + attempt +
                              " no_live; retry_delay_ms=" + retryDelay.TotalMilliseconds.ToString("F0") +
                              " failed_streak=" + failedAttemptStreak +
                              " cooldown_guard=1.");
                    await Task.Delay(retryDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppendLog("[PRO2_AUTO] cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR Pro2 auto reconnect: " + ex);
            InputStatus = "自动重连遇到异常，将在 3 秒后由用户重新进入游戏恢复：" + FirstLine(ex.Message);
            Status = "Pro2 自动重连异常停止：" + FirstLine(ex.Message);
        }
        finally
        {
            if (!shuttingDown && !cancellationToken.IsCancellationRequested)
            {
                AutoReconnectEnabled = false;
            }
            RaiseConnectionStateChanged();
        }
    }

    private async Task StartEnabledSlotsAutoReconnectAsync(CancellationToken cancellationToken)
    {
        Pro2ControllerSlot[] enabledSlots = pro2Slots.Where(slot => slot.Enabled).ToArray();
        if (enabledSlots.Length == 0)
        {
            pro2Slots[0].Enabled = true;
            enabledSlots = [pro2Slots[0]];
        }

        foreach (Pro2ControllerSlot slot in enabledSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StartPro2SlotAutoReconnectAsync(slot, cancellationToken);
        }

        UpdateMultiSlotInputStatus();
    }

    private async Task StartPro2SlotAutoReconnectAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        if (slot == null)
        {
            return;
        }

        slot.Enabled = true;
        if (!Running || !activeMode.HasValue || !IsMultiSlotMode(activeMode.Value))
        {
            ViiperDeviceProfile targetProfile = IsMultiSlotMode(selectedMode)
                ? SelectedProfile
                : ViiperDeviceProfile.Pro2;
            await SwitchModeAsync(targetProfile, cancellationToken);
        }

        if (!slot.VirtualDeviceRunning)
        {
            slot.Status = "Slot 已启用，但虚拟手柄尚未创建。请重新部署当前模式。";
            UpdateMultiSlotInputStatus();
            return;
        }

        if (slot.AutoReconnectTask is { IsCompleted: false })
        {
            slot.Status = slot.InputSource.IsRunning
                ? "已连接，自动守护正在运行。"
                : "自动连接已在持续寻找 Pro2。";
            UpdateMultiSlotInputStatus();
            return;
        }

        slot.AutoReconnectCts?.Dispose();
        slot.AutoReconnectCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
        slot.AutoReconnectEnabled = true;
        string preferredAddress = LastKnownPro2AddressForSlot(slot);
        slot.Status = string.IsNullOrWhiteSpace(preferredAddress)
            ? "自动连接已启动，正在寻找未被其他 Slot 占用的 Pro2。"
            : "自动连接已启动，优先寻找上次保存的 Pro2：" + preferredAddress;
        AppendLog("[SLOT_MULTI] slot=" + slot.Index + " auto enabled mode=" + activeMode +
                  " preferred=" + (string.IsNullOrWhiteSpace(preferredAddress) ? "none" : preferredAddress) +
                  " startup_auto=" + startupAutomationActive);
        slot.AutoReconnectTask = Pro2SlotAutoReconnectLoopAsync(slot, slot.AutoReconnectCts.Token);
        UpdateMultiSlotInputStatus();
    }

    private async Task Pro2SlotAutoReconnectLoopAsync(
        Pro2ControllerSlot slot,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        int failedAttemptStreak = 0;
        bool previouslyLive = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (slot.InputSource.TryConsumeDisconnectSignal(
                        out Pro2BleDisconnectSignal lowLevelDisconnect))
                {
                    previouslyLive = false;
                    NotifyPro2Offline(
                        slot.Index,
                        lowLevelDisconnect,
                        autoReconnectContinues: true);
                    slot.Status = "Windows 已确认手柄离线，正在清理连接并自动重连。";
                    await bleOperationGate.WaitAsync(cancellationToken);
                    try
                    {
                        await slot.InputSource.StopAsync();
                    }
                    finally
                    {
                        bleOperationGate.Release();
                    }
                    slot.RefreshFromSource();
                    UpdateMultiSlotInputStatus();
                }

                bool live = slot.InputSource.IsRunning &&
                            slot.InputSource.TryGetLatest(out _, out TimeSpan age) &&
                            age <= TimeSpan.FromMilliseconds(LostInputTimeoutMilliseconds);
                if (live)
                {
                    failedAttemptStreak = 0;
                    slot.RefreshFromSource();
                    RememberConnectedAddress(slot);
                    MarkStartupAutomationConnected("slot_" + slot.Index);
                    if (!previouslyLive)
                    {
                        previouslyLive = true;
                        AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                                  " live address=" + slot.ConnectedAddress);
                        UpdateMultiSlotInputStatus();
                    }

                    await Task.Delay(750, cancellationToken);
                    continue;
                }

                if (TryPauseStartupAutomationAfterTimeout("slot_" + slot.Index, slot))
                {
                    break;
                }

                if (slot.InputSource.IsRunning)
                {
                    previouslyLive = false;
                    TimeSpan staleAge = slot.InputSource.TryGetLatest(out _, out TimeSpan measuredAge)
                        ? measuredAge
                        : TimeSpan.MaxValue;
                    NotifyPro2Offline(
                        slot.Index,
                        slot.InputSource.CreateInputTimeoutDisconnectSignal(staleAge),
                        autoReconnectContinues: true);
                    AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                              " input stale age_ms=" +
                              (staleAge == TimeSpan.MaxValue ? "unknown" : staleAge.TotalMilliseconds.ToString("F0")) +
                              "; recycling BLE session.");
                    slot.Status = "输入中断，正在清理旧 BLE 连接并自动重连。";
                    await bleOperationGate.WaitAsync(cancellationToken);
                    try
                    {
                        await slot.InputSource.StopAsync();
                    }
                    finally
                    {
                        bleOperationGate.Release();
                    }
                    slot.RefreshFromSource();
                    UpdateMultiSlotInputStatus();
                }

                attempt++;
                previouslyLive = false;
                slot.Status = "自动连接第 " + attempt + " 次：扫描未被占用的 Pro2。";
                AppendLog("[SLOT_MULTI] slot=" + slot.Index + " attempt=" + attempt + " begin.");
                try
                {
                    await TryConnectPro2SlotOnceAsync(slot, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedAttemptStreak++;
                    TimeSpan retryDelay = Pro2ReconnectDelayForAttempt(failedAttemptStreak);
                    slot.Status = "连接异常：" + FirstLine(ex.Message) +
                                  "，" + retryDelay.TotalSeconds.ToString("F1") + " 秒后重试。";
                    AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                              " attempt=" + attempt +
                              " failed retry_delay_ms=" + retryDelay.TotalMilliseconds.ToString("F0") +
                              " error=" + ex);
                    UpdateMultiSlotInputStatus();
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                slot.RefreshFromSource();
                UpdateMultiSlotInputStatus();
                if (!slot.InputSource.IsRunning)
                {
                    failedAttemptStreak++;
                    TimeSpan retryDelay = Pro2ReconnectDelayForAttempt(failedAttemptStreak);
                    string diagnostic = slot.InputSource.LastScanDiagnostic;
                    slot.Status = string.IsNullOrWhiteSpace(diagnostic)
                        ? "未找到未占用 Pro2，" + retryDelay.TotalSeconds.ToString("F1") + " 秒后继续。"
                        : "未连接：" + diagnostic + " " + retryDelay.TotalSeconds.ToString("F1") + " 秒后继续。";
                    AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                              " attempt=" + attempt +
                              " no_live retry_delay_ms=" + retryDelay.TotalMilliseconds.ToString("F0") +
                              " failed_streak=" + failedAttemptStreak);
                    await Task.Delay(retryDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppendLog("[SLOT_MULTI] slot=" + slot.Index + " auto cancelled.");
        }
        catch (Exception ex)
        {
            slot.Status = "自动重连异常停止：" + FirstLine(ex.Message);
            AppendLog("[SLOT_MULTI] slot=" + slot.Index + " auto failed: " + ex);
        }
        finally
        {
            if (!shuttingDown && !cancellationToken.IsCancellationRequested)
            {
                slot.AutoReconnectEnabled = false;
            }
            slot.RefreshFromSource();
            UpdateMultiSlotInputStatus();
        }
    }

    private async Task TryConnectPro2SlotOnceAsync(
        Pro2ControllerSlot slot,
        CancellationToken cancellationToken)
    {
        var excluded = ConnectedPro2AddressesExcept(slot);
        var progress = new Progress<string>(
            line => AppendLog("[PRO2_SLOT " + slot.Index + "] " + line));
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlySet<string> preferred = PreferredAddressSetForSlot(slot);
            bool onlyPreferred = startupAutomationActive && preferred.Count > 0;
            await slot.InputSource.StartAsync(
                progress,
                excluded,
                preferred,
                onlyPreferred,
                ResolveStickCalibration,
                cancellationToken);
        }
        finally
        {
            bleOperationGate.Release();
        }

        slot.RefreshFromSource();
        if (slot.InputSource.IsRunning)
        {
            RememberConnectedAddress(slot);
            MarkStartupAutomationConnected("slot_connect_" + slot.Index);
        }
    }

    private IReadOnlySet<string> PreferredAddressSetForSlot(Pro2ControllerSlot slot)
    {
        string address = LastKnownPro2AddressForSlot(slot);
        if (string.IsNullOrWhiteSpace(address))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { address };
    }

    private string LastKnownPro2AddressForSlot(Pro2ControllerSlot slot)
    {
        string[] addresses = V60UserSettings.NormalizeAddressSlots(userSettings.LastConnectedPro2Addresses);
        int index = slot.Index - 1;
        return index >= 0 && index < addresses.Length
            ? addresses[index]
            : "";
    }

    private void RememberConnectedAddress(Pro2ControllerSlot slot)
    {
        string address = V60UserSettings.NormalizeBleAddress(slot.InputSource.ConnectedAddress);
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        ApplySavedStickCalibration(slot, address, logChange: true);
        string[] addresses = V60UserSettings.NormalizeAddressSlots(userSettings.LastConnectedPro2Addresses);
        int index = slot.Index - 1;
        if (index < 0 || index >= addresses.Length ||
            string.Equals(addresses[index], address, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        addresses[index] = address;
        userSettings.LastConnectedPro2Addresses = addresses;
        SaveUserSettings("[PRO2_BLE_ADDR]");
        AppendLog("[PRO2_BLE_ADDR] slot=" + slot.Index + " saved=" + address);
    }

    private Pro2StickCalibrationProfile? ResolveStickCalibration(string address)
    {
        return userSettings.TryGetStickCalibration(address, out Pro2StickCalibrationProfile profile)
            ? profile
            : null;
    }

    private void ApplySavedStickCalibration(
        Pro2ControllerSlot slot,
        string address,
        bool logChange)
    {
        string normalizedAddress = V60UserSettings.NormalizeBleAddress(address);
        Pro2StickCalibrationProfile? profile = ResolveStickCalibration(normalizedAddress);
        string calibrationKey = StickCalibrationKey(normalizedAddress, profile);
        bool captureActive = slot.InputSource.IsStickCalibrationCaptureActive;
        if (captureActive)
        {
            return;
        }

        bool alreadyApplied =
            string.Equals(
                slot.AppliedStickCalibrationKey,
                calibrationKey,
                StringComparison.Ordinal);
        string summary;
        if (captureActive || alreadyApplied)
        {
            summary = slot.InputSource.StickCalibrationSummary;
        }
        else if (profile != null || !slot.InputSource.IsRunning)
        {
            summary = slot.InputSource.SetStickCalibration(profile);
            slot.AppliedStickCalibrationKey = calibrationKey;
        }
        else
        {
            summary = slot.InputSource.StickCalibrationSummary;
            slot.AppliedStickCalibrationKey = calibrationKey;
        }
        slot.StickCalibrationStatus = profile == null
            ? "摇杆校准：未保存，使用中心自动学习 + 固定 ±1600 回退。"
            : profile.RangeCalibrated
                ? "摇杆校准：零位 + 完整行程已加载。"
                : "摇杆校准：零位已加载，尚未完成完整行程。";
        if (logChange && !captureActive && !alreadyApplied)
        {
            AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                      " address=" +
                      (normalizedAddress.Length == 0 ? "unknown" : normalizedAddress) +
                      " source=" + (profile == null ? "fixed_1600_fallback" : "saved_profile") +
                      " " + summary);
        }
    }

    private static string StickCalibrationKey(
        string address,
        Pro2StickCalibrationProfile? profile)
    {
        string normalizedAddress = V60UserSettings.NormalizeBleAddress(address);
        return normalizedAddress + "|" +
               (profile == null ? "fallback" : profile.Summary);
    }

    private void MarkStartupAutomationConnected(string context)
    {
        if (!startupAutomationActive)
        {
            return;
        }

        startupAutomationActive = false;
        startupAutomationPaused = false;
        startupAutomationDeadlineUtc = null;
        AppendLog("[STARTUP_AUTO] connected context=" + context + " guard_continues=1");
        OnPropertyChanged(nameof(StartupAutomationSummary));
    }

    private bool TryPauseStartupAutomationAfterTimeout(string context, Pro2ControllerSlot? currentSlot)
    {
        if (!startupAutomationActive || !startupAutomationDeadlineUtc.HasValue ||
            DateTimeOffset.UtcNow < startupAutomationDeadlineUtc.Value)
        {
            return false;
        }

        startupAutomationActive = false;
        startupAutomationPaused = true;
        startupAutomationDeadlineUtc = null;
        AutoReconnectEnabled = false;
        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            slot.AutoReconnectEnabled = false;
            slot.Status = "开机自动连接 5 分钟内未 live，已暂停；请手动操作。";
            if (!ReferenceEquals(slot, currentSlot))
            {
                slot.AutoReconnectCts?.Cancel();
            }
        }
        if (currentSlot != null)
        {
            autoReconnectCts?.Cancel();
        }

        InputStatus = "开机自动连接已暂停：5 分钟内没有连接到 Pro2。";
        Status = "开机自动连接已暂停，避免 Windows BLE 长时间反复建立连接；请唤醒手柄后手动点击进入游戏。";
        AppendLog("[STARTUP_AUTO] paused timeout context=" + context + " deadline_seconds=300");
        if (!startupAutomationNoticeSent)
        {
            startupAutomationNoticeSent = true;
            RequestUserNotification(
                "开机自动连接已暂停",
                "5 分钟内没有连接到 " + SelectedModeLabel +
                " 的 Pro2。已暂停自动检索，避免 Windows BLE 长时间反复建立连接；请手动点击模式或进入游戏恢复。");
        }
        RaiseConnectionStateChanged();
        OnPropertyChanged(nameof(StartupAutomationSummary));
        return true;
    }

    private async Task CancelStartupAutomationForManualOperationAsync(string operation)
    {
        if (!startupAutomationActive && !startupAutomationPaused)
        {
            return;
        }

        startupAutomationActive = false;
        startupAutomationPaused = false;
        startupAutomationDeadlineUtc = null;
        AppendLog("[STARTUP_AUTO] cancelled_by_manual operation=" + operation);
        await StopAutoReconnectAsync();
        Status = "已取消开机自动连接，转为手动操作：" + operation;
        OnPropertyChanged(nameof(StartupAutomationSummary));
    }

    private void RequestUserNotification(string title, string message)
    {
        UserNotificationRequested?.Invoke(title, message);
    }

    private void OnPro2DisconnectDetected(
        int slotIndex,
        Pro2BleDisconnectSignal signal)
    {
        void Notify()
        {
            if (shuttingDown)
            {
                return;
            }

            Pro2ControllerSlot slot = pro2Slots[Math.Clamp(slotIndex - 1, 0, pro2Slots.Length - 1)];
            bool reconnecting = slotIndex == 1
                ? AutoReconnectEnabled || slot.AutoReconnectEnabled
                : slot.AutoReconnectEnabled;
            NotifyPro2Offline(slotIndex, signal, reconnecting);
        }

        if (uiContext != null)
        {
            uiContext.Post(_ => Notify(), null);
        }
        else
        {
            Notify();
        }
    }

    private void NotifyPro2Offline(
        int slotIndex,
        Pro2BleDisconnectSignal signal,
        bool autoReconnectContinues)
    {
        int noticeIndex = Math.Clamp(slotIndex - 1, 0, lastDisconnectNoticeSequence.Length - 1);
        if (signal.ConnectionSequence > 0 &&
            lastDisconnectNoticeSequence[noticeIndex] == signal.ConnectionSequence)
        {
            AppendLog("[PRO2_OFFLINE_NOTICE] duplicate_suppressed=1 slot=" +
                      slotIndex + " " + signal.TelemetryValue);
            return;
        }

        lastDisconnectNoticeSequence[noticeIndex] = signal.ConnectionSequence;
        string severity = signal.IsAbnormal ? "abnormal" : "offline";
        string title = signal.IsAbnormal
            ? "Pro2 异常断联警报"
            : "Pro2 手柄已离线";
        string reason = DescribePro2DisconnectReason(signal);
        string address = string.IsNullOrWhiteSpace(signal.ConnectedAddress)
            ? "未知地址"
            : signal.ConnectedAddress;
        string age = double.IsFinite(signal.LastInputAgeMs)
            ? signal.LastInputAgeMs.ToString("F0") + "ms"
            : "未知";
        string battery = signal.LastBatteryPercent == GamepadState.BatteryUnknown
            ? "电量未知"
            : "最后电量 " + signal.LastBatteryPercent + "%" +
              (signal.LastBatteryCharging ? "（充电中）" : "");
        string followUp = autoReconnectContinues
            ? "程序将继续自动重连。"
            : "自动重连未启用。";

        AppendLog("[PRO2_OFFLINE_NOTICE] slot=" + slotIndex +
                  " severity=" + severity + " " + signal.TelemetryValue +
                  " reason=\"" + reason + "\"");
        RequestUserNotification(
            title,
            "Slot " + slotIndex + " 的 Pro2 已离线。\n" +
            reason + "\n" +
            "地址：" + address + "；最后输入距今：" + age + "；" + battery + "。\n" +
            followUp);
    }

    private static string DescribePro2DisconnectReason(
        Pro2BleDisconnectSignal signal)
    {
        if (signal.Detector == "fd2_input_timeout")
        {
            return "超过 2 秒没有收到 FD2 输入，且 Windows 尚未先报告物理断开；这是疑似异常断流，也可能是手柄关机或没电后系统状态更新延迟。";
        }

        return signal.BluetoothErrorCode switch
        {
            "RadioNotAvailable" =>
                "Windows 报告蓝牙无线电不可用，可能是蓝牙被关闭、USB 蓝牙接收器掉线或驱动异常。",
            "ResourceInUse" =>
                "Windows 报告蓝牙资源被占用，属于异常链路错误。",
            "OtherError" =>
                "Windows GATT 会话报告未分类错误，属于异常断联。",
            "DisabledByPolicy" =>
                "Windows 策略禁用了蓝牙访问。",
            "NotSupported" or "TransportNotSupported" =>
                "当前蓝牙适配器或驱动报告不支持所需传输。",
            "DisabledByUser" =>
                "Windows 报告蓝牙已被用户关闭。",
            "ConsentRequired" =>
                "Windows 报告蓝牙访问需要用户授权。",
            "DeviceNotConnected" =>
                "Windows 确认远端设备已断开，但无法区分手柄关机、没电、离开范围或无线链路丢失。",
            _ =>
                "Windows 确认手柄已离线，但没有提供具体原因；可能是手柄关机、没电、离开范围或无线链路中断。"
        };
    }

    private HashSet<string> ConnectedPro2AddressesExcept(Pro2ControllerSlot slot)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Pro2ControllerSlot other in pro2Slots)
        {
            if (ReferenceEquals(other, slot))
            {
                continue;
            }

            string address = other.InputSource.ConnectedAddress;
            if (!string.IsNullOrWhiteSpace(address) &&
                other.InputSource.IsRunning)
            {
                addresses.Add(address);
            }
        }

        return addresses;
    }

    private async Task ScanPro2SlotAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        if (slot == null)
        {
            return;
        }

        var progress = new Progress<string>(
            line => AppendLog("[PRO2_SLOT " + slot.Index + "] " + line));
        IReadOnlyList<string> found =
            await slot.InputSource.ScanAsync(progress, TimeSpan.FromSeconds(8), cancellationToken);
        slot.Status = found.Count == 0
            ? "扫描完成：未发现 Pro2。"
            : "扫描完成：候选 " + found.Count + " 个，点击连接会自动避开已占用地址。";
        slot.RefreshFromSource();
        UpdateMultiSlotInputStatus();
    }

    private async Task DisconnectPro2SlotAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        if (slot == null)
        {
            return;
        }

        await slot.StopAutoReconnectAsync();
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            await slot.InputSource.StopAsync();
        }
        finally
        {
            bleOperationGate.Release();
        }

        slot.Status = "已断开真实 Pro2；虚拟设备仍保持在线。";
        slot.RefreshFromSource();
        UpdateMultiSlotInputStatus();
    }

    private async Task CalibrateStickCenterSlotAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        if (!TryGetLiveCalibrationTarget(
                slot,
                out Pro2ControllerSlot liveSlot,
                out string address))
        {
            return;
        }

        RequestUserNotification(
            "摇杆零位校准",
            "采样现在开始。请把手柄平放并完全松开左右摇杆，保持两秒，不要触碰摇杆或按键。");
        string started = liveSlot.InputSource.StartManualStickCenterCalibration();
        AppendLog("[PRO2_STICK_CAL] slot=" + liveSlot.Index +
                  " address=" + address + " " + started);
        liveSlot.Status = "摇杆零位校准中：请完全松开左右摇杆，保持两秒。";
        liveSlot.StickCalibrationStatus = "摇杆校准：正在采集零位...";
        Status = liveSlot.Name + " 正在采集摇杆零位。";
        await WaitForStickCalibrationAsync(
            liveSlot,
            "零位",
            2,
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

        Pro2StickCalibrationResult result =
            liveSlot.InputSource.CompleteManualStickCenterCalibration();
        FinishStickCalibration(liveSlot, address, result, "零位");
    }

    private async Task CalibrateStickRangeSlotAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        if (!TryGetLiveCalibrationTarget(
                slot,
                out Pro2ControllerSlot liveSlot,
                out string address))
        {
            return;
        }
        if (!liveSlot.InputSource.StickCalibrationProfile.CenterCalibrated)
        {
            liveSlot.StickCalibrationStatus = "摇杆校准：请先执行“零位 2 秒”。";
            Status = liveSlot.Name + " 尚未完成零位校准，请先点击“零位 2 秒”。";
            AppendLog("[PRO2_STICK_CAL] slot=" + liveSlot.Index +
                      " address=" + address +
                      " status=rejected mode=range reason=center_calibration_required");
            return;
        }

        string started = liveSlot.InputSource.StartManualStickRangeCalibration();
        if (!started.Contains("status=started", StringComparison.Ordinal))
        {
            liveSlot.StickCalibrationStatus = "摇杆校准：完整行程启动失败。";
            Status = "完整行程校准未启动：" + started;
            AppendLog("[PRO2_STICK_CAL] slot=" + liveSlot.Index +
                      " address=" + address + " " + started);
            return;
        }

        RequestUserNotification(
            "摇杆完整行程校准",
            "采样现在开始，共八秒。请把左右摇杆分别贴着外圈缓慢转满至少三圈，确保上、下、左、右都推到底，最后松开摇杆。");
        AppendLog("[PRO2_STICK_CAL] slot=" + liveSlot.Index +
                  " address=" + address + " " + started);
        liveSlot.Status = "完整行程校准中：八秒内将左右摇杆沿外圈各转满至少三圈。";
        liveSlot.StickCalibrationStatus = "摇杆校准：正在采集四轴八方向端点...";
        Status = liveSlot.Name + " 正在采集完整摇杆行程。";
        await WaitForStickCalibrationAsync(
            liveSlot,
            "完整行程",
            8,
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

        Pro2StickCalibrationResult result =
            liveSlot.InputSource.CompleteManualStickRangeCalibration();
        FinishStickCalibration(liveSlot, address, result, "完整行程");
    }

    private async Task WaitForStickCalibrationAsync(
        Pro2ControllerSlot slot,
        string label,
        int seconds,
        CancellationToken cancellationToken)
    {
        for (int remaining = seconds; remaining > 0; remaining--)
        {
            slot.StickCalibrationStatus =
                "摇杆校准：" + label + "采集中，剩余 " + remaining + " 秒...";
            Status = slot.Name + " " + label + "校准采集中，剩余 " + remaining + " 秒。";
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private bool TryGetLiveCalibrationTarget(
        Pro2ControllerSlot? slot,
        out Pro2ControllerSlot liveSlot,
        out string address)
    {
        if (slot == null || !slot.InputSource.IsRunning)
        {
            liveSlot = slot ?? pro2Slots[0];
            address = "";
            Status = "请先连接需要校准的真实 Pro2 手柄。";
            if (slot != null)
            {
                slot.StickCalibrationStatus = "摇杆校准：没有 live 手柄，无法采样。";
                AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                          " status=rejected reason=no_live_source");
            }
            return false;
        }

        address = V60UserSettings.NormalizeBleAddress(slot.InputSource.ConnectedAddress);
        if (address.Length == 0)
        {
            liveSlot = slot;
            Status = "当前 Pro2 没有可保存的 BLE 地址，无法建立独立校准档案。";
            slot.StickCalibrationStatus = "摇杆校准：BLE 地址不可用。";
            AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                      " status=rejected reason=missing_ble_address");
            return false;
        }

        liveSlot = slot;
        return true;
    }

    private void FinishStickCalibration(
        Pro2ControllerSlot slot,
        string expectedAddress,
        Pro2StickCalibrationResult result,
        string label)
    {
        string currentAddress =
            V60UserSettings.NormalizeBleAddress(slot.InputSource.ConnectedAddress);
        bool sameLiveController =
            slot.InputSource.IsRunning &&
            string.Equals(expectedAddress, currentAddress, StringComparison.OrdinalIgnoreCase);
        if (!sameLiveController)
        {
            Pro2StickCalibrationProfile? savedProfile =
                ResolveStickCalibration(currentAddress.Length > 0
                    ? currentAddress
                    : expectedAddress);
            slot.InputSource.SetStickCalibration(savedProfile);
            slot.StickCalibrationStatus = "摇杆校准：手柄在采样过程中离线，未保存。";
            slot.Status = "校准中断：真实 Pro2 已离线或连接对象发生变化。";
            Status = slot.Name + " " + label + "校准中断，旧配置保持不变。";
            AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                      " address=" + expectedAddress +
                      " status=rejected mode=" + label +
                      " reason=controller_changed_or_offline result=\"" +
                      result.Message + "\"");
            RequestUserNotification(
                label + "校准中断",
                "真实 Pro2 在采样期间离线或连接对象发生变化，旧配置没有被覆盖。");
            return;
        }
        if (!result.Success || result.Profile == null)
        {
            slot.StickCalibrationStatus = "摇杆校准：" + label + "采样不完整，旧配置未覆盖。";
            slot.Status = label + "校准失败，请按提示重试。";
            Status = slot.Name + " " + label + "校准未通过，旧配置保持不变。";
            AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                      " address=" + expectedAddress + " " + result.Message);
            RequestUserNotification(
                label + "校准未完成",
                "采样没有通过，旧配置没有被覆盖。\n\n" +
                StickCalibrationFailureText(result.Message));
            return;
        }

        userSettings.SetStickCalibration(expectedAddress, result.Profile);
        SaveUserSettings("[PRO2_STICK_CAL]");
        string applied = slot.InputSource.SetStickCalibration(result.Profile);
        slot.AppliedStickCalibrationKey =
            StickCalibrationKey(expectedAddress, result.Profile);
        slot.StickCalibrationStatus = result.Profile.RangeCalibrated
            ? "摇杆校准：零位 + 完整行程已保存，按当前手柄地址自动加载。"
            : "摇杆校准：零位已保存，请继续执行完整行程校准。";
        slot.Status = label + "校准成功，当前连接已立即应用。";
        Status = slot.Name + " " + label + "校准完成。";
        AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                  " address=" + expectedAddress + " " + result.Message +
                  " apply=\"" + applied + "\"");
        RequestUserNotification(
            label + "校准完成",
            label + "校准已保存并立即应用到当前手柄。\n\n" +
            result.Profile.Summary);
    }

    private static string StickCalibrationFailureText(string result)
    {
        if (result.Contains("not_started", StringComparison.Ordinal))
        {
            return "采样流程被意外中止，请使用修复后的版本重新尝试。";
        }
        if (result.Contains("insufficient_samples", StringComparison.Ordinal))
        {
            return "收到的有效手柄数据不足，请确认手柄保持连接后重试。";
        }
        if (result.Contains("incomplete_directional_travel", StringComparison.Ordinal))
        {
            return "至少一个摇杆方向没有推到底。请让左右摇杆都沿外圈完整转动。";
        }
        if (result.Contains("sticks_moved", StringComparison.Ordinal))
        {
            return "零位采样期间检测到摇杆移动，请完全松开摇杆后重试。";
        }
        return result;
    }

    private void UpdateMultiSlotInputStatus()
    {
        if (!activeMode.HasValue || !IsMultiSlotMode(activeMode.Value))
        {
            RaiseConnectionStateChanged();
            return;
        }

        int virtualCount = pro2Slots.Count(slot => slot.VirtualDeviceRunning);
        int liveCount = pro2Slots.Count(slot => slot.InputSource.IsRunning);
        InputStatus = ActiveProfile.Label + " 多手柄：虚拟实例 " + virtualCount +
                      " 个，真实 Pro2 BLE live " + liveCount +
                      " 个。启用的 Slot 会独立扫描、独立回传震动。";
        Status = InputStatus;
        RaiseConnectionStateChanged();
    }

    private static TimeSpan Pro2ReconnectDelayForAttempt(int failedAttemptStreak)
    {
        if (failedAttemptStreak <= 3)
        {
            return TimeSpan.FromMilliseconds(2500);
        }
        if (failedAttemptStreak <= 6)
        {
            return TimeSpan.FromSeconds(5);
        }
        if (failedAttemptStreak <= 10)
        {
            return TimeSpan.FromSeconds(10);
        }

        return TimeSpan.FromSeconds(30);
    }

    private async Task StopAutoReconnectAsync()
    {
        CancellationTokenSource? reconnectCts = autoReconnectCts;
        Task? reconnectTask = autoReconnectTask;
        autoReconnectCts = null;
        autoReconnectTask = null;
        AutoReconnectEnabled = false;
        if (reconnectCts != null)
        {
            reconnectCts.Cancel();
        }
        if (reconnectTask != null)
        {
            try
            {
                await reconnectTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        reconnectCts?.Dispose();

        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            await slot.StopAutoReconnectAsync();
            slot.RefreshFromSource();
        }
    }

    private async Task StartAsync(ViiperDeviceProfile profile, CancellationToken cancellationToken)
    {
        if (Running)
        {
            return;
        }

        try
        {
            bool inputLive = inputSource.IsRunning &&
                             inputSource.TryGetLatest(out _, out TimeSpan inputAge) &&
                             inputAge <= TimeSpan.FromMilliseconds(500);
            Running = true;
            Status = "正在启动 " + profile.Label + " 虚拟手柄...";
            ViiperPushRateOption pushRate = SelectedPushRateOption;
            ViiperGyroModeOption gyro = ViiperGyroModeOption.Default;
            GyroAxisInversion gyroAxisInversion = default;
            Ps5ImuMappingOption ps5ImuMapping = Ps5ImuMappingOption.Default;
            Ps5OutputImuTuning ps5OutputImuTuning = SelectedPs5OutputImuTuning;
            ProfessionalImuOptions professionalImuOptions = profile.IsProfessionalImuTest
                ? ProfessionalImuOptions.ForTestModes(
                    ps5OutputImuTuning,
                    professionalInvertGyroPitch,
                    professionalInvertGyroYaw,
                    professionalInvertGyroRoll)
                : ProfessionalImuOptions.Default;
            string professionalTelemetry = professionalImuOptions.Enabled
                ? professionalImuOptions.TelemetryValue
                : "disabled";
            ViiperDeviceProfile runtimeProfile = profile with
            {
                SendInterval = pushRate.Interval,
                SourcePaced = pushRate.SourcePaced
            };
            AppendLog("[START] mode=" + runtimeProfile.Label +
                      " type=" + runtimeProfile.DeviceType +
                      " cadence=" + PushCadenceTelemetry(pushRate) +
                      " interval_ms=" + pushRate.Interval.TotalMilliseconds.ToString("F1") +
                      " gyro_mode=" + gyro.Label +
                      " ps5_imu_map=" + ps5ImuMapping.Mapping.TelemetryValue +
                      " ps5_output_imu=" + ps5OutputImuTuning.TelemetryValue +
                      " gyro_axis_inv=" + gyroAxisInversion.TelemetryValue +
                      " professional_imu=" + professionalTelemetry +
                      " backend=" + SelectedBackendOption.Label +
                      " flush=immediate");
            if (runtimeProfile.Mode == ViiperVirtualMode.DualSenseLike ||
                runtimeProfile.Mode == ViiperVirtualMode.DualSenseProfessionalImuTest)
            {
                AppendDualSenseAudioEndpointHint();
            }
            else if (runtimeProfile.Mode == ViiperVirtualMode.DualSenseEdge)
            {
                AppendLog("[EDGE_IDENTITY] type=dualsenseedge vid=0x054c pid=0x0df2 product=\"DualSense Edge Wireless Controller\" paddles=L4/R4 feedback=ordinary_6byte edge_hd_status=blocked_by_viiper_feedback_contract");
            }
            if (runtimeProfile.Mode == ViiperVirtualMode.DualSenseProfessionalImuTest)
            {
                AppendLog("[PRO_IMU] PS5 test mode uses DualSense haptic identity, so ordinary PS5 HD audio-to-rumble path is reused.");
            }
            if (runtimeProfile.Mode == ViiperVirtualMode.XboxProfessionalImuTest)
            {
                AppendLog("[PRO_IMU] Xbox test mode does not claim native gyro; XboxOutputMode=Off by default, IMU is diagnostic CSV/log only.");
            }
            if (runtimeProfile.IsProfessionalImuTest)
            {
                AppendLog("[PRO_IMU] Professional IMU mode entered output_mode=" + runtimeProfile.Label +
                          " sample_handling=" + professionalImuOptions.OutputSampleMode +
                          " source_rate=current_ble" +
                          " virtual_report_rate=" + professionalImuOptions.OutputReportRateMode +
                          " ProjectAccel=+X,+Z,-Y ProjectGyro=+X,+Z,-Y" +
                          " GyroScale=" + ps5OutputImuTuning.GyroScalePitch.ToString("0.###") +
                          "," + ps5OutputImuTuning.GyroScaleYaw.ToString("0.###") +
                          "," + ps5OutputImuTuning.GyroScaleRoll.ToString("0.###") +
                          " DualSenseGyroRawPerDps=" + ProfessionalImuConverter.DualSenseGyroRawPerDps.ToString("0.###") +
                          " output_gyro_invert_pitch=" + professionalInvertGyroPitch.ToString().ToLowerInvariant() +
                          " output_gyro_invert_yaw=" + professionalInvertGyroYaw.ToString().ToLowerInvariant() +
                          " output_gyro_invert_roll=" + professionalInvertGyroRoll.ToString().ToLowerInvariant() +
                          " bias_status=NotCalibrated bias_source=none" +
                          " professional_gyro_uncalibrated_behavior=" + professionalImuOptions.ProfessionalGyroUncalibratedBehavior +
                          " output_gyro_muted_until_calibrated=true" +
                          " integral_state=Disabled integral_running=false" +
                          " hid_audit_builder=" + DualSenseProfessionalHidLayout.BuilderName +
                          " hid_audit_report_id=" + DualSenseProfessionalHidLayout.ReportIdLabel +
                          " hid_audit_report_len=" + DualSenseProfessionalHidLayout.ReportLength +
                          " hid_audit_gyro_offsets=" + DualSenseProfessionalHidLayout.GyroXOffset +
                          "," + DualSenseProfessionalHidLayout.GyroYOffset +
                          "," + DualSenseProfessionalHidLayout.GyroZOffset +
                          " hid_audit_accel_offsets=" + DualSenseProfessionalHidLayout.AccelXOffset +
                          "," + DualSenseProfessionalHidLayout.AccelYOffset +
                          "," + DualSenseProfessionalHidLayout.AccelZOffset +
                          " legacy_ps5_mapper_after_professional=false");
            }
            ResolveExperimentalBackendSelection(runtimeProfile);
            if (IsMultiSlotMode(runtimeProfile.Mode))
            {
                await StartMultiSlotSessionsAsync(runtimeProfile, cancellationToken);
                SetActiveMode(runtimeProfile.Mode);
                int runningSlots = pro2Slots.Count(slot => slot.VirtualDeviceRunning);
                if (runtimeProfile.Mode == ViiperVirtualMode.DualSenseLike &&
                    AudioEndpointGuardEnabled)
                {
                    await ApplyDualSenseAudioGuardAsync(cancellationToken);
                }
                DiagnoseSteamControllerCache(runtimeProfile);
                Status = runtimeProfile.Label + " 多手柄模式已部署 " + runningSlots +
                         " 个独立 VIIPER 实例。点击“连接 Pro2 · 进入游戏”后，每个启用 Slot 会独立寻找真实手柄。";
                return;
            }

            var progress = new Progress<string>(AppendLog);
            ViiperBridgeSession? createdSession = null;
            var faultProgress = new Progress<Exception>(
                ex =>
                {
                    if (createdSession != null)
                    {
                        _ = RecoverFaultedSessionAsync(createdSession, ex);
                    }
                });
            createdSession = new ViiperBridgeSession(
                new ViiperProtocolClient(Host, ParsePort()),
                runtimeProfile,
                progress,
                inputSource,
                inputSource,
                faultProgress,
                gyro.Mode,
                gyroAxisInversion,
                ps5ImuMapping.Mapping,
                ps5OutputImuTuning,
                professionalImuOptions,
                professionalHidAuditController,
                new Progress<ProfessionalImuUiSnapshot>(UpdateProfessionalImuSnapshot));
            session = createdSession;
            await createdSession.StartAsync(cancellationToken);
            SetActiveMode(runtimeProfile.Mode);
            if ((runtimeProfile.Mode == ViiperVirtualMode.DualSenseLike ||
                 runtimeProfile.Mode == ViiperVirtualMode.DualSenseProfessionalImuTest) &&
                AudioEndpointGuardEnabled)
            {
                await ApplyDualSenseAudioGuardAsync(cancellationToken);
            }
            DiagnoseSteamControllerCache(runtimeProfile);
            Status = runtimeProfile.Label + " 虚拟设备已连接。当前输入源：" +
                (inputLive
                    ? "Pro2 BLE live，rumble 写回已启用"
                    : "neutral；稍后点击“连接 Pro2 BLE”即可在不中断虚拟设备的情况下切换为 live 输入") + "。";
        }
        catch (Exception ex)
        {
            string failure = "启动失败：" + ExplainStartFailure(ex);
            AppendLog("ERROR start: " + ex);
            await StopSessionAsync(updateStatus: false);
            await CleanupVirtualDeviceResidueAsync(
                "start_failed_" + profile.Mode,
                cancellationToken,
                includePnpDump: false);
            Status = failure;
        }
    }

    private void ResolveExperimentalBackendSelection(ViiperDeviceProfile profile)
    {
        VirtualBackendOption backend = SelectedBackendOption;
        if (backend.Mode == VirtualBackendMode.ViiperServer)
        {
            AppendLog("[BACKEND] active=viiper_server reason=stable_three_mode_haptic_path");
            return;
        }

        string libPath = FindLibViiperCandidate() ?? "";
        string reason = backend.Mode switch
        {
            VirtualBackendMode.LibViiperExperimental when string.IsNullOrWhiteSpace(libPath) =>
                "libVIIPER.dll not found in app/runtime folders",
            VirtualBackendMode.LibViiperExperimental =>
                "libVIIPER.dll found at " + libPath + " but V6.2 has not bound DualSense/NS2Pro three-mode entrypoints yet",
            VirtualBackendMode.EmbeddedUsbipExperimental =>
                "C# embedded USBIP server is scaffolded as the V6.2 research target, but the release build keeps VIIPER for PS5 HD haptic safety",
            _ => "unknown experimental backend"
        };
        AppendLog("[BACKEND] requested=" + backend.Label +
                  " mode=" + backend.Mode +
                  " profile=" + profile.DeviceType +
                  " active=viiper_server fallback_reason=\"" + reason + "\"");
    }

    private async Task StartMultiSlotSessionsAsync(
        ViiperDeviceProfile runtimeProfile,
        CancellationToken cancellationToken)
    {
        Pro2ControllerSlot[] enabledSlots = pro2Slots.Where(slot => slot.Enabled).ToArray();
        if (enabledSlots.Length == 0)
        {
            pro2Slots[0].Enabled = true;
            enabledSlots = [pro2Slots[0]];
            AppendLog("[SLOT_MULTI] no enabled slot; slot=1 enabled automatically.");
        }

        int started = 0;
        foreach (Pro2ControllerSlot slot in enabledSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            slot.Session = null;
            slot.VirtualDeviceRunning = false;
            slot.Status = "正在创建独立 " + runtimeProfile.Label + " 虚拟设备...";

            ViiperDeviceProfile slotProfile = runtimeProfile with
            {
                Label = runtimeProfile.Label + " Slot " + slot.Index,
                DeviceSpecificSerialNumber =
                    ViiperDeviceProfile.SlotSerialNumber(runtimeProfile.Mode, slot.Index)
            };
            var progress = new Progress<string>(
                line => AppendLog("[SLOT " + slot.Index + "] " + line));
            var faultProgress = new Progress<Exception>(
                ex =>
                {
                    slot.Status = "VIIPER 数据流异常：" + FirstLine(ex.Message);
                    AppendLog("[SLOT " + slot.Index + "] ERROR stream: " + ex);
                });
            var createdSession = new ViiperBridgeSession(
                new ViiperProtocolClient(Host, ParsePort()),
                slotProfile,
                progress,
                slot.InputSource,
                slot.InputSource,
                faultProgress,
                ViiperGyroModeOption.Default.Mode,
                default,
                Ps5ImuMappingOption.Default.Mapping,
                SelectedPs5OutputImuTuning,
                ProfessionalImuOptions.Default,
                professionalHidAuditController,
                new Progress<ProfessionalImuUiSnapshot>(UpdateProfessionalImuSnapshot));

            try
            {
                await createdSession.StartAsync(cancellationToken);
                slot.Session = createdSession;
                slot.VirtualDeviceRunning = true;
                slot.Status = "虚拟 " + runtimeProfile.Label + " 已创建，等待真实手柄连接。";
                slot.RefreshFromSource();
                started++;
                AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                          " virtual_ready=1 device_type=" + slotProfile.DeviceType);
            }
            catch (Exception ex)
            {
                await createdSession.DisposeAsync();
                slot.Session = null;
                slot.VirtualDeviceRunning = false;
                slot.Status = "虚拟设备创建失败：" + FirstLine(ex.Message);
                AppendLog("[SLOT_MULTI] slot=" + slot.Index + " virtual_failed: " + ex);
            }
        }

        foreach (Pro2ControllerSlot slot in pro2Slots.Where(slot => !slot.Enabled))
        {
            slot.VirtualDeviceRunning = false;
            slot.Status = "未启用，不创建 Steam 控制器实例。";
            slot.RefreshFromSource();
        }

        if (started == 0)
        {
            throw new InvalidOperationException(runtimeProfile.Label + " 多手柄模式没有任何 Slot 成功创建虚拟设备。");
        }

        AppendLog("[SLOT_MULTI] mode=" + runtimeProfile.Mode +
                  " virtual_instances=" + started +
                  " enabled_slots=" + string.Join(",", enabledSlots.Select(slot => slot.Index)) +
                  " isolation=per_slot_ble_source_and_rumble_sink");
    }

    private void AppendDualSenseAudioEndpointHint()
    {
        AppendLog("[AUDIO_HINT] PS5 mode exposes a DualSense audio/haptic endpoint for HD vibration. " +
                  "Do not set \"DualSense Wireless Controller\" as the Windows default speaker/headset. " +
                  "If desktop/game audio stutters or disappears, set the default playback device back to the real speakers/headset; " +
                  "the DualSense endpoint should be used by games only for controller haptic audio. The app will now guard defaults automatically.");
    }

    private async Task ApplyDualSenseAudioGuardAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(900, cancellationToken);
            RunAudioEndpointGuard();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog("[AUDIO_GUARD] warning: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private Task FixAudioDefaultsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunAudioEndpointGuard();
        Status = "已检查 Windows 默认播放/通信/麦克风，DualSense 不会作为默认音频设备。";
        return Task.CompletedTask;
    }

    private async Task DumpControllerEnumerationAsync(CancellationToken cancellationToken)
    {
        var client = new ViiperProtocolClient(Host, ParsePort());
        IReadOnlyList<string> lines =
            await ControllerEnumerationDiagnostics.DumpAsync(
                client,
                sessionLog.FilePath,
                cancellationToken);
        foreach (string line in lines)
        {
            AppendLog(line);
        }
        Status = "设备枚举诊断已写入日志。重点看 [VIIPER_DUMP]、[USBIP_PORT]、[PNP_HID]。";
    }

    private async Task CleanupStaleVirtualDevicesAsync(CancellationToken cancellationToken)
    {
        if (!IsLoopbackHost(Host))
        {
            throw new InvalidOperationException("清理残留虚拟设备只允许本地 VIIPER（localhost/127.0.0.1），避免误删远端 USBIP bus。");
        }

        await StopAutoReconnectAsync();
        await StopSessionAsync(updateStatus: false);
        var client = new ViiperProtocolClient(Host, ParsePort());
        IReadOnlyList<string> lines =
            await ControllerEnumerationDiagnostics.CleanupStaleVirtualDevicesAsync(
                client,
                cancellationToken);
        foreach (string line in lines)
        {
            AppendLog(line);
        }
        Status = "虚拟设备已按 USBIP detach → VIIPER remove 顺序清理。Steam 内存中的旧 If_Hid 需使用“刷新 Steam 缓存”完成一次性收尾。";
    }

    private async Task RefreshSteamControllerCacheAsync(CancellationToken cancellationToken)
    {
        SteamControllerCacheAnalysis before = SteamControllerCacheService.AnalyzeRecentIfHid(
            ActiveProfile.ExpectedVid,
            ActiveProfile.ExpectedPid,
            TimeSpan.FromMinutes(30));
        AppendLog("[STEAM_CACHE_REFRESH] preflight steam_running=" + before.SteamRunning.ToString().ToLowerInvariant() +
                  " stale_if_hid=" + before.PotentialStaleIfHid.ToString().ToLowerInvariant() +
                  " detail=" + before.Detail);
        if (!before.SteamRunning)
        {
            Status = "Steam 当前没有运行，不存在进程内控制器缓存需要刷新。";
            return;
        }

        ViiperDeviceProfile restoreProfile = activeMode.HasValue
            ? ProfileFor(activeMode.Value)
            : SelectedProfile;
        bool restorePrimaryAutoReconnect = AutoReconnectEnabled && !IsMultiSlotMode(restoreProfile.Mode);
        bool restoreSlotAutoReconnect =
            IsMultiSlotMode(restoreProfile.Mode) &&
            (AutoReconnectEnabled || pro2Slots.Any(slot => slot.AutoReconnectEnabled));

        Status = "正在正常拔出虚拟手柄并请求 Steam 退出，程序不会强制结束 Steam 进程。";
        await StopAutoReconnectAsync();
        await StopSessionAsync(updateStatus: false);
        await CleanupVirtualDeviceResidueAsync(
            "steam_controller_cache_refresh",
            cancellationToken,
            includePnpDump: false);

        SteamControllerCacheRefreshResult refresh =
            await SteamControllerCacheService.RestartSteamAsync(cancellationToken);
        AppendLog("[STEAM_CACHE_REFRESH] success=" + refresh.Success.ToString().ToLowerInvariant() +
                  " steam_was_running=" + refresh.SteamWasRunning.ToString().ToLowerInvariant() +
                  " detail=" + refresh.Detail +
                  " exe=\"" + (refresh.SteamExePath ?? "") + "\"");
        if (!refresh.Success)
        {
            await RestoreAfterSteamCacheRefreshAsync(
                restoreProfile,
                restorePrimaryAutoReconnect,
                restoreSlotAutoReconnect,
                cancellationToken);
            Status = "Steam 没有在 20 秒内正常退出，程序未强制结束它；当前虚拟手柄已恢复。请先退出正在运行的 Steam 游戏，再重试刷新。";
            return;
        }

        steamGhostNoticeSent = false;
        await Task.Delay(1500, cancellationToken);
        await RestoreAfterSteamCacheRefreshAsync(
            restoreProfile,
            restorePrimaryAutoReconnect,
            restoreSlotAutoReconnect,
            cancellationToken);

        Status = restoreProfile.Label + " 已在新的 Steam 进程中恢复，旧 If_Hid/SDL 槽位不再复用。";
    }

    private async Task RestoreAfterSteamCacheRefreshAsync(
        ViiperDeviceProfile restoreProfile,
        bool restorePrimaryAutoReconnect,
        bool restoreSlotAutoReconnect,
        CancellationToken cancellationToken)
    {
        await EnsureViiperReadyAsync(cancellationToken);
        await StartAsync(restoreProfile, cancellationToken);
        if (restoreSlotAutoReconnect && Running)
        {
            await StartEnabledSlotsAutoReconnectAsync(cancellationToken);
        }
        else if (restorePrimaryAutoReconnect && Running)
        {
            StartAutoReconnect();
        }
    }

    private void DiagnoseSteamControllerCache(ViiperDeviceProfile profile)
    {
        SteamControllerCacheAnalysis analysis = SteamControllerCacheService.AnalyzeRecentIfHid(
            profile.ExpectedVid,
            profile.ExpectedPid,
            TimeSpan.FromMinutes(30));
        AppendLog("[STEAM_CACHE] steam_running=" + analysis.SteamRunning.ToString().ToLowerInvariant() +
                  " stale_if_hid=" + analysis.PotentialStaleIfHid.ToString().ToLowerInvariant() +
                  " detail=" + analysis.Detail +
                  " controller_log=\"" + (analysis.ControllerLogPath ?? "") + "\"");
        if (!analysis.PotentialStaleIfHid || steamGhostNoticeSent)
        {
            return;
        }

        steamGhostNoticeSent = true;
        RequestUserNotification(
            "检测到 Steam 旧控制器槽位",
            "当前虚拟设备身份是 " + profile.ExpectedVid.ToUpperInvariant() + ":" +
            profile.ExpectedPid.ToUpperInvariant() +
            "，但 Steam 仍在显示旧 If_Hid。设备端已经正常切换；可在主界面点击“刷新 Steam 缓存”做一次性清理。");
    }

    private async Task CleanupVirtualDeviceResidueAsync(
        string reason,
        CancellationToken cancellationToken,
        bool includePnpDump)
    {
        if (!IsLoopbackHost(Host))
        {
            AppendLog("[VIRTUAL_DEVICE_GUARD] skipped reason=" + reason + " host=" + Host);
            return;
        }

        try
        {
            AppendLog("[VIRTUAL_DEVICE_GUARD] begin reason=" + reason);
            var client = new ViiperProtocolClient(Host, ParsePort());
            IReadOnlyList<string> lines =
                await ControllerEnumerationDiagnostics.CleanupStaleVirtualDevicesAsync(
                    client,
                    cancellationToken,
                    includePnpDump);
            foreach (string line in lines)
            {
                AppendLog(line);
            }

            await Task.Delay(900, cancellationToken);
            AppendLog("[VIRTUAL_DEVICE_GUARD] complete reason=" + reason + " settle_ms=900");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog("[VIRTUAL_DEVICE_GUARD] warning reason=" + reason + " " +
                      ex.GetType().Name + ": " + FirstLine(ex.Message));
        }
    }

    private async Task ExportDiagnosticsLogAsync(CancellationToken cancellationToken)
    {
        var client = new ViiperProtocolClient(Host, ParsePort());
        IReadOnlyList<string> dump =
            await ControllerEnumerationDiagnostics.DumpAsync(
                client,
                sessionLog.FilePath,
                cancellationToken);
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "v6_logs");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            "diagnostics_v6_2_31_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        var builder = new StringBuilder();
        builder.AppendLine("# V6.2.32 stick-calibration-test diagnostics export");
        builder.AppendLine("# session_log=" + sessionLog.FilePath);
        builder.AppendLine();
        foreach (string line in dump)
        {
            builder.AppendLine(line);
        }
        builder.AppendLine();
        builder.AppendLine("# UI log snapshot");
        builder.Append(LogText);
        await File.WriteAllTextAsync(
            path,
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        AppendLog("[DIAG_EXPORT] path=\"" + path + "\"");
        Status = "诊断包已导出：" + path;
    }

    private void RunAudioEndpointGuard()
    {
        foreach (string line in AudioEndpointGuard.EnsureDualSenseIsNotDefault())
        {
            AppendLog(line);
        }
    }

    private static string? FindLibViiperCandidate()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "libVIIPER.dll"),
            Path.Combine(baseDir, "runtime", "libVIIPER.dll"),
            Path.Combine(baseDir, "tools", "viiper", "libVIIPER.dll"),
            Path.Combine(Environment.CurrentDirectory, "tools", "viiper", "haptic-src", "dist", "libVIIPER", "libVIIPER.dll")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task StopAsync()
    {
        await StopAutoReconnectAsync();
        await StopSessionAsync(updateStatus: true);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await CleanupVirtualDeviceResidueAsync(
            "manual_stop",
            cleanup.Token,
            includePnpDump: false);
    }

    private async Task StopSessionAsync(bool updateStatus)
    {
        await StopMultiSlotSessionsAsync();
        ViiperBridgeSession? active = session;
        session = null;
        if (active != null)
        {
            await active.DisposeAsync();
        }
        Running = false;
        SetActiveMode(null);
        if (updateStatus)
        {
            Status = "已停止虚拟设备。当前角色仍保持选中，可随时重新部署。";
        }
    }

    private async Task StopMultiSlotSessionsAsync()
    {
        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            ViiperBridgeSession? active = slot.Session;
            slot.Session = null;
            if (active != null)
            {
                try
                {
                    await active.DisposeAsync();
                }
                catch (Exception ex)
                {
                    AppendLog("[PRO2_SLOT " + slot.Index + "] session cleanup warning: " + ex.Message);
                }
            }

            slot.VirtualDeviceRunning = false;
            slot.RefreshFromSource();
        }
    }

    private async Task RecoverFaultedSessionAsync(ViiperBridgeSession failedSession, Exception error)
    {
        if (shuttingDown)
        {
            return;
        }

        await operationGate.WaitAsync();
        Busy = true;
        try
        {
            if (!ReferenceEquals(session, failedSession))
            {
                return;
            }

            ViiperDeviceProfile restartProfile = failedSession.Profile;
            bool keepAutoReconnect = AutoReconnectEnabled;
            if (keepAutoReconnect)
            {
                await StopAutoReconnectAsync();
            }
            session = null;
            await failedSession.DisposeAsync();
            Running = false;
            SetActiveMode(null);
            Status = "VIIPER 数据流异常，正在自动恢复 " + restartProfile.Label + "...";
            AppendLog("ERROR session stream: " + error);

            try
            {
                await EnsureViiperReadyAsync(lifetimeCts.Token);
                await CleanupVirtualDeviceResidueAsync(
                    "session_recovery_" + restartProfile.Mode,
                    lifetimeCts.Token,
                    includePnpDump: false);
                await StartAsync(restartProfile, lifetimeCts.Token);
                if (Running && activeMode == restartProfile.Mode)
                {
                    Status = restartProfile.Label + " 已从 VIIPER 数据流异常中自动恢复。";
                    AppendLog("[VIIPER_RECOVERY] restarted mode=" + restartProfile.Label);
                    if (keepAutoReconnect)
                    {
                        StartAutoReconnect();
                    }
                }
                else
                {
                    Status = "VIIPER 数据流异常后自动恢复未完成，请查看日志并重试当前角色。";
                    AppendLog("[VIIPER_RECOVERY] restart did not reach running state.");
                }
            }
            catch (Exception restartError)
            {
                Status = "VIIPER 数据流异常，自动恢复失败：" + FirstLine(restartError.Message);
                AppendLog("ERROR session recovery: " + restartError);
            }
        }
        catch (Exception cleanupError)
        {
            Running = false;
            SetActiveMode(null);
            Status = "VIIPER 数据流异常，清理失败：" + FirstLine(cleanupError.Message);
            AppendLog("ERROR session cleanup: " + cleanupError);
        }
        finally
        {
            Busy = false;
            operationGate.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        if (shuttingDown)
        {
            return;
        }

        shuttingDown = true;
        lifetimeCts.Cancel();
        await operationGate.WaitAsync();
        Busy = true;
        try
        {
            try
            {
                await StopAutoReconnectAsync();
            }
            catch (Exception ex)
            {
                AppendLog("[SHUTDOWN] auto reconnect cleanup warning: " + ex.Message);
            }

            try
            {
                await StopAsync();
            }
            catch (Exception ex)
            {
                AppendLog("[SHUTDOWN] virtual device cleanup warning: " + ex.Message);
            }

            try
            {
                foreach (Pro2BleInputSource source in pro2Slots
                             .Select(slot => slot.InputSource)
                             .Distinct())
                {
                    await source.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                AppendLog("[SHUTDOWN] Pro2 BLE cleanup warning: " + ex.Message);
            }

            if (viiperProcess != null)
            {
                try
                {
                    if (!viiperProcess.HasExited)
                    {
                        viiperProcess.Kill(entireProcessTree: true);
                        await viiperProcess.WaitForExitAsync();
                        AppendLog("[VIIPER_SERVER] stopped local server.");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("[VIIPER_SERVER] stop warning: " + ex.Message);
                }
                finally
                {
                    viiperProcess.Dispose();
                    viiperProcess = null;
                    localViiperApiPort = null;
                }
            }
            AppendLog("[SHUTDOWN] complete.");
        }
        finally
        {
            Busy = false;
            operationGate.Release();
            bleOperationGate.Dispose();
            lifetimeCts.Dispose();
            await sessionLog.DisposeAsync();
        }
    }

    private static string? FindLocalViiperExe()
    {
        // A packaged build must use the server it was tested with. Looking up
        // the repository copy first made release EXEs silently run stale code.
        string? embedded = ExtractEmbeddedViiperRuntime();
        if (!string.IsNullOrWhiteSpace(embedded))
        {
            return embedded;
        }

        string relative = Path.Combine(
            "tools",
            "viiper",
            "haptic-v0.8.0",
            "viiper-haptic.exe");
        string? cursor = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(cursor); i++)
        {
            string candidate = Path.GetFullPath(Path.Combine(cursor, relative));
            if (File.Exists(candidate))
            {
                return candidate;
            }
            cursor = Directory.GetParent(cursor)?.FullName;
        }

        string cwdCandidate = Path.GetFullPath(relative);
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        return null;
    }

    private static string? ExtractEmbeddedViiperRuntime()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "embedded",
            "v6.2.32-stick-calibration-test-r2",
            "viiper",
            "haptic-v0.8.0");
        Directory.CreateDirectory(root);
        string exe = Path.Combine(root, "viiper-haptic.exe");
        string licenses = Path.Combine(root, "LICENSE.txt");

        if (!ExtractResourceIfAvailable(
                assembly,
                "Embedded.viiper.haptic.exe",
                exe))
        {
            return null;
        }
        ExtractResourceIfAvailable(
            assembly,
            "Embedded.viiper.haptic.license",
            licenses);
        return exe;
    }

    private static bool ExtractResourceIfAvailable(Assembly assembly, string resourceName, string destination)
    {
        using Stream? source = assembly.GetManifestResourceStream(resourceName);
        if (source == null)
        {
            return false;
        }

        byte[] embeddedHash = SHA256.HashData(source);
        source.Position = 0;
        if (File.Exists(destination))
        {
            using FileStream existing = File.OpenRead(destination);
            byte[] existingHash = SHA256.HashData(existing);
            if (CryptographicOperations.FixedTimeEquals(embeddedHash, existingHash))
            {
                return true;
            }
        }

        string temp = destination + ".tmp";
        using (FileStream output = File.Create(temp))
        {
            source.CopyTo(output);
        }
        File.Move(temp, destination, overwrite: true);
        return true;
    }

    private int ParsePort()
    {
        if (!int.TryParse(Port, out int parsed) || parsed is < 1 or > 65535)
        {
            throw new InvalidOperationException("VIIPER Port 必须是 1 到 65535 之间的整数。");
        }

        return parsed;
    }

    private async Task RunExclusiveAsync(
        string operation,
        Func<CancellationToken, Task> action,
        bool isStartupAutomationOperation = false)
    {
        if (shuttingDown)
        {
            return;
        }

        if (!await operationGate.WaitAsync(0))
        {
            Status = "已有操作正在进行，请等待当前步骤完成。";
            AppendLog("[BUSY] rejected operation=" + operation);
            return;
        }

        Busy = true;
        try
        {
            if (!isStartupAutomationOperation)
            {
                await CancelStartupAutomationForManualOperationAsync(operation);
            }
            await action(lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            AppendLog("[CANCELLED] operation=" + operation);
        }
        catch (Exception ex)
        {
            Status = operation + "失败：" + FirstLine(ex.Message);
            AppendLog("ERROR operation " + operation + ": " + ex);
        }
        finally
        {
            Busy = false;
            operationGate.Release();
        }
    }

    private void DisposeExitedViiperProcess()
    {
        if (viiperProcess is not { HasExited: true })
        {
            return;
        }

        AppendLog("[VIIPER_SERVER] previous process exited code=" + viiperProcess.ExitCode);
        viiperProcess.Dispose();
        viiperProcess = null;
        localViiperApiPort = null;
    }

    private static IReadOnlyList<ViiperPortPair> BuildViiperPortCandidates(int requestedApiPort)
    {
        var candidates = new List<ViiperPortPair>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(int apiPort, string source)
        {
            int usbPort = DeriveUsbPort(apiPort);
            if (!IsValidTcpPort(apiPort) ||
                !IsValidTcpPort(usbPort) ||
                apiPort == usbPort)
            {
                return;
            }

            string key = apiPort + "/" + usbPort;
            if (seen.Add(key))
            {
                candidates.Add(new ViiperPortPair(apiPort, usbPort, source));
            }
        }

        Add(requestedApiPort, "requested");
        if (requestedApiPort != 3242)
        {
            Add(requestedApiPort + 100, "requested_plus_100");
            Add(requestedApiPort + 200, "requested_plus_200");
            Add(requestedApiPort + 1000, "requested_plus_1000");
        }

        Add(33242, "fallback_1");
        Add(34242, "fallback_2");
        Add(35242, "fallback_3");
        Add(36242, "fallback_4");
        Add(37242, "fallback_5");
        return candidates;
    }

    private static int DeriveUsbPort(int apiPort)
    {
        if (apiPort == 3242)
        {
            return 3241;
        }

        return apiPort == 1 ? 2 : apiPort - 1;
    }

    private static bool IsValidTcpPort(int port)
    {
        return port is >= 1 and <= 65535;
    }

    private static bool IsLoopbackTcpPortAvailable(int port, out string detail)
    {
        if (!IsValidTcpPort(port))
        {
            detail = "invalid_port";
            return false;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            detail = "free";
            return true;
        }
        catch (SocketException ex)
        {
            detail = "busy socket_error=" + ex.SocketErrorCode + " message=\"" + SanitizeForLog(ex.Message) + "\"";
            return false;
        }
        catch (Exception ex)
        {
            detail = "probe_error=" + ex.GetType().Name + " message=\"" + SanitizeForLog(ex.Message) + "\"";
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private async Task<string?> TryPingViiperOnPortAsync(
        int apiPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await PingCoreAsync(apiPort, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadTailText(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }

            return string.Join(
                Environment.NewLine,
                File.ReadLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .TakeLast(maxLines));
        }
        catch (Exception ex)
        {
            return "read_tail_failed " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static bool LooksLikePortConflict(string text)
    {
        string value = text ?? "";
        return value.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("forbidden by its access permissions", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("通常每个套接字地址", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("bind:", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("listen tcp", StringComparison.OrdinalIgnoreCase) &&
               value.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeUsbipDriverIssue(string text)
    {
        string value = text ?? "";
        return value.Contains("usbip", StringComparison.OrdinalIgnoreCase) &&
               (value.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("attach", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("denied", StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeForLog(string value)
    {
        return (value ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\"", "'");
    }

    private static bool IsLoopbackHost(string value)
    {
        string candidate = (value ?? "").Trim();
        if (candidate.Length == 0 ||
            candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(candidate, out IPAddress? address) &&
               IPAddress.IsLoopback(address);
    }

    private static bool LooksLikeViiperPing(string response)
    {
        return response.Contains("\"server\":\"VIIPER\"", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ViiperPortPair(
        int ApiPort,
        int UsbPort,
        string Source);

    private sealed record ViiperStartupFailure(
        bool PortConflict,
        string Summary,
        string UserMessage);

    private sealed class ProcessOutputTail
    {
        private readonly int maxLines;
        private readonly Queue<string> lines = new();
        private readonly object gate = new();

        public ProcessOutputTail(int maxLines)
        {
            this.maxLines = Math.Max(4, maxLines);
        }

        public void Attach(Process process)
        {
            process.OutputDataReceived += (_, e) => Add("stdout", e.Data);
            process.ErrorDataReceived += (_, e) => Add("stderr", e.Data);
            try
            {
                process.BeginOutputReadLine();
            }
            catch (Exception ex)
            {
                Add("stdout_capture", ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Add("stderr_capture", ex.GetType().Name + ": " + ex.Message);
            }
        }

        public string Snapshot()
        {
            lock (gate)
            {
                return string.Join(Environment.NewLine, lines);
            }
        }

        private void Add(string stream, string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (gate)
            {
                lines.Enqueue(stream + ": " + line);
                while (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }
        }
    }

    private ViiperDeviceProfile SelectedProfile => ProfileFor(selectedMode);
    private ViiperDeviceProfile ActiveProfile => ProfileFor(activeMode ?? selectedMode);
    private static string PushCadenceTelemetry(ViiperPushRateOption option) =>
        option.SourcePaced
            ? "source_adaptive(host_poll_cap_ms=" + option.Interval.TotalMilliseconds.ToString("F1") + ")"
            : "fixed_" + option.Hz.ToString("F1") + "hz";

    private ViiperPushRateOption SelectedPushRateOption =>
        ViiperPushRateOption.FromLabel(selectedPushRateLabel);
    private VirtualBackendOption SelectedBackendOption =>
        VirtualBackendOption.FromLabel(selectedBackendLabel);
    private StickProcessingOption SelectedStickProcessingOption =>
        StickProcessingOption.FromLabel(selectedStickProcessingLabel);
    private Ps5OutputImuTuning SelectedPs5OutputImuTuning =>
        Ps5OutputImuTuning.Default;

    private static bool IsMultiSlotMode(ViiperVirtualMode mode)
    {
        return mode is ViiperVirtualMode.DualSenseLike
            or ViiperVirtualMode.DualSenseEdge
            or ViiperVirtualMode.Pro2
            or ViiperVirtualMode.Xbox;
    }

    private Pro2ControllerSlot? SlotFromParameter(object? parameter)
    {
        if (parameter is Pro2ControllerSlot slot)
        {
            return slot;
        }

        if (parameter is int index)
        {
            return pro2Slots.FirstOrDefault(candidate => candidate.Index == index);
        }

        if (parameter is string text &&
            int.TryParse(text, out int parsed))
        {
            return pro2Slots.FirstOrDefault(candidate => candidate.Index == parsed);
        }

        return null;
    }

    private static ViiperDeviceProfile ProfileFor(ViiperVirtualMode mode)
    {
        return mode switch
        {
            ViiperVirtualMode.DualSenseLike => ViiperDeviceProfile.DualSenseLike,
            ViiperVirtualMode.DualSenseEdge => ViiperDeviceProfile.DualSenseEdge,
            ViiperVirtualMode.Pro2 => ViiperDeviceProfile.Pro2,
            ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static ViiperVirtualMode ModeFromKey(string? key)
    {
        return V60UserSettings.NormalizeModeKey(key) switch
        {
            "dualsenseedge" => ViiperVirtualMode.DualSenseEdge,
            "pro2" => ViiperVirtualMode.Pro2,
            "xbox" => ViiperVirtualMode.Xbox,
            _ => ViiperVirtualMode.DualSenseLike
        };
    }

    private static string ModeKey(ViiperVirtualMode mode)
    {
        return mode switch
        {
            ViiperVirtualMode.DualSenseEdge => "dualsenseedge",
            ViiperVirtualMode.Pro2 => "pro2",
            ViiperVirtualMode.Xbox => "xbox",
            _ => "dualsense"
        };
    }

    private void SelectMode(ViiperVirtualMode mode)
    {
        if (selectedMode == mode)
        {
            return;
        }

        selectedMode = mode;
        userSettings.SelectedModeKey = ModeKey(mode);
        SaveUserSettings("[MODE_SELECT]");
        OnPropertyChanged(nameof(IsDualSenseSelected));
        OnPropertyChanged(nameof(IsDualSenseEdgeSelected));
        OnPropertyChanged(nameof(IsDualSenseProfessionalImuSelected));
        OnPropertyChanged(nameof(IsPro2Selected));
        OnPropertyChanged(nameof(IsXboxSelected));
        OnPropertyChanged(nameof(IsXboxProfessionalImuSelected));
        OnPropertyChanged(nameof(SelectedModeLabel));
        OnPropertyChanged(nameof(SelectedHeroName));
        OnPropertyChanged(nameof(SelectedModeSubtitle));
        OnPropertyChanged(nameof(ModeHeadline));
    }

    private void SetActiveMode(ViiperVirtualMode? mode)
    {
        activeMode = mode;
        OnPropertyChanged(nameof(IsDualSenseActive));
        OnPropertyChanged(nameof(IsDualSenseEdgeActive));
        OnPropertyChanged(nameof(IsDualSenseProfessionalImuActive));
        OnPropertyChanged(nameof(IsPro2Active));
        OnPropertyChanged(nameof(IsXboxActive));
        OnPropertyChanged(nameof(IsXboxProfessionalImuActive));
        OnPropertyChanged(nameof(ModeHeadline));
    }

    private void RaiseConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsInputConnected));
        OnPropertyChanged(nameof(BleButtonText));
        OnPropertyChanged(nameof(BleStateText));
        OnPropertyChanged(nameof(CanManualBleControl));
    }

    private void ClearLog()
    {
        log.Clear();
        OnPropertyChanged(nameof(LogText));
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var persisted = new StringBuilder();
        foreach (string line in text.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            string prefix = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] ";
            log.Append(prefix).AppendLine(line);
            persisted.Append(prefix).AppendLine(line);
        }
        if (log.Length > MaxUiLogCharacters)
        {
            log.Remove(0, log.Length - TrimmedUiLogCharacters);
            log.Insert(0, "[UI LOG TRIMMED]\r\n");
        }
        sessionLog.Write(persisted.ToString());
        OnPropertyChanged(nameof(LogText));
    }

    private static string FirstLine(string text)
    {
        return (text ?? "").Replace("\r", "").Split('\n')[0];
    }

    private void SaveUserSettings(string logPrefix)
    {
        try
        {
            userSettings.Save();
        }
        catch (Exception ex)
        {
            AppendLog(logPrefix + " settings save warning: " + ex.Message);
        }
    }

    private static string ExplainStartFailure(Exception ex)
    {
        string first = FirstLine(ex.Message);
        if (first.Contains("usbip", StringComparison.OrdinalIgnoreCase) &&
            first.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "VIIPER 找不到 usbip.exe。请点击“安装/修复 usbip-win2”，使用 EXE 内置的正式安装器，完成后重新启动本地 VIIPER。" +
                   UsbipPortableInstallHint;
        }

        if (first.Contains("attach", StringComparison.OrdinalIgnoreCase) ||
            first.Contains("device handler", StringComparison.OrdinalIgnoreCase))
        {
            return "USBIP 虚拟设备挂载失败。若刚安装/修复 USBIP，请先重启 Windows；重启后仍失败再点击“安装/修复 USBIP”。原始错误：" + first;
        }

        if (first.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            first.Contains("连接被拒绝", StringComparison.OrdinalIgnoreCase))
        {
            return "VIIPER 服务未响应。程序会自动启动内置 VIIPER；若仍失败，请展开系统控制台查看 VIIPER 日志。原始错误：" + first;
        }

        return first;
    }

    private void RefreshRuntimeReadiness()
    {
        UsbipRuntime? usbip = UsbipRuntimeLocator.Find();
        if (usbip != null)
        {
            RuntimeReadinessText = "USBIP 程序已发现，启动角色时会自动检查内核驱动；VIIPER 已内置，无需另行安装。";
            return;
        }

        UsbipInstaller? installer = UsbipRuntimeLocator.FindBundledInstaller();
        RuntimeReadinessText = installer != null
            ? "首次使用：需要安装 USBIP 内核驱动。直接选择角色即可自动打开内置安装向导，VIIPER 无需另行安装。"
            : "发布包不完整：未安装 USBIP，且没有找到内置安装器。请重新获取完整 EXE。";
    }

    private async Task RefreshRuntimeReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            UsbipRuntime? usbip = UsbipRuntimeLocator.Find();
            if (usbip == null)
            {
                UsbipInstaller? installer = UsbipRuntimeLocator.FindBundledInstaller();
                RuntimeReadinessText = installer != null
                    ? "VIIPER 已内置；未发现可用的 USBIP。直接选择角色或点击“安装/修复 USBIP”会打开 EXE 内置的正式安装器。" + UsbipPortableInstallHint
                    : "发布包不完整：VIIPER 可用，但没有找到 USBIP 内核驱动或内置安装器。";
                AppendLog("[RUNTIME_DIAG] viiper=embedded usbip=not_installed installer=" + (installer != null ? "embedded" : "missing"));
                return;
            }

            RuntimeReadinessText = "正在验证 USBIP 内核驱动；VIIPER 已内置，无需另行安装...";
            UsbipProbeResult probe = await UsbipRuntimeLocator.ProbeAsync(usbip, cancellationToken);
            if (probe.Ready)
            {
                RuntimeReadinessText = "运行环境已就绪：VIIPER 已内置，USBIP 内核驱动可用。无需额外下载或安装 VIIPER。";
                AppendLog("[RUNTIME_DIAG] viiper=embedded usbip=ready exe=" + usbip.ExePath);
                return;
            }

            RuntimeReadinessText = "已找到 USBIP 程序，但内核驱动尚未就绪。若刚安装，请先重启 Windows；重启后仍失败再点“安装/修复 USBIP”。VIIPER 本体已内置。";
            AppendLog("[RUNTIME_DIAG] viiper=embedded usbip=installed_driver_not_ready detail=" + probe.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeReadinessText = "VIIPER 已内置；USBIP 状态检测失败：" + FirstLine(ex.Message);
            AppendLog("[RUNTIME_DIAG] failed " + ex.Message);
        }
    }

    public event Action<string, string>? UserNotificationRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


