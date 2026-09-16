using System.IO;

namespace XinHeLianSheng.Pico2WFlasher;

public sealed record BootselDevice(
    string RootPath,
    string VolumeLabel,
    string Model,
    string BoardId,
    string InfoText);

public sealed record UnsupportedBootVolume(string RootPath, string Reason);

public sealed record BootselScanResult(
    IReadOnlyList<BootselDevice> Supported,
    IReadOnlyList<UnsupportedBootVolume> Unsupported);

public static class BootselDeviceLocator
{
    public static BootselScanResult Scan()
    {
        var supported = new List<BootselDevice>();
        var unsupported = new List<UnsupportedBootVolume>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Network or DriveType.CDRom) continue;
                if (!drive.IsReady) continue;
                string infoPath = Path.Combine(drive.RootDirectory.FullName, "INFO_UF2.TXT");
                if (!File.Exists(infoPath)) continue;
                string text = File.ReadAllText(infoPath);
                Inspect(drive.RootDirectory.FullName, drive.VolumeLabel, text,
                    supported, unsupported);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            catch (NotSupportedException) { }
        }

        return new BootselScanResult(supported, unsupported);
    }

    public static BootselScanResult InspectForTest(
        string rootPath, string volumeLabel, string infoText)
    {
        var supported = new List<BootselDevice>();
        var unsupported = new List<UnsupportedBootVolume>();
        Inspect(rootPath, volumeLabel, infoText, supported, unsupported);
        return new BootselScanResult(supported, unsupported);
    }

    private static void Inspect(
        string rootPath,
        string volumeLabel,
        string infoText,
        List<BootselDevice> supported,
        List<UnsupportedBootVolume> unsupported)
    {
        if (!infoText.Contains("UF2 Bootloader", StringComparison.OrdinalIgnoreCase))
        {
            unsupported.Add(new(rootPath, "存在 INFO_UF2.TXT，但不是 UF2 BOOTSEL 设备。"));
            return;
        }

        string model = ReadField(infoText, "Model");
        string boardId = ReadField(infoText, "Board-ID");
        bool isRp2350 = volumeLabel.Equals("RP2350", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("RP2350", StringComparison.OrdinalIgnoreCase) ||
                        boardId.Contains("RP2350", StringComparison.OrdinalIgnoreCase);
        if (!isRp2350)
        {
            unsupported.Add(new(rootPath,
                "检测到 RP2040 / Pico 1 BOOTSEL；本固件只能刷入 Pico 2 W（RP2350）。"));
            return;
        }

        supported.Add(new BootselDevice(rootPath, volumeLabel, model, boardId, infoText));
    }

    private static string ReadField(string text, string name)
    {
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return string.Empty;
    }
}
