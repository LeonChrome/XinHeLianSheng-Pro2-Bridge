using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

// Presentation-only sampling. No input/rumble work depends on this refresh.
public sealed partial class MainViewModel
{
    private uint compactRaw, compactFd2, compactParsed;
    private long compactSampleTicks;
    private string compactInputRate = "—";
    private string compactBattery = "电量未知";
    private BatteryVisual compactBatteryVisual = BatteryVisual.Create(GamepadState.Neutral(), false, false, false);
    private string compactRawRate = "—";
    private string compactParsedRate = "—";
    private string compactInputAge = "—";
    private bool showDiagnostics;
    private ICommand? compactModeCommand, cancelCommand, compactStopCommand;

    public ICommand SelectCompactModeCommand => compactModeCommand ??=
        new RelayCommand(async parameter =>
        {
            if (Busy || previewOnly && parameter == null) return;
            ViiperVirtualMode mode = parameter?.ToString() switch
            {
                "Xbox" => ViiperVirtualMode.Xbox,
                "PS5" => ViiperVirtualMode.DualSenseLike,
                "Pro1" => ViiperVirtualMode.Pro1,
                _ => ViiperVirtualMode.Pro2
            };
            if (Running)
                await RunExclusiveAsync("切换兼容模式", token => SwitchModeAsync(ProfileFor(mode), token));
            else SelectMode(mode);
            RefreshCompactPresentation();
        });

    public ICommand CancelConnectionCommand => cancelCommand ??=
        new RelayCommand(_ => currentOperationCts?.Cancel());
    public ICommand CompactStopCommand => compactStopCommand ??=
        new RelayCommand(_ => RunExclusiveAsync("停止输出与自动重连", _ => StopAsync()));
    public bool CompactCanStop => !Busy &&
        (Running || AutoReconnectEnabled || pro2Slots.Any(slot => slot.AutoReconnectEnabled));

    public bool CompactPs5Selected => IsDualSenseSelected;
    public double RumblePercent
    {
        get => RumbleMultiplier * 100;
        set { RumbleMultiplier = value / 100; OnPropertyChanged(); OnPropertyChanged(nameof(RumblePercentText)); }
    }
    public string RumblePercentText => RumblePercent.ToString("0") + "%";
    public string CompactInputRate => compactInputRate;
    public string BleStabilitySummary
    {
        get
        {
            Pro2RateEvidence rate = inputSource.GetRateEvidence();
            if (!rate.Connected || !rate.Running) return "连接后持续测量真实输入";
            if (rate.LastInputAgeMs is not (>= 0 and < 500)) return "输入停顿 · 查看连接状态";
            BleRateStabilitySnapshot? health = rate.Stability;
            if (health == null || !health.HasValidWindow) return "正在建立约 5 秒监测窗口";
            if (!health.IsRecent) return "测量已过期，等待新输入窗口";
            if (health.IsStableHighRate) return "高速稳定 · 已测 " + TimeSpan.FromSeconds(health.ValidMeasuredSeconds).ToString(@"hh\:mm\:ss");
            if (health.State == BleRateStabilityState.Recovering) return "速率恢复中 · " + health.ConsecutiveGoodWindows + "/3 个高速窗口";
            return health.ShouldWarnLowRate ? "持续低速 · 查看下方诊断" : "持续观察 · 最新窗口 " +
                health.LastWindow!.EffectiveHz.ToString("F1") + " Hz";
        }
    }
    public string CompactBattery => compactBattery;
    public BatteryVisual CompactBatteryVisual => compactBatteryVisual;
    public string CompactBatteryHint
    {
        get
        {
            var battery = inputSource.GetBatterySnapshot();
            string unavailable = inputSource.BatteryLevelReadStatus == "read_not_permitted"
                ? " 手柄拒绝直接读取档位（ATT 0x02）；定时更新使用新的通知报告，不重复 Read。"
                : " 尚未取得原生档位；不按电压猜测百分比。";
            return "显示手柄原生 0–9 档位，不是精确容量百分比。" +
                (battery.State.BatteryVoltageMillivolts > 0
                    ? " 电池电压 " + (battery.State.BatteryVoltageMillivolts / 1000.0).ToString("F3") + " V。" : "") +
                (battery.HasConnectionLevel ? " 档位采集于 " + battery.LevelAgeSeconds.ToString("0") + " 秒前。" : unavailable) +
                (inputSource.RuntimeBatterySuspensionReason.Length > 0
                    ? inputSource.RuntimeBatteryRefreshStatus == "cooldown"
                        ? " 更新已暂停；输入稳定10分钟后尝试恢复一次，保留上次读取值。"
                        : " 自动更新已暂停以保护输入：" + inputSource.RuntimeBatterySuspensionReason + "；保留上次读取值。"
                    : RuntimeBatteryRefreshEnabled ? " 约每 10 分钟短暂采集一次新档位，结束后核验通知已关闭。"
                    : " 自动更新已关闭；保留上次读取值。") +
                " 电压和充电状态随 FD2 原包更新。";
        }
    }
    public string CompactRawRate => compactRawRate;
    public string CompactParsedRate => compactParsedRate;
    public string CompactInputAge => compactInputAge;
    public string CompactFirmware => Pro2DevicePresentation.Firmware(inputSource.FirmwareInfo, inputSource.IsTransportConnected);
    public string CompactFirmwareHint => inputSource.FirmwareInfo is { } info
        ? "蓝牙补丁 " + info.BluetoothPatchVersion + " · DSP " + (info.DspVersion ?? "未报告")
        : "连接时读取手柄的实际固件版本";
    public string CompactHeadset
    {
        get
        {
            bool fresh = inputSource.TryGetLatest(out var state, out var age) && age.TotalMilliseconds < 2000;
            return Pro2DevicePresentation.Headset(state, fresh, inputSource.IsTransportConnected);
        }
    }
    public string CompactHapticSource => (session ?? pro2Slots[0].Session)?.FeedbackSource ?? "等待游戏反馈";
    public string CompactActualMode => activeMode.HasValue ? ProfileFor(activeMode.Value).Label : "尚未创建虚拟手柄";
    public string CompatibilityTitle => BleCompatibilityInspector.Current.Plan.Title;
    public string CompatibilityGuidance => BleCompatibilityInspector.Current.Plan.Guidance;
    public bool CanConfigureWin10Stack => BleCompatibilityInspector.Current.Plan.CanConfigureWin10Stack;
    private Pro2ConnectionPresentation ConnectionPresentation =>
        Pro2ConnectionPresentation.Create(inputSource.ConnectionPhase,
            inputSource.IsTransportConnected, Running, Busy, AutoReconnectEnabled, inputSource.IsGameplayReady);
    public string CompactConnectionTitle => ConnectionPresentation.Title;
    public string CompactConnectionHint => ConnectionPresentation.Hint;
    public string CompactConnectText => inputSource.ConnectionPhase == Pro2ConnectionPhase.Scanning ? "正在搜索…" :
        inputSource.ConnectionPhase == Pro2ConnectionPhase.Connecting ? "正在连接…" :
        inputSource.ConnectionPhase == Pro2ConnectionPhase.Initializing ? "正在初始化…" :
        inputSource.IsTransportConnected && inputSource.ConnectionPhase == Pro2ConnectionPhase.Measuring ? "正在测速…" :
        inputSource.IsRunning && !inputSource.IsGameplayReady && !Running ? "等待输入恢复" :
        Running ? "游戏输出已启动" : inputSource.IsRunning ? "进入游戏" : "连接 Pro2";
    public bool CompactCanConnect => CanOperate && !Running &&
        inputSource.ConnectionPhase is not (Pro2ConnectionPhase.Scanning or Pro2ConnectionPhase.Connecting or
            Pro2ConnectionPhase.Initializing or Pro2ConnectionPhase.Measuring) &&
        (!inputSource.IsRunning || inputSource.IsGameplayReady);
    public string CompactStateText => ConnectionPresentation.Badge;
    public string CompactSystemText => SystemBluetoothDiagnostics.Footer;
    public bool ShowDiagnostics
    {
        get => showDiagnostics;
        set { showDiagnostics = value; OnPropertyChanged(); FlushCompactLog(); }
    }

    public void ResetCompactTelemetrySample()
    {
        compactSampleTicks = 0;
        Pro2RateEvidence rate = inputSource.GetRateEvidence();
        bool fresh = rate.Connected && rate.Running && rate.RecentWindow &&
            rate.LastInputAgeMs is >= 0 and < 500;
        compactInputRate = fresh ? rate.Fd2Hz.ToString("0.0") : "—";
        compactRawRate = fresh ? rate.Fd2Hz.ToString("0.0") + " Hz" : "—";
        compactParsedRate = fresh ? rate.ParsedHz.ToString("0.0") + " Hz" : "—";
    }

    public void RefreshCompactTelemetry()
    {
        var counters = inputSource.GetCompactCounters();
        long now = Stopwatch.GetTimestamp();
        if (compactSampleTicks != 0)
        {
            double seconds = Stopwatch.GetElapsedTime(compactSampleTicks, now).TotalSeconds;
            bool monotonic = counters.Raw >= compactRaw && counters.Fd2 >= compactFd2 &&
                             counters.Parsed >= compactParsed;
            bool live = inputSource.IsTransportConnected && counters.LastTicks > 0 && monotonic &&
                double.IsFinite(seconds) && seconds > 0;
            compactInputRate = live ? ((counters.Fd2 - compactFd2) / seconds).ToString("0.0") : "—";
            compactRawRate = live ? ((counters.Raw - compactRaw) / seconds).ToString("0.0") + " Hz" : "—";
            compactParsedRate = live ? ((counters.Parsed - compactParsed) / seconds).ToString("0.0") + " Hz" : "—";
        }
        compactSampleTicks = now;
        compactRaw = counters.Raw;
        compactFd2 = counters.Fd2;
        compactParsed = counters.Parsed;
        var battery = inputSource.GetBatterySnapshot();
        compactBattery = Pro2BatteryStatus.Display(battery.State, battery.Fresh || battery.HasConnectionLevel,
            levelAtConnection: battery.HasConnectionLevel && !battery.RuntimeLevel);
        if (battery.RuntimeLevel && !battery.LevelFresh) compactBattery += "（上次读取）";
        compactBatteryVisual = BatteryVisual.Create(battery.State, inputSource.IsRunning,
            battery.HasConnectionLevel, battery.Fresh,
            inputSource.RuntimeBatterySuspensionReason.Length > 0
                ? inputSource.RuntimeBatteryRefreshStatus == "cooldown" ? "等待恢复" : "更新已暂停" :
                battery.RuntimeLevel ? battery.LevelFresh && RuntimeBatteryRefreshEnabled ? "定时更新" : "上次读取" : "连接时档位");
        compactInputAge = inputSource.IsRunning && counters.LastTicks > 0
            ? Stopwatch.GetElapsedTime(counters.LastTicks, now).TotalMilliseconds.ToString("0") + " ms" : "—";
        foreach (string property in new[] { nameof(CompactInputRate), nameof(CompactRawRate),
                     nameof(CompactParsedRate), nameof(CompactBattery), nameof(CompactInputAge),
                     nameof(CompactHapticSource), nameof(CompactActualMode), nameof(RumblePercentText), nameof(CompactBatteryHint), nameof(CompactBatteryVisual),
                     nameof(CompactFirmware), nameof(CompactFirmwareHint), nameof(CompactHeadset), nameof(EnvironmentReportText) })
            OnPropertyChanged(property);
        RefreshCompactPresentation();
        FlushCompactLog();
    }

    private void RefreshCompactPresentation()
    {
        RefreshBleCompatibilityPresentation();
        RefreshLowRateDiagnosis();
        QueueLowRateAutomaticOptimization();
        foreach (string property in new[] { nameof(CompactPs5Selected),
                     nameof(BleStabilitySummary), nameof(ShowWin11CompatibilityAction), nameof(CanTryWin11Compatibility),
                     nameof(CompactConnectionTitle), nameof(CompactConnectionHint),
                     nameof(CompactConnectText), nameof(CompactCanConnect), nameof(CompactStateText), nameof(CompactFirmware), nameof(CompactFirmwareHint), nameof(CompactHeadset), nameof(EnvironmentReportText) })
            OnPropertyChanged(property);
        OnPropertyChanged(nameof(CompactCanStop));
    }

    private void OnInputConnectionStateChanged()
    {
        void Refresh()
        {
            if (shuttingDown) return;
            RefreshCompactPresentation();
            OnPropertyChanged(nameof(CompactActualMode));
        }
        if (uiContext != null) uiContext.Post(_ => Refresh(), null);
        else Refresh();
    }

    private void QueuePendingGameResume()
    {
        if (Interlocked.Exchange(ref resumeGameQueued, 1) != 0) return;
        async System.Threading.Tasks.Task Resume()
        {
            try
            {
                if (!enterGamePending || shuttingDown) return;
                AppendLog("[PRO2_AUTO_RESUME] real input gameplay-ready; scheduling pending game mode without requiring 133Hz.");
                await RunExclusiveAsync("输入验收通过后继续进入游戏", EnterGameAsync);
            }
            finally { Interlocked.Exchange(ref resumeGameQueued, 0); }
        }
        // Never await a mode switch from the reconnect loop itself:
        // SwitchMode pauses/joins that loop, which would otherwise self-deadlock.
        if (uiContext != null) uiContext.Post(async _ => await Resume(), null);
        else _ = System.Threading.Tasks.Task.Run(Resume);
    }

    private void FlushCompactLog()
    {
        lock (logGate)
        {
            if (!ShowDiagnostics || !logDirty) return;
            logDirty = false;
        }
        OnPropertyChanged(nameof(LogText));
    }
}
