using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

/// <summary>
/// Connection-only discovery acceleration. A saved address must be advertising
/// now; an unrecognised Nintendo manufacturer flag alone never ends a scan.
/// Manual lists and explicit diagnostics do not construct this policy.
/// </summary>
public sealed class Pro2DiscoveryPolicy
{
    private readonly HashSet<string> preferred;
    private readonly HashSet<string> excluded;
    private readonly bool onlyPreferred;
    public static TimeSpan CollectionWindow => TimeSpan.FromMilliseconds(250);

    public static bool AllowsEarlyCompletion(IEnumerable<string> arguments, bool isolatedCustomExperiment)
    {
        if (isolatedCustomExperiment) return false;
        foreach (string argument in arguments)
            if (argument.Equals("--ble-diagnostic", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public Pro2DiscoveryPolicy(IEnumerable<string> preferred, IEnumerable<string> excluded, bool onlyPreferred)
    {
        this.preferred = new(preferred, StringComparer.OrdinalIgnoreCase);
        this.excluded = new(excluded, StringComparer.OrdinalIgnoreCase);
        this.onlyPreferred = onlyPreferred;
    }

    public bool IsFreshSavedTarget(string address) =>
        !excluded.Contains(address) && preferred.Contains(address);

    public string? EarlyCompletionReason(string address, string? name, bool nintendoManufacturer)
    {
        if (excluded.Contains(address)) return null;
        if (preferred.Contains(address)) return "saved_address_advertisement";
        if (onlyPreferred && preferred.Count > 0) return null;
        // Pro/Controller/Switch are intentionally insufficient. A new unnamed
        // Nintendo controller retains the complete original discovery window.
        return nintendoManufacturer && HasExplicitPro2Name(name)
            ? "named_pro2_advertisement" : null;
    }

    private static bool HasExplicitPro2Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (string token in new[] { "pro2", "pro 2" })
        {
            int start = 0;
            while ((start = name.IndexOf(token, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = start + token.Length;
                if ((start == 0 || !char.IsLetterOrDigit(name[start - 1])) &&
                    (end == name.Length || !char.IsLetterOrDigit(name[end]))) return true;
                start = end;
            }
        }
        return false;
    }

    public static async Task<string> WaitForScanCompletionAsync(TimeSpan duration,
        Task<string> earlyCandidate, Task watcherStopped, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task fullWindowWait = Task.Delay(duration, waitCts.Token);
        Task? collectionWait = null;
        try
        {
            Task completed = await Task.WhenAny(fullWindowWait, watcherStopped, earlyCandidate);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed == earlyCandidate)
            {
                string reason = await earlyCandidate;
                collectionWait = Task.Delay(CollectionWindow, waitCts.Token);
                Task collectionCompleted = await Task.WhenAny(collectionWait, fullWindowWait, watcherStopped);
                cancellationToken.ThrowIfCancellationRequested();
                return collectionCompleted == fullWindowWait ? "full_window" :
                    collectionCompleted == watcherStopped ? "watcher_stopped" : reason;
            }
            return completed == watcherStopped ? "watcher_stopped" : "full_window";
        }
        finally
        {
            // Observe and cancel both timers; no background continuation is
            // retained after a short connection scan, stop or cancellation.
            waitCts.Cancel();
            try { await fullWindowWait; } catch (OperationCanceledException) { }
            if (collectionWait != null) { try { await collectionWait; } catch (OperationCanceledException) { } }
        }
    }
}
