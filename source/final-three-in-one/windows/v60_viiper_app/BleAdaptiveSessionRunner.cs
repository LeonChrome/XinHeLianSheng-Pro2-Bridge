using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public interface IBleAdaptiveTrialBackend
{
    bool Connected { get; }
    Task<BleAdaptiveMeasurement> MeasureAsync(string phase, CancellationToken token);
    // null means restore/release to the pre-test preference, not proof of interval rollback.
    Task<bool> RequestAsync(BleAdaptivePresetCandidate? candidate, CancellationToken token);
    void ReleaseTrialRequest();
    void Report(string message);
}

public sealed record BleAdaptiveTrialRecord(string Preset, bool RequestAccepted,
    BleAdaptiveMeasurement? Measurement, string Decision);
public sealed record BleAdaptiveRunResult(BleAdaptivePresetCandidate? Selected,
    BleAdaptiveMeasurement Native, BleAdaptiveMeasurement Final, bool FinalVerified,
    string Code, IReadOnlyList<BleAdaptiveTrialRecord> Trials);

/// <summary>
/// Bounded single-connection state machine, independently testable without any radio.
/// A successful request never substitutes for a complete received-input window.
/// </summary>
public static class BleAdaptiveSessionRunner
{
    public static async Task<BleAdaptiveRunResult> RunAsync(BleAdaptiveCapabilities capabilities,
        IBleAdaptiveTrialBackend backend, BleAdaptivePreset? preferred, CancellationToken token)
    {
        backend.Report("正在测量原连接 · 10 秒");
        BleAdaptiveMeasurement native = await backend.MeasureAsync("NATIVE", token);
        var trials = new List<BleAdaptiveTrialRecord>();
        token.ThrowIfCancellationRequested();
        if (!backend.Connected || !native.Sample.Connected)
            return new(null, native, native with { Sample = native.Sample with { Connected = false } },
                false, "link_disconnected", trials);
        if (!BleAdaptivePresetPolicy.IsValidMeasurement(native))
        {
            // Timer rounding, a stale packet or an incomplete first window must
            // not silently turn an empty plan into a completed preset test. One
            // same-connection retry is bounded and sends no parameter requests.
            backend.Report("原连接测量无效 · 同连接补测 10 秒（1/1）；尚未发送预设");
            native = await backend.MeasureAsync("NATIVE_RETRY", token);
            token.ThrowIfCancellationRequested();
            if (!backend.Connected || !native.Sample.Connected)
                return new(null, native, native with { Sample = native.Sample with { Connected = false } },
                    false, "link_disconnected", trials);
            if (!BleAdaptivePresetPolicy.IsValidMeasurement(native))
            {
                backend.Report("未进行参数测试 · 两次原连接测量均无效；未发送预设");
                return new(null, native, native, false, "native_measurement_not_ready", trials);
            }
        }
        BleAdaptivePresetPlan plan = BleAdaptivePresetPolicy.CreatePlan(capabilities, native, preferred);
        if (plan.Candidates.Count == 0)
            return new(null, native, native, BleAdaptivePresetPolicy.IsValidMeasurement(native), plan.Code, trials);

        BleAdaptiveMeasurement best = native;
        BleAdaptiveMeasurement latest = native;
        BleAdaptivePresetCandidate? winner = null;
        bool preferenceChanged = false, finished = false, restoreFailed = false;
        BleAdaptiveRunResult DisconnectedResult() => new(null, native,
            latest with { Sample = latest.Sample with { Connected = false } }, false, "link_disconnected", trials);
        async Task<bool> RestoreBestAsync(CancellationToken restoreToken)
        {
            bool restored = await backend.RequestAsync(winner, restoreToken);
            if (!restored)
            {
                restoreFailed = true;
                winner = null;
                backend.ReleaseTrialRequest();
                backend.Report("此前请求恢复被拒绝，已尝试释放试验参数；清理结果见记录，真实间隔是否恢复需要实测。");
            }
            return restored;
        }
        try
        {
            int index = 0;
            foreach (BleAdaptivePresetCandidate candidate in plan.Candidates)
            {
                token.ThrowIfCancellationRequested();
                if (!backend.Connected) return DisconnectedResult();
                if (++index > BleAdaptivePresetPolicy.MaximumTrials) break;
                backend.Report("正在优化蓝牙 · 方案 " + index + "/" + plan.Candidates.Count + " · " + candidate.DisplayName);
                // RequestAsync may submit then await negotiation. Cancellation in
                // that await still needs restoration, even before it returns.
                preferenceChanged = true;
                bool accepted = await backend.RequestAsync(candidate, token);
                if (!accepted)
                {
                    trials.Add(new(candidate.Id, false, null, "request_rejected"));
                    continue;
                }
                BleAdaptiveMeasurement after = await backend.MeasureAsync(candidate.Id, token);
                latest = after;
                BleAdaptivePresetDecision decision = BleAdaptivePresetPolicy.EvaluateCandidate(native, best, after);
                trials.Add(new(candidate.Id, true, after, decision.Code));
                if (!backend.Connected) return DisconnectedResult();
                if (!decision.Retain)
                {
                    if (!await RestoreBestAsync(token)) break;
                    continue;
                }

                if (BleAdaptivePresetPolicy.MeetsTarget(after))
                {
                    backend.Report("高速方案已出现 · 再复核 10 秒");
                    BleAdaptiveMeasurement verify = await backend.MeasureAsync(candidate.Id + "_VERIFY", token);
                    latest = verify;
                    if (!backend.Connected) return DisconnectedResult();
                    BleAdaptivePresetDecision verified = BleAdaptivePresetPolicy.VerifyFinal(after, verify);
                    trials.Add(new(candidate.Id + "_VERIFY", true, verify, verified.Code));
                    if (verified.Retain && BleAdaptivePresetPolicy.MeetsTarget(verify))
                    {
                        finished = true;
                        return new(candidate, native, verify, true, verified.Code, trials);
                    }
                    // A transient 133Hz window must not stop the remaining arsenal.
                    decision = BleAdaptivePresetPolicy.EvaluateCandidate(native, best, verify);
                    if (!decision.Retain)
                    {
                        if (!backend.Connected || !await RestoreBestAsync(token)) break;
                        continue;
                    }
                    after = verify;
                }
                best = after;
                winner = candidate;
            }

            if (!backend.Connected) return DisconnectedResult();
            backend.Report("方案测试完成 · 正在复核最终连接 10 秒");
            BleAdaptiveMeasurement final = await backend.MeasureAsync("FINAL", token);
            BleAdaptivePresetDecision finalDecision = BleAdaptivePresetPolicy.VerifyFinal(best, final);
            if (!finalDecision.Retain && winner != null && backend.Connected)
            {
                // An unverified winner is not remembered. Restore the original request
                // and measure what actually happens; request disposal alone is not rollback.
                winner = null;
                await RestoreBestAsync(token);
                final = await backend.MeasureAsync("RESTORE_VERIFY", token);
                finalDecision = BleAdaptivePresetPolicy.VerifyFinal(native, final);
            }
            finished = true;
            return new(winner, native, final, !restoreFailed && finalDecision.Retain,
                restoreFailed ? "request_restore_failed" : finalDecision.Code, trials);
        }
        finally
        {
            // Cancellation/timeout must not leave the last (possibly worse) candidate.
            // Closing the physical connection is handled by the existing session owner.
            if (!finished && preferenceChanged && backend.Connected)
            {
                try { await RestoreBestAsync(CancellationToken.None); }
                catch (Exception ex)
                {
                    backend.ReleaseTrialRequest();
                    backend.Report("取消后的请求恢复失败，已尝试释放试验参数；清理结果见记录：" + ex.GetType().Name);
                }
            }
        }
    }
}
