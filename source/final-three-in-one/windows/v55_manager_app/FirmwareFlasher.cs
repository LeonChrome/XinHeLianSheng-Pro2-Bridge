using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V55Manager;

public enum FlashMode
{
    Upgrade,
    Repair,
    EraseAndFlash
}

public sealed class DriverCompatibilityException : InvalidOperationException
{
    public DriverCompatibilityException(string message) : base(message)
    {
    }
}

public sealed class DownloadModeException : InvalidOperationException
{
    public DownloadModeException(string message) : base(message)
    {
    }
}

public sealed class FirmwareFlasher
{
    private static readonly TimeSpan PortDriverSettleDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ChipProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EraseTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    public async Task FlashAsync(
        string port,
        string profileId,
        FlashMode mode,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            throw new InvalidOperationException("请先选择 CH343P 对应的 COM 串口。");
        }

        FirmwarePackage package = EmbeddedAssets.EnsurePackage();
        FirmwareProfile profile = package.GetProfile(profileId);
        bool useConservativeEsptool =
            await PreparePortAsync(package.EsptoolPath, port, progress, cancellationToken);
        int firstBaud = mode == FlashMode.Repair || useConservativeEsptool ? 115200 : 460800;
        bool firstNoStub = mode == FlashMode.Repair || useConservativeEsptool;

        progress.Report("内置固件: " + package.Manifest.FirmwareVersion + " / " + profile.Label);
        progress.Report("工具: " + package.EsptoolPath);
        progress.Report("目标: " + port + ", baud " + firstBaud);

        string chipOutput = await ProbeChipAsync(
            package.EsptoolPath, port, progress, cancellationToken);

        if (!chipOutput.Contains("ESP32-S3", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("所选串口没有识别为 ESP32-S3，已拒绝刷入。");
        }

        if (mode == FlashMode.EraseAndFlash)
        {
            progress.Report("正在整片擦除。");
            await RunEsptoolAsync(package.EsptoolPath,
                CommonArgs(port, 115200, true, "erase_flash"), progress, cancellationToken);
        }

        try
        {
            await WriteFlashAsync(package, profile, port, firstBaud, firstNoStub, progress, cancellationToken);
        }
        catch (Exception ex) when (mode != FlashMode.Repair && ex is not PortBusyException)
        {
            progress.Report("高速刷入失败，使用 115200 + --no-stub 修复路径重试。");
            await WriteFlashAsync(package, profile, port, 115200, true, progress, cancellationToken);
        }

        progress.Report("刷入完成。请重新插拔原生 USB / OTG，然后点击“USB 检查”。");
    }

    public async Task EraseFlashAsync(
        string port,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            throw new InvalidOperationException("请先选择 CH343P 对应的 COM 串口。");
        }

        FirmwarePackage package = EmbeddedAssets.EnsurePackage();
        bool useConservativeEsptool =
            await PreparePortAsync(package.EsptoolPath, port, progress, cancellationToken);

        progress.Report("清理目标: " + port);
        progress.Report("正在确认 ESP32-S3 芯片。");
        string chipOutput = await ProbeChipAsync(
            package.EsptoolPath, port, progress, cancellationToken);
        if (!chipOutput.Contains("ESP32-S3", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("所选串口没有识别为 ESP32-S3，已拒绝擦除。");
        }

        progress.Report("正在整片擦除 Flash。固件、NVS、BLE 配对和模式设置都会被清空。");
        await RunEsptoolAsync(
            package.EsptoolPath,
            CommonArgs(port, 115200, useConservativeEsptool, "erase_flash"),
            progress,
            cancellationToken);
        progress.Report("Flash 已完整清理。控制板现在没有应用固件，可作为全新 ESP32-S3 演示。");
    }

    private static async Task<bool> PreparePortAsync(
        string esptoolPath,
        string port,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (!await SerialCommandClient.CloseAsync(5000))
        {
            throw new InvalidOperationException(
                "刷写前无法释放 " + port +
                "。请等待当前串口操作结束，或关闭串口监视器后重试。");
        }

        bool useConservativeEsptool =
            await CheckPortDriverAsync(port, progress, cancellationToken);
        await CleanupKnownSerialConsumersAsync(port, progress);
        await CleanupStaleEsptoolProcessesAsync(esptoolPath, port, progress);
        progress.Report(
            "[FLASH_PORT] 已完成 " + port +
            " 的项目进程清理，等待 CH343 驱动稳定后交给 esptool 探测。");
        await Task.Delay(PortDriverSettleDelay, cancellationToken);
        return useConservativeEsptool;
    }

    private static async Task<bool> CheckPortDriverAsync(
        string port,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        Task<PortDriverInfo?> query =
            Task.Run(() => DeviceInspector.QueryPortDriver(port), cancellationToken);
        Task completed = await Task.WhenAny(
            query,
            Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
        if (completed != query)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(
                "[FLASH_DRIVER] " + port +
                " driver query timed out; continuing with process safeguards.");
            return false;
        }

        PortDriverInfo? driver = await query;
        if (driver == null)
        {
            progress.Report(
                "[FLASH_DRIVER] " + port +
                " driver metadata unavailable; continuing with process safeguards.");
            return false;
        }

        progress.Report("[FLASH_DRIVER] " + driver.Summary);
        if (driver.IsKnownKernelHangRisk)
        {
            progress.Report(
                "[FLASH_DRIVER_WARN] 检测到 Windows build " + driver.WindowsBuild +
                " 与 WCH CH343 驱动 " + driver.Version +
                " 的高风险组合。本次不再硬拦截刷写，但会自动切换到 115200 + --no-stub 保守路径。" +
                "若仍出现串口占用或 esptool 无法结束，请点击“修复 CH343 驱动”切换到 Microsoft usbser 后重新插拔控制口。");
            return true;
        }

        return false;
    }

    private static async Task<string> ProbeChipAsync(
        string esptoolPath,
        string port,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunEsptoolAsync(
                esptoolPath,
                CommonArgs(port, 115200, true, "chip_id"),
                progress,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not PortBusyException &&
                                   ex is not DownloadModeException &&
                                   ex is not EsptoolTimeoutException &&
                                   ex is not OperationCanceledException)
        {
            progress.Report(
                "chip_id 首次探测失败，等待控制板复位后使用稳定参数重试一次。");
            await Task.Delay(700, cancellationToken);
            return await RunEsptoolAsync(
                esptoolPath,
                CommonArgs(port, 115200, true, "chip_id"),
                progress,
                cancellationToken);
        }
    }

    private static async Task WriteFlashAsync(
        FirmwarePackage package,
        FirmwareProfile profile,
        string port,
        int baud,
        bool noStub,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var args = CommonArgs(port, baud, noStub, "write_flash");
        args.Add("--flash_mode");
        args.Add(package.Manifest.FlashMode);
        args.Add("--flash_freq");
        args.Add(package.Manifest.FlashFreq);
        args.Add("--flash_size");
        args.Add(package.Manifest.FlashSize);
        foreach (FirmwareAsset asset in profile.Assets)
        {
            args.Add(asset.Offset);
            args.Add(Path.Combine(package.FirmwareRoot, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
        }
        await RunEsptoolAsync(package.EsptoolPath, args, progress, cancellationToken);
    }

    internal static List<string> CommonArgs(string port, int baud, bool noStub, string command)
    {
        var args = new List<string> { "--chip", "esp32s3" };
        if (noStub)
        {
            args.Add("--no-stub");
        }

        args.AddRange(new[]
        {
            "-p", port,
            "-b", baud.ToString(),
            "--before", "default_reset",
            "--after", "hard_reset",
            "--connect-attempts", "5",
            command
        });
        return args;
    }

    internal static async Task<string> RunEsptoolAsync(
        string esptoolPath,
        IReadOnlyList<string> args,
        IProgress<string> progress,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null)
    {
        string targetPort = FindPort(args);
        if (!string.IsNullOrWhiteSpace(targetPort))
        {
            await CleanupStaleEsptoolProcessesAsync(esptoolPath, targetPort, progress);
        }

        string command = FindCommand(args);
        TimeSpan commandTimeout = timeoutOverride ?? GetCommandTimeout(command);
        var lines = new List<string>();
        var psi = new ProcessStartInfo(esptoolPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        progress.Report("esptool " + string.Join(" ", args));
        progress.Report(
            "[ESPTOOL_WATCHDOG] command=" + command +
            " timeout_seconds=" + commandTimeout.TotalSeconds.ToString("F1"));
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(e.Data, lines, progress);
        process.ErrorDataReceived += (_, e) => Capture(e.Data, lines, progress);

        int processId = 0;
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 esptool。");
        }
        processId = process.Id;

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Task exitTask = process.WaitForExitAsync(CancellationToken.None);
        Task timeoutTask = Task.Delay(commandTimeout, cancellationToken);
        Task completed;
        try
        {
            completed = await Task.WhenAny(exitTask, timeoutTask);
        }
        catch (OperationCanceledException)
        {
            TerminateEsptoolTree(process, processId, progress);
            throw;
        }

        if (completed != exitTask)
        {
            bool callerCancelled = cancellationToken.IsCancellationRequested;
            progress.Report(
                "[ESPTOOL_WATCHDOG] " + command +
                (callerCancelled ? " cancelled" : " timed out") +
                "; terminating process tree pid=" + processId);
            TerminateEsptoolTree(process, processId, progress);
            await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2)));
            await EnsureNoMatchingEsptoolProcessesAsync(
                esptoolPath, targetPort, progress);
            if (callerCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new EsptoolTimeoutException(
                "esptool " + command + " 在 " +
                commandTimeout.TotalSeconds.ToString("F1") +
                " 秒内没有结束。已停止刷写，避免留下更多占用进程。");
        }

        await exitTask;
        CleanupEsptoolChildren(processId, progress);
        string combined = string.Join(Environment.NewLine, lines);
        if (process.ExitCode != 0)
        {
            if (IsPortBusyFailure(combined))
            {
                string port = FindPort(args);
                await CleanupStaleEsptoolProcessesAsync(esptoolPath, port, progress);
                throw new PortBusyException(BuildPortBusyMessage(port, combined));
            }
            if (IsDownloadModeFailure(combined))
            {
                throw new DownloadModeException(
                    BuildDownloadModeMessage(FindPort(args), combined));
            }
            throw new InvalidOperationException("esptool 失败，exit=" + process.ExitCode + Environment.NewLine + combined);
        }

        return combined;
    }

    private static bool IsPortBusyFailure(string output)
    {
        return output.Contains("PermissionError", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("port is busy", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("could not open port", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDownloadModeFailure(string output)
    {
        return output.Contains("Wrong boot mode detected", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("needs to be in download mode", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetCommandTimeout(string command)
    {
        return command switch
        {
            "chip_id" => ChipProbeTimeout,
            "erase_flash" => EraseTimeout,
            "write_flash" => WriteTimeout,
            _ => DefaultCommandTimeout
        };
    }

    private static string BuildPortBusyMessage(string port, string detail)
    {
        return "esptool 无法打开 " + port + "：Manager 已关闭自身串口，并清理使用该端口的 DualSenseHostTrace、项目 monitor/send_command 和残留 esptool。若旧进程已进入 CH343 内核终止挂起，Windows 仍会显示进程但无法结束，此时需要拔插 CH343P 控制口后再试。"
            + Environment.NewLine + detail;
    }

    private static string BuildDownloadModeMessage(string port, string detail)
    {
        string target = string.IsNullOrWhiteSpace(port) ? "所选串口" : port;
        return target +
            " 已能打开，但 ESP32-S3 没有进入 ROM 下载模式。通常是开发板自动下载电路、BOOT/EN 按键时序或 CH343 驱动 DTR/RTS 控制不兼容，不是固件包损坏。请按住 BOOT，再点击刷机；日志出现 Connecting... 时点按 EN/RST，看到 Chip is ESP32-S3 后松开 BOOT。若仍失败，请把 CH343 驱动切换为 Microsoft “USB 串行设备”，或更换支持自动下载的 ESP32-S3 开发板。"
            + Environment.NewLine + detail;
    }

    private static void CleanupStaleEsptoolProcesses(
        string esptoolPath,
        string port,
        IProgress<string> progress)
    {
        int[] matching = FindMatchingEsptoolProcesses(esptoolPath, port).ToArray();
        foreach (int pid in matching)
        {
            KillProcess(pid, "[FLASH_CLEANUP] stale esptool", progress);
        }
    }

    private static async Task CleanupStaleEsptoolProcessesAsync(
        string esptoolPath,
        string port,
        IProgress<string> progress)
    {
        Task cleanupTask = Task.Run(() => CleanupStaleEsptoolProcesses(esptoolPath, port, progress));
        Task completed = await Task.WhenAny(cleanupTask, Task.Delay(TimeSpan.FromMilliseconds(1200)));
        if (completed != cleanupTask)
        {
            throw new PortBusyException(
                "检查残留 esptool 进程超时。为避免叠加占用，已取消本次刷写。");
        }
        await cleanupTask;
        await EnsureNoMatchingEsptoolProcessesAsync(esptoolPath, port, progress);
    }

    private static async Task EnsureNoMatchingEsptoolProcessesAsync(
        string esptoolPath,
        string port,
        IProgress<string> progress)
    {
        await Task.Delay(250);
        int[] remaining = FindMatchingEsptoolProcesses(esptoolPath, port).ToArray();
        if (remaining.Length == 0)
        {
            return;
        }

        string pids = string.Join(",", remaining);
        progress.Report(
            "[FLASH_CLEANUP] esptool processes still present: " + pids);
        throw new PortBusyException(
            "检测到无法结束的 esptool 进程 pid=" + pids +
            "。它可能卡在 CH343 驱动内核调用中。请拔插一次 CH343P 控制口，再重新刷写；Manager 不会继续启动新的 esptool。");
    }

    private static async Task CleanupKnownSerialConsumersAsync(
        string port,
        IProgress<string> progress)
    {
        Task cleanupTask = Task.Run(() =>
        {
            foreach ((int pid, string name, string commandLine) in
                     FindKnownSerialConsumerProcesses(port))
            {
                progress.Report("[FLASH_PORT_OWNER] " + port +
                                " held by " + name +
                                " pid=" + pid +
                                " command=" + CompactCommandLine(commandLine));
                KillProcess(
                    pid,
                    "[FLASH_CLEANUP] project serial consumer",
                    progress);
            }
        });
        Task completed = await Task.WhenAny(
            cleanupTask,
            Task.Delay(TimeSpan.FromMilliseconds(1800)));
        if (completed != cleanupTask)
        {
            throw new PortBusyException(
                "检查项目串口占用进程超时。为避免叠加占用，已取消本次刷写。");
        }
        await cleanupTask;

        string[] remaining = FindKnownSerialConsumerProcesses(port)
            .Select(item => item.Name + " pid=" + item.Pid)
            .ToArray();
        if (remaining.Length > 0)
        {
            throw new PortBusyException(
                "以下项目进程仍未结束：" + string.Join(", ", remaining) +
                "。请拔插一次 CH343P 控制口后重试。");
        }
    }

    private static IEnumerable<(int Pid, string Name, string CommandLine)>
        FindKnownSerialConsumerProcesses(string port)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId,Name,CommandLine FROM Win32_Process");
        foreach (ManagementObject item in searcher.Get())
        {
            int pid;
            try
            {
                pid = Convert.ToInt32(item["ProcessId"]);
            }
            catch
            {
                continue;
            }

            if (pid == Environment.ProcessId)
            {
                continue;
            }

            string name = Convert.ToString(item["Name"]) ?? "";
            string commandLine = Convert.ToString(item["CommandLine"]) ?? "";
            if (!CommandTargetsPort(commandLine, port) ||
                !IsKnownProjectSerialConsumer(name, commandLine))
            {
                continue;
            }

            yield return (pid, name, commandLine);
        }
    }

    private static bool CommandTargetsPort(string commandLine, string port)
    {
        if (string.IsNullOrWhiteSpace(commandLine) ||
            string.IsNullOrWhiteSpace(port))
        {
            return false;
        }

        return Regex.IsMatch(
            commandLine,
            @"(?<![A-Z0-9])" + Regex.Escape(port) + @"(?![A-Z0-9])",
            RegexOptions.IgnoreCase);
    }

    private static bool IsKnownProjectSerialConsumer(
        string processName,
        string commandLine)
    {
        if (processName.Equals(
                "DualSenseHostTrace.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool shellOrPython =
            processName.Equals(
                "powershell.exe",
                StringComparison.OrdinalIgnoreCase) ||
            processName.Equals(
                "pwsh.exe",
                StringComparison.OrdinalIgnoreCase) ||
            processName.Equals(
                "python.exe",
                StringComparison.OrdinalIgnoreCase) ||
            processName.Equals(
                "python3.exe",
                StringComparison.OrdinalIgnoreCase);
        if (!shellOrPython)
        {
            return false;
        }

        return commandLine.Contains(
                   "tools\\esp32s3\\send_command.ps1",
                   StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(
                   "tools\\esp32s3\\monitor.ps1",
                   StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(
                   "idf_monitor.py",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string CompactCommandLine(string commandLine)
    {
        string compact = Regex.Replace(commandLine, @"\s+", " ").Trim();
        return compact.Length <= 240 ? compact : compact[..240] + "...";
    }

    private static void CleanupEsptoolChildren(int parentPid, IProgress<string> progress)
    {
        if (parentPid <= 0) return;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId,Name FROM Win32_Process WHERE ParentProcessId=" + parentPid);
            foreach (ManagementObject item in searcher.Get())
            {
                string name = Convert.ToString(item["Name"]) ?? "";
                if (!string.Equals(name, "esptool.exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int pid = Convert.ToInt32(item["ProcessId"]);
                KillProcess(pid, "[FLASH_CLEANUP] child esptool", progress);
            }
        }
        catch
        {
        }
    }

    private static void TerminateEsptoolTree(
        Process process,
        int parentPid,
        IProgress<string> progress)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            progress.Report(
                "[FLASH_CLEANUP] esptool tree pid=" + parentPid +
                " kill request failed: " + ex.Message);
        }

        CleanupEsptoolChildren(parentPid, progress);
        KillProcess(parentPid, "[FLASH_CLEANUP] esptool parent", progress);
    }

    private static IEnumerable<int> FindMatchingEsptoolProcesses(string esptoolPath, string port)
    {
        string expectedPath = Path.GetFullPath(esptoolPath);
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='esptool.exe'");
        foreach (ManagementObject item in searcher.Get())
        {
            string executablePath = Convert.ToString(item["ExecutablePath"]) ?? "";
            string commandLine = Convert.ToString(item["CommandLine"]) ?? "";
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(executablePath);
            }
            catch
            {
                continue;
            }

            if (!string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!commandLine.Contains(port, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return Convert.ToInt32(item["ProcessId"]);
        }
    }

    private static void KillProcess(int pid, string label, IProgress<string> progress)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                progress.Report(label + " pid=" + pid + " is already exiting");
                return;
            }
            process.Kill(entireProcessTree: true);
            if (process.WaitForExit(1500))
            {
                progress.Report(label + " pid=" + pid + " killed");
            }
            else
            {
                progress.Report(label + " pid=" + pid + " did not exit; unplug/replug CH343P if COM remains busy");
            }
        }
        catch
        {
            if (ProcessStillListed(pid))
            {
                progress.Report(
                    label + " pid=" + pid +
                    " termination pending in CH343 driver; unplug/replug the control port if esptool still reports access denied");
            }
            else
            {
                progress.Report(label + " pid=" + pid + " already exited");
            }
        }
    }

    private static bool ProcessStillListed(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Process WHERE ProcessId=" + pid);
            return searcher.Get().Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string FindPort(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], "-p", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return "所选串口";
    }

    private static string FindCommand(IReadOnlyList<string> args)
    {
        string[] commands =
        {
            "chip_id",
            "erase_flash",
            "write_flash",
            "flash_id",
            "read_mac"
        };
        foreach (string arg in args)
        {
            foreach (string command in commands)
            {
                if (string.Equals(arg, command, StringComparison.OrdinalIgnoreCase))
                {
                    return command;
                }
            }
        }
        return "unknown";
    }

    private static void Capture(string? line, List<string> lines, IProgress<string> progress)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (lines)
        {
            lines.Add(line);
        }
        progress.Report(line);
    }

    private sealed class PortBusyException : InvalidOperationException
    {
        public PortBusyException(string message) : base(message)
        {
        }
    }

    private sealed class EsptoolTimeoutException : TimeoutException
    {
        public EsptoolTimeoutException(string message) : base(message)
        {
        }
    }
}
