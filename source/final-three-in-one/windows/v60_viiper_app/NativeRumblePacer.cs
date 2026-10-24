using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Y700Switch2V60Viiper;

/// <summary>
/// An asynchronous one-shot pacing timer. Windows 10 1803+ supports the
/// CREATE_WAITABLE_TIMER_HIGH_RESOLUTION flag. No global timer resolution,
/// power policy or thread priority changes, and no busy waiting are used.
/// Timer resolution is not a guarantee of thread scheduling or BLE delivery.
/// https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw
/// https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-setwaitabletimerex
/// </summary>
public sealed class NativeRumblePacer : IDisposable
{
    private const uint HighResolution = 0x00000002;
    private const uint TimerModifyAndSynchronize = 0x00100002;
    private readonly object gate = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private string backend;
    private int operations;
    private bool disposed, resourcesDisposed;

    /// <param name="forceFallback">Deterministic offline testing only; the
    /// production writer should leave this false.</param>
    public NativeRumblePacer(bool forceFallback = false)
    {
        backend = "task_delay_fallback:not_supported";
        if (forceFallback)
        {
            backend = "task_delay_fallback:forced_test";
            return;
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)) return;
        try
        {
            SafeWaitHandle handle = CreateWaitableTimerExW(IntPtr.Zero, null,
                HighResolution, TimerModifyAndSynchronize);
            if (handle.IsInvalid)
            {
                backend = "task_delay_fallback:create_error_" + Marshal.GetLastWin32Error();
                handle.Dispose();
                return;
            }
            // Probe availability once; each actual wait owns its own timer.
            // A cancelled timer may remain signaled. Reusing that timer before
            // registered-wait teardown completes could consume the next signal.
            handle.Dispose();
            backend = "high_resolution_waitable_timer";
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            backend = "task_delay_fallback:" + ex.GetType().Name;
        }
    }

    public string Backend => Volatile.Read(ref backend);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        // Match Task.Delay's finite maximum in both backends. Negative/infinite
        // waits do not belong in a rumble packet pacing loop.
        if (delay < TimeSpan.Zero || delay.TotalMilliseconds > uint.MaxValue - 1d)
            throw new ArgumentOutOfRangeException(nameof(delay));
        CancellationTokenSource linked;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            operations++;
        }
        return DelayCoreAsync(delay, linked);
    }

    private async Task DelayCoreAsync(TimeSpan delay, CancellationTokenSource linked)
    {
        bool entered = false;
        try
        {
            CancellationToken token = linked.Token;
            await serial.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (delay == TimeSpan.Zero) return;
            if (Backend == "high_resolution_waitable_timer")
            {
                try
                {
                    await WaitNativeAsync(delay, token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (IsUnavailable(ex) || ex is Win32Exception)
                {
                    string reason = ex is Win32Exception native ? "arm_error_" + native.NativeErrorCode : ex.GetType().Name;
                    Volatile.Write(ref backend, "task_delay_fallback:" + reason);
                }
            }
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        finally
        {
            if (entered) serial.Release();
            linked.Dispose();
            lock (gate)
            {
                operations--;
                if (disposed && operations == 0) DisposeResources();
            }
        }
    }

    private static async Task WaitNativeAsync(TimeSpan delay, CancellationToken token)
    {
        SafeWaitHandle handle = CreateWaitableTimerExW(IntPtr.Zero, null,
            HighResolution, TimerModifyAndSynchronize);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        using var timer = new TimerWaitHandle(handle);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? registered = null;
        CancellationTokenRegistration cancellation = default;
        try
        {
            // Arm before registering. A synchronization timer retains its
            // signaled state until a waiter consumes it, so a short expiry
            // between arming and registration is not lost.
            long due = -Math.Max(1, delay.Ticks); // relative, in 100ns units
            if (!SetWaitableTimerEx(timer.SafeWaitHandle, ref due, 0,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = ThreadPool.RegisterWaitForSingleObject(timer,
                static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                completed, Timeout.Infinite, executeOnlyOnce: true);
            cancellation = token.Register(static state =>
            {
                var data = ((TaskCompletionSource<bool> Completion, CancellationToken Token))state!;
                data.Completion.TrySetCanceled(data.Token);
            }, (completed, token));
            await completed.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            cancellation.Dispose();
            // Cancel does not signal a wait. Cancellation completes the TCS
            // separately; always unregister even on failed arming/cancellation.
            try { CancelWaitableTimer(timer.SafeWaitHandle); }
            catch (Exception ex) when (IsUnavailable(ex)) { }
            finally { registered?.Unregister(null); }
            // Unregister(null) can leave a callback in flight. .NET registered
            // waits retain a reference to this SafeWaitHandle until removed;
            // disposing our owner cannot close a handle still in that wait.
            // Every operation has a DIFFERENT timer and TCS: neither an old
            // signaled timer nor its pending registration is ever reused by a
            // new delay. Callbacks touch only their own TCS, never the timer.
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            // Only our linked, asynchronous delay callbacks are registered.
            // Existing DelayAsync tasks complete cancelled and own final cleanup.
            lifetime.Cancel();
            if (operations == 0) DisposeResources();
        }
    }

    private void DisposeResources()
    {
        if (resourcesDisposed) return;
        resourcesDisposed = true;
        lifetime.Dispose();
        serial.Dispose();
    }

    private static bool IsUnavailable(Exception ex) => ex is DllNotFoundException or
        EntryPointNotFoundException or PlatformNotSupportedException;

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes,
        string? name, uint flags, uint desiredAccess);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime,
        int period, IntPtr completion, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
