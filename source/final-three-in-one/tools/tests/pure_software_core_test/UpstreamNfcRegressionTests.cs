using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Pro2WirelessExperiment;
using Y700Switch2V60Viiper;

internal static class UpstreamNfcRegressionTests
{
    private sealed class Pipe : MemoryStream
    {
        public readonly Channel<byte[]> Messages=Channel.CreateUnbounded<byte[]>();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data,CancellationToken token=default)=>
            Messages.Writer.WriteAsync(data.ToArray(),token);
        public async Task<byte[]> NextAsync()=>await Messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }
    public static async Task RunAsync(Action<bool,string> check,string output)
    {
        byte[] uid=[4,11,22,33,44,55,66],data=new byte[540];
        for(int i=0;i<data.Length;i++)data[i]=(byte)(i*37+13);
        uid.AsSpan(0,3).CopyTo(data);uid.AsSpan(3).CopyTo(data.AsSpan(4));
        data[3]=(byte)(0x88^uid[0]^uid[1]^uid[2]);data[8]=(byte)(uid[3]^uid[4]^uid[5]^uid[6]);
        byte[] amiiboPage=[0xF1,0x10,0xFF,0xEE];
        amiiboPage.CopyTo(data,12);
        var plain=new NintendoNfcTag(uid,data,"fixture","test",DateTimeOffset.UnixEpoch);
        var signed=plain with{Signature=Enumerable.Range(0,32).Select(i=>(byte)(i+7)).ToArray()};
        byte[] encoded=ExperimentSession.EncodeUpstreamNfc(1,plain);
        byte[] encodedSigned=ExperimentSession.EncodeUpstreamNfc(1,signed);
        byte[] invalidPage=(byte[])data.Clone(); invalidPage[14]=0;
        try{ExperimentSession.EncodeUpstreamNfc(1,plain with{Data=invalidPage});check(false,"unknown capacity rejected");}
        catch(InvalidDataException){check(true,"unknown capacity rejected");}
        // Optional cross-language fixtures. Only synthetic test bytes reach disk.
        void Export(string variable,byte[] bytes)
        {
            string? path=Environment.GetEnvironmentVariable(variable);
            if(!string.IsNullOrEmpty(path)&&Path.IsPathFullyQualified(path))File.WriteAllBytes(path,bytes);
        }
        Export("PRO2_TEST_NFC_WIRE_FILE",encoded);Export("PRO2_TEST_NFC_SIGNED_WIRE_FILE",encodedSigned);
        check(encoded.Length==784&&encodedSigned.Length==840,"NFC transfer retains the existing fixed-size input framing with optional signature");
        for(int i=0;i<27;i++)
        {
            int start=i*28;
            check(encoded.AsSpan(start,4).SequenceEqual("P1NF"u8)&&encoded[start+4]==1&&encoded[start+5]==i&&
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(start+6))==1&&
                encoded.AsSpan(start+8,20).SequenceEqual(data.AsSpan(i*20,20)),"ordered NFC wire block "+i);
        }
        check(encoded[760]==2&&encodedSigned[760]==3&&encodedSigned[788]==3&&encodedSigned[789]==1&&encodedSigned[816]==2&&
            encodedSigned.AsSpan(764,20).SequenceEqual(signed.Signature.AsSpan(0,20))&&
            encodedSigned.AsSpan(792,12).SequenceEqual(signed.Signature.AsSpan(20))&&
            encodedSigned.AsSpan(804,8).ToArray().All(x=>x==0),"originality signature bytes and padding preserved before atomic commit");
        try{ExperimentSession.EncodeUpstreamNfc(1,plain with{Data=new byte[540]});check(false,"invalid UID rejected");}catch(InvalidDataException){}
        await using var relay=new ExperimentSession(Path.Combine(output,"switch-nfc-"+Guid.NewGuid().ToString("N"))){UseUpstreamSwitch=true};
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(ExperimentSession);using var pipe=new Pipe();
        type.GetField("upstreamPipe",flags)!.SetValue(relay,pipe);
        type.GetField("protocol",flags)!.SetValue(relay,new WirelessPro1Protocol(new byte[6]));
        async Task Observe(string message)
        {
            using var reader=new StreamReader(new MemoryStream(Encoding.ASCII.GetBytes("123 ms  "+message+"\n")));
            await (Task)type.GetMethod("ReadUpstreamStatusAsync",flags)!.Invoke(relay,[reader])!;
        }
        string file=Path.Combine(relay.DirectoryPath,"fixture.bin");
        File.WriteAllBytes(file,[..data,..signed.Signature]);
        Task load=relay.LoadTagAsync(file,CancellationToken.None);
        check((await pipe.NextAsync()).SequenceEqual(encodedSigned),"loading a signed file sends real encoder output");
        byte[] input=new byte[49];input[0]=0x30;input[4]=0x10;
        await (Task)type.GetMethod("WriteUpstreamFrameAsync",flags)!.Invoke(relay,[input,CancellationToken.None])!;
        check((await pipe.NextAsync()).SequenceEqual(ExperimentSession.EncodeUpstreamSwitchFrame(input))&&!load.IsCompleted,
            "physical input streams between the tag commit and the native acknowledgement");
        await Observe("NFC_TAG_ACK generation=99 present=1 ok=1");
        check(!load.IsCompleted&&!relay.HasTag,"foreign ACK cannot present an unconfirmed tag");
        await Observe("NFC_TAG_ACK generation=1 present=1 ok=1");
        await load;
        check(relay.HasTag&&relay.UpstreamNfcStatus.Contains("就绪"),"native acceptance sets ready/captured only");
        await Observe("NFC_HOST_READ generation=1");
        check(relay.UpstreamNfcStatus.Contains("请求读取"),"physical host read request surfaced");
        await Observe("NFC_TAG_DATA_SENT generation=1");
        await Observe("NFC_HOST_SCAN generation=1");
        check(relay.UpstreamNfcStatus.Contains("完整标签应答"),"actual final response distinguished from console/game acceptance");
        Task remove=relay.RemoveTagAsync(CancellationToken.None);
        byte[] clear=await pipe.NextAsync();
        check(clear.Length==28&&clear[4]==0&&clear[6]==2,"eject creates an independent tagged transaction");
        await Observe("NFC_TAG_ACK generation=2 present=0 ok=1");
        await remove;await Observe("NFC_TAG_DATA_SENT generation=1");await Observe("NFC_HOST_SCAN generation=0");
        check(!relay.HasTag&&relay.UpstreamNfcStatus.Contains("移除"),"removal suppresses old reports and automatic reinsertion");
        Task rejected=relay.LoadTagAsync(file,CancellationToken.None);await pipe.NextAsync();
        await Observe("NFC_TAG_ACK generation=3 present=0 ok=0");
        try{await rejected;check(false,"negative backend acknowledgement rejected");}catch(IOException){}
        check(!relay.HasTag&&relay.UpstreamNfcStatus.Contains("未确认"),"negative ack never shown as tag success");
        var closing=relay.LoadTagAsync(file,CancellationToken.None);await pipe.NextAsync();
        type.GetMethod("CloseUpstreamInput",flags)!.Invoke(relay,null);
        try{await closing.WaitAsync(TimeSpan.FromSeconds(1));check(false,"closing backend must reject outstanding capture");}catch(IOException){}
        check(!relay.HasTag&&closing.IsFaulted,"closing backend ends the pending transaction without blocking input shutdown");
        type.GetField("upstreamPipe",flags)!.SetValue(relay,null);
    }
}
