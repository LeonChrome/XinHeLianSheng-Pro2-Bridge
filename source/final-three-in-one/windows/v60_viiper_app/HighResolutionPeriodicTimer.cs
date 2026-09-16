using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Y700Switch2V60Viiper;

public sealed class HighResolutionPeriodicTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitFailed = 0xFFFFFFFF;
    private readonly TimeSpan period;
    private readonly SafeWaitHandle? timer;
    private readonly Stopwatch scheduleWatch = Stopwatch.StartNew();
    private readonly long intervalStopwatchTicks;
    private long fallbackNextTicks;
    private bool disposed;

    public HighResolutionPeriodicTimer(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        this.period = period;
        intervalStopwatchTicks = ToStopwatchTicks(period);
        fallbackNextTicks = intervalStopwatchTicks;

        if (!OperatingSystem.IsWindows())
        {
            Backend = "stopwatch_fallback";
            return;
        }

        timer = CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        }

        if (timer.IsInvalid)
        {
            timer.Dispose();
            timer = null;
            Backend = "stopwatch_fallback";
            return;
        }

        ArmNativeTimer();

        Backend = "high_resolution_waitable_timer_absolute";
    }

    public string Backend { get; }

    public bool WaitForNextTick(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (timer != null)
        {
            uint result = WaitForSingleObject(timer, 1000);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == WaitObject0)
            {
                AdvanceDeadline();
                ArmNativeTimer();
                return true;
            }

            if (result == WaitFailed)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "High-resolution timer wait failed.");
            }

            throw new TimeoutException("High-resolution timer did not signal within one second.");
        }

        WaitWithStopwatch(cancellationToken);
        return true;
    }

    private void WaitWithStopwatch(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = fallbackNextTicks - scheduleWatch.ElapsedTicks;
            if (remaining <= 0)
            {
                AdvanceDeadline();
                return;
            }

            double remainingMilliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            if (remainingMilliseconds > 2)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(80);
            }
        }
    }

    private void AdvanceDeadline()
    {
        fallbackNextTicks += intervalStopwatchTicks;
        long now = scheduleWatch.ElapsedTicks;
        if (fallbackNextTicks <= now)
        {
            long missed = ((now - fallbackNextTicks) / intervalStopwatchTicks) + 1;
            fallbackNextTicks += missed * intervalStopwatchTicks;
        }
    }

    private void ArmNativeTimer()
    {
        if (timer == null)
        {
            return;
        }

        long remainingStopwatchTicks = Math.Max(
            1,
            fallbackNextTicks - scheduleWatch.ElapsedTicks);
        long remainingTimeSpanTicks = Math.Max(
            1,
            checked((long)Math.Round(
                remainingStopwatchTicks *
                (double)TimeSpan.TicksPerSecond /
                Stopwatch.Frequency)));
        long dueTime = -remainingTimeSpanTicks;
        if (!SetWaitableTimerEx(
                timer,
                ref dueTime,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to arm the high-resolution waitable timer.");
        }
    }

    private static long ToStopwatchTicks(TimeSpan value)
    {
        return Math.Max(
            1,
            checked((long)Math.Round(value.TotalSeconds * Stopwatch.Frequency)));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer?.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(
        IntPtr timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(
        SafeWaitHandle timer,
        ref long dueTime,
        int periodMilliseconds,
        IntPtr completionRoutine,
        IntPtr completionRoutineArgument,
        IntPtr wakeContext,
        uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
