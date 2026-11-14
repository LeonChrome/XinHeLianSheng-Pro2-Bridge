using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class BleCachedStartupRegressionTests
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var legacy=JsonSerializer.Deserialize<V60UserSettings>("{\"AutoOptimizeBle\":false}")!;
        legacy.MigrateAutomaticBlePolicy();
        check(legacy.AutoOptimizeBle&&legacy.BleAutomaticPolicyVersion==1&&legacy.BleAutomaticPolicyMigrated,"legacy disabled setting migrates once to automatic high-rate startup");
        var explicitOff=JsonSerializer.Deserialize<V60UserSettings>("{\"AutoOptimizeBle\":false,\"BleAutomaticPolicyVersion\":1}")!;
        explicitOff.MigrateAutomaticBlePolicy();
        check(!explicitOff.AutoOptimizeBle&&!explicitOff.BleAutomaticPolicyMigrated,"an explicit off choice after migration remains off");
        BleAdaptiveMeasurement Sample(int count=1330)=>new(new(10,count,count,1,true,true,count>1000?8:16,6),0,25);
        var key=new BleAdaptiveEnvironmentKey(26300,"test","pnp","adapter","provider","driver","date");
        var cached=new BleAdaptiveCachedPreset(key,BleAdaptivePreset.Win11Fixed7p5,Sample(660),Sample(),DateTimeOffset.UtcNow);
        var caps=new BleAdaptiveCapabilities(true,true,true);
        var fast=new Fake(Sample(),Sample());
        var result=await BleCachedConnectionStartup.TryAsync(caps,fast,cached,default);
        check(result is {FinalVerified:true,Code:"cached_start_target_verified"}&&fast.Events.SequenceEqual(new[]{"request:win11_fixed_7p5","measure:CACHED_START_CHECK","measure:CACHED_START_VERIFY"}),
            "cached connection requests 7.5ms before slow baseline and requires two current input windows");
        var slow=new Fake(Sample(660));
        check(await BleCachedConnectionStartup.TryAsync(caps,slow,cached,default)==null&&slow.Events.Last()=="request:restore", "failed cached target restores original request for bounded normal fallback");
        var unstable=new Fake(Sample(),Sample(660));
        check(await BleCachedConnectionStartup.TryAsync(caps,unstable,cached,default)==null&&unstable.Events.Last()=="request:restore","a high first window cannot skip failed current verification");
        var rejected=new Fake(){Reject=true};
        check(await BleCachedConnectionStartup.TryAsync(caps,rejected,cached,default)==null&&rejected.Events.All(x=>!x.StartsWith("measure:")),"API rejection cannot claim cached input success");
        var win10=new Fake();
        check(await BleCachedConnectionStartup.TryAsync(new(false,false,false),win10,cached,default)==null&&win10.Events.Count==0,"cached startup never sends unsupported Win10 API requests");
        var partial=new Fake();
        check(await BleCachedConnectionStartup.TryAsync(caps,partial,cached with{Verified=Sample(660)},default)==null&&partial.Events.Count==0,"66Hz cache cannot become a 133Hz startup hint");
        using var cancel=new CancellationTokenSource();var interrupted=new Fake(){Cancel=cancel};
        try{await BleCachedConnectionStartup.TryAsync(caps,interrupted,cached,cancel.Token);check(false,"cancellation expected");}
        catch(OperationCanceledException){check(interrupted.Events.Last()=="request:restore","cancellation after submitting cached preference restores original request");}
        var peer=new Fake(Sample(),Sample()){Healthy=false};
        check(await BleCachedConnectionStartup.TryAsync(caps,peer,cached,default)==null&&peer.Events.Count==0,"unhealthy peer prevents cached request");
    }
    private sealed class Fake(params BleAdaptiveMeasurement[] samples):IBleAdaptiveTrialBackend,IBleAdaptivePeerGuard
    {
        private readonly Queue<BleAdaptiveMeasurement> measurements=new(samples);
        internal List<string> Events=new();internal bool Reject,Healthy=true;internal CancellationTokenSource? Cancel;
        public bool Connected=>true;public bool PeersHealthy=>Healthy;
        public Task<BleAdaptiveMeasurement> MeasureAsync(string phase,CancellationToken token){token.ThrowIfCancellationRequested();Events.Add("measure:"+phase);return Task.FromResult(measurements.Dequeue());}
        public Task<bool> RequestAsync(BleAdaptivePresetCandidate? preset,CancellationToken token){Events.Add("request:"+(preset?.Id??"restore"));if(preset!=null&&Cancel!=null){Cancel.Cancel();token.ThrowIfCancellationRequested();}return Task.FromResult(preset==null||!Reject);}
        public void ReleaseTrialRequest()=>Events.Add("release");public void Report(string message){}
    }
}
