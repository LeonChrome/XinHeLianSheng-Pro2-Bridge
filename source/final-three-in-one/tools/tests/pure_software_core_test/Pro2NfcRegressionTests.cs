using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class Pro2NfcRegressionTests
{
    private static readonly byte[] Uid=[4,0x12,0x34,0x56,0x78,0x90,0xab];
    private static byte[] Buffer()
    {
        byte[] buffer=new byte[600];buffer[0]=4;Uid.CopyTo(buffer,8);
        for(int i=19;i<51;i++)buffer[i]=(byte)i;
        var tag=buffer.AsSpan(60);Uid.AsSpan(0,3).CopyTo(tag);Uid.AsSpan(3).CopyTo(tag[4..]);
        tag[3]=(byte)(0x88^Uid[0]^Uid[1]^Uid[2]);tag[8]=(byte)(Uid[3]^Uid[4]^Uid[5]^Uid[6]);
        tag[12]=0xe1;tag[13]=0x10;tag[14]=0x3e;return buffer;
    }
    private static byte[] Reply(byte sub,byte[]? body=null)
    {
        body??=[];var reply=new byte[8+body.Length];reply[0]=1;reply[1]=1;reply[2]=1;reply[3]=sub;
        reply[4]=0x10;reply[5]=0x78;body.CopyTo(reply,8);return reply;
    }
    private static byte[] Status(bool reading,bool wrongUid=false)
    {
        byte[] body=new byte[61];body[0]=reading?(byte)4:(byte)9;body[8]=7;Uid.CopyTo(body,9);
        if(wrongUid)body[9]^=1;return Reply(5,body);
    }
    private static byte[] Chunk(int offset,byte[] buffer)
    {
        int count=Math.Min(70,600-offset);byte[] body=new byte[3+count];body[0]=offset==560?(byte)1:(byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1),(ushort)count);buffer.AsSpan(offset,count).CopyTo(body.AsSpan(3));return Reply(0x15,body);
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        check(Convert.ToHexString(Pro2NfcProtocol.BuildScanCommand())=="01910103000500000000002C01","Pro2 scan uses the observed open-ended discovery payload");
        check(Convert.ToHexString(Pro2NfcProtocol.BuildReadCommand(Uid))=="0191010600130000D007041234567890AB0103003B3C7778860000",
            "latest Pro2 read descriptor contains actual UID and only NTAG215 read ranges");
        var commands=new[]{Pro2NfcProtocol.BuildScanCommand(),Pro2NfcProtocol.BuildStopCommand(),
            Pro2NfcProtocol.BuildStatusCommand(),Pro2NfcProtocol.BuildReadCommand(Uid),Pro2NfcProtocol.BuildReadBufferCommand(560)};
        foreach(var command in commands)
        {
            byte[] extended=Pro2NfcProtocol.BuildExtendedPacket(command);
            check(Pro2NfcProtocol.IsReadOnlyCommand(command)&&extended.AsSpan(0,33).ToArray().All(b=>b==0)&&extended.AsSpan(33).SequenceEqual(command),
                "extended NFC packet preserves the 33-zero prefix and BLE command "+command[3]);
        }
        foreach(byte sub in new byte[]{0,1,2,7,8,0x14,0x16,0xff})
        {
            byte[] command=[1,0x91,1,sub,0,0,0,0];
            check(!Pro2NfcProtocol.IsReadOnlyCommand(command),"unverified or write NFC command rejected "+sub);
            try{Pro2NfcProtocol.BuildExtendedPacket(command);check(false,"unsafe command must throw");}catch(InvalidOperationException){check(true,"unsafe command never reaches GATT");}
        }
        byte[] changed=Pro2NfcProtocol.BuildReadCommand(Uid);changed[24]=0xff;
        check(!Pro2NfcProtocol.IsReadOnlyCommand(changed),"arbitrary page ranges cannot enter the shipped reader");
        foreach(int offset in new[]{-1,1,69,599,600,int.MaxValue})
        {try{Pro2NfcProtocol.BuildReadBufferCommand(offset);check(false,"invalid buffer offset");}catch(ArgumentOutOfRangeException){check(true,"offset is range/alignment bounded");}}
        check(Pro2NfcProtocol.TryParseStatus(Status(false),out var status)&&status.State==9&&status.Uid.SequenceEqual(Uid),"status UID parsed from captured offsets");
        object owner=new();byte[] init=[1,0x91,1,0x0c,0,0,0,0];var initAck=Reply(0x0c);
        var initWaiter=new Pro2CommandAckWaiter(owner,init);
        check(!Pro2NfcProtocol.IsReaderResponse(false,initAck)&&initWaiter.TryComplete(owner,initAck),
            "NFC response routing cannot swallow the ordinary 01/0C connection handshake ACK");
        check(Pro2NfcProtocol.IsReaderResponse(true,Status(false))&&!Pro2NfcProtocol.IsReaderResponse(true,new byte[]{1}),
            "only active NFC transactions own complete NFC response frames");
        byte[] buffer=Buffer();var tag=Pro2NfcProtocol.ParseTag(buffer,Uid);
        check(tag.Data.Length==540&&tag.Signature.SequenceEqual(buffer[19..51])&&tag.Uid.SequenceEqual(Uid),"600-byte buffer splits into tag/signature/identity without inventing data");
        for(int offset=0;offset<600;offset+=70)
        {
            var reply=Chunk(offset,buffer);
            check(Pro2NfcProtocol.TryParseChunk(reply,offset,out var data,out bool last)&&data.Length==Math.Min(70,600-offset)&&last==(offset==560),"exact Pro2 chunk framing "+offset);
            check(!Pro2NfcProtocol.TryParseChunk(reply[..^1],offset,out _,out _),"truncated chunk rejected "+offset);
            reply[8]^=1;check(!Pro2NfcProtocol.TryParseChunk(reply,offset,out _,out _),"premature/missing last flag rejected "+offset);
        }
        foreach(int corrupt in new[]{8,60,63,68,74})
        {
            byte[] bad=(byte[])buffer.Clone();bad[corrupt]^=1;
            try{Pro2NfcProtocol.ParseTag(bad,Uid);check(false,"corrupt identity/BCC/capacity");}catch(IOException){check(true,"corrupt card never exported "+corrupt);}
        }
        var v3=(byte[])buffer.Clone();v3[18]=6;
        try{Pro2NfcProtocol.ParseTag(v3,Uid);check(false,"partial 2K must not be a full dump");}catch(NotSupportedException){check(true,"2KB partial read is explicitly unsupported");}
        await Replay(check,"success");await Replay(check,"chunk_short");await Replay(check,"uid_changed");
        await Replay(check,"nack");await Replay(check,"timeout");await Replay(check,"stop_failure");await Replay(check,"cancel");
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();int sent=0;
            try{await new Pro2NfcReader((_,_)=>{sent++;return Task.FromResult(Reply(4));}).ReadAsync(cancelled.Token);check(false,"pre-cancelled read");}
            catch(OperationCanceledException){check(sent==0,"pre-cancelled NFC operation sends no command");}
        }
        await using var source=new Pro2BleInputSource();
        check(!source.CanReadNfc&&!source.IsReadingNfc,"disconnected BLE exposes no enabled NFC action");
        try{await source.ReadNfcAsync(default);check(false,"disconnected read");}catch(NotSupportedException){check(true,"disconnected read sends no commands");}
        var reading=typeof(Pro2BleInputSource).GetField("nfcReading",BindingFlags.Instance|BindingFlags.NonPublic)!;
        reading.SetValue(source,true);
        check(!source.IsOutputReady&&!source.CanOptimizeConnectionCompatibility&&!source.CanAutomaticallyOptimizeConnectionCompatibility,
            "NFC claim blocks rumble and both parameter-optimization paths");
        reading.SetValue(source,false);
    }
    private static async Task Replay(Action<bool,string> check,string failure)
    {
        bool reading=false;int chunks=0,stops=0;var seen=new List<byte>();var buffer=Buffer();
        using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        Task<byte[]> Exchange(byte[] command,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();seen.Add(command[3]);
            check(Pro2NfcProtocol.IsReadOnlyCommand(command),"reader emits whitelist commands only "+failure);
            if(command[3]==4)
            {
                stops++;
                if(failure=="stop_failure"&&chunks==9)throw new IOException("simulated stop failure");
                return Task.FromResult(Reply(4));
            }
            if(failure=="timeout"&&command[3]==3)throw new TimeoutException("simulated no response");
            if(failure=="nack"&&command[3]==3){var nack=Reply(3);nack[5]=0;return Task.FromResult(nack);}
            if(failure=="cancel"&&command[3]==3){cts.Cancel();ct.ThrowIfCancellationRequested();}
            if(command[3]==5)return Task.FromResult(Status(reading,failure=="uid_changed"&&chunks==9));
            if(command[3]==6){reading=true;return Task.FromResult(Reply(6));}
            if(command[3]==0x15)
            {
                chunks++;var reply=Chunk(BinaryPrimitives.ReadUInt16LittleEndian(command.AsSpan(8)),buffer);
                return Task.FromResult(failure=="chunk_short"&&chunks==3?reply[..^1]:reply);
            }
            return Task.FromResult(Reply(command[3]));
        }
        try
        {
            var result=await new Pro2NfcReader(Exchange).ReadAsync(cts.Token);
            check(failure=="success"&&result.Data.SequenceEqual(buffer[60..])&&chunks==9,"physical-read replay returns exactly 540 bytes");
        }
        catch(Exception error)when(error is IOException or TimeoutException or OperationCanceledException)
        {check(failure!="success","failing replay cannot return a tag: "+failure);}
        check(stops>=2&&seen[^1]==4,"success, failure and cancellation always attempt stop: "+failure);
        check(!seen.Contains(8)&&!seen.Contains(0x14),"reader never emits tag-write commands: "+failure);
    }
}
