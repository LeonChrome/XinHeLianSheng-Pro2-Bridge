using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed record SteamControllerCacheAnalysis(
    bool SteamRunning,
    bool PotentialStaleIfHid,
    string Detail,
    string? ControllerLogPath);

public sealed record SteamControllerCacheRefreshResult(
    bool Success,
    bool SteamWasRunning,
    string Detail,
    string? SteamExePath);

public sealed record SteamIfHidObservation(string Vid, string Pid, DateTime ObservedAt);

public static class SteamControllerCacheService
{
    private static readonly Regex IfHidBlockRegex = new(
        @"\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})[^\]]*\]\s+Local Device Found\s*\r?\n" +
        @"\s*type:\s*(?<vid>[0-9a-fA-F]{4})\s+(?<pid>[0-9a-fA-F]{4})" +
        @"(?:(?:\r?\n)[^\r\n]*){0,7}?Product:\s*If_Hid\s*(?:\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SteamControllerCacheAnalysis AnalyzeRecentIfHid(
        string currentVid,
        string currentPid,
        TimeSpan recentWindow)
    {
        Process[] steamProcesses = Process.GetProcessesByName("steam");
        try
        {
            if (steamProcesses.Length == 0)
            {
                return new SteamControllerCacheAnalysis(false, false, "steam_not_running", null);
            }

            string? steamExe = TryGetSteamExe(steamProcesses);
            string? controllerLog = FindControllerLog(steamExe);
            if (controllerLog == null)
            {
                return new SteamControllerCacheAnalysis(true, false, "controller_log_not_found", null);
            }

            string tail = ReadFileTail(controllerLog, 2 * 1024 * 1024);
            SteamIfHidObservation? latest = FindLatestIfHidObservation(tail);
            if (latest == null)
            {
                return new SteamControllerCacheAnalysis(true, false, "recent_if_hid_not_found", controllerLog);
            }

            string staleVid = latest.Vid;
            string stalePid = latest.Pid;
            string expectedVid = NormalizeHexId(currentVid);
            string expectedPid = NormalizeHexId(currentPid);
            DateTime? steamStartedAt = TryGetEarliestStartTime(steamProcesses);
            bool identityMismatch = !string.Equals(staleVid, expectedVid, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(stalePid, expectedPid, StringComparison.OrdinalIgnoreCase);
            bool recent = DateTime.Now - latest.ObservedAt <= recentWindow &&
                          latest.ObservedAt <= DateTime.Now.AddMinutes(1) &&
                          (!steamStartedAt.HasValue ||
                           latest.ObservedAt >= steamStartedAt.Value.AddSeconds(-2));
            bool stale = identityMismatch && recent;
            string detail = "if_hid=" + staleVid + ":" + stalePid +
                            " current=" + expectedVid + ":" + expectedPid +
                            " observed=" + latest.ObservedAt.ToString("yyyy-MM-dd HH:mm:ss") +
                            " steam_started=" + (steamStartedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown") +
                            " recent=" + recent.ToString().ToLowerInvariant() +
                            " identity_mismatch=" + identityMismatch.ToString().ToLowerInvariant();
            return new SteamControllerCacheAnalysis(true, stale, detail, controllerLog);
        }
        finally
        {
            foreach (Process process in steamProcesses)
            {
                process.Dispose();
            }
        }
    }

    public static SteamIfHidObservation? FindLatestIfHidObservation(string controllerLogText)
    {
        Match? latest = IfHidBlockRegex.Matches(controllerLogText ?? "").Cast<Match>().LastOrDefault();
        if (latest == null ||
            !DateTime.TryParseExact(
                latest.Groups["time"].Value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime observedAt))
        {
            return null;
        }

        return new SteamIfHidObservation(
            latest.Groups["vid"].Value.ToUpperInvariant(),
            latest.Groups["pid"].Value.ToUpperInvariant(),
            observedAt);
    }

    public static async Task<SteamControllerCacheRefreshResult> RestartSteamAsync(
        CancellationToken cancellationToken)
    {
        Process[] steamProcesses = Process.GetProcessesByName("steam");
        try
        {
            if (steamProcesses.Length == 0)
            {
                return new SteamControllerCacheRefreshResult(
                    true,
                    false,
                    "steam_not_running_no_cache_to_refresh",
                    FindSteamExeFallback());
            }

            string? steamExe = TryGetSteamExe(steamProcesses) ?? FindSteamExeFallback();
            if (string.IsNullOrWhiteSpace(steamExe) || !File.Exists(steamExe))
            {
                return new SteamControllerCacheRefreshResult(
                    false,
                    true,
                    "steam_exe_not_found",
                    steamExe);
            }

            int[] originalPids = steamProcesses.Select(process => process.Id).ToArray();
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                Arguments = "-shutdown",
                UseShellExecute = true
            })?.Dispose();

            bool exited = await WaitForProcessesExitAsync(originalPids, TimeSpan.FromSeconds(20), cancellationToken);
            if (!exited)
            {
                return new SteamControllerCacheRefreshResult(
                    false,
                    true,
                    "steam_shutdown_timeout_no_force_kill",
                    steamExe);
            }

            await Task.Delay(500, cancellationToken);
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                UseShellExecute = true
            })?.Dispose();
            return new SteamControllerCacheRefreshResult(
                true,
                true,
                "steam_restarted_clean_controller_process_cache",
                steamExe);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SteamControllerCacheRefreshResult(
                false,
                steamProcesses.Length > 0,
                ex.GetType().Name + ": " + OneLine(ex.Message),
                TryGetSteamExe(steamProcesses));
        }
        finally
        {
            foreach (Process process in steamProcesses)
            {
                process.Dispose();
            }
        }
    }

    private static async Task<bool> WaitForProcessesExitAsync(
        IReadOnlyCollection<int> processIds,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool anyAlive = false;
            foreach (int processId in processIds)
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        anyAlive = true;
                        break;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            if (!anyAlive)
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static string? TryGetSteamExe(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
        {
            try
            {
                string? path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static DateTime? TryGetEarliestStartTime(IEnumerable<Process> processes)
    {
        DateTime? earliest = null;
        foreach (Process process in processes)
        {
            try
            {
                DateTime startedAt = process.StartTime;
                if (!earliest.HasValue || startedAt < earliest.Value)
                {
                    earliest = startedAt;
                }
            }
            catch
            {
            }
        }
        return earliest;
    }

    private static string? FindSteamExeFallback()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindControllerLog(string? steamExe)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(steamExe))
        {
            string? root = Path.GetDirectoryName(steamExe);
            if (!string.IsNullOrWhiteSpace(root))
            {
                candidates.Add(Path.Combine(root, "logs", "controller.txt"));
            }
        }

        string? fallback = FindSteamExeFallback();
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            candidates.Add(Path.Combine(Path.GetDirectoryName(fallback)!, "logs", "controller.txt"));
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    }

    private static string ReadFileTail(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    private static string NormalizeHexId(string value)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }
        return normalized.PadLeft(4, '0').ToUpperInvariant();
    }

    private static string OneLine(string value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ");
}
