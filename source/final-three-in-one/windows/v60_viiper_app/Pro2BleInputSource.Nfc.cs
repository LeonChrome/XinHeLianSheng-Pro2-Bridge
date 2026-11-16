using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private readonly SemaphoreSlim nfcOutputGate=new(1,1);
    private GattCharacteristic? nfcWriteCharacteristic, nfcSecondaryCharacteristic;
    private CancellationTokenSource? nfcReadCts;
    private TaskCompletionSource? nfcReadComplete;
    private Pro2CommandAckWaiter? pendingNfcAck;
    private volatile bool nfcReading, nfcNeedsReconnect;
    private volatile string nfcStatus="未读取 · Pro2 BLE 只读实验";
    public bool IsReadingNfc=>nfcReading;
    public string NfcStatus=>IsReadingNfc?nfcStatus:
        !IsRunning||!transportConnected?"请先连接 Pro2 无线手柄":
        nfcNeedsReconnect?nfcStatus:
        nfcWriteCharacteristic==null?"本次连接未发现 NFC 扩展命令接口，请查看诊断":nfcStatus;
    public bool CanReadNfc=>IsRunning&&transportConnected&&!disconnectSignalCaptured&&!nfcReading&&!nfcNeedsReconnect&&
        ackCharacteristic?.Uuid==AckUuid&&nfcWriteCharacteristic?.Uuid==Pro2NfcProtocol.ExtendedWriteUuid&&
        (nfcWriteCharacteristic.CharacteristicProperties&GattCharacteristicProperties.WriteWithoutResponse)!=0;

    public async Task<NintendoNfcTag> ReadNfcAsync(CancellationToken token)
    {
        BluetoothLEDevice owner;GattCharacteristic writer,ack;GattCharacteristic? secondary;
        CancellationTokenSource operation;TaskCompletionSource done;long sequence;
        lock(gate)
        {
            token.ThrowIfCancellationRequested();
            if(nfcNeedsReconnect)throw new IOException("上一轮 NFC 应答未确认结束，请重新连接手柄后再读取。");
            if(!CanReadNfc||device==null)throw new NotSupportedException("先连接 Pro2 BLE；当前连接未提供已知的 NFC 命令接口。");
            if(compatibilityTrialCts!=null||activeBleDiagnosticCapture!=null)
                throw new InvalidOperationException("正在测试蓝牙参数，请结束后再读卡。");
            owner=device;writer=nfcWriteCharacteristic!;ack=ackCharacteristic!;secondary=nfcSecondaryCharacteristic;
            sequence=connectionSequence;operation=CancellationTokenSource.CreateLinkedTokenSource(token);
            operation.CancelAfter(TimeSpan.FromSeconds(35));
            done=new(TaskCreationOptions.RunContinuationsAsynchronously);
            nfcReading=true;nfcReadCts=operation;nfcReadComplete=done;
        }
        bool SameOwner(){lock(gate)return ReferenceEquals(device,owner)&&connectionSequence==sequence&&
            IsRunning&&transportConnected&&!disconnectSignalCaptured&&ReferenceEquals(ackCharacteristic,ack);}
        void Report(string message)
        {
            lock(gate){if(!ReferenceEquals(nfcReadCts,operation))return;nfcStatus=message;}
            NfcTrace("[PRO2_NFC] "+message+" tag_writes=0");
        }
        (long T,uint Raw,uint Parsed) Counts(){lock(gate)return(Stopwatch.GetTimestamp(),fd2InputNotifyCount,fd2InputParsedCount);}
        string Rates((long T,uint Raw,uint Parsed) a,(long T,uint Raw,uint Parsed) b)
        {
            if(b.Raw<a.Raw||b.Parsed<a.Parsed)return "measurement_invalid=connection_changed";
            double seconds=Stopwatch.GetElapsedTime(a.T,b.T).TotalSeconds;
            return FormattableString.Invariant($"seconds={seconds:F2} fd2_hz={(seconds>0?(b.Raw-a.Raw)/seconds:0):F2} parsed_hz={(seconds>0?(b.Parsed-a.Parsed)/seconds:0):F2}");
        }
        bool wakeHeld=false,outputHeld=false,secondaryAdded=false,secondaryOwned=false;
        bool restoreBatteryWorker=runtimeBatteryTask!=null;
        Task? batteryStopTask=null;
        bool readSucceeded=false,stopConfirmed=false;int commandCount=0;
        var start=Counts();var before=start;var finished=start;
        async Task<byte[]> Exchange(byte[] command,CancellationToken ct)
        {
            if(!Pro2NfcProtocol.IsReadOnlyCommand(command))throw new InvalidOperationException("拒绝非 NFC 只读命令。");
            if(!SameOwner())throw new IOException("读卡期间 Pro2 连接已改变。");
            var pending=new Pro2CommandAckWaiter(ack,command);
            Volatile.Write(ref pendingNfcAck,pending);
            long sent=Stopwatch.GetTimestamp();bool submitted=false;
            try
            {
                // No retry: a timed-out 0x15 reply has no offset/transaction ID.
                // Retrying it could misattribute a late reply to the next chunk.
                submitted=true;commandCount++;if(command[3]==3)stopConfirmed=false;
                var status=await writer.WriteValueAsync(ToBuffer(Pro2NfcProtocol.BuildExtendedPacket(command)),
                    GattWriteOption.WriteWithoutResponse).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(3),ct).ConfigureAwait(false);
                if(status!=GattCommunicationStatus.Success)throw new IOException("NFC 命令写入失败："+status);
                byte[] reply=await pending.Task.WaitAsync(TimeSpan.FromSeconds(3),ct).ConfigureAwait(false);
                Pro2NfcProtocol.RequireSuccess(command,reply);
                if(!SameOwner())throw new IOException("收到应答时 Pro2 连接已改变。");
                if(command[3]==4)stopConfirmed=true;else if(command[3]==3)stopConfirmed=false;
                NfcTrace("[PRO2_NFC_COMMAND] sub=0x"+command[3].ToString("X2")+
                    " response_bytes="+reply.Length+" elapsed_ms="+Stopwatch.GetElapsedTime(sent).TotalMilliseconds.ToString("F1")+
                    " response_source=primary_ack payload_logged=false tag_writes=0");
                return reply;
            }
            catch(Exception error)
            {
                if(SameOwner()&&submitted&&!pending.Task.IsCompleted&&(error is TimeoutException or OperationCanceledException))
                {nfcNeedsReconnect=true;Report("应答超时或被取消 · 再次读卡前请重新连接");}
                throw;
            }
            finally{Interlocked.CompareExchange(ref pendingNfcAck,null,pending);}
        }
        void OnSecondary(GattCharacteristic sender,GattValueChangedEventArgs args)
        {
            // Captured ordinary NFC replies use the already subscribed primary
            // ACK. Never feed duplicate secondary replies into chunk assembly.
            if(SameOwner())NfcTrace("[PRO2_NFC_SECONDARY] bytes="+args.CharacteristicValue.Length+" routed_to_reader=false");
        }
        try
        {
            Report("准备只读 NFC · 输入保持接收，震动暂时暂停");
            // Join a possible in-progress battery probe before measuring NFC.
            // Merely disabling its next command would leave an overlapping
            // 20s capture which could falsely blame NFC gaps on the battery.
            batteryStopTask=StopRuntimeBatteryRefreshAsync();
            try{await batteryStopTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);}
            catch(TimeoutException)
            {nfcNeedsReconnect=true;throw new IOException("电量查询清理未及时结束，未开始 NFC 读取；请重新连接。");}
            operation.Token.ThrowIfCancellationRequested();
            if(!await wakeCommandGate.WaitAsync(TimeSpan.FromSeconds(3),operation.Token).ConfigureAwait(false))
                throw new IOException("手柄命令通道正忙，请稍后重试。");
            wakeHeld=true;
            if(!await nfcOutputGate.WaitAsync(TimeSpan.FromSeconds(3),operation.Token).ConfigureAwait(false))
                throw new IOException("震动写入未及时结束，未开始读卡。");
            outputHeld=true;
            if(!SameOwner())throw new IOException("Pro2 已断开。");
            if(gattSession is {MaxPduSize:>0 and <84})throw new NotSupportedException("当前 GATT MTU 不足以接收 NFC 分块应答。");
            byte[] neutral;bool hadRumble;
            lock(writeGate)
            {
                hadRumble=rumbleQueue.LastWrittenActive;neutral=rumbleQueue.LastWrittenNeutral;
                int discarded=rumbleQueue.DiscardForNfc();ordinaryRumbleHold.Invalidate();
                NfcTrace("[PRO2_NFC_RUMBLE] paused=true discarded="+discarded+" old_effects_replayed=false");
            }
            if(hadRumble&&rumbleCharacteristic is {} rumble)
            {
                var stop=await WriteCharacteristicAsync(rumble,neutral,operation.Token).WaitAsync(TimeSpan.FromSeconds(2),operation.Token).ConfigureAwait(false);
                if(stop!=GattCommunicationStatus.Success)throw new IOException("未确认震动停止，未开始读卡。");
                rumbleQueue.MarkWriteResult(new(neutral,false,Stopwatch.GetTimestamp(),true,Source:"nfc_stop"),true);
            }
            start=Counts();await Task.Delay(1500,operation.Token).ConfigureAwait(false);before=Counts();
            NfcTrace("[PRO2_NFC_RATE] phase=before "+Rates(start,before)+" synthetic_reports=0");
            if(secondary!=null)
            {
                var prior=await secondary.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask(operation.Token)
                    .WaitAsync(TimeSpan.FromSeconds(2),operation.Token).ConfigureAwait(false);
                if(prior.Status!=GattCommunicationStatus.Success)throw new IOException("无法确认 NFC 应答订阅状态。");
                secondary.ValueChanged+=OnSecondary;secondaryAdded=true;
                if(prior.ClientCharacteristicConfigurationDescriptor==GattClientCharacteristicConfigurationDescriptorValue.None)
                {
                    // Own this subscription before issuing the asynchronous call,
                    // so even a timeout follows the cleanup path.
                    secondaryOwned=true;
                    var subscription=secondary.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask();
                    GattCommunicationStatus subscribed;
                    try{subscribed=await subscription.WaitAsync(TimeSpan.FromSeconds(2),operation.Token).ConfigureAwait(false);}
                    catch
                    {
                        nfcNeedsReconnect=true;
                        _=ReleaseLateNfcSubscriptionAsync(subscription,secondary,owner,sequence);
                        throw;
                    }
                    if(subscribed!=GattCommunicationStatus.Success)throw new IOException("NFC 应答订阅失败："+subscribed);
                }
            }
            var result=await new Pro2NfcReader(Exchange,Report).ReadAsync(operation.Token).ConfigureAwait(false);
            readSucceeded=true;Report("已读取 NTAG215 · 540 字节");
            return new(result.Uid,result.Data,FormatAddress(owner.BluetoothAddress),"Pro2Ble",DateTimeOffset.Now){Signature=result.Signature};
        }
        catch(Exception error)
        {
            Report(error is OperationCanceledException?"读取已取消":"读取未完成："+error.Message);
            throw;
        }
        finally
        {
            try
            {
            finished=Counts();
            if(SameOwner()&&before.T>start.T)NfcTrace("[PRO2_NFC_RATE] phase=during "+Rates(before,finished)+" synthetic_reports=0");
            if(secondaryOwned&&secondary!=null&&SameOwner())
            {
                try
                {
                    using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var released=await secondary.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                        .AsTask(cleanup.Token).WaitAsync(TimeSpan.FromSeconds(2),cleanup.Token).ConfigureAwait(false);
                    var verified=await secondary.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask(cleanup.Token)
                        .WaitAsync(TimeSpan.FromSeconds(1),cleanup.Token).ConfigureAwait(false);
                    if(released!=GattCommunicationStatus.Success||verified.Status!=GattCommunicationStatus.Success||
                        verified.ClientCharacteristicConfigurationDescriptor!=GattClientCharacteristicConfigurationDescriptorValue.None)
                        throw new IOException("NFC 应答订阅未确认释放。");
                }
                catch(Exception error){nfcNeedsReconnect=true;Report("NFC 订阅清理未确认 · 请重新连接："+error.GetType().Name);}
            }
            if(secondaryAdded&&secondary!=null)secondary.ValueChanged-=OnSecondary;
            if(SameOwner())
            {
                var afterStart=Counts();
                await Task.Delay(1500).ConfigureAwait(false);var after=Counts();
                NfcTrace("[PRO2_NFC_RATE] phase=after "+Rates(afterStart,after)+" synthetic_reports=0");
            }
            if(SameOwner()&&!stopConfirmed&&commandCount>0)nfcNeedsReconnect=true;
            if(readSucceeded&&(nfcNeedsReconnect||!SameOwner()))
                throw new IOException("读取已结束，但清理或连接未确认；请重新连接后再试。");
            }
            finally
            {
            if(outputHeld)nfcOutputGate.Release();if(wakeHeld)wakeCommandGate.Release();
            if(restoreBatteryWorker&&SameOwner()&&connectionProgress is {} progress)
            {
                if(batteryStopTask is {IsCompleted:false})
                    _=RestoreBatteryAfterNfcAsync(batteryStopTask,done.Task,owner,sequence);
                else
                {
                    StartRuntimeBatteryRefresh(progress);
                    NfcTrace("[PRO2_NFC] battery_worker_restored=true standard_refresh_schedule=true");
                }
            }
            lock(gate)
            {
                if(ReferenceEquals(nfcReadCts,operation))
                {nfcReadCts=null;nfcReadComplete=null;nfcReading=false;}
            }
            if(nfcNeedsReconnect)nfcStatus="请重新连接后再读卡 · 上次清理或应答未确认";
            NfcTrace("[PRO2_NFC_RESULT] success="+(readSucceeded&&!nfcNeedsReconnect)+" commands="+commandCount+
                " scan_stop_confirmed="+stopConfirmed+" next_read_requires_reconnect="+nfcNeedsReconnect+" tag_writes=0 input_rate_reconfigured=false");
            operation.Dispose();done.TrySetResult();ConnectionStateChanged?.Invoke();
            }
        }
    }
    private void NfcTrace(string message)
    {try{connectionProgress?.Report(message);}catch(Exception){/* Diagnostics cannot retain command/channel ownership. */}}
    private async Task RestoreBatteryAfterNfcAsync(Task stopped,Task nfcComplete,BluetoothLEDevice owner,long sequence)
    {
        try
        {
            await stopped.ConfigureAwait(false);await nfcComplete.ConfigureAwait(false);
            lock(gate)
            {
                if(!ReferenceEquals(device,owner)||connectionSequence!=sequence||!IsRunning||!transportConnected||IsReadingNfc)return;
                if(connectionProgress is {} progress)StartRuntimeBatteryRefresh(progress);
            }
            NfcTrace("[PRO2_NFC] deferred_battery_worker_restored=true");
        }
        catch(Exception error){NfcTrace("[PRO2_NFC] deferred_battery_restore="+error.GetType().Name);}
    }
    private async Task ReleaseLateNfcSubscriptionAsync(Task<GattCommunicationStatus> pending,GattCharacteristic characteristic,
        BluetoothLEDevice owner,long sequence)
    {
        try
        {
            await pending.ConfigureAwait(false);
            lock(gate)if(!ReferenceEquals(device,owner)||connectionSequence!=sequence)return;
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask(cleanup.Token).WaitAsync(TimeSpan.FromSeconds(3),cleanup.Token).ConfigureAwait(false);
        }
        catch(Exception error){NfcTrace("[PRO2_NFC] late_subscription_cleanup="+error.GetType().Name+" connection_seq="+sequence);}
    }
    private async Task StopNfcReadAsync()
    {
        CancellationTokenSource? operation;Task? done;
        lock(gate){operation=nfcReadCts;done=nfcReadComplete?.Task;}
        try{operation?.Cancel();}catch(ObjectDisposedException){}
        try{if(done!=null)await done.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);}
        catch(TimeoutException){NfcTrace("[PRO2_NFC] owner_shutdown_cleanup_deadline=true");}
    }
}
