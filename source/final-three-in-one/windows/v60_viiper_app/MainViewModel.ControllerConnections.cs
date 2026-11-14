using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim deviceListGate=new(1,1);
    private IReadOnlyList<NintendoHidDevice> discoveredControllers=Array.Empty<NintendoHidDevice>();
    private ICommand? refreshControllerDevicesCommand;
    private readonly ConcurrentDictionary<int,byte> queuedSlotOutputs=new();
    private readonly ConcurrentDictionary<int,byte> attemptedSlotOutputs=new();
    public ICommand RefreshControllerDevicesCommand => refreshControllerDevicesCommand ??= new RelayCommand(_=>RefreshControllerDevicesAsync());
    public bool PrimaryBleControlsVisible => pro2Slots[0].IsBleInputSelected && PrimaryPhysical==null;
    public async Task RefreshControllerDevicesAsync(bool wait = false)
    {
        if(previewOnly || shuttingDown) return;
        if(wait) await deviceListGate.WaitAsync();
        else if(!await deviceListGate.WaitAsync(0)) return;
        try
        {
            discoveredControllers=await Task.Run(NintendoHidDevice.Enumerate);
            ApplyDiscoveredControllers(discoveredControllers);
            AppendLog("[CONTROLLER_DISCOVERY] physical_count="+discoveredControllers.Count+" virtual_devices_excluded=true");
        }
        catch(Exception e) { Status="手柄列表刷新失败："+FirstLine(e.Message); AppendLog("[CONTROLLER_DISCOVERY] "+e); }
        finally { deviceListGate.Release(); }
    }
    private void ApplyDiscoveredControllers(IReadOnlyList<NintendoHidDevice> devices)
    {
        foreach(var slot in pro2Slots)
            slot.UpdateDeviceChoices(devices.Where(d=>!pro2Slots.Any(other=>other.Index!=slot.Index &&
                other.PhysicalInput is {IsRunning:true} source && string.Equals(source.Device.PhysicalId,d.PhysicalId,StringComparison.OrdinalIgnoreCase))));
    }
    private async Task ConnectConfiguredSlotAsync(Pro2ControllerSlot slot, CancellationToken token)
    {
        slot.LastConnectionError="";
        try { await ConnectConfiguredSlotCoreAsync(slot,token); }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
        catch(Exception error)
        {
            slot.LastConnectionError="连接未完成："+FirstLine(error.Message);
            AppendLog("[SLOT_INPUT_CONNECT] slot="+slot.Index+" requested="+slot.SelectedInputKind+" error="+error);
            throw;
        }
    }
    private async Task ConnectConfiguredSlotCoreAsync(Pro2ControllerSlot slot, CancellationToken token)
    {
        if(previewOnly) { Status="界面预览不连接实体设备。"; return; }
        attemptedSlotOutputs.TryRemove(slot.Index,out _);
        if(!slot.CanChangeConnection) throw new InvalidOperationException("此手柄正在读卡，请先取消或等待读取结束，再更换连接。");
        slot.Enabled=true;
        if(slot.SelectedInputKind==ControllerInputKind.Pro2Ble)
        {
            if(slot.PhysicalInput is { } physical)
            {
                await StopPhysicalReconnectAsync(slot);
                await physical.DisposeAsync(); slot.PhysicalInput=null;
                lock(autoSleepStates) autoSleepStates.Remove(slot.Index);
                slot.IsSleeping=false; slot.SleepStatus="";
            }
            if(slot.Index==1) { manualReconnectTarget=null; await EnterGameAsync(token); }
            else await StartPro2SlotAutoReconnectAsync(slot,token);
        }
        else
        {
            await RefreshControllerDevicesAsync(wait:true);
            var selected=slot.PhysicalDeviceChoices.FirstOrDefault(d=>string.Equals(d.PhysicalId,slot.SelectedPhysicalId,StringComparison.OrdinalIgnoreCase));
            if(selected==null) throw new InvalidOperationException("未找到所选的 "+slot.InputChoiceLabel+" 手柄。检查连接后刷新列表，再选择实体手柄。");
            await ConnectPhysicalInputCoreAsync(slot,selected,token);
        }
        slot.RefreshFromSource(); RefreshCompactPresentation();
    }
    private void OnInputConfigurationChanged(Pro2ControllerSlot slot)
    {
        userSettings.SlotInputs=pro2Slots.Select(s=>s.InputConfiguration).ToArray();
        SaveUserSettings("[INPUT_CONFIGURATION]");
        OnPropertyChanged(nameof(PrimaryBleControlsVisible)); RefreshCompactPresentation();
    }
    public Task InitializeControllerChoicesAsync() => pro2Slots.Any(s=>s.IsPhysicalInputSelected) ? RefreshControllerDevicesAsync() : Task.CompletedTask;
    private async Task ConnectEnabledControllerSlotsAsync(CancellationToken token)
    {
        foreach(var slot in pro2Slots.Where(s=>s.Enabled))
        {
            try { await ConnectConfiguredSlotAsync(slot,token); }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
            catch(Exception e) { slot.Status="连接失败："+FirstLine(e.Message); AppendLog("[STARTUP_INPUT] slot="+slot.Index+" "+e); }
        }
    }
    private void QueueSlotOutputResume(Pro2ControllerSlot slot)
    {
        if(previewOnly || slot.VirtualDeviceRunning || attemptedSlotOutputs.ContainsKey(slot.Index) || !slot.InputSource.IsGameplayReady || !queuedSlotOutputs.TryAdd(slot.Index,0)) return;
        async Task Resume()
        {
            try
            {
                await RunExclusiveAsync("接入手柄 "+slot.Index+" 游戏输出",async token=>
                {
                    if(!slot.Enabled || !slot.AutoReconnectEnabled || slot.PhysicalInput!=null || !slot.IsBleInputSelected || !slot.InputSource.IsGameplayReady) return;
                    if(!attemptedSlotOutputs.TryAdd(slot.Index,0)) return;
                    await EnsureViiperReadyAsync(token);
                    if(await StartSingleSlotSessionAsync(slot,RuntimeProfileForSlot(slot),token))
                    {
                        Running=true; SetActiveMode(pro2Slots[0].SelectedMode);
                        if(HasPs5VirtualOutput) await ApplyDualSenseAudioGuardAsync(token);
                        slot.RefreshFromSource(); RefreshCompactPresentation();
                    }
                });
            }
            finally { queuedSlotOutputs.TryRemove(slot.Index,out _); }
        }
        if(uiContext!=null) uiContext.Post(async _=>await Resume(),null); else _=Task.Run(Resume);
    }
}
