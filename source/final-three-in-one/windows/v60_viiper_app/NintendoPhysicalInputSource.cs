using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed record PhysicalInputSnapshot(bool Running, string Status, double Hz, double AgeMs,
    long Reports, long RejectedReports, long QueueDrops, double GapP50, double GapP95, double GapMax,
    string Calibration, string Identity, string NfcStatus);
public sealed record NintendoNfcTag(byte[] Uid, byte[] Data, string Controller, string Transport, DateTimeOffset ReadAt);
public interface INintendoHidTransport : IAsyncDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token);
    Task WriteAsync(ReadOnlyMemory<byte> report, CancellationToken token);
}

public sealed partial class NintendoPhysicalInputSource : IGamepadInputSource, IGamepadInputMetricsSource,
    IGamepadInputRateSource, IGamepadFreshSequentialInputSource, IGamepadRealtimeInputSource,
    IGamepadSessionInputSource, IGamepadSignaledInputSource, IGamepadOutputSink
{
    private static readonly ConcurrentDictionary<string, byte> Owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly NintendoHidDevice device;
    private readonly NintendoPhysicalParser parser;
    private readonly Action<string> log;
    private readonly object gate = new(), replyGate = new();
    private readonly Queue<GamepadState> states = new();
    private readonly Queue<(long Tick,double Gap)> stamps = new();
    private readonly SemaphoreSlim inputSignal = new(0,1), commandGate = new(1,1), nfcGate = new(1,1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Pro2OutputPacket> outputs = Channel.CreateBounded<Pro2OutputPacket>(new BoundedChannelOptions(8)
        { SingleReader=false, SingleWriter=false, FullMode=BoundedChannelFullMode.DropOldest });
    private readonly Channel<byte[]> mcu = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        { SingleReader=true, SingleWriter=true, FullMode=BoundedChannelFullMode.DropOldest });
    private INintendoHidTransport? transport;
    private INintendoUsbCommandChannel? usbCommands;
    private Task? readTask, outputTask;
    private TaskCompletionSource<byte[]>? reply;
    private Func<byte[],bool>? replyMatcher;
    private GamepadState latest = GamepadState.Neutral();
    private bool ownsDevice, initialized;
    private volatile bool running, nfcBusy;
    private string status = "未连接", nfcStatus = "未读取";
    private long total, rejected, drops, lastTicks, lastMetric;
    private long outputWrites, outputSuperseded, outputFailures;
    private byte[]? previousRaw;
    private uint previousDeviceClock;
    private long repeatedRaw, repeatedClock, clockChanges;
    private string lastGyroResult = "";
    private int sequence;
    private double rumbleGain = 1;
    private long cachedRateTicks;
    private double cachedRate;
    private int disposeStarted;
    private readonly TaskCompletionSource disposeComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public NintendoPhysicalInputSource(NintendoHidDevice device, Action<string>? log = null)
    { this.device=device; parser=new(device.Kind); this.log=log ?? (_=>{}); }
    public NintendoHidDevice Device => device;
    public bool IsRunning => running && !nfcBusy;
    public string Status => status;
    public bool IsOutputReady => running && initialized && !nfcBusy;
    public bool CanReadNfc => running && device.Kind==NintendoInputKind.Pro1Bluetooth && device.InputLength>=362;
    public string CapabilitySummary => device.IsPro1
        ? "Pro1：按键、摇杆、陀螺仪、震动；实体没有背键和 C 键。NFC："+
            (device.Kind==NintendoInputKind.Pro1Bluetooth ? device.InputLength>=362 ? "只读实验，需实卡验证。" : "此 HID 接口未暴露完整 NFC 报告。" : "USB 路线尚未接入，请改用 Pro1 蓝牙读取。")
        : "Pro2 USB：按键、摇杆、背键、C 键、陀螺仪、震动。NFC：协议不同，尚未接入。";
    public string NfcStatus => nfcStatus;
    public bool IsReadingNfc => nfcBusy;
    public double CurrentParsedRateHz
    {
        get
        {
            lock(gate)
            {
                long now=Stopwatch.GetTimestamp();
                if(cachedRateTicks==0 || Stopwatch.GetElapsedTime(cachedRateTicks,now).TotalMilliseconds>=250)
                {
                    cachedRateTicks=now;
                    double span=stamps.Count>1 ? Stopwatch.GetElapsedTime(stamps.Peek().Tick,lastTicks).TotalSeconds : 0;
                    cachedRate=span>0 ? (stamps.Count-1)/span : 0;
                }
                return lastTicks>0 && Stopwatch.GetElapsedTime(lastTicks,now).TotalSeconds<1 ? cachedRate : 0;
            }
        }
    }
    public string MetricsSummary => Format(Snapshot());
    public void SetDeadzones(StickDeadzoneSettings settings) { lock(gate) parser.SetDeadzones(settings); }
    public string StartGyroCalibration() { lock(gate) return parser.StartGyroCalibration(); }
    public string GyroCalibrationSummary { get { lock(gate) return parser.GyroCalibrationSummary; } }
    public void CancelGyroCalibration() { lock(gate) parser.CancelGyroCalibration(); }
    public Pro2StickCalibrationProfile StickCalibrationProfile { get { lock(gate) return parser.StickCalibrationProfile; } }
    public string StickCalibrationSummary { get { lock(gate) return parser.StickCalibrationSummary; } }
    public Pro2PhysicalStickAxes LastPhysicalStickAxes { get { lock(gate) return parser.LastPhysicalStickAxes; } }
    public string SetStickCalibration(Pro2StickCalibrationProfile? profile) { lock(gate) return parser.SetStickCalibration(profile); }
    public string StartStickCalibration(bool range) { lock(gate) return parser.StartStickCalibration(range); }
    public void CancelStickCalibration() { lock(gate) parser.CancelStickCalibration(); }
    public Pro2StickCalibrationResult CompleteStickCalibration(bool range) { lock(gate) return parser.CompleteStickCalibration(range); }
    public void SetRumbleGain(double gain) => Volatile.Write(ref rumbleGain, double.IsFinite(gain) ? Math.Clamp(gain,0,3) : 1);
    public async Task ConnectAsync(CancellationToken token, INintendoHidTransport? suppliedTransport = null,
        INintendoUsbCommandChannel? suppliedCommands = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeStarted)!=0,this);
        if (!Owners.TryAdd(device.PhysicalId,0)) throw new IOException("这只实体手柄已被其他槽位使用。");
        ownsDevice=true;
        try
        {
            transport=suppliedTransport ?? new NintendoHidConnection(device);
            // Never let WPF's synchronization context schedule physical input.
            readTask=Task.Run(()=>ReadLoopAsync(lifetime.Token));
            log($"[PHYSICAL_INPUT_OPEN] kind={device.Kind} id=\"{device.PhysicalId}\" input_length={device.InputLength} output_length={device.OutputLength} virtual_input_excluded=true");
            if (device.IsPro1)
            {
                if (device.Kind==NintendoInputKind.Pro1Usb)
                {
                    foreach (byte command in new byte[]{2,3,2})
                        await ExchangeAsync([0x80,command], p=>p.Length>=2&&p[0]==0x81&&p[1]==command,token);
                    // ForceUSB deliberately has no ACK (same policy as SDL).
                    await transport.WriteAsync(new byte[]{0x80,4},token);
                }
                byte[] sticks=[],imu=[];
                try { sticks=await ReadSpiAsync(0x603d,18,token); imu=await ReadSpiAsync(0x6020,24,token); }
                catch(Exception e) when(e is IOException or TimeoutException) { log("[PRO1_CALIBRATION] fallback="+e.Message); }
                lock(gate) parser.SetPro1FactoryCalibration(sticks,imu);
                await SubcommandAsync(0x40,[1],token);
                await SubcommandAsync(0x41,[3,0,1,1],token);
                await SubcommandAsync(0x48,[1],token);
                await SubcommandAsync(0x03,[0x30],token);
            }
            else
            {
                usbCommands=suppliedCommands ?? Pro2UsbCommands.Open(device,log);
                await Task.Run(()=>Pro2UsbCommands.Initialize(usbCommands,log),token);
            }
            initialized=true;
            long deadline=Stopwatch.GetTimestamp();
            while (Volatile.Read(ref total)<3 && Stopwatch.GetElapsedTime(deadline).TotalSeconds<4)
            { token.ThrowIfCancellationRequested(); if (readTask.IsCompleted) await readTask; await Task.Delay(50,token); }
            if (total<3 || readTask.IsCompleted) throw new IOException("初始化未取得持续有效输入。请退出其他控制器软件，检查 USB 数据线／系统蓝牙配对。");
            running=true; status="已连接 · "+device.Kind;
            outputTask=Task.Run(()=>OutputLoopAsync(lifetime.Token));
            log("[PHYSICAL_INPUT_READY] "+MetricsSummary+" windows_native_transport="+(suppliedTransport==null)+" scope=current_connection_only");
        }
        catch { await DisposeAsync(); throw; }
    }
    private async Task<byte[]> ReadSpiAsync(uint address, byte length, CancellationToken token)
    {
        byte[] data=[(byte)address,(byte)(address>>8),(byte)(address>>16),(byte)(address>>24),length];
        var response=await SubcommandAsync(0x10,data,token);
        if(response.Length<20+length || !response.AsSpan(15,5).SequenceEqual(data)) throw new IOException("Pro1 校准应答地址不符");
        return response[20..(20+length)];
    }
    private async Task<byte[]> SubcommandAsync(byte command, byte[] data, CancellationToken token)
    {
        byte[] packet=new byte[11+data.Length]; packet[0]=1; packet[1]=NextSequence();
        Pro1PhysicalRumble.Neutral.CopyTo(packet,2); packet[10]=command; data.CopyTo(packet,11);
        var response=await ExchangeAsync(packet,p=>p.Length>=15&&p[0]==0x21&&p[14]==command,token);
        if((response[13]&0x80)==0) throw new IOException($"Pro1 命令 {command:X2} 被拒绝：{response[13]:X2}");
        return response;
    }
    private byte NextSequence() => (byte)(Interlocked.Increment(ref sequence)&15);
    private async Task<byte[]> ExchangeAsync(byte[] request, Func<byte[],bool> match, CancellationToken token)
    {
        await commandGate.WaitAsync(token);
        try
        {
            var pending=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(replyGate) { reply=pending; replyMatcher=match; }
            await transport!.WriteAsync(request,token);
            return await pending.Task.WaitAsync(TimeSpan.FromSeconds(2),token);
        }
        finally { lock(replyGate) { reply=null; replyMatcher=null; } commandGate.Release(); }
    }
    private async Task ReadLoopAsync(CancellationToken token)
    {
        try
        {
            byte[] buffer=new byte[Math.Max(device.InputLength,368)];
            while (!token.IsCancellationRequested)
            {
                int length=await transport!.ReadAsync(buffer.AsMemory(0,device.InputLength),token).ConfigureAwait(false);
                if(length==0) throw new IOException("实体 HID 输入已关闭");
                byte[] report=buffer.AsSpan(0,length).ToArray();
                lock(replyGate) if(replyMatcher?.Invoke(report)==true) reply?.TrySetResult(report);
                if(nfcBusy)
                {
                    if(report[0]==0x31) mcu.Writer.TryWrite(report);
                    // NFC command replies may contain repeated button state.
                    // They are never counted as gameplay reports or rate evidence.
                    continue;
                }
                long now=Stopwatch.GetTimestamp();
                lock(gate)
                {
                    if(!parser.TryParse(report,out var state)) { rejected++; continue; }
                    string gyroResult = parser.GyroCalibrationResult;
                    if (gyroResult != lastGyroResult)
                    {
                        lastGyroResult = gyroResult;
                        log("[PHYSICAL_GYRO_CAL] " + parser.GyroCalibrationSummary);
                    }
                    uint deviceClock=device.Kind==NintendoInputKind.Pro2Usb && report.Length>=47
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(43,4)) : report[1];
                    if(previousRaw!=null)
                    {
                        if(report.AsSpan().SequenceEqual(previousRaw)) repeatedRaw++;
                        if(deviceClock==previousDeviceClock) repeatedClock++; else clockChanges++;
                    }
                    previousRaw=report; previousDeviceClock=deviceClock;
                    total++; state.SourceTimestampTicks=now; state.RawNotificationSequence=(ulong)total; state.Updates=(uint)total;
                    double gap=lastTicks==0?0:Stopwatch.GetElapsedTime(lastTicks,now).TotalMilliseconds;
                    lastTicks=now; latest=state; stamps.Enqueue((now,gap));
                    while(stamps.Count>1 && Stopwatch.GetElapsedTime(stamps.Peek().Tick,now).TotalSeconds>5) stamps.Dequeue();
                    if(states.Count>=64) { states.Dequeue(); drops++; } states.Enqueue(state);
                }
                try { inputSignal.Release(); } catch(SemaphoreFullException) { }
                if(lastMetric==0) lastMetric=now;
                if(Stopwatch.GetElapsedTime(lastMetric,now).TotalSeconds>=5)
                { lastMetric=now; log("[PHYSICAL_INPUT_WINDOW] "+MetricsSummary); }
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception e)
        { status="输入断开："+e.Message; log("[PHYSICAL_INPUT_DISCONNECTED] "+device.Kind+" "+e); lock(replyGate) reply?.TrySetException(e); }
        finally { running=false; initialized=false; outputs.Writer.TryComplete(); mcu.Writer.TryComplete(); }
    }
    public PhysicalInputSnapshot Snapshot()
    {
        lock(gate)
        {
            long now=Stopwatch.GetTimestamp(); var samples=stamps.ToArray();
            double seconds=samples.Length>1?Stopwatch.GetElapsedTime(samples[0].Tick,samples[^1].Tick).TotalSeconds:0;
            double age=lastTicks==0?double.PositiveInfinity:Stopwatch.GetElapsedTime(lastTicks,now).TotalMilliseconds;
            double hz=seconds>0 && age<1000?(samples.Length-1)/seconds:0;
            double[] gaps=samples.Skip(1).Select(s=>s.Gap).Order().ToArray();
            double P(double n)=>gaps.Length==0?0:gaps[Math.Clamp((int)Math.Ceiling(gaps.Length*n)-1,0,gaps.Length-1)];
            return new(running,status,hz,age,total,rejected,drops,P(.5),P(.95),P(1),parser.Calibration,device.PhysicalId,nfcStatus);
        }
    }
    private string Format(PhysicalInputSnapshot s)=>FormattableString.Invariant($"kind={device.Kind} parsed_hz={s.Hz:F2} input_reports={s.Reports} non_input_reports={s.RejectedReports} gap_p50_ms={s.GapP50:F2} gap_p95_ms={s.GapP95:F2} gap_max_ms={s.GapMax:F2} queue_drops={s.QueueDrops} age_ms={s.AgeMs:F1} calibration={s.Calibration} count_scope=physical_reports_only imu_samples_not_button_reports=true nfc={s.NfcStatus} adjacent_identical_raw={Interlocked.Read(ref repeatedRaw)} adjacent_identical_device_clock={Interlocked.Read(ref repeatedClock)} device_clock_changes={Interlocked.Read(ref clockChanges)} output_writes={Interlocked.Read(ref outputWrites)} output_superseded={Interlocked.Read(ref outputSuperseded)} output_failures={Interlocked.Read(ref outputFailures)} cross_generation_rumble=parameter_conversion_unvalidated_frequency_proxy native_pro1_unity=byte_passthrough");
    public bool TryGetLatest(out GamepadState state,out TimeSpan age)
    { lock(gate) { state=latest.Clone(); age=lastTicks==0?TimeSpan.MaxValue:Stopwatch.GetElapsedTime(lastTicks); return IsRunning&&total>0; } }
    public bool TryGetNextFresh(out GamepadState state,out TimeSpan age)
    { lock(gate) { if(IsRunning&&states.TryDequeue(out var next)) { state=next; age=Stopwatch.GetElapsedTime(next.SourceTimestampTicks); return true; } state=GamepadState.Neutral(); age=TimeSpan.MaxValue; return false; } }
    public bool TryGetNewest(out GamepadState state,out TimeSpan age,out int superseded)
    { lock(gate) { superseded=Math.Max(0,states.Count-1); while(states.Count>1) states.Dequeue(); return TryGetNextFresh(out state,out age); } }
    public int PrepareForConsumerSession() { lock(gate) { int count=states.Count; states.Clear(); return count; } }
    public async Task<bool> WaitForInputAsync(TimeSpan timeout,CancellationToken token)=>await inputSignal.WaitAsync(timeout,token);
    public bool TryWriteOutputReport(ReadOnlySpan<byte> report,out string error)=>TryWriteOutputPacket(new(report.ToArray(),"physical-bridge",true,null),out error);
    public bool TryWriteOutputPacket(Pro2OutputPacket packet,out string error)
    {
        error="";
        if(!IsOutputReady) { error=nfcBusy?"NFC 正在读取，震动暂停":"实体输入未连接"; return false; }
        if(packet.Report.Length!=64||packet.Report[0]!=2) { error="不支持的震动报告格式"; return false; }
        // The writer is bounded; record locally discarded feedback explicitly.
        while(outputs.Reader.Count>=7 && outputs.Reader.TryRead(out _)) Interlocked.Increment(ref outputSuperseded);
        return outputs.Writer.TryWrite(packet);
    }
    private async Task OutputLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach(var packet in outputs.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if(nfcBusy) continue;
                if(device.IsPro1)
                {
                    double gain=Volatile.Read(ref rumbleGain);
                    byte[] tones;
                    try { tones=packet.NativePro1Rumble is {Length:8} native?Pro1PhysicalRumble.ScaleNative(native,gain):Pro1PhysicalRumble.FromPro2(packet.Report,gain); }
                    catch(NotSupportedException e)
                    {
                        Interlocked.Increment(ref outputFailures);
                        log("[PHYSICAL_OUTPUT_REJECTED] "+e.Message+" action=neutral next_packets=continue");
                        tones=Pro1PhysicalRumble.Neutral;
                    }
                    byte[] report=new byte[10]; report[0]=0x10; report[1]=NextSequence(); tones.CopyTo(report,2);
                    await transport!.WriteAsync(report,token).ConfigureAwait(false);
                }
                else
                {
                    byte[] scaled=new byte[33];
                    if(!Pro2NativeRumbleEncoder.TryEncode(packet.Report,scaled,Volatile.Read(ref rumbleGain),out _,out string error))
                    { Interlocked.Increment(ref outputFailures); log("[PHYSICAL_OUTPUT_REJECTED] "+error); continue; }
                    byte[] report=(byte[])packet.Report.Clone(); scaled.CopyTo(report,0); report[0]=2;
                    await transport!.WriteAsync(report,token).ConfigureAwait(false);
                }
                Interlocked.Increment(ref outputWrites);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception e) { Interlocked.Increment(ref outputFailures); log("[PHYSICAL_OUTPUT_ERROR] "+e); status="输入已连接，震动写入失败："+e.Message; }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposeStarted,1)!=0) { await disposeComplete.Task; return; }
        try
        {
        if(!ownsDevice) return;
        if(initialized && !nfcBusy && transport!=null)
            try
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                byte[] report=device.IsPro1?[0x10,NextSequence(),..Pro1PhysicalRumble.Neutral]:Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"physical-dispose").Report;
                await transport.WriteAsync(report,stop.Token);
            }
            catch(Exception e) { log("[PHYSICAL_STOP] "+e.Message); }
        running=false; initialized=false; status="已断开 · "+device.Kind;
        lock(gate) { parser.CancelStickCalibration(); parser.CancelGyroCalibration(); }
        lifetime.Cancel(); outputs.Writer.TryComplete();
        if(nfcBusy)
        {
            if(await nfcGate.WaitAsync(TimeSpan.FromSeconds(3))) nfcGate.Release();
        }
        if(transport!=null) await transport.DisposeAsync();
        foreach(var task in new[]{readTask,outputTask}) if(task!=null) try { await task; } catch(OperationCanceledException) { }
        usbCommands?.Dispose(); usbCommands=null;
        Owners.TryRemove(device.PhysicalId,out _); ownsDevice=false;
        log("[PHYSICAL_INPUT_CLOSED] id=\""+device.PhysicalId+"\" resource_ownership=released");
        }
        finally
        {
            if(ownsDevice) { Owners.TryRemove(device.PhysicalId,out _); ownsDevice=false; }
            disposeComplete.TrySetResult();
        }
    }
}
