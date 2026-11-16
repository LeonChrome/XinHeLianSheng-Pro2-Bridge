using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Y700Switch2V60Viiper;

public enum WirelessRelayMessage : byte { FreshInput=1, ProtocolReply=2, HostOutput=3, LinkState=4, Heartbeat=5, Stop=6, BackendReady=7 }
public sealed record WirelessRelayFrame(WirelessRelayMessage Kind,ulong Sequence,long Timestamp,byte[] Payload);

// Authenticated loopback IPC for the optional, independently launched radio
// backend. No unauthenticated UDP input is ever turned into a controller report.
public static class WirelessRelayDatagram
{
    public const int HeaderLength=48,MaxPayload=362;
    public static byte[] Encode(ReadOnlySpan<byte> session,WirelessRelayMessage kind,ulong sequence,long timestamp,ReadOnlySpan<byte> payload)
    {
        if(session.Length!=16||payload.Length>MaxPayload||!Enum.IsDefined(kind))throw new ArgumentException("无线转发 IPC 字段无效。");
        byte[] packet=new byte[HeaderLength+payload.Length];"PRW1"u8.CopyTo(packet);packet[4]=1;packet[5]=(byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6),checked((ushort)payload.Length));session.CopyTo(packet.AsSpan(8));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24),sequence);BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(32),timestamp);
        payload.CopyTo(packet.AsSpan(HeaderLength));return packet;
    }
    public static bool TryDecode(ReadOnlySpan<byte> packet,ReadOnlySpan<byte> session,out WirelessRelayFrame? frame)
    {
        frame=null;
        if(session.Length!=16||packet.Length<HeaderLength||!packet[..4].SequenceEqual("PRW1"u8)||packet[4]!=1||
            !CryptographicOperations.FixedTimeEquals(packet.Slice(8,16),session))return false;
        int length=BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        if(length>MaxPayload||packet.Length!=HeaderLength+length||!Enum.IsDefined((WirelessRelayMessage)packet[5]))return false;
        foreach(byte reserved in packet.Slice(40,8))if(reserved!=0)return false;
        frame=new((WirelessRelayMessage)packet[5],BinaryPrimitives.ReadUInt64LittleEndian(packet[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(packet[32..]),packet[HeaderLength..].ToArray());return true;
    }
}
