using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class SendV55HapticAudioTest
{
    private const int SampleRate = 48000;
    private const int Channels = 4;
    private const int BitsPerSample = 16;
    private const int BytesPerSample = 2;
    private const int WaveFormatExtensible = 0xfffe;
    private const int WhdrDone = 0x00000001;
    private static readonly Guid PcmSubFormat = new Guid("00000001-0000-0010-8000-00aa00389b71");

    public static int Main(string[] args)
    {
        Options options = Options.Parse(args);
        try
        {
            List<WaveOutDevice> devices = EnumerateDevices();
            if (options.ListDevices)
            {
                Console.WriteLine("[V5_5_HAPTIC_AUDIO_TEST] device_count=" + devices.Count);
                foreach (WaveOutDevice device in devices)
                {
                    Console.WriteLine("[V5_5_HAPTIC_AUDIO_TEST] id=" + device.Id + " endpoint=\"" + device.Name + "\" channels=" + device.Channels);
                }
                return 0;
            }

            WaveOutDevice target = FindDevice(devices, options.DeviceName);
            if (target == null)
            {
                Console.Error.WriteLine("[V5_5_HAPTIC_AUDIO_TEST] sent=false error=endpoint_not_found device_name=\"" + options.DeviceName + "\"");
                foreach (WaveOutDevice device in devices)
                {
                    Console.Error.WriteLine("[V5_5_HAPTIC_AUDIO_TEST] candidate id=" + device.Id + " endpoint=\"" + device.Name + "\"");
                }
                return 2;
            }

            byte[] pcm = BuildPattern(options.Pattern, options.DurationMs, options.Intensity);
            IntPtr hWaveOut;
            WaveFormatExtensibleFormat format = MakeFormat();
            int rc = waveOutOpen(out hWaveOut, target.Id, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (rc != 0)
            {
                Console.Error.WriteLine("[V5_5_HAPTIC_AUDIO_TEST] sent=false error=waveOutOpen_" + rc + " detail=\"" + WaveError(rc) + "\" endpoint=\"" + target.Name + "\"");
                return 3;
            }

            GCHandle dataHandle = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            WaveHeader header = new WaveHeader();
            header.Data = dataHandle.AddrOfPinnedObject();
            header.BufferLength = pcm.Length;
            try
            {
                rc = waveOutPrepareHeader(hWaveOut, ref header, Marshal.SizeOf(typeof(WaveHeader)));
                if (rc != 0) throw new InvalidOperationException("waveOutPrepareHeader_" + rc + " " + WaveError(rc));
                rc = waveOutWrite(hWaveOut, ref header, Marshal.SizeOf(typeof(WaveHeader)));
                if (rc != 0) throw new InvalidOperationException("waveOutWrite_" + rc + " " + WaveError(rc));

                int waitMs = Math.Max(options.DurationMs + 300, 400);
                int elapsed = 0;
                while ((header.Flags & WhdrDone) == 0 && elapsed < waitMs)
                {
                    Thread.Sleep(10);
                    elapsed += 10;
                }
                Console.WriteLine("[V5_5_HAPTIC_AUDIO_TEST] endpoint=\"" + target.Name + "\" device_id=" + target.Id +
                                  " channels=4 sample_rate=48000 bits=16 pattern=" + options.Pattern +
                                  " duration_ms=" + options.DurationMs + " intensity=" + options.Intensity +
                                  " sent=true bytes=" + pcm.Length);
                return 0;
            }
            finally
            {
                waveOutUnprepareHeader(hWaveOut, ref header, Marshal.SizeOf(typeof(WaveHeader)));
                waveOutClose(hWaveOut);
                if (dataHandle.IsAllocated) dataHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[V5_5_HAPTIC_AUDIO_TEST] sent=false error=\"" + ex.Message.Replace("\"", "'") + "\"");
            return 1;
        }
    }

    private static WaveOutDevice FindDevice(List<WaveOutDevice> devices, string selector)
    {
        int id;
        if (int.TryParse(selector, out id))
        {
            foreach (WaveOutDevice device in devices)
            {
                if (device.Id == id) return device;
            }
        }
        foreach (WaveOutDevice device in devices)
        {
            if (device.Name.IndexOf(selector ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return device;
            }
        }
        return null;
    }

    private static List<WaveOutDevice> EnumerateDevices()
    {
        List<WaveOutDevice> devices = new List<WaveOutDevice>();
        uint count = waveOutGetNumDevs();
        for (int i = 0; i < count; i++)
        {
            WaveOutCaps caps;
            int rc = waveOutGetDevCaps(new IntPtr(i), out caps, Marshal.SizeOf(typeof(WaveOutCaps)));
            if (rc == 0)
            {
                devices.Add(new WaveOutDevice { Id = i, Name = caps.Name, Channels = caps.Channels });
            }
        }
        return devices;
    }

    private static WaveFormatExtensibleFormat MakeFormat()
    {
        WaveFormatExtensibleFormat format = new WaveFormatExtensibleFormat();
        format.FormatTag = WaveFormatExtensible;
        format.Channels = Channels;
        format.SamplesPerSec = SampleRate;
        format.BitsPerSample = BitsPerSample;
        format.BlockAlign = Channels * BytesPerSample;
        format.AvgBytesPerSec = (uint)(SampleRate * format.BlockAlign);
        format.Size = 22;
        format.ValidBitsPerSample = BitsPerSample;
        format.ChannelMask = 0x00000033;
        format.SubFormat = PcmSubFormat;
        return format;
    }

    private static byte[] BuildPattern(string pattern, int durationMs, int intensity)
    {
        int frames = (int)((long)SampleRate * Math.Max(1, durationMs) / 1000L);
        byte[] pcm = new byte[frames * Channels * BytesPerSample];
        float amp = Clamp(intensity, 0, 100) / 100.0f;
        for (int frame = 0; frame < frames; frame++)
        {
            float left;
            float right;
            PatternValue(pattern, frame, amp, out left, out right);
            WriteSample(pcm, frame, 2, left);
            WriteSample(pcm, frame, 3, right);
        }
        return pcm;
    }

    private static void WriteSample(byte[] pcm, int frame, int channel, float value)
    {
        int sample = Clamp((int)(value * 32767.0f), -32768, 32767);
        int offset = (frame * Channels + channel) * BytesPerSample;
        pcm[offset] = (byte)(sample & 0xff);
        pcm[offset + 1] = (byte)((sample >> 8) & 0xff);
    }

    private static void PatternValue(string pattern, int frame, float amp, out float left, out float right)
    {
        string p = (pattern ?? "").ToLowerInvariant();
        float l = 0;
        float r = 0;
        if (p == "ch2_tick") l = Burst(frame, 40, 180);
        else if (p == "ch3_tick") r = Burst(frame, 40, 180);
        else if (p == "both_tick") l = r = Burst(frame, 40, 180);
        else if (p == "ch2_punch") l = Burst(frame, 120, 95);
        else if (p == "ch3_punch") r = Burst(frame, 120, 95);
        else if (p == "both_punch") l = r = Burst(frame, 120, 95);
        else if (p == "continuous") l = r = Sine(frame, 160) * 0.55f;
        else if (p == "texture")
        {
            l = Noise(frame) * 0.38f + Sine(frame, 240) * 0.14f;
            r = Noise(frame + 17) * 0.36f + Sine(frame, 260) * 0.14f;
        }
        else if (p == "sweep")
        {
            float hz = 80.0f + 260.0f * (frame / (float)SampleRate);
            l = r = Sine(frame, hz) * 0.45f;
        }
        else if (p == "silence")
        {
            l = r = 0;
        }
        else
        {
            l = r = Burst(frame, 40, 180);
        }
        left = l * amp;
        right = r * amp;
    }

    private static float Sine(int frame, float hz)
    {
        return (float)Math.Sin(2.0 * Math.PI * hz * frame / SampleRate);
    }

    private static float Burst(int frame, int ms, float hz)
    {
        int burstFrames = SampleRate * ms / 1000;
        if (frame >= burstFrames) return 0.0f;
        float fade = 1.0f - frame / (float)Math.Max(1, burstFrames);
        return Sine(frame, hz) * fade;
    }

    private static float Noise(int frame)
    {
        uint x = (uint)(frame * 1664525 + 1013904223);
        return (((x >> 8) & 0xffff) / 32768.0f) - 1.0f;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string WaveError(int rc)
    {
        StringBuilder builder = new StringBuilder(256);
        int textRc = waveOutGetErrorText(rc, builder, builder.Capacity);
        return textRc == 0 ? builder.ToString() : "unknown";
    }

    private sealed class Options
    {
        public string Pattern = "both_tick";
        public int DurationMs = 600;
        public int Intensity = 50;
        public string DeviceName = "Wireless Controller";
        public bool ListDevices;

        public static Options Parse(string[] args)
        {
            Options options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--list", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-ListDevices", StringComparison.OrdinalIgnoreCase))
                {
                    options.ListDevices = true;
                }
                else if (arg.Equals("--pattern", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-Pattern", StringComparison.OrdinalIgnoreCase))
                {
                    options.Pattern = Next(args, ref i, arg);
                }
                else if (arg.Equals("--duration-ms", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-DurationMs", StringComparison.OrdinalIgnoreCase))
                {
                    options.DurationMs = Math.Max(1, int.Parse(Next(args, ref i, arg)));
                }
                else if (arg.Equals("--intensity", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-Intensity", StringComparison.OrdinalIgnoreCase))
                {
                    options.Intensity = Clamp(int.Parse(Next(args, ref i, arg)), 0, 100);
                }
                else if (arg.Equals("--device-name", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-DeviceName", StringComparison.OrdinalIgnoreCase))
                {
                    options.DeviceName = Next(args, ref i, arg);
                }
            }
            return options;
        }

        private static string Next(string[] args, ref int index, string arg)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + arg);
            index++;
            return args[index];
        }
    }

    private sealed class WaveOutDevice
    {
        public int Id;
        public string Name;
        public int Channels;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WaveOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved1;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensibleFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveOutGetDevCaps(IntPtr deviceId, out WaveOutCaps caps, int capsSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hWaveOut, int deviceId, ref WaveFormatExtensibleFormat format, IntPtr callback, IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, ref WaveHeader header, int headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, ref WaveHeader header, int headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, ref WaveHeader header, int headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveOutGetErrorText(int error, StringBuilder text, int textLength);
}
