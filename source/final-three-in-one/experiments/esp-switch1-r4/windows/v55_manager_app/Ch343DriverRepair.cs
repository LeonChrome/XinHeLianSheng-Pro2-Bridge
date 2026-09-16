using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V55Manager;

public sealed record Ch343DriverRepairResult(
    bool Completed,
    int? ExitCode,
    string ScriptPath,
    string LogPath,
    string BackupDirectory,
    string Message);

public static class Ch343DriverRepair
{
    private const int RepairTimeoutSeconds = 120;

    public static async Task<Ch343DriverRepairResult> RunAsync(
        PortDriverInfo driver,
        CancellationToken cancellationToken = default)
    {
        if (driver == null)
        {
            throw new ArgumentNullException(nameof(driver));
        }

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard");
        string toolDirectory = Path.Combine(root, "tools");
        string logDirectory = Path.Combine(root, "logs");
        string backupDirectory = Path.Combine(root, "driver_backup", "ch343");
        Directory.CreateDirectory(toolDirectory);
        Directory.CreateDirectory(logDirectory);
        Directory.CreateDirectory(backupDirectory);

        string scriptPath = Path.Combine(toolDirectory, "switch_ch343_to_usbser.ps1");
        string logPath = Path.Combine(
            logDirectory,
            "ch343_driver_repair_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        File.WriteAllText(scriptPath, RepairScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string arguments =
            "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) +
            " -InstanceId " + Quote(driver.DeviceId) +
            " -BackupDirectory " + Quote(backupDirectory) +
            " -LogPath " + Quote(logPath);

        using Process process = StartElevatedPowerShell(arguments);
        Task waitTask = process.WaitForExitAsync(cancellationToken);
        Task completed = await Task.WhenAny(
            waitTask,
            Task.Delay(TimeSpan.FromSeconds(RepairTimeoutSeconds), cancellationToken));
        if (completed != waitTask)
        {
            return new Ch343DriverRepairResult(
                Completed: false,
                ExitCode: null,
                ScriptPath: scriptPath,
                LogPath: logPath,
                BackupDirectory: backupDirectory,
                Message:
                    "管理员驱动修复脚本仍在运行，界面已停止等待。请查看 UAC/PowerShell 窗口，完成后拔插 CH343P 控制口。日志：" +
                    logPath);
        }

        await waitTask;
        int exitCode = process.ExitCode;
        string tail = ReadTail(logPath, 2400);
        string message = exitCode == 0
            ? "CH343 驱动修复脚本已完成。备份目录：" + backupDirectory + "；日志：" + logPath
            : "CH343 驱动修复脚本失败，exit=" + exitCode + "。日志：" + logPath;
        if (!string.IsNullOrWhiteSpace(tail))
        {
            message += Environment.NewLine + tail;
        }

        return new Ch343DriverRepairResult(
            Completed: true,
            ExitCode: exitCode,
            ScriptPath: scriptPath,
            LogPath: logPath,
            BackupDirectory: backupDirectory,
            Message: message);
    }

    private static Process StartElevatedPowerShell(string arguments)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("无法启动管理员 PowerShell。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("用户取消了 UAC 管理员授权。", ex);
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string ReadTail(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length <= maxChars)
            {
                return text.Trim();
            }

            return text.Substring(text.Length - maxChars).Trim();
        }
        catch
        {
            return "";
        }
    }

    private const string RepairScript = """
param(
    [string]$InstanceId = "",
    [string]$BackupDirectory = "",
    [string]$LogPath = ""
)

$ErrorActionPreference = "Stop"
$transcriptStarted = $false

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

try {
    if (![string]::IsNullOrWhiteSpace($LogPath)) {
        $logDirectory = Split-Path -Parent $LogPath
        if (![string]::IsNullOrWhiteSpace($logDirectory)) {
            New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        }
        Start-Transcript -Path $LogPath -Force | Out-Null
        $transcriptStarted = $true
    }

    if (!(Test-IsAdministrator)) {
        throw "This script must run as administrator."
    }

    if ([string]::IsNullOrWhiteSpace($InstanceId)) {
        $matches = @(Get-CimInstance Win32_PnPSignedDriver |
            Where-Object { $_.DeviceID -like "USB\VID_1A86&PID_55D3*" })
        if ($matches.Count -ne 1) {
            throw "Expected exactly one CH343 VID_1A86&PID_55D3 device; found $($matches.Count)."
        }
        $InstanceId = $matches[0].DeviceID
    }

    if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
        $BackupDirectory = Join-Path $env:LOCALAPPDATA "PRO2WirelessReceiverControlBoard\driver_backup\ch343"
    }

    New-Item -ItemType Directory -Force -Path $BackupDirectory | Out-Null

    $driver = Get-CimInstance Win32_PnPSignedDriver |
        Where-Object { $_.DeviceID -eq $InstanceId } |
        Select-Object -First 1
    if (!$driver) {
        throw "No signed driver metadata was found for $InstanceId."
    }

    Write-Output "[CH343_DRIVER] instance=$InstanceId"
    Write-Output "[CH343_DRIVER] current_inf=$($driver.InfName)"
    Write-Output "[CH343_DRIVER] provider=$($driver.DriverProviderName)"
    Write-Output "[CH343_DRIVER] version=$($driver.DriverVersion)"

    if ($driver.InfName -eq "usbser.inf") {
        Write-Output "[CH343_DRIVER] result=already_usbser"
        return
    }

    if ($driver.InfName -notmatch "^oem\d+\.inf$") {
        throw "Current CH343 driver package is not a removable third-party OEM INF: $($driver.InfName)."
    }

    $publishedName = $driver.InfName
    & pnputil /export-driver $publishedName $BackupDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to back up $publishedName to $BackupDirectory."
    }
    Write-Output "[CH343_DRIVER] backup=$BackupDirectory"

    & pnputil /delete-driver $publishedName /uninstall /force
    $deleteExitCode = $LASTEXITCODE
    if ($deleteExitCode -ne 0 -and $deleteExitCode -ne 3010) {
        throw "Unable to uninstall $publishedName. pnputil exit=$deleteExitCode"
    }
    if ($deleteExitCode -eq 3010) {
        Write-Output "[CH343_DRIVER] replug_required=true"
        Write-Output "[CH343_DRIVER] result=pending_usbser"
        return
    }

    & pnputil /scan-devices
    if ($LASTEXITCODE -ne 0) {
        throw "Driver rescan failed."
    }

    Start-Sleep -Seconds 4
    $after = (& pnputil /enum-devices /instanceid $InstanceId /drivers | Out-String)
    Write-Output $after

    $activeDriver = Get-CimInstance Win32_PnPSignedDriver |
        Where-Object { $_.DeviceID -eq $InstanceId } |
        Select-Object -First 1
    if (!$activeDriver -or $activeDriver.InfName -ne "usbser.inf") {
        Write-Output "[CH343_DRIVER] result=replug_required"
        throw "CH343 did not bind to Microsoft's usbser driver yet. Unplug/replug the CH343P control port and check Device Manager."
    }

    Write-Output "[CH343_DRIVER] result=usbser"
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if ($transcriptStarted) {
        Stop-Transcript | Out-Null
    }
}
""";
}
