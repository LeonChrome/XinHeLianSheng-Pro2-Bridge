using System;

namespace Y700Switch2V60Viiper;

[Flags]
public enum GamepadButtons : ulong
{
    None = 0,
    South = 1UL << 0,
    East = 1UL << 1,
    West = 1UL << 2,
    North = 1UL << 3,
    L1 = 1UL << 4,
    R1 = 1UL << 5,
    L2 = 1UL << 6,
    R2 = 1UL << 7,
    Back = 1UL << 8,
    Start = 1UL << 9,
    LeftStick = 1UL << 10,
    RightStick = 1UL << 11,
    DPadDown = 1UL << 12,
    DPadRight = 1UL << 13,
    DPadLeft = 1UL << 14,
    DPadUp = 1UL << 15,
    Home = 1UL << 16,
    Capture = 1UL << 17,
    PaddleRight = 1UL << 18,
    PaddleLeft = 1UL << 19,
    Aux = 1UL << 20
}

public sealed class GamepadState
{
    public const ushort AxisCenter = 2048;
    public const ushort AxisMax = 4095;
    public const ushort TriggerMax = 4095;
    public const byte BatteryUnknown = 255;

    public GamepadButtons Buttons { get; set; }
    public ushort Lx { get; set; } = AxisCenter;
    public ushort Ly { get; set; } = AxisCenter;
    public ushort Rx { get; set; } = AxisCenter;
    public ushort Ry { get; set; } = AxisCenter;
    public ushort L2 { get; set; }
    public ushort R2 { get; set; }
    public bool AccelValid { get; set; }
    public bool GyroValid { get; set; }
    public short AccelX { get; set; }
    public short AccelY { get; set; }
    public short AccelZ { get; set; }
    public short GyroX { get; set; }
    public short GyroY { get; set; }
    public short GyroZ { get; set; }
    public SwitchImuRawSample[] SwitchRawImuSamples { get; set; } = [];
    public int SwitchRawImuOffset { get; set; } = -1;
    public string SwitchRawImuBytesHex { get; set; } = "";
    // Native Switch 2 motion clock from FD2, in microseconds. This is kept
    // separate from SourceTimestampTicks, which measures Windows delivery.
    public uint MotionTimestampUs { get; set; }
    public long SourceTimestampTicks { get; set; }
    public ulong RawNotificationSequence { get; set; }
    public byte BatteryPercent { get; set; } = BatteryUnknown;
    public bool BatteryCharging { get; set; }
    public uint Updates { get; set; }

    public bool IsPressed(GamepadButtons button) => (Buttons & button) != 0;

    public GamepadState Clone()
    {
        return (GamepadState)MemberwiseClone();
    }

    public static GamepadState Neutral()
    {
        return new GamepadState();
    }
}

public interface IGamepadInputSource : IAsyncDisposable
{
    bool IsRunning { get; }
    string Status { get; }
    bool TryGetLatest(out GamepadState state, out TimeSpan age);
}

public interface IGamepadInputMetricsSource
{
    string MetricsSummary { get; }
}

public interface IGamepadInputRateSource
{
    double CurrentParsedRateHz { get; }
}

public interface IGamepadSequentialInputSource
{
    bool TryGetNext(out GamepadState state, out TimeSpan age);
}

// A new virtual USB session must not replay BLE frames accumulated while the
// previous device was being removed and the replacement was enumerating.
public interface IGamepadSessionInputSource
{
    int PrepareForConsumerSession();
}

// A low-latency ordered consumer receives only newly arrived source frames.
// When the source queue is empty, the virtual device owns latest-state reuse.
public interface IGamepadFreshSequentialInputSource
{
    bool TryGetNextFresh(out GamepadState state, out TimeSpan age);
}

// A real-time consumer may discard already-stale queued notifications and
// publish only the newest state. Axis and IMU values are never altered.
public interface IGamepadRealtimeInputSource
{
    bool TryGetNewest(out GamepadState state, out TimeSpan age, out int supersededCount);
}

public interface IGamepadRuntimeTelemetrySink
{
    void ReportViiperPushRate(double actualHz);
}

public interface IGamepadOutputSink
{
    bool IsOutputReady { get; }
    bool TryWriteOutputReport(ReadOnlySpan<byte> report, out string error);
}
