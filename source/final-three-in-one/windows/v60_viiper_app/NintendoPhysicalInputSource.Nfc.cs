using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class NintendoPhysicalInputSource
{
    public async Task<NintendoNfcTag> ReadNfcAsync(CancellationToken token)
    {
        if(!CanReadNfc) throw new NotSupportedException("当前提供 Pro1 传统蓝牙 NFC 读取。Pro1 USB 与 Pro2 的 NFC 尚未完成实机验证；不会发送猜测命令。");
        if(!await nfcGate.WaitAsync(0,token)) throw new IOException("NFC 读取已在运行");
        double before=CurrentParsedRateHz; nfcBusy=true; nfcStatus="读取中";
        lock(gate) states.Clear();
        while(outputs.Reader.TryRead(out _)) { }
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await SubcommandAsync(3,[0x31],bounded.Token);
            await SubcommandAsync(0x22,[1],bounded.Token);
            await WaitMcuModeAsync(1,bounded.Token);
            byte[] config=new byte[38]; config[0]=0x21; config[2]=4;
            config[37]=Pro1NfcProtocol.Crc8(config.AsSpan(1,36));
            await SubcommandAsync(0x21,config,bounded.Token);
            await WaitMcuModeAsync(4,bounded.Token);
            await WaitNfcStateAsync(0,bounded.Token);
            await McuRequestAsync(Pro1NfcProtocol.Request(2),bounded.Token);
            await WaitNfcStateAsync(0,bounded.Token);
            await McuRequestAsync(Pro1NfcProtocol.Request(1,0,[0,0,0,0x2c,1]),bounded.Token);
            await WaitNfcStateAsync(1,bounded.Token);
            byte[] uid=[];
            while(uid.Length==0)
            {
                var response=await McuRequestAsync(Pro1NfcProtocol.Request(4),bounded.Token);
                if(Pro1NfcProtocol.TryGetMcu(response,out byte type,out var data)) Pro1NfcProtocol.TryGetTag(type,data,out uid);
            }
            // Read 135 NTAG215 pages. No tag writes, keys or firmware operations.
            byte[] read=[0xd0,7,0,0,0,0,0,0,0,1,3,0,0x3b,0x3c,0x77,0x78,0x86,0,0];
            await McuRequestAsync(Pro1NfcProtocol.Request(6,0,read),bounded.Token);
            var dump=new List<byte>(540); byte packet=0; bool finished=false;
            for(int tries=0;tries<60 && !finished;tries++)
            {
                var response=await McuRequestAsync(Pro1NfcProtocol.Request(4,packet),bounded.Token);
                if(!Pro1NfcProtocol.TryGetMcu(response,out byte type,out var data)) continue;
                if(type==0x3a && data[1]==7)
                {
                    int count=((data[4]<<8)|data[5])&0x7ff;
                    int offset=data[2]==1?66:6; if(offset==66) count-=60;
                    if(count<=0 || offset+count>data.Length || dump.Count+count>540) throw new IOException("NFC 数据包长度无效");
                    dump.AddRange(data.AsSpan(offset,count).ToArray()); packet++;
                }
                else if(type==0x2a && data[6]==7) throw new IOException("标签已移开");
                else if(type==0x2a && data[6]==4 && dump.Count==540) finished=true;
            }
            if(!finished || !Pro1NfcProtocol.IsNtag215(dump.ToArray(),uid)) throw new IOException("NFC 数据不完整或 UID 校验不符；未导出为 amiibo");
            nfcStatus="已读取 540 字节 · UID "+Convert.ToHexString(uid);
            log("[NFC_READ_COMPLETE] transport=Pro1Bluetooth bytes=540 uid="+Convert.ToHexString(uid)+" tag_writes=0 authenticity_verified=false");
            return new(uid,dump.ToArray(),device.PhysicalId,"Pro1Bluetooth",DateTimeOffset.Now);
        }
        catch(Exception e) { nfcStatus="读取未完成："+e.Message; log("[NFC_READ_FAILED] "+e); throw; }
        finally
        {
            try
            {
                using var restore=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                restore.CancelAfter(TimeSpan.FromSeconds(5));
                if(!lifetime.IsCancellationRequested)
                {
                    // Still restore normal reports if suspending MCU is NACKed.
                    try { await SubcommandAsync(0x22,[0],restore.Token); }
                    finally { await SubcommandAsync(3,[0x30],restore.Token); }
                    log(FormattableString.Invariant($"[NFC_RESTORE] report_mode=30 ack=matched before_hz={before:F2} current_hz={CurrentParsedRateHz:F2} tag_writes=0"));
                }
                else log("[NFC_RESTORE] skipped=source_disposed ack=not_requested");
            }
            catch(Exception e) { status="NFC 后输入恢复失败，请断开本槽位再连接"; nfcStatus+="；恢复失败"; log("[NFC_RESTORE_FAILED] "+e); }
            lock(gate) { states.Clear(); stamps.Clear(); cachedRate=0; cachedRateTicks=0; }
            nfcBusy=false; nfcGate.Release();
        }
    }
    private async Task WaitMcuModeAsync(byte mode,CancellationToken token)
    {
        for(int i=0;i<16;i++)
        {
            var response=await McuRequestAsync([],token,1);
            if(Pro1NfcProtocol.TryGetMcu(response,out byte type,out var data)&&type==1&&data.Length>6&&data[6]==mode) return;
        }
        throw new IOException("NFC MCU 未进入目标模式 "+mode);
    }
    private async Task WaitNfcStateAsync(byte wanted,CancellationToken token)
    {
        for(int i=0;i<12;i++)
        {
            var response=await McuRequestAsync(Pro1NfcProtocol.Request(4),token);
            if(Pro1NfcProtocol.TryGetMcu(response,out byte type,out var data)&&type==0x2a&&data.Length>6&&data[0]==0&&data[1]==5&&
                (data[6]==wanted || wanted==1 && Pro1NfcProtocol.TryGetTag(type,data,out _))) return;
        }
        throw new IOException("NFC 未进入目标状态 "+wanted);
    }
    private async Task<byte[]> McuRequestAsync(byte[] data,CancellationToken token,byte command=2)
    {
        while(mcu.Reader.TryRead(out _)) { }
        byte[] request=new byte[49]; request[0]=0x11; request[1]=NextSequence();
        Pro1PhysicalRumble.Neutral.CopyTo(request,2); request[10]=command; data.CopyTo(request,11);
        await transport!.WriteAsync(request,token);
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(token); bounded.CancelAfter(1500);
        while(true)
        {
            byte[] report=await mcu.Reader.ReadAsync(bounded.Token);
            if(Pro1NfcProtocol.TryGetMcu(report,out _,out _)) return report;
        }
    }
}
