using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private Pro2ControllerSlot? selectedCalibrationSlot;
    private ICommand? calibrateGyroSlotCommand;
    public ICommand CalibrateGyroSlotCommand => calibrateGyroSlotCommand ??= new RelayCommand(parameter=>
        RunExclusiveAsync("校准所选手柄陀螺仪",token=>SlotFromParameter(parameter) is { } slot ? CalibrateSelectedGyroAsync(slot,token) : Task.CompletedTask));
    public Pro2ControllerSlot SelectedCalibrationSlot
    {
        get => selectedCalibrationSlot ?? pro2Slots[0];
        set
        {
            if (value == null || !pro2Slots.Contains(value)) return;
            selectedCalibrationSlot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationStatusText));
            OnPropertyChanged(nameof(CanCalibrateSelectedInput));
        }
    }
    public bool CanCalibrateSelectedInput => CanOperate && CalibrationInputIsLive(SelectedCalibrationSlot);
    public string CalibrationStatusText
    {
        get
        {
            var slot = SelectedCalibrationSlot;
            string transport = slot.PhysicalInput?.Device.Kind.ToString() ?? "Pro2 BLE";
            return slot.Name + " · " + transport +
                (CalibrationInputIsLive(slot) ? " · 输入在线" : " · 请先连接手柄") + "\n" +
                slot.StickCalibrationStatus + "\n" + slot.GyroCalibrationStatus;
        }
    }
    private static bool CalibrationInputIsLive(Pro2ControllerSlot slot) =>
        slot.ActiveInput.IsRunning && slot.ActiveInput.TryGetLatest(out _, out var age) && age.TotalMilliseconds < 500;

    private async Task CalibratePhysicalStickAsync(Pro2ControllerSlot slot, bool range, CancellationToken token)
    {
        var source = slot.PhysicalInput!;
        string label = range ? "完整行程" : "零位";
        if (!CalibrationInputIsLive(slot))
        {
            slot.StickCalibrationStatus = "摇杆校准：输入离线或暂停，未开始采样。";
            Status = slot.Name + " 请先连接手柄。";
            return;
        }
        var previous = source.StickCalibrationProfile;
        if (range && !previous.CenterCalibrated)
        {
            slot.StickCalibrationStatus = "摇杆校准：请先完成零位校准。";
            Status = slot.Name + " 请先完成零位校准。";
            return;
        }
        string identity = source.Device.PhysicalId;
        bool committed = false;
        string started = source.StartStickCalibration(range);
        AppendLog($"[PHYSICAL_STICK_CAL] slot={slot.Index} kind={source.Device.Kind} identity=\"{identity}\" {started}");
        if (!started.Contains("status=started", StringComparison.Ordinal))
        {
            slot.StickCalibrationStatus = "摇杆校准未启动：" + started;
            return;
        }
        try
        {
            slot.StickCalibrationStatus = range ? "左右摇杆沿外圈各转动数圈，覆盖上下左右。" : "完全松开左右摇杆，保持两秒。";
            await WaitForStickCalibrationAsync(slot, range ? "沿外圈转动双摇杆 · 行程" : "松开双摇杆 · 零位", range ? 8 : 2, token);
            await Task.Delay(200, token);
            var result = source.CompleteStickCalibration(range);
            if (!ReferenceEquals(slot.PhysicalInput, source) || !CalibrationInputIsLive(slot))
            {
                slot.StickCalibrationStatus = "摇杆校准中断：手柄离线或连接已更换，旧配置保留。";
                AppendLog($"[PHYSICAL_STICK_CAL] slot={slot.Index} status=rejected reason=source_changed_or_offline");
            }
            else if (!result.Success || result.Profile == null)
            {
                slot.StickCalibrationStatus = "摇杆校准未通过：" + StickCalibrationFailureText(result.Message);
                AppendLog($"[PHYSICAL_STICK_CAL] slot={slot.Index} {result.Message}");
            }
            else
            {
                bool hadPrevious = userSettings.PhysicalStickCalibrations.TryGetValue(identity, out var saved);
                userSettings.PhysicalStickCalibrations[identity] = result.Profile;
                try { if (!previewOnly) userSettings.Save(); }
                catch (Exception error)
                {
                    if (hadPrevious) userSettings.PhysicalStickCalibrations[identity] = saved!;
                    else userSettings.PhysicalStickCalibrations.Remove(identity);
                    slot.StickCalibrationStatus = "校准保存失败，已恢复旧配置：" + FirstLine(error.Message);
                    AppendLog($"[PHYSICAL_STICK_CAL] slot={slot.Index} status=rejected reason=settings_save_failed error={FirstLine(error.Message)}");
                    Status = slot.StickCalibrationStatus;
                    return;
                }
                committed = true;
                slot.StickCalibrationStatus = result.Profile.RangeCalibrated ? "摇杆零位与行程已保存，立即生效。" : "摇杆零位已保存；可继续校准行程。";
                AppendLog($"[PHYSICAL_STICK_CAL] slot={slot.Index} {result.Message} persist=physical_identity applied=true");
            }
            Status = slot.Name + " · " + slot.StickCalibrationStatus;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            slot.StickCalibrationStatus = "校准已取消，旧配置保留。";
            throw;
        }
        finally
        {
            source.CancelStickCalibration();
            if (!committed) source.SetStickCalibration(previous);
            OnPropertyChanged(nameof(CalibrationStatusText));
        }
    }

    private async Task CalibrateSelectedGyroAsync(Pro2ControllerSlot slot, CancellationToken token)
    {
        if (!CalibrationInputIsLive(slot))
        {
            Status = slot.GyroCalibrationStatus = "陀螺仪校准：请先连接手柄。";
            return;
        }
        var physical = slot.PhysicalInput;
        var ble = slot.InputSource;
        string address = ble.ConnectedAddress;
        string started = physical?.StartGyroCalibration() ?? ble.StartManualGyroCalibration();
        AppendLog($"[INPUT_GYRO_CAL] slot={slot.Index} transport={physical?.Device.Kind.ToString() ?? "BLE"} {started}");
        try
        {
            for (int remaining = 3; remaining > 0; remaining--)
            {
                Status = slot.GyroCalibrationStatus = "请将手柄静置平面 · 陀螺仪采样剩余 " + remaining + " 秒。";
                await Task.Delay(1000, token);
            }
            await Task.Delay(500, token);
            string summary = physical?.GyroCalibrationSummary ?? ble.GyroCalibrationSummary;
            bool sameInput = ReferenceEquals(slot.PhysicalInput, physical) &&
                (physical != null || string.Equals(address, ble.ConnectedAddress, StringComparison.OrdinalIgnoreCase));
            bool complete = sameInput && CalibrationInputIsLive(slot) &&
                summary.Contains("status=calibrated", StringComparison.Ordinal) &&
                summary.Contains(Pro2QualityMode.GyroEnabled || physical != null ? "result=manual_committed_3s" : "result=manual_committed", StringComparison.Ordinal);
            slot.GyroCalibrationStatus = complete ? "陀螺仪校准完成，当前连接已应用。" :
                !sameInput || !CalibrationInputIsLive(slot) ? "陀螺仪校准中断：输入离线或已更换，请重新连接后重试。" :
                "陀螺仪校准未通过：请松开按键和摇杆，静置稳定平面后重试。";
            Status = slot.Name + " · " + slot.GyroCalibrationStatus;
            AppendLog($"[INPUT_GYRO_CAL] slot={slot.Index} complete={complete} same_input={sameInput} {summary}");
        }
        finally
        {
            physical?.CancelGyroCalibration();
            OnPropertyChanged(nameof(CalibrationStatusText));
        }
    }
}
