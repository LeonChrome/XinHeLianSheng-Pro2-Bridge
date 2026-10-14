using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Non-blocking producers, one bounded file worker. Never deletes previous logs.
public sealed class SessionLogWriter : IAsyncDisposable
{
    private const long MaxPersistentLogBytes = 8L * 1024L * 1024L;
    private readonly Channel<string> pending;
    private readonly Task worker;
    private long dropped;
    private string? lastError;
    private readonly long detailedLimit;
    private string recentText = "";

    public SessionLogWriter(string? directory = null, int capacity = 2048,
        long detailedLimitBytes = MaxPersistentLogBytes)
    {
        if (detailedLimitBytes < 512 || detailedLimitBytes > MaxPersistentLogBytes)
            throw new ArgumentOutOfRangeException(nameof(detailedLimitBytes));
        detailedLimit = detailedLimitBytes;
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard", "v6_logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory,
            "manager_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" +
            Guid.NewGuid().ToString("N")[..8] + ".log");
        RecentFilePath = Path.ChangeExtension(FilePath, ".recent.log");
        pending = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(WriteLoopAsync);
    }

    public string FilePath { get; }
    public string RecentFilePath { get; }
    public string RecentText => Volatile.Read(ref recentText);
    public static long MaxLogBytes => MaxPersistentLogBytes;
    public long DroppedCount => Interlocked.Read(ref dropped);
    public string? LastError => Volatile.Read(ref lastError);

    public void Write(string text)
    {
        if (!string.IsNullOrEmpty(text) && !pending.Writer.TryWrite(text))
            Interlocked.Increment(ref dropped);
    }

    public async ValueTask DisposeAsync()
    {
        pending.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await using var file = new FileStream(FilePath, FileMode.CreateNew,
                FileAccess.Write, FileShare.ReadWrite, 65536, useAsync: true);
            await using var writer = new StreamWriter(file, new UTF8Encoding(false), 16384);
            // Only our newly-created, unique session snapshot is rewritten.
            // The original capped log and ALL historical session files are preserved.
            await using var recentFile = new FileStream(RecentFilePath, FileMode.CreateNew,
                FileAccess.Write, FileShare.ReadWrite, 16384, useAsync: true);
            var recent = new RecentSessionStatus();
            long recentFlushedAt = 0;
            async Task FlushRecentAsync()
            {
                string snapshot = recent.Snapshot;
                Volatile.Write(ref recentText, snapshot);
                byte[] data = Encoding.UTF8.GetBytes(snapshot);
                recentFile.Position = 0;
                recentFile.SetLength(0);
                await recentFile.WriteAsync(data).ConfigureAwait(false);
                await recentFile.FlushAsync().ConfigureAwait(false);
                recentFlushedAt = Stopwatch.GetTimestamp();
            }
            long bytesWritten = 0;
            bool limitWritten = false;
            long notedDrops = 0;
            await foreach (string text in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                recent.Add(text);
                if (recentFlushedAt == 0 || Stopwatch.GetElapsedTime(recentFlushedAt).TotalSeconds >= 5)
                    await FlushRecentAsync().ConfigureAwait(false);
                if (bytesWritten + Encoding.UTF8.GetByteCount(text) + 128 > detailedLimit)
                {
                    if (!limitWritten)
                    {
                        await writer.WriteLineAsync("[LOG_LIMIT] detail limit reached; live evidence continues in " + Path.GetFileName(RecentFilePath));
                        await writer.FlushAsync();
                        limitWritten = true;
                    }
                    continue;
                }
                if (limitWritten) continue;
                await writer.WriteAsync(text);
                bytesWritten += Encoding.UTF8.GetByteCount(text);
                long count = DroppedCount;
                if (count > notedDrops)
                {
                    string notice = "[LOG_DROPPED] bounded queue overflow count=" + count;
                    await writer.WriteLineAsync(notice);
                    bytesWritten += Encoding.UTF8.GetByteCount(notice + Environment.NewLine);
                    notedDrops = count;
                }
                if (pending.Reader.Count == 0)
                    await writer.FlushAsync();
            }
            if (DroppedCount > notedDrops)
                await writer.WriteLineAsync("[LOG_DROPPED] bounded queue overflow count=" + DroppedCount);
            await FlushRecentAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref lastError, ex.Message);
            pending.Writer.TryComplete(ex);
        }
    }
}
