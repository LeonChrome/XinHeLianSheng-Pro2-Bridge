using System.Diagnostics;

namespace Y700Switch2V60Viiper;

// A system resume is not permission to restart a radio. Only delay new
// connections while Windows brings its existing Bluetooth interfaces back.
public sealed class BleHostResumeGate
{
    private readonly Func<long> now;
    private long readyAt;
    private int suspended;
    public BleHostResumeGate(Func<long>? timestamp=null)=>now=timestamp??Stopwatch.GetTimestamp;
    public void Suspend()=>Volatile.Write(ref suspended,1);
    public void Resume()
    {
        Interlocked.Exchange(ref readyAt,now()+3*Stopwatch.Frequency);
        Volatile.Write(ref suspended,0);
    }
    public TimeSpan Remaining=>Volatile.Read(ref suspended)!=0?Timeout.InfiniteTimeSpan:
        TimeSpan.FromSeconds(Math.Max(0,Interlocked.Read(ref readyAt)-now())/(double)Stopwatch.Frequency);
    public async Task WaitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while(Remaining is var delay&&delay!=TimeSpan.Zero)
            await Task.Delay(delay==Timeout.InfiniteTimeSpan?TimeSpan.FromMilliseconds(250):
                TimeSpan.FromMilliseconds(Math.Min(250,delay.TotalMilliseconds)),token);
    }
}
