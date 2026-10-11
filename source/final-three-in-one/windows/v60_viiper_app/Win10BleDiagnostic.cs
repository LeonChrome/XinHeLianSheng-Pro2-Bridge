using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Y700Switch2V60Viiper;

public sealed record BluetoothAdapterDiagnosticInfo(
    string Name,
    string PnpDeviceId,
    string Vid,
    string Pid,
    string DriverProvider,
    string DriverVersion,
    string DriverDate,
    string WinRtDeviceId);

public sealed record BleGapStatistics(
    int SampleCount,
    double P50Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms,
    double MinMs,
    double MaxMs,
    IReadOnlyDictionary<string, int> Distribution);

public sealed record BleTrialMeasurement(
    string Phase, double DurationSeconds, double Fd2Hz, double ParsedHz,
    int Fd2Count, int ParsedCount, ushort ConnectionIntervalUnits,
    string RequestStatus, BleGapStatistics Gaps, double LastInputAgeMs = double.PositiveInfinity,
    bool ConnectedAtEnd = false, NotifyGapSummary? ExactGapSummary = null);

public sealed record Win10BleDiagnosticReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double DurationSeconds,
    string ExperimentProfile,
    string WindowsProductName,
    string WindowsDisplayVersion,
    string WindowsBuild,
    string RuntimeOsVersion,
    BluetoothAdapterDiagnosticInfo Adapter,
    string ConnectedPro2Address,
    string Fd2DescriptorHandle,
    string Fd2DescriptorUuid,
    ushort RequestedFd2ReportRateHz,
    string Fd2DescriptorValueBefore,
    string Fd2DescriptorWriteStatus,
    string Fd2DescriptorValueAfter,
    int RawNotifyCount,
    int Fd2NotifyCount,
    int ParsedCount,
    double RawNotifyHz,
    double Fd2NotifyHz,
    double ParsedHz,
    BleGapStatistics Fd2NotifyGaps,
    string Verdict)
{
    public double EffectiveInputHz => Math.Min(Fd2NotifyHz, ParsedHz);
    public double ParsedRatio => Fd2NotifyHz > 0 ? ParsedHz / Fd2NotifyHz : 0;
    public bool Input133HzAccepted => Win10BleDiagnostic.Is133HzInputAccepted(Fd2NotifyHz, ParsedHz);
    public string CompatibilityRoute { get; init; } = "unknown";
    public string CompatibilityGuidance { get; init; } = "";
    public string SystemStackSha256 { get; init; } = "unknown";
    public bool Win10StackValuesConfigured { get; init; }
    public bool PublicRequestApiAvailable { get; init; }
    public bool PublicReadApiAvailable { get; init; }
    public byte BatteryLevelAtConnection { get; init; } = GamepadState.BatteryUnknown;
    public byte BatteryLastReadLevel { get; init; } = GamepadState.BatteryUnknown;
    public bool RuntimeBatteryRefreshEnabled { get; init; }
    public string RuntimeBatteryRefreshStatus { get; init; } = "unknown";
    public string RuntimeBatterySuspensionReason { get; init; } = "";
    public double BatteryLevelAgeSeconds { get; init; }
    public ushort BatteryVoltageMillivolts { get; init; }
    public bool BatteryChargeStateKnown { get; init; }
    public bool BatteryCharging { get; init; }
    public bool BatteryExternalPower { get; init; }
    public string BatteryTransport { get; init; } = "unknown";
    public ushort ActualConnectionIntervalUnits { get; init; }
    public double ActualConnectionIntervalMs => ActualConnectionIntervalUnits * 1.25;
    public ushort ActualConnectionLatency { get; init; }
    public ushort ActualLinkTimeoutUnits { get; init; }
    public string ConnectionPreferenceStatus { get; init; } = "unknown";
    public bool Custom7p5ExperimentEnabled { get; init; }
    public string Win11ParameterRoute { get; init; } = "not_evaluated";
    public BleTrialMeasurement? BeforeParameters { get; init; }
    public BleTrialMeasurement? AfterCustomParameters { get; init; }
    public IEnumerable<string> ToLogLines()
    {
        yield return "[BLE_COMPATIBILITY] route=" + CompatibilityRoute +
            " bthport_sha256=" + SystemStackSha256 + " win10_configured=" + Win10StackValuesConfigured +
            " request_api=" + PublicRequestApiAvailable + " read_api=" + PublicReadApiAvailable;
        yield return "[BLE_TURBO_TEST] profile=" + ExperimentProfile +
                     " duration_s=" + DurationSeconds.ToString("F2") +
                     " verdict=" + Verdict;
        yield return "[BLE_TURBO_LINK] actual_interval_units=" + ActualConnectionIntervalUnits +
            " actual_interval_ms=" + ActualConnectionIntervalMs.ToString("F2") +
            " latency=" + ActualConnectionLatency + " timeout_units=" + ActualLinkTimeoutUnits +
            " preference_status=" + ConnectionPreferenceStatus + " custom_7p5_experiment=" + Custom7p5ExperimentEnabled +
            " win11_parameter_route=" + Win11ParameterRoute;
        foreach (BleTrialMeasurement? trial in new[] { BeforeParameters, AfterCustomParameters })
        {
            if (trial == null) continue;
            yield return "[BLE_TRIAL_COMPARE] phase=" + trial.Phase + " duration_s=" + trial.DurationSeconds.ToString("F2") +
                " fd2_hz=" + trial.Fd2Hz.ToString("F2") + " parsed_hz=" + trial.ParsedHz.ToString("F2") +
                " actual_interval_ms=" + (trial.ConnectionIntervalUnits * 1.25).ToString("F2") +
                " p50_gap_ms=" + trial.Gaps.P50Ms.ToString("F2") + " p95_gap_ms=" + trial.Gaps.P95Ms.ToString("F2") +
                " request_status=" + trial.RequestStatus;
        }
        yield return "[BLE_TURBO_OS] product=\"" + WindowsProductName +
                     "\" display_version=" + WindowsDisplayVersion +
                     " build=" + WindowsBuild +
                     " runtime=" + RuntimeOsVersion;
        yield return "[BLE_TURBO_ADAPTER] name=\"" + Adapter.Name +
                     "\" pnp_id=\"" + Adapter.PnpDeviceId +
                     "\" vid=" + Adapter.Vid +
                     " pid=" + Adapter.Pid +
                     " provider=\"" + Adapter.DriverProvider +
                     "\" driver_version=" + Adapter.DriverVersion +
                     " driver_date=" + Adapter.DriverDate;
        yield return "[BLE_TURBO_FD2_DESCRIPTOR] handle=" + Fd2DescriptorHandle +
                     " uuid=" + Fd2DescriptorUuid +
                     " requested_hz=" + RequestedFd2ReportRateHz +
                     " before=" + Fd2DescriptorValueBefore +
                     " write_status=" + Fd2DescriptorWriteStatus +
                     " after=" + Fd2DescriptorValueAfter;
        yield return "[BLE_TURBO_RATE] raw_notify_hz=" + RawNotifyHz.ToString("F2") +
                     " fd2_notify_hz=" + Fd2NotifyHz.ToString("F2") +
                     " parsed_hz=" + ParsedHz.ToString("F2") +
                     " raw_count=" + RawNotifyCount +
                     " fd2_count=" + Fd2NotifyCount +
                     " parsed_count=" + ParsedCount;
        yield return "[BLE_TURBO_INPUT_ACCEPTANCE] effective_input_hz=" + EffectiveInputHz.ToString("F2") +
            " parsed_ratio=" + ParsedRatio.ToString("F4") + " accepted_133=" + Input133HzAccepted;
        yield return "[PRO2_BATTERY_DIAGNOSTIC] connection_level=" +
            (BatteryLevelAtConnection <= 9 ? BatteryLevelAtConnection + "/9" : "unknown") +
            " last_read_level=" + (BatteryLastReadLevel <= 9 ? BatteryLastReadLevel + "/9" : "unknown") +
            " runtime_refresh=" + RuntimeBatteryRefreshEnabled + " refresh_status=" + RuntimeBatteryRefreshStatus +
            " suspension_reason=" + RuntimeBatterySuspensionReason +
            " level_age_s=" + BatteryLevelAgeSeconds.ToString("F1") +
            " voltage_mv=" + BatteryVoltageMillivolts + " charge_known=" + BatteryChargeStateKnown +
            " charging=" + BatteryCharging + " external_power=" + BatteryExternalPower +
            " transport=" + BatteryTransport;
        yield return "[BLE_TURBO_GAP] p50_ms=" + Fd2NotifyGaps.P50Ms.ToString("F2") +
                     " p90_ms=" + Fd2NotifyGaps.P90Ms.ToString("F2") +
                     " p95_ms=" + Fd2NotifyGaps.P95Ms.ToString("F2") +
                     " p99_ms=" + Fd2NotifyGaps.P99Ms.ToString("F2") +
                     " min_ms=" + Fd2NotifyGaps.MinMs.ToString("F2") +
                     " max_ms=" + Fd2NotifyGaps.MaxMs.ToString("F2") +
                     " samples=" + Fd2NotifyGaps.SampleCount;
        yield return "[BLE_TURBO_GAP_DISTRIBUTION] " + string.Join(
            " ",
            Fd2NotifyGaps.Distribution.Select(pair => pair.Key + "=" + pair.Value));
    }
}

internal sealed class Win10BleDiagnosticCapture
{
    private const int MaximumSamples = 8192;
    private readonly List<long> rawTicks = new(MaximumSamples);
    private readonly List<long> fd2Ticks = new(MaximumSamples);
    private readonly List<long> parsedTicks = new(MaximumSamples);

    public Win10BleDiagnosticCapture(long startedTicks, DateTimeOffset startedAtUtc)
    {
        StartedTicks = startedTicks;
        StartedAtUtc = startedAtUtc;
    }

    public long StartedTicks { get; }
    public DateTimeOffset StartedAtUtc { get; }

    public void RecordRaw(long ticks, bool isFd2)
    {
        Add(rawTicks, ticks);
        if (isFd2)
        {
            Add(fd2Ticks, ticks);
        }
    }

    public void RecordParsed(long ticks, bool isFd2)
    {
        if (isFd2)
        {
            Add(parsedTicks, ticks);
        }
    }

    public Win10BleDiagnosticSamples Snapshot(long completedTicks)
    {
        return new Win10BleDiagnosticSamples(
            StartedTicks,
            completedTicks,
            rawTicks.ToArray(),
            fd2Ticks.ToArray(),
            parsedTicks.ToArray());
    }

    private static void Add(List<long> samples, long ticks)
    {
        if (samples.Count < MaximumSamples)
        {
            samples.Add(ticks);
        }
    }
}

internal sealed record Win10BleDiagnosticSamples(
    long StartedTicks,
    long CompletedTicks,
    long[] RawTicks,
    long[] Fd2Ticks,
    long[] ParsedTicks)
{
    public double DurationSeconds => Math.Max(
        0.001,
        (CompletedTicks - StartedTicks) / (double)Stopwatch.Frequency);
}

internal static class Win10BleDiagnostic
{
    private static readonly string[] RequestedDeviceProperties =
    [
        "System.Devices.DeviceInstanceId",
        "System.Devices.HardwareIds",
        "System.Devices.DriverProvider",
        "System.Devices.DriverVersion",
        "System.Devices.DriverDate",
        "System.Devices.Manufacturer"
    ];

    public static async Task<BluetoothAdapterDiagnosticInfo> CaptureAdapterAsync()
    {
        BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null)
        {
            return UnknownAdapter("BluetoothAdapter.GetDefaultAsync returned null");
        }

        return await CaptureAdapterAsync(adapter);
    }

    public static async Task<BluetoothAdapterDiagnosticInfo> CaptureAdapterAsync(BluetoothAdapter adapter)
    {

        string name = "unknown";
        string pnpId = "";
        string provider = "";
        string version = "";
        string date = "";
        try
        {
            DeviceInformation info = await DeviceInformation.CreateFromIdAsync(
                adapter.DeviceId,
                RequestedDeviceProperties);
            name = EmptyToUnknown(info.Name);
            pnpId = PropertyString(info, "System.Devices.DeviceInstanceId");
            provider = PropertyString(info, "System.Devices.DriverProvider");
            version = PropertyString(info, "System.Devices.DriverVersion");
            date = PropertyString(info, "System.Devices.DriverDate");
        }
        catch
        {
            // Registry fallback below still records the adapter and driver metadata.
        }

        if (string.IsNullOrWhiteSpace(pnpId))
        {
            pnpId = DeviceInstanceIdFromWinRtId(adapter.DeviceId);
        }

        RegistryAdapterDetails registry = ReadRegistryAdapter(pnpId);
        if (string.IsNullOrWhiteSpace(pnpId)) pnpId = registry.PnpDeviceId;
        // A WinRT interface name may be the PC's name, not the radio model.
        if (!string.IsNullOrWhiteSpace(registry.Name)) name = EmptyToUnknown(registry.Name);
        if (string.IsNullOrWhiteSpace(provider)) provider = registry.Provider;
        if (string.IsNullOrWhiteSpace(version)) version = registry.DriverVersion;
        if (string.IsNullOrWhiteSpace(date)) date = registry.DriverDate;

        (string vid, string pid) = ParseVidPid(pnpId);
        return new BluetoothAdapterDiagnosticInfo(
            EmptyToUnknown(name),
            EmptyToUnknown(pnpId),
            EmptyToUnknown(vid),
            EmptyToUnknown(pid),
            EmptyToUnknown(provider),
            EmptyToUnknown(version),
            EmptyToUnknown(date),
            EmptyToUnknown(adapter.DeviceId));
    }

    public static Win10BleDiagnosticReport BuildReport(
        Win10BleDiagnosticSamples samples,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        BluetoothAdapterDiagnosticInfo adapter,
        string connectedAddress,
        ushort descriptorHandle,
        string descriptorUuid,
        ushort requestedHz,
        string descriptorBefore,
        string descriptorWriteStatus,
        string descriptorAfter)
    {
        double seconds = samples.DurationSeconds;
        // Count over the entire capture: a disconnect/silent tail must reduce the
        // reported rate, rather than disappearing from a first-to-last calculation.
        double rawHz = samples.RawTicks.Length / seconds;
        double fd2Hz = samples.Fd2Ticks.Length / seconds;
        double parsedHz = samples.ParsedTicks.Length / seconds;
        BleCompatibilitySnapshot compatibility = BleCompatibilityInspector.Refresh();
        return new Win10BleDiagnosticReport(
            startedAtUtc,
            completedAtUtc,
            seconds,
            ReadActiveExperimentProfile(),
            compatibility.Facts.Build >= 22000 ? "Windows 11 " + ReadWindowsValue("EditionID") : ReadWindowsValue("ProductName"),
            ReadWindowsValue("DisplayVersion"),
            ReadWindowsBuild(),
            Environment.OSVersion.VersionString,
            adapter,
            EmptyToUnknown(connectedAddress),
            descriptorHandle == 0 ? "none" : "0x" + descriptorHandle.ToString("X4"),
            EmptyToUnknown(descriptorUuid),
            requestedHz,
            EmptyToUnknown(descriptorBefore),
            EmptyToUnknown(descriptorWriteStatus),
            EmptyToUnknown(descriptorAfter),
            samples.RawTicks.Length,
            samples.Fd2Ticks.Length,
            samples.ParsedTicks.Length,
            rawHz,
            fd2Hz,
            parsedHz,
            BuildGapStatistics(samples.Fd2Ticks),
            ClassifyRates(fd2Hz, parsedHz))
        {
            CompatibilityRoute = compatibility.Plan.Route.ToString(),
            CompatibilityGuidance = compatibility.Plan.Guidance,
            SystemStackSha256 = compatibility.Facts.StackSha256,
            Win10StackValuesConfigured = compatibility.Facts.Win10StackValuesConfigured,
            PublicRequestApiAvailable = compatibility.Facts.RequestParametersApiAvailable,
            PublicReadApiAvailable = compatibility.Facts.ReadParametersApiAvailable
        };
    }

    public static async Task<(string JsonPath, string TextPath)> SaveAsync(
        Win10BleDiagnosticReport report,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "v6_logs",
            "ble_turbo");
        Directory.CreateDirectory(directory);
        string stem = "ble-diagnostic-" +
                      SanitizeFileName(report.ExperimentProfile) + "-" +
                      DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string jsonPath = Path.Combine(directory, stem + ".json");
        string textPath = Path.Combine(directory, stem + ".log");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, options),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllLinesAsync(
            textPath,
            report.ToLogLines(),
            new UTF8Encoding(false),
            cancellationToken);
        return (jsonPath, textPath);
    }

    internal static BleGapStatistics BuildGapStatistics(long[] ticks)
    {
        double[] gaps = ticks.Length < 2
            ? []
            : ticks.Zip(ticks.Skip(1), (left, right) =>
                    (right - left) * 1000.0 / Stopwatch.Frequency)
                .OrderBy(value => value)
                .ToArray();
        var distribution = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["near_7_5ms"] = 0,
            ["near_15ms"] = 0,
            ["near_22_5ms"] = 0,
            ["near_30ms"] = 0,
            ["near_37_5ms"] = 0,
            ["near_45ms"] = 0,
            ["near_50ms"] = 0,
            ["near_60ms"] = 0,
            ["other"] = 0
        };
        foreach (double gap in gaps)
        {
            string bucket = gap switch
            {
                >= 5.0 and < 11.25 => "near_7_5ms",
                >= 11.25 and < 18.75 => "near_15ms",
                >= 18.75 and < 26.25 => "near_22_5ms",
                >= 26.25 and < 33.75 => "near_30ms",
                >= 33.75 and < 41.25 => "near_37_5ms",
                >= 41.25 and < 47.50 => "near_45ms",
                >= 47.50 and < 55.00 => "near_50ms",
                >= 55.00 and < 65.00 => "near_60ms",
                _ => "other"
            };
            distribution[bucket]++;
        }

        return new BleGapStatistics(
            gaps.Length,
            Percentile(gaps, 0.50),
            Percentile(gaps, 0.90),
            Percentile(gaps, 0.95),
            Percentile(gaps, 0.99),
            gaps.Length == 0 ? 0 : gaps[0],
            gaps.Length == 0 ? 0 : gaps[^1],
            distribution);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        double weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    private static double SampleRate(long[] ticks)
    {
        if (ticks.Length < 2) return 0;
        double seconds = (ticks[^1] - ticks[0]) / (double)Stopwatch.Frequency;
        return seconds <= 0 ? 0 : (ticks.Length - 1) / seconds;
    }

    public static string ClassifyRates(double fd2Hz, double parsedHz)
    {
        if (!double.IsFinite(fd2Hz) || !double.IsFinite(parsedHz) || fd2Hz < 0 || parsedHz < 0)
            return "invalid_rate_measurement";
        if (fd2Hz >= 30 && parsedHz < 30)
            return "input_parse_failure_fd2_receiving_but_parsed_below_30hz";
        if (fd2Hz >= 30 && parsedHz / fd2Hz < 0.95)
            return "input_parse_loss_not_accepted";
        return ClassifyEffectiveRate(Math.Min(fd2Hz, parsedHz));
    }

    public static bool Is133HzInputAccepted(double fd2Hz, double parsedHz) =>
        double.IsFinite(fd2Hz) && double.IsFinite(parsedHz) &&
        fd2Hz >= PureSoftware133Mode.MinimumAcceptedHz && parsedHz >= PureSoftware133Mode.MinimumAcceptedHz &&
        parsedHz / fd2Hz >= 0.95;

    private static string ClassifyEffectiveRate(double fd2Hz) => fd2Hz switch
    {
        >= 100 => "high_performance_success_ge_100hz",
        >= 60 => "success_ge_60hz",
        >= 50 => "clear_improvement_below_target_50_60hz",
        >= 30 => "partial_improvement_30_50hz",
        _ => "no_clear_improvement_below_30hz"
    };

    private static string ReadWindowsValue(string name)
    {
        return EmptyToUnknown(Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            name,
            "")?.ToString());
    }

    private static string ReadWindowsBuild()
    {
        string build = ReadWindowsValue("CurrentBuildNumber");
        string ubr = ReadWindowsValue("UBR");
        return ubr == "unknown" ? build : build + "." + ubr;
    }

    private static string ReadActiveExperimentProfile()
    {
        // Do not mislabel the newly verified global route as an old adapter-level A experiment.
        BleCompatibilitySnapshot compatibility = BleCompatibilityInspector.Current;
        if (compatibility.Facts.Build >= 22000) return "WIN11_PUBLIC_OR_NATIVE";
        if (compatibility.Facts.Win10StackValuesConfigured)
            return compatibility.Plan.CanConfigureWin10Stack ? "WIN10_STACK_7P5MS" : "WIN10_EXISTING_STACK_UNAUDITED";
        try
        {
            string marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "XinHeLianSheng-Pro2-Bridge",
                "BLE-Turbo",
                "active-profile.json");
            if (!File.Exists(marker)) return "BASELINE";
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(marker));
            if (document.RootElement.TryGetProperty("profile", out JsonElement value))
            {
                return EmptyToUnknown(value.GetString()).ToUpperInvariant();
            }
        }
        catch
        {
        }
        return "BASELINE";
    }

    private static RegistryAdapterDetails ReadRegistryAdapter(string preferredPnpId)
    {
        if (!string.IsNullOrWhiteSpace(preferredPnpId))
        {
            // Match the exact PNP instance, including non-USB radios. Never borrow
            // another adapter's driver details to fill an unknown default radio.
            using RegistryKey? exact = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + preferredPnpId);
            return ReadRegistryAdapterDetails(exact, preferredPnpId);
        }
        const string enumUsbPath = @"SYSTEM\CurrentControlSet\Enum\USB";
        using RegistryKey? enumUsb = Registry.LocalMachine.OpenSubKey(enumUsbPath);
        if (enumUsb == null) return new RegistryAdapterDetails();

        RegistryAdapterDetails? first = null;
        foreach (string hardware in enumUsb.GetSubKeyNames())
        {
            using RegistryKey? hardwareKey = enumUsb.OpenSubKey(hardware);
            if (hardwareKey == null) continue;
            foreach (string instance in hardwareKey.GetSubKeyNames())
            {
                string pnpId = "USB\\" + hardware + "\\" + instance;
                using RegistryKey? deviceKey = hardwareKey.OpenSubKey(instance);
                if (!IsBluetoothAdapterKey(deviceKey)) continue;
                RegistryAdapterDetails candidate = ReadRegistryAdapterDetails(deviceKey, pnpId);
                first ??= candidate;
                if (!string.IsNullOrWhiteSpace(preferredPnpId) &&
                    string.Equals(pnpId, preferredPnpId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return first ?? new RegistryAdapterDetails();
    }

    private static bool IsBluetoothAdapterKey(RegistryKey? key)
    {
        return string.Equals(
            key?.GetValue("ClassGUID")?.ToString(),
            "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static RegistryAdapterDetails ReadRegistryAdapterDetails(
        RegistryKey? deviceKey,
        string pnpId)
    {
        if (deviceKey == null) return new RegistryAdapterDetails(PnpDeviceId: pnpId);
        string name = deviceKey.GetValue("FriendlyName")?.ToString() ??
                      deviceKey.GetValue("DeviceDesc")?.ToString() ?? "";
        // DeviceDesc can be an INF resource reference followed by its display name.
        if (name.StartsWith('@') && name.Contains(';')) name = name[(name.LastIndexOf(';') + 1)..];
        string driver = deviceKey.GetValue("Driver")?.ToString() ?? "";
        string provider = deviceKey.GetValue("Mfg")?.ToString() ?? "";
        string version = "";
        string date = "";
        if (!string.IsNullOrWhiteSpace(driver))
        {
            using RegistryKey? driverKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\" + driver);
            provider = driverKey?.GetValue("ProviderName")?.ToString() ?? provider;
            version = driverKey?.GetValue("DriverVersion")?.ToString() ?? "";
            date = driverKey?.GetValue("DriverDate")?.ToString() ?? "";
        }
        return new RegistryAdapterDetails(name, pnpId, provider, version, date);
    }

    private static string PropertyString(DeviceInformation info, string name)
    {
        if (!info.Properties.TryGetValue(name, out object? value) || value == null)
        {
            return "";
        }
        if (value is string[] array) return string.Join(",", array);
        if (value is IEnumerable<string> strings) return string.Join(",", strings);
        return value.ToString() ?? "";
    }

    private static string DeviceInstanceIdFromWinRtId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string normalized = id.Trim();
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }
        int classGuid = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (classGuid >= 0) normalized = normalized[..classGuid];
        return normalized.Replace('#', '\\');
    }

    private static (string Vid, string Pid) ParseVidPid(string pnpId)
    {
        string upper = pnpId.ToUpperInvariant();
        int vidAt = upper.IndexOf("VID_", StringComparison.Ordinal);
        int pidAt = upper.IndexOf("PID_", StringComparison.Ordinal);
        string vid = vidAt >= 0 && upper.Length >= vidAt + 8
            ? upper.Substring(vidAt + 4, 4)
            : "";
        string pid = pidAt >= 0 && upper.Length >= pidAt + 8
            ? upper.Substring(pidAt + 4, 4)
            : "";
        return (vid, pid);
    }

    private static BluetoothAdapterDiagnosticInfo UnknownAdapter(string reason) =>
        new(reason, "unknown", "unknown", "unknown", "unknown", "unknown", "unknown", "unknown");

    private static string EmptyToUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
        }
        return builder.ToString();
    }

    private sealed record RegistryAdapterDetails(
        string Name = "",
        string PnpDeviceId = "",
        string Provider = "",
        string DriverVersion = "",
        string DriverDate = "");
}
