using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Y700Switch2V60Viiper;

// Single log-worker owner; bounded recent evidence, not a receive-path queue.
public sealed class RecentSessionStatus
{
    public const int MaximumBytes = 128 * 1024;
    private readonly Queue<(string Text, int Bytes)> lines = new();
    private int bytes;
    public void Add(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (!Important(line)) continue;
            string value = line.TrimEnd('\r');
            if (value.Length > 2048) value = value[..2048] + " [RECENT_LINE_TRUNCATED]";
            value += Environment.NewLine;
            int size = Encoding.UTF8.GetByteCount(value);
            while (lines.Count > 0 && bytes + size > MaximumBytes)
                bytes -= lines.Dequeue().Bytes;
            lines.Enqueue((value, size));
            bytes += size;
        }
    }
    public string Snapshot => string.Concat(lines.Select(line => line.Text));
    private static bool Important(string line) =>
        line.Contains("[PRO2_BLE_RATE_WINDOW]", StringComparison.Ordinal) ||
        line.Contains("[BLE_SESSION_EVIDENCE]", StringComparison.Ordinal) ||
        line.Contains("[BATTERY_RUNTIME", StringComparison.Ordinal) ||
        line.Contains("[PRO2_BATTERY]", StringComparison.Ordinal) ||
        line.Contains("[AUDIO_GUARD]", StringComparison.Ordinal) ||
        line.Contains("[BLE_LOW_RATE_DIAG]", StringComparison.Ordinal) ||
        line.Contains("[START]", StringComparison.Ordinal) ||
        line.Contains("[SHUTDOWN]", StringComparison.Ordinal) ||
        line.Contains("[BLUETOOTH_RESTART", StringComparison.Ordinal) ||
        line.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("error", StringComparison.OrdinalIgnoreCase);
}
