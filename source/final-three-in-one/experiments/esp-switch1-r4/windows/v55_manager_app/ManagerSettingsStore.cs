using System;
using System.IO;
using System.Text.Json;

namespace Y700Switch2V55Manager;

public sealed class ManagerSettings
{
    public string LastPortName { get; set; } = "";
    public string LastBleTarget { get; set; } = "";
    public string LastAudioDeviceName { get; set; } = "";
    public string DesiredModeId { get; set; } = "";
    public string LastSuccessfulProfileId { get; set; } = "";
    public string PreviousSuccessfulProfileId { get; set; } = "";
    public string PendingProfileId { get; set; } = "";
    public string PendingExpectedUsbMarker { get; set; } = "";
    public DateTime? PendingRequestedUtc { get; set; }
    public bool FirstRunGuideOffered { get; set; }
    public bool FirstRunGuideCompleted { get; set; }
    public bool XboxPaddleLeftEnabled { get; set; }
    public bool XboxPaddleRightEnabled { get; set; }
    public string XboxPaddleLeftMode { get; set; } = "hold";
    public string XboxPaddleRightMode { get; set; } = "hold";
    public string XboxPaddleLeftTargets { get; set; } = "B";
    public string XboxPaddleRightTargets { get; set; } = "A";
    public string XboxPaddleLeftTapMs { get; set; } = "70";
    public string XboxPaddleRightTapMs { get; set; } = "70";
    public string XboxPaddleLeftTurboOnMs { get; set; } = "45";
    public string XboxPaddleLeftTurboOffMs { get; set; } = "45";
    public string XboxPaddleRightTurboOnMs { get; set; } = "45";
    public string XboxPaddleRightTurboOffMs { get; set; } = "45";
}

public static class ManagerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PRO2WirelessReceiverControlBoard",
        "manager_settings.json");

    public static ManagerSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new ManagerSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ManagerSettings>(json, JsonOptions) ?? new ManagerSettings();
        }
        catch
        {
            return new ManagerSettings();
        }
    }

    public static void Save(ManagerSettings settings)
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(settings ?? new ManagerSettings(), JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
        }
    }
}
