using System;
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

    public SessionLogWriter(string? directory = null, int capacity = 2048)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard", "v6_logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory,
            "manager_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" +
            Guid.NewGuid().ToString("N")[..8] + ".log");
        pending = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(WriteLoopAsync);
    }

    public string FilePath { get; }
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
            long bytesWritten = 0;
            bool limitWritten = false;
            long notedDrops = 0;
            await foreach (string text in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (bytesWritten + Encoding.UTF8.GetByteCount(text) + 128 > MaxPersistentLogBytes)
                {
                    if (!limitWritten)
                    {
                        await writer.WriteLineAsync("[LOG_LIMIT] 8 MB limit reached; further logging muted.");
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
        }
        catch (Exception ex)
        {
            Volatile.Write(ref lastError, ex.Message);
            pending.Writer.TryComplete(ex);
        }
    }
}
