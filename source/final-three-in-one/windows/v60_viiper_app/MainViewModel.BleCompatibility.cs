using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private int bleAutoOptimizationQueued;
    private bool win10BleHomePanelExpanded;
    public bool ShowWin10BleHomeAction => BleTurboUiPresentation.ShowHomeAction(
        OperatingSystem.IsWindows(), Environment.OSVersion.Version.Build);
    public bool Win10BleHomePanelExpanded
    {
        get => win10BleHomePanelExpanded;
        set
        {
            if (Busy || !ShowWin10BleHomeAction || value == win10BleHomePanelExpanded) return;
            win10BleHomePanelExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowWin10BleHomePanel));
        }
    }
    public bool ShowWin10BleHomePanel => ShowWin10BleHomeAction && Win10BleHomePanelExpanded;
    public void ToggleWin10BleHomePanel() => Win10BleHomePanelExpanded = !Win10BleHomePanelExpanded;
    public string Win10BleHomeHint => inputSource.IsHighRateReady && inputSource.IsTransportConnected
        ? "当前输入已达标，无需修改蓝牙参数。"
        : CanConfigureWin10Stack
            ? "优先尝试 7.5ms；需管理员授权，可能短暂断开其他蓝牙设备。"
            : "当前系统检查未通过，暂不能应用参数。请查看系统与蓝牙信息。";
    private Win10BlePresetDefinition selectedWin10BlePreset = Win10BlePresetPolicy.Default;
    public IReadOnlyList<Win10BlePresetDefinition> Win10BlePresetOptions => Win10BlePresetPolicy.Presets;
    public Win10BlePresetDefinition SelectedWin10BlePreset
    {
        get => selectedWin10BlePreset;
        set
        {
            if (value == null || Busy || IsBleOptimizationRunning) return;
            selectedWin10BlePreset = Win10BlePresetPolicy.Get(value.Preset);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Win10BleConfigurationConfirmation));
        }
    }

    public bool AutoOptimizeBle
    {
        get => userSettings.AutoOptimizeBle;
        set
        {
            if (value == userSettings.AutoOptimizeBle || !CanChangeBleOptimizationSettings) return;
            userSettings.AutoOptimizeBle = value;
            foreach (Pro2ControllerSlot slot in pro2Slots)
                slot.InputSource.AutoOptimizeBleEnabled = value;
            SaveUserSettings("[BLE_AUTO_OPTIMIZE]");
            OnPropertyChanged();
        }
    }

    public bool IsBleOptimizationRunning => inputSource.IsCompatibilityOptimizationRunning;
    public bool IsWin10BleExperimental => BleTurboUiPresentation.IsExperimental(BleCompatibilityInspector.Current.Plan);
    public string Win10BlePresetSafetyNote => BleTurboUiPresentation.SafetyNote(BleCompatibilityInspector.Current.Plan);
    public string Win10BleConfigurationConfirmation => BleTurboUiPresentation.Confirmation(
        BleCompatibilityInspector.Current.Plan, SelectedWin10BlePreset);
    public bool CanChangeBleOptimizationSettings => !Busy && !IsBleOptimizationRunning;
    public string BleOptimizationSummary => inputSource.CompatibilityOptimizationSummary;
    public bool ShowBleOptimizationSummary => !string.IsNullOrWhiteSpace(BleOptimizationSummary);
    public string BleConnectionTitle => IsBleOptimizationRunning ? "正在优化蓝牙连接" : CompactConnectionTitle;
    public string BleConnectionHint => IsBleOptimizationRunning
        ? BleOptimizationSummary + " · 按真实输入保留较好方案。"
        : CompactConnectionHint;

    public bool ShowWin11CompatibilityAction => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        (HasLowRateWarning || IsBleOptimizationRunning);
    public bool CanTryWin11Compatibility => !previewOnly && !Busy &&
        inputSource.CanOptimizeConnectionCompatibility;

    private void InitializeBleCompatibilitySettings()
    {
        foreach (Pro2ControllerSlot slot in pro2Slots)
            slot.InputSource.AutoOptimizeBleEnabled = userSettings.AutoOptimizeBle;
    }

    private void RefreshBleCompatibilityPresentation()
    {
        foreach (string property in new[] { nameof(AutoOptimizeBle), nameof(IsBleOptimizationRunning),
            nameof(CanChangeBleOptimizationSettings), nameof(BleOptimizationSummary), nameof(ShowBleOptimizationSummary),
            nameof(BleConnectionTitle), nameof(BleConnectionHint), nameof(ShowWin11CompatibilityAction),
            nameof(CanTryWin11Compatibility), nameof(IsWin10BleExperimental), nameof(Win10BlePresetSafetyNote),
            nameof(Win10BleConfigurationConfirmation), nameof(CanConfigureWin10Stack),
            nameof(ShowWin10BleHomeAction), nameof(ShowWin10BleHomePanel), nameof(Win10BleHomeHint) })
            OnPropertyChanged(property);
    }

    private void QueueLowRateAutomaticOptimization()
    {
        if (previewOnly || shuttingDown || !AutoOptimizeBle || Busy || !HasLowRateWarning ||
            !inputSource.CanOptimizeConnectionCompatibility ||
            Interlocked.Exchange(ref bleAutoOptimizationQueued, 1) != 0) return;
        async Task Optimize()
        {
            try
            {
                if (!shuttingDown && AutoOptimizeBle && inputSource.CanOptimizeConnectionCompatibility)
                {
                    AppendLog("[BLE_AUTO_PRESET] trigger=confirmed_live_low_rate once_per_connection=true");
                    await TryWin11CompatibilityAsync();
                }
            }
            finally { Interlocked.Exchange(ref bleAutoOptimizationQueued, 0); }
        }
        if (uiContext != null) uiContext.Post(async _ => await Optimize(), null);
        else _ = Task.Run(Optimize);
    }

    public Task TryWin11CompatibilityAsync() => RunExclusiveAsync("自动优化蓝牙", async token =>
    {
        if (!inputSource.CanOptimizeConnectionCompatibility)
        {
            Status = "当前连接无需优化、正在测试或本轮方案已测完；正常高速连接不会被修改。";
            return;
        }
        Status = "正在自动对比蓝牙方案；请保持手柄开机，实测速率持续显示。";
        Pro2CompatibilityTrialOutcome outcome;
        await bleOperationGate.WaitAsync(token);
        try
        {
            outcome = await inputSource.OptimizeConnectionCompatibilityAsync(new Progress<string>(message =>
            {
                AppendLog(message);
                RefreshBleCompatibilityPresentation();
            }), token);
        }
        finally { bleOperationGate.Release(); }
        Status = outcome.Summary;
        await RefreshEnvironmentInfoAsync();
        RefreshCompactPresentation();
    });
}
