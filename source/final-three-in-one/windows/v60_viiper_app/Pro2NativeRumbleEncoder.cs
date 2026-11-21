using System;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Native Pro2 USB output and its BLE characteristic share the two 16-byte motor
/// blocks. At unity gain they are opaque: only report ID 02 becomes 00.
/// Do not route these bytes through the synthesized Xbox/PS5 vibration encoder.
/// Protocol: ndeadly/switch2_controller_research, hid_reports.md, Output 0x02.
/// Headers 0x5n, 0x6n and 0x7n declare 1, 2 and 3 five-byte samples.
/// Each sample contains two little-endian ten-bit frequency/amplitude pairs.
/// </summary>
public static class Pro2NativeRumbleEncoder
{
    public const int PacketSize = 33;
    private const ulong AmplitudeMask = (0x3ffUL << 10) | (0x3ffUL << 30);

    public static bool TryEncode(ReadOnlySpan<byte> report, Span<byte> packet,
        double gain, out bool active, out string error)
    {
        active = false;
        error = "";
        if (report.Length != 64 || report[0] != 2 || packet.Length < PacketSize)
        {
            error = "native Pro2 output requires a 64-byte report 02 and a 33-byte BLE buffer";
            return false;
        }
        gain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 3) : 1;
        // Preserve sequence/control bytes, all three subframes, both motors,
        // including neutral/unknown payloads. No amplitude normalization at 100%.
        report.Slice(0, PacketSize).CopyTo(packet);
        packet[0] = 0;
        if (gain != 1)
        {
            if (!HasKnownBlocks(report))
            {
                if (gain == 0)
                {
                    // Explicit mute can always send a known neutral instead of
                    // attempting to interpret an unknown motor-block format.
                    WriteNeutral(packet);
                    return true;
                }
                packet.Slice(0, PacketSize).Clear();
                error = "unknown Pro2 rumble format; use 100% for unchanged native forwarding";
                return false;
            }
            for (int side = 1; side <= 17; side += 16)
            for (int frame = 0; frame < SampleCount(report[side]); frame++)
            {
                Span<byte> data = packet.Slice(side + 1 + frame * 5, 5);
                ulong packed = ReadFrame(data);
                ulong first = Scale((packed >> 10) & 0x3ff, gain);
                ulong second = Scale((packed >> 30) & 0x3ff, gain);
                packed = (packed & ~AmplitudeMask) | (first << 10) | (second << 30);
                for (int i = 0; i < 5; i++) data[i] = (byte)(packed >> (i * 8));
            }
        }
        active = HasEffect(packet);
        return true;
    }

    public static bool HasEffect(ReadOnlySpan<byte> report)
    {
        if (report.Length < PacketSize) return false;
        for (int side = 1; side <= 17; side += 16)
        {
            if (!IsKnownBlock(report[side])) return true; // Never discard opaque commands as "silent".
            for (int frame = 0; frame < SampleCount(report[side]); frame++)
                if ((ReadFrame(report.Slice(side + 1 + frame * 5, 5)) & AmplitudeMask) != 0) return true;
        }
        return false;
    }

    public static bool IsKnownBlock(byte header) => (header >> 4) is >= 5 and <= 7;
    public static int SampleCount(byte header) => (header >> 4) - 4;
    public static bool HasKnownBlocks(ReadOnlySpan<byte> report) => report.Length >= PacketSize &&
        IsKnownBlock(report[1]) && IsKnownBlock(report[17]);

    // An empty pair from the Pro1 decoder carries no new samples. Keep its
    // original bytes for physical Pro1 sinks, without writing a Pro2 stop.
    public static bool IsEmptyPro1Update(Pro2OutputPacket packet) =>
        packet.NativePro1Rumble is {Length: 8} && packet.Report.Length == 64 &&
        (packet.Report[1] & 0xf0) == 0x40 && (packet.Report[17] & 0xf0) == 0x40;

    public static bool TryEncodeUsb(Pro2OutputPacket source, Span<byte> output, byte sequence, double gain,
        out string error)
    {
        if (output.Length < 64) { error = "Pro2 USB output requires 64 bytes"; return false; }
        bool encoded = source.PreserveNativePayload
            ? TryEncode(source.Report, output, gain, out _, out error)
            : Pro2BleRumblePacketEncoder.TryEncodeRaw02(source.Report, sequence, output, out _, out error, gain);
        if (!encoded) return false;
        output[0] = 0x02;
        source.Report.AsSpan(PacketSize).CopyTo(output[PacketSize..]);
        return true;
    }
    private static ulong Scale(ulong amplitude, double gain) =>
        (ulong)Math.Clamp(Math.Round(amplitude * gain, MidpointRounding.AwayFromZero), 0, 1023);
    private static ulong ReadFrame(ReadOnlySpan<byte> data)
    {
        ulong packed = 0;
        for (int i = 0; i < 5; i++) packed |= (ulong)data[i] << (i * 8);
        return packed;
    }
    private static void WriteNeutral(Span<byte> packet)
    {
        byte leftSequence = (byte)(packet[1] & 0x0f);
        byte rightSequence = (byte)(packet[17] & 0x0f);
        packet.Slice(0, PacketSize).Clear();
        for (int side = 1; side <= 17; side += 16)
        {
            packet[side] = (byte)(0x50 | (side == 1 ? leftSequence : rightSequence));
            // SDL's native zero-amplitude frame (391 / 274 frequency fields).
            packet[side + 1] = 0x87;
            packet[side + 2] = 0x01;
            packet[side + 3] = 0x20;
            packet[side + 4] = 0x11;
        }
    }

    public static byte[] Neutralize(ReadOnlySpan<byte> blePacket)
    {
        byte[] packet = new byte[PacketSize];
        if (blePacket.Length >= PacketSize) blePacket.Slice(0, PacketSize).CopyTo(packet);
        WriteNeutral(packet);
        return packet;
    }
}
