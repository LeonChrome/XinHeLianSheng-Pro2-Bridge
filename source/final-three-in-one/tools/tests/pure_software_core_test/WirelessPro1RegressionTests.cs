using System.Buffers.Binary;
using System.Diagnostics;
using Y700Switch2V60Viiper;

internal static class WirelessPro1RegressionTests
{
    public static void WriteIpcFixtures(string output)
    {
        System.IO.Directory.CreateDirectory(output);byte[] session=Enumerable.Range(0,16).Select(i=>(byte)i).ToArray();
        var manifest=new List<object>();
        void Save(string name,byte[] packet,bool valid)
        {System.IO.File.WriteAllBytes(System.IO.Path.Combine(output,name+".bin"),[..session,..packet]);manifest.Add(new{name,valid});}
        foreach(var entry in new[]{(Id:(byte)0x30,Length:49),(Id:(byte)0x31,Length:362),(Id:(byte)0x3f,Length:12),(Id:(byte)0x21,Length:49)})
        {
            byte[] payload=new byte[entry.Length];payload[0]=entry.Id;var kind=entry.Id==0x21?WirelessRelayMessage.ProtocolReply:WirelessRelayMessage.FreshInput;
            Save("valid-"+entry.Id.ToString("X2"),WirelessRelayDatagram.Encode(session,kind,99,12345,payload),true);
        }
        var heartbeat=WirelessRelayDatagram.Encode(session,WirelessRelayMessage.Heartbeat,100,12345,[]);Save("valid-heartbeat",heartbeat,true);
        byte[] wrong=(byte[])heartbeat.Clone();wrong[8]^=1;Save("bad-session",wrong,false);
        wrong=(byte[])heartbeat.Clone();wrong[40]=1;Save("bad-reserved",wrong,false);
        wrong=(byte[])heartbeat.Clone();wrong[6]=1;Save("bad-length",wrong,false);
        Save("bad-short",heartbeat[..^1],false);
        byte[] invalidReport=new byte[49];invalidReport[0]=0x31;Save("bad-mcu-size",WirelessRelayDatagram.Encode(session,WirelessRelayMessage.FreshInput,101,12345,invalidReport),false);
        System.IO.File.WriteAllText(System.IO.Path.Combine(output,"manifest.json"),System.Text.Json.JsonSerializer.Serialize(manifest));
    }
    private static byte[] Sub(byte command,params byte[] data)=>[1,0,0,1,0x40,0x40,0,1,0x40,0x40,command,..data];
    private static byte[] Mcu(byte command,byte[]? payload=null)=>[0x11,0,0,1,0x40,0x40,0,1,0x40,0x40,command,..payload??[]];
    public static void Run(Action<bool,string> check)
    {
        var systemRadio=new WirelessRelayRadio(@"USB\VID_0BDA&PID_0852\RADIO_A","Realtek","BTHUSB",true,true);
        check(systemRadio.CanPrepare&&!systemRadio.Ready,"system Bluetooth requires reversible preparation before the native backend can open it");
        var preparedRadio=systemRadio with{Service="WinUSB"};
        check(preparedRadio.Ready&&!preparedRadio.CanPrepare,"an already prepared radio is not rebound by the application");
        var unsupportedRadio=new WirelessRelayRadio(@"USB\VID_8087&PID_0033\RADIO_B","Intel","WinUSB",false,false);
        check(!unsupportedRadio.Ready&&!unsupportedRadio.CanPrepare,"an unsupported adapter cannot become a transmitter merely by using WinUSB");
        check(!(systemRadio with{Service="unknown"}).CanPrepare,"an unknown driver state cannot trigger automatic radio preparation");
        check(systemRadio.Label!=(systemRadio with{InstanceId=@"USB\VID_0BDA&PID_0852\RADIO_B"}).Label,"manual selection distinguishes two radios of the same model by exact instance");
        WirelessRelayRadio[] available=[systemRadio,unsupportedRadio];
        check(WirelessRelayRadioSelection.Resolve(available,null)==null,"multiple adapters require explicit selection instead of choosing the first radio");
        check(WirelessRelayRadioSelection.Resolve(available,systemRadio.InstanceId)==systemRadio,"adapter refresh preserves the selected exact instance");
        check(WirelessRelayRadioSelection.Resolve([unsupportedRadio],systemRadio.InstanceId)==null,"unplugging the chosen adapter never redirects driver preparation to a different radio");
        check(WirelessRelayRadioSelection.Resolve([systemRadio],null)==systemRadio,"a single radio is a visible selectable default");
        byte[] session=Enumerable.Range(0,16).Select(i=>(byte)i).ToArray(),payload=new byte[362];payload[0]=0x31;
        var encoded=WirelessRelayDatagram.Encode(session,WirelessRelayMessage.FreshInput,7,123,payload);
        check(WirelessRelayDatagram.TryDecode(encoded,session,out var decoded)&&decoded!.Sequence==7&&decoded.Timestamp==123&&decoded.Payload.SequenceEqual(payload),"authenticated maximum NFC-sized relay datagram roundtrip");
        foreach(int index in new[]{0,4,6,8,12,23,40,47})
        {byte[] corrupt=(byte[])encoded.Clone();corrupt[index]^=0x80;check(!WirelessRelayDatagram.TryDecode(corrupt,session,out _),"relay rejects corrupt envelope/session/reserved field "+index);}
        check(!WirelessRelayDatagram.TryDecode(encoded[..^1],session,out _)&&!WirelessRelayDatagram.TryDecode([..encoded,0],session,out _),"relay packet length exact; no truncated/padded NFC reports");
        for(int id=1;id<=6;id++)
        {var frame=WirelessRelayDatagram.Encode(session,(WirelessRelayMessage)id,(ulong)id,42,[]);check(WirelessRelayDatagram.TryDecode(frame,session,out _),"relay command type "+id);}
        var protocol=new WirelessPro1Protocol([2,3,4,5,6,7]);var state=GamepadState.Neutral();long tick=Stopwatch.GetTimestamp(),frequency=Stopwatch.Frequency;
        WirelessPro1Output Send(byte[] bytes)=>protocol.HandleOutput(bytes,state,tick,frequency);
        var info=Send(Sub(2));check(info.Reply is{Length:49}&&info.Reply[13]==0x82&&info.Reply.AsSpan(19,6).SequenceEqual(new byte[]{2,3,4,5,6,7}),"Pro1 device-info ACK contains actual selected radio address");
        foreach(byte id in new byte[]{0x11,0x12,0x60,0xff})check(Send(Sub(id)).Reply![13]==0,"unknown/flash-write commands are NACKed "+id);
        check(Send(Sub(3,0x30)).Reply![13]==0x80&&protocol.InputMode==0x30,"host selects full Pro1 input reports");
        check(Send(Sub(0x40,1)).Reply![13]==0x80&&protocol.ImuEnabled,"host enables motion");
        check(Send(Sub(0x41,3,0,1,1)).Reply![13]==0x80&&Send(Sub(0x41,0,0,1,1)).Reply![13]==0,"supported IMU sensitivity accepted, unimplemented range rejected");
        var spi=Send(Sub(0x10,0x3d,0x60,0,0,18)).Reply!;var imu=Send(Sub(0x10,0x20,0x60,0,0,24)).Reply!;
        var parser=new NintendoPhysicalParser(NintendoInputKind.Pro1Bluetooth);parser.SetDeadzones(new(0,0));parser.SetPro1FactoryCalibration(spi.AsSpan(20,18),imu.AsSpan(20,24));
        GamepadButtons[] buttons=[GamepadButtons.South,GamepadButtons.East,GamepadButtons.North,GamepadButtons.West,GamepadButtons.L1,GamepadButtons.R1,GamepadButtons.L2,GamepadButtons.R2,GamepadButtons.Back,GamepadButtons.Start,GamepadButtons.LeftStick,GamepadButtons.RightStick,GamepadButtons.Home,GamepadButtons.Capture,GamepadButtons.DPadDown,GamepadButtons.DPadUp,GamepadButtons.DPadRight,GamepadButtons.DPadLeft];
        foreach(var button in buttons)
        {
            state.Buttons=button;state.Lx=3200;state.Ly=3800;state.Rx=700;state.Ry=1200;
            state.GyroValid=state.AccelValid=true;state.AccelX=800;state.AccelY=-1200;state.AccelZ=4096;state.GyroX=1234;state.GyroY=-4567;state.GyroZ=2000;
            protocol.Observe(state,tick);var report=protocol.BuildInput(state,tick,frequency);
            check(report.Length==49&&report[0]==0x30&&parser.TryParse(report,out var roundtrip,tick),"wireless full report accepted by existing Pro1 parser "+button);
            parser.TryParse(report,out var parsed,tick);
            check(parsed.Buttons==button,"physical-position button mapping roundtrip "+button);
            check(Math.Abs(parsed.Lx-state.Lx)<4&&Math.Abs(parsed.Ly-state.Ly)<4&&Math.Abs(parsed.Rx-state.Rx)<4&&Math.Abs(parsed.Ry-state.Ry)<4,"normalized sticks agree with advertised factory calibration");
            check(Math.Abs(parsed.GyroX-state.GyroX)<4&&Math.Abs(parsed.GyroY-state.GyroY)<4&&Math.Abs(parsed.GyroZ-state.GyroZ)<4&&parsed.AccelX==state.AccelX&&parsed.AccelY==state.AccelY&&parsed.AccelZ==state.AccelZ,"all six motion axes preserve sign/units through Pro1 calibration");
        }
        state=GamepadState.Neutral();
        check(Send(Sub(0x22,1)).Reply![13]==0x80&&Send(Sub(0x21,0x21,0,4)).Reply![13]==0xa0,"NFC MCU resume and mode configuration handshake");
        var status=Send(Mcu(1)).Reply!;
        check(Pro1NfcProtocol.TryGetMcu(status,out byte type,out var data)&&type==1&&data[6]==4,"MCU status reports NFC mode without fabricating tag data");
        var scanning=Send(Mcu(2,Pro1NfcProtocol.Request(1,0,[0,0,0,0x2c,1])));
        check(scanning.CaptureRequested&&!protocol.HasTag,"host scan triggers one physical USB capture while no snapshot exists");
        byte[] uid=[4,11,22,33,44,55,66],raw=new byte[540];uid.AsSpan(0,3).CopyTo(raw);uid.AsSpan(3,4).CopyTo(raw.AsSpan(4));raw[3]=(byte)(0x88^uid[0]^uid[1]^uid[2]);raw[8]=(byte)(uid[3]^uid[4]^uid[5]^uid[6]);
        new byte[]{0xf1,0x10,0xff,0xee}.CopyTo(raw,12);for(int i=16;i<raw.Length;i++)raw[i]=(byte)(i*31);
        var tag=new NintendoNfcTag(uid,raw,"synthetic_fixture","offline",DateTimeOffset.UnixEpoch){Signature=Enumerable.Range(0,32).Select(i=>(byte)(i+3)).ToArray()};protocol.SetTag(tag);
        var tagReply=Send(Mcu(2,Pro1NfcProtocol.Request(4))).Reply!;
        check(Pro1NfcProtocol.TryGetMcu(tagReply,out type,out data)&&Pro1NfcProtocol.TryGetTag(type,data,out var found)&&found.SequenceEqual(uid),"NFC present notification accepted by production reader");
        byte[] ranges=[0xd0,7,0,0,0,0,0,0,0,0,1,3,0,0x3b,0x3c,0x77,0x78,0x86,0,0];
        var startRead=Send(Mcu(2,Pro1NfcProtocol.Request(6,0,ranges)));
        check(startRead.Reply is{Length:362},"NFC read uses full 362-byte report");
        List<byte> dump=[];
        for(byte part=0;part<2;part++)
        {
            var report=Send(Mcu(2,Pro1NfcProtocol.Request(4,part))).Reply!;
            check(Pro1NfcProtocol.TryGetMcu(report,out type,out data)&&type==0x3a&&data[1]==7,"MCU tag-data fragment "+part);
            int count=((data[4]<<8)|data[5])&0x7ff;int offset=data[2]==1?66:6;if(offset==66)count-=60;
            dump.AddRange(data.AsSpan(offset,count).ToArray());
            var duplicate=Send(Mcu(2,Pro1NfcProtocol.Request(4,part))).Reply!;
            check(report.AsSpan(49).SequenceEqual(duplicate.AsSpan(49)),"repeated poll retransmits exact fragment without advancing "+part);
            check(report[^1]==Pro1NfcProtocol.Crc8(report.AsSpan(49,312)),"outgoing NFC data CRC matches full MCU payload "+part);
        }
        check(dump.SequenceEqual(raw)&&Pro1NfcProtocol.IsNtag215(dump.ToArray(),uid),"all 540 original tag bytes restored exactly with UID/BCC validation");
        var end=Send(Mcu(2,Pro1NfcProtocol.Request(4,2))).Reply!;
        check(Pro1NfcProtocol.TryGetMcu(end,out type,out data)&&type==0x2a&&data[6]==4,"read completion is explicit after receiver ACKs both fragments");
        var write=Send(Mcu(2,Pro1NfcProtocol.Request(8,0,[1,2,3]))).Reply!;
        check(Pro1NfcProtocol.TryGetMcu(write,out type,out data)&&data[6]==7,"unsupported NFC write is reported as error, never fake success");
        protocol.SetTag(null);check(!protocol.HasTag,"explicit snapshot removal clears virtual tag");
        Send(Sub(3,0x31));check(protocol.BuildInput(state,tick,frequency).Length==362,"stream switches to NFC report mode on host request");
        Send(Sub(3,0x3f));check(protocol.BuildInput(state,tick,frequency).Length==12,"simple-mode payload length agrees with HID descriptor");
        var history=new WirelessPro1Protocol([2,3,4,5,6,7]);
        history.HandleOutput(Sub(3,0x30),state,tick,frequency);history.HandleOutput(Sub(0x40,1),state,tick,frequency);
        for(int i=0;i<3;i++)history.Observe(new GamepadState{AccelValid=true,AccelZ=(short)(100+i*100)},tick-(long)((2-i)*.005*frequency));
        var motion=history.BuildInput(new GamepadState{AccelValid=true,AccelZ=300},tick,frequency);
        check(BinaryPrimitives.ReadInt16LittleEndian(motion.AsSpan(17))==300&&BinaryPrimitives.ReadInt16LittleEndian(motion.AsSpan(29))==200&&BinaryPrimitives.ReadInt16LittleEndian(motion.AsSpan(41))==100,"Pro1 motion wire order is newest/previous/oldest, matching SDL receiver consumption");
        foreach(double hz in new[]{1000.0/15,133,250})
        {
            var cadence=new WirelessPro1Cadence(hz,1000000);List<long> selected=[];
            for(int i=0;i<1000;i++)if(cadence.TrySelect(new GamepadState{SourceTimestampTicks=1000000+i*4000},out var chosen))selected.Add(chosen.SourceTimestampTicks);
            double measured=(selected.Count-1)*1000000.0/(selected[^1]-selected[0]);
            check(Math.Abs(measured-hz)<.6&&selected.Distinct().Count()==selected.Count,"cadence selects distinct physical reports without manufacturing timer frames "+hz);
        }
        var edges=new WirelessPro1Cadence(1000.0/15,1000000);List<GamepadButtons> delivered=[];
        for(int i=0;i<16;i++)
        {var item=new GamepadState{SourceTimestampTicks=1000000+i*4000,Buttons=i==1?GamepadButtons.South:GamepadButtons.None};if(edges.TrySelect(item,out var chosen))delivered.Add(chosen.Buttons);}
        check(delivered.Count(b=>b==GamepadButtons.South)==1&&delivered[^1]==GamepadButtons.None,"short physical press/release survives downsampling without repetition or stuck button");
    }
}
