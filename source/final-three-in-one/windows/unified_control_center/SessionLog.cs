using System.IO;

namespace XinHeLianSheng.ControlCenter;

public static class SessionLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XinHeLianSheng", "ControlCenter", "logs");

    public static string LogDirectory => _logDirectory;

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            _logDirectory = Path.Combine(Path.GetTempPath(),
                "XinHeLianSheng", "ControlCenter", "logs");
            Directory.CreateDirectory(LogDirectory);
        }
        foreach (string old in Directory.EnumerateFiles(LogDirectory, "control_center_*.log"))
        {
            try { File.Delete(old); } catch { }
        }
        _path = Path.Combine(LogDirectory,
            "control_center_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        Write("[APP] 三合一控制中心启动 version=1.0.0-integration-test");
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_path is null) return;
            File.AppendAllText(_path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
    }
}
