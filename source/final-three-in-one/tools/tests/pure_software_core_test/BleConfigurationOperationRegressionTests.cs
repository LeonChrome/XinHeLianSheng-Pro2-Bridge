using System;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class BleConfigurationOperationRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        DateTimeOffset requested = DateTimeOffset.Parse("2026-10-11T00:00:00Z"), now = requested.AddSeconds(20);
        string Marker(string operation = "apply", string preset = "7p5", bool succeeded = true, bool required = false,
            DateTimeOffset? at = null) => JsonSerializer.Serialize(new
            { operation, preset, restartSucceeded = succeeded, restartRequired = required, completedAt = (at ?? requested.AddSeconds(5)).ToString("O") });
        foreach (string preset in new[] { "7p5", "flexible", "15" })
        {
            var success = BleConfigurationOperationEvidence.Parse(Marker(preset: preset), "apply", preset, requested, now);
            check(success.Known && success.RestartSucceeded && !success.RestartRequired,
                "matching current-operation marker confirms adapter restart independently of 7.5ms configuration " + preset);
            var reboot = BleConfigurationOperationEvidence.Parse(Marker(preset: preset, succeeded: false, required: true), "apply", preset, requested, now);
            check(reboot.Known && !reboot.RestartSucceeded && reboot.RestartRequired,
                "failed restart cannot be treated as ready for auto retest " + preset);
        }
        foreach (string malformed in new[] { "{}", "null", "[]", "not json", new string(' ', 65537),
            Marker(preset: "15"), Marker(operation: "restore"), Marker(at: requested.AddSeconds(-1)),
            Marker(at: now.AddSeconds(1)), Marker(succeeded: true, required: true), Marker(succeeded: false, required: false) })
        {
            var unknown = BleConfigurationOperationEvidence.Parse(malformed, "apply", "7p5", requested, now);
            check(!unknown.Known && !unknown.RestartSucceeded && unknown.RestartRequired,
                "absent/stale/contradictory/unrelated completion never authorizes automatic reconnect");
        }
        var restored = BleConfigurationOperationEvidence.Parse(Marker("restore", "original"), "restore", "original", requested, now);
        check(restored.Known && restored.RestartSucceeded, "restore completion has its own exact operation and preset identity");
    }
}
