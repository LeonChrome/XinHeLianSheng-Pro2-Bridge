using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

/// <summary>A short-proven preference to request and retest, never current input evidence.</summary>
public sealed record BleFastConnectionHint(BleAdaptiveEnvironmentKey Environment,
    BleAdaptivePreset Preset, DateTimeOffset VerifiedAtUtc);

/// <summary>
/// Separate from fully verified adaptive presets. No saved hint admits gameplay
/// or proves stability; every use needs two new real-input gate windows.
/// </summary>
public static class BleFastConnectionHintStore
{
    private static readonly object FileGate = new();
    public const int MaximumFileBytes = 16384;
    public const int LifetimeDays = 7;
    private static string DefaultPath(BleAdaptiveEnvironmentKey environment)
    {
        // Firmware is the sole pre-ACK unknown. Keep the other exact identity
        // fields in the filename so four controllers cannot overwrite each other.
        var lookupIdentity = environment with { PolicyRevision = WithoutFirmware(environment.PolicyRevision) };
        string identityHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(lookupIdentity)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard", "ble-fast-connection-hints-v1", identityHash + ".json");
    }

    public static BleFastConnectionHint? Load(BleAdaptiveEnvironmentKey? expected, out string code, string? path = null) =>
        LoadCore(expected, false, out code, path);

    /// <summary>
    /// The only relaxed match: the firmware ACK is not available yet. All other
    /// identity fields, including this controller's MAC, remain an exact match.
    /// </summary>
    public static BleFastConnectionHint? LoadForEarlyRequest(BleAdaptiveEnvironmentKey? expected,
        out string code, string? path = null) => LoadCore(expected, true, out code, path);

    public static void Save(BleFastConnectionHint hint, BleAdaptiveMeasurement first,
        BleAdaptiveMeasurement second, string? path = null)
    {
        if (hint == null || !BleFastConnectionPolicy.IsHigh(first, second) ||
            !ValidHint(hint, DateTimeOffset.UtcNow, out _))
            throw new ArgumentException("Only a known device-scoped preference with two current real high-rate windows may be stored.", nameof(hint));
        Write(hint, path);
    }

    /// <summary>Seeds the retest hint after the existing full-cache validator has accepted real improvement.</summary>
    public static void SaveFromValidatedCache(BleAdaptiveCachedPreset cached, string? path = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (cached == null || !BleAdaptivePresetPolicy.IsCacheUsable(cached, cached.Environment, now, out _) ||
            !BleAdaptivePresetPolicy.MeetsTarget(cached.Verified))
            throw new ArgumentException("Only a fully validated high-rate cache entry may seed a request hint.", nameof(cached));
        var hint = new BleFastConnectionHint(cached.Environment, cached.Preset, cached.VerifiedAtUtc);
        if (!ValidHint(hint, now, out _))
            throw new ArgumentException("The validated preference must have complete exact identity and an allowed hint preset.", nameof(cached));
        Write(hint, path);
    }

    private static void Write(BleFastConnectionHint hint, string? path)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(hint);
        if (bytes.Length > MaximumFileBytes) throw new ArgumentException("Preference identity exceeds the bounded hint size.", nameof(hint));
        lock (FileGate)
        {
            string target = Path.GetFullPath(path ?? DefaultPath(hint.Environment));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static BleFastConnectionHint? LoadCore(BleAdaptiveEnvironmentKey? expected, bool early,
        out string code, string? path)
    {
        code = "fast_hint_environment_unavailable";
        if (!ValidEnvironment(expected, requireFirmware: !early)) return null;
        lock (FileGate)
        {
            try
            {
                string target = path ?? DefaultPath(expected!);
                if (!File.Exists(target)) { code = "fast_hint_absent"; return null; }
                using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > MaximumFileBytes) { code = "fast_hint_oversized"; return null; }
                // Keep the read bounded even if another process changes the file after opening it.
                byte[] bytes = new byte[MaximumFileBytes + 1];
                int count = 0, read;
                while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) != 0) count += read;
                if (count > MaximumFileBytes) { code = "fast_hint_oversized"; return null; }
                var entry = JsonSerializer.Deserialize<BleFastConnectionHint>(bytes.AsSpan(0, count));
                if (entry == null || !ValidHint(entry, DateTimeOffset.UtcNow, out code)) return null;
                bool matches = early ? SameExceptFirmware(entry.Environment, expected!) : entry.Environment == expected;
                if (!matches) { code = "fast_hint_environment_mismatch"; return null; }
                code = early ? "fast_hint_early_request_only_retest_required" : "fast_hint_exact_environment_retest_required";
                return entry;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or NotSupportedException)
            {
                code = "fast_hint_read_" + error.GetType().Name;
                return null;
            }
        }
    }

    private static bool ValidHint(BleFastConnectionHint hint, DateTimeOffset now, out string code)
    {
        code = "fast_hint_invalid_identity";
        if (!ValidEnvironment(hint.Environment, requireFirmware: true)) return false;
        code = "fast_hint_preset_not_allowed";
        // No arbitrary ABI parameters, native result, 15ms, or flexible range is persisted here.
        if (hint.Preset is not (BleAdaptivePreset.Win11Fixed7p5 or BleAdaptivePreset.Win11ThroughputOptimized)) return false;
        code = "fast_hint_expired_or_future";
        if (hint.VerifiedAtUtc > now || now - hint.VerifiedAtUtc > TimeSpan.FromDays(LifetimeDays)) return false;
        code = "fast_hint_valid_preference_only";
        return true;
    }

    private static bool ValidEnvironment(BleAdaptiveEnvironmentKey? environment, bool requireFirmware)
    {
        if (environment == null || environment.WindowsBuild < 22000 ||
            environment.BluetoothStackSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
            !Known(environment.AdapterPnpId) || !Known(environment.AdapterSetFingerprint) ||
            !Known(environment.DriverProvider) || !Known(environment.DriverVersion) || !Known(environment.DriverDate) ||
            !TryPolicyTokens(environment.PolicyRevision, out string os, out string firmware, out string controller, out string peers)) return false;
        if (!Version.TryParse(os, out var version) || version.Major != 10 || version.Build != environment.WindowsBuild ||
            !int.TryParse(peers, NumberStyles.None, CultureInfo.InvariantCulture, out int peerCount) || peerCount < 0 || peerCount > 255) return false;
        string[] mac = controller.Split(':');
        return mac.Length == 6 && mac.All(part => part.Length == 2 && part.All(Uri.IsHexDigit)) &&
            controller != "00:00:00:00:00:00" && !controller.Equals("FF:FF:FF:FF:FF:FF", StringComparison.OrdinalIgnoreCase) &&
            (requireFirmware ? Known(firmware) : !string.IsNullOrWhiteSpace(firmware));
    }

    private static bool Known(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 &&
        !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !value.Equals("missing", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static bool TryPolicyTokens(string? revision, out string os, out string firmware, out string controller, out string peers)
    {
        os = firmware = controller = peers = "";
        if (!Known(revision)) return false;
        string[] parts = revision!.Split('|');
        if (parts.Length < 5 || !Known(parts[0])) return false;
        bool Get(string key, out string value)
        {
            string prefix = key + "=";
            string[] matches = parts.Where(part => part.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            value = matches.Length == 1 ? matches[0][prefix.Length..] : "";
            return matches.Length == 1 && !string.IsNullOrWhiteSpace(value);
        }
        return Get("os", out os) && Get("fw", out firmware) && Get("controller", out controller) && Get("peer_count", out peers);
    }

    private static bool SameExceptFirmware(BleAdaptiveEnvironmentKey saved, BleAdaptiveEnvironmentKey expected)
    {
        var savedIdentity = saved with { PolicyRevision = WithoutFirmware(saved.PolicyRevision) };
        var expectedIdentity = expected with { PolicyRevision = WithoutFirmware(expected.PolicyRevision) };
        return savedIdentity == expectedIdentity;
    }

    private static string WithoutFirmware(string revision) => string.Join("|", revision.Split('|')
        .Where(part => !part.StartsWith("fw=", StringComparison.Ordinal)));
}
