namespace XinHeLianSheng.Pico2WFlasher;

public static class FirmwareIdentity
{
    public static bool IsExpectedAndReady(string response, out string reason)
    {
        if (!response.Contains(
                "firmware_revision=" + FirmwareManifest.Revision,
                StringComparison.Ordinal))
        {
            reason = "固件修订号不是 " + FirmwareManifest.Revision + "。";
            return false;
        }
        if (!response.Contains(
                "firmware_version=" + FirmwareManifest.Version,
                StringComparison.Ordinal))
        {
            reason = "固件完整版本不匹配。";
            return false;
        }
        if (!response.Contains("runtime_ready=true", StringComparison.Ordinal))
        {
            reason = "固件尚未完成自检、无线芯片或当前模式后端初始化。";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
