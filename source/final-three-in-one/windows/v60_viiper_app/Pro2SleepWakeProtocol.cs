using System.Security.Cryptography;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Pure byte-level helpers for Switch 2 controller pairing / sleep (no I/O).
/// Layout per ndeadly/switch2_controller_research, verified on a real Pro2 (fw 1.1.5) 2026-11-01.
/// </summary>
public static class Pro2SleepWakeProtocol
{
    public const uint PairingFlashAddress = 0x1FA000;
    public const ushort NintendoCompanyId = 0x0553;

    /// <summary>06/02: controller drops the link and turns LEDs off; button press re-advertises when paired.</summary>
    public static byte[] SleepCommand()
    {
        byte[] request = new byte[20];
        new byte[] { 0x06, 0x91, 0x01, 0x02, 0x00, 0x0C, 0x00, 0x00 }.CopyTo(request, 0);
        return request;
    }

    /// <summary>Bluetooth address bytes in the controller's (reversed / little-endian) order.</summary>
    public static byte[] ReversedAddress(ulong address)
    {
        byte[] bytes = new byte[6];
        for (int i = 0; i < 6; i++) bytes[i] = (byte)(address >> (8 * i));
        return bytes;
    }

    /// <summary>15/01 with the two host addresses the console also sends (address, address with LSB-1).</summary>
    public static byte[] ExchangeAddressesCommand(ulong host)
    {
        byte[] first = ReversedAddress(host);
        byte[] second = (byte[])first.Clone();
        second[0] = (byte)(second[0] - 1);
        byte[] request = new byte[8 + 14];
        new byte[] { 0x15, 0x91, 0x01, 0x01, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x02 }.CopyTo(request, 0);
        first.CopyTo(request, 10);
        second.CopyTo(request, 16);
        return request;
    }

    public static byte[] ExchangeKeysCommand(byte[] a1) => KeyedCommand(0x04, a1);
    public static byte[] ConfirmCommand(byte[] a2) => KeyedCommand(0x02, a2);
    public static byte[] FinaliseCommand() => [0x15, 0x91, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00];

    private static byte[] KeyedCommand(byte subcommand, byte[] value)
    {
        if (value.Length != 16) throw new ArgumentException("16-byte value required", nameof(value));
        byte[] request = new byte[8 + 17];
        new byte[] { 0x15, 0x91, 0x01, subcommand, 0x00, 0x11, 0x00, 0x00, 0x00 }.CopyTo(request, 0);
        value.CopyTo(request, 9);
        return request;
    }

    /// <summary>LTK = A1 XOR B1 (both as transmitted).</summary>
    public static byte[] ComputeLtk(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> b1)
    {
        byte[] ltk = new byte[16];
        for (int i = 0; i < 16; i++) ltk[i] = (byte)(a1[i] ^ b1[i]);
        return ltk;
    }

    /// <summary>Expected 15/02 response: AES128-ECB(key = reverse(LTK), block = reverse(A2)).</summary>
    public static byte[] ExpectedConfirmResponse(byte[] ltk, byte[] a2)
    {
        byte[] key = (byte[])ltk.Clone(); Array.Reverse(key);
        byte[] block = (byte[])a2.Clone(); Array.Reverse(block);
        using Aes aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    /// <summary>
    /// True when the pairing area (starting at 0x1FA000) holds 1-2 entries and entry #1 is this host.
    /// The flash stores the host address in normal byte order at offset 0x08.
    /// </summary>
    public static bool IsHostStored(ReadOnlySpan<byte> pairingArea, ulong host) =>
        pairingArea.Length >= 14 && pairingArea[0] is >= 1 and <= 2 &&
        Convert.ToHexString(pairingArea.Slice(8, 6)) == host.ToString("X12");
}

/// <summary>
/// Idle detection from parsed controls only (buttons, sticks, triggers). IMU is ignored so a
/// controller lying on a desk is idle. Not thread-safe: the caller serialises access.
/// </summary>
public sealed class Pro2UserActivityTracker
{
    public const int AxisDelta = 320;      // ~8 % of the 12-bit range: above stick noise
    public const int AxisHeld = 900;       // stick deliberately held away from centre
    public const int TriggerDelta = 320;
    public const int TriggerHeld = 900;

    private bool initialised;
    private ulong buttons;
    private int lx = GamepadState.AxisCenter, ly = GamepadState.AxisCenter;
    private int rx = GamepadState.AxisCenter, ry = GamepadState.AxisCenter;
    private int l2, r2;

    public long LastActivityTicks { get; private set; }

    public void Reset(long nowTicks)
    {
        LastActivityTicks = nowTicks;
    }

    /// <returns>true when this sample counts as user activity.</returns>
    public bool Note(GamepadState s, long nowTicks)
    {
        ulong pressed = (ulong)s.Buttons;
        bool active = !initialised ||
            pressed != 0 || pressed != buttons ||
            Math.Abs(s.Lx - lx) > AxisDelta || Math.Abs(s.Ly - ly) > AxisDelta ||
            Math.Abs(s.Rx - rx) > AxisDelta || Math.Abs(s.Ry - ry) > AxisDelta ||
            Math.Abs(s.Lx - GamepadState.AxisCenter) > AxisHeld || Math.Abs(s.Ly - GamepadState.AxisCenter) > AxisHeld ||
            Math.Abs(s.Rx - GamepadState.AxisCenter) > AxisHeld || Math.Abs(s.Ry - GamepadState.AxisCenter) > AxisHeld ||
            Math.Abs(s.L2 - l2) > TriggerDelta || Math.Abs(s.R2 - r2) > TriggerDelta ||
            s.L2 > TriggerHeld || s.R2 > TriggerHeld;
        if (!active) return false;
        initialised = true;
        LastActivityTicks = nowTicks;
        buttons = pressed;
        lx = s.Lx; ly = s.Ly; rx = s.Rx; ry = s.Ry; l2 = s.L2; r2 = s.R2;
        return true;
    }
}
