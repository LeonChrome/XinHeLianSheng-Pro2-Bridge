using System;
using System.Buffers.Binary;
using System.Text.Json;
using Y700Switch2V60Viiper;

static void Pack12(byte[] data, int offset, ushort x, ushort y)
{
    data[offset] = (byte)(x & 0xff);
    data[offset + 1] = (byte)(((x >> 8) & 0x0f) | ((y & 0x0f) << 4));
    data[offset + 2] = (byte)((y >> 4) & 0xff);
}

static byte[] Fd2(
    ushort lx,
    ushort ly,
    ushort rx,
    ushort ry,
    uint buttons = 0)
{
    byte[] report = new byte[60];
    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(4, 4), buttons);
    Pack12(report, 10, lx, ly);
    Pack12(report, 13, rx, ry);
    return report;
}

static string Axes(GamepadState state) =>
    $"lx={state.Lx} ly={state.Ly} rx={state.Rx} ry={state.Ry}";

static ReplayStats ReplayFrames(
    IEnumerable<(byte[] Raw, TimeSpan At, string Label)> frames,
    bool verbose)
{
    var parser = new Pro2HidReportParser();
    var filter = new Pro2InputStabilityFilter();
    var stats = new ReplayStats();
    int index = 0;
    foreach ((byte[] raw, TimeSpan at, string label) in frames)
    {
        index++;
        stats.TotalFrames++;
        bool parsed = parser.TryParseFd2Payload(raw, out GamepadState parsedState, out string source);
        if (!parsed)
        {
            stats.ParseFailures++;
            if (verbose)
            {
                Console.WriteLine($"{index,4} {label} parse_fail");
            }
            continue;
        }

        Pro2InputFilterResult result = filter.ProcessAt(parsedState, at);
        if (result.HasAxisIntervention)
        {
            stats.SpikeFrames++;
        }
        foreach (Pro2AxisFilterEvent ev in result.Events)
        {
            stats.RawToFilteredMaxDelta = Math.Max(stats.RawToFilteredMaxDelta, ev.RawToFilteredDelta);
            if (ev.InputSwallowed)
            {
                stats.InputSwallowedCount++;
            }
            if (ev.ActiveMotion)
            {
                stats.ActiveMotionCount++;
            }
            if (ev.FastReversal)
            {
                stats.FastReversalCount++;
            }
            switch (ev.Decision)
            {
                case Pro2AxisFilterDecisionKind.Hold:
                    stats.HeldSpikes++;
                    break;
                case Pro2AxisFilterDecisionKind.Reject:
                    stats.RejectedSpikes++;
                    break;
                case Pro2AxisFilterDecisionKind.Accept:
                    stats.AcceptedCandidates++;
                    break;
                case Pro2AxisFilterDecisionKind.RampAccept:
                    stats.RampedCandidates++;
                    break;
            }
        }

        if (verbose)
        {
            string decision = result.Events.Count == 0
                ? "accept_normal"
                : string.Join("|", result.Events.Select(e =>
                    $"{e.AxisName}:{e.Decision}:{e.Reason}:motion={e.MotionClass}:old={e.OldValue}:raw={e.NewValue}:out={e.OutputValue}:frames={e.ConsecutiveSuspectFrames}:age={e.CandidateAgeMs:F1}:swallowed={e.InputSwallowed}:diff={e.RawToFilteredDelta}"));
            Console.WriteLine(
                $"{index,4} {label} t={at.TotalMilliseconds,7:F1} raw[{Axes(parsedState)}] filtered[{Axes(result.AcceptedState)}] {decision}");
        }
    }

    return stats;
}

static IEnumerable<(byte[] Raw, TimeSpan At, string Label)> SyntheticFrames()
{
    ushort center = GamepadState.AxisCenter;
    ushort max = GamepadState.AxisMax;
    yield return (Fd2(center, center, center, center), TimeSpan.Zero, "center");

    yield return (Fd2(max, center, center, center, 0x00000004), TimeSpan.FromMilliseconds(15), "single_spike");
    yield return (Fd2(center, center, center, center), TimeSpan.FromMilliseconds(30), "single_recover");

    for (int i = 1; i <= 5; i++)
    {
        yield return (Fd2(max, center, center, center), TimeSpan.FromMilliseconds(100 + i * 15), "bad_cluster_" + i);
    }
    yield return (Fd2(center, center, center, center), TimeSpan.FromMilliseconds(190), "bad_cluster_recover");

    for (int i = 0; i < 10; i++)
    {
        yield return (Fd2(max, center, center, center), TimeSpan.FromMilliseconds(300 + i * 15), "real_fast_push_" + i);
    }

    for (int i = 0; i < 8; i++)
    {
        ushort value = (ushort)(center + i * 120);
        yield return (Fd2(value, center, (ushort)(center + i * 80), center), TimeSpan.FromMilliseconds(500 + i * 15), "slow_move_" + i);
    }

    yield return (Fd2(max, center, (ushort)(center + 200), (ushort)(center - 200)), TimeSpan.FromMilliseconds(700), "single_axis_bad");
    yield return (Fd2(center, center, (ushort)(center + 240), (ushort)(center - 240)), TimeSpan.FromMilliseconds(715), "single_axis_recover");
    yield return (Fd2(0, center, (ushort)(center + 280), center), TimeSpan.FromMilliseconds(730), "fast_reversal_left");
    yield return (Fd2(0, center, (ushort)(center + 320), center), TimeSpan.FromMilliseconds(745), "fast_reversal_left_hold");
    yield return (Fd2(max, 0, 0, max), TimeSpan.FromMilliseconds(800), "multi_axis_bad");
    yield return (Fd2(center, center, center, center), TimeSpan.FromMilliseconds(815), "multi_axis_recover");
}

static IEnumerable<(byte[] Raw, TimeSpan At, string Label)> LoadJsonl(string path)
{
    string[] files;
    if (File.Exists(path))
    {
        files = [path];
    }
    else if (Directory.Exists(path))
    {
        files = Directory.GetFiles(path, "*.jsonl", SearchOption.TopDirectoryOnly);
    }
    else
    {
        throw new FileNotFoundException("Replay path not found", path);
    }

    foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
    {
        DateTimeOffset? firstTimestamp = null;
        foreach (string line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type) ||
                type.GetString() != "frame" ||
                !root.TryGetProperty("frame", out JsonElement frame))
            {
                continue;
            }

            string? rawHex = frame.TryGetProperty("RawFd2Hex", out JsonElement rawElement)
                ? rawElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(rawHex))
            {
                continue;
            }

            DateTimeOffset timestamp = frame.TryGetProperty("Timestamp", out JsonElement tsElement) &&
                                       tsElement.TryGetDateTimeOffset(out DateTimeOffset parsedTs)
                ? parsedTs
                : DateTimeOffset.UtcNow;
            firstTimestamp ??= timestamp;
            ulong frameIndex = frame.TryGetProperty("FrameIndex", out JsonElement indexElement)
                ? indexElement.GetUInt64()
                : 0;
            yield return (
                Convert.FromHexString(rawHex),
                timestamp - firstTimestamp.Value,
                Path.GetFileName(file) + "#" + frameIndex);
        }
    }
}

string? input = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
bool verbose = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase) == false;
bool synthetic = input == null ||
                 args.Contains("--synthetic", StringComparer.OrdinalIgnoreCase);

ReplayStats stats = synthetic
    ? ReplayFrames(SyntheticFrames(), verbose)
    : ReplayFrames(LoadJsonl(input!), verbose);

Console.WriteLine(stats);

if (synthetic)
{
    if (stats.TotalFrames < 20 ||
        stats.HeldSpikes == 0 ||
        stats.RejectedSpikes == 0 ||
        stats.AcceptedCandidates == 0 ||
        stats.FastReversalCount == 0)
    {
        throw new InvalidOperationException("Synthetic replay did not exercise hold/reject/low-latency accept paths.");
    }
    if (stats.InputSwallowedCount != 0)
    {
        throw new InvalidOperationException("Synthetic replay swallowed a fast input.");
    }
}

internal sealed class ReplayStats
{
    public int TotalFrames { get; set; }
    public int ParseFailures { get; set; }
    public int SpikeFrames { get; set; }
    public int RejectedSpikes { get; set; }
    public int HeldSpikes { get; set; }
    public int AcceptedCandidates { get; set; }
    public int RampedCandidates { get; set; }
    public int FalseAcceptCount { get; set; }
    public int ActiveMotionCount { get; set; }
    public int FastReversalCount { get; set; }
    public int InputSwallowedCount { get; set; }
    public int RawToFilteredMaxDelta { get; set; }

    public override string ToString()
    {
        return "total_frames=" + TotalFrames +
               " parse_failures=" + ParseFailures +
               " spike_frames=" + SpikeFrames +
               " rejected_spikes=" + RejectedSpikes +
               " held_spikes=" + HeldSpikes +
               " accepted_candidates=" + AcceptedCandidates +
               " ramped_candidates=" + RampedCandidates +
               " false_accept_count=" + FalseAcceptCount +
               " active_motion_count=" + ActiveMotionCount +
               " fast_reversal_count=" + FastReversalCount +
               " input_swallowed_count=" + InputSwallowedCount +
               " raw_to_filtered_max_delta=" + RawToFilteredMaxDelta;
    }
}
