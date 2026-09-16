using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Y700Switch2V60Viiper;

public static class AudioEndpointGuard
{
    private const int DeviceStateActive = 0x00000001;
    private const int StgmRead = 0x00000000;

    private static readonly PropertyKey DeviceFriendlyNameKey = new(
        Guid.Parse("a45c254e-df1c-4efd-8020-67d146a850e0"),
        14);

    public static IReadOnlyList<string> EnsureDualSenseIsNotDefault()
    {
        var logs = new List<string>();
        object? enumeratorObject = null;
        object? policyObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            policyObject = new PolicyConfigClientComObject();
            var policy = (IPolicyConfig)policyObject;

            GuardFlow(enumerator, policy, AudioDataFlow.Render, logs);
            GuardFlow(enumerator, policy, AudioDataFlow.Capture, logs);
            if (logs.Count == 0)
            {
                logs.Add("[AUDIO_GUARD] checked defaults; DualSense is not the default playback/communication endpoint.");
            }
        }
        catch (Exception ex)
        {
            logs.Add("[AUDIO_GUARD] warning: unable to inspect or adjust Windows audio defaults: " +
                     ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            ReleaseComObject(policyObject);
            ReleaseComObject(enumeratorObject);
        }

        return logs;
    }

    private static void GuardFlow(
        IMMDeviceEnumerator enumerator,
        IPolicyConfig policy,
        AudioDataFlow flow,
        List<string> logs)
    {
        List<AudioEndpoint> active = EnumerateActiveEndpoints(enumerator, flow);
        if (active.Count == 0)
        {
            logs.Add("[AUDIO_GUARD] " + flow + " has no active endpoint.");
            return;
        }

        AudioEndpoint? fallback = ChooseFallback(enumerator, flow, active);
        foreach (AudioRole role in Enum.GetValues<AudioRole>())
        {
            AudioEndpoint? current = GetDefaultEndpoint(enumerator, flow, role);
            if (current == null)
            {
                continue;
            }

            logs.Add("[AUDIO_GUARD] default " + flow + "/" + role +
                     "=\"" + current.Name + "\" dualsense=" + current.IsDualSense);
            if (!current.IsDualSense)
            {
                continue;
            }

            if (fallback == null)
            {
                logs.Add("[AUDIO_GUARD] " + flow + "/" + role +
                         " is DualSense, but no non-DualSense active fallback endpoint was found.");
                continue;
            }

            int hr = policy.SetDefaultEndpoint(fallback.Id, role);
            if (hr == 0)
            {
                logs.Add("[AUDIO_GUARD] moved default " + flow + "/" + role +
                         " from DualSense to \"" + fallback.Name + "\".");
            }
            else
            {
                logs.Add("[AUDIO_GUARD] failed to move default " + flow + "/" + role +
                         " to \"" + fallback.Name + "\" hr=0x" + hr.ToString("x8") + ".");
            }
        }
    }

    private static AudioEndpoint? ChooseFallback(
        IMMDeviceEnumerator enumerator,
        AudioDataFlow flow,
        IReadOnlyList<AudioEndpoint> active)
    {
        AudioRole[] roles =
        [
            AudioRole.Multimedia,
            AudioRole.Console,
            AudioRole.Communications
        ];

        foreach (AudioRole role in roles)
        {
            AudioEndpoint? endpoint = GetDefaultEndpoint(enumerator, flow, role);
            if (endpoint != null && !endpoint.IsDualSense)
            {
                return endpoint;
            }
        }

        return active.FirstOrDefault(e => !e.IsDualSense);
    }

    private static List<AudioEndpoint> EnumerateActiveEndpoints(
        IMMDeviceEnumerator enumerator,
        AudioDataFlow flow)
    {
        IMMDeviceCollection? collection = null;
        var endpoints = new List<AudioEndpoint>();
        try
        {
            int hr = enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out collection);
            if (hr != 0 || collection == null)
            {
                return endpoints;
            }

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) == 0 && device != null)
                    {
                        endpoints.Add(ReadEndpoint(device, flow));
                    }
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }
        }
        finally
        {
            ReleaseComObject(collection);
        }

        return endpoints;
    }

    private static AudioEndpoint? GetDefaultEndpoint(
        IMMDeviceEnumerator enumerator,
        AudioDataFlow flow,
        AudioRole role)
    {
        IMMDevice? device = null;
        try
        {
            int hr = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
            if (hr != 0 || device == null)
            {
                return null;
            }

            return ReadEndpoint(device, flow);
        }
        finally
        {
            ReleaseComObject(device);
        }
    }

    private static AudioEndpoint ReadEndpoint(IMMDevice device, AudioDataFlow flow)
    {
        string id = GetDeviceId(device);
        string name = GetFriendlyName(device);
        return new AudioEndpoint(id, name, flow, LooksLikeDualSenseEndpoint(id, name));
    }

    private static string GetDeviceId(IMMDevice device)
    {
        IntPtr idPtr = IntPtr.Zero;
        try
        {
            int hr = device.GetId(out idPtr);
            if (hr != 0 || idPtr == IntPtr.Zero)
            {
                return "";
            }

            return Marshal.PtrToStringUni(idPtr) ?? "";
        }
        finally
        {
            if (idPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(idPtr);
            }
        }
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            int hr = device.OpenPropertyStore(StgmRead, out store);
            if (hr != 0 || store == null)
            {
                return "";
            }

            PropertyKey key = DeviceFriendlyNameKey;
            hr = store.GetValue(ref key, out value);
            if (hr != 0)
            {
                return "";
            }

            return value.AsString();
        }
        finally
        {
            PropVariantClear(ref value);
            ReleaseComObject(store);
        }
    }

    private static bool LooksLikeDualSenseEndpoint(string id, string name)
    {
        string joined = (id + " " + name).ToLowerInvariant();
        return joined.Contains("dualsense", StringComparison.Ordinal) ||
               joined.Contains("vid_054c", StringComparison.Ordinal) ||
               joined.Contains("pid_0ce6", StringComparison.Ordinal) ||
               joined.Contains("054c&0ce6", StringComparison.Ordinal) ||
               joined.Contains("054c") && joined.Contains("0ce6") ||
               name.Contains("DualSense Wireless Controller", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("DualSense", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private sealed record AudioEndpoint(
        string Id,
        string Name,
        AudioDataFlow Flow,
        bool IsDualSense);

    private enum AudioDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum AudioRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("bcde0395-e52f-467c-8e3d-c4579291692e")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private sealed class PolicyConfigClientComObject
    {
    }

    [ComImport]
    [Guid("a95664d2-9614-4f35-a746-de8db63617e6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            AudioDataFlow dataFlow,
            int stateMask,
            out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            AudioDataFlow dataFlow,
            AudioRole role,
            out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("d666063f-1587-4e43-81f1-b948e807363f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            int clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(
            int accessMode,
            out IPropertyStore properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            out IntPtr waveFormat);

        [PreserveSig]
        int GetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.Bool)] bool defaultFormat,
            out IntPtr waveFormat);

        [PreserveSig]
        int ResetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName);

        [PreserveSig]
        int SetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            IntPtr waveFormat,
            IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.Bool)] bool defaultPeriod,
            out long defaultPeriodValue,
            out long minimumPeriodValue);

        [PreserveSig]
        int SetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            ref long period);

        [PreserveSig]
        int GetShareMode(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            IntPtr mode);

        [PreserveSig]
        int SetShareMode(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            ref PropertyKey key,
            out PropVariant value);

        [PreserveSig]
        int SetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            ref PropertyKey key,
            ref PropVariant value);

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            AudioRole role);

        [PreserveSig]
        int SetEndpointVisibility(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;

        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        private ushort vt;
        private ushort reserved1;
        private ushort reserved2;
        private ushort reserved3;
        private IntPtr pointerValue;
        private int intValue;

        public string AsString()
        {
            return vt == 31 && pointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(pointerValue) ?? ""
                : "";
        }
    }
}
