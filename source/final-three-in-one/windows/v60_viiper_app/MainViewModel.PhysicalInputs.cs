using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private NintendoPhysicalInputSource? PrimaryPhysical => pro2Slots[0].PhysicalInput;
    public bool CanProbePhysicalInputs => !previewOnly;
    public string PrimaryInputLabel => PrimaryPhysical == null ? "真实 FD2 输入" : "真实 HID 输入 · " + PrimaryPhysical.Device.Kind;
    public string PhysicalInputsReport => string.Join("\n\n",pro2Slots.Where(s=>s.PhysicalInput!=null)
        .Select(s=>s.Name+" · "+s.PhysicalInput!.Device.Label+"\n"+s.PhysicalInput.MetricsSummary));

    public Task ConnectPhysicalInputAsync(int slotIndex,NintendoHidDevice device) => RunExclusiveAsync("连接实体手柄",token=>
        ConnectPhysicalInputCoreAsync(pro2Slots[Math.Clamp(slotIndex-1,0,3)],device,token));
    private async Task ConnectPhysicalInputCoreAsync(Pro2ControllerSlot slot,NintendoHidDevice device,CancellationToken token)
    {
        if(previewOnly) { Status="界面预览不连接真实设备"; return; }
        if(!slot.CanChangeConnection) throw new InvalidOperationException("此手柄正在读卡，请先取消或等待读取结束。");
        slot.SelectedInputKind=ControllerInputConfiguration.FromPhysical(device.Kind);
        slot.SelectedPhysicalDevice=device;
        foreach(var other in pro2Slots.Where(s=>s.Index!=slot.Index && string.Equals(s.PhysicalInput?.Device.PhysicalId,device.PhysicalId,StringComparison.OrdinalIgnoreCase)))
        {
            if(other.PhysicalInput!.IsRunning || other.PhysicalReconnectTask is {IsCompleted:false})
                throw new IOException("这只手柄已分配到其他槽位。先断开原槽位，再重新选择。");
            other.PhysicalInput=null; other.Status="实体手柄已移至 "+slot.Name+"。"; other.RefreshFromSource();
        }
        await StopPhysicalReconnectAsync(slot);
        if(slot.Index==1) await StopPrimaryBleReconnectOnlyAsync(); else await slot.StopAutoReconnectAsync();
        await bleOperationGate.WaitAsync(token);
        try
        {
            await slot.InputSource.StopAsync();
            lock(autoSleepStates) autoSleepStates.Remove(slot.Index);
            slot.Enabled=true; slot.IsSleeping=false; slot.SleepStatus="此输入使用原生 HID，不发送 Pro2 BLE 休眠命令";
            bool sameLiveInput = slot.PhysicalInput is { IsRunning:true } current &&
                string.Equals(current.Device.PhysicalId, device.PhysicalId, StringComparison.OrdinalIgnoreCase) &&
                current.Snapshot().AgeMs < 1000;
            if (!sameLiveInput)
            {
                if(slot.PhysicalInput!=null) await slot.PhysicalInput.DisposeAsync();
                var source=CreatePhysicalSource(device,slot.Index); slot.PhysicalInput=source;
                try { await source.ConnectAsync(token); }
                catch { slot.RefreshFromSource(); RefreshCompactPresentation(); throw; }
            }
        }
        finally { bleOperationGate.Release(); }
        StartPhysicalReconnect(slot);
        // A router keeps an existing virtual device attached across input swaps.
        await EnsureViiperReadyAsync(token);
        if(!await StartSingleSlotSessionAsync(slot,RuntimeProfileForSlot(slot),token))
            throw new IOException("实体输入已接入，虚拟输出尚未就绪；修复 USBIP 后在此窗口重新连接。");
        Running=true; SetActiveMode(pro2Slots[0].SelectedMode);
        if(slot.Session?.Profile.IsPs5Family==true) await ApplyDualSenseAudioGuardAsync(token);
        StartPhysicalReconnect(slot);
        slot.RefreshFromSource(); RefreshCompactPresentation();
        MarkStartupAutomationConnected("physical_slot_"+slot.Index);
        Status=slot.Name+" 已连接 · "+device.Kind+" · 真实频率以实测为准。";
    }

    private NintendoPhysicalInputSource CreatePhysicalSource(NintendoHidDevice device,int slot)
    {
        var source=new NintendoPhysicalInputSource(device,line=>AppendLog("[SLOT "+slot+"] "+line));
        source.SetDeadzones(userSettings.StickDeadzones); source.SetRumbleGain(rumbleMultiplier);
        pro2Slots[slot-1].GyroCalibrationStatus="陀螺仪：当前连接可执行三秒静置校准。";
        if (userSettings.PhysicalStickCalibrations.TryGetValue(device.PhysicalId, out var profile))
        {
            source.SetStickCalibration(profile);
            pro2Slots[slot-1].StickCalibrationStatus = "摇杆校准：已加载本实体手柄的零位" + (profile.RangeCalibrated ? "与行程。" : "。可继续校准行程。");
        }
        else pro2Slots[slot-1].StickCalibrationStatus = "摇杆校准：尚未保存手动校准，可先校准零位。";
        return source;
    }
    private async Task StopPrimaryBleReconnectOnlyAsync()
    {
        var cts=autoReconnectCts; var task=autoReconnectTask;
        autoReconnectCts=null; autoReconnectTask=null; AutoReconnectEnabled=false;
        cts?.Cancel();
        if(task!=null) try { await task; } catch(OperationCanceledException) { }
        cts?.Dispose();
        await pro2Slots[0].StopAutoReconnectAsync();
    }
    public Task DisconnectPhysicalInputAsync(int slotIndex)=>RunExclusiveAsync("断开实体输入",token=>
        DisconnectPro2SlotAsync(pro2Slots[Math.Clamp(slotIndex-1,0,3)],token));
    public Task UseBleInputAsync(int slotIndex)=>RunExclusiveAsync("使用 Pro2 BLE",async token=>
    {
        var slot=pro2Slots[Math.Clamp(slotIndex-1,0,3)];
        if(!slot.CanChangeConnection) throw new InvalidOperationException("此手柄正在读卡，请先取消或等待读取结束。");
        await StopPhysicalReconnectAsync(slot);
        if(slot.PhysicalInput is { } source) await source.DisposeAsync();
        slot.PhysicalInput=null; slot.Enabled=true; slot.SelectedInputKind=ControllerInputKind.Pro2Ble;
        if(slot.Index==1) await EnterGameAsync(token); else await StartPro2SlotAutoReconnectAsync(slot,token);
    });
    public Task<NintendoNfcTag> ReadPhysicalNfcAsync(int slotIndex,CancellationToken token)
    {
        var source=pro2Slots[Math.Clamp(slotIndex-1,0,3)].PhysicalInput;
        return source!=null ? source.ReadNfcAsync(token) : throw new NotSupportedException("请先接入 Pro1 蓝牙实体手柄；当前 BLE 输入没有已验证的 NFC 读取协议。");
    }
    private async Task EnterPhysicalGameAsync(CancellationToken token)
    {
        foreach(var slot in pro2Slots.Where(s=>s.Enabled))
        {
            try
            {
            if(slot.IsPhysicalInputSelected && slot.PhysicalInput==null) await ConnectConfiguredSlotAsync(slot,token);
            else if(slot.PhysicalInput != null) await StartPhysicalSlotAsync(slot,token);
            else if(slot.InputSource.IsRunning)
            {
                await EnsureViiperReadyAsync(token);
                await StartSingleSlotSessionAsync(slot,RuntimeProfileForSlot(slot),token);
            }
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
            catch(Exception e) { slot.Status="连接失败："+FirstLine(e.Message); AppendLog("[SLOT_CONNECT] slot="+slot.Index+" "+e); }
        }
        Running=pro2Slots.Any(s=>s.VirtualDeviceRunning);
        if(Running) SetActiveMode(pro2Slots[0].SelectedMode);
        if(Running) foreach(var slot in pro2Slots.Where(s=>s.Enabled && s.PhysicalInput==null))
            await StartPro2SlotAutoReconnectAsync(slot,token);
        if(HasPs5VirtualOutput) await ApplyDualSenseAudioGuardAsync(token);
        RefreshCompactPresentation();
    }
    private async Task StartPhysicalSlotAsync(Pro2ControllerSlot slot, CancellationToken token)
    {
        await StopPhysicalReconnectAsync(slot);
        await bleOperationGate.WaitAsync(token);
        try
        {
            if (slot.PhysicalInput is not { } source) return;
            if (!source.IsRunning || source.Snapshot().AgeMs >= 1000)
            {
                string identity=source.Device.PhysicalId;
                await source.DisposeAsync();
                var devices=await Task.Run(NintendoHidDevice.Enumerate,token);
                var found=devices.FirstOrDefault(d=>string.Equals(d.PhysicalId,identity,StringComparison.OrdinalIgnoreCase));
                if(found==null) throw new IOException("未找到之前的实体手柄。请检查 USB 数据线／Pro1 蓝牙连接；如更换设备，请在“输入 / NFC”重新选择。");
                var replacement=CreatePhysicalSource(found,slot.Index);
                slot.PhysicalInput=replacement;
                await replacement.ConnectAsync(token);
            }
        }
        finally { bleOperationGate.Release(); slot.RefreshFromSource(); RefreshCompactPresentation(); }
        slot.Enabled=true;
        StartPhysicalReconnect(slot);
        await EnsureViiperReadyAsync(token);
        if(!await StartSingleSlotSessionAsync(slot,RuntimeProfileForSlot(slot),token))
            throw new IOException("实体输入已连接，虚拟输出尚未就绪。请修复 USBIP 后点击进入游戏重试。");
        Running=pro2Slots.Any(s=>s.VirtualDeviceRunning);
        if(slot.Index==1) SetActiveMode(slot.SelectedMode);
        if(HasPs5VirtualOutput) await ApplyDualSenseAudioGuardAsync(token);
        slot.RefreshFromSource(); RefreshCompactPresentation();
    }
    private void StartPhysicalReconnect(Pro2ControllerSlot slot)
    {
        if(slot.PhysicalReconnectTask is {IsCompleted:false} || slot.PhysicalInput==null) return;
        slot.PhysicalReconnectCts?.Dispose();
        var cts=CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
        slot.PhysicalReconnectCts=cts;
        slot.PhysicalReconnectTask=MonitorPhysicalInputAsync(slot,cts.Token);
    }
    private async Task MonitorPhysicalInputAsync(Pro2ControllerSlot slot,CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested && slot.PhysicalInput is { } source)
            {
                await Task.Delay(2000,token);
                var health=source.Snapshot();
                if(health.Running && (health.AgeMs<2000 || source.NfcStatus=="读取中")) continue;
                if(!slot.Enabled) break;
                slot.Status="实体输入已离线，等待同一设备返回；虚拟手柄保持在线。";
                var identity=source.Device.PhysicalId;
                await source.DisposeAsync();
                var devices=await Task.Run(NintendoHidDevice.Enumerate,token);
                var found=devices.FirstOrDefault(d=>string.Equals(d.PhysicalId,identity,StringComparison.OrdinalIgnoreCase));
                if(found==null) continue;
                var replacement=CreatePhysicalSource(found,slot.Index);
                try
                {
                    await replacement.ConnectAsync(token);
                    if(!ReferenceEquals(slot.PhysicalInput,source)) { await replacement.DisposeAsync(); break; }
                    slot.PhysicalInput=replacement;
                    slot.LastConnectionError="";
                    AppendLog("[PHYSICAL_RECONNECT] slot="+slot.Index+" identity_match=true virtual_device_recreated=false");
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested) { await replacement.DisposeAsync(); throw; }
                catch(Exception e) { await replacement.DisposeAsync(); AppendLog("[PHYSICAL_RECONNECT] slot="+slot.Index+" retry="+e.Message); }
                slot.RefreshFromSource();
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception e) { AppendLog("[PHYSICAL_MONITOR_ERROR] slot="+slot.Index+" "+e); }
    }
    private static async Task StopPhysicalReconnectAsync(Pro2ControllerSlot slot)
    {
        var cts=slot.PhysicalReconnectCts; var task=slot.PhysicalReconnectTask;
        slot.PhysicalReconnectCts=null; slot.PhysicalReconnectTask=null;
        cts?.Cancel();
        if(task!=null) try { await task; } catch(OperationCanceledException) { }
        cts?.Dispose();
    }
    private void RefreshPhysicalCompactTelemetry(NintendoPhysicalInputSource physical)
    {
        var health=physical.Snapshot();
        compactInputRate=health.Running&&health.AgeMs<1000?health.Hz.ToString("0.0"):"—";
        compactRawRate=compactParsedRate=compactInputRate+" Hz";
        compactInputAge=health.Running?health.AgeMs.ToString("0")+" ms":"—";
        bool live=physical.TryGetLatest(out var state,out var age)&&age.TotalSeconds<2;
        compactBattery=state.BatteryPercent==GamepadState.BatteryUnknown?"电量未知":state.BatteryPercent+"%（档位）";
        if(!physical.Device.IsPro1) compactBattery=state.BatteryVoltageMillivolts>0?(state.BatteryVoltageMillivolts/1000.0).ToString("0.00")+" V":"电量未知";
        compactBatteryVisual=BatteryVisual.Create(state,health.Running,state.BatteryPercent!=GamepadState.BatteryUnknown,live,"原生 HID 报告");
        if(physical.Device.IsPro1 && state.BatteryPercent!=GamepadState.BatteryUnknown)
        {
            var visualState=state.Clone(); visualState.BatteryLevel=(byte)Math.Round(state.BatteryPercent*.09);
            compactBatteryVisual=BatteryVisual.Create(visualState,health.Running,true,live,"Pro1 原生档位") with {Level=state.BatteryLevel+" / 4"};
        }
        foreach(string name in new[]{nameof(PrimaryInputLabel),nameof(CompactInputRate),nameof(CompactRawRate),nameof(CompactParsedRate),
            nameof(CompactBattery),nameof(CompactBatteryVisual),nameof(CompactBatteryHint),nameof(CompactInputAge),nameof(CompactFirmware),
            nameof(CompactFirmwareHint),nameof(CompactHeadset),nameof(CompactHapticSource),nameof(CompactActualMode),nameof(EnvironmentReportText)}) OnPropertyChanged(name);
        RefreshCompactPresentation(); FlushCompactLog();
    }
}
