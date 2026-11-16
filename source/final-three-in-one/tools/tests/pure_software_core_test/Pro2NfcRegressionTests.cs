using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class Pro2NfcRegressionTests
{
    private static readonly byte[] Uid=[4,0x12,0x34,0x56,0x78,0x90,0xab];
    private static byte[] Buffer(bool amiibo=false)
    {
        byte[] buffer=new byte[600];buffer[0]=4;Uid.CopyTo(buffer,8);
        for(int i=19;i<51;i++)buffer[i]=(byte)i;
        var tag=buffer.AsSpan(60);Uid.AsSpan(0,3).CopyTo(tag);Uid.AsSpan(3).CopyTo(tag[4..]);
        tag[3]=(byte)(0x88^Uid[0]^Uid[1]^Uid[2]);tag[8]=(byte)(Uid[3]^Uid[4]^Uid[5]^Uid[6]);
        if(amiibo)new byte[]{0xf1,0x10,0xff,0xee}.CopyTo(buffer,72);
        else{tag[12]=0xe1;tag[13]=0x10;tag[14]=0x3e;}
        return buffer;
    }
    private static byte[] Reply(byte sub,byte[]? body=null)
    {
        body??=[];var reply=new byte[8+body.Length];reply[0]=1;reply[1]=1;reply[2]=1;reply[3]=sub;
        reply[4]=0x10;reply[5]=0x78;body.CopyTo(reply,8);return reply;
    }
    private static byte[] Status(bool reading,bool wrongUid=false,byte? state=null,byte detail=0)
    {
        byte[] body=new byte[61];body[0]=state??(reading?(byte)4:(byte)9);body[1]=detail;body[8]=7;Uid.CopyTo(body,9);
        if(wrongUid)body[10]^=1;return Reply(5,body);
    }
    private static byte[] Chunk(int offset,byte[] buffer,int maximum=70,int paddedFrameLength=0)
    {
        int count=Math.Min(maximum,600-offset);byte[] body=new byte[3+count];body[0]=offset+count==600?(byte)1:(byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1),(ushort)count);buffer.AsSpan(offset,count).CopyTo(body.AsSpan(3));
        byte[] reply=Reply(0x15,body);if(paddedFrameLength>reply.Length)Array.Resize(ref reply,paddedFrameLength);return reply;
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        check(Convert.ToHexString(Pro2NfcProtocol.BuildScanCommand())=="01910103000500000000002C01","Pro2 scan uses the observed open-ended discovery payload");
        check(Convert.ToHexString(Pro2NfcProtocol.BuildReadCommand(Uid))=="0191010600130000D007000000000000000103003B3C7778860000",
            "default read descriptor uses the captured console zero-UID read form");
        check(Convert.ToHexString(Pro2NfcProtocol.BuildReadCommand(Uid,Pro2NfcReadDescriptorMode.UidBound))=="0191010600130000D007041234567890AB0103003B3C7778860000",
            "UID-bound fallback preserves the upstream active probe read form");
        var commands=new[]{Pro2NfcProtocol.BuildScanCommand(),Pro2NfcProtocol.BuildStopCommand(),
            Pro2NfcProtocol.BuildStatusCommand(),Pro2NfcProtocol.BuildReadCommand(Uid),
            Pro2NfcProtocol.BuildReadCommand(Uid,Pro2NfcReadDescriptorMode.UidBound),Pro2NfcProtocol.BuildReadBufferCommand(560)};
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
        byte[] partialUid=Pro2NfcProtocol.BuildReadCommand(Uid);partialUid[11]=1;
        check(!Pro2NfcProtocol.IsReadOnlyCommand(partialUid),"partial zero UID is not an allowed read descriptor");
        foreach(int offset in new[]{-1,600,65535,int.MaxValue})
        {try{Pro2NfcProtocol.BuildReadBufferCommand(offset);check(false,"invalid buffer offset");}catch(ArgumentOutOfRangeException){check(true,"offset cannot escape the 600-byte read-only buffer");}}
        foreach(int offset in new[]{0,1,69,84,588,599})
        {
            byte[] read=Pro2NfcProtocol.BuildReadBufferCommand(offset);
            check(Pro2NfcProtocol.IsReadOnlyCommand(read)&&BinaryPrimitives.ReadUInt16LittleEndian(read.AsSpan(8))==offset,
                "next read offset can follow actual chunk length "+offset);
        }
        check(Pro2NfcProtocol.TryParseStatus(Status(false),out var status)&&status.State==9&&status.Uid.SequenceEqual(Uid),"status UID parsed from captured offsets");
        object owner=new();byte[] init=[1,0x91,1,0x0c,0,0,0,0];var initAck=Reply(0x0c);
        var initWaiter=new Pro2CommandAckWaiter(owner,init);
        check(!Pro2NfcProtocol.IsReaderResponse(false,initAck)&&initWaiter.TryComplete(owner,initAck),
            "NFC response routing cannot swallow the ordinary 01/0C connection handshake ACK");
        check(Pro2NfcProtocol.IsReaderResponse(true,Status(false))&&!Pro2NfcProtocol.IsReaderResponse(true,new byte[]{1}),
            "only active NFC transactions own complete NFC response frames");
        byte[] buffer=Buffer();var tag=Pro2NfcProtocol.ParseTag(buffer,Uid);
        check(tag.Data.Length==540&&tag.Signature.SequenceEqual(buffer[19..51])&&tag.Uid.SequenceEqual(Uid),"600-byte buffer splits into tag/signature/identity without inventing data");
        byte[] native=Buffer(true);var nativeCheck=Pro2NfcProtocol.InspectTag(native,Uid);
        check(nativeCheck.IsValid&&nativeCheck.Format=="amiibo_ntag215"&&nativeCheck.Page3=="F110FFEE",
            "native amiibo page 3 is recognized independently of blank NDEF capacity marker");
        check(Pro2NfcProtocol.ParseTag(native,Uid).Data.SequenceEqual(native[60..]),"amiibo header and tag bytes are exported unchanged");
        check(Pro2NfcProtocol.InspectTag(buffer,Uid).Format=="ndef_ntag215","plain NTAG215 remains supported");
        byte[] locked=(byte[])buffer.Clone();locked[75]=0x0f;
        check(Pro2NfcProtocol.InspectTag(locked,Uid).IsValid,"read-only NDEF NTAG215 access byte remains readable");
        byte[] badBcc0=(byte[])native.Clone();badBcc0[63]^=1;
        var bcc0Check=Pro2NfcProtocol.InspectTag(badBcc0,Uid);
        check(!bcc0Check.IsValid&&!bcc0Check.Bcc0Valid&&bcc0Check.Bcc1Valid&&bcc0Check.DataUidMatches,
            "amiibo recognition does not bypass BCC0");
        byte[] badBcc1=(byte[])native.Clone();badBcc1[68]^=1;
        check(!Pro2NfcProtocol.InspectTag(badBcc1,Uid).Bcc1Valid,"amiibo recognition does not bypass BCC1");
        byte[] otherUid=(byte[])native.Clone();otherUid[61]^=1;
        otherUid[63]=(byte)(0x88^otherUid[60]^otherUid[61]^otherUid[62]);
        var otherUidCheck=Pro2NfcProtocol.InspectTag(otherUid,Uid);
        check(!otherUidCheck.IsValid&&!otherUidCheck.DataUidMatches&&otherUidCheck.Bcc0Valid,
            "a self-consistent but different UID cannot be exported for the detected tag");
        foreach(var invalid in new[]{badBcc0,badBcc1,otherUid})
        {try{Pro2NfcProtocol.ParseTag(invalid,Uid);check(false,"invalid native tag");}catch(IOException){check(true,"native tag integrity failures still block export");}}
        check(!Pro2NfcProtocol.InspectTag(native[..^1],Uid).IsValid&&!Pro2NfcProtocol.InspectTag(native,Uid[..6]).IsValid,
            "incomplete buffer or UID cannot pass format validation");
        check(!nativeCheck.Telemetry.Contains(Convert.ToHexString(Uid))&&nativeCheck.Telemetry.Contains("page3=F110FFEE"),
            "validation diagnostics distinguish the format without disclosing UID or tag payload");
        for(int offset=0;offset<600;offset+=70)
        {
            var reply=Chunk(offset,buffer);
            check(Pro2NfcProtocol.TryParseChunk(reply,offset,out var data,out bool last)&&data.Length==Math.Min(70,600-offset)&&last==(offset==560),"exact Pro2 chunk framing "+offset);
            check(!Pro2NfcProtocol.TryParseChunk(reply[..^1],offset,out _,out _),"truncated chunk rejected "+offset);
            reply[8]^=1;check(!Pro2NfcProtocol.TryParseChunk(reply,offset,out _,out _),"premature/missing last flag rejected "+offset);
        }
        for(int offset=0;offset<600;offset+=84)
        {
            check(Pro2NfcProtocol.TryParseChunk(Chunk(offset,buffer,84),offset,out var data,out bool last)&&
                data.SequenceEqual(buffer[offset..Math.Min(offset+84,600)])&&last==(offset+data.Length==600),
                "84-byte chunks (95-byte full frames) retain declared data and final boundary "+offset);
        }
        var padded=Chunk(0,buffer,70,95);
        check(Pro2NfcProtocol.TryParseChunk(padded,0,out var unpadded,out _)&&unpadded.SequenceEqual(buffer[..70]),
            "95-byte frame with declared 70-byte data accepts only its zero transport padding");
        padded[^1]=0x5a;
        check(Pro2NfcProtocol.TryParseChunk(padded,0,out var declaredOnly,out _)&&declaredOnly.SequenceEqual(buffer[..70]),
            "bytes beyond declared payload are excluded even when the transport tail is nonzero");
        var reservedFlags=Chunk(0,buffer);reservedFlags[8]|=0x80;
        check(Pro2NfcProtocol.TryParseChunk(reservedFlags,0,out _,out bool reservedLast)&&!reservedLast,
            "only flag bit zero is the documented end marker");
        var empty=Chunk(0,buffer);empty[9]=empty[10]=0;
        check(!Pro2NfcProtocol.TryParseChunk(empty,0,out _,out _),"zero-length chunk cannot cause an infinite offset loop");
        var overflow=Chunk(588,buffer,84);overflow[9]=84;
        check(!Pro2NfcProtocol.TryParseChunk(overflow,588,out _,out _),"declared chunk cannot run past NTAG215 buffer end");
        foreach(int corrupt in new[]{8,60,63,68,74})
        {
            byte[] bad=(byte[])buffer.Clone();bad[corrupt]^=1;
            try{Pro2NfcProtocol.ParseTag(bad,Uid);check(false,"corrupt identity/BCC/capacity");}catch(IOException){check(true,"corrupt card never exported "+corrupt);}
        }
        var v3=(byte[])buffer.Clone();v3[18]=6;
        try{Pro2NfcProtocol.ParseTag(v3,Uid);check(false,"partial 2K must not be a full dump");}catch(NotSupportedException){check(true,"2KB partial read is explicitly unsupported");}
        await Replay(check,"success");await Replay(check,"primary_ack");await Replay(check,"chunk_short");await Replay(check,"uid_changed");
        await Replay(check,"nack");await Replay(check,"timeout");await Replay(check,"stop_failure");await Replay(check,"cancel");
        await Replay(check,"chunk_84");await Replay(check,"padded_95");await Replay(check,"busy_delayed");
        await Replay(check,"busy_timeout");await Replay(check,"removed");
        await Replay(check,"amiibo");await Replay(check,"amiibo_84");await Replay(check,"amiibo_bad_bcc");
        await Replay(check,"terminal_3e_success");await Replay(check,"terminal_3d_success");
        await Replay(check,"terminal_all");await Replay(check,"read_nack_fallback");
        await Replay(check,"changed_after_error");await Replay(check,"timeout_on_read");await Replay(check,"cancel_on_read");
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
        var recent=new RecentSessionStatus();
        string[] nfcEvidence=[
            "[14:17:48.756] [NFC_UI] slot=1 action=read tag_writes=0",
            "[14:17:50.273] [PRO2_NFC_CHANNEL] route=existing_primary_ack auxiliary_cccd_reads=0 auxiliary_cccd_writes=0",
            "[14:17:50.274] [PRO2_NFC_COMMAND] phase=send sub=0x04",
            "[14:17:50.280] [PRO2_NFC_ACK] response_bytes=8 header=0101010410780000 matched=True",
            "[14:17:50.281] [PRO2_NFC_STATUS] state=0x09 detail=0x00 uid_length=7",
            "[14:17:50.281] [PRO2_NFC_CHUNK] buffer_offset=0 frame_bytes=95 declared_bytes=84",
            "[14:17:50.281] [PRO2_NFC_VALIDATE] data_uid_match=True bcc0_valid=True bcc1_valid=True page3=F110FFEE format=amiibo_ntag215",
            "[14:17:50.281] [PRO2_NFC_ATTEMPT] attempt=2/3 descriptor=uid_bound",
            "[14:17:50.281] [PRO2_NFC_RECOVERY] attempt=1/3 retry_allowed=True state_meaning=unconfirmed",
            "[14:17:50.282] [PRO2_NFC] 读取未完成：无法确认 NFC 应答订阅状态。",
            "[14:17:51.788] [PRO2_NFC_RATE] phase=after fd2_hz=132.72 parsed_hz=132.72",
            "[14:17:51.789] [PRO2_NFC_RESULT] success=False commands=0 tag_writes=0"];
        foreach(string line in nfcEvidence)recent.Add(line);
        foreach(string line in nfcEvidence)check(recent.Snapshot.Contains(line),"recent log retains NFC original evidence after detail cap: "+line.Split(']')[1]);
    }
    private static async Task Replay(Action<bool,string> check,string failure)
    {
        bool reading=false;int chunks=0,stops=0,readStatusPolls=0,readAttempts=0,terminalReplies=0;var seen=new List<byte>();
        var readModes=new List<Pro2NfcReadDescriptorMode>();
        var buffer=Buffer(failure.StartsWith("amiibo"));object primaryOwner=new();
        if(failure=="amiibo_bad_bcc")buffer[63]^=1;
        bool succeeds=failure is "success" or "primary_ack" or "chunk_84" or "padded_95" or "busy_delayed" or "amiibo" or "amiibo_84" or
            "terminal_3e_success" or "terminal_3d_success" or "read_nack_fallback";
        bool wide=failure is "chunk_84" or "amiibo_84";
        int expectedChunks=wide?8:9;var diagnostics=new List<string>();
        var offsets=new List<int>();
        using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<byte[]> Exchange(byte[] command,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();seen.Add(command[3]);
            check(Pro2NfcProtocol.IsReadOnlyCommand(command),"reader emits whitelist commands only "+failure);
            Task<byte[]> Respond(byte[] reply)
            {
                if(failure!="primary_ack")return Task.FromResult(reply);
                // Real command waiter and primary-owner identity, with neither
                // an auxiliary characteristic nor a readable CCCD available.
                var pending=new Pro2CommandAckWaiter(primaryOwner,command);
                check(!pending.TryComplete(new object(),reply)&&!pending.Task.IsCompleted,"foreign owner cannot satisfy NFC ACK");
                byte[] noise=(byte[])reply.Clone();noise[3]^=0x80;
                check(!pending.TryComplete(primaryOwner,noise)&&!pending.Task.IsCompleted,"other command cannot satisfy NFC ACK");
                check(Pro2NfcProtocol.IsReaderResponse(true,reply)&&pending.TryComplete(primaryOwner,reply),"existing primary ACK completes NFC command without auxiliary CCCD");
                return pending.Task;
            }
            if(command[3]==4)
            {
                stops++;
                if(failure=="stop_failure"&&chunks==9)throw new IOException("simulated stop failure");
                reading=false;
                return Respond(Reply(4));
            }
            if(failure=="timeout"&&command[3]==3)throw new TimeoutException("simulated no response");
            if(failure=="nack"&&command[3]==3){var nack=Reply(3);nack[5]=0;return Respond(nack);}
            if(failure=="cancel"&&command[3]==3){cts.Cancel();ct.ThrowIfCancellationRequested();}
            if(command[3]==5)
            {
                if(reading&&chunks==0)
                {
                    readStatusPolls++;
                    if(failure=="busy_timeout"||failure=="busy_delayed"&&readStatusPolls<=22)return Respond(Status(true,state:2));
                    if(failure=="removed")return Respond(Status(true,state:7,detail:0x41));
                    if(failure=="terminal_all"||(failure is "terminal_3e_success" or "terminal_3d_success" or "changed_after_error")&&readAttempts==1)
                    {
                        terminalReplies++;return Respond(Status(true,state:7,detail:failure=="terminal_3d_success"?(byte)0x3d:(byte)0x3e));
                    }
                }
                if(!reading&&failure=="changed_after_error"&&readAttempts>0)return Respond(Status(false,true));
                return Respond(Status(reading,failure=="uid_changed"&&chunks==expectedChunks));
            }
            if(command[3]==6)
            {
                readAttempts++;readStatusPolls=0;
                readModes.Add(command.AsSpan(10,7).SequenceEqual(new byte[7])?Pro2NfcReadDescriptorMode.ConsoleZeroUid:Pro2NfcReadDescriptorMode.UidBound);
                if(failure=="timeout_on_read")throw new TimeoutException("simulated ambiguous read ACK timeout");
                if(failure=="cancel_on_read"){cts.Cancel();ct.ThrowIfCancellationRequested();}
                if(failure=="read_nack_fallback"&&readAttempts==1){var nack=Reply(6);nack[5]=0;return Respond(nack);}
                reading=true;return Respond(Reply(6));
            }
            if(command[3]==0x15)
            {
                chunks++;int offset=BinaryPrimitives.ReadUInt16LittleEndian(command.AsSpan(8));offsets.Add(offset);
                var reply=Chunk(offset,buffer,wide?84:70,failure=="padded_95"?95:0);
                return Respond(failure=="chunk_short"&&chunks==3?reply[..^1]:reply);
            }
            return Respond(Reply(command[3]));
        }
        try
        {
            var result=await new Pro2NfcReader(Exchange,diagnostic:diagnostics.Add).ReadAsync(cts.Token);
            check(succeeds&&result.Data.SequenceEqual(buffer[60..])&&chunks==expectedChunks,"physical-read replay returns exactly 540 bytes "+failure);
            if(wide)check(offsets.SequenceEqual(new[]{0,84,168,252,336,420,504,588}),"reader advances by 84 bytes, not assumed 70 or 95");
            if(failure=="padded_95")check(offsets.SequenceEqual(new[]{0,70,140,210,280,350,420,490,560}),"transport padding is excluded from read offsets");
            if(failure=="busy_delayed")check(readStatusPolls==23,"pending state beyond descriptor 2s is allowed to complete");
            if(failure.StartsWith("amiibo"))check(diagnostics.Any(line=>line.Contains("format=amiibo_ntag215")&&line.Contains("valid=True")),
                "native amiibo format is logged after complete assembly");
            if(failure is "terminal_3e_success" or "terminal_3d_success" or "read_nack_fallback")
                check(readModes.SequenceEqual(new[]{Pro2NfcReadDescriptorMode.ConsoleZeroUid,Pro2NfcReadDescriptorMode.UidBound})&&
                    stops>=3&&diagnostics.Any(line=>line.StartsWith("[PRO2_NFC_RECOVERY]")),
                    "explicit error recovers in the same reader with stop/rediscovery and the bounded alternate descriptor");
        }
        catch(Exception error)when(error is IOException or TimeoutException or OperationCanceledException)
        {
            check(!succeeds,"failing replay cannot return a tag: "+failure);
            if(failure=="busy_timeout")check(error is IOException&&error.Message.Contains("0x02")&&chunks==0,"pending timeout reports actual state and never reads an unready buffer");
            if(failure=="removed")check(error is IOException&&chunks==0,"tag removal while pending aborts before buffer reads");
            if(failure=="amiibo_bad_bcc")check(error is IOException&&error.Message.Contains("BCC0")&&
                diagnostics.Any(line=>line.Contains("bcc0_valid=False")),"failed native tag names and records the exact check before cleanup");
            if(failure=="terminal_all")check(readAttempts==3&&terminalReplies==3&&chunks==0&&
                readModes.SequenceEqual(new[]{Pro2NfcReadDescriptorMode.ConsoleZeroUid,Pro2NfcReadDescriptorMode.UidBound,Pro2NfcReadDescriptorMode.ConsoleZeroUid}),
                "terminal errors are not busy-polled, and stop after two recoveries without exporting stale data");
            if(failure=="changed_after_error")check(readAttempts==1&&chunks==0&&error.Message.Contains("另一张标签"),
                "recovery cannot switch tags or mix their data");
            if(failure is "timeout_on_read" or "cancel_on_read" or "busy_timeout" or "chunk_short" or "amiibo_bad_bcc")
                check(readAttempts==1,"ambiguous timeout, cancellation or corrupt data never triggers a descriptor retry: "+failure);
        }
        check(stops>=2&&seen[^1]==4,"success, failure and cancellation always attempt stop: "+failure);
        check(!seen.Contains(8)&&!seen.Contains(0x14),"reader never emits tag-write commands: "+failure);
    }
}
