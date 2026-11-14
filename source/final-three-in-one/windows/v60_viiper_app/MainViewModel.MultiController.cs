using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    public string MultiControllerSummary => $"已连接 {pro2Slots.Count(s => s.ActiveInput.IsRunning)} / 4 · 休眠 {pro2Slots.Count(s => s.IsSleeping)} · 每只独立输入、震动与重连";
    private ICommand? removeSlotCommand, optimizeSlotCommand, applySlotModeCommand;
    public ICommand ApplyPro2SlotModeCommand => applySlotModeCommand ??= new RelayCommand(parameter =>
        RunExclusiveAsync("应用单只手柄模式", async token =>
        {
            if (SlotFromParameter(parameter) is { } slot) await ApplySlotModeAsync(slot, token);
        }));
    private bool HasPs5VirtualOutput => session?.Profile.IsPs5Family == true ||
        pro2Slots.Any(s => s.VirtualDeviceRunning && s.Session?.Profile.IsPs5Family == true);
    private ViiperDeviceProfile RuntimeProfileForSlot(Pro2ControllerSlot slot) =>
        Pro2SlotModes.Profile(slot.SelectedMode, slot.Index, SelectedPushRateOption);

    private async Task ApplySlotModeAsync(Pro2ControllerSlot slot, CancellationToken token)
    {
        if(!slot.CanChangeConnection) { Status=slot.Status="此手柄正在读卡，请先取消或等待读取结束，再切换游戏输出。"; return; }
        if (slot.Index == 1) SelectMode(slot.SelectedMode);
        if (!Running || !slot.Enabled || previewOnly)
        { slot.Status = "模式已保存；下次连接使用 " + Pro2SlotModes.FromMode(slot.SelectedMode).Label + "。"; return; }
        if (slot.VirtualDeviceRunning && slot.Session?.Profile.Mode == slot.SelectedMode)
        { slot.Status = "当前已使用所选模式。"; return; }
        await bleOperationGate.WaitAsync(token);
        try
        {
            ViiperVirtualMode? previous = slot.Session?.Profile.Mode;
            ViiperVirtualMode requested = slot.SelectedMode;
            var result = await Pro2SlotModeSwitch.ReplaceAsync(previous, requested, async () =>
            {
                var old = slot.Session; slot.Session = null; slot.VirtualDeviceRunning = false;
                if (old != null) await old.DisposeAsync();
            }, (mode, ct) => StartSingleSlotSessionAsync(slot,
                Pro2SlotModes.Profile(mode, slot.Index, SelectedPushRateOption), ct), token);
            if (slot.Index == 1) SetActiveMode(slot.Session?.Profile.Mode ?? requested);
            slot.Status = result switch
            {
                Pro2SlotModeChangeResult.Changed => "已切换为 " + Pro2SlotModes.FromMode(requested).Label + "；其他手柄保持运行。",
                Pro2SlotModeChangeResult.RestoredPrevious => "新模式创建失败，已恢复原模式；其他手柄保持运行。",
                _ => "本槽位输出未启动；BLE 和其他手柄保持，点击连接重试。"
            };
            AppendLog("[SLOT_MODE_SWITCH] slot=" + slot.Index + " previous=" + previous + " requested=" + requested +
                " actual=" + slot.Session?.Profile.Mode + " result=" + result + " physical_ble_reconnected=false peers_preserved=true");
        }
        finally { bleOperationGate.Release(); }
        if (HasPs5VirtualOutput) await ApplyDualSenseAudioGuardAsync(token);
        else StopBuiltInAudioGuard();
        slot.RefreshFromSource(); UpdateMultiSlotInputStatus(); RefreshCompactPresentation();
    }
    public ICommand RemovePro2SlotCommand => removeSlotCommand ??= new RelayCommand(parameter =>
        RunExclusiveAsync("移除手柄", async token =>
        {
            var slot = SlotFromParameter(parameter);
            if (slot == null || slot.Index == 1) return;
            await DisconnectPro2SlotAsync(slot, token);
            slot.PhysicalInput=null;
            lock (autoSleepStates) autoSleepStates.Remove(slot.Index);
            slot.IsSleeping = false;
            slot.SleepStatus = "";
            slot.Enabled = false;
            var previous = slot.Session; slot.Session = null;
            if (previous != null) await previous.DisposeAsync();
            slot.VirtualDeviceRunning = false;
            slot.Status = "已移除；其他手柄保持连接。";
            if (!HasPs5VirtualOutput) StopBuiltInAudioGuard();
            UpdateMultiSlotInputStatus();
        }));
    public ICommand OptimizePro2SlotCommand => optimizeSlotCommand ??= new RelayCommand(parameter =>
        RunExclusiveAsync("优化单只手柄蓝牙", async token =>
        {
            var slot = SlotFromParameter(parameter);
            if (slot == null) return;
            await OptimizeSlotCompatibilityAsync(slot, token);
        }));

    private void InitializeMultiControllerSafety()
    {
        bool[] enabled = V60UserSettings.NormalizeEnabledSlots(userSettings.EnabledPro2Slots);
        string[] modes = Pro2SlotModes.Normalize(userSettings.SlotModeKeys, userSettings.SelectedModeKey);
        foreach (var slot in pro2Slots)
        {
            slot.LoadInputConfiguration(ControllerInputConfiguration.Normalize(userSettings.SlotInputs)[slot.Index-1]);
            slot.Enabled = enabled[slot.Index - 1];
            slot.SelectedMode = Pro2SlotModes.FromKey(modes[slot.Index - 1]).Mode;
            slot.InputSource.PeerRatesProvider = () => CapturePeerRates(slot.Index);
            slot.PropertyChanged += (_, e) =>
            {
                if(e.PropertyName==nameof(Pro2ControllerSlot.InputConfiguration)) OnInputConfigurationChanged(slot);
                if(e.PropertyName==nameof(Pro2ControllerSlot.SelectedInputKind))
                {
                    ApplyDiscoveredControllers(discoveredControllers);
                    if(slot.IsPhysicalInputSelected && !previewOnly) _=RefreshControllerDevicesAsync();
                }
                if (e.PropertyName is nameof(Pro2ControllerSlot.StickCalibrationStatus) or nameof(Pro2ControllerSlot.GyroCalibrationStatus))
                    OnPropertyChanged(nameof(CalibrationStatusText));
                if (e.PropertyName == nameof(Pro2ControllerSlot.SelectedMode))
                {
                    userSettings.SlotModeKeys = pro2Slots.Select(s => Pro2SlotModes.FromMode(s.SelectedMode).Key).ToArray();
                    if (slot.Index == 1) SelectMode(slot.SelectedMode);
                    SaveUserSettings("[SLOT_MODE_SELECT]");
                }
                if (e.PropertyName == nameof(Pro2ControllerSlot.IsSleeping))
                {
                    OnPropertyChanged(nameof(MultiControllerSummary));
                    RefreshConnectionDiagnosis();
                }
                if (e.PropertyName != nameof(Pro2ControllerSlot.Enabled)) return;
                userSettings.EnabledPro2Slots = pro2Slots.Select(s => s.Enabled).ToArray();
                SaveUserSettings("[SLOT_CONFIGURATION]");
            };
        }
        userSettings.SelectedModeKey = Pro2SlotModes.FromMode(pro2Slots[0].SelectedMode).Key;
    }

    public string MultiControllerDiagnostics => "多手柄状态（缓存；无额外 GATT 查询）\n" +
        string.Join("\n\n", pro2Slots.Where(s => s.Enabled || s.VirtualDeviceRunning || s.ActiveInput.IsRunning).Select(s =>
            s.Name + " · " + s.ConnectedAddress + " · " + s.ModeSummary + " · " + s.RateSummary + "\n" + s.DiagnosticSummary + "\n" +
            (s.PhysicalInput != null ? s.PhysicalInput.MetricsSummary : s.InputSource.ConnectionDiagnosticText + "\n" + s.InputSource.GetAdaptiveDiagnosticText(includeHistory: false)))) +
        "\n系统默认适配器不是各链路归属证明；多只手柄同时达到 133Hz 需要实测。\n";

    private IReadOnlyList<BlePeerRate> CapturePeerRates(int except) => pro2Slots.Where(s => s.Index != except)
        .Select(s => { var r = s.InputSource.GetRateEvidence(); return new BlePeerRate(s.Index,
            s.InputSource.ConnectedAddress, r.Connected, r.RecentWindow && r.LastInputAgeMs < 500, Math.Min(r.Fd2Hz, r.ParsedHz)); }).ToArray();

    private async Task OptimizeSlotCompatibilityAsync(Pro2ControllerSlot slot, CancellationToken token)
    {
        if (slot.PhysicalInput != null)
        {
            Status = slot.Status = "当前使用原生 HID 输入，不应用 Pro2 BLE 连接参数。";
            return;
        }
        await bleOperationGate.WaitAsync(token);
        try
        {
            // Eligibility is rechecked after waiting; another loop may have
            // already optimized or disconnected this very same link.
            if (!slot.InputSource.CanOptimizeConnectionCompatibility)
            {
                slot.Status = !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                    ? "Win10 参数授权在首页；这是系统全局设置，不单独修改某只手柄。"
                    : "当前无需优化、尚未连接或本轮预设已完成；保持现有输入。";
                return;
            }
            var outcome = await slot.InputSource.OptimizeConnectionCompatibilityAsync(
                new DirectProgress<string>(line => AppendLog("[PRO2_SLOT " + slot.Index + "] " + line)), token);
            slot.Status = outcome.Summary;
            slot.RefreshFromSource();
        }
        finally { bleOperationGate.Release(); }
        OnPropertyChanged(nameof(MultiControllerSummary));
    }
}
