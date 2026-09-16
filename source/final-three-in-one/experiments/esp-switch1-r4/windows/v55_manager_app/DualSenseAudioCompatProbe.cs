using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace Y700Switch2V55Manager;

public sealed record DualSenseCompatDevice(
    string Kind,
    string Name,
    string InstanceId,
    string ContainerId,
    string Status);

public sealed record DualSenseAudioEndpointProbe(
    string Name,
    string Type,
    string EndpointId);

public sealed record DualSenseAudioCompatProbeResult(
    IReadOnlyList<DualSenseCompatDevice> HidDevices,
    IReadOnlyList<DualSenseCompatDevice> AudioDevices,
    IReadOnlyList<DualSenseAudioEndpointProbe> AudioEndpoints,
    bool ContainerIdMatch,
    bool NameMatch)
{
    public string MatchMode => ContainerIdMatch
        ? "container_id"
        : NameMatch
            ? "name_only"
            : AudioEndpoints.Count == 0 && AudioDevices.Count == 0
                ? "no_audio_endpoint"
                : HidDevices.Count == 0
                    ? "no_hid"
                    : "none";
}

public static class DualSenseAudioCompatProbe
{
    public static DualSenseAudioCompatProbeResult Run()
    {
        IReadOnlyList<DualSenseCompatDevice> hid = GetPnpDevices(isAudio: false);
        IReadOnlyList<DualSenseCompatDevice> audio = GetPnpDevices(isAudio: true);
        IReadOnlyList<DualSenseAudioEndpointProbe> endpoints = GetAudioEndpoints();

        bool containerMatch = hid.Any(h =>
            !string.IsNullOrWhiteSpace(h.ContainerId) &&
            audio.Any(a => string.Equals(a.ContainerId, h.ContainerId, StringComparison.OrdinalIgnoreCase)));

        bool nameMatch = hid.Count > 0 && endpoints.Any(ep =>
            ep.Name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) ||
            ep.Name.Contains("DualSense", StringComparison.OrdinalIgnoreCase));

        return new DualSenseAudioCompatProbeResult(hid, audio, endpoints, containerMatch, nameMatch);
    }

    public static string FormatSummary(DualSenseAudioCompatProbeResult probe)
    {
        var lines = new List<string>
        {
            $"match_mode={probe.MatchMode} hid_count={probe.HidDevices.Count} audio_pnp_count={probe.AudioDevices.Count} audio_endpoint_count={probe.AudioEndpoints.Count}"
        };

        foreach (DualSenseCompatDevice hid in probe.HidDevices.Take(4))
        {
            lines.Add($"hid name={hid.Name} container={ValueOrUnknown(hid.ContainerId)} status={ValueOrUnknown(hid.Status)} instance={ValueOrUnknown(hid.InstanceId)}");
        }

        foreach (DualSenseCompatDevice audio in probe.AudioDevices.Take(4))
        {
            lines.Add($"audio_pnp name={audio.Name} container={ValueOrUnknown(audio.ContainerId)} status={ValueOrUnknown(audio.Status)} instance={ValueOrUnknown(audio.InstanceId)}");
        }

        foreach (DualSenseAudioEndpointProbe endpoint in probe.AudioEndpoints.Take(6))
        {
            lines.Add($"audio_endpoint name={endpoint.Name} type={endpoint.Type} id={endpoint.EndpointId}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<DualSenseCompatDevice> GetPnpDevices(bool isAudio)
    {
        var devices = new List<DualSenseCompatDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name,Status,PNPClass,DeviceID FROM Win32_PnPEntity");
            foreach (ManagementObject item in searcher.Get())
            {
                string name = Convert.ToString(item["Name"]) ?? "";
                string status = Convert.ToString(item["Status"]) ?? "";
                string pnpClass = Convert.ToString(item["PNPClass"]) ?? "";
                string deviceId = Convert.ToString(item["DeviceID"]) ?? "";

                bool match = isAudio
                    ? (pnpClass.Contains("AudioEndpoint", StringComparison.OrdinalIgnoreCase) ||
                       pnpClass.Contains("MEDIA", StringComparison.OrdinalIgnoreCase)) &&
                      LooksLikeDualSenseAudio(name, deviceId)
                    : LooksLikeDualSenseHid(name, deviceId);

                if (!match)
                {
                    continue;
                }

                devices.Add(new DualSenseCompatDevice(
                    isAudio ? "audio" : "hid",
                    string.IsNullOrWhiteSpace(name) ? "unknown" : name,
                    deviceId,
                    ReadContainerId(deviceId),
                    status));
            }
        }
        catch
        {
        }

        return devices;
    }

    private static IReadOnlyList<DualSenseAudioEndpointProbe> GetAudioEndpoints()
    {
        var endpoints = new List<DualSenseAudioEndpointProbe>();
        try
        {
            string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
            using RegistryKey? audioKey = Registry.LocalMachine.OpenSubKey(root);
            if (audioKey == null)
            {
                return endpoints;
            }

            foreach (string type in new[] { "Render", "Capture" })
            {
                using RegistryKey? typeKey = audioKey.OpenSubKey(type);
                if (typeKey == null)
                {
                    continue;
                }

                foreach (string endpointId in typeKey.GetSubKeyNames())
                {
                    using RegistryKey? endpointKey = typeKey.OpenSubKey(endpointId + "\\Properties");
                    if (endpointKey == null)
                    {
                        continue;
                    }

                    string? friendly = endpointKey.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                    string name = string.IsNullOrWhiteSpace(friendly) ? "unknown" : friendly;
                    if (!LooksLikeDualSenseAudio(name, endpointId))
                    {
                        continue;
                    }

                    endpoints.Add(new DualSenseAudioEndpointProbe(name, type.ToLowerInvariant(), endpointId));
                }
            }
        }
        catch
        {
        }

        return endpoints;
    }

    private static bool LooksLikeDualSenseHid(string name, string deviceId)
    {
        return name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) ||
               deviceId.Contains("VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase) ||
               deviceId.Contains("VID_054C&PID_0DF2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDualSenseAudio(string name, string id)
    {
        return name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Controller Speaker", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Sony Interactive", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("VID_054C&PID_0DF2", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadContainerId(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return "";
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            object? value = key?.GetValue("ContainerID");
            return value?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
