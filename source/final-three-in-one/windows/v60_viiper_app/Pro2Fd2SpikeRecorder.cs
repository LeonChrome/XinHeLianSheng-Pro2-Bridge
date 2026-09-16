using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed class Pro2Fd2SpikeRecorder : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Pro2InputStabilityOptions options;
    private readonly Pro2Fd2FrameSnapshot?[] frames;
    private readonly Queue<DateTimeOffset> dumpWindow = new();
    private readonly Channel<SpikeDumpRequest> dumpQueue;
    private readonly Task writerTask;
    private long droppedDumpCount;
    private long writtenDumpCount;

    public Pro2Fd2SpikeRecorder(Pro2InputStabilityOptions options)
    {
        this.options = Pro2InputStabilityOptions.Normalize(options);
        CaptureEnabled = this.options.RawIntegrityModeEnabled ||
                         IsEnvironmentFlagEnabled("PRO2_FD2_SPIKE_CAPTURE");
        frames = new Pro2Fd2FrameSnapshot[this.options.AxisSpikeRingBufferCapacity];
        dumpQueue = Channel.CreateBounded<SpikeDumpRequest>(
            new BoundedChannelOptions(16)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        writerTask = Task.Run(WriteLoopAsync);
    }

    public long DroppedDumpCount => droppedDumpCount;
    public long WrittenDumpCount => writtenDumpCount;
    public bool CaptureEnabled { get; }

    public void Clear()
    {
        lock (gate)
        {
            Array.Clear(frames);
        }
    }

    public void AddFrame(Pro2Fd2FrameSnapshot frame)
    {
        if (!CaptureEnabled)
        {
            return;
        }

        lock (gate)
        {
            frames[(int)(frame.FrameIndex % (ulong)frames.Length)] = frame;
        }
    }

    public bool TryQueueDump(
        Pro2AxisSpikeTelemetry telemetry,
        ulong triggerFrameIndex,
        out string path)
    {
        path = "";
        if (!CaptureEnabled ||
            !options.AxisSpikeRawDumpEnabled ||
            options.AxisSpikeDumpRateLimitPer10Seconds <= 0)
        {
            droppedDumpCount++;
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (dumpWindow)
        {
            while (dumpWindow.Count > 0 &&
                   now - dumpWindow.Peek() > TimeSpan.FromSeconds(10))
            {
                dumpWindow.Dequeue();
            }

            if (dumpWindow.Count >= options.AxisSpikeDumpRateLimitPer10Seconds)
            {
                droppedDumpCount++;
                return false;
            }

            dumpWindow.Enqueue(now);
        }

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "v6_logs",
            "spikes");
        string safeAxis = telemetry.AxisName.Replace(" ", "_", StringComparison.Ordinal);
        path = Path.Combine(
            directory,
            "spike_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") +
            "_" + telemetry.ParseSeq +
            "_" + safeAxis + ".jsonl");
        var request = new SpikeDumpRequest(
            telemetry,
            triggerFrameIndex,
            path,
            Math.Max(0, options.AxisSpikeRingBufferFramesBefore),
            Math.Max(0, options.AxisSpikeRingBufferFramesAfter));
        if (!dumpQueue.Writer.TryWrite(request))
        {
            droppedDumpCount++;
            path = "";
            return false;
        }

        return true;
    }

    private async Task WriteLoopAsync()
    {
        await foreach (SpikeDumpRequest request in dumpQueue.Reader.ReadAllAsync())
        {
            try
            {
                int delayMs = Math.Min(1000, request.AfterFrames * 24 + 120);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                IReadOnlyList<Pro2Fd2FrameSnapshot> snapshot =
                    SnapshotRange(
                        request.TriggerFrameIndex >= (ulong)request.BeforeFrames
                            ? request.TriggerFrameIndex - (ulong)request.BeforeFrames
                            : 0,
                        request.TriggerFrameIndex + (ulong)request.AfterFrames);
                Directory.CreateDirectory(Path.GetDirectoryName(request.Path)!);
                await using FileStream stream = new(
                    request.Path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    16 * 1024,
                    useAsync: true);
                await using StreamWriter writer = new(stream);
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new
                    {
                        type = "spike",
                        request.Telemetry,
                        request.TriggerFrameIndex,
                        request.BeforeFrames,
                        request.AfterFrames,
                        captured_frames = snapshot.Count
                    })).ConfigureAwait(false);
                foreach (Pro2Fd2FrameSnapshot frame in snapshot)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new
                        {
                            type = "frame",
                            frame
                        })).ConfigureAwait(false);
                }

                writtenDumpCount++;
            }
            catch
            {
                droppedDumpCount++;
            }
        }
    }

    private IReadOnlyList<Pro2Fd2FrameSnapshot> SnapshotRange(ulong first, ulong last)
    {
        List<Pro2Fd2FrameSnapshot> result = [];
        lock (gate)
        {
            foreach (Pro2Fd2FrameSnapshot? frame in frames)
            {
                if (frame != null &&
                    frame.FrameIndex >= first &&
                    frame.FrameIndex <= last)
                {
                    result.Add(frame);
                }
            }
        }

        result.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        dumpQueue.Writer.TryComplete();
        await writerTask.ConfigureAwait(false);
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return value != null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SpikeDumpRequest(
        Pro2AxisSpikeTelemetry Telemetry,
        ulong TriggerFrameIndex,
        string Path,
        int BeforeFrames,
        int AfterFrames);
}

public sealed record Pro2AxisSnapshot(
    ushort Lx,
    ushort Ly,
    ushort Rx,
    ushort Ry);

public sealed record Pro2MotionSnapshot(
    bool AccelValid,
    bool GyroValid,
    short AccelX,
    short AccelY,
    short AccelZ,
    short GyroX,
    short GyroY,
    short GyroZ);

public sealed record Pro2FrameFilterEventSnapshot(
    string AxisName,
    string Decision,
    string Reason,
    ushort OldValue,
    ushort NewValue,
    ushort OutputValue,
    int Delta,
    double StickVectorDelta,
    int ConsecutiveSuspectFrames,
    double CandidateAgeMs,
    double FrameDeltaMs,
    bool DirectionStable,
    bool Continuous,
    string MotionClass,
    bool ActiveMotion,
    bool FastReversal,
    bool CenterCrossing,
    bool InputSwallowed,
    int RawToFilteredDelta);

public sealed record Pro2Fd2FrameSnapshot(
    ulong FrameIndex,
    DateTimeOffset Timestamp,
    double DeltaMsFromPreviousFrame,
    string ReportType,
    int ReportLen,
    string RawFd2Hex,
    Pro2AxisSnapshot RawAxes,
    Pro2AxisSnapshot FilteredAxes,
    ulong Buttons,
    Pro2MotionSnapshot Motion,
    bool ParseOk,
    string ParseSource,
    string FilterResult,
    IReadOnlyList<Pro2FrameFilterEventSnapshot> FilterEvents);

public sealed record Pro2AxisSpikeTelemetry(
    DateTimeOffset Timestamp,
    string ReportType,
    int ReportLen,
    string AxisName,
    ushort OldValue,
    ushort NewValue,
    ushort OutputValue,
    int Delta,
    ushort OldLeftX,
    ushort OldLeftY,
    ushort OldRightX,
    ushort OldRightY,
    ushort NewLeftX,
    ushort NewLeftY,
    ushort NewRightX,
    ushort NewRightY,
    double StickVectorDelta,
    double SourceAgeMs,
    double BleGapMs,
    int ConsecutiveSuspectFrames,
    string AcceptedOrRejected,
    string Reason,
    string RawFd2Hex,
    ulong ParseSeq,
    ulong StateSeq,
    double BleHz,
    double ViiperPushHz,
    double CandidateAgeMs,
    double FrameDeltaMs,
    bool DirectionStable,
    bool Continuous,
    string MotionClass,
    bool ActiveMotion,
    bool FastReversal,
    bool CenterCrossing,
    bool InputSwallowed,
    int RawToFilteredDelta);

public static class Pro2SpikeSnapshot
{
    public static Pro2AxisSnapshot Axes(GamepadState state) =>
        new(state.Lx, state.Ly, state.Rx, state.Ry);

    public static Pro2MotionSnapshot Motion(GamepadState state) =>
        new(
            state.AccelValid,
            state.GyroValid,
            state.AccelX,
            state.AccelY,
            state.AccelZ,
            state.GyroX,
            state.GyroY,
            state.GyroZ);

    public static IReadOnlyList<Pro2FrameFilterEventSnapshot> Events(
        IReadOnlyList<Pro2AxisFilterEvent> events)
    {
        return events.Select(e => new Pro2FrameFilterEventSnapshot(
                e.AxisName,
                e.Decision.ToString(),
                e.Reason,
                e.OldValue,
                e.NewValue,
                e.OutputValue,
                e.Delta,
                e.StickVectorDelta,
                e.ConsecutiveSuspectFrames,
                e.CandidateAgeMs,
                e.FrameDeltaMs,
                e.DirectionStable,
                e.Continuous,
                e.MotionClass,
                e.ActiveMotion,
                e.FastReversal,
                e.CenterCrossing,
                e.InputSwallowed,
                e.RawToFilteredDelta))
            .ToArray();
    }
}
