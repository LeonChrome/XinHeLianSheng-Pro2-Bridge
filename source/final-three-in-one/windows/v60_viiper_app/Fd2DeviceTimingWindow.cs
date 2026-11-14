using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace Y700Switch2V60Viiper;

public sealed record Fd2DeviceTimingSummary(int ObservedPackets,int StoredPackets,int MalformedPackets,int ValidPairs,
    int ReorderedPackets,int RepeatedMotionTimestamps,int MotionClockDiscontinuities,bool SamplesTruncated,
    double? DeviceDtP50Ms,double? DeviceDtP95Ms,double? HostMinusDeviceJitterMaxMs,
    long MotionTimestampAdvanceRaw,IReadOnlyDictionary<string,int> CounterSteps)
{
    public string ClockScale=>"assumed_1000_motion_ticks_per_ms";
    public string Scope=>"adjacent_observed_fd2_not_generation_or_air_timestamps";
    public string Attribution=>"not_determined_by_timestamp_deltas";
    private static string F(double? value)=>value?.ToString("F3",CultureInfo.InvariantCulture)??"unavailable";
    public string ToLogFields()=>"device_dt_p50_ms="+F(DeviceDtP50Ms)+" device_dt_p95_ms="+F(DeviceDtP95Ms)+
        " host_minus_device_jitter_max_ms="+F(HostMinusDeviceJitterMaxMs)+" device_timing_pairs="+ValidPairs+
        " device_timing_packets="+ObservedPackets+" device_timing_malformed="+MalformedPackets+
        " device_timing_reordered="+ReorderedPackets+" device_timing_repeated_motion="+RepeatedMotionTimestamps+
        " device_timing_clock_discontinuities="+MotionClockDiscontinuities+" device_timing_truncated="+SamplesTruncated+
        " device_clock_scale="+ClockScale+" device_timing_scope="+Scope+" device_timing_attribution="+Attribution+
        " device_motion_advance_raw="+MotionTimestampAdvanceRaw+
        " device_counter_steps_raw="+string.Join(",",CounterSteps.Select(pair=>pair.Key+":"+pair.Value));
}

// Allocated only for explicit measurement windows. No allocation, sorting,
// formatting, disk I/O or extra GATT on the notify path. Caller owns the gate.
public sealed class Fd2DeviceTimingWindow
{
    public const int Capacity=8192;
    private readonly Packet[] packets=new Packet[Capacity];
    private int observed,stored,malformed;
    private readonly record struct Packet(long HostTicks,uint Motion,uint Counter,int Order);

    public void Record(ReadOnlySpan<byte> data,long hostTicks)
    {
        observed++;
        if(data.Length!=63){malformed++;return;}
        if(stored>=Capacity)return;
        packets[stored]=new(hostTicks,BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(42,4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data),stored);
        stored++;
    }

    // Called after detaching the capture from the source gate. Sorting and
    // percentiles cannot block ongoing input delivery under that gate.
    public Fd2DeviceTimingSummary Snapshot()
    {
        Packet[] ordered=packets.AsSpan(0,stored).ToArray();
        Array.Sort(ordered,(a,b)=>a.HostTicks!=b.HostTicks?a.HostTicks.CompareTo(b.HostTicks):a.Order.CompareTo(b.Order));
        int reordered=0,repeated=0,discontinuities=0,pairs=0;
        long advance=0;
        double maxJitter=0;
        var deltas=new List<double>(Math.Max(0,stored-1));
        var counterSteps=new int[34];
        for(int i=0;i<ordered.Length;i++)
        {
            if(ordered[i].Order!=i)reordered++;
            if(i==0)continue;
            Packet before=ordered[i-1],after=ordered[i];
            uint counter=unchecked(after.Counter-before.Counter);
            counterSteps[(int)Math.Min(counter,33)]++;
            uint motion=unchecked(after.Motion-before.Motion);
            if(motion==0){repeated++;continue;}
            // A small unsigned delta handles uint wrap; a reset/backwards jump
            // has no meaningful elapsed time and must not become a huge gap.
            if(motion>int.MaxValue){discontinuities++;continue;}
            double deviceMs=motion/1000.0;
            double hostMs=(after.HostTicks-before.HostTicks)*1000.0/Stopwatch.Frequency;
            deltas.Add(deviceMs);pairs++;advance+=motion;
            maxJitter=Math.Max(maxJitter,Math.Abs(hostMs-deviceMs));
        }
        deltas.Sort();
        double? P(double q)=>deltas.Count==0?null:deltas[Math.Clamp((int)Math.Ceiling(deltas.Count*q)-1,0,deltas.Count-1)];
        var distribution=new Dictionary<string,int>();
        for(int i=0;i<counterSteps.Length;i++)if(counterSteps[i]>0)
            distribution[i==33?"33+":i.ToString(CultureInfo.InvariantCulture)]=counterSteps[i];
        return new(observed,stored,malformed,pairs,reordered,repeated,discontinuities,observed-malformed>stored,
            P(.5),P(.95),pairs==0?null:maxJitter,advance,distribution);
    }
}
