using System;
using System.Text.RegularExpressions;

namespace Y700Switch2V55Manager;

internal sealed record BleInputStatus(
    string TransportState,
    string Schema,
    bool HasMetrics,
    bool InputLive,
    long Updates,
    long AgeMs,
    long RateMilliHz)
{
    public const long FreshAgeLimitMs = 500;

    public bool Connected =>
        string.Equals(TransportState, "connected", StringComparison.OrdinalIgnoreCase);

    // Very old firmware did not expose input freshness metrics. For those builds,
    // a connected BLE transport is the strongest available compatibility signal.
    public bool Ready =>
        Connected &&
        (!HasMetrics ||
         (InputLive && Updates > 0 && AgeMs >= 0 && AgeMs <= FreshAgeLimitMs));
}

internal static class BleInputStatusParser
{
    public static BleInputStatus Parse(string text)
    {
        string transport = ReadString(text, "ble");

        string inputLiveText = ReadBool(text, "input_live");
        long inputUpdates = ReadCounter(text, "input_updates");
        long inputAgeMs = ReadCounter(text, "input_age_ms");
        long inputRateMilliHz = ReadCounter(text, "input_rate_millihz");
        bool hasDualSenseMetrics =
            inputLiveText.Length > 0 ||
            inputUpdates >= 0 ||
            inputAgeMs >= 0 ||
            inputRateMilliHz >= 0;

        if (hasDualSenseMetrics)
        {
            return new BleInputStatus(
                transport,
                "input",
                true,
                string.Equals(inputLiveText, "true", StringComparison.OrdinalIgnoreCase),
                inputUpdates,
                inputAgeMs,
                inputRateMilliHz);
        }

        string liveText = ReadString(text, "live");
        long liveUpdates = ReadCounter(text, "live_updates");
        long liveAgeMs = ReadCounter(text, "live_age_ms");
        long liveRateMilliHz = ReadCounter(text, "ble_input_actual_mhz");
        bool hasPro2Metrics =
            liveText.Length > 0 ||
            liveUpdates >= 0 ||
            liveAgeMs >= 0 ||
            liveRateMilliHz >= 0;

        if (hasPro2Metrics)
        {
            bool live =
                string.Equals(liveText, "active", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(liveText, "live", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(liveText, "true", StringComparison.OrdinalIgnoreCase);
            return new BleInputStatus(
                transport,
                "live",
                true,
                live,
                liveUpdates,
                liveAgeMs,
                liveRateMilliHz);
        }

        return new BleInputStatus(transport, "legacy", false, false, -1, -1, -1);
    }

    private static long ReadCounter(string text, string name)
    {
        MatchCollection matches = Regex.Matches(
            text ?? "",
            "\"" + Regex.Escape(name) + "\"\\s*:\\s*(-?\\d+)",
            RegexOptions.IgnoreCase);
        return matches.Count == 0 ? -1 : long.Parse(matches[^1].Groups[1].Value);
    }

    private static string ReadString(string text, string name)
    {
        MatchCollection matches = Regex.Matches(
            text ?? "",
            "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]*)\"",
            RegexOptions.IgnoreCase);
        return matches.Count == 0 ? "" : matches[^1].Groups[1].Value;
    }

    private static string ReadBool(string text, string name)
    {
        MatchCollection matches = Regex.Matches(
            text ?? "",
            "\"" + Regex.Escape(name) + "\"\\s*:\\s*(true|false)",
            RegexOptions.IgnoreCase);
        return matches.Count == 0 ? "" : matches[^1].Groups[1].Value.ToLowerInvariant();
    }
}
