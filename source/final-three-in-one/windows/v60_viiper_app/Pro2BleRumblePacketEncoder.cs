using System;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Final transport for already-packed Pro2 parameters, including converted
/// Pro1/Xbox/PS5 feedback. A packed ten-bit amplitude is not a fresh 0..29000
/// input: normalising it again used to boost it by about 2.26 and reverse tones.
/// Keep the same five-byte layout as the native USB-to-BLE path.
/// </summary>
public static class Pro2BleRumblePacketEncoder
{
    public const int Raw02ReportSize = 64;
    public const int BlePacketSize = 33;

    public static bool TryEncodeRaw02(
        ReadOnlySpan<byte> report,
        byte packetId,
        Span<byte> packet,
        out bool active,
        out string error,
        double gain = 1.0)
    {
        active = false;
        error = "";
        if (packet.Length < BlePacketSize)
        {
            error = "BLE rumble packet buffer too small";
            return false;
        }
        if (report.Length != Raw02ReportSize || report[0] != 0x02)
        {
            error = "raw02 HID report must be 64 bytes with report id 0x02";
            return false;
        }
        if (!Pro2NativeRumbleEncoder.HasKnownBlocks(report))
        {
            error = "converted raw02 HID report requires one to three samples per motor";
            return false;
        }

        // Reuse the audited amplitude-only gain operation. At 100%, every
        // subframe survives byte-for-byte, including frequency/control bits.
        // This does not label the source native: PreserveNativePayload remains
        // false for converted feedback, and the caller owns its sequence.
        if (!Pro2NativeRumbleEncoder.TryEncode(report, packet, gain, out active, out error))
            return false;
        packet[1] = (byte)((packet[1] & 0xf0) | (packetId & 0x0f));
        packet[17] = (byte)((packet[17] & 0xf0) | (packetId & 0x0f));
        return true;
    }
}
