using System.Buffers.Binary;
using Y700Switch2V60Viiper;

internal static class WirelessControllerRegressionTests
{
    public static void Run(Action<bool,string> check)
    {
        var neutral=GamepadState.Neutral();
        var ps=new WirelessControllerProtocol(WirelessPadMode.Ps5);
        byte[] p=ps.BuildInput(neutral,0);
        check(p.Length==78&&p[0]==0x31&&p[9]==8,"PS5 full Bluetooth input and neutral hat");
        check(p.AsSpan(2,4).ToArray().All(b=>b==128),"PS5 neutral axes");
        // Independently generated with Python zlib over the specified neutral
        // report, prefixed by A1. Do not compute expected CRC with production code.
        check(BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(74))==0xe61a45e5,"PS5 CRC independent golden vector (neutral gravity 8192, battery unknown)");
        check(p[54]==0xf0,"PS5 unknown battery is not invented as 50 percent");
        check(p[34]==0x80&&p[38]==0x80,"PS5 both contacts inactive at rest");
        var xbox=new WirelessControllerProtocol(WirelessPadMode.Xbox);
        byte[] x=xbox.BuildInput(neutral,0);
        check(x.Length==17&&x[0]==1&&x[13]==0,"Xbox Classic report and neutral hat");
        check(BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(1))==32768&&BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(3))==32767,"Xbox neutral unsigned axes");
        check(xbox.AuxiliaryInput?.SequenceEqual(new byte[]{2,0})==true,"Xbox initial Guide release has separate report");
        xbox.BuildInput(neutral,1);check(xbox.AuxiliaryInput==null,"Xbox no repeated Guide traffic");
        check(xbox.BatteryInput==null,"Xbox unknown battery is not fabricated");
        var charged=GamepadState.Neutral();charged.BatteryPercent=80;
        xbox.BuildInput(charged,2);check(xbox.BatteryInput?.SequenceEqual(new byte[]{4,204})==true,"Xbox battery has its own report");
        xbox.BuildInput(charged,3);check(xbox.BatteryInput==null,"Xbox cached battery is not repeatedly emitted");
        foreach(var item in new[]{(GamepadButtons.South,32,1),(GamepadButtons.East,64,2),(GamepadButtons.West,16,8),(GamepadButtons.North,128,16)})
        {
            var state=GamepadState.Neutral();state.Buttons=item.Item1;
            check((ps.BuildInput(state,0)[9]&240)==item.Item2,"PS5 positional face button "+item.Item1);
            check(xbox.BuildInput(state,0)[14]==item.Item3,"Xbox positional face button "+item.Item1);
        }
        foreach(var item in new[]{(GamepadButtons.DPadUp,0),(GamepadButtons.DPadUp|GamepadButtons.DPadRight,1),(GamepadButtons.DPadRight,2),(GamepadButtons.DPadDown,4),(GamepadButtons.DPadLeft,6)})
        {
            var state=GamepadState.Neutral();state.Buttons=item.Item1;
            check((ps.BuildInput(state,0)[9]&15)==item.Item2,"PS5 hat "+item.Item1);
            check(xbox.BuildInput(state,0)[13]==item.Item2+1,"Xbox hat "+item.Item1);
        }
        var edge=GamepadState.Neutral();edge.Lx=0;edge.Ly=0;edge.Rx=4095;edge.Ry=4095;edge.L2=4095;edge.R2=2048;
        x=xbox.BuildInput(edge,10);
        check(BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(1))==0&&BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(3))==65535&&BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(5))==65535&&BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(7))==0,"Xbox axis endpoints and Y inversion");
        check(BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(9))==1023&&BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(11))==511,"Xbox 10-bit triggers");
        var guide=GamepadState.Neutral();guide.Buttons=GamepadButtons.Home;
        xbox.BuildInput(guide,20);check(xbox.AuxiliaryInput?.SequenceEqual(new byte[]{2,1})==true,"Xbox Guide press");
        xbox.BuildInput(neutral,30);check(xbox.AuxiliaryInput?.SequenceEqual(new byte[]{2,0})==true,"Xbox Guide release");
        var touch=GamepadState.Neutral();touch.Ps5Touch=new(true,1919,1079);touch.Buttons=GamepadButtons.Capture;
        p=ps.BuildInput(touch,0);
        check(p[34]==0&&p[35]==127&&p[36]==119&&p[37]==67&&(p[11]&2)!=0,"PS5 touch coordinates and click");
        for(int i=0;i<260;i++)
        {
            p=ps.BuildInput(neutral,i*100);
            check((p[1]>>4)==(p[8]&15)&&WirelessControllerProtocol.VerifyCrc(p,0xa1),"PS5 sequence rollover and per-frame CRC "+i);
        }
        byte[] output=new byte[78];output[0]=0x31;output[2]=0x10;output[3]=3;output[5]=17;output[6]=201;
        WirelessControllerProtocol.WriteCrc(output,0xa2);
        check(ps.TryFeedback(output,out var packet,out _,out _)&&packet!.Report.SequenceEqual(Pro2OutputPacketMapper.BuildOrdinaryPacket(17,201,"expected").Report),"PS5 compatible weak/strong feedback channels");
        output[5]^=1;check(!ps.TryFeedback(output,out _,out _,out _),"PS5 corrupted output CRC rejected");
        output[3]=0;WirelessControllerProtocol.WriteCrc(output,0xa2);check(!ps.TryFeedback(output,out _,out _,out _),"PS5 control-only report does not erase rumble");
        check(!ps.TryFeedback(new byte[]{0x32},out _,out _,out _),"PS5 audio report not misread as ordinary rumble");
        byte[] feedback={3,2,100,100,100,100,20,3,1};
        check(xbox.TryFeedback(feedback,out packet,out int duration,out _)&&duration==200&&packet!.Report.SequenceEqual(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,255,"expected").Report),"Xbox main strong motor enable and duration");
        feedback[1]=1;
        check(xbox.TryFeedback(feedback,out packet,out _,out _)&&packet!.Report.SequenceEqual(Pro2OutputPacketMapper.BuildOrdinaryPacket(255,0,"expected").Report),"Xbox main weak motor enable");
        feedback[1]=12;
        check(xbox.TryFeedback(feedback,out packet,out _,out _)&&!packet!.Active,"Xbox trigger flags cannot drive nonexistent trigger motors");
        feedback[6]=0;
        check(xbox.TryFeedback(feedback,out _,out duration,out _)&&duration==0,"Xbox zero duration is stop");
        check(!xbox.TryFeedback(feedback.AsSpan(0,8),out _,out _,out _),"Xbox truncated report rejected");
        bool refused=false;try{WirelessPadModes.FromVirtual(ViiperVirtualMode.Pro2);}catch(NotSupportedException){refused=true;}
        check(refused,"Native Pro2 is never silently aliased to wireless Pro1");
        var pro1=new WirelessPro1Protocol(new byte[6]);byte[] led=new byte[12];led[0]=1;led[10]=0x30;led[11]=1;
        check(pro1.HandleOutput(led,neutral,0,1000).PlayerLights==1,"Pro1 accepted player-light request reaches physical bridge");
        check(pro1.HandleOutput(led.AsSpan(0,11),neutral,0,1000).PlayerLights==null,"Pro1 malformed LED request cannot touch physical lights");
        byte[] mode=new byte[12];mode[0]=1;mode[10]=3;mode[11]=0x30;pro1.HandleOutput(mode,neutral,1,1000);
        byte[] release=pro1.BuildSafetyRelease(2,1000);
        check(release.Length==49&&release[0]==0x30&&release.AsSpan(3,3).ToArray().All(b=>b==0),"Pro1 pause safety release carries no pressed buttons");
    }
    public static void WriteFixtures(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        byte[] secret=Enumerable.Range(0,16).Select(i=>(byte)i).ToArray();var manifest=new List<object>();
        void Save(string name,string profile,byte[] payload,bool valid,WirelessRelayMessage kind=WirelessRelayMessage.FreshInput)
        {
            byte[] packet=WirelessRelayDatagram.Encode(secret,kind,123,5678,payload);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,name+".bin"),[..secret,..packet]);manifest.Add(new{name,profile,valid});
        }
        var neutral=GamepadState.Neutral();
        byte[] ps=new WirelessControllerProtocol(WirelessPadMode.Ps5).BuildInput(neutral,0);
        byte[] xb=new WirelessControllerProtocol(WirelessPadMode.Xbox).BuildInput(neutral,0);
        Save("ps5-valid","ps5",ps,true);Save("ps5-reject-usb64","ps5",new byte[64],false);
        Save("ps5-reject-pro1","ps5",new byte[49],false);Save("ps5-reject-xbox","ps5",xb,false);
        Save("xbox-valid","xbox",xb,true);Save("xbox-guide","xbox",[2,1],true,WirelessRelayMessage.ProtocolReply);
        Save("xbox-battery","xbox",[4,204],true,WirelessRelayMessage.ProtocolReply);
        Save("xbox-reject-ps5","xbox",ps,false);Save("xbox-reject-truncated","xbox",xb[..^1],false);
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"manifest.json"),System.Text.Json.JsonSerializer.Serialize(manifest));
    }
}
