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

public sealed record UsbipRuntime(string ExePath, string DirectoryPath);
public sealed record UsbipInstaller(string InstallerPath, string? LicensePath);
public sealed record UsbipProbeResult(bool Ready, string Detail);

public static class UsbipRuntimeLocator
{
    public const string BundledVersion = "v0.9.7.7";
    public const string InstallerFileName = "USBip-0.9.7.7-x64.exe";

    public static UsbipRuntime? Find()
    {
        foreach (string candidate in CandidatePaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return new UsbipRuntime(candidate, Path.GetDirectoryName(candidate) ?? "");
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

    private static IEnumerable<string> CandidatePaths()
    {
        foreach (string pathCandidate in PathEnvironmentCandidates())
        {
            yield return pathCandidate;
        }

        foreach (string registryCandidate in InstalledProductCandidates())
        {
            yield return registryCandidate;
        }

        foreach (string localRoot in LocalSearchRoots())
        {
            foreach (string found in TryFindUnder(localRoot))
            {
                yield return found;
            }
        }

        foreach (string programRoot in ProgramFilesRoots())
        {
            yield return Path.Combine(programRoot, "USBip", "usbip.exe");
            yield return Path.Combine(programRoot, "USBIP", "usbip.exe");
            yield return Path.Combine(programRoot, "usbip-win2", "usbip.exe");
            yield return Path.Combine(programRoot, "USBip-win2", "usbip.exe");
            yield return Path.Combine(programRoot, "USBip", "bin", "usbip.exe");
            yield return Path.Combine(programRoot, "usbip-win2", "bin", "usbip.exe");
        }
    }

    private static IReadOnlyList<string> InstalledProductCandidates()
    {
        var candidates = new List<string>();
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null)
                {
                    continue;
                }

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? product = uninstall.OpenSubKey(subKeyName);
                    string displayName = product?.GetValue("DisplayName") as string ?? "";
                    if (!displayName.Contains("USBip", StringComparison.OrdinalIgnoreCase) &&
                        !displayName.Contains("USB-IP", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string installLocation = product?.GetValue("InstallLocation") as string ?? "";
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        candidates.Add(Path.Combine(installLocation.Trim().Trim('"'), "usbip.exe"));
                        candidates.Add(Path.Combine(installLocation.Trim().Trim('"'), "bin", "usbip.exe"));
                    }

                    string displayIcon = product?.GetValue("DisplayIcon") as string ?? "";
                    if (!string.IsNullOrWhiteSpace(displayIcon))
                    {
                        string iconPath = displayIcon.Trim().Trim('"');
                        int argumentSeparator = iconPath.IndexOf(',');
                        if (argumentSeparator > 0)
                        {
                            iconPath = iconPath[..argumentSeparator];
                        }
                        if (iconPath.EndsWith("usbip.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(iconPath);
                        }
                    }
                }
            }
            catch
            {
                // Registry discovery is supplemental; PATH and fixed locations remain available.
            }
        }
        return candidates;
    }

    private static IEnumerable<string> PathEnvironmentCandidates()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir, "usbip.exe");
            }
            catch
            {
                continue;
            }

            yield return candidate;
        }
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
            "v6.2.32-test-r2",
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


