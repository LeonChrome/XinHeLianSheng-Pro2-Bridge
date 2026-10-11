using System;

namespace Y700Switch2V60Viiper;

// Decode first-generation Nintendo 4-byte low/high tones, then encode the
// existing physical Pro2 5-byte frame. This is parameter conversion, not PCM
// passthrough; frequency is bounded to the physical output's supported range.
public static class Switch1RumbleConverter
{
    // Existing repository switch1_protocol.cpp amplitude table (milli-units).
    private static readonly ushort[] Amplitudes =
    [0,10,12,14,17,20,24,28,33,40,47,56,67,80,95,112,117,123,128,134,140,146,
     152,159,166,173,181,189,198,206,215,225,230,235,240,245,251,256,262,268,273,
     279,286,292,298,305,311,318,325,332,340,347,355,362,370,378,387,395,404,413,
     422,431,440,450,460,470,480,491,501,512,524,535,547,559,571,584,596,609,623,
     636,650,665,679,694,709,725,741,757,773,790,808,825,843,862,881,900,920,940,
     960,981,1003];

    public readonly record struct Tones(ushort LowHz, ushort LowAmplitude, ushort HighHz, ushort HighAmplitude);

    public static Tones Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 4) throw new ArgumentException("Switch Pro rumble requires four bytes.", nameof(encoded));
        int highCode = ((encoded[0] | ((encoded[1] & 1) << 8)) >> 2) + 0x60;
        int lowCode = (encoded[2] & 0x7F) + 0x40;
        int highAmplitude = encoded[1] >> 1;
        int lowAmplitude = encoded[3] >= 0x40
            ? ((encoded[3] - 0x40) * 2 + (encoded[2] >> 7)) : 0;
        return new(Frequency(lowCode), Amplitude(lowAmplitude), Frequency(highCode), Amplitude(highAmplitude));
    }

    private static ushort Frequency(int code) =>
        (ushort)Math.Clamp(Math.Round(10 * Math.Pow(2, code / 32.0)), 40, 511);
    private static ushort Amplitude(int code) =>
        (ushort)((Amplitudes[Math.Clamp(code, 0, Amplitudes.Length - 1)] * 29000u + 501u) / 1003u);

    public static bool TryMap(ReadOnlySpan<byte> feedback, out Pro2OutputPacket packet, out string reason)
    {
        packet = default!; reason = "";
        if (feedback.Length != 10) { reason = "Switch Pro feedback must be ten bytes"; return false; }
        byte? led = (feedback[8] & 2) != 0 ? feedback[9] : null;
        if ((feedback[8] & 1) == 0)
        { reason = led.HasValue ? "led-only feedback has no physical Pro2 rumble payload" : "feedback has no rumble flag"; return false; }
        var left = Decode(feedback[..4]); var right = Decode(feedback.Slice(4, 4));
        packet = Pro2OutputPacketMapper.BuildRaw02Packet(left.LowHz, left.LowAmplitude, left.HighHz, left.HighAmplitude,
            right.LowHz, right.LowAmplitude, right.HighHz, right.HighAmplitude, "switch1-hd-converted") with { PlayerLedMask = led };
        return true;
    }
}
