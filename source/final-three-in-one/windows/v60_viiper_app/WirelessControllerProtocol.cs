using System.Buffers.Binary;
using System.Diagnostics;

namespace Y700Switch2V60Viiper;

public enum WirelessPadMode { Pro1, Xbox, Ps5 }
public static class WirelessPadModes
{
    public static WirelessPadMode FromVirtual(ViiperVirtualMode mode)=>mode switch
    {ViiperVirtualMode.Xbox or ViiperVirtualMode.XboxProfessionalImuTest=>WirelessPadMode.Xbox,ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest=>WirelessPadMode.Ps5,ViiperVirtualMode.Pro1=>WirelessPadMode.Pro1,_=>throw new NotSupportedException("原生 Pro2 蓝牙外发尚未实现，请选择 Pro1、Xbox 或 PS5。")};
    public static string Argument(WirelessPadMode mode)=>mode.ToString().ToLowerInvariant();
    public static string Label(WirelessPadMode mode)=>mode switch{WirelessPadMode.Ps5=>"PS5 / DualSense 蓝牙",WirelessPadMode.Xbox=>"Xbox One S 蓝牙",_=>"Pro1 / Nintendo"};
}

// Bluetooth HID wire packets. The existing USB/VIIPER API is NOT a Bluetooth
// packet format. PS5 IMU conversion reuses the existing calibrated converter.
public sealed class WirelessControllerProtocol
{
    private readonly WirelessPadMode mode;
    private byte sequence;
    private bool? previousGuide;
    private byte? previousBattery;
    public WirelessControllerProtocol(WirelessPadMode mode){this.mode=mode;}
    public byte[]? AuxiliaryInput {get;private set;}
    public byte[]? BatteryInput {get;private set;}
    public byte[] BuildInput(GamepadState state,long ticks)
    {
        AuxiliaryInput=null;BatteryInput=null;
        if(mode==WirelessPadMode.Ps5)return Ps5(state,ticks);
        if(mode!=WirelessPadMode.Xbox)throw new InvalidOperationException("Pro1 uses its MCU-aware protocol engine.");
        byte[] p=new byte[17];p[0]=1;
        ushort Axis(ushort value,bool invert){int raw=Math.Clamp((int)value,0,4095);int v=raw<=2048?raw*32768/2048:32768+(raw-2048)*32767/2047;return (ushort)(invert?65535-v:v);}
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1),Axis(state.Lx,false));BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3),Axis(state.Ly,true));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(5),Axis(state.Rx,false));BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(7),Axis(state.Ry,true));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(9),(ushort)(state.IsPressed(GamepadButtons.L2)?1023:Math.Clamp((int)state.L2,0,4095)*1023/4095));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(11),(ushort)(state.IsPressed(GamepadButtons.R2)?1023:Math.Clamp((int)state.R2,0,4095)*1023/4095));
        int hat=Hat(state);p[13]=(byte)(hat==8?0:hat+1);
        void Bit(int i,int bit,GamepadButtons button){if(state.IsPressed(button))p[i]|=(byte)bit;}
        Bit(14,1,GamepadButtons.South);Bit(14,2,GamepadButtons.East);Bit(14,8,GamepadButtons.West);Bit(14,16,GamepadButtons.North);
        Bit(14,64,GamepadButtons.L1);Bit(14,128,GamepadButtons.R1);Bit(15,4,GamepadButtons.Back);Bit(15,8,GamepadButtons.Start);
        Bit(15,32,GamepadButtons.LeftStick);Bit(15,64,GamepadButtons.RightStick);Bit(16,1,GamepadButtons.Back);
        bool guide=state.IsPressed(GamepadButtons.Home);
        if(previousGuide!=guide){previousGuide=guide;AuxiliaryInput=[2,guide?(byte)1:(byte)0];}
        if(state.BatteryPercent!=GamepadState.BatteryUnknown)
        {
            byte battery=(byte)(Math.Clamp((int)state.BatteryPercent,0,100)*255/100);
            if(previousBattery!=battery){previousBattery=battery;BatteryInput=[4,battery];}
        }
        return p;
    }
    private byte[] Ps5(GamepadState state,long ticks)
    {
        byte[] p=new byte[78];p[0]=0x31;p[1]=(byte)((sequence&15)<<4);
        byte[] common=VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike,state);
        for(int i=0;i<4;i++)p[2+i]=(byte)(unchecked((sbyte)common[i])+128);
        p[6]=common[9];p[7]=common[10];p[8]=sequence++;
        p[9]=(byte)Hat(state);
        void Bit(int i,int bit,GamepadButtons button){if(state.IsPressed(button))p[i]|=(byte)bit;}
        Bit(9,16,GamepadButtons.West);Bit(9,32,GamepadButtons.South);Bit(9,64,GamepadButtons.East);Bit(9,128,GamepadButtons.North);
        Bit(10,1,GamepadButtons.L1);Bit(10,2,GamepadButtons.R1);Bit(10,4,GamepadButtons.L2);Bit(10,8,GamepadButtons.R2);
        Bit(10,16,GamepadButtons.Back);Bit(10,32,GamepadButtons.Start);Bit(10,64,GamepadButtons.LeftStick);Bit(10,128,GamepadButtons.RightStick);
        Bit(11,1,GamepadButtons.Home);Bit(11,2,GamepadButtons.Capture);
        common.AsSpan(21,12).CopyTo(p.AsSpan(17));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(29),unchecked((uint)(ticks*(3000000.0/Stopwatch.Frequency))));
        int x=Math.Clamp((int)state.Ps5Touch.X,0,1919),y=Math.Clamp((int)state.Ps5Touch.Y,0,1079);
        p[34]=state.Ps5Touch.Active?(byte)0:(byte)0x80;p[35]=(byte)x;p[36]=(byte)((x>>8)|((y&15)<<4));p[37]=(byte)(y>>4);p[38]=0x80;
        int battery=Math.Clamp(state.BatteryPercent/10,0,10);
        p[54]=state.BatteryPercent==GamepadState.BatteryUnknown?(byte)0xf0:(byte)(battery|(state.BatteryCharging?0x10:state.BatteryExternalPower&&state.BatteryPercent>=100?0x20:0));
        WriteCrc(p,0xa1);return p;
    }
    public static int Hat(GamepadState state)
    {
        bool u=state.IsPressed(GamepadButtons.DPadUp),d=state.IsPressed(GamepadButtons.DPadDown),l=state.IsPressed(GamepadButtons.DPadLeft),r=state.IsPressed(GamepadButtons.DPadRight);
        return u&&!d?(r&&!l?1:l&&!r?7:0):d&&!u?(r&&!l?3:l&&!r?5:4):r&&!l?2:l&&!r?6:8;
    }
    public bool TryFeedback(ReadOnlySpan<byte> report,out Pro2OutputPacket? packet,out int durationMs,out string detail)
    {
        packet=null;durationMs=0;detail="";
        if(mode==WirelessPadMode.Ps5)
        {
            if(report.Length!=78||report[0]!=0x31){detail="PS5 Bluetooth output requires report 31 / 78 bytes; audio streams are not ordinary rumble";return false;}
            if(!VerifyCrc(report,0xa2)){detail="PS5 Bluetooth output CRC mismatch";return false;}
            if(report[2]!=0x10){detail="PS5 Bluetooth output tag mismatch";return false;}
            if((report[3]&1)==0){detail="PS5 control-only output; no compatible motor update";return false;}
            packet=Pro2OutputPacketMapper.BuildOrdinaryPacket(report[5],report[6],"wireless-ps5-compatible-rumble");return true;
        }
        if(mode==WirelessPadMode.Xbox)
        {
            if(report.Length!=9||report[0]!=3){detail="Xbox Bluetooth output requires report 03 / 9 bytes";return false;}
            // Classic Xbox motor-enable bits run from right main motor (bit 0)
            // to left trigger (bit 3). Do not treat trigger flags as main motors.
            int strong=(report[1]&2)!=0?Math.Clamp((int)report[4],0,100):0,weak=(report[1]&1)!=0?Math.Clamp((int)report[5],0,100):0;
            packet=Pro2OutputPacketMapper.BuildOrdinaryPacket((byte)(weak*255/100),(byte)(strong*255/100),"wireless-xbox-rumble");
            durationMs=report[6]*10;detail="trigger_motors_not_present=true delay_units="+report[7]+" loop_count="+report[8];return true;
        }
        detail="Pro1 feedback uses its native HD rumble decoder";return false;
    }
    public static uint Crc(ReadOnlySpan<byte> data,byte seed)
    {
        uint value=0xffffffff;
        void Byte(byte b){value^=b;for(int i=0;i<8;i++)value=(value>>1)^((value&1)!=0?0xedb88320u:0);}
        Byte(seed);foreach(byte b in data)Byte(b);return ~value;
    }
    public static void WriteCrc(Span<byte> report,byte seed)=>BinaryPrimitives.WriteUInt32LittleEndian(report[^4..],Crc(report[..^4],seed));
    public static bool VerifyCrc(ReadOnlySpan<byte> report,byte seed)=>report.Length>=4&&BinaryPrimitives.ReadUInt32LittleEndian(report[^4..])==Crc(report[..^4],seed);
}
