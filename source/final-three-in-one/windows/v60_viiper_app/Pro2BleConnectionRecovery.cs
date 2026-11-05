using System.Diagnostics;

namespace Y700Switch2V60Viiper;

public sealed record Pro2WakeAdvertisement(Pro2ManualCandidate Candidate, long ObservedTicks, bool Connectable)
{
    public bool IsUsable(IReadOnlySet<string> excluded, IReadOnlySet<string> preferred, bool onlyPreferred) =>
        Candidate.IsValid && Connectable && ObservedTicks > 0 &&
        Stopwatch.GetElapsedTime(ObservedTicks) is { TotalSeconds: >= 0 and <= 5 } &&
        !excluded.Contains(Candidate.AddressText) &&
        (!onlyPreferred || preferred.Count == 0 || preferred.Contains(Candidate.AddressText));
}

// Windows may still be completing a connection after our deadline. Keep its
// late result owned and do not queue a second connection on this source.
public sealed class Pro2BleConnectionRecovery
{
    private Task? pendingCleanup;
    public bool IsDraining => pendingCleanup is { IsCompleted: false };

    public async Task<T> AwaitAsync<T>(Task<T> operation, TimeSpan deadline,
        Action<T>? releaseLateResult, CancellationToken token)
    {
        try
        {
            return await operation.WaitAsync(deadline, token);
        }
        catch (TimeoutException)
        {
            pendingCleanup = DrainAsync(operation, releaseLateResult);
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            pendingCleanup = DrainAsync(operation, releaseLateResult);
            throw;
        }
    }

    private static async Task DrainAsync<T>(Task<T> operation, Action<T>? releaseLateResult)
    {
        try
        {
            T result = await operation.ConfigureAwait(false);
            releaseLateResult?.Invoke(result);
        }
        catch (Exception) { /* Observe late failures from an abandoned connection. */ }
    }

    public static bool IsServiceFailure(BleConnectionAttemptEvidence? evidence, string address) =>
        evidence is { Stage: "GATT 服务发现", Outcome: "timeout" or "Unreachable" } &&
        address.Equals(evidence.Address, StringComparison.OrdinalIgnoreCase);
}
