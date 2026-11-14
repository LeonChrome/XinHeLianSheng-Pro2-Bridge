using System.Diagnostics;
using System.ComponentModel;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private readonly BleHostResumeGate hostResumeGate=new();
    public void NotifyHostPowerMode(bool suspended)
    {
        if(previewOnly||shuttingDown)return;
        if(suspended)
        {
            hostResumeGate.Suspend();
            foreach(var slot in pro2Slots)slot.InputSource.CancelConnectionAttemptForHostSuspend();
        }
        else hostResumeGate.Resume();
        AppendLog("[BLE_HOST_POWER] state="+(suspended?"suspend":"resume")+" connection_settle_ms="+(suspended?0:3000)+
            " adapter_restart=false healthy_sessions_preserved=true native_cancel_supported=false");
    }
    public bool CanOfferBluetoothRecovery=>ConnectionWarningSlot is { } slot&&slot.InputSource.ConnectionDiagnosis.Code is
        "unreachable" or "gatt_draining" or "gatt_stalled" or "timeout";
    public async Task<BluetoothRecoveryLaunch?> PrepareBluetoothRecoveryAsync(
        Func<IReadOnlyList<BluetoothRestartTarget>,BluetoothRestartTarget?> confirm)
    {
        BluetoothRecoveryLaunch? launch=null;
        await RunExclusiveAsync("恢复蓝牙连接",async token=>
        {
            if(previewOnly){SetBluetoothRestartStatus("界面预览：未重新初始化蓝牙。");return;}
            // Cached enumeration stays usable when the WinRT stack is wedged.
            if(environmentSnapshot==null)await RefreshEnvironmentInfoAsync();
            if(environmentSnapshot==null)throw new InvalidOperationException("蓝牙适配器信息未获取。");
            var target=confirm(BluetoothRestartPolicy.Targets(environmentSnapshot));
            if(target==null)return;
            if(!target.CanRestart)throw new InvalidOperationException("未核实蓝牙适配器，未执行恢复。");
            int mask=pro2Slots.Where(s=>s.Enabled&&(s.Index==1||s.ActiveInput.IsRunning||s.AutoReconnectEnabled||s.VirtualDeviceRunning))
                .Aggregate(0,(value,slot)=>value|(1<<(slot.Index-1)));
            if(mask==0)mask=1;
            string op=Guid.NewGuid().ToString("N");
            var proposed=new BluetoothRecoveryLaunch(op,target.PnpId,target.InterfaceId,mask);
            using var parent=Process.GetCurrentProcess();
            var info=new ProcessStartInfo(Environment.ProcessPath??throw new InvalidOperationException("助手路径未获取。"))
                {UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            foreach(var arg in BluetoothAdapterRestart.ReinitializeArguments(target,op,BluetoothRecoveryLaunch.ParentIdentity(parent)))info.ArgumentList.Add(arg);
            AppendLog("[BLE_RECOVERY_REQUEST] operation="+op+" pnp_id=\""+target.PnpId+"\" mask="+mask+
                " selected_by_user=true wait_parent_exit=true driver_replacement=false pairing_delete=false");
            try
            {
                using var child=await Task.Run(()=>Process.Start(info),token);
                if(child==null)throw new InvalidOperationException("管理员恢复进程未启动。");
                // The helper waits for this process to exit. Cancelling UAC above
                // must leave the current controller sessions untouched.
                launch=proposed;SetBluetoothRestartStatus("正在退出旧连接进程，随后恢复所选蓝牙并重新连接…");
            }
            catch(Win32Exception error)when(error.NativeErrorCode==1223){SetBluetoothRestartStatus("已取消授权，未重新初始化蓝牙；可重新点击连接。");}
        });
        return launch;
    }
    private async Task CompleteBluetoothRecoveryAsync(BluetoothRecoveryLaunch launch,BluetoothRestartResult result)
    {
        App.RecoveryLaunch=null;App.RecoveryResult=null;
        bluetoothRestartDiagnostic=result.Telemetry+"\n"+result.Message+"\n日志："+BluetoothAdapterRestart.ResultPath(launch.Operation)+"\n"+result.NativeOutput;
        AppendLog(bluetoothRestartDiagnostic);
        if(!result.Verified)
        {
            SetBluetoothRestartStatus(result.RebootRequired?"Windows 仍要求重启，蓝牙恢复未确认；没有自动重连或重启系统。":result.Message);
            return;
        }
        SetBluetoothRestartStatus("所选蓝牙已重新启用，正在按原槽位恢复；真实输入和频率将重新验收。");
        foreach(var slot in pro2Slots)slot.Enabled=(launch.SlotMask&(1<<(slot.Index-1)))!=0;
        startupAutomationActive=true;startupAutomationPaused=false;startupAutomationNoticeSent=false;
        startupAutomationDeadlineUtc=DateTimeOffset.UtcNow.AddMinutes(5);
        await RunExclusiveAsync("蓝牙恢复后重新连接",ConnectEnabledControllerSlotsAsync,isStartupAutomationOperation:true);
    }
}
