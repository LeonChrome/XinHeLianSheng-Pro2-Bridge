namespace Y700Switch2V60Viiper;

public static class BluetoothRebootEvidence
{
    public const string Guidance = "蓝牙适配器处于系统待重启状态，请保存工作后重启 Windows，再连接手柄。";

    // cfg.h DN_NEED_RESTART and CM_PROB_NEED_RESTART (Device Manager Code 14).
    public static bool DeviceStatusRequiresReboot(uint status, uint problem) =>
        (status & 0x00000100) != 0 || problem == 14;

    public static bool RequiresReboot(int? exit, int? disableExit, string? output, bool devicePending = false) =>
        devicePending || exit == 3010 || disableExit == 3010 ||
        (exit == 50 || disableExit == 50) && HasExplicitPendingMessage(output);

    public static bool HasExplicitPendingMessage(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("设备正在等待系统重新启动", StringComparison.Ordinal) ||
                line.Contains("裝置正在等待系統重新啟動", StringComparison.Ordinal)) return true;
            string trimmed=line.Trim();
            if(trimmed.StartsWith("The device is pending a system reboot to complete a previous operation",StringComparison.OrdinalIgnoreCase)||
                trimmed.StartsWith("Device is pending a system reboot to complete a previous operation",StringComparison.OrdinalIgnoreCase)||
                trimmed.StartsWith("The device is pending a system restart to complete a previous operation",StringComparison.OrdinalIgnoreCase)||
                trimmed.StartsWith("A system reboot is required to complete a previous operation",StringComparison.OrdinalIgnoreCase)||
                trimmed.StartsWith("A system restart is required to complete a previous operation",StringComparison.OrdinalIgnoreCase))return true;
        }
        return false; // Generic "restart failed/not supported" is not reboot evidence.
    }
}
