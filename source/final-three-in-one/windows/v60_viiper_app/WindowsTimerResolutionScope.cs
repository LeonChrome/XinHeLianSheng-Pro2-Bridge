using System;
using System.Runtime.InteropServices;

namespace Y700Switch2V60Viiper;

public sealed class WindowsTimerResolutionScope : IDisposable
{
    private const uint TimePeriodMilliseconds = 1;
    private const uint TimerrNoError = 0;
    private bool disposed;

    private WindowsTimerResolutionScope(bool active, uint result)
    {
        IsActive = active;
        Result = result;
    }

    public bool IsActive { get; }
    public uint Result { get; }

    public static WindowsTimerResolutionScope Begin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsTimerResolutionScope(active: false, result: uint.MaxValue);
        }

        uint result = timeBeginPeriod(TimePeriodMilliseconds);
        return new WindowsTimerResolutionScope(result == TimerrNoError, result);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (IsActive)
        {
            _ = timeEndPeriod(TimePeriodMilliseconds);
        }
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint periodMilliseconds);
}
