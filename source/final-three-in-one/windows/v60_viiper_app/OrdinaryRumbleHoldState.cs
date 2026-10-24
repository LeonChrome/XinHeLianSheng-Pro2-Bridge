using System;
using System.Diagnostics;
using System.Globalization;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Xbox / DualSense ordinary output is a state (start/change/stop), not a PCM
/// stream. Keep its latest accepted state alive on the physical Pro2, without
/// inventing a short idle expiry or replaying native / converted HD packets.
/// Called only under the owning writer's lock; no timer or extra writer thread.
/// </summary>
public sealed class OrdinaryRumbleHoldState
{
    public const double RefreshIntervalMs = 12;
    private byte[]? current;
    private string source = "";
    private long revision, dueTicks;
    private bool armed;
    public bool Active => current != null;
    public long Revision => revision;
    public long Generated { get; private set; }
    public long Cancelled { get; private set; }
    public long FailureResets { get; private set; }
    public string Telemetry => "ordinary_hold=" + Active + " ordinary_refresh_target_ms=12 refresh_clock=write_start" +
        " ordinary_refresh_generated=" + Generated.ToString(CultureInfo.InvariantCulture) +
        " ordinary_refresh_cancelled=" + Cancelled.ToString(CultureInfo.InvariantCulture) +
        " ordinary_refresh_failure_resets=" + FailureResets.ToString(CultureInfo.InvariantCulture) +
        " ordinary_refresh_scope=xbox_ps5_state_only native_or_hd_refresh=false no_source_idle_cutoff=true";

    public static bool IsOrdinarySource(string value, bool native = false) => !native &&
        (value == "xinput" || value == "dualsense-ordinary");

    public void Observe(ReadOnlySpan<byte> packet, string value, bool native, bool valid)
    {
        revision++;
        current = null; armed = false; dueTicks = 0; source = "";
        if (!valid || !IsOrdinarySource(value, native) || packet.Length < 33 ||
            !Pro2NativeRumbleEncoder.HasEffect(packet)) return;
        current = packet[..33].ToArray(); source = value;
    }

    public void Written(Pro2QueuedRumble item, bool success, long now, long? writeStarted = null)
    {
        if (item.GeneratedKind is not "source" and not "refresh") { Invalidate(); return; }
        if (!success)
        {
            if (current != null && item.Source == source && SameState(current, item.Data))
            { FailureResets++; Invalidate(); }
            return;
        }
        // An old queued start must not arm a newer state, and an already
        // received stop must never be undone by completion of that old write.
        if (current == null || !SameState(current, item.Data) || item.Source != source) return;
        armed = true;
        // The interval begins at write START. Waiting another 12ms after a
        // slow GATT completion would double-count transport service time.
        dueTicks = (writeStarted ?? now) +
            (long)Math.Ceiling(RefreshIntervalMs * Stopwatch.Frequency / 1000);
    }

    public double MillisecondsUntilDue(long now) => current == null || !armed
        ? double.PositiveInfinity : Math.Max(0, (dueTicks - now) * 1000.0 / Stopwatch.Frequency);

    public bool TryCreate(long now, bool sourcePending, byte sequence, out Pro2QueuedRumble? item)
    {
        item = null;
        if (current == null || !armed || sourcePending || now < dueTicks) return false;
        byte[] copy = current.ToArray();
        copy[1] = copy[17] = (byte)(0x50 | (sequence & 15));
        item = new(copy, false, now, Source: source, GeneratedKind: "refresh", StateRevision: revision);
        // At most one in-flight refresh. A successful completion re-arms it.
        armed = false; Generated++;
        return true;
    }

    public bool IsCurrent(Pro2QueuedRumble item) => current != null &&
        item.StateRevision == revision && item.Source == source && SameState(current, item.Data);

    public void RefreshSuperseded() => Cancelled++;

    public void Invalidate()
    {
        revision++; current = null; source = ""; armed = false; dueTicks = 0;
    }

    public void Reset()
    {
        Invalidate(); Generated = Cancelled = FailureResets = 0;
    }

    private static bool SameState(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length < 33 || b.Length < 33) return false;
        for (int i = 1; i < 33; i++)
            if (i is 1 or 17 ? (a[i] & 0xf0) != (b[i] & 0xf0) : a[i] != b[i]) return false;
        return true;
    }
}

public static class Pro2RumblePacingPolicy
{
    // Do not equate incoming FD2 interval with outgoing packet capacity: BLE
    // may carry several PDUs per connection event. Serial GATT completion and
    // measured queue/service telemetry decide actual capacity, not fake repeats.
    public const double MinimumWriteIntervalMs = 7.5;
}
