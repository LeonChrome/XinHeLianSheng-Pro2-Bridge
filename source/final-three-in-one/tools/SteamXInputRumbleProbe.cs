using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public static class SteamXInputRumbleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(int dwUserIndex, ref XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern int XInputSetState14(int dwUserIndex, ref XINPUT_VIBRATION pVibration);

    public static int Main(string[] args)
    {
        int seconds = GetArg(args, "--seconds", 20);
        int pulseMs = GetArg(args, "--pulse-ms", 500);
        int gapMs = GetArg(args, "--gap-ms", 350);
        ushort low = (ushort)Math.Max(0, Math.Min(65535, GetArg(args, "--low", 60000)));
        ushort high = (ushort)Math.Max(0, Math.Min(65535, GetArg(args, "--high", 60000)));
        string logPath = GetStringArg(args, "--log", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamXInputRumbleProbe.log"));

        using (var log = new StreamWriter(logPath, true))
        {
            Log(log, "start seconds=" + seconds + " pulseMs=" + pulseMs + " gapMs=" + gapMs +
                " low=" + low + " high=" + high + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);

            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            int pulse = 0;
            while (DateTime.UtcNow < deadline)
            {
                for (int i = 0; i < 4; i++)
                {
                    XINPUT_STATE state = new XINPUT_STATE();
                    int getRc = XInputGetState14(i, ref state);
                    Log(log, "poll index=" + i + " getRc=" + getRc + " packet=" + state.dwPacketNumber +
                        " buttons=0x" + state.Gamepad.wButtons.ToString("x4"));
                }

                XINPUT_VIBRATION on = new XINPUT_VIBRATION();
                on.wLeftMotorSpeed = low;
                on.wRightMotorSpeed = high;
                for (int i = 0; i < 4; i++)
                {
                    int rc = XInputSetState14(i, ref on);
                    Log(log, "rumble-on pulse=" + pulse + " index=" + i + " rc=" + rc);
                }

                Thread.Sleep(Math.Max(1, pulseMs));

                XINPUT_VIBRATION off = new XINPUT_VIBRATION();
                for (int i = 0; i < 4; i++)
                {
                    int rc = XInputSetState14(i, ref off);
                    Log(log, "rumble-off pulse=" + pulse + " index=" + i + " rc=" + rc);
                }

                pulse++;
                Thread.Sleep(Math.Max(1, gapMs));
            }

            Log(log, "complete pulses=" + pulse);
        }

        return 0;
    }

    private static int GetArg(string[] args, string name, int defaultValue)
    {
        string value = GetStringArg(args, name, null);
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }
        int parsed;
        return int.TryParse(value, out parsed) ? parsed : defaultValue;
    }

    private static string GetStringArg(string[] args, string name, string defaultValue)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return defaultValue;
    }

    private static void Log(StreamWriter log, string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message;
        Console.WriteLine(line);
        log.WriteLine(line);
        log.Flush();
    }
}
