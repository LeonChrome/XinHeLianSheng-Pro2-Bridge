using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Wire observations and read sequence: PicoSwitch2 v2.6.0 (Apache-2.0),
// tools/nfc_probe.ps1 and docs/experiments/pro2-native-nfc-read-2026-07-25.md.
// This codec exposes no arbitrary command, tag write or firmware operation.
public sealed record Pro2NfcStatus(byte State, byte Detail, byte[] Uid);
public sealed record Pro2NfcReadResult(byte[] Uid, byte[] Data, byte[] Signature);
public static class Pro2NfcProtocol
{
    public const int TagLength = 540, BufferLength = 600;
    public static readonly Guid ExtendedWriteUuid = new("3dacbc7e-6955-40b5-8eaf-6f9809e8b379");
    public static readonly Guid SecondaryReplyUuid = new("506d9f7d-4278-4e95-a549-326ba77657e0");
    private static byte[] Command(byte sub, params byte[] payload)
    {
        byte[] result=new byte[8+payload.Length];
        result[0]=1;result[1]=0x91;result[2]=1;result[3]=sub;result[5]=(byte)payload.Length;
        payload.CopyTo(result,8);return result;
    }
    public static byte[] BuildScanCommand() => Command(3,0,0,0,0x2c,1);
    public static byte[] BuildStatusCommand() => Command(5);
    public static byte[] BuildStopCommand() => Command(4);
    public static byte[] BuildReadCommand(ReadOnlySpan<byte> uid)
    {
        if(uid.Length!=7||uid[0]!=4)throw new ArgumentException("需要有效的七字节 NTAG UID。",nameof(uid));
        byte[] payload=new byte[19];payload[0]=0xd0;payload[1]=7;uid.CopyTo(payload.AsSpan(2));
        payload[9]=1;payload[10]=3;
        new byte[]{0,0x3b,0x3c,0x77,0x78,0x86,0,0}.CopyTo(payload,11);
        return Command(6,payload);
    }
    public static byte[] BuildReadBufferCommand(int offset)
    {
        if(offset<0||offset>=BufferLength)throw new ArgumentOutOfRangeException(nameof(offset));
        return Command(0x15,(byte)offset,(byte)(offset>>8));
    }
    public static bool IsReadOnlyCommand(ReadOnlySpan<byte> command)
    {
        if(command.Length<8||command[0]!=1||command[1]!=0x91||command[2]!=1||command[4]!=0||
            command[6]!=0||command[7]!=0||command[5]!=command.Length-8)return false;
        return command[3] switch
        {
            3=>command.SequenceEqual(BuildScanCommand()),
            4 or 5=>command.Length==8,
            6=>command.Length==27&&command[10]==4&&command.SequenceEqual(BuildReadCommand(command.Slice(10,7))),
            0x15=>command.Length==10&&BinaryPrimitives.ReadUInt16LittleEndian(command[8..])<BufferLength,
            _=>false
        };
    }
    public static byte[] BuildExtendedPacket(ReadOnlySpan<byte> command)
    {
        if(!IsReadOnlyCommand(command))throw new InvalidOperationException("只允许已定义的 NFC 只读命令。");
        byte[] framed=new byte[33+command.Length];command.CopyTo(framed.AsSpan(33));return framed;
    }
    // The normal connection handshake also uses command family 0x01.
    // Only an explicitly active read owns this response route.
    public static bool IsReaderResponse(bool reading,ReadOnlySpan<byte> response)=>
        reading&&response.Length>=8&&response[0]==1;
    public static bool MatchesResponse(ReadOnlySpan<byte> request,ReadOnlySpan<byte> response) =>
        request.Length>=8&&response.Length>=8&&response.Length<=512&&response[0]==1&&
        response[1]==1&&response[2]==1&&response[3]==request[3];
    public static void RequireSuccess(ReadOnlySpan<byte> request,ReadOnlySpan<byte> response)
    {
        if(!MatchesResponse(request,response))
            throw new IOException("Pro2 NFC 应答格式或命令不匹配；未返回标签文件。");
        if(response[5] is not (0x78 or 0xf8))
            throw new IOException("Pro2 拒绝 NFC 命令 01/"+request[3].ToString("X2")+"（状态 0x"+response[5].ToString("X2")+"）；未返回标签文件。");
    }
    public static bool TryParseStatus(ReadOnlySpan<byte> response,out Pro2NfcStatus status)
    {
        status=new(0,0,[]);
        if(!MatchesResponse(BuildStatusCommand(),response)||response[5] is not (0x78 or 0xf8)||response.Length<10)return false;
        byte[] uid=response.Length>=24&&response[16]==7?response.Slice(17,7).ToArray():[];
        status=new(response[8],response[9],uid);return true;
    }
    public static bool TryParseChunk(ReadOnlySpan<byte> response,int offset,out byte[] data,out bool last)
    {
        data=[];last=false;
        if(offset<0||offset>=BufferLength||response.Length<11||
            !MatchesResponse(BuildReadBufferCommand(offset),response)||response[5] is not (0x78 or 0xf8))return false;
        int length=BinaryPrimitives.ReadUInt16LittleEndian(response[9..]);last=(response[8]&1)!=0;
        // Genuine firmware can return a different chunk size or a longer frame.
        // Advance by the declared payload, never by an assumed 70-byte block or
        // by the notification length. The upstream probe likewise excludes any
        // bytes outside that declared span; only flag bit 0 denotes the end.
        if(length<=0||length>BufferLength-offset||response.Length<11+length||
            last!=(offset+length==BufferLength))return false;
        data=response.Slice(11,length).ToArray();return true;
    }
    public static Pro2NfcReadResult ParseTag(ReadOnlySpan<byte> buffer,ReadOnlySpan<byte> expectedUid)
    {
        if(buffer.Length!=BufferLength||expectedUid.Length!=7||!buffer.Slice(8,7).SequenceEqual(expectedUid))
            throw new IOException("标签长度或 UID 不一致，未导出不完整数据。");
        if(buffer[18]==6)throw new NotSupportedException("当前只支持 NTAG215；2KB 标签的完整读取尚未接入。");
        ReadOnlySpan<byte> data=buffer[60..];
        if(!Pro1NfcProtocol.IsNtag215(data,expectedUid)||data[14]!=0x3e)
            throw new IOException("标签不符合 NTAG215 容量或 UID/BCC 校验，未作为完整文件导出。");
        return new(expectedUid.ToArray(),data.ToArray(),buffer.Slice(19,32).ToArray());
    }
}

public sealed class Pro2NfcReader(
    Func<byte[],CancellationToken,Task<byte[]>> exchange,Action<string>? progress=null)
{
    public async Task<Pro2NfcReadResult> ReadAsync(CancellationToken token)
    {
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(25));
        CancellationToken ct=bounded.Token;bool completed=false,started=false;
        async Task<byte[]> Send(byte[] command,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            started=true;
            byte[] reply=await exchange(command,cancellation).ConfigureAwait(false);
            Pro2NfcProtocol.RequireSuccess(command,reply);return reply;
        }
        async Task<Pro2NfcStatus> Status()
        {
            byte[] reply=await Send(Pro2NfcProtocol.BuildStatusCommand(),ct).ConfigureAwait(false);
            if(!Pro2NfcProtocol.TryParseStatus(reply,out var status))throw new IOException("NFC 状态应答长度或格式错误。");
            return status;
        }
        try
        {
            progress?.Invoke("等待标签 · 请贴近手柄顶部 NFC 区");
            byte[] uid=[];
            // Re-arm a bounded discovery cycle instead of polling an expired scan.
            for(int cycle=0;cycle<15&&uid.Length==0;cycle++)
            {
                await Send(Pro2NfcProtocol.BuildStopCommand(),ct).ConfigureAwait(false);
                await Send(Pro2NfcProtocol.BuildScanCommand(),ct).ConfigureAwait(false);
                for(int poll=0;poll<9;poll++)
                {
                    var status=await Status().ConfigureAwait(false);
                    if(status.State==9)
                    {
                        if(status.Uid.Length!=7)throw new NotSupportedException("已检测到标签，本版只支持七字节 UID 的 NTAG215。");
                        uid=status.Uid;break;
                    }
                    await Task.Delay(100,ct).ConfigureAwait(false);
                }
            }
            if(uid.Length==0)throw new TimeoutException("未检测到标签，请贴近 NFC 区后重试。");
            progress?.Invoke("已发现标签 · 正在读取 NTAG215");
            await Send(Pro2NfcProtocol.BuildReadCommand(uid),ct).ConfigureAwait(false);
            bool ready=false;Pro2NfcStatus? lastState=null;
            long operationStarted=Stopwatch.GetTimestamp();
            // The descriptor itself permits 2000ms. The old ten 100ms polls
            // could abort a healthy pending operation before that elapsed.
            while(Stopwatch.GetElapsedTime(operationStarted).TotalSeconds<4)
            {
                var status=await Status().ConfigureAwait(false);
                lastState=status;
                if(status.Uid.Length==7&&!status.Uid.SequenceEqual(uid))throw new IOException("等待读取时标签 UID 改变。");
                if(status.State==4){ready=true;break;}
                if(status.State==7&&status.Detail==0x41)throw new IOException("读取时标签已移开。");
                await Task.Delay(100,ct).ConfigureAwait(false);
            }
            if(!ready)throw new IOException("NFC 读取未在等待窗口内完成（状态 0x"+
                (lastState?.State.ToString("X2")??"--")+"，详情 0x"+(lastState?.Detail.ToString("X2")??"--")+"），请保持标签贴稳后重试。");
            byte[] buffer=new byte[Pro2NfcProtocol.BufferLength];
            for(int offset=0;offset<buffer.Length;)
            {
                byte[] reply=await Send(Pro2NfcProtocol.BuildReadBufferCommand(offset),ct).ConfigureAwait(false);
                if(!Pro2NfcProtocol.TryParseChunk(reply,offset,out var data,out _))
                    throw new IOException("NFC 分块格式或长度不一致（偏移 "+offset+"，应答 "+reply.Length+" 字节）；未保存文件。");
                data.CopyTo(buffer,offset);offset+=data.Length;
                progress?.Invoke("读取数据 · "+offset+" / 600 字节");
            }
            var finalStatus=await Status().ConfigureAwait(false);
            if(finalStatus.State!=4||(finalStatus.Uid.Length==7&&!finalStatus.Uid.SequenceEqual(uid)))
                throw new IOException("读取完成前标签状态或 UID 改变，未保存文件。");
            var result=Pro2NfcProtocol.ParseTag(buffer,uid);completed=true;return result;
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested)
        {throw new TimeoutException("NFC 读取达到 25 秒上限，已停止本次读取。");}
        finally
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try{if(started)await Send(Pro2NfcProtocol.BuildStopCommand(),cleanup.Token).ConfigureAwait(false);}
            catch(Exception error)
            {
                progress?.Invoke("停止扫描未确认 · 请重新连接手柄");
                if(completed)throw new IOException("标签已读取，但停止扫描未确认；请重新连接后再试。",error);
            }
        }
    }
}
