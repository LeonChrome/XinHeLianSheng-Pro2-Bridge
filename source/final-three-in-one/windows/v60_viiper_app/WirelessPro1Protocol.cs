using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Y700Switch2V60Viiper;

public sealed record WirelessPro1Output(byte[]? Reply,byte[]? Rumble,bool CaptureRequested,string Operation)
{ public byte? PlayerLights {get;init;} }

// Nintendo Classic HID wire format. All offsets are report-ID-relative (there
// is no A1 HID transaction header here). State/ACKs stay out of input counters.
// Protocol observations: dekuNukem/Nintendo_Switch_Reverse_Engineering;
// existing project's Pico switch1_protocol.cpp. No Nintendo flash/tag writes.
public sealed class WirelessPro1Protocol
{
    private readonly byte[] address;
    private readonly Action<string> log;
    private readonly List<(long Tick,GamepadState State)> motion=[];
    private byte[] mcu=new byte[313];
    private byte mcuMode=1;
    private bool mcuEnabled,scanning,reading;
    private NintendoNfcTag? tag;
    private int readPart=-1;
    private bool pendingMcuResponse;
    public byte InputMode {get;private set;}=0x3f;
    public bool ImuEnabled {get;private set;}
    public bool VibrationEnabled {get;private set;}
    public byte PlayerLights {get;private set;}
    public long HostCommands {get;private set;}
    public long NfcCommands {get;private set;}
    public long RejectedCommands {get;private set;}
    public bool HasTag=>tag!=null;
    public WirelessPro1Protocol(byte[] localAddress,Action<string>? trace=null)
    {
        if(localAddress.Length!=6)throw new ArgumentException("蓝牙地址应为六字节。");
        address=(byte[])localAddress.Clone();log=trace??(_=>{});mcu[0]=0xff;SealMcu();
    }
    public void SetTag(NintendoNfcTag? value)
    {
        if(value!=null&&!Pro1NfcProtocol.IsNtag215(value.Data,value.Uid))throw new ArgumentException("需要完整的 NTAG215 标签快照。");
        tag=value==null?null:value with{Uid=(byte[])value.Uid.Clone(),Data=(byte[])value.Data.Clone(),Signature=(byte[])value.Signature.Clone()};
        reading=false;readPart=-1;SetNfcStatus(scanning&&tag!=null?(byte)9:(byte)(scanning?1:0));
        log("[WIRELESS_NFC_SNAPSHOT] present="+(tag!=null)+" bytes="+(tag?.Data.Length??0)+" mode=read_only_snapshot automatic_physical_removal=false tag_writes=0");
    }
    public void Observe(GamepadState state,long ticks)
    {
        motion.Add((ticks,state.Clone()));
        while(motion.Count>32)motion.RemoveAt(0);
    }
    // Internal relay snapshots always carry the complete physical state. The
    // native btkeyLib engine owns host report modes, IMU enablement and timing.
    public byte[] BuildRelaySnapshot(GamepadState state,long ticks,long frequency)
    {
        ArgumentNullException.ThrowIfNull(state);
        if(frequency<=0)throw new ArgumentOutOfRangeException(nameof(frequency));
        byte[] report=Standard(state,49,0x30,ticks,frequency);
        for(int i=0;i<3;i++)
        {
            long wanted=ticks-(long)(i*frequency/200.0);var selected=state;
            // A neutral safety snapshot has no valid sensors and must not
            // inherit the last moving sample from the observation history.
            if(state.AccelValid||state.GyroValid)
            {
                selected=motion.Count>0?motion[0].State:state;
                for(int j=motion.Count-1;j>=0;j--)if(motion[j].Tick<=wanted){selected=motion[j].State;break;}
            }
            WriteMotion(report.AsSpan(13+i*12,12),selected);
        }
        return report;
    }
    public byte[] BuildInput(GamepadState state,long ticks,long frequency,double reportPeriodMs=15)
    {
        if(InputMode==0x3f&&!pendingMcuResponse)return Simple(state);
        byte mode=InputMode==0x31||pendingMcuResponse?(byte)0x31:(byte)0x30;pendingMcuResponse=false;
        byte[] report=Standard(state,mode==0x31?362:49,mode,ticks,frequency);
        if(ImuEnabled)
        {
            // Native Pro1 has three 5ms motion samples. Select real observed
            // states at those times, holding edges when history is insufficient.
            // This never increments physical-report or fresh-input counts.
            for(int i=0;i<3;i++)
            {
                // SDL consumes wire sample 2 first, then 1, then 0. Native Pro1
                // uses a 15ms report / 5ms sample span. Higher-rate trials use
                // held real history over their own report span, never invent
                // three independently measured samples per USB input report.
                long wanted=ticks-(long)(i*Math.Clamp(reportPeriodMs,.5,100)/3000*frequency);var selected=motion.Count>0?motion[0].State:state;
                for(int j=motion.Count-1;j>=0;j--)if(motion[j].Tick<=wanted){selected=motion[j].State;break;}
                WriteMotion(report.AsSpan(13+i*12,12),selected);
            }
        }
        if(mode==0x31)mcu.CopyTo(report,49);
        return report;
    }
    public byte[] BuildSafetyRelease(long ticks,long frequency)
    {
        var neutral=GamepadState.Neutral();if(InputMode==0x3f)return Simple(neutral);
        byte mode=InputMode==0x31?(byte)0x31:(byte)0x30;
        byte[] report=Standard(neutral,mode==0x31?362:49,mode,ticks,frequency);
        if(mode==0x31)mcu.CopyTo(report,49);return report;
    }
    public WirelessPro1Output HandleOutput(ReadOnlySpan<byte> report,GamepadState state,long ticks,long frequency)
    {
        HostCommands++;
        if(report.Length<10||report[0] is not (1 or 0x10 or 0x11))return Reject("output_report_or_length");
        byte[]? rumble=VibrationEnabled?report.Slice(2,8).ToArray():null;
        if(report[0]==0x10)return new(null,rumble,false,"rumble");
        if(report[0]==0x11)
        {
            if(report.Length<11)return Reject("mcu_missing_command");
            return McuRequest(report[10..],state,ticks,frequency,rumble);
        }
        if(report.Length<11)return Reject("missing_subcommand");
        byte command=report[10];ReadOnlySpan<byte> data=report[11..];
        byte[] reply=Standard(state,49,0x21,ticks,frequency);reply[13]=0x80;reply[14]=command;
        bool valid=true;
        switch(command)
        {
            case 1:break; // pairing request: experiment bond is owned by BTstack
            case 2:
                reply[13]=0x82;new byte[]{3,0x48,3,2}.CopyTo(reply,15);address.CopyTo(reply,19);reply[25]=1;reply[26]=2;break;
            case 3:
                valid=data.Length>=1&&data[0] is 0x30 or 0x31 or 0x3f;
                if(valid){InputMode=data[0];pendingMcuResponse=false;}break;
            case 4:reply[13]=0x83;break;
            case 8:valid=data.Length>=1;break; // shipment hint: never forward power commands
            case 0x10:
                valid=data.Length>=5&&data[4]<=29;
                if(valid)
                {
                    reply[13]=0x90;data[..5].CopyTo(reply.AsSpan(15));
                    uint offset=BinaryPrimitives.ReadUInt32LittleEndian(data);ReadSpi(offset,reply.AsSpan(20,data[4]));
                }
                break;
            case 0x21:
                valid=data.Length>=3&&data[0]==0x21&&data[1]==0&&data[2] is 0 or 4;
                if(valid)
                {
                    byte[] configAck=McuConfigurationAck();reply[13]=0xa0;configAck.CopyTo(reply,15);
                    log("[WIRELESS_PRO1_MCU_CONFIG_ACK] schema=pro1_config_3_5 prefix="+Convert.ToHexString(configAck.AsSpan(0,8))+" crc="+configAck[33].ToString("X2"));
                    mcuMode=data[2]==4?(byte)4:(byte)1;
                }
                break;
            case 0x22:
                valid=data.Length>=1&&data[0]<=1;
                if(valid){mcuEnabled=data[0]==1;mcuMode=1;scanning=false;reading=false;mcu=McuState();}break;
            case 0x30:valid=data.Length>=1;if(valid)PlayerLights=data[0];break;
            case 0x31:reply[13]=0xb0;reply[15]=PlayerLights;break;
            case 0x38:break;
            case 0x40:valid=data.Length>=1&&data[0]<=1;if(valid)ImuEnabled=data[0]==1;break;
            case 0x41:valid=data.Length>=4&&data[0]==3&&data[1]==0&&data[2]==1&&data[3]==1;break;
            case 0x48:
                valid=data.Length>=1&&data[0]<=1;
                if(valid)
                {
                    VibrationEnabled=data[0]==1;
                    if(!VibrationEnabled)rumble=Pro1PhysicalRumble.Neutral;
                }
                break;
            case 0x50:reply[13]=0xd0;BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(15),state.BatteryVoltageMillivolts==0?(ushort)0:checked((ushort)(state.BatteryVoltageMillivolts/2)));break;
            default:valid=false;break; // includes SPI writes / firmware / unknown
        }
        if(!valid){RejectedCommands++;reply[13]=0;}
        log("[WIRELESS_PRO1_SUBCOMMAND] sub="+command.ToString("X2")+" ack="+reply[13].ToString("X2")+" input_mode="+InputMode.ToString("X2")+" accepted="+valid);
        return new(reply,rumble,false,"subcommand_"+command.ToString("X2")){PlayerLights=valid&&command==0x30?PlayerLights:null};
    }
    private WirelessPro1Output Reject(string reason)
    {
        RejectedCommands++;log("[WIRELESS_PRO1_REJECTED] reason="+reason);return new(null,null,false,reason);
    }
    private WirelessPro1Output McuRequest(ReadOnlySpan<byte> data,GamepadState state,long ticks,long frequency,byte[]? rumble)
    {
        NfcCommands++;bool capture=false;
        if(!mcuEnabled)return Reject("mcu_suspended");
        if(data[0]==1)mcu=McuState();
        else if(data[0]==2&&data.Length>=39&&mcuMode==4)
        {
            var request=data.Slice(1,38);
            if(request[36]!=Pro1NfcProtocol.Crc8(request[..36])||request[4]>31)return Reject("nfc_crc_or_length");
            byte cmd=request[0],packet=request[2];
            switch(cmd)
            {
                case 1:
                    scanning=true;reading=false;readPart=-1;capture=tag==null;SetNfcStatus(tag==null?(byte)1:(byte)9);break;
                case 2:scanning=false;reading=false;readPart=-1;SetNfcStatus(0);break;
                case 4:
                    if(reading&&tag!=null)
                    {
                        // Poll packet IDs are acknowledged chunk indices. A
                        // duplicate poll retransmits identical bytes; no timer
                        // advances data behind the receiver's back.
                        readPart=Math.Min((int)packet,2);
                        if(readPart<2)SetReadChunk(readPart);else{reading=false;SetNfcStatus(4);}
                    }
                    else SetNfcStatus(scanning?(tag==null?(byte)1:(byte)9):(byte)0);
                    break;
                case 6:
                    if(tag==null||!KnownReadRanges(request)){SetNfcStatus(7,0x3e);break;}
                    reading=true;readPart=-1;SetNfcStatus(2);break;
                default:
                    RejectedCommands++;SetNfcStatus(7,0x3e);break; // no writes, authentication or raw RF passthrough
            }
            log("[WIRELESS_NFC_REQUEST] command="+cmd.ToString("X2")+" packet="+packet+" read_part="+readPart+" cached_tag="+(tag!=null)+" capture_requested="+capture+" tag_writes=0");
        }
        else return Reject("mcu_command_or_length");
        byte[] response=Standard(state,362,0x31,ticks,frequency);mcu.CopyTo(response,49);
        pendingMcuResponse=true;
        return new(response,rumble,capture,"mcu");
    }
    private bool KnownReadRanges(ReadOnlySpan<byte> request)
    {
        int count=request[4];var data=request.Slice(5,count);
        if(data.Length!=20||data[0]!=0xd0||data[1]!=7)return false;
        bool zero=true;foreach(byte b in data.Slice(2,8))zero&=b==0;
        if(!zero&&(tag==null||!data.Slice(2,7).SequenceEqual(tag.Uid)||data[9]!=0))return false;
        return data[10..].SequenceEqual(new byte[]{1,3,0,0x3b,0x3c,0x77,0x78,0x86,0,0});
    }
    private static byte[] McuConfigurationAck()
    {
        // Configuration ACKs use a distinct 34-byte schema, not the 313-byte
        // runtime status report. Pro Controller MCU firmware remains 3/5.
        // Compare MissionControl ProcessMcuWrite and its 33-byte CRC coverage.
        byte[] output=new byte[34];
        new byte[]{1,0,0xff,0,3,0,5,1}.CopyTo(output,0);
        output[33]=Pro1NfcProtocol.Crc8(output.AsSpan(0,33));return output;
    }
    private byte[] McuState()
    {
        byte[] output=new byte[313];output[0]=1;output[4]=3;output[6]=5;output[7]=mcuEnabled?mcuMode:(byte)1;
        output[^1]=Pro1NfcProtocol.Crc8(output.AsSpan(0,312));return output;
    }
    private void SetNfcStatus(byte state,byte detail=0)
    {
        mcu=new byte[313];mcu[0]=0x2a;mcu[2]=5;mcu[5]=9;mcu[6]=0x31;mcu[7]=state;
        if(state==7)mcu[8]=detail;
        if(tag!=null&&state is 4 or 9)
        {mcu[11]=1;mcu[12]=1;mcu[13]=2;mcu[15]=7;tag.Uid.CopyTo(mcu,16);}
        SealMcu();
    }
    private void SetReadChunk(int part)
    {
        mcu=new byte[313];mcu[0]=0x3a;mcu[2]=7;mcu[3]=(byte)(part+1);
        if(part==0)
        {
            // 60 bytes of reader metadata + 245 original tag bytes.
            BinaryPrimitives.WriteUInt16BigEndian(mcu.AsSpan(5),305);
            mcu[7]=2;mcu[11]=1;mcu[12]=2;mcu[14]=7;tag!.Uid.CopyTo(mcu,15);
            if(tag.Signature.Length==32)tag.Signature.CopyTo(mcu,26);
            new byte[]{3,0,0x3b,0x3c,0x77,0x78,0x86,0,0}.CopyTo(mcu,58);
            tag.Data.AsSpan(0,245).CopyTo(mcu.AsSpan(67));
        }
        else
        {BinaryPrimitives.WriteUInt16BigEndian(mcu.AsSpan(5),0x0927);tag!.Data.AsSpan(245,295).CopyTo(mcu.AsSpan(7));}
        SealMcu();
    }
    private void SealMcu()=>mcu[^1]=Pro1NfcProtocol.Crc8(mcu.AsSpan(0,mcu.Length-1));
    private static byte[] Standard(GamepadState state,int length,byte id,long ticks,long frequency)
    {
        byte[] report=new byte[length];report[0]=id;report[1]=(byte)((ticks*200/frequency)&255);
        int level=state.BatteryPercent==GamepadState.BatteryUnknown?4:Math.Clamp((state.BatteryPercent+12)/25,0,4);
        report[2]=(byte)(level<<5|(state.BatteryCharging?0x10:0)|(state.BatteryExternalPower?1:0));
        void Bit(int index,int mask,GamepadButtons button){if(state.IsPressed(button))report[index]|=(byte)mask;}
        Bit(3,1,GamepadButtons.West);Bit(3,2,GamepadButtons.North);Bit(3,4,GamepadButtons.South);Bit(3,8,GamepadButtons.East);
        Bit(3,0x40,GamepadButtons.R1);Bit(3,0x80,GamepadButtons.R2);
        Bit(4,1,GamepadButtons.Back);Bit(4,2,GamepadButtons.Start);Bit(4,4,GamepadButtons.RightStick);Bit(4,8,GamepadButtons.LeftStick);
        Bit(4,0x10,GamepadButtons.Home);Bit(4,0x20,GamepadButtons.Capture);
        Bit(5,1,GamepadButtons.DPadDown);Bit(5,2,GamepadButtons.DPadUp);Bit(5,4,GamepadButtons.DPadRight);Bit(5,8,GamepadButtons.DPadLeft);
        Bit(5,0x40,GamepadButtons.L1);Bit(5,0x80,GamepadButtons.L2);
        PackStick(report.AsSpan(6),state.Lx,state.Ly);PackStick(report.AsSpan(9),state.Rx,state.Ry);report[12]=8;return report;
    }
    private static void PackStick(Span<byte> target,ushort x,ushort y)
    {x=Math.Min(x,(ushort)4095);y=Math.Min(y,(ushort)4095);target[0]=(byte)x;target[1]=(byte)((x>>8)|(y<<4));target[2]=(byte)(y>>4);}
    private static void WriteMotion(Span<byte> target,GamepadState state)
    {
        short Gyro(short value)=>(short)Math.Clamp(Math.Round(value*(13371.0/(936*16.384))),short.MinValue,short.MaxValue);
        short[] values=[state.AccelValid?state.AccelX:(short)0,state.AccelValid?state.AccelY:(short)0,state.AccelValid?state.AccelZ:(short)0,
            state.GyroValid?Gyro(state.GyroX):(short)0,state.GyroValid?Gyro(state.GyroY):(short)0,state.GyroValid?Gyro(state.GyroZ):(short)0];
        for(int i=0;i<6;i++)BinaryPrimitives.WriteInt16LittleEndian(target[(i*2)..],values[i]);
    }
    private static byte[] Simple(GamepadState state)
    {
        byte[] report=new byte[12];report[0]=0x3f;
        GamepadButtons[] buttons=[GamepadButtons.South,GamepadButtons.East,GamepadButtons.West,GamepadButtons.North,GamepadButtons.L1,GamepadButtons.R1,
            GamepadButtons.L2,GamepadButtons.R2,GamepadButtons.Back,GamepadButtons.Start,GamepadButtons.LeftStick,GamepadButtons.RightStick,GamepadButtons.Home,GamepadButtons.Capture];
        for(int i=0;i<buttons.Length;i++)if(state.IsPressed(buttons[i]))report[1+i/8]|=(byte)(1<<(i%8));
        bool up=state.IsPressed(GamepadButtons.DPadUp),down=state.IsPressed(GamepadButtons.DPadDown),left=state.IsPressed(GamepadButtons.DPadLeft),right=state.IsPressed(GamepadButtons.DPadRight);
        report[3]=(byte)(up?(right?1:left?7:0):down?(right?3:left?5:4):right?2:left?6:8);
        ushort[] axes=[state.Lx,state.Ly,state.Rx,state.Ry];
        for(int i=0;i<4;i++)
        {int value=Math.Clamp((int)axes[i],0,4095);int scaled=value<=2048?value*32768/2048:32768+(value-2048)*32767/2047;BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4+i*2),(ushort)scaled);}return report;
    }
    private static void ReadSpi(uint address,Span<byte> output)
    {
        output.Fill(0xff);
        byte[] factory=new byte[0x100];factory.AsSpan().Fill(0xff);
        // Calibration matches normalized shared GamepadState: sticks use the
        // full 12-bit range; accelerometer 4096/g; gyro 13371 / 936dps.
        for(int axis=0;axis<3;axis++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(factory.AsSpan(0x20+axis*2),0);
            BinaryPrimitives.WriteInt16LittleEndian(factory.AsSpan(0x26+axis*2),16384);
            BinaryPrimitives.WriteInt16LittleEndian(factory.AsSpan(0x2c+axis*2),0);
            BinaryPrimitives.WriteInt16LittleEndian(factory.AsSpan(0x32+axis*2),13371);
        }
        foreach(int block in new[]{0x3d,0x46})for(int triplet=0;triplet<3;triplet++)
            PackStick(factory.AsSpan(block+triplet*3),triplet==(block==0x3d?1:0)?(ushort)2048:(ushort)2047,triplet==(block==0x3d?1:0)?(ushort)2048:(ushort)2047);
        new byte[]{0x32,0x32,0x32,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff}.CopyTo(factory,0x50);
        Convert.FromHexString("5E010000F10F19D04CAE40E1000000000000FFFFFFFFFFFF").CopyTo(factory,0x80);
        Convert.FromHexString("19D04CAE40E1000000000000FFFFFFFFFFFF").CopyTo(factory,0x98);
        for(int i=0;i<output.Length;i++)
        {ulong at=(ulong)address+(uint)i;if(at>=0x6000&&at<0x6100)output[i]=factory[at-0x6000];}
    }
}
