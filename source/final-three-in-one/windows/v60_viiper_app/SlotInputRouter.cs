using System;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Stable session endpoint: hot unplug/replug may replace a physical HID source
// without recreating the virtual USB controller or changing other slots.
public sealed class SlotInputRouter : IGamepadInputSource, IGamepadInputRateSource,
    IGamepadInputMetricsSource, IGamepadFreshSequentialInputSource, IGamepadRealtimeInputSource,
    IGamepadSessionInputSource, IGamepadSignaledInputSource, IGamepadRuntimeTelemetrySink, IGamepadOutputSink
{
    private IGamepadInputSource current;
    public SlotInputRouter(IGamepadInputSource source) { current=source; }
    public IGamepadInputSource Current => Volatile.Read(ref current);
    public void Switch(IGamepadInputSource source) => Volatile.Write(ref current,source);
    public bool IsRunning => Current.IsRunning;
    public string Status => Current.Status;
    public double CurrentParsedRateHz => (Current as IGamepadInputRateSource)?.CurrentParsedRateHz ?? 0;
    public string MetricsSummary => (Current as IGamepadInputMetricsSource)?.MetricsSummary ?? "";
    public bool IsOutputReady => Current is IGamepadOutputSink { IsOutputReady:true };
    public bool TryGetLatest(out GamepadState state,out TimeSpan age)=>Current.TryGetLatest(out state,out age);
    public bool TryGetNextFresh(out GamepadState state,out TimeSpan age)
    { if(Current is IGamepadFreshSequentialInputSource source) return source.TryGetNextFresh(out state,out age); state=GamepadState.Neutral(); age=TimeSpan.MaxValue; return false; }
    public bool TryGetNewest(out GamepadState state,out TimeSpan age,out int superseded)
    { if(Current is IGamepadRealtimeInputSource source) return source.TryGetNewest(out state,out age,out superseded); superseded=0; return TryGetNextFresh(out state,out age); }
    public int PrepareForConsumerSession()=>(Current as IGamepadSessionInputSource)?.PrepareForConsumerSession() ?? 0;
    public Task<bool> WaitForInputAsync(TimeSpan timeout,CancellationToken token)=>Current is IGamepadSignaledInputSource source
        ? source.WaitForInputAsync(timeout,token):Task.FromResult(false);
    public void ReportViiperPushRate(double actualHz)=>(Current as IGamepadRuntimeTelemetrySink)?.ReportViiperPushRate(actualHz);
    public bool TryWriteOutputReport(ReadOnlySpan<byte> report,out string error)
    { if(Current is IGamepadOutputSink sink) return sink.TryWriteOutputReport(report,out error); error="实体输入没有震动输出"; return false; }
    public bool TryWriteOutputPacket(Pro2OutputPacket packet,out string error)
    { if(Current is IGamepadOutputSink sink) return sink.TryWriteOutputPacket(packet,out error); error="实体输入没有震动输出"; return false; }
    // The slot owns the physical lifetime, not the virtual session.
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
