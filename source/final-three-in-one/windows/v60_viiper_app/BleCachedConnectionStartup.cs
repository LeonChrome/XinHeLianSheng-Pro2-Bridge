using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Only called at initial connection with an exact-environment, fully measured
// cache entry. It never treats a saved success or API Success as current input.
public static class BleCachedConnectionStartup
{
    public static async Task<BleAdaptiveRunResult?> TryAsync(BleAdaptiveCapabilities capabilities,
        IBleAdaptiveTrialBackend backend, BleAdaptiveCachedPreset? cached, CancellationToken token)
    {
        if(!capabilities.Win11 || !capabilities.RequestApi || cached==null ||
            !BleAdaptivePresetPolicy.MeetsTarget(cached.Verified)) return null;
        var candidate=BleAdaptivePresetPolicy.GetCachedHighRateCandidate(cached.Preset);
        if(candidate==null || !backend.Connected) return null;
        bool PeersHealthy()=>backend is not IBleAdaptivePeerGuard guard || guard.PeersHealthy;
        if(!PeersHealthy()) return null;
        bool mayHaveChanged=false, retained=false;
        try
        {
            backend.Report("优先恢复本机已验证的 7.5ms 方案 · 正在连接测速");
            mayHaveChanged=true;
            if(!await backend.RequestAsync(candidate,token)) return null;
            var first=await backend.MeasureAsync("CACHED_START_CHECK",token);
            if(!backend.Connected || !PeersHealthy() || !BleAdaptivePresetPolicy.MeetsTarget(first)) return null;
            backend.Report("已恢复高速输入 · 再复核 10 秒");
            var verify=await backend.MeasureAsync("CACHED_START_VERIFY",token);
            var decision=BleAdaptivePresetPolicy.VerifyFinal(first,verify);
            if(!backend.Connected || !PeersHealthy() || !decision.Retain || !BleAdaptivePresetPolicy.MeetsTarget(verify)) return null;
            retained=true;
            return new(candidate,first,verify,true,"cached_start_target_verified",new List<BleAdaptiveTrialRecord>
            {
                new(candidate.Id,true,first,"cached_start_target_observed"),
                new(candidate.Id+"_VERIFY",true,verify,decision.Code)
            });
        }
        finally
        {
            if(mayHaveChanged&&!retained&&backend.Connected)
            {
                backend.Report("已验证方案本次未通过 · 恢复原请求后继续自动测试");
                try { if(!await backend.RequestAsync(null,CancellationToken.None)) backend.ReleaseTrialRequest(); }
                catch(Exception error) { backend.ReleaseTrialRequest();backend.Report("缓存方案清理："+error.GetType().Name); }
            }
        }
    }
}
