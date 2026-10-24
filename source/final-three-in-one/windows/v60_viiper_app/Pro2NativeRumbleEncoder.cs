using System;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Native Pro2 USB output and its BLE characteristic share the two 16-byte motor
/// blocks. At unity gain they are opaque: only report ID 02 becomes 00.
/// Do not route these bytes through the synthesized Xbox/PS5 vibration encoder.
/// Protocol: ndeadly/switch2_controller_research, hid_reports.md, Output 0x02.
/// Known 0x5n frames use the four packed ten-bit slots emitted by SDL's
/// EncodeHDRumble: frequency, amplitude, frequency, amplitude (little endian).
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
            if (!KnownFormat(report[1]) || !KnownFormat(report[17]))
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
            for (int frame = 0; frame < 3; frame++)
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
            if (!KnownFormat(report[side])) return true; // Never discard opaque commands as "silent".
            for (int frame = 0; frame < 3; frame++)
                if ((ReadFrame(report.Slice(side + 1 + frame * 5, 5)) & AmplitudeMask) != 0) return true;
        }
        return false;
    }

    private static bool KnownFormat(byte header) => (header & 0xf0) == 0x50;
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
