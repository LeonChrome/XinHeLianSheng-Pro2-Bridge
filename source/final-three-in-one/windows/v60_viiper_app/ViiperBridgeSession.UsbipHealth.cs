using System.Diagnostics;

namespace Y700Switch2V60Viiper;

public sealed partial class ViiperBridgeSession
{
    private Task? attachmentTask;
    private VirtualOutputHealth outputHealth = new(VirtualOutputState.Starting, "正在检查虚拟手柄", "等待 USBIP 与 Windows 输入接口就绪");
    public VirtualOutputHealth OutputHealth => Volatile.Read(ref outputHealth);
    private void SetOutputHealth(VirtualOutputHealth health)
    {
        if (cts.IsCancellationRequested && health.State is not (VirtualOutputState.Failed or VirtualOutputState.Stopped)) return;
        var old = Interlocked.Exchange(ref outputHealth, health);
        if (old != health)
            progress.Report($"[USBIP_HEALTH] state={health.State} repairs={health.Repairs} bus={device?.BusId} dev={device?.DevId} detail={health.Detail.Replace('\r', ' ').Replace('\n', ' ')}");
    }

    private async Task MonitorAttachmentAsync(CancellationToken token)
    {
        var policy = new UsbipRecoveryPolicy();
        var watch = Stopwatch.StartNew();
        if (!IsLocalHost(client.Host))
        { SetOutputHealth(new(VirtualOutputState.Unverified, "远程输出状态待确认", "仅本机虚拟 USB 支持自动检查与恢复")); return; }
        while (!token.IsCancellationRequested)
        {
            try
            {
                var target = device;
                if (target == null) return;
                var observed = await ControllerEnumerationDiagnostics.ProbeAttachmentAsync(target, client.Port, token).ConfigureAwait(false);
                bool repair = policy.Observe(observed.State, watch.Elapsed);
                if (token.IsCancellationRequested) return;
                var state = observed.State switch {
                    UsbipAttachmentState.Ready => GetStream() == null ? VirtualOutputState.Recovering : VirtualOutputState.Ready,
                    UsbipAttachmentState.Unknown => VirtualOutputState.Unverified,
                    UsbipAttachmentState.Starting => VirtualOutputState.Starting,
                    _ => VirtualOutputState.Recovering
                };
                if (state != VirtualOutputState.Ready && policy.Attempts >= UsbipRecoveryPolicy.MaximumAttempts && !repair &&
                    observed.State != UsbipAttachmentState.Unknown) state = VirtualOutputState.Failed;
                string summary = state switch {
                    VirtualOutputState.Ready => "虚拟手柄已就绪",
                    VirtualOutputState.Unverified => "虚拟手柄状态待确认",
                    VirtualOutputState.Starting => "Windows 正在识别虚拟手柄",
                    VirtualOutputState.Failed => "虚拟输出未就绪，请停止后重新进入游戏",
                    _ => "正在恢复虚拟手柄"
                };
                SetOutputHealth(new(state, summary, observed.Detail, policy.Attempts));
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
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
    }
    private static bool IsLocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1";
}
