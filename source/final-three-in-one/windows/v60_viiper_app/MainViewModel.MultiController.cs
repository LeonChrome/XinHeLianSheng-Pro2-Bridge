using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private ViiperDeviceProfile? multiRuntimeProfile;
    public string MultiControllerSummary => $"已连接 {pro2Slots.Count(s => s.InputSource.IsRunning)} / 4 · 休眠 {pro2Slots.Count(s => s.IsSleeping)} · 每只独立输入、震动与重连";
    private ICommand? removeSlotCommand, optimizeSlotCommand;
    public ICommand RemovePro2SlotCommand => removeSlotCommand ??= new RelayCommand(parameter =>
        RunExclusiveAsync("移除手柄", async token =>
        {
            var slot = SlotFromParameter(parameter);
            if (slot == null || slot.Index == 1) return;
            await DisconnectPro2SlotAsync(slot, token);
            lock (autoSleepStates) autoSleepStates.Remove(slot.Index);
            slot.IsSleeping = false;
            slot.SleepStatus = "";
            slot.Enabled = false;
            var previous = slot.Session; slot.Session = null;
            if (previous != null) await previous.DisposeAsync();
            slot.VirtualDeviceRunning = false;
            slot.Status = "已移除；其他手柄保持连接。";
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
        foreach (var slot in pro2Slots)
        {
            slot.Enabled = enabled[slot.Index - 1];
            slot.InputSource.PeerRatesProvider = () => CapturePeerRates(slot.Index);
            slot.PropertyChanged += (_, e) =>
            {
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
    }

    public string MultiControllerDiagnostics => "多手柄状态（缓存；无额外 GATT 查询）\n" +
        string.Join("\n\n", pro2Slots.Where(s => s.Enabled || s.VirtualDeviceRunning || s.InputSource.IsRunning).Select(s =>
            s.Name + " · " + s.ConnectedAddress + " · " + s.RateSummary + "\n" + s.DiagnosticSummary + "\n" +
            s.InputSource.ConnectionDiagnosticText + "\n" + s.InputSource.GetAdaptiveDiagnosticText(includeHistory: false))) +
        "\n系统默认适配器不是各链路归属证明；多只手柄同时达到 133Hz 需要实测。\n";

    private IReadOnlyList<BlePeerRate> CapturePeerRates(int except) => pro2Slots.Where(s => s.Index != except)
        .Select(s => { var r = s.InputSource.GetRateEvidence(); return new BlePeerRate(s.Index,
            s.InputSource.ConnectedAddress, r.Connected, r.RecentWindow && r.LastInputAgeMs < 500, Math.Min(r.Fd2Hz, r.ParsedHz)); }).ToArray();

    private async Task OptimizeSlotCompatibilityAsync(Pro2ControllerSlot slot, CancellationToken token)
    {
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
