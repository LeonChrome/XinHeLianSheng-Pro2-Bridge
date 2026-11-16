using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using Y700Switch2V60Viiper;

internal static class UsbNfcRegressionTests
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        foreach(string scenario in new[]{"success","usb89_padded100","cancel","wrong_transport","timeout"})
        {
            bool shouldSucceed=scenario is "success" or "usb89_padded100";
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var device=new NintendoHidDevice("offline",@"USB\VID_057E&PID_2069\TEST",Guid.NewGuid().ToString(),NintendoInputKind.Pro2Usb,64,64,"offline USB");
            var io=new StreamingUsb();var channel=new NfcUsb(scenario,stop);var trace=new List<string>();
            await using var input=new NintendoPhysicalInputSource(device,line=>{lock(trace)trace.Add(line);});
            await input.ConnectAsync(stop.Token,io,channel);channel.During=()=>check(input.IsRunning&&!input.IsOutputReady,"USB HID continues while NFC temporarily gates feedback "+scenario);
            long before=input.Snapshot().Reports;bool success=false;
            try
            {
                var result=await input.ReadUsbNfcAsync(stop.Token);success=true;
                check(shouldSucceed&&result.Transport=="Pro2Usb"&&result.Data.SequenceEqual(channel.Buffer[60..]),"shared NFC reader obtains unchanged USB tag bytes "+scenario);
            }
            catch(Exception e)when(e is IOException or OperationCanceledException or TimeoutException)
            {check(!shouldSucceed,"USB NFC failure cannot return a complete tag "+scenario);}
            check(success==shouldSucceed,"USB NFC completion result matches response validity "+scenario);
            check(input.IsRunning&&input.IsOutputReady,"USB NFC does not destroy the input or feedback session "+scenario);
            check(channel.Calls.Where(c=>c.Command==1).All(c=>new byte[]{3,4,5,6,0x0c,0x15}.Contains(c.Sub)),"USB NFC whitelist excludes tag writes, report mode changes and unacknowledged optional 01/01");
            check(!channel.Calls.Any(c=>c.Command==1&&c.Sub==1),"known real USB no-ACK command 01/01 is never sent");
            if(success)
            {
                check(input.Snapshot().Reports>before+10,"actual HID reader continues taking reports through NFC transaction");
                check(channel.Calls.Count(c=>c.Command==1&&c.Sub==4)>=2,"USB NFC always confirms stop before and after reading");
                check(trace.Any(t=>t.Contains("[USB_NFC_END] success=True")&&t.Contains("report_mode_changes=0")),"USB NFC keeps before/during metrics and no-report-mode-change evidence");
                if(scenario=="usb89_padded100")
                    check(channel.Offsets.SequenceEqual(new[]{0,89,178,267,356,445,534}),"real USB 89-byte chunks and padded final 66-byte chunk advance by declared bytes");
            }
            else
            {
                int count=channel.Calls.Count;
                try{await input.ReadUsbNfcAsync(CancellationToken.None);check(false,"poisoned USB NFC must require reconnect");}
                catch(IOException){check(channel.Calls.Count==count,"ambiguous USB response prevents speculative command retries");}
            }
        }
    }
    private sealed class StreamingUsb:INintendoHidTransport
    {
        private uint sequence;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token)
        {
            await Task.Delay(4,token);byte[] report=new byte[64];report[0]=5;report[1]=1;
            foreach(int offset in new[]{11,14}){report[offset+1]=8;report[offset+2]=128;}
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(53),4096);BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(43),++sequence);
            report.CopyTo(buffer);return 64;
        }
        public Task WriteAsync(ReadOnlyMemory<byte> report,CancellationToken token){token.ThrowIfCancellationRequested();return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class NfcUsb:INintendoUsbCommandChannel
    {
        public readonly byte[] Buffer=new byte[600];
        public readonly ConcurrentQueue<(byte Command,byte Sub)> Calls=new();
        public readonly List<int> Offsets=[];
        public Action? During;
        private readonly string scenario;
        private readonly CancellationTokenSource stop;
        private bool reading;
        public NfcUsb(string scenario,CancellationTokenSource stop)
        {
            this.scenario=scenario;this.stop=stop;byte[] uid=[4,9,8,7,6,5,4];uid.CopyTo(Buffer,8);uid.AsSpan(0,3).CopyTo(Buffer.AsSpan(60));uid.AsSpan(3).CopyTo(Buffer.AsSpan(64));
            Buffer[63]=(byte)(0x88^uid[0]^uid[1]^uid[2]);Buffer[68]=(byte)(uid[3]^uid[4]^uid[5]^uid[6]);new byte[]{0xf1,0x10,0xff,0xee}.CopyTo(Buffer,72);
        }
        public byte[] Send(byte command,byte sub,byte[] data)
        {
            Calls.Enqueue((command,sub));byte[] reply=[command,1,0,sub,0,0x78,0,0];if(command!=1)return reply;
            if(sub==1)throw new TimeoutException("Real USB 01/01 has no ACK; this must never be a prerequisite.");
            if(sub==5)
            {Array.Resize(ref reply,24);reply[8]=reading?(byte)4:(byte)9;reply[16]=7;Buffer.AsSpan(8,7).CopyTo(reply.AsSpan(17));}
            if(sub==6)
            {
                During?.Invoke();reading=true;
                if(scenario=="cancel")stop.Cancel();
                if(scenario=="wrong_transport")reply[2]=1;
                if(scenario=="timeout")throw new TimeoutException("offline ambiguous USB timeout");
            }
            if(sub==0x15)
            {
                int offset=BinaryPrimitives.ReadUInt16LittleEndian(data),count=Math.Min(scenario=="usb89_padded100"?89:84,600-offset);Offsets.Add(offset);
                Array.Resize(ref reply,scenario=="usb89_padded100"?100:11+count);
                reply[8]=offset+count==600?(byte)1:(byte)0;BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(9),(ushort)count);Buffer.AsSpan(offset,count).CopyTo(reply.AsSpan(11));
            }
            return reply;
        }
        public void Dispose(){}
    }
}
