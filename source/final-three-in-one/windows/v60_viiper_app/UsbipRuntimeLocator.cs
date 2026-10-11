using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

public sealed record UsbipRuntime(string ExePath, string DirectoryPath, string Source = "unknown");
public sealed record UsbipInstaller(string InstallerPath, string? LicensePath);
public sealed record UsbipProbeResult(bool Ready, string Detail);

public static class UsbipRuntimeLocator
{
    public const string BundledVersion = "v0.9.7.7";
    public const string InstallerFileName = "USBip-0.9.7.7-x64.exe";

    public static UsbipRuntime? Find(string? configuredExePath = null)
    {
        string configured = configuredExePath ?? V60UserSettings.Load().UsbipExecutablePath;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in CandidatePaths(configured))
        {
            string path = NormalizeExecutablePath(candidate.Path);
            if (path.Length > 0 && seen.Add(path) && File.Exists(path))
            {
                return new UsbipRuntime(path, Path.GetDirectoryName(path) ?? "", candidate.Source);
            }
        }

        return null;
    }

    public static UsbipInstaller? FindBundledInstaller()
    {
        foreach (string candidate in CandidateInstallerPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                string? dir = Path.GetDirectoryName(candidate);
                string? license = dir == null ? null : Path.Combine(dir, "LICENSE.txt");
                return new UsbipInstaller(candidate, File.Exists(license) ? license : null);
            }
        }

        return ExtractEmbeddedInstaller();
    }

    public static string BuildPathWithUsbipDirectory(string currentPath, UsbipRuntime runtime)
    {
        string safeCurrentPath = currentPath ?? "";
        if (string.IsNullOrWhiteSpace(runtime.DirectoryPath))
        {
            return safeCurrentPath;
        }

        string[] parts = safeCurrentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ContainsDirectory(parts, runtime.DirectoryPath))
        {
            return safeCurrentPath;
        }

        return runtime.DirectoryPath + Path.PathSeparator + safeCurrentPath;
    }

    public static async Task<UsbipProbeResult> ProbeAsync(
        UsbipRuntime runtime,
        CancellationToken cancellationToken)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = runtime.ExePath,
                Arguments = "port",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = runtime.DirectoryPath
            });
            if (process == null)
            {
                return new UsbipProbeResult(false, "usbip.exe 无法启动");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                return new UsbipProbeResult(false, "usbip port 检测超过 5 秒");
            }

            string output = ((await process.StandardOutput.ReadToEndAsync()) + " " +
                             (await process.StandardError.ReadToEndAsync())).Trim();
            return process.ExitCode == 0
                ? new UsbipProbeResult(true, string.IsNullOrWhiteSpace(output) ? "driver command ready" : output)
                : new UsbipProbeResult(
                    false,
                    "usbip port exit=" + process.ExitCode +
                    (string.IsNullOrWhiteSpace(output) ? "" : " / " + output));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UsbipProbeResult(false, ex.Message);
        }
    }

    private static bool ContainsDirectory(IEnumerable<string> parts, string target)
    {
        string fullTarget;
        try
        {
            fullTarget = Path.GetFullPath(target);
        }
        catch
        {
            return false;
        }

        foreach (string part in parts)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(part), fullTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    public static string NormalizeExecutablePath(string? value)
    {
        string path = NormalizeAbsolutePath(value);
        return path.Length > 0 && string.Equals(Path.GetFileName(path), "usbip.exe", StringComparison.OrdinalIgnoreCase)
            ? path : "";
    }

    private static string NormalizeAbsolutePath(string? value)
    {
        try
        {
            string text = Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"')).Trim();
            return Path.IsPathFullyQualified(text) ? Path.GetFullPath(text) : "";
        }
        catch { return ""; }
    }

    // DisplayIcon may be "D:\\Custom Folder\\usbip.exe",0. Strip the icon
    // index BEFORE parsing quotes, rather than leaving a trailing quote behind.
    // UninstallString is used only to infer a sibling directory, never executed.
    public static string ParseRegisteredExecutablePath(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith('"'))
        {
            int closing = text.IndexOf('"', 1);
            if (closing < 0) return "";
            text = text.Substring(1, closing - 1);
        }
        else
        {
            int ending = text.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (ending < 0) return "";
            text = text[..(ending + 4)];
        }
        string path = NormalizeAbsolutePath(text);
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : "";
    }

    public static IReadOnlyList<string> GetInstallRecordCandidatePaths(
        string? installLocation, string? displayIcon, string? uninstallString)
    {
        var paths = new List<string>();
        AddDirectory(installLocation);
        foreach (string? registered in new[] { displayIcon, uninstallString })
        {
            string executable = ParseRegisteredExecutablePath(registered);
            if (executable.Length == 0) continue;
            if (NormalizeExecutablePath(executable).Length > 0) paths.Add(executable);
            AddDirectory(Path.GetDirectoryName(executable));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        void AddDirectory(string? value)
        {
            string directory = NormalizeAbsolutePath(value);
            if (directory.Length == 0) return;
            paths.Add(Path.Combine(directory, "usbip.exe"));
            paths.Add(Path.Combine(directory, "bin", "usbip.exe"));
        }
    }

    public static IEnumerable<string> GetPathCandidates(string? pathValue)
    {
        foreach (string entry in (pathValue ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = NormalizeAbsolutePath(entry);
            if (directory.Length > 0) yield return Path.Combine(directory, "usbip.exe");
        }
    }

    private static IEnumerable<(string Path, string Source)> CandidatePaths(string configuredExePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredExePath)) yield return (configuredExePath, "saved_manual_path");
        foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process,
            EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? path = null;
            try { path = Environment.GetEnvironmentVariable("PATH", target); } catch { }
            foreach (string candidate in GetPathCandidates(path)) yield return (candidate, "path_" + target);
        }
        foreach (var candidate in InstalledProductCandidates()) yield return candidate;
        foreach (string localRoot in LocalSearchRoots())
        {
            foreach (string found in TryFindUnder(localRoot)) yield return (found, "near_application");
        }
        foreach (string programRoot in ProgramFilesRoots())
        {
            foreach (string relative in new[] { "USBip", "usbip-win2" })
            {
                yield return (Path.Combine(programRoot, relative, "usbip.exe"), "common_directory");
                yield return (Path.Combine(programRoot, relative, "bin", "usbip.exe"), "common_directory");
            }
        }
    }

    private static IReadOnlyList<(string Path, string Source)> InstalledProductCandidates()
    {
        var candidates = new List<(string Path, string Source)>();
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using (RegistryKey? appPath = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\usbip.exe"))
                {
                    string path = NormalizeExecutablePath(appPath?.GetValue(null) as string);
                    if (path.Length > 0) candidates.Add((path, "app_paths_" + hive + "_" + view));
                }
                using RegistryKey? uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null)
                {
                    continue;
                }

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? product = uninstall.OpenSubKey(subKeyName);
                        string displayName = product?.GetValue("DisplayName") as string ?? "";
                        if (!displayName.Contains("USBip", StringComparison.OrdinalIgnoreCase) &&
                            !displayName.Contains("USB-IP", StringComparison.OrdinalIgnoreCase) &&
                            !displayName.Contains("USB/IP", StringComparison.OrdinalIgnoreCase))
                            continue;
                        foreach (string path in GetInstallRecordCandidatePaths(
                            product?.GetValue("InstallLocation") as string,
                            product?.GetValue("DisplayIcon") as string,
                            product?.GetValue("UninstallString") as string))
                            candidates.Add((path, "uninstall_" + hive + "_" + view));
                    }
                    catch { /* One inaccessible/bad product must not hide all others. */ }
                }
            }
            catch
            {
                // Registry discovery is supplemental; PATH and fixed locations remain available.
            }
        }
        return candidates;
    }

    private static IEnumerable<string> LocalSearchRoots()
    {
        string[] relativeRoots =
        [
            Path.Combine("tools", "usbip-win2"),
            Path.Combine("tools", "usbip"),
            "usbip-win2",
            "usbip"
        ];

        string? cursor = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(cursor); i++)
        {
            foreach (string relative in relativeRoots)
            {
                yield return Path.GetFullPath(Path.Combine(cursor, relative));
            }
            cursor = Directory.GetParent(cursor)?.FullName;
        }

        foreach (string relative in relativeRoots)
        {
            yield return Path.GetFullPath(relative);
        }
    }

    private static IEnumerable<string> TryFindUnder(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "usbip.exe", SearchOption.AllDirectories)
                .Take(8)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (string file in files)
        {
            yield return file;
        }
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return programFiles;
        }

        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return programFilesX86;
        }
    }

    private static IEnumerable<string> CandidateInstallerPaths()
    {
        string[] relativeInstallers =
        [
            Path.Combine("usbip-win2", BundledVersion, InstallerFileName),
            Path.Combine("tools", "usbip-win2", BundledVersion, InstallerFileName),
            Path.Combine(BundledVersion, InstallerFileName),
            InstallerFileName
        ];

        string? cursor = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(cursor); i++)
        {
            foreach (string relative in relativeInstallers)
            {
                yield return Path.GetFullPath(Path.Combine(cursor, relative));
            }
            cursor = Directory.GetParent(cursor)?.FullName;
        }

        foreach (string relative in relativeInstallers)
        {
            yield return Path.GetFullPath(relative);
        }
    }

    private static UsbipInstaller? ExtractEmbeddedInstaller()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "embedded",
            "pure-software-v1.0.0",
            "usbip-win2",
            BundledVersion);
        Directory.CreateDirectory(root);
        string installer = Path.Combine(root, InstallerFileName);
        string license = Path.Combine(root, "LICENSE.txt");

        if (!ExtractResourceIfAvailable(assembly, "Embedded.usbip.installer", installer))
        {
            return null;
        }

        bool hasLicense = ExtractResourceIfAvailable(
            assembly,
            "Embedded.usbip.license",
            license);
        return new UsbipInstaller(installer, hasLicense ? license : null);
    }

    private static bool ExtractResourceIfAvailable(
        Assembly assembly,
        string resourceName,
        string destination)
    {
        using Stream? source = assembly.GetManifestResourceStream(resourceName);
        if (source == null)
        {
            return false;
        }

        if (File.Exists(destination) && new FileInfo(destination).Length == source.Length)
        {
            return true;
        }

        string temp = destination + ".tmp";
        using (FileStream output = File.Create(temp))
        {
            source.CopyTo(output);
        }
        File.Move(temp, destination, overwrite: true);
        return true;
    }
}


