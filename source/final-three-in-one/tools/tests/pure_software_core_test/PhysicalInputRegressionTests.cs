using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.IO;
using System.Threading.Channels;
using Y700Switch2V60Viiper;

internal static class PhysicalInputRegressionTests
{
    public static async Task RunAsync(Action<bool,string> check,string output)
    {
        var nativeGuid=new Guid("6F13725E-EF0E-4FD3-AE5F-B2DE989EC825");
        check(NintendoHidDevice.ParseInterfaceGuids(null,nativeGuid.ToString("B")).SequenceEqual(new[]{nativeGuid}),
            "WinUSB MS OS descriptor singular REG_SZ interface GUID is discovered");
        check(NintendoHidDevice.ParseInterfaceGuids(new[]{"invalid",nativeGuid.ToString()},nativeGuid.ToString("B")).SequenceEqual(new[]{nativeGuid}),
            "plural multi-string and singular GUIDs are validated and deduplicated");
        check(NintendoHidDevice.IsPhysicalChain([@"HID\VID_057E&PID_2069",@"USB\VID_057E&PID_2069\A",@"USB\ROOT_HUB30\A",@"PCI\X"]),"physical USB ancestry accepted");
        check(NintendoHidDevice.IsPhysicalChain([@"HID\NINTENDO",@"BTHENUM\DEV_1234",@"USB\VID_0BDA"]),"classic Bluetooth HID accepted");
        check(!NintendoHidDevice.IsPhysicalChain([@"HID\VID_057E&PID_2009",@"USBIP_VHCI\X",@"USB\ROOT_HUB30\A"]),"virtual Nintendo PID never becomes input");
        check(!NintendoHidDevice.IsPhysicalChain([@"HID\VID_057E&PID_2069",@"ROOT\VIIPER"]),"virtual roots rejected");
        foreach(var kind in Enum.GetValues<NintendoInputKind>())
        {
            var parser=new NintendoPhysicalParser(kind); parser.SetDeadzones(new(0,0));
            check(!parser.TryParse(new byte[8],out _)&&!parser.TryParse(Ack(3),out _),"ACKs and short reports are not gameplay "+kind);
            for(int bit=0;bit<4;bit++)
            {
                byte[] report=Input(kind); report[kind==NintendoInputKind.Pro2Usb?5:3]=(byte)(1<<bit);
                check(parser.TryParse(report,out var state),"physical state accepted "+kind);
                GamepadButtons expected=bit switch {0=>GamepadButtons.West,1=>GamepadButtons.North,2=>GamepadButtons.South,_=>GamepadButtons.East};
                check(state.Buttons==expected,"position-based Nintendo face buttons "+kind+" "+bit);
                check(!state.IsPressed(GamepadButtons.PaddleLeft|GamepadButtons.PaddleRight|GamepadButtons.Aux),"no invented physical back/C keys "+kind);
                check(state.Lx==2048&&state.Ly==2048&&state.Rx==2048&&state.Ry==2048,"neutral axes share canonical center "+kind);
                foreach(var mode in Pro2SlotModes.Options)
                {
                    var profile=Pro2SlotModes.Profile(mode.Mode,1,ViiperPushRateOption.Default);
                    check(VirtualPadPackets.FromGamepad(profile,state).Length==profile.InputSize,"all virtual modes accept physical parser "+kind+" "+mode.Mode);
                }
            }
            byte[] up=Input(kind); Pack(up,kind==NintendoInputKind.Pro2Usb?11:6,2048,3600);
            check(parser.TryParse(up,out var upState)&&upState.Ly>3900,"physical stick up stays canonical up "+kind);
        }
        // Factory calibration must change center/range without inventing input frames.
        var calibrated=new NintendoPhysicalParser(NintendoInputKind.Pro1Bluetooth); calibrated.SetDeadzones(new(0,0));
        byte[] sticks=new byte[18]; Pack(sticks,0,1400,1500); Pack(sticks,3,2000,2100); Pack(sticks,6,1300,1400);
        Pack(sticks,9,2200,1900); Pack(sticks,12,1500,1400); Pack(sticks,15,1300,1600);
        byte[] imu=new byte[24]; for(int i=0;i<3;i++){BinaryPrimitives.WriteInt16LittleEndian(imu.AsSpan(6+i*2),16384);BinaryPrimitives.WriteInt16LittleEndian(imu.AsSpan(18+i*2),15336);}
        calibrated.SetPro1FactoryCalibration(sticks,imu);
        byte[] center=Input(NintendoInputKind.Pro1Bluetooth);Pack(center,6,2000,2100);Pack(center,9,2200,1900);
        check(calibrated.TryParse(center,out var cs)&&cs.Lx==2048&&cs.Ly==2048&&cs.Rx==2048&&cs.Ry==2048,"factory stick centers applied independently");
        check(cs.AccelValid&&cs.GyroValid&&cs.GyroX==0&&cs.AccelZ==4096,"factory IMU scaling uses canonical units");
        byte[] native=[0,0xc8,0x40,0x72,0,1,0x40,0x40];
        check(Pro1PhysicalRumble.ScaleNative(native,1).SequenceEqual(native),"Pro1 native HD rumble is byte-exact at unity");
        var muted=Pro1PhysicalRumble.ScaleNative(native,0);
        check(Switch1RumbleConverter.Decode(muted).HighAmplitude==0&&Switch1RumbleConverter.Decode(muted).LowAmplitude==0,"native Pro1 mute silences both bands");
        for(int i=0;i<101;i++)
        {
            byte[] sample=Pro1PhysicalRumble.Encode(160,i/100.0,320,i/100.0);
            var decoded=Switch1RumbleConverter.Decode(sample);
            check(Math.Abs(decoded.LowHz-160)<=3&&Math.Abs(decoded.HighHz-320)<=6,"Pro1 frequency quantization");
            check(Math.Abs(decoded.LowAmplitude-i*290)<=1600,"Pro1 amplitude quantization bounded");
        }
        check(Pro1NfcProtocol.Crc8("123456789"u8)==0xf4,"MCU CRC8 protocol check vector");
        byte[] unknown=Pro2OutputPacketMapper.BuildOrdinaryPacket(100,100,"test").Report; unknown[1]=0x10;
        try { Pro1PhysicalRumble.FromPro2(unknown); check(false,"opaque rumble must not be decoded as tones"); }
        catch(NotSupportedException) { check(true,"unknown Pro2 rumble is rejected before Pro1 encoding"); }
        byte[] request=Pro1NfcProtocol.Request(1,0,[0,0,0,0x2c,1]);
        check(request.Length==38&&request[36]==Pro1NfcProtocol.Crc8(request.AsSpan(0,36)),"NFC output CRC covers fixed MCU request");
        foreach(int length in new[]{362,368})
        {
            byte[] response=Mcu(0x2a,9,length); response[64]=7; new byte[]{4,1,2,3,4,5,6}.CopyTo(response,65);
            check(Pro1NfcProtocol.TryGetMcu(response,out byte type,out var data)&&Pro1NfcProtocol.TryGetTag(type,data,out var uid)&&uid.Length==7,"NFC handles native and padded transport lengths "+length);
        }
        await SourceLifecycleAsync(check);
        await NfcAsync(check,false);
        await NfcAsync(check,true);
        await NfcRestoreFailureAsync(check);
        foreach(var kind in Enum.GetValues<NintendoInputKind>())
            foreach(var mode in Pro2SlotModes.Options) await VirtualRoundTripAsync(kind,mode.Mode,check);
        File.WriteAllText(Path.Combine(output,"physical-input-validation.txt"),"Offline protocol replay, real production input/feedback loops, cancellation and ownership checks passed.\nPhysical Pro1/USB/NFC hardware not used. No driver, pairing or radio changes.\n");
    }
    private static NintendoHidDevice Device(NintendoInputKind kind,string id)=>new("fixture",id,id,kind,kind==NintendoInputKind.Pro2Usb?64:368,64,"offline replay");
    private static async Task SourceLifecycleAsync(Action<bool,string> check)
    {
        foreach(var kind in Enum.GetValues<NintendoInputKind>())
        {
            var io=new FakeHid(kind);
            await using var source=new NintendoPhysicalInputSource(Device(kind,"ownership-"+kind));
            using var deadline=new CancellationTokenSource(8000);
            await source.ConnectAsync(deadline.Token,io,kind==NintendoInputKind.Pro2Usb?new FakeUsb(io):null);
            check(source.IsRunning&&source.Snapshot().Reports==3,"successful init requires real parsed reports "+kind);
            long before=source.Snapshot().Reports;io.Feed(Ack(3));await Task.Delay(20);
            check(source.Snapshot().Reports==before,"subcommand ACK excluded from measured input "+kind);
            source.PrepareForConsumerSession();check(!source.TryGetNextFresh(out _,out _),"no stale backlog after output restart "+kind);
            for(int i=0;i<160;i++){byte[] report=Input(kind);report[1]=(byte)i;io.Feed(report);}
            await Until(()=>source.Snapshot().Reports>=before+160,deadline.Token);
            check(source.Snapshot().QueueDrops>0,"physical input queue is bounded "+kind);
            check(source.TryGetNewest(out var state,out _,out int superseded)&&superseded==63&&state.RawNotificationSequence==(ulong)(before+160),"newest fresh report selected "+kind);
            await using var duplicate=new NintendoPhysicalInputSource(Device(kind,"ownership-"+kind));
            try{await duplicate.ConnectAsync(deadline.Token,new FakeHid(kind));check(false,"duplicate physical claim rejected");}catch(IOException){check(true,"physical ownership prevents two slots sharing one handle");}
            check(source.TryWriteOutputPacket(Pro2OutputPacketMapper.BuildOrdinaryPacket(100,150,"test"),out _),"physical feedback queued "+kind);
            await Until(()=>io.Writes.Any(p=>p[0]==(kind==NintendoInputKind.Pro2Usb?2:0x10)),deadline.Token);
            check(io.Writes.Any(p=>p[0]==(kind==NintendoInputKind.Pro2Usb?2:0x10)),"mode feedback encoded for physical transport "+kind);
            await Task.WhenAll(source.DisposeAsync().AsTask(),source.DisposeAsync().AsTask());
            await using var reopened=new NintendoPhysicalInputSource(Device(kind,"ownership-"+kind));
            var nextIo=new FakeHid(kind);await reopened.ConnectAsync(deadline.Token,nextIo,kind==NintendoInputKind.Pro2Usb?new FakeUsb(nextIo):null);
            check(reopened.IsRunning,"hotplug replacement can reclaim released identity "+kind);
        }
        var brokenIo=new FakeHid(NintendoInputKind.Pro1Bluetooth){RejectCommand=0x48};
        await using var broken=new NintendoPhysicalInputSource(Device(NintendoInputKind.Pro1Bluetooth,"rejected-init"));
        try{await broken.ConnectAsync(default,brokenIo);check(false,"NACK must fail init");}catch(IOException){check(!broken.IsRunning,"NACK never reports connected");}
    }
    private static async Task NfcAsync(Action<bool,string> check,bool cancel)
    {
        var io=new FakeHid(NintendoInputKind.Pro1Bluetooth){EmulateNfc=true,WaitWithoutTag=cancel};
        await using var source=new NintendoPhysicalInputSource(Device(NintendoInputKind.Pro1Bluetooth,"nfc-"+cancel));
        using var deadline=new CancellationTokenSource(8000);await source.ConnectAsync(deadline.Token,io);
        long before=source.Snapshot().Reports;
        using var cts=new CancellationTokenSource(cancel?250:4000);
        if(cancel)
        {
            try{await source.ReadNfcAsync(cts.Token);check(false,"cancelled tag read must not return success");}catch(OperationCanceledException){check(true,"NFC cancellation propagated after restore");}
        }
        else
        {
            var result=await source.ReadNfcAsync(cts.Token);
            check(result.Data.Length==540&&result.Uid.SequenceEqual(io.Uid),"complete NFC tag dump returned with matching UID/BCC");
        }
        check(io.Writes.Where(p=>p[0]==1).All(p=>p[10] is not (0x11 or 0x12)),"NFC never writes flash");
        check(io.Writes.Where(p=>p[0]==0x11&&p[10]==2).All(p=>p[11] is not (8 or 15)),"NFC never sends tag write/auth commands");
        check(io.Writes.Any(p=>p[0]==1&&p[10]==0x22&&p[11]==0)&&io.Writes.Last(p=>p[0]==1)[10]==3&&io.Writes.Last(p=>p[0]==1)[11]==0x30,"success and cancellation restore MCU suspend and report 30");
        // The restoration may enqueue three fresh report-30 packets, never MCU replies.
        await Task.Delay(30);
        check(source.Snapshot().Reports<=before+3,"NFC responses never inflate gameplay report frequency");
        io.Feed(Input(NintendoInputKind.Pro1Bluetooth));await Until(()=>source.TryGetLatest(out _,out _),deadline.Token);
        check(source.IsRunning&&source.TryGetLatest(out _,out _),"gameplay input resumes after NFC");
    }
    private static async Task NfcRestoreFailureAsync(Action<bool,string> check)
    {
        var io=new FakeHid(NintendoInputKind.Pro1Bluetooth){EmulateNfc=true,RejectCommand=0x22};
        await using var source=new NintendoPhysicalInputSource(Device(NintendoInputKind.Pro1Bluetooth,"nfc-restore-failure"));
        using var deadline=new CancellationTokenSource(5000); await source.ConnectAsync(deadline.Token,io);
        try { await source.ReadNfcAsync(deadline.Token); check(false,"NFC MCU NACK must not return a tag"); }
        catch(IOException) { check(true,"NFC MCU NACK propagated"); }
        check(io.Writes.Last(p=>p[0]==1)[10]==3 && io.Writes.Last(p=>p[0]==1)[11]==0x30,
            "normal input mode restored even when MCU suspend is rejected");
        check(source.NfcStatus.Contains("恢复失败"),"partial NFC restore is reported without false success");
    }
    private static async Task VirtualRoundTripAsync(NintendoInputKind kind,ViiperVirtualMode mode,Action<bool,string> check)
    {
        using var stop=new CancellationTokenSource(8000);var profile=Pro2SlotModes.Profile(mode,1,ViiperPushRateOption.Default);
        var io=new FakeHid(kind);
        await using var source=new NintendoPhysicalInputSource(Device(kind,"stream-"+kind+mode));await source.ConnectAsync(stop.Token,io,kind==NintendoInputKind.Pro2Usb?new FakeUsb(io):null);
        var router=new SlotInputRouter(source);source.PrepareForConsumerSession();
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        using var client=new TcpClient{NoDelay=true};var accepting=listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);using var host=await accepting;host.NoDelay=true;listener.Stop();
        await using var session=new ViiperBridgeSession(new("127.0.0.1",1),profile,new Quiet(),router,router);
        typeof(ViiperBridgeSession).GetMethod("SetStream",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(session,[new ViiperDeviceStream(client,client.GetStream())]);
        var input=(Task)typeof(ViiperBridgeSession).GetMethod("InputLoopAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(session,[stop.Token])!;
        var feedback=(Task)typeof(ViiperBridgeSession).GetMethod("FeedbackLoopAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(session,[stop.Token])!;
        try
        {
            byte[] packet=new byte[profile.InputSize];await host.GetStream().ReadExactlyAsync(packet,stop.Token);
            for(int i=0;i<12;i++)
            {
                byte[] report=Input(kind);report[kind==NintendoInputKind.Pro2Usb?5:3]=4;Pack(report,kind==NintendoInputKind.Pro2Usb?11:6,2400+i*20,2048);io.Feed(report);
                await host.GetStream().ReadExactlyAsync(packet,stop.Token);
                source.TryGetLatest(out var expected,out _);
                check(packet.SequenceEqual(VirtualPadPackets.FromGamepad(profile,expected)),"production stream maps physical input "+kind+" "+mode);
            }
            byte[] wire=mode==ViiperVirtualMode.Pro1?[0,0xc8,0x40,0x72,0,1,0x40,0x40,1,0]:mode==ViiperVirtualMode.Xbox?[200,100]:mode==ViiperVirtualMode.Pro2?Pro2Feedback():Ps5Feedback();
            await host.GetStream().WriteAsync(wire,stop.Token);
            byte reportId=kind==NintendoInputKind.Pro2Usb?(byte)2:(byte)0x10;
            await Until(()=>io.Writes.Any(p=>p[0]==reportId),stop.Token);
            var actual=io.Writes.Last(p=>p[0]==reportId);
            check(actual.Length==(kind==NintendoInputKind.Pro2Usb?64:10),"physical feedback has native framing "+kind+" "+mode);
            if(mode==ViiperVirtualMode.Pro1&&kind!=NintendoInputKind.Pro2Usb) check(actual.AsSpan(2,8).SequenceEqual(wire.AsSpan(0,8)),"production Pro1 native HD rumble passes through exact bytes");
            await source.DisposeAsync();await host.GetStream().ReadExactlyAsync(packet,stop.Token);
            check(packet.SequenceEqual(VirtualPadPackets.FromGamepad(profile,GamepadState.Neutral())),"unplug releases controls once "+mode);
            var replacementIo=new FakeHid(kind);
            await using var replacement=new NintendoPhysicalInputSource(Device(kind,"stream-"+kind+mode));await replacement.ConnectAsync(stop.Token,replacementIo,kind==NintendoInputKind.Pro2Usb?new FakeUsb(replacementIo):null);replacement.PrepareForConsumerSession();
            router.Switch(replacement);byte[] next=Input(kind);next[kind==NintendoInputKind.Pro2Usb?5:3]=8;replacementIo.Feed(next);
            await host.GetStream().ReadExactlyAsync(packet,stop.Token);
            replacement.TryGetLatest(out var replaced,out _);
            check(packet.SequenceEqual(VirtualPadPackets.FromGamepad(profile,replaced)),"same virtual stream survives physical replacement "+mode);
        }
        finally{stop.Cancel();try{await Task.WhenAll(input,feedback);}catch(OperationCanceledException){}}
    }
    private static byte[] Pro2Feedback(){byte[] p=new byte[34];Pro2OutputPacketMapper.BuildOrdinaryPacket(100,200,"fixture").Report.AsSpan(1,32).CopyTo(p);p[32]=1;return p;}
    private static byte[] Ps5Feedback(){byte[] p=new byte[DualSenseHapticFrame.WireSize];p[0]=1;p[1]=48;p[4]=2;p[5]=3;p[7]=100;p[8]=200;return p;}
    private static async Task Until(Func<bool> predicate,CancellationToken token){while(!predicate())await Task.Delay(2,token);}
    private sealed class Quiet:IProgress<string>{public void Report(string value){}}
    private static byte[] Ack(byte command){byte[] p=new byte[64];p[0]=0x21;p[13]=0x80;p[14]=command;return p;}
    private static byte[] Input(NintendoInputKind kind)
    {
        byte[] p=new byte[kind==NintendoInputKind.Pro2Usb?64:49];p[0]=(byte)(kind==NintendoInputKind.Pro2Usb?5:0x30);p[1]=1;
        Pack(p,kind==NintendoInputKind.Pro2Usb?11:6,2048,2048);Pack(p,kind==NintendoInputKind.Pro2Usb?14:9,2048,2048);
        int imu=kind==NintendoInputKind.Pro2Usb?49:37;BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(imu+4),4096);return p;
    }
    private static void Pack(byte[] p,int at,int x,int y){p[at]=(byte)x;p[at+1]=(byte)((x>>8)|((y&15)<<4));p[at+2]=(byte)(y>>4);}
    private static byte[] Mcu(byte type,byte state,int length=362){byte[] p=new byte[length];p[0]=0x31;p[49]=type;p[50]=0;p[51]=5;p[55]=0x31;p[56]=state;return p;}
    private sealed class FakeHid(NintendoInputKind kind):INintendoHidTransport
    {
        private readonly Channel<byte[]> incoming=Channel.CreateUnbounded<byte[]>();
        public ConcurrentQueue<byte[]> Writes=new();public byte RejectCommand;public bool EmulateNfc,WaitWithoutTag;private byte mcuMode=1,nfcState,chunk;private bool reading,tagSeen;
        public byte[] Uid=[4,1,2,3,4,5,6];
        public void Feed(byte[] p)=>incoming.Writer.TryWrite(p);
        public async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token){var p=await incoming.Reader.ReadAsync(token);p.CopyTo(buffer);return p.Length;}
        public Task WriteAsync(ReadOnlyMemory<byte> memory,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();byte[] p=memory.ToArray();Writes.Enqueue(p);
            if(p[0]==0x80){if(p[1]!=4)Feed([0x81,p[1]]);return Task.CompletedTask;}
            if(p[0]==1)
            {
                byte command=p[10];var ack=Ack(command);if(command==RejectCommand)ack[13]=0;
                if(command==0x10){Array.Resize(ref ack,20+p[15]);p.AsSpan(11,5).CopyTo(ack.AsSpan(15));}
                if(command==0x21)mcuMode=4;
                Feed(ack);
                if(command==3&&p[11]==0x30)for(int i=0;i<3;i++)Feed(Input(kind));
            }
            if(p[0]==0x11&&EmulateNfc)
            {
                byte[] response;
                if(p[10]==1)response=Mcu(1,mcuMode);
                else
                {
                    byte cmd=p[11];if(cmd==2)nfcState=0;if(cmd==1)nfcState=1;
                    if(cmd==6){reading=true;chunk=0;response=Mcu(0x2a,1);}
                    else if(reading&&chunk<2)
                    {
                        response=Mcu(0x3a,0);response[51]=7;int count=chunk==0?240:300;response[52]=(byte)(chunk==0?1:2);
                        int declared=count+(chunk==0?60:0);response[54]=(byte)(declared>>8);response[55]=(byte)declared;
                        byte[] dump=new byte[540];Uid.AsSpan(0,3).CopyTo(dump);Uid.AsSpan(3,4).CopyTo(dump.AsSpan(4));
                        dump[3]=(byte)(0x88^dump[0]^dump[1]^dump[2]);dump[8]=(byte)(dump[4]^dump[5]^dump[6]^dump[7]);
                        dump.AsSpan(chunk==0?0:240,count).CopyTo(response.AsSpan(chunk==0?116:56));chunk++;
                    }
                    else if(reading)response=Mcu(0x2a,4);
                    else if(nfcState==1&&!WaitWithoutTag&&tagSeen){response=Mcu(0x2a,9);response[64]=7;Uid.CopyTo(response,65);}
                    else {response=Mcu(0x2a,nfcState);if(nfcState==1)tagSeen=true;}
                }
                Feed(response);
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync(){incoming.Writer.TryComplete();return ValueTask.CompletedTask;}
    }
    private sealed class FakeUsb(FakeHid io):INintendoUsbCommandChannel
    {
        public byte[] Send(byte command,byte sub,byte[] data)
        {if(command==1)throw new IOException("This firmware has no ACK for optional NFC initialization");if(command==3&&sub==13)for(int i=0;i<3;i++)io.Feed(Input(NintendoInputKind.Pro2Usb));return [command,1,0,sub,0,0xf8,0,0];}
        public void Dispose(){}
    }
}
