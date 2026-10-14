using System;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? builtInAudioGuardCts;

    private void StopBuiltInAudioGuard()
    {
        var previous = Interlocked.Exchange(ref builtInAudioGuardCts, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void StartBuiltInAudioGuard()
    {
        if (previewOnly || shuttingDown) return;
        StopBuiltInAudioGuard();
        var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
        builtInAudioGuardCts = owner;
        CancellationToken token = owner.Token;
        _ = Task.Run(async () =>
        {
            string previous = "";
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                    // Independent of the UI timer and BLE/rumble workers. Never
                    // disable the haptic endpoint; adjust a default only if it is DualSense.
                    string summary = string.Join(Environment.NewLine, AudioEndpointGuard.EnsureDualSenseIsNotDefault());
                    token.ThrowIfCancellationRequested();
                    if (summary != previous) { AppendLog(summary); previous = summary; }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { AppendLog("[AUDIO_GUARD] worker_error=" + ex.GetType().Name); }
            finally
            {
                Interlocked.CompareExchange(ref builtInAudioGuardCts, null, owner);
                owner.Dispose();
            }
        });
    }
}
