using System.Buffers.Binary;
using System.IO;
using Y700Switch2V60Viiper;

namespace Pro2WirelessExperiment;

public sealed partial class ExperimentSession
{
    private readonly SemaphoreSlim tagUpdateGate=new(1,1);
    private TaskCompletionSource<bool>? upstreamTagAck;
    private ushort upstreamTagGeneration;
    private bool upstreamTagExpected,upstreamTagConfirmed,hostReadAttempted;
    private string upstreamTagSource="",upstreamTagProgress="等待读取实体卡或载入标签文件。";
    private int upstreamScans,upstreamReads,upstreamDataSent,upstreamTagProgressStage;
    public string UpstreamNfcStatus
    {
        get { lock(protocolGate)return upstreamTagProgress; }
    }

    // Each record is the same size as P1IN. Tag bytes are staged by the child
    // until it receives all 27 blocks and a commit for the same generation.
    public static byte[] EncodeUpstreamNfc(ushort generation,NintendoNfcTag? tag)
    {
        if(generation==0)throw new ArgumentOutOfRangeException(nameof(generation));
        if(tag!=null)ValidateSwitchTag(tag);
        int blocks=tag==null?0:27;
        int signatures=tag?.Signature.Length==32?2:0;
        byte[] wire=new byte[(blocks+signatures+1)*28];
        for(int i=0;i<=blocks+signatures;i++)
        {
            Span<byte> frame=wire.AsSpan(i*28,28);
            "P1NF"u8.CopyTo(frame);
            BinaryPrimitives.WriteUInt16LittleEndian(frame[6..],generation);
            if(i<blocks)
            {
                frame[4]=1;frame[5]=(byte)i;
                tag!.Data.AsSpan(i*20,20).CopyTo(frame[8..]);
            }
            else if(i<blocks+signatures)
            {
                int index=i-blocks;
                frame[4]=3;frame[5]=(byte)index;
                tag!.Signature.AsSpan(index*20,index==0?20:12).CopyTo(frame[8..]);
            }
            else frame[4]=tag==null?(byte)0:(byte)2;
        }
        return wire;
    }

    private static void ValidateSwitchTag(NintendoNfcTag tag)
    {
        if(!Pro1NfcProtocol.IsNtag215(tag.Data,tag.Uid)||tag.Signature.Length is not(0 or 32))
            throw new InvalidDataException("标签长度或 UID/BCC 校验失败。");
        if(Pro2NfcProtocol.ClassifyNtag215Page(Convert.ToHexString(tag.Data.AsSpan(12,4)))=="unknown")
            throw new InvalidDataException("标签第 3 页的容量标记与 NTAG215 / amiibo 不符。");
    }

    private async Task<bool> PublishUpstreamTagAsync(NintendoNfcTag? tag,long revision,CancellationToken token)
    {
        await tagUpdateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ushort id;
            TaskCompletionSource<bool> ack;
            lock(protocolGate)
            {
                if(revision!=tagGeneration||disposed!=0)return false;
                // The CLI may read the physical card before it starts the radio.
                if(upstreamPipe==null&&!relayStarted)
                {
                    snapshot=tag;
                    upstreamTagProgress=tag==null?"已移除标签。":"已读取实体卡，等待发射器启动。";
                    return true;
                }
                if(upstreamTagGeneration==ushort.MaxValue)
                    throw new IOException("本轮标签操作已达上限，请重新开始转发。");
                id=++upstreamTagGeneration;
                ack=new(TaskCreationOptions.RunContinuationsAsynchronously);
                upstreamTagAck=ack;
                upstreamTagExpected=tag!=null;upstreamTagConfirmed=false;
                upstreamTagProgressStage=0;
                upstreamTagSource=tag?.Transport=="file"?"文件":"实体卡";
                upstreamTagProgress=tag==null?"正在移除标签…":"正在载入"+upstreamTagSource+"…";
            }
            try
            {
                await WriteUpstreamPayloadAsync(EncodeUpstreamNfc(id,tag),token).ConfigureAwait(false);
                if(!await ack.Task.WaitAsync(TimeSpan.FromSeconds(5),token).ConfigureAwait(false))
                    throw new IOException("发射器拒绝标签更新，请重试。");
                lock(protocolGate)
                {
                    snapshot=tag;
                    return revision==tagGeneration;
                }
            }
            catch(Exception e)
            {
                lock(protocolGate)
                {
                    upstreamTagConfirmed=false;
                    upstreamTagProgress="标签更新未确认，可重试或移除标签。";
                }
                if(e is TimeoutException)throw new IOException(upstreamTagProgress,e);
                throw;
            }
            finally { lock(protocolGate)upstreamTagAck=null; }
        }
        finally { tagUpdateGate.Release(); }
    }

    private void ObserveUpstreamNfc(string line)
    {
        string[] fields=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        string? generation=Array.Find(fields,field=>field.StartsWith("generation=",StringComparison.Ordinal));
        if(generation==null||!ushort.TryParse(generation.AsSpan(11),out ushort id))return;
        bool requestCard=false;
        lock(protocolGate)
        {
            bool matches=upstreamTagGeneration==id;
            switch(fields[0])
            {
                case "NFC_TAG_ACK" when matches&&upstreamTagAck is {Task.IsCompleted:false} ack:
                    upstreamTagConfirmed=fields.Contains("ok=1")&&fields.Contains(upstreamTagExpected?"present=1":"present=0");
                    if(upstreamTagConfirmed)upstreamTagProgress=upstreamTagExpected?
                        upstreamTagSource+"已就绪，等待 Switch 扫描。":"已移除标签。";
                    ack.TrySetResult(upstreamTagConfirmed);
                    break;
                case "NFC_HOST_SCAN":
                    upstreamScans++;
                    if(upstreamTagConfirmed&&upstreamTagExpected&&matches&&upstreamTagProgressStage==0)
                    {
                        upstreamTagProgressStage=1;
                        upstreamTagProgress="Switch 已开始扫描"+upstreamTagSource+"。";
                    }
                    else if(id==0&&!upstreamTagExpected&&upstreamTagAck==null&&snapshot==null&&!hostReadAttempted)
                    {
                        requestCard=CanReadNfc;
                        hostReadAttempted=true;
                        upstreamTagProgress=requestCard?"Switch 请求扫描，请将 amiibo 贴在手柄上。":"Switch 请求扫描，请载入标签文件。";
                    }
                    break;
                case "NFC_HOST_READ" when matches&&upstreamTagConfirmed&&upstreamTagExpected:
                    upstreamReads++;
                    if(upstreamTagProgressStage<2){upstreamTagProgressStage=2;upstreamTagProgress="Switch 已请求读取标签。";}
                    break;
                case "NFC_TAG_DATA_SENT" when matches&&upstreamTagConfirmed&&upstreamTagExpected:
                    upstreamDataSent++;upstreamTagProgressStage=3;upstreamTagProgress="已向 Switch 发出完整标签应答，请查看游戏结果。";break;
            }
        }
        if(requestCard)_=CaptureFromHostAsync(lifetime.Token);
    }

    private async Task WriteUpstreamPayloadAsync(byte[] payload,CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pipe=Volatile.Read(ref upstreamPipe)??throw new IOException("Switch 发射端已关闭。");
            // Complete the current fixed-size transaction once started; a caller
            // cancel must not leave half a record before the next USB input.
            await pipe.WriteAsync(payload,lifetime.Token).ConfigureAwait(false);
            await pipe.FlushAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally { sendGate.Release(); }
    }
}
