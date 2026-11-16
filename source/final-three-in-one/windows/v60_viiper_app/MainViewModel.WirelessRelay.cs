using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    public bool WirelessRelayModeActive {get;private set;}
    public async Task<bool> EnterWirelessRelayModeAsync()
    {
        if(shuttingDown||WirelessRelayModeActive||!await operationGate.WaitAsync(0))return false;
        Busy=true;
        try
        {
            if(pro2Slots.Any(slot=>!slot.CanChangeConnection)){Status="请先完成或取消 NFC 读取，再打开蓝牙转发。";return false;}
            await CancelStartupAutomationForManualOperationAsync("蓝牙转发");
            if(!previewOnly)
            {
                await StopAsync();
                foreach(var slot in pro2Slots)
                {
                    await DisconnectPro2SlotAsync(slot,lifetimeCts.Token);
                    slot.PhysicalInput=null;slot.RefreshFromSource();
                }
            }
            WirelessRelayModeActive=true;
            Status="蓝牙转发已接管输入；关闭转发窗口后可恢复本机游玩。";
            AppendLog("[WIRELESS_RELAY_MODE] active=true local_outputs_stopped=true reconnect_paused=true driver_changes=0");
            return true;
        }
        finally{Busy=false;operationGate.Release();}
    }
    public void ExitWirelessRelayMode()
    {
        WirelessRelayModeActive=false;
        // Do not reconnect automatically after restoring a radio: the receiver
        // may still hold its old pairing entry. Local gameplay is explicit.
        Status="蓝牙转发已退出，可重新连接手柄在本机游玩。";
        OnPropertyChanged(nameof(CanStart));OnPropertyChanged(nameof(CanStop));OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanManageRuntime));RefreshCompactPresentation();
        AppendLog("[WIRELESS_RELAY_MODE] active=false automatic_local_reconnect=false");
    }
}
