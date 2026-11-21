using System;
using System.Buffers.Binary;

namespace Y700Switch2V60Viiper;

// Each HID connection owns one decoder. Consecutive, even byte-identical,
// commands can change pitch or level relative to the preceding sample.
// Field layout: https://switchbrew.org/wiki/Joy-Con#VibrationAmFmPackFormat
public sealed class Switch1RumbleStream
{
    private readonly Motor left = new();
    private readonly Motor right = new();

    public bool TryMap(ReadOnlySpan<byte> feedback, out Pro2OutputPacket packet, out string reason)
    {
        packet = default!;
        reason = "";
        if (feedback.Length != 10) { reason = "Switch Pro feedback must be ten bytes"; return false; }
        byte? led = (feedback[8] & 2) != 0 ? feedback[9] : null;
        if ((feedback[8] & 1) == 0)
        {
            reason = led.HasValue ? "led-only feedback has no physical Pro2 rumble payload" : "feedback has no rumble flag";
            return false;
        }

        byte[] report = new byte[64];
        report[0] = 2;
        int leftCount = left.Decode(BinaryPrimitives.ReadUInt32LittleEndian(feedback), report.AsSpan(2, 15));
        int rightCount = right.Decode(BinaryPrimitives.ReadUInt32LittleEndian(feedback[4..]), report.AsSpan(18, 15));
        // Zero means no new samples on this motor. While the other motor
        // plays its samples, keep the previous value for the same interval.
        if (leftCount == 0 && rightCount != 0) leftCount = left.Hold(report.AsSpan(2, 15), rightCount);
        if (rightCount == 0 && leftCount != 0) rightCount = right.Hold(report.AsSpan(18, 15), leftCount);
        report[1] = (byte)(0x40 | leftCount << 4);
        report[17] = (byte)(0x40 | rightCount << 4);
        packet = new(report, "switch1-hd-converted", leftCount != 0 && Pro2NativeRumbleEncoder.HasEffect(report), led,
            ConversionTelemetry: $"frequency_mapping=pro2_log96 samples_left={leftCount} samples_right={rightCount}",
            NativePro1Rumble: feedback[..8].ToArray());
        return true;
    }

    // Pitch and amplitude commands share a 1/32-octave unit. The 5-bit
    // opcodes form a 5x3 grid of amplitude/pitch deltas after the presets.
    private struct Band
    {
        public int Pitch;
        public int Level;
        public readonly double Amplitude => Level < -254 ? 0 : Math.Pow(2, Level / 32.0);

        public void AbsoluteLevel(int code)
        {
            Level = code switch { 0 => -256, < 16 => 8 * code - 248, < 32 => 2 * code - 158, _ => code - 127 };
        }

        public void Command(int code)
        {
            if (code == 0) { Pitch = 0; Level = -256; }
            else if (code < 12) Level = (1 - code) * 16;
            else if (code < 17) Pitch = (code - 14) * 6;
            else
            {
                int index = code - 17;
                int amplitudeDelta = (index / 3) switch { 0 => 4, 1 => 1, 2 => 0, 3 => -1, _ => -4 };
                Level = Math.Clamp(Level + amplitudeDelta, -256, 0);
                Pitch = Math.Clamp(Pitch + 1 - index % 3, -64, 64);
            }
        }
    }

    private sealed class Motor
    {
        private Band low = new() { Level = -256 };
        private Band high = new() { Level = -256 };

        public int Decode(uint word, Span<byte> frames)
        {
            int format = (int)(word >> 30);
            int Field(int shift, int mask) => (int)(word >> shift) & mask;
            if (format == 0) return 0;
            int commandOffset = 20;
            int count = format;
            int start = 0;
            if (format == 1 && (word & 0xfffff) != 0)
            {
                if ((word & 3) == 0)
                {
                    high.Pitch = Field(2, 127) - 64;
                    high.AbsoluteLevel(Field(9, 127));
                    low.Pitch = Field(16, 127) - 64;
                    low.AbsoluteLevel(Field(23, 127));
                }
                else
                {
                    ref Band band = ref ((word & 1) != 0 ? ref high : ref low);
                    int code = Field(23, 127);
                    if ((word & 4) != 0) band.Pitch = code - 64;
                    else band.AbsoluteLevel(code);
                    count = 3;
                    commandOffset = 13;
                }
                WriteSample(frames);
                start = 1;
            }
            else if (format == 2 && (word & 1023) != 0)
            {
                bool highAbsolute = (word & 1) != 0;
                ref Band absolute = ref (highAbsolute ? ref high : ref low);
                ref Band other = ref (highAbsolute ? ref low : ref high);
                absolute.Pitch = Field(1, 127) - 64;
                absolute.AbsoluteLevel(Field(23, 127));
                other.Command(Field(18, 31));
                WriteSample(frames);
                start = 1;
                commandOffset = 8;
            }
            for (int sample = start; sample < count; sample++, commandOffset -= 10)
            {
                high.Command(Field(commandOffset, 31));
                low.Command(Field(commandOffset + 5, 31));
                WriteSample(frames[(sample * 5)..]);
            }
            return count;
        }

        public int Hold(Span<byte> frames, int count)
        {
            for (int i = 0; i < count; i++) WriteSample(frames[(i * 5)..]);
            return count;
        }

        private void WriteSample(Span<byte> frame)
        {
            // Nintendo EP4674492A1, paras 60-73: 96 pitch steps per octave,
            // 333 at 110 Hz. 160 and 320 Hz round to 385 and 481. Every
            // first-generation pitch step is exactly three destination steps.
            // Console wire comparison: nxnative_patch commit 3c6f7e8721.
            double lowLevel = low.Amplitude, highLevel = high.Amplitude;
            double scale = 0.98 * 1023 / Math.Max(1, lowLevel + highLevel);
            ulong lowGain = (ulong)Math.Ceiling(lowLevel * scale - 0.5);
            ulong highGain = (ulong)Math.Ceiling(highLevel * scale - 0.5);
            uint lowPitch = (uint)(385 + 3 * low.Pitch);
            uint highPitch = (uint)(481 + 3 * high.Pitch);
            ulong value = lowPitch | (lowGain << 10) | ((ulong)highPitch << 20) | (highGain << 30);
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)value);
            frame[4] = (byte)(value >> 32);
        }
    }
}
