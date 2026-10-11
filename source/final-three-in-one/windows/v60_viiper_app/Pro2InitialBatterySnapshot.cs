using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed record Pro2InitialBatterySnapshotResult(
    byte[]? Payload, bool SubscriptionReleased, string Status, string CleanupStatus);

// Runs BEFORE FD2 is enabled and is default-on in pure-software releases. There is no
// gameplay callback, background query, retry timer, or voltage-to-SOC estimate.
public static class Pro2InitialBatterySnapshot
{
    public const string EnableArgument = "--battery-init-snapshot-test";
    public const string DisableArgument = "--battery-snapshot-off-test";

    public static bool IsRequested(string[] arguments) =>
        ResolveRequested(arguments, PureSoftware133Mode.IsReleaseBuild);

    public static bool ResolveRequested(string[] arguments, bool defaultEnabled) =>
        !arguments.Contains(DisableArgument, StringComparer.OrdinalIgnoreCase) &&
        (defaultEnabled || arguments.Contains(EnableArgument, StringComparer.OrdinalIgnoreCase));

    public static bool IsValid(byte[] payload) =>
        payload.Any(value => value != 0) &&
        Pro2BatteryStatus.TryDecodePrimary(payload, out var battery) && battery.HasLevel;

    public static async Task<Pro2InitialBatterySnapshotResult> CaptureAsync(
        Func<CancellationToken, Task<bool>> enable,
        Func<CancellationToken, Task<byte[]>> receive,
        Func<CancellationToken, Task<bool>> disableAndVerify,
        CancellationToken token, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(2);
        byte[]? payload = null;
        string status = "not_started", cleanupStatus = "not_started";
        bool released = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(limit);
        try
        {
            if (await enable(deadline.Token))
            {
                var received = await receive(deadline.Token);
                status = IsValid(received) ? "captured" : "invalid_report";
                if (status == "captured") payload = received;
            }
            else status = "subscribe_rejected";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { status = "timeout"; }
        catch (Exception ex) { status = "error_" + ex.GetType().Name; }
        finally
        {
            // Even a rejected/timed-out enable can leave a remote subscription.
            // Cleanup has its own token so cancellation cannot bypass it.
            using var cleanup = new CancellationTokenSource(limit);
            try
            {
                released = await disableAndVerify(cleanup.Token);
                cleanupStatus = released ? "none_verified" : "none_not_verified";
            }
            catch (Exception ex) { cleanupStatus = "error_" + ex.GetType().Name; }
        }
        return new(payload, released, status, cleanupStatus);
    }
}
