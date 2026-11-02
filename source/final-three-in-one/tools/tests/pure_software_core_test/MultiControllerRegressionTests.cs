using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class MultiControllerRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string output)
    {
        Pro2SlotAddress S(int i, string saved, string connected = "", bool occupied = false, bool enabled = true) => new(i, enabled, saved, connected, occupied);
        check(V60UserSettings.NormalizeEnabledSlots(null).SequenceEqual(new[] {true,false,false,false}), "old settings keep single-controller default");
        check(V60UserSettings.NormalizeEnabledSlots(new[] {false,true,true,true,true}).SequenceEqual(new[] {true,true,true,true}), "persisted slots bounded and primary retained");
        var savedSettings = JsonSerializer.Deserialize<V60UserSettings>(JsonSerializer.Serialize(new V60UserSettings{EnabledPro2Slots=[true,true,false,false]}))!;
        check(savedSettings.EnabledPro2Slots.SequenceEqual(new[] {true,true,false,false}), "extra enabled controller persists independently from addresses");
        var slots = new[] { S(1, "AA", "AA", true), S(2, "BB"), S(3, "CC"), S(4, "DD") };
        check(Pro2MultiControllerPolicy.Exclusions(2, slots).SetEquals(new[] { "AA", "CC", "DD" }), "slot 2 excludes live and reserved peers");
        check(Pro2MultiControllerPolicy.Exclusions(1, new[] { S(1,"AA"), S(2,"AA") }).Count == 0 &&
            Pro2MultiControllerPolicy.Exclusions(2, new[] { S(1,"AA"), S(2,"AA") }).Contains("AA"), "duplicate saved addresses assign priority without mutual deadlock");
        check(Pro2MultiControllerPolicy.Exclusions(1, new[] { S(1,""), S(2,"BB", "BB", true, false) }).Contains("BB"), "transport occupied even when disabled");
        check(Pro2MultiControllerPolicy.Exclusions(1, new[] { S(1,""), S(2,"BB", enabled:false) }).Count == 0, "removed slot releases offline reservation");
        // Reproduce simultaneous queued attempts: take the snapshot after locking.
        for (int run=0; run<20; run++)
        {
            using var gate = new SemaphoreSlim(1);
            var connected = new string[4];
            await Task.WhenAll(Enumerable.Range(0,4).Select(async i =>
            {
                await gate.WaitAsync();
                try
                {
                    var exclude=Pro2MultiControllerPolicy.Exclusions(i+1, Enumerable.Range(0,4).Select(j=>S(j+1,"",connected[j]??"",connected[j]!=null)));
                    connected[i]=new[]{"A","B","C","D"}.First(a=>!exclude.Contains(a));
                    await Task.Yield();
                }
                finally {gate.Release();}
            }));
            check(connected.Distinct().Count()==4, "four queued connections never choose the same address " + run);
        }
        var before = new[] { new BlePeerRate(1,"A",true,true,133) };
        check(BlePeerRateGuard.Healthy(before,[new(1,"A",true,true,130)]), "normal peer rate noise retained");
        check(!BlePeerRateGuard.Healthy(before,[new(1,"A",true,true,66)]) && !BlePeerRateGuard.Healthy(before,[]), "peer regression/disconnect blocks keeping local fast preset");
        foreach(bool rejectedRestore in new[]{false,true})
        {
            var backend=new PeerBackend(rejectedRestore);
            var r=await BleAdaptiveSessionRunner.RunAsync(new(true,true,true),backend,null,CancellationToken.None);
            check(r.Selected==null&&!r.FinalVerified&&r.Code.StartsWith("peer_rate_regression")&&backend.Requests.SequenceEqual(new[]{"win11_fixed_7p5","native"}), "peer regression restores original and stops remaining presets");
            check(backend.Released==rejectedRestore, "rejected peer rollback is explicitly released");
        }
        var serials=new HashSet<string>(); var macs=new HashSet<string>();
        foreach(int i in Enumerable.Range(1,4))
        {
            var p=ViiperDeviceProfile.DualSenseLike with {DeviceSpecificSerialNumber=ViiperDeviceProfile.SlotSerialNumber(ViiperVirtualMode.DualSenseLike,i)};
            check(serials.Add(p.DeviceSpecificSerialNumber)&&macs.Add((string)p.DeviceSpecificArguments()["mac_address"]!), "PS5 slot serial and protocol identity unique "+i);
            check(p.DeviceSpecificArguments()["mac_address"]!.Equals(p.DeviceSpecificArguments()["mac_address"]), "identity stable across recreation "+i);
        }
        foreach(var profile in new[]{ViiperDeviceProfile.Pro2,ViiperDeviceProfile.Pro1,ViiperDeviceProfile.Xbox,ViiperDeviceProfile.DualSenseLike})
            await FourStreamsAsync(profile with {SourcePaced=true},check);
        var modes = Pro2SlotModes.Options.Select(o => o.Mode).ToArray();
        for (int rotation = 0; rotation < 4; rotation++)
            await ProfilesAsync(Enumerable.Range(0, 4).Select(i => Pro2SlotModes.Profile(modes[(i + rotation) % 4],
                i + 1, ViiperPushRateOption.Default)).ToArray(), check);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"multi-controller-tests.txt"),"Offline: four concurrent real C# input consumers in each homogeneous mode and four mixed rotations; all four feedback protocols isolated and payload checked. No Bluetooth/USBIP hardware proof. Address ownership, mode replacement and peer-rate rollback tested.\n");
    }
    private sealed class PeerBackend(bool rejectRestore):IBleAdaptiveTrialBackend,IBleAdaptivePeerGuard
    {
        public bool Connected=>true; public bool PeersHealthy=>Requests.Count==0||Requests.Last()=="native"&&!rejectRestore;
        public List<string> Requests=new(); public bool Released;
        public Task<bool> RequestAsync(BleAdaptivePresetCandidate? p,CancellationToken t){Requests.Add(p?.Id??"native");return Task.FromResult(p!=null||!rejectRestore);}
        public void ReleaseTrialRequest()=>Released=true; public void Report(string m){}
        public Task<BleAdaptiveMeasurement> MeasureAsync(string phase,CancellationToken t)=>Task.FromResult(new BleAdaptiveMeasurement(new(10,phase=="NATIVE"?660:1330,phase=="NATIVE"?660:1330,1,true,true,8,6)));
    }
    private static async Task FourStreamsAsync(ViiperDeviceProfile profile,Action<bool,string> check)
        => await ProfilesAsync(Enumerable.Repeat(profile, 4).ToArray(), check);
    private static async Task ProfilesAsync(ViiperDeviceProfile[] profiles,Action<bool,string> check)
    {
        using var stop=new CancellationTokenSource();
        var sessions=new List<ViiperBridgeSession>();var clients=new List<TcpClient>();var hosts=new List<TcpClient>();
        var sources=new List<Source>();var loops=new List<Task>();
        try
        {
            for(int i=0;i<4;i++)
            {
                var profile = profiles[i];
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                var client=new TcpClient{NoDelay=true};var accept=listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
                var host=await accept;host.NoDelay=true;listener.Stop();clients.Add(client);hosts.Add(host);
                var source=new Source();sources.Add(source);
                var session=new ViiperBridgeSession(new("127.0.0.1",1),profile,new Quiet(),source,source);
                sessions.Add(session);
                typeof(ViiperBridgeSession).GetMethod("SetStream",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(session,[new ViiperDeviceStream(client,client.GetStream())]);
                source.Feed(new());
                loops.Add((Task)typeof(ViiperBridgeSession).GetMethod("InputLoopAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(session,[stop.Token])!);
                loops.Add((Task)typeof(ViiperBridgeSession).GetMethod("FeedbackLoopAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(session,[stop.Token])!);
            }
            async Task Expect(int i,GamepadState s)
            {
                var profile = profiles[i];
                var bytes=new byte[profile.InputSize];using var deadline=new CancellationTokenSource(2000);
                await hosts[i].GetStream().ReadExactlyAsync(bytes,deadline.Token);
                check(bytes.SequenceEqual(VirtualPadPackets.FromGamepad(profile,s)),"isolated actual stream "+profile.Mode+" slot="+(i+1));
            }
            for(int i=0;i<4;i++)await Expect(i,new());
            for(int frame=0;frame<120;frame++)
            {
                var states=Enumerable.Range(0,4).Select(i=>new GamepadState{Buttons=(GamepadButtons)(1UL<<i),Lx=(ushort)(500+i*700+frame),GyroValid=true,GyroX=(short)(i*100+frame),Updates=(uint)frame}).ToArray();
                for(int i=0;i<4;i++)sources[i].Feed(states[i]);
                for(int i=0;i<4;i++)await Expect(i,states[i]);
            }
            for(int i=0;i<4;i++)
            {
                var profile = profiles[i]; var feedback = Feedback(profile, i);
                Pro2OutputPacket expected;
                bool mapped = profile.UsesDualSenseHaptics
                    ? new DualSenseHapticRumbleScheduler().TryProcess(feedback, out expected!, out _, out _)
                    : Pro2OutputPacketMapper.TryMapFeedback(profile, feedback, out expected!, out _);
                check(mapped, "test feedback is valid for " + profile.Mode);
                await hosts[i].GetStream().WriteAsync(feedback);
                using var wait=new CancellationTokenSource(2000);
                while(sources[i].Writes.IsEmpty)await Task.Delay(1,wait.Token);
                check(sources.Select(s=>s.Writes.Count).SequenceEqual(Enumerable.Range(0,4).Select(j=>j<=i?1:0)),"feedback only reaches matching slot "+(i+1));
                check(sources[i].Writes.Single().SequenceEqual(expected.Report), "mode-specific feedback payload reaches only its own physical sink " + profile.Mode);
            }
            sources[1].Running=false;sources[1].Wake();await Expect(1,new());
            for(int i=0;i<4;i++)if(i!=1){var s=new GamepadState{Buttons=GamepadButtons.North,Lx=(ushort)(2000+i)};sources[i].Feed(s);await Expect(i,s);}
            check(!loops.Any(t=>t.IsFaulted),"one source disconnect does not stop sibling streams "+string.Join(",",profiles.Select(p=>p.Mode)));
        }
        finally
        {
            stop.Cancel();try{await Task.WhenAll(loops);}catch(OperationCanceledException){}
            foreach(var session in sessions)await session.DisposeAsync();
            foreach(var c in clients.Concat(hosts))c.Dispose();
        }
    }
    private static byte[] Feedback(ViiperDeviceProfile profile, int slot)
    {
        byte weak = (byte)(30 + slot * 20), strong = (byte)(180 - slot * 10);
        if (profile.Mode == ViiperVirtualMode.Xbox) return [strong, weak];
        if (profile.Mode == ViiperVirtualMode.Pro1) return [0, 0xC8, 0x40, (byte)(0x70 + slot), 0, 1, 0x40, 0x40, 1, 0];
        if (profile.Mode == ViiperVirtualMode.Pro2)
        {
            byte[] frame = new byte[34]; var packet = Pro2OutputPacketMapper.BuildOrdinaryPacket(weak, strong, "test");
            packet.Report.AsSpan(1, 32).CopyTo(frame); frame[32] = 1; return frame;
        }
        byte[] wire = new byte[DualSenseHapticFrame.WireSize]; wire[0] = 1; wire[1] = 48;
        wire[4] = 2; wire[5] = 3; wire[7] = weak; wire[8] = strong; return wire;
    }
    private sealed class Quiet:IProgress<string>{public void Report(string s){}}
    private sealed class Source:IGamepadInputSource,IGamepadRealtimeInputSource,IGamepadSignaledInputSource,IGamepadOutputSink
    {
        private readonly object gate=new();private readonly InputFrameSignal signal=new();private GamepadState? latest;private long ticks;private bool pending;private uint updates;
        public volatile bool Running=true;public bool IsRunning=>Running;public string Status=>"offline test source";
        public ConcurrentQueue<byte[]> Writes=new();public bool IsOutputReady=>true;
        public bool TryWriteOutputReport(ReadOnlySpan<byte> b,out string error){Writes.Enqueue(b.ToArray());error="";return true;}
        public void Feed(GamepadState s){lock(gate){latest=s.Clone();latest.Updates=++updates;ticks=Stopwatch.GetTimestamp();pending=true;}signal.Notify();}
        public void Wake()=>signal.Notify();
        public bool TryGetLatest(out GamepadState state,out TimeSpan age){lock(gate){state=latest??new();age=latest==null?TimeSpan.MaxValue:Stopwatch.GetElapsedTime(ticks);return latest!=null;}}
        public bool TryGetNewest(out GamepadState state,out TimeSpan age,out int superseded){lock(gate){TryGetLatest(out state,out age);superseded=0;bool result=pending;pending=false;return result;}}
        public Task<bool> WaitForInputAsync(TimeSpan timeout,CancellationToken t)=>signal.WaitAsync(timeout,t);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
