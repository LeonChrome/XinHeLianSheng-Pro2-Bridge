using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Y700Switch2V60Viiper;

namespace Pro2WirelessExperiment;

public sealed class ExperimentSession:IAsyncDisposable
{
    private readonly Action<string> show;
    private readonly object protocolGate=new();
    private readonly Channel<string> journal=Channel.CreateBounded<string>(new BoundedChannelOptions(8192){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    private readonly Task journalTask;
    private readonly TaskCompletionSource disposeComplete=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long logDropped;
    private string journalFailure="";
    private readonly CancellationTokenSource lifetime=new();
    private NintendoPhysicalInputSource? source;
    private Semaphore? inputLease;
    private WirelessPro1Protocol? protocol;
    private NintendoNfcTag? snapshot;
    private UdpClient? ipc;
    private IPEndPoint? radioEndpoint;
    private Process? radio;
    private RadioDriverLease? radioLease;
    private Task? receiveTask,producerTask,heartbeatTask,stdoutTask,stderrTask;
    private readonly SemaphoreSlim sendGate=new(1,1);
    private readonly byte[] session=RandomNumberGenerator.GetBytes(16);
    private long sendSequence,receivedSequence,selectedInputs,rumblePackets,rumbleFailures;
    private long invalidIpc,usbBaselineConsumed;
    private Task<NintendoNfcTag>? captureTask;
    private int disposed;
    private bool relayStarted,receiverConnected;
    private volatile bool radioEnded;
    private volatile string radioEndReason="";
    private long tagGeneration;
    private double targetHz=1000.0/15;
    private WirelessPro1Cadence? cadence;
    public string DirectoryPath {get;}
    public bool UsbConnected=>source?.IsRunning==true;
    public bool RadioStarted=>relayStarted;
    public bool RadioFinished=>radioEnded;
    public int RadioExitCode {get;private set;}=-1;
    public bool CanReadNfc=>source?.CanReadUsbNfc==true&&captureTask is not{IsCompleted:false};
    public string Summary=>source==null?"未连接 USB 手柄":source.MetricsSummary;
    public string State=>radioEnded?radioEndReason:receiverConnected?"无线 Pro1 已连接 · 接收端频率待验证":relayStarted?"等待接收设备配对 Pro Controller":UsbConnected?"USB 已连接 · 无线发射尚未启动":"未连接";
    public ExperimentSession(string directory,Action<string>? display=null)
    {
        DirectoryPath=Path.GetFullPath(directory);CreatePrivateDirectory(DirectoryPath);
        show=display??(_=>{});
        journalTask=Task.Run(WriteJournalAsync);
        Log("[EXPERIMENT_BEGIN] version=0.3 windows="+Environment.OSVersion.Version+" x64="+Environment.Is64BitOperatingSystem+" source=existing_assistant physical_usb_only=true startup_driver_changes=0");
    }
    private static void CreatePrivateDirectory(string path)
    {
        if(System.IO.Directory.Exists(path))throw new IOException("本轮日志目录已存在，请选择新的目录，避免混合会话或覆盖标签文件。");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var identity=WindowsIdentity.GetCurrent();var security=new DirectorySecurity();
        // Apply a protected DACL atomically at creation. Reassigning an existing
        // directory's owner unnecessarily required WRITE_OWNER/admin rights.
        security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new(identity.User!,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        var info=new DirectoryInfo(path);info.Create(security);
        var actual=info.GetAccessControl(AccessControlSections.Access);
        if(!actual.AreAccessRulesProtected||actual.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule=>rule.AccessControlType==AccessControlType.Allow&&!rule.IdentityReference.Equals(identity.User)))
            throw new UnauthorizedAccessException("未能创建本用户专用的日志目录；尚未保存会话令牌或标签内容。");
    }
    public void Log(string message)
    {
        string line=DateTimeOffset.Now.ToString("HH:mm:ss.fff ")+message;
        if(!journal.Writer.TryWrite(line))Interlocked.Increment(ref logDropped);
        show(line);
    }
    private async Task WriteJournalAsync()
    {
        string path=Path.Combine(DirectoryPath,"experiment.log");StreamWriter? writer=null;
        try
        {
            writer=new StreamWriter(new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read,65536,true),new UTF8Encoding(false));
            while(await journal.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                for(int i=0;i<256&&journal.Reader.TryRead(out var line);i++)await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                if(writer.BaseStream.Length>16*1024*1024)
                {
                    await writer.DisposeAsync().ConfigureAwait(false);writer=null;File.Move(path,Path.Combine(DirectoryPath,"experiment.previous.log"),true);
                    writer=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true),new UTF8Encoding(false));
                }
            }
        }
        catch(Exception e){journalFailure=e.ToString();show("[JOURNAL_FAILED] "+e.Message);}
        finally{if(writer!=null)await writer.DisposeAsync().ConfigureAwait(false);}
    }
    public async Task ConnectUsbAsync(NintendoHidDevice device,CancellationToken token)
    {
        if(source!=null)throw new InvalidOperationException("本实验已经持有实体手柄。");
        if(device.Kind==NintendoInputKind.Pro1Bluetooth)throw new NotSupportedException("本实验仅接收实体 USB 手柄。");
        if(Mutex.TryOpenExisting(@"Global\Y700Switch2V60ViiperManager",out var manager))
        {
            using(manager)
            {
                bool acquired;
                try{acquired=manager.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
                if(!acquired)throw new IOException("正式助手正在运行，请先从托盘退出，再做独立 USB 实验；尚未发送初始化命令。");
                manager.ReleaseMutex();
            }
        }
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.PhysicalId.ToUpperInvariant())));
        var lease=new Semaphore(1,1,@"Local\PRO2UsbRelayInput_"+key);
        if(!lease.WaitOne(0)){lease.Dispose();throw new IOException("这只 USB 手柄已由另一个实验窗口持有。");}
        inputLease=lease;
        try{source=new(device,Log);await source.ConnectAsync(token).ConfigureAwait(false);source.PrepareForConsumerSession();}
        catch{if(source!=null)await source.DisposeAsync();source=null;ReleaseInputLease();throw;}
        Log("[USB_BASELINE] "+source.MetricsSummary);
        foreach(var adapter in WirelessRelayEnvironment.EnumerateRadios())
            Log("[RELAY_RADIO_ENVIRONMENT] name="+adapter.Name+" instance="+adapter.InstanceId+" service="+adapter.Service+" driver="+adapter.DriverVersion+" ready="+adapter.Ready+" radio_opened=false");
        // Continuously drain real reports even during USB-only diagnostics.
        // An idle consumer queue is not a failed USB link or lost RF packets.
        producerTask=Task.Run(()=>ProduceAsync(lifetime.Token));
    }
    private void ReleaseInputLease()
    {var lease=Interlocked.Exchange(ref inputLease,null);if(lease!=null){lease.Release();lease.Dispose();}}
    public async Task<WirelessRelayRadio> PrepareRadioAsync(WirelessRelayRadio selected,CancellationToken token)
    {
        if(radioLease!=null||relayStarted)throw new InvalidOperationException("本轮已经选择发射器；请停止后再换设备。");
        radioLease=await RadioDriverLease.PrepareAsync(selected,Log,token);
        return WirelessRelayEnvironment.EnumerateRadios().SingleOrDefault(r=>r.InstanceId.Equals(selected.InstanceId,StringComparison.OrdinalIgnoreCase))
            ??throw new IOException("准备后没有重新发现所选发射器；停止测试会请求恢复。");
    }
    public async Task StartRadioAsync(WirelessRelayRadio selected,string executable,string? firmwareFolder,int seconds,CancellationToken token,double reportHz=1000.0/15)
    {
        if(source?.IsRunning!=true)throw new IOException("先连接实体 USB 手柄。");
        if(relayStarted)throw new InvalidOperationException("发射端已经启动。");
        if(reportHz is <20 or >250)throw new ArgumentOutOfRangeException(nameof(reportHz));
        targetHz=reportHz;cadence=new(targetHz,Stopwatch.Frequency);
        var current=WirelessRelayEnvironment.EnumerateRadios().SingleOrDefault(r=>r.InstanceId.Equals(selected.InstanceId,StringComparison.OrdinalIgnoreCase));
        if(current?.Ready!=true)throw new IOException("所选蓝牙当前没有绑定 WinUSB。未更换任何驱动，请先完成可恢复的发射适配器准备。");
        if(!File.Exists(executable))throw new FileNotFoundException("找不到独立原生发射端。",executable);
        if(selected.Realtek&&(string.IsNullOrWhiteSpace(firmwareFolder)||!File.Exists(Path.Combine(firmwareFolder,"rtl8852cu_fw"))))
            throw new IOException("此 Realtek 实验需要匹配的 rtl8852cu_fw 运行固件目录；未开始接管蓝牙。");
        if(selected.Realtek&&Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(firmwareFolder!,"rtl8852cu_fw"),token)))!="9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0")
            throw new IOException("Realtek 运行固件与已验证版本的 SHA256 不符，未接管蓝牙。");
        ipc=new(new IPEndPoint(IPAddress.Loopback,0));
        int managedPort=((IPEndPoint)ipc.Client.LocalEndPoint!).Port;
        using(var reserve=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))radioEndpoint=(IPEndPoint)reserve.Client.LocalEndPoint!;
        string sessionPath=Path.Combine(DirectoryPath,"private-session.bin");await File.WriteAllBytesAsync(sessionPath,session,token);
        var start=new ProcessStartInfo(Path.GetFullPath(executable)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(executable))!};
        void Arg(string flag,string value){start.ArgumentList.Add(flag);start.ArgumentList.Add(value);}
        Arg("--instance",selected.InstanceId);Arg("--session-file",sessionPath);Arg("--output",Path.Combine(DirectoryPath,"radio-result.json"));
        Arg("--port",radioEndpoint.Port.ToString());Arg("--managed-port",managedPort.ToString());Arg("--seconds",Math.Clamp(seconds,15,600).ToString());Arg("--wait-seconds","180");
        if(selected.Realtek)Arg("--firmware-folder",Path.GetFullPath(firmwareFolder!));
        Log("[RADIO_PREFLIGHT] instance="+selected.InstanceId+" service="+selected.Service+" driver_provider="+selected.DriverProvider+" driver_version="+selected.DriverVersion+" driver_date="+selected.DriverDate+" exe_sha256="+Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable,token)))+" system_pairing_imported=false");
        receiveTask=Task.Run(()=>ReceiveAsync(lifetime.Token));
        radio=Process.Start(start)??throw new IOException("原生发射端未启动。");relayStarted=true;
        radioLease?.RecordRadio(radio);
        stdoutTask=Task.Run(()=>PumpLogAsync(radio.StandardOutput,"RADIO"));stderrTask=Task.Run(()=>PumpLogAsync(radio.StandardError,"RADIO_ERROR"));
        heartbeatTask=Task.Run(()=>HeartbeatAsync(lifetime.Token));
        Log(FormattableString.Invariant($"[RELAY_START] identity=Pro_Controller target_vid_pid=057E:2009 physical_input=source_only target_report_hz={targetHz:F3} imu=real_history_hold newest_wire_index=0 output_superseding=recorded receiver_rate_verified=false nfc=read_only_snapshot"));
    }
    private async Task PumpLogAsync(StreamReader reader,string kind)
    {while(await reader.ReadLineAsync().ConfigureAwait(false) is {} line)Log("["+kind+"] "+line);}
    private async Task SendAsync(WirelessRelayMessage kind,byte[] payload,long timestamp,CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] frame=WirelessRelayDatagram.Encode(session,kind,(ulong)++sendSequence,timestamp,payload);
            await ipc!.SendAsync(frame,radioEndpoint!,token).ConfigureAwait(false);
        }
        finally{sendGate.Release();}
    }
    private async Task HeartbeatAsync(CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested)
            {
                if(radio?.HasExited==true)
                {
                    RadioExitCode=radio.ExitCode;
                    Log("[RADIO_EXIT] exit_code="+radio.ExitCode+" receiver_connected="+receiverConnected);receiverConnected=false;radioEnded=true;
                    if(source!=null)Log("[PHYSICAL_AT_RADIO_EXIT] "+source.MetricsSummary);
                    radioEndReason=radio.ExitCode==0?"本轮限时测试已结束 · 正在恢复系统蓝牙":"发射端已退出（代码 "+radio.ExitCode+"）· 正在恢复系统蓝牙";
                    await RestoreRadioAsync();radioEndReason="本轮发射已停止 · 系统蓝牙已恢复（若由本工具切换）";break;
                }
                await SendAsync(WirelessRelayMessage.Heartbeat,[],Stopwatch.GetTimestamp(),token).ConfigureAwait(false);
                await Task.Delay(1000,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception e){radioEndReason=e.Message;radioEnded=true;Log("[HEARTBEAT_FAILED] "+e);}
    }
    private async Task ProduceAsync(CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested)
            {
                // Keep consuming USB while the driver guard restores Bluetooth.
                // Stopping here filled the input queue during cleanup and made
                // successful runs appear to have lost active relay reports.
                bool radioStopped=radio?.HasExited==true;
                if(source!.TryGetNextFresh(out var state,out var age))
                {
                    byte[]? report=null;long selectedTicks=state.SourceTimestampTicks;
                    lock(protocolGate)
                    {
                        if(protocol!=null&&receiverConnected&&!radioStopped)
                        {
                            protocol.Observe(state,state.SourceTimestampTicks);
                            if(cadence!.TrySelect(state,out var selected)){selectedTicks=selected.SourceTimestampTicks;report=protocol.BuildInput(selected,selectedTicks,Stopwatch.Frequency,1000/targetHz);}
                        }
                        else Interlocked.Increment(ref usbBaselineConsumed);
                    }
                    if(report!=null&&Stopwatch.GetElapsedTime(selectedTicks).TotalMilliseconds<=100)
                    {await SendAsync(WirelessRelayMessage.FreshInput,report,selectedTicks,token).ConfigureAwait(false);Interlocked.Increment(ref selectedInputs);}
                }
                else
                {
                    if(!source.IsRunning||!source.TryGetLatest(out _,out var lastAge)||lastAge.TotalMilliseconds>500)
                    {Log("[RELAY_INPUT_STOPPED] status="+source.Status+" stale_guard_ms=500");await RequestRadioStopAsync();break;}
                    await source.WaitForInputAsync(TimeSpan.FromMilliseconds(100),token).ConfigureAwait(false);
                }
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception e){Log("[PRODUCER_FAILED] "+e);await RequestRadioStopAsync();}
    }
    private async Task ReceiveAsync(CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested)
            {
                var packet=await ipc!.ReceiveAsync(token).ConfigureAwait(false);
                if(!packet.RemoteEndPoint.Equals(radioEndpoint)||!WirelessRelayDatagram.TryDecode(packet.Buffer,session,out var frame)||frame!.Sequence<=(ulong)receivedSequence)
                {Interlocked.Increment(ref invalidIpc);continue;}
                receivedSequence=(long)frame.Sequence;
                if(frame.Kind==WirelessRelayMessage.LinkState&&frame.Payload.Length==8)
                {
                    lock(protocolGate)
                    {
                        if(protocol==null){protocol=new WirelessPro1Protocol(frame.Payload[..6],Log);if(snapshot!=null)protocol.SetTag(snapshot);}
                    }
                    receiverConnected=frame.Payload[6]==1;
                    if(frame.Payload[6]>=2){radioEnded=true;radioEndReason="无线链路已停止 · 原因 "+frame.Payload[7]+" · 查看本轮日志";}
                    Log("[RELAY_LINK] state="+frame.Payload[6]+" reason="+frame.Payload[7]+" actual_local_address="+Convert.ToHexString(frame.Payload.AsSpan(0,6)));
                    continue;
                }
                if(frame.Kind!=WirelessRelayMessage.HostOutput)continue;
                if(!source!.TryGetLatest(out var latest,out var age)||age.TotalMilliseconds>500)
                {Log("[HOST_COMMAND_REJECTED] physical_input_stale=true");continue;}
                WirelessPro1Output output;
                lock(protocolGate)output=protocol!.HandleOutput(frame.Payload,latest,Stopwatch.GetTimestamp(),Stopwatch.Frequency);
                // MCU replies occupy the next real input report, matching the
                // selected cadence. Extra zero-IMU 0x31 packets would corrupt
                // sensor timing and inflate the apparent gamepad report rate.
                if(output.Reply!=null&&output.Reply[0]!=0x31)await SendAsync(WirelessRelayMessage.ProtocolReply,output.Reply,Stopwatch.GetTimestamp(),token).ConfigureAwait(false);
                if(output.Rumble!=null)
                {
                    byte[] feedback=new byte[10];output.Rumble.CopyTo(feedback,0);feedback[8]=1;
                    if(Switch1RumbleConverter.TryMap(feedback,out var physical,out string why)&&source.TryWriteOutputPacket(physical,out why))Interlocked.Increment(ref rumblePackets);
                    else{Interlocked.Increment(ref rumbleFailures);Log("[RELAY_RUMBLE_REJECTED] "+why);}
                }
                if(output.CaptureRequested&&CanReadNfc)
                {
                    // Single reader remains active while the host polls. UI
                    // reads and host-triggered reads share the same source gate.
                    _=CaptureFromHostAsync(token);
                }
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception e){Log("[RADIO_RECEIVE_FAILED] "+e);await RequestRadioStopAsync();}
    }
    private async Task CaptureFromHostAsync(CancellationToken token)
    {try{await CaptureNfcAsync(token).ConfigureAwait(false);}catch(Exception e){Log("[HOST_NFC_CAPTURE_FAILED] "+e.Message);}}
    public Task<NintendoNfcTag> CaptureNfcAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(!CanReadNfc)throw new InvalidOperationException("当前无法开始 USB NFC 读取。");
        captureTask=CaptureNfcCoreAsync(token);return captureTask;
    }
    private async Task<NintendoNfcTag> CaptureNfcCoreAsync(CancellationToken token)
    {
        long generation;lock(protocolGate)generation=++tagGeneration;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        var tag=await source!.ReadUsbNfcAsync(linked.Token).ConfigureAwait(false);
        bool applied;lock(protocolGate){applied=generation==tagGeneration;if(applied){snapshot=tag;protocol?.SetTag(tag);}}
        // Keep a verified local dump so a receiver failure doesn't lose the
        // physical read; the snapshot is explicitly separate from live RF.
        var saved=await Task.Run(()=>NintendoNfcExport.Save(Path.Combine(DirectoryPath,"last-read.bin"),tag,false,true),linked.Token).ConfigureAwait(false);
        Log("[RELAY_NFC_CAPTURED] bytes=540 file_sha256="+saved.Sha256+" snapshot_mode=true applied="+applied+" tag_writes=0");return tag;
    }
    public async Task LoadTagAsync(string path,CancellationToken token)
    {
        byte[] raw=await File.ReadAllBytesAsync(path,token).ConfigureAwait(false);
        if(raw.Length is not (540 or 572))throw new IOException("只支持 540 或 572 字节的 NTAG215 文件。");
        byte[] data=raw[..540],uid=[data[0],data[1],data[2],data[4],data[5],data[6],data[7]];
        var tag=new NintendoNfcTag(uid,data,"local_snapshot","file",DateTimeOffset.Now){Signature=raw.Length==572?raw[540..]:[]};
        lock(protocolGate){if(protocol==null)throw new IOException("先启动发射端，等待蓝牙就绪。");++tagGeneration;snapshot=tag;protocol.SetTag(tag);}
        Log("[RELAY_NFC_FILE_LOADED] bytes="+raw.Length+" sha256="+Convert.ToHexString(SHA256.HashData(raw))+" authenticity_verified=false");
    }
    public void RemoveTag()
    {lock(protocolGate){++tagGeneration;snapshot=null;protocol?.SetTag(null);}Log("[RELAY_NFC_REMOVED] snapshot_removed=true tag_writes=0");}
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0){await disposeComplete.Task.ConfigureAwait(false);return;}
        try
        {
            if(ipc!=null&&radio is{HasExited:false})
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try{await SendAsync(WirelessRelayMessage.Stop,[],Stopwatch.GetTimestamp(),stop.Token).ConfigureAwait(false);}catch(Exception e){Log("[RADIO_STOP_REQUEST_FAILED] "+e.Message);}
            }
            lifetime.Cancel();
            if(radio is{HasExited:false})
            {
                using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try{await radio.WaitForExitAsync(wait.Token).ConfigureAwait(false);}catch(OperationCanceledException){Log("[RADIO_STOP_TIMEOUT] action=terminate_own_child");radio.Kill(entireProcessTree:true);await radio.WaitForExitAsync().ConfigureAwait(false);}
            }
            foreach(var task in new[]{receiveTask,producerTask,heartbeatTask,stdoutTask,stderrTask})if(task!=null)try{await task.ConfigureAwait(false);}catch(Exception e){Log("[WORKER_STOP] "+e.GetType().Name);}
            if(captureTask!=null)try{await captureTask.ConfigureAwait(false);}catch(Exception e){Log("[NFC_STOP] "+e.GetType().Name);}
            var physical=source?.Snapshot();if(source!=null)await source.DisposeAsync().ConfigureAwait(false);
            await RestoreRadioAsync();
            object result=new{version="0.3",windows=Environment.OSVersion.Version.ToString(),physical,selectedInputs,usbBaselineConsumed,rumblePackets,rumbleFailures,invalidIpc,
                hostCommands=protocol?.HostCommands,nfcCommands=protocol?.NfcCommands,rejectedCommands=protocol?.RejectedCommands,
                targetHz,cadenceSkipped=cadence?.Skipped,staleTransitions=cadence?.StaleTransitions,
                receiverRateVerified=false,tagWrites=0,radioDriverPrepared=radioLease?.Prepared??false,radioDriverRestored=radioLease?.Restored??false,radioBackup=radioLease?.StateDirectory,radioExit=radio?.ExitCode,logDropped,journalFailure};
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath,"session-result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            Log("[EXPERIMENT_END] selected_inputs="+selectedInputs+" receiver_rate_verified=false radio_driver_prepared="+(radioLease?.Prepared??false)+" radio_driver_restored="+(radioLease?.Restored??false));
        }
        finally
        {
            try
            {
                if(source!=null)try{await source.DisposeAsync().ConfigureAwait(false);}catch(Exception e){Log("[USB_FINAL_CLEANUP] "+e.GetType().Name);}
                ReleaseInputLease();
                try{await RestoreRadioAsync();}catch(Exception e){Log("[RADIO_RESTORE_REQUIRED] "+e.Message);}
                ipc?.Dispose();radio?.Dispose();CryptographicOperations.ZeroMemory(session);
                string secret=Path.Combine(DirectoryPath,"private-session.bin");if(File.Exists(secret))File.Delete(secret);
                source=null;receiverConnected=false;relayStarted=false;
            }
            finally{journal.Writer.TryComplete();await journalTask.ConfigureAwait(false);disposeComplete.TrySetResult();}
        }
    }
    private async Task RequestRadioStopAsync()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try{if(radio is{HasExited:false})await SendAsync(WirelessRelayMessage.Stop,[],Stopwatch.GetTimestamp(),stop.Token).ConfigureAwait(false);}
        catch(Exception e){Log("[FAILURE_STOP_REQUEST] "+e.GetType().Name);}
        lifetime.Cancel();receiverConnected=false;
        await RestoreRadioAsync();
    }
    private async Task RestoreRadioAsync(){if(radioLease!=null&&!radioLease.Restored)await radioLease.RestoreAsync();}
}
