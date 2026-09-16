using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V55Manager;

public static class SerialCommandClient
{
    private sealed class SerialCommandTimeoutException : TimeoutException
    {
        public SerialCommandTimeoutException(string message) : base(message)
        {
        }
    }

    private static readonly SemaphoreSlim PortGate = new(1, 1);
    private static readonly object PortStateSync = new();
    private static SerialPort? activePort;
    private static string activePortName = "";
    private static long activeGeneration;
    private static int shuttingDown;
    private static readonly Regex AnsiEscape = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled);

    public static void Close()
    {
        CancelActiveOperations();
        SerialPort? port;
        bool gateTaken = PortGate.Wait(TimeSpan.FromMilliseconds(100));
        try
        {
            port = DetachActivePort();
        }
        finally
        {
            if (gateTaken)
            {
                PortGate.Release();
            }
        }

        if (port != null)
        {
            _ = Task.Run(() => DisposePort(port));
        }
    }

    public static async Task<bool> CloseAsync(int timeoutMs = 750)
    {
        int timeout = Math.Max(50, timeoutMs);
        CancelActiveOperations();
        SerialPort? port = null;
        if (!await PortGate.WaitAsync(TimeSpan.FromMilliseconds(timeout)))
        {
            port = DetachActivePort();
            if (port != null)
            {
                _ = Task.Run(() => DisposePort(port));
            }
            return false;
        }
        try
        {
            port = DetachActivePort();
        }
        finally
        {
            PortGate.Release();
        }

        if (port == null)
        {
            return true;
        }

        Task closeTask = Task.Run(() => DisposePort(port));
        Task completed = await Task.WhenAny(closeTask, Task.Delay(timeout));
        if (completed != closeTask)
        {
            return false;
        }
        await closeTask;
        return true;
    }

    public static void CloseInBackground()
    {
        _ = Task.Run(async () => await CloseAsync(750));
    }

    public static void Shutdown()
    {
        Volatile.Write(ref shuttingDown, 1);
        Close();
    }

    public static async Task<string> SendAsync(
        string portName,
        string command,
        int readSeconds,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref shuttingDown) != 0)
        {
            throw new OperationCanceledException("程序正在关闭，已取消串口命令。");
        }
        if (string.IsNullOrWhiteSpace(portName)) throw new InvalidOperationException("请先选择 ESP32 控制板对应的串口。");
        if (PortGate.CurrentCount == 0)
        {
            progress.Report("[SERIAL] waiting for " + portName);
        }

        int requestedReadSeconds = Math.Max(1, readSeconds);
        TimeSpan commandTimeout = TimeSpan.FromSeconds(
            Math.Clamp(requestedReadSeconds + 4, 5, 45));
        if (!await PortGate.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken))
        {
            throw new SerialCommandTimeoutException(
                "上一条串口命令仍未释放，暂时无法执行“" + command +
                "”。请等待几秒后重试；如果连续出现，请拔插 CH343P 控制口。");
        }

        long generation = Interlocked.Increment(ref activeGeneration);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<string> sendTask = Task.Run(
                () => SendLocked(portName, command, requestedReadSeconds, progress, linkedCts.Token, generation),
                CancellationToken.None);
            Task completed = await Task.WhenAny(sendTask, Task.Delay(commandTimeout, cancellationToken));
            if (completed == sendTask)
            {
                return await sendTask;
            }

            linkedCts.Cancel();
            InvalidateGeneration(generation);
            SerialPort? abandonedPort = DetachActivePort();
            if (abandonedPort != null)
            {
                _ = Task.Run(() => DisposePort(abandonedPort));
            }

            Task quickDrain = await Task.WhenAny(sendTask, Task.Delay(600, CancellationToken.None));
            if (quickDrain == sendTask)
            {
                try
                {
                    return await sendTask;
                }
                catch (OperationCanceledException)
                {
                    // Convert the internal cancellation into the user-facing timeout below.
                }
            }

            throw new SerialCommandTimeoutException(
                "串口命令“" + command + "”在 " +
                commandTimeout.TotalSeconds.ToString("F0") +
                " 秒内没有完成。已放弃本次 BLE/串口操作，避免界面卡死。请拔插 CH343P 控制口后再试。");
        }
        finally
        {
            PortGate.Release();
        }
    }

    private static string SendLocked(
        string portName,
        string command,
        int readSeconds,
        IProgress<string> progress,
        CancellationToken cancellationToken,
        long generation)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfGenerationStopped(generation, cancellationToken);
            try
            {
                var builder = new StringBuilder();
                SerialPort port = EnsureOpen(portName, progress, generation, cancellationToken);

                ThrowIfGenerationStopped(generation, cancellationToken);
                progress.Report("> " + command);
                port.WriteLine(command.TrimEnd());

                DateTime deadline = DateTime.UtcNow.AddSeconds(readSeconds);
                DateTime? responseSeenAt = null;
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfGenerationStopped(generation, cancellationToken);
                    string chunk = port.ReadExisting();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        string clean = AnsiEscape.Replace(chunk, "");
                        builder.Append(clean);
                        foreach (string line in clean.Replace("\r", "").Split('\n'))
                        {
                            if (ShouldReportSerialLine(line))
                            {
                                progress.Report(line.Trim());
                            }
                        }
                        if (clean.Contains("{\"ok\":", StringComparison.Ordinal) ||
                            clean.Contains("{\"cmd\":", StringComparison.Ordinal))
                        {
                            responseSeenAt = DateTime.UtcNow;
                        }
                    }
                    if (responseSeenAt.HasValue &&
                        DateTime.UtcNow - responseSeenAt.Value >= TimeSpan.FromMilliseconds(200))
                    {
                        break;
                    }
                    cancellationToken.WaitHandle.WaitOne(75);
                }
                return builder.ToString();
            }
            catch (Exception ex) when (IsPortFailure(ex) && attempt == 1)
            {
                progress.Report("[SERIAL] port error, reopening " + portName + ": " + ex.Message);
                CloseActivePort(generation);
                cancellationToken.WaitHandle.WaitOne(250);
            }
        }
    }

    private static SerialPort EnsureOpen(
        string portName,
        IProgress<string> progress,
        long generation,
        CancellationToken cancellationToken)
    {
        lock (PortStateSync)
        {
            if (IsGenerationCurrent(generation) &&
                activePort != null &&
                activePort.IsOpen &&
                string.Equals(activePortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                return activePort;
            }
        }

        CloseActivePort(generation);
        var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            NewLine = "\n",
            Encoding = Encoding.UTF8,
            ReadTimeout = 200,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None
        };

        port.Open();
        cancellationToken.ThrowIfCancellationRequested();
        port.DtrEnable = false;
        port.RtsEnable = false;
        if (!IsGenerationCurrent(generation))
        {
            DisposePort(port);
            throw new OperationCanceledException(cancellationToken);
        }

        lock (PortStateSync)
        {
            if (!IsGenerationCurrent(generation))
            {
                DisposePort(port);
                throw new OperationCanceledException(cancellationToken);
            }

            activePort = port;
            activePortName = portName;
        }
        progress.Report("[SERIAL] opened persistent " + portName);
        Thread.Sleep(350);
        try
        {
            port.DiscardInBuffer();
        }
        catch (InvalidOperationException)
        {
            CloseActivePort(generation);
            throw;
        }
        return port;
    }

    private static void CloseActivePort(long generation)
    {
        if (!IsGenerationCurrent(generation))
        {
            return;
        }

        DisposePort(DetachActivePort());
    }

    private static SerialPort? DetachActivePort()
    {
        lock (PortStateSync)
        {
            SerialPort? port = activePort;
            activePort = null;
            activePortName = "";
            return port;
        }
    }

    private static void DisposePort(SerialPort? port)
    {
        if (port == null) return;
        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch
        {
            // Closing a stale USB serial handle can fail after unplug; discard it either way.
        }
        finally
        {
            port.Dispose();
        }
    }

    private static bool IsPortFailure(Exception ex)
    {
        return ex is IOException ||
               ex is UnauthorizedAccessException ||
               ex is InvalidOperationException ||
               ex is TimeoutException;
    }

    private static bool ShouldReportSerialLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string trimmed = line.Trim();
        if (trimmed.StartsWith("{\"ok\":", StringComparison.Ordinal) ||
            trimmed.StartsWith("{\"cmd\":", StringComparison.Ordinal))
        {
            return true;
        }

        string lower = trimmed.ToLowerInvariant();
        return lower.Contains("guru meditation", StringComparison.Ordinal) ||
               lower.Contains("panic", StringComparison.Ordinal) ||
               lower.Contains("watchdog", StringComparison.Ordinal) ||
               lower.Contains("brownout", StringComparison.Ordinal) ||
               lower.Contains("abort()", StringComparison.Ordinal) ||
               lower.Contains("backtrace:", StringComparison.Ordinal) ||
               lower.Contains("assert failed", StringComparison.Ordinal) ||
               lower.Contains("stack overflow", StringComparison.Ordinal) ||
               lower.Contains("esp_err", StringComparison.Ordinal) ||
               lower.StartsWith("e (", StringComparison.Ordinal) ||
               lower.StartsWith("rst:", StringComparison.Ordinal);
    }

    private static bool IsGenerationCurrent(long generation)
    {
        return Interlocked.Read(ref activeGeneration) == generation;
    }

    private static void ThrowIfGenerationStopped(
        long generation,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref shuttingDown) != 0 || !IsGenerationCurrent(generation))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void InvalidateGeneration(long generation)
    {
        Interlocked.CompareExchange(ref activeGeneration, generation + 1, generation);
    }

    private static void CancelActiveOperations()
    {
        Interlocked.Increment(ref activeGeneration);
    }
}
