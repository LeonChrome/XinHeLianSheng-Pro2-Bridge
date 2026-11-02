using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed class Pro2ControllerSlot : INotifyPropertyChanged
{
    private bool enabled;
    private bool autoReconnectEnabled;
    private bool virtualDeviceRunning;
    private bool isSleeping;
    private string sleepStatus = "";
    private ViiperVirtualMode selectedMode = ViiperVirtualMode.Pro2;
    private string status;
    private string connectedAddress = "";
    private string stickCalibrationStatus = "摇杆校准：连接手柄后可执行零位或完整行程校准。";

    public Pro2ControllerSlot(int index, bool enabled)
    {
        Index = index;
        Name = "手柄 " + index;
        this.enabled = enabled;
        status = enabled
            ? "已启用，等待进入游戏。"
            : "未启用。";
        InputSource = new Pro2BleInputSource();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }
    public string Name { get; }
    public bool CanRemove => Index != 1;
    public IReadOnlyList<Pro2SlotModeOption> ModeOptions => Pro2SlotModes.Options;
    public ViiperVirtualMode SelectedMode
    {
        get => selectedMode;
        set { value = Pro2SlotModes.FromMode(value).Mode; if (selectedMode == value) return;
            selectedMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeSummary)); }
    }
    public string ModeSummary => VirtualDeviceRunning && Session != null
        ? "当前输出：" + Pro2SlotModes.FromMode(Session.Profile.Mode).Label +
            (Session.Profile.Mode == SelectedMode ? "" : " · 点击应用模式生效")
        : "待用模式：" + Pro2SlotModes.FromMode(SelectedMode).Label;
    public string SleepStatus
    {
        get => sleepStatus;
        set { if (sleepStatus == value) return; sleepStatus = value; OnPropertyChanged(); }
    }
    public bool IsSleeping
    {
        get => isSleeping;
        set { if (isSleeping == value) return; isSleeping = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(RateSummary)); OnPropertyChanged(nameof(DisplayStatus)); }
    }
    public Pro2BleInputSource InputSource { get; }
    public ViiperBridgeSession? Session { get; set; }
    public CancellationTokenSource? AutoReconnectCts { get; set; }
    public Task? AutoReconnectTask { get; set; }
    public string AppliedStickCalibrationKey { get; set; } = "";

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }

            enabled = value;
            Status = enabled
                ? "已启用，等待进入游戏。"
                : "未启用。";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Headline));
            OnPropertyChanged(nameof(ModeSummary));
        }
    }

    public bool AutoReconnectEnabled
    {
        get => autoReconnectEnabled;
        set
        {
            if (autoReconnectEnabled == value)
            {
                return;
            }

            autoReconnectEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Headline));
        }
    }

    public bool VirtualDeviceRunning
    {
        get => virtualDeviceRunning;
        set
        {
            if (virtualDeviceRunning == value)
            {
                return;
            }

            virtualDeviceRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Headline));
            OnPropertyChanged(nameof(ModeSummary));
        }
    }

    public string ConnectedAddress
    {
        get => connectedAddress;
        set
        {
            string normalized = value ?? "";
            if (connectedAddress == normalized)
            {
                return;
            }

            connectedAddress = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Headline));
        }
    }

    public string Status
    {
        get => status;
        set
        {
            string normalized = value ?? "";
            if (status == normalized)
            {
                return;
            }

            status = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayStatus));
        }
    }

    public string StickCalibrationStatus
    {
        get => stickCalibrationStatus;
        set
        {
            string normalized = value ?? "";
            if (stickCalibrationStatus == normalized)
            {
                return;
            }

            stickCalibrationStatus = normalized;
            OnPropertyChanged();
        }
    }

    public string Headline =>
        Name +
        (Enabled ? " · ON" : " · OFF") +
        (VirtualDeviceRunning ? " · USB" : "") +
        (InputSource.IsRunning ? " · BLE " + InputSource.LinkRateClass : "") +
        (AutoReconnectEnabled ? " · AUTO" : "") +
        (string.IsNullOrWhiteSpace(ConnectedAddress) ? "" : " · " + ConnectedAddress);

    public string RateSummary
    {
        get
        {
            var r = InputSource.GetRateEvidence();
            if (IsSleeping) return "已休眠 · 按任意键唤醒";
            if (!r.Connected || !r.Running) return "未连接";
            if (!r.RecentWindow || r.LastInputAgeMs >= 500) return "输入中断或正在测量";
            return $"FD2 {r.Fd2Hz:F1} Hz / 解析 {r.ParsedHz:F1} Hz · " +
                (r.IntervalUnits > 0 ? $"间隔 {r.IntervalUnits * 1.25:F2}ms · " : "") +
                (r.HighRate ? "高速" : r.GameplayReady ? "可游玩，建议优化" : "低速，待优化");
        }
    }
    public string OptimizationSummary => InputSource.CompatibilityOptimizationSummary;
    public string DisplayStatus => IsSleeping ? Status : InputSource.IsRunning
        ? InputSource.IsCompatibilityOptimizationRunning ? OptimizationSummary : "已连接 · 输入、震动与重连独立运行"
        : Status.StartsWith("真实 Pro2 BLE", StringComparison.Ordinal) ? "未连接 · 等待重连" : Status;
    public string DiagnosticSummary
    {
        get
        {
            var r = InputSource.GetRateEvidence();
            return $"系统 {Environment.OSVersion.Version} · 请求 {r.DescriptorStatus} · 参数 {r.ParameterStatus}" +
                $"\nNotify P50 / P95：{r.Gaps.P50Ms:F2} / {r.Gaps.P95Ms:F2}ms · 解析失败 {r.ParseFailures}" +
                "\n" + OptimizationSummary + "\n" + (IsSleeping ? "休眠中；此槽位按地址独立监听唤醒" :
                    InputSource.ConnectionDiagnosis.Title + "\n" + InputSource.ConnectionDiagnosis.Advice);
        }
    }

    public void RefreshFromSource()
    {
        ConnectedAddress = InputSource.ConnectedAddress;
        if (InputSource.IsRunning) Status = InputSource.Status;
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(RateSummary));
        OnPropertyChanged(nameof(OptimizationSummary));
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(DiagnosticSummary));
    }

    public async Task StopAutoReconnectAsync()
    {
        CancellationTokenSource? cts = AutoReconnectCts;
        Task? task = AutoReconnectTask;
        AutoReconnectCts = null;
        AutoReconnectTask = null;
        AutoReconnectEnabled = false;
        if (cts != null)
        {
            cts.Cancel();
        }

        if (task != null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
