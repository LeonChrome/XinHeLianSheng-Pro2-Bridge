using System;
using System.Collections.Generic;
using System.Globalization;

namespace Y700Switch2V60Viiper;

/// <summary>Read-only diagnostic targeting. Never infer device identity from an arbitrary MAC.</summary>
public static class BleDiagnosticTargetResolver
{
    public static string Resolve(string? explicitAddress, IEnumerable<string?> saved)
    {
        if (!string.IsNullOrWhiteSpace(explicitAddress))
        {
            if (!TryParsePublicAddress(explicitAddress, out _))
                throw new ArgumentException("Invalid --preferred-address; expected a six-byte Bluetooth address.");
            return V60UserSettings.NormalizeBleAddress(explicitAddress);
        }
        foreach (string? address in saved)
            if (TryParsePublicAddress(address, out _)) return V60UserSettings.NormalizeBleAddress(address);
        return "";
    }

    public static bool TryParsePublicAddress(string? text, out ulong address)
    {
        address = 0;
        string normalized = V60UserSettings.NormalizeBleAddress(text);
        if (normalized.Length != 17) return false;
        for (int i = 0; i < normalized.Length; i++)
        {
            if (i % 3 == 2) { if (normalized[i] != ':') return false; }
            else if (!Uri.IsHexDigit(normalized[i])) return false;
        }
        return ulong.TryParse(normalized.Replace(":", ""), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address) &&
            address != 0 && address != 0xFFFFFFFFFFFF;
    }

    public static bool CanInitializeUnadvertisedTarget(bool exactPro2Channels) => exactPro2Channels;
}

