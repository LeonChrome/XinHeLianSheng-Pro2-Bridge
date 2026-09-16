using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed class SessionLogWriter : IAsyncDisposable
{
    private const long MaxPersistentLogBytes = 8L * 1024L * 1024L;
    private readonly object sync = new();
    private long bytesWritten;
    private bool limitNoticeWritten;

    public SessionLogWriter()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "v6_logs");
        Directory.CreateDirectory(directory);
        CleanupPreviousLogs(directory);
        FilePath = Path.Combine(
            directory,
            "manager_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        using FileStream file = new(
            FilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite);
    }

    public string FilePath { get; }

    public static long MaxLogBytes => MaxPersistentLogBytes;

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (sync)
        {
            if (bytesWritten >= MaxPersistentLogBytes)
            {
                WriteLimitNoticeIfNeeded();
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytesWritten + bytes.Length > MaxPersistentLogBytes)
            {
                WriteLimitNoticeIfNeeded();
                bytesWritten = MaxPersistentLogBytes;
                return;
            }

            File.AppendAllText(
                FilePath,
                text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            bytesWritten += bytes.Length;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }

    private void WriteLimitNoticeIfNeeded()
    {
        if (limitNoticeWritten)
        {
            return;
        }

        limitNoticeWritten = true;
        string notice =
            Environment.NewLine +
            "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " +
            "[LOG_LIMIT] persistent session log reached " +
            MaxPersistentLogBytes / 1024 / 1024 +
            " MB; further file logging is muted for this run. Use 导出诊断 only when needed." +
            Environment.NewLine;
        File.AppendAllText(
            FilePath,
            notice,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void CleanupPreviousLogs(string directory)
    {
        string[] patterns =
        [
            "manager_*.log",
            "viiper_server_*.log",
            "startup_guard_*.log",
            "diagnostics_v6*.log",
            "professional_imu_*.csv"
        ];
        foreach (string pattern in patterns)
        {
            foreach (string path in Directory.EnumerateFiles(directory, pattern))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Best effort: a stale VIIPER process may still hold its log.
                }
            }
        }
    }
}
