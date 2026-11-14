using System.Diagnostics;

namespace Y700Switch2V60Viiper;

public sealed partial class ViiperBridgeSession
{
    private Task? attachmentTask;
    private readonly SemaphoreSlim attachmentRepairGate=new(1,1);
    private string configurationRepairDiagnostic="";
    public string ConfigurationRepairDiagnostic=>Volatile.Read(ref configurationRepairDiagnostic);
    private int configurationRepairNeedsReboot;
    public bool ConfigurationRepairNeedsReboot=>Volatile.Read(ref configurationRepairNeedsReboot)!=0;
    private VirtualOutputHealth outputHealth = new(VirtualOutputState.Starting, "正在检查虚拟手柄", "等待 USBIP 与 Windows 输入接口就绪");
    public VirtualOutputHealth OutputHealth => Volatile.Read(ref outputHealth);
    private void SetOutputHealth(VirtualOutputHealth health)
    {
        if (cts.IsCancellationRequested && health.State is not (VirtualOutputState.Failed or VirtualOutputState.Stopped)) return;
        var old = Interlocked.Exchange(ref outputHealth, health);
        if (old != health)
            progress.Report($"[USBIP_HEALTH] state={health.State} repairs={health.Repairs} problem={health.Problem} instance=\"{health.InstanceId}\" bus={device?.BusId} dev={device?.DevId} detail={health.Detail.Replace('\r', ' ').Replace('\n', ' ')}");
    }

    private async Task MonitorAttachmentAsync(CancellationToken token)
    {
        var policy = new UsbipRecoveryPolicy();
        var watch = Stopwatch.StartNew();
        if (!IsLocalHost(client.Host))
        { SetOutputHealth(new(VirtualOutputState.Unverified, "远程输出状态待确认", "仅本机虚拟 USB 支持自动检查与恢复")); return; }
        while (!token.IsCancellationRequested)
        {
            bool gateHeld=false;
            try
            {
                await attachmentRepairGate.WaitAsync(token).ConfigureAwait(false);gateHeld=true;
                var target = device;
                if (target == null) return;
                var observed = await ControllerEnumerationDiagnostics.ProbeAttachmentAsync(target, client.Port, token).ConfigureAwait(false);
                bool repair = policy.Observe(observed.State, watch.Elapsed);
                if (token.IsCancellationRequested) return;
                var state = observed.State switch {
                    UsbipAttachmentState.Ready => GetStream() == null ? VirtualOutputState.Recovering : VirtualOutputState.Ready,
                    UsbipAttachmentState.Unknown => VirtualOutputState.Unverified,
                    UsbipAttachmentState.Starting => VirtualOutputState.Starting,
                    UsbipAttachmentState.ConfigurationError => VirtualOutputState.Failed,
                    _ => VirtualOutputState.Recovering
                };
                if (state != VirtualOutputState.Ready && policy.Attempts >= UsbipRecoveryPolicy.MaximumAttempts && !repair &&
                    observed.State != UsbipAttachmentState.Unknown) state = VirtualOutputState.Failed;
                string summary = observed.State==UsbipAttachmentState.ConfigurationError ? "虚拟手柄配置异常（代码 19）" : state switch {
                    VirtualOutputState.Ready => "虚拟手柄已就绪",
                    VirtualOutputState.Unverified => "虚拟手柄状态待确认",
                    VirtualOutputState.Starting => "Windows 正在识别虚拟手柄",
                    VirtualOutputState.Failed => "虚拟输出未就绪，请停止后重新进入游戏",
                    _ => "正在恢复虚拟手柄"
                };
                if(observed.State==UsbipAttachmentState.Ready)Volatile.Write(ref configurationRepairNeedsReboot,0);
                if(observed.State==UsbipAttachmentState.ConfigurationError&&ConfigurationRepairNeedsReboot)summary="设备重建需要重启 Windows";
                string detail=observed.Detail+(observed.State==UsbipAttachmentState.ConfigurationError&&ConfigurationRepairDiagnostic.Length>0 ? "\n"+ConfigurationRepairDiagnostic : "");
                SetOutputHealth(new(state, summary, detail, policy.Attempts,observed.Problem,observed.InstanceId));
                if (repair)
                {
                    // Do not recreate the export, clear other slots, or touch the physical BLE link.
                    var exports = await client.BusDevicesAsync(target.BusId, token).ConfigureAwait(false);
                    if (!exports.Any(d => d.DevId == target.DevId && profile.MatchesIdentity(d)))
                    {
                        SetOutputHealth(new(VirtualOutputState.Failed, "虚拟设备已丢失，请停止后重新进入游戏", "VIIPER 导出不存在；未反复执行 attach", policy.Attempts));
                        return;
                    }
                    SetOutputHealth(new(VirtualOutputState.Recovering, "正在重新挂载虚拟手柄", $"第 {policy.Attempts}/{UsbipRecoveryPolicy.MaximumAttempts} 次 · 只处理当前手柄，保留 BLE 连接", policy.Attempts));
                    var result = await ControllerEnumerationDiagnostics.EnsureSingleUsbipAttachmentAsync(target, client.Host, client.Port, token, reattachExisting: true).ConfigureAwait(false);
                    foreach (var line in result.Lines) progress.Report(line);
                    UsbipDetachCount += (ulong)result.DetachedPortCount;
                    usbipAttachCount += (ulong)result.AttachCount;
                    lastUsbipLifecycleEvent = "targeted_attachment_repair";
                    // A successful attach is only an intent: readiness is verified on the next observation.
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                policy.Observe(UsbipAttachmentState.Unknown, watch.Elapsed);
                if (token.IsCancellationRequested) return;
                SetOutputHealth(new(VirtualOutputState.Unverified, "虚拟手柄状态读取失败", ex.GetType().Name + ": " + ex.Message, policy.Attempts));
            }
            finally{if(gateHeld)attachmentRepairGate.Release();}
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
    }
    private static bool IsLocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1";

    public async Task RepairConfigurationAsync(CancellationToken token)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,cts.Token);
        token=linked.Token;
        if(!IsLocalHost(client.Host))throw new InvalidOperationException("仅支持本机虚拟手柄修复。");
        if(ConfigurationRepairNeedsReboot)throw new InvalidOperationException("Windows 要求重启后完成上次重建，未重复移除设备。");
        await attachmentRepairGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var target=device??throw new InvalidOperationException("虚拟导出已停止。");
            var observed=await ControllerEnumerationDiagnostics.ProbeAttachmentAsync(target,client.Port,token).ConfigureAwait(false);
            if(observed.State!=UsbipAttachmentState.ConfigurationError||observed.Ports.Length!=1||
                !VirtualDeviceConfigurationRepair.MatchesProfile(observed.InstanceId,profile))
                throw new InvalidOperationException("当前不是已确认的代码 19 故障；未移除设备。");
            int repairs=OutputHealth.Repairs;
            SetOutputHealth(new(VirtualOutputState.Recovering,"正在重建当前虚拟手柄","等待管理员授权；保留实体连接与其他手柄。",repairs,19,observed.InstanceId));
            var result=await VirtualDeviceConfigurationRepair.RequestAsync(observed.InstanceId,observed.Ports[0],token).ConfigureAwait(false);
            configurationRepairDiagnostic=result.Message+"\n本次备份和日志："+System.IO.Path.GetDirectoryName(VirtualDeviceConfigurationRepair.ResultPath(result.Operation));
            Volatile.Write(ref configurationRepairNeedsReboot,result.RebootRequired?1:0);
            progress.Report("[VIRTUAL_CONFIG_REPAIR] operation="+result.Operation+" instance=\""+result.InstanceId+
                "\" removed="+result.Removed+" exit="+result.RemoveExit+" backup=\""+
                System.IO.Path.GetDirectoryName(VirtualDeviceConfigurationRepair.ResultPath(result.Operation))+"\" ble_restart=false class_filters_changed=false");
            if(!result.Removed)
            {
                SetOutputHealth(new(VirtualOutputState.Failed,result.RebootRequired?"设备重建需要重启 Windows":"虚拟手柄修复未完成",result.Message,repairs,19,observed.InstanceId));
                return;
            }
            token.ThrowIfCancellationRequested(); // Do not reattach a session stopped while UAC/helper was running.
            var attached=await ControllerEnumerationDiagnostics.EnsureSingleUsbipAttachmentAsync(target,client.Host,client.Port,token,reattachExisting:true).ConfigureAwait(false);
            foreach(var line in attached.Lines)progress.Report(line);
            UsbipDetachCount+=(ulong)attached.DetachedPortCount;usbipAttachCount+=(ulong)attached.AttachCount;
            lastUsbipLifecycleEvent="user_configuration_rebuild";
            for(int i=0;i<12;i++)
            {
                await Task.Delay(1000,token).ConfigureAwait(false);
                observed=await ControllerEnumerationDiagnostics.ProbeAttachmentAsync(target,client.Port,token).ConfigureAwait(false);
                if(observed.State==UsbipAttachmentState.Ready&&GetStream()!=null)
                {SetOutputHealth(new(VirtualOutputState.Ready,"虚拟手柄已恢复",observed.Detail,repairs));return;}
                if(observed.State==UsbipAttachmentState.ConfigurationError)break;
            }
            SetOutputHealth(new(VirtualOutputState.Failed,"虚拟手柄仍未就绪",observed.Detail+"\n设备已重建一次，不再自动重复；可切换其他输出模式，或按配置诊断修复对应驱动软件。",repairs,observed.Problem,observed.InstanceId));
        }
        catch(Exception ex)
        {
            var old=OutputHealth;
            string message=ex is System.ComponentModel.Win32Exception {NativeErrorCode:1223} ? "已取消管理员授权，未重建设备。" : ex.Message;
            SetOutputHealth(new(VirtualOutputState.Failed,"虚拟手柄修复未完成",message,old.Repairs,old.Problem,old.InstanceId));
            throw;
        }
        finally{attachmentRepairGate.Release();}
    }
}
