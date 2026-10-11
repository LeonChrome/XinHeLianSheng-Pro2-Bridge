using System;
using System.Buffers.Binary;

namespace Y700Switch2V60Viiper;

// Passive FD2 byte 0x21, or read-only command 0B/04 in protocol fixtures.
// Do not reinterpret the undocumented flags word
// as primary-report Power Info: this controller returns 0000/0003 while
// primary Power Info is 1C (level 7, unplugged).
public readonly record struct Pro2ChargeStatus(ushort Code, ushort Flags)
{
    public bool Known => Code == 0 || Code == 0x20 || Code is >= 0x30 and <= 0x3f;
    public bool Charging => Code is >= 0x30 and <= 0x3f;
    public bool ExternalPower => Code == 0x20 || Charging;

    public static bool TryDecodeFd2(ReadOnlySpan<byte> payload, out Pro2ChargeStatus status)
    {
        status = default;
        if (payload.Length < 60) return false;
        // Offset 0x22 is NOT a high byte of charge state: it may contain
        // battery current on other controllers. FD2 charge state is one byte.
        status = new Pro2ChargeStatus(payload[0x21], 0);
        return true;
    }

    public static bool TryDecodeReply(ReadOnlySpan<byte> reply, out Pro2ChargeStatus status)
    {
        status = default;
        if (reply.Length < 12 || reply[0] != 0x0b || reply[1] != 1 ||
            reply[2] != 1 || reply[3] != 4 || reply[5] != 0x78) return false;
        status = new Pro2ChargeStatus(BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(8, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(10, 2)));
        return true;
    }

    public void ApplyTo(GamepadState state)
    {
        state.BatteryPowerKnown = Known;
        state.BatteryCharging = Known && Charging;
        state.BatteryExternalPower = Known && ExternalPower;
    }
}
