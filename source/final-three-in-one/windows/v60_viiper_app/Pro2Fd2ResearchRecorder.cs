using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Opt-in raw capture for laboratory comparisons. The BLE callback never waits for disk I/O.
public sealed class Pro2Fd2ResearchRecorder : IAsyncDisposable
{
    private const string EnvironmentVariable = "PRO2_FD2_RESEARCH_CAPTURE";
    private static readonly object ClaimGate = new();
    private static readonly HashSet<string> ClaimedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<Entry>? queue;
    private Task? writerTask;
    private int startState;
    private long dropped;

    public Pro2Fd2ResearchRecorder()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath)) return;

        OutputPath = Path.GetFullPath(configuredPath);
        queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    }

    public bool Enabled => queue != null;
    public string OutputPath { get; } = "";
    public long Dropped => dropped;

    public void TryRecord(ulong sequence, DateTimeOffset timestamp, double deltaMs, byte[] raw)
    {
        if (queue == null) return;
        EnsureWriterStarted();
        if (Volatile.Read(ref startState) != 1) return;
        if (!queue.Writer.TryWrite(new Entry(sequence, timestamp, deltaMs, raw.ToArray())))
        {
            System.Threading.Interlocked.Increment(ref dropped);
        }
    }

    private void EnsureWriterStarted()
    {
        if (Volatile.Read(ref startState) != 0) return;
        lock (ClaimGate)
        {
            if (startState != 0) return;
            if (!ClaimedPaths.Add(OutputPath))
            {
                startState = -1;
                Interlocked.Increment(ref dropped);
                return;
            }
            startState = 1;
            writerTask = Task.Run(WriteLoopAsync);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            await using FileStream stream = new(
                OutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                useAsync: true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "research_capture",
                created = DateTimeOffset.Now,
                source = "Pro2 BLE FD2",
                environment_variable = EnvironmentVariable
            })).ConfigureAwait(false);

            int pendingFlush = 0;
            await foreach (Entry entry in queue!.Reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "frame",
                    frame = new
                    {
                        FrameIndex = entry.Sequence,
                        Timestamp = entry.Timestamp,
                        DeltaMsFromPreviousFrame = entry.DeltaMs,
                        RawFd2Hex = Convert.ToHexString(entry.Raw)
                    }
                })).ConfigureAwait(false);
                if (++pendingFlush >= 64)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                    pendingFlush = 0;
                }
            }
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            System.Threading.Interlocked.Increment(ref dropped);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (queue == null) return;
        if (Volatile.Read(ref startState) == 1)
        {
            queue.Writer.TryComplete();
            if (writerTask != null) await writerTask.ConfigureAwait(false);
            lock (ClaimGate) ClaimedPaths.Remove(OutputPath);
        }
    }

    private sealed record Entry(ulong Sequence, DateTimeOffset Timestamp, double DeltaMs, byte[] Raw);
}
