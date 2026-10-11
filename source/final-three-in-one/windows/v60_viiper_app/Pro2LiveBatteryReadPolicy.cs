using System;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed record Pro2LiveBatteryReadResult(string Status, int? ProtocolError,
    byte[]? Payload, bool HasLevel, byte Level);

// Pure, offline-testable guard. No retry/fallback/subscription capability.
public sealed class Pro2LiveBatteryReadPolicy
{
    private int attempted;
    public bool Attempted => Volatile.Read(ref attempted) != 0;
    public static bool AcceptBaseline(double fd2Hz, double parsedHz, double p50Ms) =>
        double.IsFinite(fd2Hz) && double.IsFinite(parsedHz) && double.IsFinite(p50Ms) &&
        fd2Hz >= 128 && parsedHz >= 128 && p50Ms is >= 0 and <= 8.5;

    public static Pro2LiveBatteryReadResult Decode(string status, int? error, byte[]? payload)
    {
        if (status != "Success")
            return new(error == 2 ? "read_not_permitted" : "read_" + status, error, null, false, GamepadState.BatteryUnknown);
        if (payload == null || !Pro2InitialBatterySnapshot.IsValid(payload))
            return new("invalid_report", error, payload, false, GamepadState.BatteryUnknown);
        Pro2BatteryStatus.TryDecodePrimary(payload, out var battery);
        return new("captured", error, payload, true, battery.Level);
    }

    public async Task<Pro2LiveBatteryReadResult> ReadOnceAsync(bool baselineAccepted,
        Func<bool> sameLiveConnection, Func<CancellationToken, Task<Pro2LiveBatteryReadResult>> read,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!baselineAccepted) return new("baseline_rejected", null, null, false, GamepadState.BatteryUnknown);
        if (!sameLiveConnection()) return new("connection_changed", null, null, false, GamepadState.BatteryUnknown);
        if (Interlocked.CompareExchange(ref attempted, 1, 0) != 0)
            return new("already_attempted", null, null, false, GamepadState.BatteryUnknown);
        try
        {
            var result = await read(token).WaitAsync(TimeSpan.FromSeconds(8), token);
            return sameLiveConnection() ? result : new("connection_changed", null, null, false, GamepadState.BatteryUnknown);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return new("timeout_os_may_still_complete", null, null, false, GamepadState.BatteryUnknown); }
        catch (Exception ex) { return new("error_" + ex.GetType().Name, null, null, false, GamepadState.BatteryUnknown); }
    }
}
