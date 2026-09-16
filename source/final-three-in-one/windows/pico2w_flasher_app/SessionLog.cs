using System.IO;
using System.Text;

namespace XinHeLianSheng.Pico2WFlasher;

public sealed class SessionLog : IDisposable
{
    private const long MaximumBytes = 1024 * 1024;
    private readonly object sync = new();
    private readonly StreamWriter writer;

    public SessionLog()
    {
        Directory.CreateDirectory(LogDirectory);
        foreach (string old in Directory.EnumerateFiles(LogDirectory, "flasher_*.log"))
        {
            try { File.Delete(old); } catch { }
        }
        Path = System.IO.Path.Combine(
            LogDirectory, "flasher_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        writer = new StreamWriter(Path, false, new UTF8Encoding(true)) { AutoFlush = true };
    }

    public static string LogDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XinHeLianSheng", "Pico2WFlasher", "logs");

    public string Path { get; }

    public void Write(string message)
    {
        lock (sync)
        {
            if (writer.BaseStream.Length >= MaximumBytes) return;
            writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }

    public void Dispose() => writer.Dispose();

    public static string WriteCrash(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string path = System.IO.Path.Combine(LogDirectory, "flasher_crash.log");
            File.WriteAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + exception,
                new UTF8Encoding(true));
            return path;
        }
        catch
        {
            return "(日志写入失败)";
        }
    }
}
