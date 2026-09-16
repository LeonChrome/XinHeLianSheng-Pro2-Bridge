using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Y700Switch2V55Manager;

public static class EmbeddedAssets
{
    public const string BundledPackageVersion = "v5.9.19-aio";
    public const string BundledFirmwareVersion = "5.9.19-manager";
    private static readonly object ExtractLock = new();
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string RootDirectory
    {
        get
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("PRO2_BRIDGE_DATA_ROOT");
            string dataRoot = string.IsNullOrWhiteSpace(overrideRoot)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.GetFullPath(overrideRoot);
            return Path.Combine(dataRoot,
                "PRO2WirelessReceiverControlBoard", "embedded", BundledPackageVersion);
        }
    }

    public static FirmwarePackage EnsurePackage()
    {
        string firmwareRoot = Path.Combine(RootDirectory, "firmware", "v5.9");
        string toolsRoot = Path.Combine(RootDirectory, "tools");
        ExtractPrefix("embedded/firmware/v5.9/", firmwareRoot);
        ExtractPrefix("embedded/tools/", toolsRoot);

        string manifestPath = Path.Combine(firmwareRoot, "firmware_manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Bundled V5.9 firmware manifest was not extracted.", manifestPath);
        }

        FirmwareManifest manifest = JsonSerializer.Deserialize<FirmwareManifest>(
                File.ReadAllText(manifestPath),
                ManifestJsonOptions)
            ?? throw new InvalidOperationException("Bundled V5.9 firmware manifest is invalid.");
        if (manifest.Profiles is not { Count: > 0 })
        {
            throw new InvalidOperationException("Bundled V5.9 firmware manifest contains no profiles.");
        }

        foreach (FirmwareProfile profile in manifest.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || profile.Assets is not { Count: > 0 })
            {
                throw new InvalidOperationException("Bundled V5.9 firmware manifest contains an incomplete profile.");
            }
            foreach (FirmwareAsset asset in profile.Assets)
            {
                string path = Path.Combine(firmwareRoot, asset.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Bundled firmware asset is missing.", path);
                }
                string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Bundled firmware hash mismatch: " + asset.Path);
                }
            }
        }

        string esptool = Path.Combine(toolsRoot, "esptool.exe");
        if (!File.Exists(esptool))
        {
            throw new FileNotFoundException("Bundled esptool.exe is missing.", esptool);
        }

        string audioSender = Path.Combine(toolsRoot, "SendV55HapticAudioTest.exe");
        if (!File.Exists(audioSender))
        {
            throw new FileNotFoundException("Bundled haptic audio sender is missing.", audioSender);
        }

        string xinputProbe = Path.Combine(toolsRoot, "SteamXInputRumbleProbe.exe");
        if (!File.Exists(xinputProbe))
        {
            throw new FileNotFoundException("Bundled XInput rumble probe is missing.", xinputProbe);
        }

        string dualNs2ProHost = Path.Combine(toolsRoot, "DualNs2ProHost.exe");
        return new FirmwarePackage(manifest, firmwareRoot, toolsRoot, esptool, audioSender, xinputProbe, dualNs2ProHost);
    }

    private static void ExtractPrefix(string resourcePrefix, string destinationRoot)
    {
        lock (ExtractLock)
        {
            Directory.CreateDirectory(destinationRoot);
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                string normalized = resourceName.Replace('\\', '/');
                if (!normalized.StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = normalized[resourcePrefix.Length..];
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                string destination = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                string fullDestination = Path.GetFullPath(destination);
                string fullRoot = Path.GetFullPath(destinationRoot);
                if (!fullDestination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Unsafe embedded resource path: " + relative);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
                using Stream? source = assembly.GetManifestResourceStream(resourceName);
                if (source == null)
                {
                    throw new InvalidOperationException("Embedded resource not found: " + resourceName);
                }
                ExtractResource(source, fullDestination);
            }
        }
    }

    private static void ExtractResource(Stream source, string fullDestination)
    {
        long sourceLength = source.CanSeek ? source.Length : -1;
        if (File.Exists(fullDestination) &&
            EmbeddedResourceMatchesFile(source, fullDestination, sourceLength))
        {
            return;
        }

        string temp = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                source.CopyTo(target);
            }
            File.Move(temp, fullDestination, overwrite: true);
        }
        catch (IOException) when (File.Exists(fullDestination) &&
                                  EmbeddedResourceMatchesFile(source, fullDestination, sourceLength))
        {
            TryDelete(temp);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static bool EmbeddedResourceMatchesFile(Stream source, string path, long sourceLength)
    {
        if (sourceLength < 0 || !source.CanSeek)
        {
            return false;
        }
        if (!File.Exists(path) || new FileInfo(path).Length != sourceLength)
        {
            return false;
        }

        long originalPosition = source.Position;
        try
        {
            source.Position = 0;
            using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] sourceBuffer = new byte[8192];
            byte[] fileBuffer = new byte[8192];
            while (true)
            {
                int sourceRead = source.Read(sourceBuffer, 0, sourceBuffer.Length);
                int fileRead = existing.Read(fileBuffer, 0, fileBuffer.Length);
                if (sourceRead != fileRead)
                {
                    return false;
                }
                if (sourceRead == 0)
                {
                    return true;
                }
                for (int i = 0; i < sourceRead; i++)
                {
                    if (sourceBuffer[i] != fileBuffer[i])
                    {
                        return false;
                    }
                }
            }
        }
        finally
        {
            source.Position = originalPosition;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed record FirmwarePackage(
    FirmwareManifest Manifest,
    string FirmwareRoot,
    string ToolsRoot,
    string EsptoolPath,
    string AudioSenderPath,
    string XInputProbePath,
    string DualNs2ProHostPath)
{
    public FirmwareProfile GetProfile(string id)
    {
        return Manifest.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Bundled firmware profile not found: " + id);
    }
}

public sealed class FirmwareManifest
{
    public string PackageVersion { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public string Target { get; set; } = "esp32s3";
    public string FlashMode { get; set; } = "dio";
    public string FlashFreq { get; set; } = "80m";
    public string FlashSize { get; set; } = "16MB";
    public string DefaultProfile { get; set; } = "pro2_bridge_v5_5";
    public List<FirmwareProfile> Profiles { get; set; } = new();
    public string Notes { get; set; } = "";
}

public sealed class FirmwareProfile
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string App { get; set; } = "";
    public List<FirmwareAsset> Assets { get; set; } = new();
}

public sealed class FirmwareAsset
{
    public string Offset { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}



