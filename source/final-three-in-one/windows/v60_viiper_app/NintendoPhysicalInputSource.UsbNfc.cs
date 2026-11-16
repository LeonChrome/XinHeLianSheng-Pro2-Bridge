using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class NintendoPhysicalInputSource
{
    // Explicit experiment API. The normal NFC UI remains on the verified BLE
    // path until USB reads have passed physical-card acceptance.
    public bool CanReadUsbNfc => running && initialized && !nfcBusy && !usbNfcPoisoned && device.Kind==NintendoInputKind.Pro2Usb && usbCommands!=null;
    private bool usbNfcPoisoned, usbNfcInitialized;

    public async Task<NintendoNfcTag> ReadUsbNfcAsync(CancellationToken token)
    {
        if(usbNfcPoisoned)throw new IOException("上一轮 USB NFC 应答未确认完成，请重新连接实体 USB 手柄。");
        if(!CanReadUsbNfc)throw new NotSupportedException("先连接实体 Pro2 USB；Pro1 USB NFC 未接入。");
        if(!await nfcGate.WaitAsync(0,token).ConfigureAwait(false))throw new IOException("NFC 正在读取。");
        nfcBusy=true;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var start=Snapshot();long started=Stopwatch.GetTimestamp();
        bool succeeded=false,outputHeld=false;
        try
        {
            if(usbNfcPoisoned)throw new IOException("上一轮 USB NFC 命令未确认完成，请重新连接实体 USB 手柄。");
            // No report-mode/250Hz changes. The physical HID reader keeps running.
            await physicalOutputGate.WaitAsync(deadline.Token).ConfigureAwait(false);outputHeld=true;
            while(outputs.Reader.TryRead(out _))Interlocked.Increment(ref outputSuperseded);
            await transport!.WriteAsync(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"usb-nfc-pause").Report,deadline.Token).ConfigureAwait(false);
            log("[USB_NFC_BEGIN] physical_input_continues=true report_mode_changes=0 tag_writes=0 baseline="+Format(start));
            async Task<byte[]> Send(byte command,byte sub,byte[] payload,CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if(usbNfcPoisoned)throw new IOException("USB NFC 应答已失步，未继续发送。");
                long sent=Stopwatch.GetTimestamp();
                try
                {
                    byte[] response=await Task.Run(()=>usbCommands!.Send(command,sub,payload),CancellationToken.None).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if(response.Length<8||response[0]!=command||response[1]!=1||response[2]!=0||response[3]!=sub)
                        throw new IOException("USB NFC 应答 transport/command 不符。");
                    // The common reader speaks BLE's transport byte 1. Normalize
                    // only a separately checked USB response; never fabricate ACKs.
                    response=(byte[])response.Clone();response[2]=1;
                    log(FormattableString.Invariant($"[USB_NFC_COMMAND] command={command:X2}/{sub:X2} response_bytes={response.Length} elapsed_ms={Stopwatch.GetElapsedTime(sent).TotalMilliseconds:F2} ack={response[5]:X2} payload_logged=false"));
                    if(sub==5&&Pro2NfcProtocol.TryParseStatus(response,out var state))
                        log("[USB_NFC_STATUS] state=0x"+state.State.ToString("X2")+" detail=0x"+state.Detail.ToString("X2")+" uid_length="+state.Uid.Length+" uid_logged=false");
                    if(sub==0x15)
                    {
                        int offset=payload.Length==2?payload[0]|payload[1]<<8:-1;
                        int declared=response.Length>=11?response[9]|response[10]<<8:-1;
                        log("[USB_NFC_CHUNK] offset="+offset+" declared_bytes="+declared+" response_bytes="+response.Length+" framing="+Convert.ToHexString(response.AsSpan(0,Math.Min(11,response.Length)))+" tag_payload_logged=false");
                    }
                    return response;
                }
                catch(Exception e)
                {
                    // USB has no transaction ID. A timeout/cancellation cannot be
                    // followed by a speculative retry on the same bulk pipe.
                    usbNfcPoisoned=true;
                    log("[USB_NFC_COMMAND_FAILED] command="+command.ToString("X2")+"/"+sub.ToString("X2")+" error="+e.GetType().Name+" reconnect_required=true");
                    throw;
                }
            }
            if(!usbNfcInitialized)
            {
                // 01/0C is ACKed by the real USB device. Do not copy BLE's
                // optional 01/01 into USB: this firmware never ACKs it and it
                // aborts a working 250Hz session before any scan is requested.
                await Send(1,0x0c,[],deadline.Token).ConfigureAwait(false);
                log("[USB_NFC_INIT_POLICY] route=usb_acked_0c optional_01_01=not_sent reason=observed_no_ack no_fake_success=true");
                usbNfcInitialized=true;
            }
            async Task<byte[]> Exchange(byte[] request,CancellationToken ct)
            {
                if(!Pro2NfcProtocol.IsReadOnlyCommand(request))throw new InvalidOperationException("USB NFC 拒绝非只读命令。");
                return await Send(1,request[3],request[8..],ct).ConfigureAwait(false);
            }
            var reader=new Pro2NfcReader(Exchange,message=>nfcStatus=message,log);
            var tag=await reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            succeeded=true;nfcStatus="Pro2 USB 已读取 540 字节 · 实验";
            return new(tag.Uid,tag.Data,device.PhysicalId,"Pro2Usb",DateTimeOffset.Now){Signature=tag.Signature};
        }
        catch(Exception e){nfcStatus="USB NFC 读取未完成："+e.Message;log("[USB_NFC_FAILED] "+e);throw;}
        finally
        {
            while(outputs.Reader.TryRead(out _))Interlocked.Increment(ref outputSuperseded);
            nfcBusy=false;if(outputHeld)physicalOutputGate.Release();nfcGate.Release();
            var end=Snapshot();double seconds=Stopwatch.GetElapsedTime(started).TotalSeconds;
            log(FormattableString.Invariant($"[USB_NFC_END] success={succeeded} seconds={seconds:F2} physical_reports={end.Reports-start.Reports} during_hz={(seconds>0?(end.Reports-start.Reports)/seconds:0):F2} reconnect_required={usbNfcPoisoned} tag_writes=0 report_mode_changes=0"));
        }
    }
}
