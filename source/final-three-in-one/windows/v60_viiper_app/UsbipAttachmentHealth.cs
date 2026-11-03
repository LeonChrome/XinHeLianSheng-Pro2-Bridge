namespace Y700Switch2V60Viiper;

public enum UsbipAttachmentState { Unknown, Missing, Duplicate, Starting, DeviceError, Ready }
public sealed record UsbipAttachmentObservation(UsbipAttachmentState State, int[] Ports, string Detail, uint Problem = 0);
public enum VirtualOutputState { Starting, Ready, Recovering, Unverified, Failed, Stopped }
public sealed record VirtualOutputHealth(VirtualOutputState State, string Summary, string Detail, int Repairs = 0);

public static class UsbipAttachmentHealth
{
    public static UsbipAttachmentObservation Evaluate(bool portsReadable, IEnumerable<int> ports, VirtualPnpSnapshot pnp)
    {
        int[] matching = ports.Distinct().Order().ToArray();
        if (!portsReadable) return new(UsbipAttachmentState.Unknown, matching, "USBIP 端口查询未完成；不据此卸载设备");
        if (matching.Length == 0) return new(UsbipAttachmentState.Missing, matching, "对应 USBIP 挂载不存在");
        if (matching.Length > 1) return new(UsbipAttachmentState.Duplicate, matching, "同一虚拟手柄存在多个挂载：" + string.Join(",", matching));
        if (!pnp.Readable) return new(UsbipAttachmentState.Unknown, matching, "端口存在，但 Windows 设备状态未读取：" + pnp.Error);
        var target = pnp.Devices.FirstOrDefault(d => d.Port == matching[0]);
        if (target == null) return new(UsbipAttachmentState.Unknown, matching, "端口已分配，Windows 设备归属尚未确认；不据此卸载设备");
        if (target.Problem == uint.MaxValue) return new(UsbipAttachmentState.Unknown, matching, "Windows 设备状态读取失败；不据此卸载设备");
        if (target.Problem != 0) return new(UsbipAttachmentState.DeviceError, matching, "Windows 设备启动失败，问题码=" + target.Problem + " · " + target.InstanceId, target.Problem);
        if (!target.Started || !target.InputReady) return new(UsbipAttachmentState.Starting, matching, "端口存在，HID / XInput 接口尚未就绪 · " + target.InstanceId);
        return new(UsbipAttachmentState.Ready, matching, "USBIP Port " + matching[0] + " · Windows 输入接口已就绪 · " + target.InstanceId);
    }
}

// Per-session limits; unknown observations never authorize a destructive repair.
public sealed class UsbipRecoveryPolicy
{
    private TimeSpan? badSince;
    private UsbipAttachmentState? badState;
    private TimeSpan nextAttempt;
    public int Attempts { get; private set; }
    public const int MaximumAttempts = 2;
    public bool Observe(UsbipAttachmentState state, TimeSpan now)
    {
        if (state is UsbipAttachmentState.Ready or UsbipAttachmentState.Unknown)
        { badSince = null; badState = null; return false; }
        if (state != badState) { badSince = now; badState = state; }
        if (Attempts >= MaximumAttempts || now < nextAttempt || now - badSince < TimeSpan.FromSeconds(15)) return false;
        Attempts++;
        nextAttempt = now + TimeSpan.FromSeconds(30 * Attempts);
        badSince = null; badState = null;
        return true;
    }
}
