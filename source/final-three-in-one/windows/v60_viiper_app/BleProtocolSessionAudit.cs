using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Y700Switch2V60Viiper;

public static class BleInitAuditPolicy
{
    public static bool ShouldSend(ReadOnlySpan<byte> command, bool skipUsbInitialization)
    {
        if (command.Length < 8) return false;
        // Normal bondless BLE input never commits/exchanges controller pairing data.
        if (command[0] == 0x15) return false;
        return !skipUsbInitialization || command[0] != 0x03 || command[3] != 0x0d;
    }

    public static string Profile(bool skipUsb) => skipUsb ? "no_pair_finalize_no_usb_init" : "no_pair_finalize";
    public static bool AckMatches(ReadOnlySpan<byte> request, ReadOnlySpan<byte> reply) =>
        request.Length >= 8 && reply.Length >= 8 && request[0] == reply[0] && request[3] == reply[3];

    public static string CommandIdentity(ReadOnlySpan<byte> data) => data.Length < 8 ? "short_packet" :
        "cmd=0x" + data[0].ToString("X2") + " sub=0x" + data[3].ToString("X2") + " len=" + data.Length;
}

// Fixed-size receive statistics; no GATT queries, disk writes or per-notify strings.
// Host-observed connection times are NOT HCI/air timestamps or RF latency.
public sealed class BleProtocolSessionAudit
{
    private readonly object sync = new();
    private readonly byte[] previous = new byte[128];
    private readonly long[] counterSteps = new long[18];
    private readonly Queue<string> control = new();
    private readonly Queue<string> ended = new();
    private string id = "none", profile = "not_started", status = "not_started";
    private long attemptTicks, windowsTicks, gattTicks, firstFd2Ticks, lastFd2Ticks, endTicks;
    private long frames, motionChanges, sameRaw, sameControls, sameMotion, sameCounter, malformed;
    private long motionBackwards, motionAdvance, delayedCallbacks, negativeCallbackAges;
    private double maxCallbackAgeMs;
    private uint previousCounter, previousMotion;
    private int previousLength;
    private ushort maxPdu;
    private bool hasPrevious, endedCurrent;
    private long sequence;

    public void Begin(bool skipUsb, long ticks)
    {
        lock (sync)
        {
            if (id != "none")
            {
                ended.Enqueue(FormatNoLock(ticks) + "\n" + string.Join("\n", control.Select(item =>
                    "[BLE_CONTROL_EVIDENCE] attempt=" + id + " " + item)));
                while (ended.Count > 3) ended.Dequeue();
            }
            id = (++sequence).ToString(CultureInfo.InvariantCulture);
            profile = BleInitAuditPolicy.Profile(skipUsb);
            status = "opening";
            attemptTicks = ticks;
            windowsTicks = gattTicks = firstFd2Ticks = lastFd2Ticks = endTicks = 0;
            frames = motionChanges = sameRaw = sameControls = sameMotion = sameCounter = malformed = 0;
            motionBackwards = motionAdvance = delayedCallbacks = negativeCallbackAges = 0;
            maxCallbackAgeMs = 0;
            previousCounter = previousMotion = 0;
            previousLength = 0;
            maxPdu = 0;
            hasPrevious = endedCurrent = false;
            Array.Clear(counterSteps);
            control.Clear();
            AddNoLock("begin profile=" + profile + " init_pairing_commands_allowed=false system_writes=false");
        }
    }

    public void Connected(string observer, long ticks)
    {
        lock (sync)
        {
            if (id == "none" || endedCurrent) return;
            if (observer == "windows" && windowsTicks == 0) windowsTicks = ticks;
            if (observer == "gatt" && gattTicks == 0) gattTicks = ticks;
            status = "connected";
            AddNoLock(observer + "_active elapsed_attempt_ms=" + Milliseconds(attemptTicks, ticks));
        }
    }

    public void SetPdu(ushort value) { lock (sync) maxPdu = value; }
    public void Note(string detail, long ticks)
    {
        lock (sync)
        {
            if (id == "none") return;
            AddNoLock("t_attempt_ms=" + Milliseconds(attemptTicks, ticks) + " " + detail);
        }
    }
    private void AddNoLock(string detail)
    {
        control.Enqueue(detail.Replace('\r', ' ').Replace('\n', ' '));
        while (control.Count > 80) control.Dequeue();
    }

    public void RecordFd2(ReadOnlySpan<byte> data, long ticks, double callbackAgeMs)
    {
        lock (sync)
        {
            if (id == "none" || endedCurrent) return;
            frames++;
            if (firstFd2Ticks == 0) firstFd2Ticks = ticks;
            lastFd2Ticks = ticks;
            if (double.IsFinite(callbackAgeMs))
            {
                if (callbackAgeMs < 0) negativeCallbackAges++;
                else
                {
                    maxCallbackAgeMs = Math.Max(maxCallbackAgeMs, callbackAgeMs);
                    if (callbackAgeMs >= 15) delayedCallbacks++;
                }
            }
            if (data.Length < 46 || data.Length > previous.Length) { malformed++; return; }
            uint counter = BinaryPrimitives.ReadUInt32LittleEndian(data);
            uint motion = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(42, 4));
            if (hasPrevious)
            {
                if (data.Length == previousLength && data.SequenceEqual(previous.AsSpan(0, previousLength))) sameRaw++;
                if (data.Slice(4, 4).SequenceEqual(previous.AsSpan(4, 4)) &&
                    data.Slice(10, 6).SequenceEqual(previous.AsSpan(10, 6))) sameControls++;
                uint counterStep = unchecked(counter - previousCounter);
                counterSteps[Math.Min((int)Math.Min(counterStep, 17u), 17)]++;
                if (counterStep == 0) sameCounter++;
                if (motion == previousMotion) sameMotion++;
                else
                {
                    motionChanges++;
                    uint step = unchecked(motion - previousMotion);
                    if (step <= int.MaxValue) motionAdvance += step;
                    else motionBackwards++;
                }
            }
            data.CopyTo(previous);
            previousLength = data.Length;
            previousCounter = counter;
            previousMotion = motion;
            hasPrevious = true;
        }
    }

    public string End(string reason, long ticks)
    {
        lock (sync)
        {
            if (id == "none") return "";
            if (!endedCurrent)
            {
                status = reason;
                endedCurrent = true;
                endTicks = ticks;
                AddNoLock("end reason=" + reason + " t_attempt_ms=" + Milliseconds(attemptTicks, ticks));
            }
            return FormatNoLock(ticks);
        }
    }

    public string Summary(long ticks)
    {
        lock (sync) return FormatNoLock(ticks);
    }

    public string DiagnosticText(long ticks)
    {
        lock (sync)
        {
            var text = new StringBuilder("BLE 初始化与停流诊断（只读缓存）\n");
            text.AppendLine(FormatNoLock(ticks));
            foreach (string item in control) text.AppendLine("[BLE_CONTROL_EVIDENCE] attempt=" + id + " " + item);
            if (ended.Count > 0)
            {
                text.AppendLine("此前连接摘要（最多 3 次；重连不会抹掉停流证据）");
                foreach (string item in ended) text.AppendLine(item);
            }
            text.AppendLine("Windows/GATT 建链时间是应用观察值，非空口时间；时间戳变化和通知数量不独自证明物理采样率。HCI 断开原因未采集，不能由 Windows 状态猜测 0x08 或 LL 超时。");
            return text.ToString();
        }
    }

    private string FormatNoLock(long ticks)
    {
        double seconds = firstFd2Ticks > 0 && lastFd2Ticks > firstFd2Ticks ?
            (lastFd2Ticks - firstFd2Ticks) / (double)Stopwatch.Frequency : 0;
        double hz = seconds > 0 ? (frames - 1) / seconds : 0;
        string steps = string.Join(",", Enumerable.Range(0, counterSteps.Length)
            .Where(i => counterSteps[i] > 0).Select(i => (i == 17 ? "17+" : i.ToString()) + ":" + counterSteps[i]));
        return "[BLE_SESSION_EVIDENCE] attempt=" + id + " profile=" + profile + " status=" + status +
            " clock=host_observed_qpc last_fd2_after_windows_ms=" + Milliseconds(windowsTicks, lastFd2Ticks) +
            " last_fd2_after_gatt_ms=" + Milliseconds(gattTicks, lastFd2Ticks) +
            " now_after_windows_ms=" + Milliseconds(windowsTicks, ticks) +
            " now_after_gatt_ms=" + Milliseconds(gattTicks, ticks) +
            " end_after_windows_ms=" + Milliseconds(windowsTicks, endTicks) +
            " end_after_gatt_ms=" + Milliseconds(gattTicks, endTicks) +
            " fd2_count=" + frames + " fd2_active_span_s=" + Number(seconds) +
            " fd2_active_span_hz=" + Number(hz) + " high_notify_candidate=" + (hz >= 180).ToString().ToLowerInvariant() +
            " fd2_age_ms=" + Milliseconds(lastFd2Ticks, ticks) +
            " adjacent_same_raw=" + sameRaw + " adjacent_same_controls=" + sameControls +
            " adjacent_same_counter=" + sameCounter + " counter_steps=" + steps +
            " motion_timestamp_changes=" + motionChanges + " adjacent_same_motion_timestamp=" + sameMotion +
            " motion_timestamp_advance_raw=" + motionAdvance + " motion_backwards=" + motionBackwards +
            " malformed=" + malformed + " callback_delivery_age_max_ms=" + Number(maxCallbackAgeMs) +
            " callback_age_ge15=" + delayedCallbacks + " callback_age_negative=" + negativeCallbackAges +
            " winrt_max_pdu=" + maxPdu + " stable_250hz_proven=false";
    }

    private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
    private static string Milliseconds(long start, long end) => start > 0 && end >= start ?
        Number((end - start) * 1000.0 / Stopwatch.Frequency) : "unavailable";
}
