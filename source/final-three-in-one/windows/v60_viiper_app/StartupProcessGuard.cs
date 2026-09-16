using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Y700Switch2V60Viiper;

public static class StartupProcessGuard
{
    private static readonly string[] ManagerNamePrefixes =
    [
        "XinHeLianSheng-VIIPER-aio-v",
        "新和联胜VIIPER版本-aio-v"
    ];

    public static string LastSummary { get; private set; } = "";

    public static string CleanupConflictingProcesses()
    {
        int currentId = Environment.ProcessId;
        var stopped = new List<string>();
        var failed = new List<string>();

        foreach (Process process in Process.GetProcesses())
        {
            if (process.Id == currentId)
            {
                process.Dispose();
                continue;
            }

            string processName;
            try
            {
                processName = process.ProcessName;
            }
            catch
            {
                process.Dispose();
                continue;
            }

            string? path = SafeMainModulePath(process);
            if (!IsConflictingManager(processName) &&
                !IsManagedViiperServer(processName, path))
            {
                process.Dispose();
                continue;
            }

            try
            {
                int id = process.Id;
                process.Kill(entireProcessTree: true);
                bool exited = process.WaitForExit(2500);
                stopped.Add(
                    processName +
                    "#" + id +
                    (exited ? "" : "(kill_pending)") +
                    (string.IsNullOrWhiteSpace(path) ? "" : " " + path));
            }
            catch (Exception ex)
            {
                failed.Add(processName + "#" + process.Id + " " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        LastSummary = stopped.Count == 0 && failed.Count == 0
            ? "[STARTUP_GUARD] no stale Manager/VIIPER process found."
            : "[STARTUP_GUARD] stopped=" + string.Join(" | ", stopped) +
              (failed.Count == 0 ? "" : " failed=" + string.Join(" | ", failed));
        WriteStartupLog(LastSummary);
        return LastSummary;
    }

    private static bool IsConflictingManager(string processName)
    {
        if (processName.Equals("Y700Switch2V60Viiper", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ManagerNamePrefixes.Any(prefix =>
            processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsManagedViiperServer(string processName, string? path)
    {
        if (!processName.Equals("viiper-haptic", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return path.Contains(
                   @"PRO2WirelessReceiverControlBoard",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Contains(
                   @"tools\viiper",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStartupLog(string summary)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PRO2WirelessReceiverControlBoard",
                "v6_logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                "startup_guard_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            File.WriteAllText(path, summary + Environment.NewLine);
        }
        catch
        {
        }
    }
}
