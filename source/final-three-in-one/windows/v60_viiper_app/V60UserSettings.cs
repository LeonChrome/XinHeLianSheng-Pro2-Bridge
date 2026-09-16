using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

public sealed class V60UserSettings
{
    private static readonly object FileGate = new();

    public double RumbleMultiplier { get; set; } = 1.0;
    public string PushRateLabel { get; set; } = ViiperPushRateOption.Default.Label;
    public string BackendLabel { get; set; } = VirtualBackendOption.Default.Label;
    public string StickProcessingLabel { get; set; } = StickProcessingOption.Default.Label;
    public string SelectedModeKey { get; set; } = "dualsense";
    public bool LaunchAtLoginEnabled { get; set; }
    public bool AutoReconnectOnStartupEnabled { get; set; }
    public string[] LastConnectedPro2Addresses { get; set; } = new string[4];
    public Dictionary<string, Pro2StickCalibrationProfile> Pro2StickCalibrations { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool AudioEndpointGuardEnabled { get; set; } = true;
    public double Ps5GyroScalePitch { get; set; } = 1.0;
    public double Ps5GyroScaleYaw { get; set; } = 1.0;
    public double Ps5GyroScaleRoll { get; set; } = 1.0;
    public bool ProfessionalInvertGyroPitch { get; set; }
    public bool ProfessionalInvertGyroYaw { get; set; }
    public bool ProfessionalInvertGyroRoll { get; set; }

    public static V60UserSettings Load()
    {
        lock (FileGate)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return new V60UserSettings();
                }

                V60UserSettings? loaded =
                    JsonSerializer.Deserialize<V60UserSettings>(
                        File.ReadAllText(SettingsPath));
                if (loaded == null)
                {
                    return new V60UserSettings();
                }
                loaded.RumbleMultiplier =
                    NormalizeRumbleMultiplier(loaded.RumbleMultiplier);
                loaded.PushRateLabel =
                    ViiperPushRateOption.FromLabel(loaded.PushRateLabel).Label;
                loaded.BackendLabel =
                    VirtualBackendOption.FromLabel(loaded.BackendLabel).Label;
                loaded.StickProcessingLabel =
                    StickProcessingOption.FromLabel(loaded.StickProcessingLabel).Label;
                loaded.SelectedModeKey =
                    NormalizeModeKey(loaded.SelectedModeKey);
                loaded.LastConnectedPro2Addresses =
                    NormalizeAddressSlots(loaded.LastConnectedPro2Addresses);
                loaded.Pro2StickCalibrations =
                    NormalizeStickCalibrations(loaded.Pro2StickCalibrations);
                loaded.Ps5GyroScalePitch =
                    NormalizePs5GyroScale(loaded.Ps5GyroScalePitch);
                loaded.Ps5GyroScaleYaw =
                    NormalizePs5GyroScale(loaded.Ps5GyroScaleYaw);
                loaded.Ps5GyroScaleRoll =
                    NormalizePs5GyroScale(loaded.Ps5GyroScaleRoll);
                return loaded;
            }
            catch
            {
                return new V60UserSettings();
            }
        }
    }

    public void Save()
    {
        lock (FileGate)
        {
            string directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            RumbleMultiplier = NormalizeRumbleMultiplier(RumbleMultiplier);
            PushRateLabel = ViiperPushRateOption.FromLabel(PushRateLabel).Label;
            BackendLabel = VirtualBackendOption.FromLabel(BackendLabel).Label;
            StickProcessingLabel = StickProcessingOption.FromLabel(StickProcessingLabel).Label;
            SelectedModeKey = NormalizeModeKey(SelectedModeKey);
            LastConnectedPro2Addresses = NormalizeAddressSlots(LastConnectedPro2Addresses);
            Pro2StickCalibrations = NormalizeStickCalibrations(Pro2StickCalibrations);
            Ps5GyroScalePitch = NormalizePs5GyroScale(Ps5GyroScalePitch);
            Ps5GyroScaleYaw = NormalizePs5GyroScale(Ps5GyroScaleYaw);
            Ps5GyroScaleRoll = NormalizePs5GyroScale(Ps5GyroScaleRoll);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    this,
                    new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
    }

    public static double NormalizeRumbleMultiplier(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1.0;
        }
        return Math.Round(Math.Clamp(value, 0.0, 3.0), 1);
    }

    public static double NormalizePs5GyroScale(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1.0;
        }
        return Math.Round(Math.Clamp(value, 0.1, 4.0), 2);
    }

    public static string NormalizeModeKey(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "dualsenseedge" or "dualsense_edge" or "edge" or "ps5edge" => "dualsenseedge",
            "dualsenseproimu" or "dualsense_professional_imu" or "ps5proimu" or "ps5_professional_imu" => "dualsenseproimu",
            "pro2" or "ns2pro" or "nintendo" => "pro2",
            "xboxproimu" or "xbox_professional_imu" or "xinputproimu" => "xboxproimu",
            "xbox" or "xinput" or "xbox360" => "xbox",
            _ => "dualsense"
        };
    }

    public static string[] NormalizeAddressSlots(string[]? value)
    {
        string[] normalized = new string[4];
        if (value == null)
        {
            return normalized;
        }

        for (int i = 0; i < normalized.Length && i < value.Length; i++)
        {
            normalized[i] = NormalizeBleAddress(value[i]);
        }
        return normalized;
    }

    public static string NormalizeBleAddress(string? value)
    {
        string text = (value ?? "").Trim().ToUpperInvariant();
        if (text.Length == 0)
        {
            return "";
        }
        return text.Replace("-", ":");
    }

    public bool TryGetStickCalibration(
        string? address,
        out Pro2StickCalibrationProfile profile)
    {
        string normalizedAddress = NormalizeBleAddress(address);
        if (normalizedAddress.Length > 0 &&
            Pro2StickCalibrations.TryGetValue(normalizedAddress, out Pro2StickCalibrationProfile? saved))
        {
            profile = Pro2StickCalibrationProfile.Normalize(saved);
            return profile.CenterCalibrated;
        }

        profile = new Pro2StickCalibrationProfile();
        return false;
    }

    public void SetStickCalibration(
        string address,
        Pro2StickCalibrationProfile profile)
    {
        string normalizedAddress = NormalizeBleAddress(address);
        Pro2StickCalibrationProfile normalizedProfile =
            Pro2StickCalibrationProfile.Normalize(profile);
        if (normalizedAddress.Length == 0 || !normalizedProfile.CenterCalibrated)
        {
            return;
        }

        Pro2StickCalibrations[normalizedAddress] = normalizedProfile;
    }

    public static Dictionary<string, Pro2StickCalibrationProfile> NormalizeStickCalibrations(
        IDictionary<string, Pro2StickCalibrationProfile>? value)
    {
        var normalized =
            new Dictionary<string, Pro2StickCalibrationProfile>(StringComparer.OrdinalIgnoreCase);
        if (value == null)
        {
            return normalized;
        }

        foreach ((string address, Pro2StickCalibrationProfile profile) in value)
        {
            string normalizedAddress = NormalizeBleAddress(address);
            Pro2StickCalibrationProfile normalizedProfile =
                Pro2StickCalibrationProfile.Normalize(profile);
            if (normalizedAddress.Length > 0 && normalizedProfile.CenterCalibrated)
            {
                normalized[normalizedAddress] = normalizedProfile;
            }
        }
        return normalized;
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PRO2WirelessReceiverControlBoard",
        "v6_settings.json");
}
