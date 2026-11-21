using System.Diagnostics;
using System.Globalization;
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

public sealed partial class ExperimentSession:IAsyncDisposable
{
    private readonly Action<string> show;
    private readonly string runtimeDirectory;
    private readonly bool integrated;
    private readonly object protocolGate=new();
    private readonly Channel<string> journal=Channel.CreateBounded<string>(new BoundedChannelOptions(8192){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    private readonly Task journalTask;
    private readonly TaskCompletionSource disposeComplete=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource backendReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long ipcPortUnreachable;
    private long logDropped;
    private string journalFailure="";
    private readonly CancellationTokenSource lifetime=new();
    private IGamepadInputSource? source;
    private NintendoPhysicalInputSource? PhysicalSource=>source as NintendoPhysicalInputSource;
    private string SourceMetrics=>(source as IGamepadInputMetricsSource)?.MetricsSummary??source?.Status??"未连接";
    private WirelessPadMode outputMode;
    private WirelessControllerProtocol? alternateProtocol;
    private long feedbackGeneration;
    private volatile bool inputPaused;
    private Task? lightsTask;
    private byte? appliedLights;
    private CancellationTokenSource? feedbackCancellation;
    private Semaphore? inputLease;
    private WirelessPro1Protocol? protocol;
    private NintendoNfcTag? snapshot;
    private string RelayTagPath=>Path.Combine(DirectoryPath,"relay-ntag215.bin");
    private UdpClient? ipc;
    private IPEndPoint? radioEndpoint;
    private Process? radio;
    private RadioDriverLease? radioLease;
    private Task? receiveTask,producerTask,heartbeatTask,stdoutTask,stderrTask;
    private readonly SemaphoreSlim sendGate=new(1,1);
    private readonly byte[] session=RandomNumberGenerator.GetBytes(16);
    private long sendSequence,receivedSequence,selectedInputs,rumblePackets,rumbleFailures;
    private Switch1RumbleStream pro1Rumble = new();
    private long invalidIpc,usbBaselineConsumed;
    private Task<NintendoNfcTag>? captureTask;
    private int disposed;
    private bool relayStarted;
    private volatile bool receiverConnected,receiverRecovering;
    private volatile int pro1Delivery;
    private volatile bool radioEnded;
    private volatile string radioEndReason="";
    // Positive means the UI can confirm this request. A negative value means
    // the same request has been claimed by a click and awaits the native result.
    private int pairingRequest;
    private int pairingResult;
    private long tagGeneration;
    private double targetHz=1000.0/15;
    private WirelessPro1Cadence? cadence;
    public string DirectoryPath {get;}
    public Func<GamepadState,long,GamepadState>? TransformInput {get;set;}
    public bool UsbConnected=>source?.IsRunning==true;
    public bool RadioStarted=>relayStarted;
    public WirelessPadMode OutputMode=>outputMode;
    public bool ReceiverConnected=>receiverConnected;
    public bool ReceiverRecovering=>receiverRecovering&&!radioEnded;
    public bool AwaitingPro1Initialization=>outputMode==WirelessPadMode.Pro1&&receiverConnected&&pro1Delivery==0;
    public bool RadioFinished=>radioEnded;
    public bool DeferSwitchPairing {get;set;}
    public bool PairingConfirmationPending=>CanConfirmSwitchPairing&&Volatile.Read(ref pairingRequest)>0;
    private bool CanConfirmSwitchPairing=>DeferSwitchPairing&&outputMode==WirelessPadMode.Pro1&&relayStarted&&
        !receiverConnected&&!radioEnded&&Volatile.Read(ref disposed)==0;
    public int RadioExitCode {get;private set;}=-1;
    // Corresponds to EXIT_PRO1_KEY_SIZE_UNSUPPORTED in the native backend.
    private const int KeySizeUnsupportedExitCode=23;
    private const string KeySizeUnsupportedMessage="Pro1 蓝牙外发已停止。所选发射器不支持读取连接加密密钥长度（HCI 0x1408）。请选择其他发射器。";
    public bool CanReadNfc=>(PhysicalSource?.CanReadUsbNfc==true||source is Pro2BleInputSource{IsRunning:true})&&captureTask is not{IsCompleted:false};
    public bool HasTag { get { lock(protocolGate)return snapshot!=null; } }
    public double InputHz=>inputPaused?0:(source as IGamepadInputRateSource)?.CurrentParsedRateHz??0;
    public GamepadState? LatestState {get{if(source!=null&&source.TryGetLatest(out var state,out var age)&&age.TotalMilliseconds<500)return state;return null;}}
    public string Firmware=>source is Pro2BleInputSource ble?Pro2DevicePresentation.Firmware(ble.FirmwareInfo,ble.IsTransportConnected):"未读取";
    private double rumbleGain=1;
    public double RumbleGain
    {
        get=>Volatile.Read(ref rumbleGain);
        set{Volatile.Write(ref rumbleGain,double.IsFinite(value)?Math.Clamp(value,0,3):1);PhysicalSource?.SetRumbleGain(rumbleGain);if(source is Pro2BleInputSource ble)ble.SetRumbleGain(rumbleGain);}
    }
    public string Summary=>SourceMetrics;
    public string State
    {
        get
        {
            if(radioEnded)return radioEndReason;
            if(CanConfirmSwitchPairing)
            {
                int request=Volatile.Read(ref pairingRequest);
                if(request>0)return "收到 Switch 配对请求 · 退出配对页后确认";
                if(request<0||Volatile.Read(ref pairingResult)==1)return "正在完成 Switch 配对…";
                if(Volatile.Read(ref pairingResult)==2)return "配对确认超时 · 请重新打开 Switch 的配对页";
                if(Volatile.Read(ref pairingResult) is 3 or 4)return "配对请求已结束 · 可从 Switch 再试一次";
            }
            if(receiverRecovering)return "接收端连接中断 · 正在恢复 HID 连接";
            if(inputPaused)return "实体输入暂时停顿 · 已松开按键，等待恢复";
            if(receiverConnected && outputMode==WirelessPadMode.Pro1)return pro1Delivery switch{
                1=>"已按接收端请求发送 Pro1 完整输入",
                _=>"蓝牙已连接 · 简单输入，等待接收端选择完整报告"
            };
            if(receiverConnected)return WirelessPadModes.Label(outputMode)+" 已连接 · 接收端频率待验证";
            if(UseUpstreamSwitch&&upstreamPageTimeout)return UpstreamPageTimeoutHint;
            if(!relayStarted)return UsbConnected?"实体输入已连接 · 无线发射尚未启动":"未连接";
            if(protocol==null&&alternateProtocol==null)return "发射器正在初始化，请稍候…";
            string device=outputMode==WirelessPadMode.Pro1?"Pro Controller":outputMode==WirelessPadMode.Xbox?"Xbox Wireless Controller":"Wireless Controller";
            return "等待接收设备配对 "+device;
        }
    }
    public ExperimentSession(string directory,Action<string>? display=null,string? runtimeDirectory=null,bool integrated=false)
    {
        this.runtimeDirectory=Path.GetFullPath(runtimeDirectory??AppContext.BaseDirectory);this.integrated=integrated;
        DirectoryPath=Path.GetFullPath(directory);CreatePrivateDirectory(DirectoryPath);
        show=display??(_=>{});
        journalTask=Task.Run(WriteJournalAsync);
        Log("[EXPERIMENT_BEGIN] version=0.3 assistant_version="+SystemBluetoothDiagnostics.CurrentAppVersion+" integrated="+integrated+" windows="+Environment.OSVersion.Version+" x64="+Environment.Is64BitOperatingSystem+" source=existing_assistant physical_usb_only=true startup_driver_changes=0");
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
        if(!integrated&&Mutex.TryOpenExisting(@"Global\Y700Switch2V60ViiperManager",out var manager))
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
        try{var physical=new NintendoPhysicalInputSource(device,Log);physical.SetRumbleGain(RumbleGain);source=physical;await physical.ConnectAsync(token).ConfigureAwait(false);physical.PrepareForConsumerSession();}
        catch{if(source!=null)await source.DisposeAsync();source=null;ReleaseInputLease();throw;}
        upstreamUsbInput=device.Kind is NintendoInputKind.Pro1Usb or NintendoInputKind.Pro2Usb;
        Log("[USB_BASELINE] "+SourceMetrics);
        foreach(var adapter in WirelessRelayEnvironment.EnumerateRadios())
            Log("[RELAY_RADIO_ENVIRONMENT] name="+adapter.Name+" instance="+adapter.InstanceId+" service="+adapter.Service+" driver="+adapter.DriverVersion+" ready="+adapter.Ready+" radio_opened=false");
        // Continuously drain real reports even during USB-only diagnostics.
        // An idle consumer queue is not a failed USB link or lost RF packets.
        producerTask=Task.Run(()=>ProduceAsync(lifetime.Token));
    }
    public async Task ConnectBleAsync(CancellationToken token)
    {
        if(source!=null)throw new InvalidOperationException("本轮已有实体输入。");
        var ble=new Pro2BleInputSource();ble.SetRumbleGain(RumbleGain);source=ble;
        try{await ble.StartAsync(new Progress<string>(Log),token).ConfigureAwait(false);if(!ble.IsRunning)throw new IOException("Pro2 BLE 输入未建立："+ble.Status);ble.PrepareForConsumerSession();}
        catch{await ble.DisposeAsync();source=null;throw;}
        Log("[BLE_RELAY_INPUT] "+SourceMetrics);producerTask=Task.Run(()=>ProduceAsync(lifetime.Token));
    }
    private void ReleaseInputLease()
    {var lease=Interlocked.Exchange(ref inputLease,null);if(lease!=null){lease.Release();lease.Dispose();}}
    public async Task<WirelessRelayRadio> PrepareRadioAsync(WirelessRelayRadio selected,CancellationToken token,bool continuous=false)
    {
        if(radioLease!=null||relayStarted)throw new InvalidOperationException("本轮已经选择发射器；请停止后再换设备。");
        if(UseUpstreamSwitch)ValidateUpstreamRadio(selected);
        radioLease=await RadioDriverLease.PrepareAsync(selected,Log,token,runtimeDirectory,continuous,UseUpstreamSwitch);
        return WirelessRelayEnvironment.EnumerateRadios().SingleOrDefault(r=>r.InstanceId.Equals(selected.InstanceId,StringComparison.OrdinalIgnoreCase))
            ??throw new IOException("准备后没有重新发现所选发射器；停止测试会请求恢复。");
    }
    public async Task StartRadioAsync(WirelessRelayRadio selected,string executable,string? firmwareFolder,int seconds,CancellationToken token,double reportHz=1000.0/15,WirelessPadMode mode=WirelessPadMode.Pro1,bool verifySwitchInitialization=false)
    {
        if(source?.IsRunning!=true)throw new IOException("先连接实体 USB 手柄。");
        if(relayStarted)throw new InvalidOperationException("发射端已经启动。");
        if(!double.IsFinite(reportHz)||reportHz is <20 or >250)throw new ArgumentOutOfRangeException(nameof(reportHz));
        outputMode=mode;targetHz=reportHz;cadence=mode==WirelessPadMode.Pro1?null:new(targetHz,Stopwatch.Frequency);
        var current=WirelessRelayEnvironment.EnumerateRadios().SingleOrDefault(r=>r.InstanceId.Equals(selected.InstanceId,StringComparison.OrdinalIgnoreCase));
        if(current==null)throw new IOException("所选蓝牙发射器已离线，请刷新设备列表。");
        if(UseUpstreamSwitch)
        {
            if(mode!=WirelessPadMode.Pro1||!upstreamUsbInput)throw new IOException("Switch 1 专用外发需要 USB 实体输入和 Pro1 输出。");
            ValidateUpstreamRadio(current);
            if(!current.SwitchReady)throw new IOException("所选蓝牙尚未准备为发射器。");
            await StartUpstreamSwitchAsync(current,seconds,token).ConfigureAwait(false);
            return;
        }
        if(!current.Ready)throw new IOException("所选蓝牙当前没有绑定 WinUSB。请先完成可恢复的发射适配器准备。");
        if(!File.Exists(executable))throw new FileNotFoundException("找不到独立原生发射端。",executable);
        if(selected.Realtek&&(string.IsNullOrWhiteSpace(firmwareFolder)||!File.Exists(Path.Combine(firmwareFolder,"rtl8852cu_fw"))))
            throw new IOException("此 Realtek 实验需要匹配的 rtl8852cu_fw 运行固件目录；未开始接管蓝牙。");
        if(selected.Realtek&&Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(firmwareFolder!,"rtl8852cu_fw"),token)))!="9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0")
            throw new IOException("Realtek 运行固件与已验证版本的 SHA256 不符，未接管蓝牙。");
        ipc=WirelessRelayLocalIpc.Create();
        Log("[RELAY_IPC] transport=loopback_udp port_unreachable_reporting=disabled startup=authenticated_backend_ready_before_heartbeat");
        int managedPort=((IPEndPoint)ipc.Client.LocalEndPoint!).Port;
        using(var reserve=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))radioEndpoint=(IPEndPoint)reserve.Client.LocalEndPoint!;
        string sessionPath=Path.Combine(DirectoryPath,"private-session.bin");await File.WriteAllBytesAsync(sessionPath,session,token);
        var start=new ProcessStartInfo(Path.GetFullPath(executable)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false),WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(executable))!};
        void Arg(string flag,string value){start.ArgumentList.Add(flag);start.ArgumentList.Add(value);}
        Arg("--instance",selected.InstanceId);Arg("--session-file",sessionPath);Arg("--output",Path.Combine(DirectoryPath,"radio-result.json"));
        Arg("--profile",WirelessPadModes.Argument(outputMode));
        if(outputMode==WirelessPadMode.Pro1)Arg("--report-hz",targetHz.ToString("R",CultureInfo.InvariantCulture));
        if(outputMode==WirelessPadMode.Pro1&&DeferSwitchPairing)Arg("--defer-switch-pairing","1");
        if(verifySwitchInitialization)Arg("--require-switch-init","1");
        Arg("--port",radioEndpoint.Port.ToString());Arg("--managed-port",managedPort.ToString());Arg("--seconds",(seconds==0?0:Math.Clamp(seconds,15,600)).ToString());Arg("--wait-seconds","180");
        if(selected.Realtek)Arg("--firmware-folder",Path.GetFullPath(firmwareFolder!));
        Log("[RADIO_PREFLIGHT] instance="+selected.InstanceId+" service="+selected.Service+" driver_provider="+selected.DriverProvider+" driver_version="+selected.DriverVersion+" driver_date="+selected.DriverDate+" exe_sha256="+Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable,token)))+" system_pairing_imported=false");
        receiveTask=Task.Run(()=>ReceiveAsync(lifetime.Token));
        radio=Process.Start(start)??throw new IOException("原生发射端未启动。");relayStarted=true;
        radioLease?.RecordRadio(radio);
        stdoutTask=Task.Run(()=>PumpLogAsync(radio.StandardOutput,"RADIO"));stderrTask=Task.Run(()=>PumpLogAsync(radio.StandardError,"RADIO_ERROR"));
        var readyWait=Stopwatch.StartNew();
        while(!backendReady.Task.IsCompletedSuccessfully)
        {
            token.ThrowIfCancellationRequested();
            if(radio.HasExited)throw new IOException("发射后端在本机通信就绪前退出，代码 "+radio.ExitCode+"；查看本轮日志。");
            if(readyWait.Elapsed.TotalSeconds>=15)throw new IOException("发射后端未在 15 秒内确认本机通信就绪；查看本轮日志，尚未进入外部配对。");
            await Task.Delay(50,token).ConfigureAwait(false);
        }
        heartbeatTask=Task.Run(()=>HeartbeatAsync(lifetime.Token));
        if(outputMode==WirelessPadMode.Pro1)
            Log(FormattableString.Invariant($"[RELAY_START] profile=Pro1 engine=btkeyLib transport=bluetooth_classic_hid protocol_owner=native physical_input=source_only snapshot=canonical_49_byte_0x30_every_fresh_source_report reference_normal_hz=60 requested_upper_cap_hz={targetHz:F3} native_upper_cap_hz={Math.Min(targetHz,60):F3} receiver_rate_verified=false nfc=validated_private_snapshot tag_writes=0 switch_pairing_mode={(DeferSwitchPairing?"confirm_after_menu":"direct")}"));
        else Log(FormattableString.Invariant($"[RELAY_START] profile={outputMode} transport=bluetooth_classic_hid physical_input=source_only target_report_hz={targetHz:F3} output_superseding=recorded receiver_rate_verified=false nfc=not_supported_by_output_identity"));
    }
    private async Task PumpLogAsync(StreamReader reader,string kind)
    {while(await reader.ReadLineAsync().ConfigureAwait(false) is {} line)Log("["+kind+"] "+line);}
    public async Task ConfirmPairingAsync(CancellationToken token)
    {
        if(!CanConfirmSwitchPairing)return;
        int request=Volatile.Read(ref pairingRequest);
        if(request<=0||Interlocked.CompareExchange(ref pairingRequest,-request,request)!=request)return;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        try
        {
            await SendAsync(WirelessRelayMessage.PairingConfirm,[(byte)request],Stopwatch.GetTimestamp(),linked.Token).ConfigureAwait(false);
        }
        catch
        {
            // The UI may retry a failed local send while the same request is live.
            Interlocked.CompareExchange(ref pairingRequest,request,-request);
            throw;
        }
    }
    private async Task SendAsync(WirelessRelayMessage kind,byte[] payload,long timestamp,CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(kind==WirelessRelayMessage.PairingConfirm&&
                (!CanConfirmSwitchPairing||Volatile.Read(ref pairingRequest)!=-payload[0]))return;
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
                    bool keySizeUnsupported=outputMode==WirelessPadMode.Pro1 && RadioExitCode==KeySizeUnsupportedExitCode;
                    Log("[RADIO_EXIT] exit_code="+radio.ExitCode+" receiver_connected="+receiverConnected);
                    receiverConnected=false;
                    if(source!=null)Log("[PHYSICAL_AT_RADIO_EXIT] "+SourceMetrics);
                    radioEndReason=keySizeUnsupported?KeySizeUnsupportedMessage:
                        radio.ExitCode==0?"本轮限时测试已结束 · 正在恢复系统蓝牙":"发射端已退出（代码 "+radio.ExitCode+"）· 正在恢复系统蓝牙";
                    radioEnded=true;
                    if(keySizeUnsupported)Log("[PRO1_RADIO_INCOMPATIBLE] "+KeySizeUnsupportedMessage);
                    await RestoreRadioAsync();
                    if(!keySizeUnsupported)radioEndReason="本轮发射已停止 · 系统蓝牙已恢复（若由本工具切换）";
                    break;
                }
                await SendAsync(WirelessRelayMessage.Heartbeat,[],Stopwatch.GetTimestamp(),token).ConfigureAwait(false);
                await Task.Delay(1000,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception e){
            radioEndReason=outputMode==WirelessPadMode.Pro1 && RadioExitCode==KeySizeUnsupportedExitCode
                ?KeySizeUnsupportedMessage+" "+e.Message:e.Message;
            radioEnded=true;Log("[HEARTBEAT_FAILED] "+e);
        }
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
                if(((IGamepadFreshSequentialInputSource)source!).TryGetNextFresh(out var state,out var age))
                {
                    if(age.TotalMilliseconds>100)continue;
                    if(inputPaused){inputPaused=false;Log("[RELAY_INPUT_RESUMED] same_receiver_link=true synthetic_input_count=0");}
                    if(TransformInput!=null)state=TransformInput(state,state.SourceTimestampTicks);
                    byte[]? report=null;long selectedTicks=state.SourceTimestampTicks;
                    lock(protocolGate)
                    {
                        if(outputMode==WirelessPadMode.Pro1&&protocol!=null&&backendReady.Task.IsCompletedSuccessfully&&!radioEnded&&!radioStopped)
                        {
                            protocol.Observe(state,state.SourceTimestampTicks);
                            report=protocol.BuildRelaySnapshot(state,state.SourceTimestampTicks,Stopwatch.Frequency);
                        }
                        else if(alternateProtocol!=null&&receiverConnected&&!radioStopped)
                        {
                            if(cadence!.TrySelect(state,out var selected)){selectedTicks=selected.SourceTimestampTicks;report=alternateProtocol.BuildInput(selected,selectedTicks);}
                        }
                        else Interlocked.Increment(ref usbBaselineConsumed);
                    }
                    if(report!=null&&Stopwatch.GetElapsedTime(selectedTicks).TotalMilliseconds<=100)
                    {
                        if(UseUpstreamSwitch)await WriteUpstreamFrameAsync(report,token).ConfigureAwait(false);
                        else await SendAsync(WirelessRelayMessage.FreshInput,report,selectedTicks,token).ConfigureAwait(false);
                        Interlocked.Increment(ref selectedInputs);
                        if(alternateProtocol?.AuxiliaryInput is {} extra)await SendAsync(WirelessRelayMessage.ProtocolReply,extra,selectedTicks,token).ConfigureAwait(false);
                        if(alternateProtocol?.BatteryInput is {} battery)await SendAsync(WirelessRelayMessage.ProtocolReply,battery,selectedTicks,token).ConfigureAwait(false);
                    }
                }
                else
                {
                    if(!source.IsRunning)
                    {Log("[RELAY_INPUT_STOPPED] status="+source.Status+" reason=physical_source_closed");await RequestRadioStopAsync();break;}
                    if((!source.TryGetLatest(out _,out var lastAge)||lastAge.TotalMilliseconds>500)&&!inputPaused)
                    {
                        inputPaused=true;Log("[RELAY_INPUT_PAUSED] release_controls=true keep_bluetooth=true input_count_increment=0");
                        byte[]? release;long stamp=Stopwatch.GetTimestamp();
                        lock(protocolGate)release=outputMode==WirelessPadMode.Pro1?protocol?.BuildRelaySnapshot(GamepadState.Neutral(),stamp,Stopwatch.Frequency):alternateProtocol?.BuildInput(GamepadState.Neutral(),stamp);
                        if((receiverConnected||outputMode==WirelessPadMode.Pro1&&backendReady.Task.IsCompletedSuccessfully&&!radioEnded&&!radioStopped)&&release!=null)
                        {
                            if(UseUpstreamSwitch)await WriteUpstreamFrameAsync(release,token).ConfigureAwait(false);
                            else await SendAsync(WirelessRelayMessage.ProtocolReply,release,stamp,token).ConfigureAwait(false);
                            if(alternateProtocol?.AuxiliaryInput is {} guide)await SendAsync(WirelessRelayMessage.ProtocolReply,guide,stamp,token).ConfigureAwait(false);
                        }
                    }
                    await ((IGamepadSignaledInputSource)source).WaitForInputAsync(TimeSpan.FromMilliseconds(100),token).ConfigureAwait(false);
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
                UdpReceiveResult packet;
                try{packet=await ipc!.ReceiveAsync(token).ConfigureAwait(false);}
                catch(SocketException error)when(error.SocketErrorCode==SocketError.ConnectionReset&&!token.IsCancellationRequested)
                {
                    long count=Interlocked.Increment(ref ipcPortUnreachable);
                    if(count<=3)Log("[RELAY_IPC_PORT_UNREACHABLE] socket_error=10054 count="+count+" action=continue_local_receive physical_bluetooth_error=false");
                    await Task.Delay(20,token).ConfigureAwait(false);continue;
                }
                if(!packet.RemoteEndPoint.Equals(radioEndpoint)||!WirelessRelayDatagram.TryDecode(packet.Buffer,session,out var frame)||frame!.Sequence<=(ulong)receivedSequence)
                {Interlocked.Increment(ref invalidIpc);continue;}
                receivedSequence=(long)frame.Sequence;
                if(frame.Kind==WirelessRelayMessage.BackendReady&&frame.Payload.Length==0)
                {if(backendReady.TrySetResult())Log("[RELAY_IPC_READY] authenticated=true native_udp_bound=true");continue;}
                if(frame.Kind==WirelessRelayMessage.LinkState&&frame.Payload.Length==8)
                {
                    byte linkState=frame.Payload[6];
                    if(linkState==7)
                    {
                        if(receiverConnected&&outputMode==WirelessPadMode.Pro1&&frame.Payload[7]<=1){
                            pro1Delivery=frame.Payload[7];
                            Log("[PRO1_DELIVERY] state="+pro1Delivery+" home_input_verified=false");
                        }
                        continue;
                    }
                    if(linkState is 5 or 6)
                    {
                        if(!DeferSwitchPairing||outputMode!=WirelessPadMode.Pro1||receiverConnected)continue;
                        byte reason=frame.Payload[7];
                        if(linkState==5&&reason!=0)
                        {
                            Volatile.Write(ref pairingResult,0);
                            if(Volatile.Read(ref pairingRequest)!=-reason)Volatile.Write(ref pairingRequest,reason);
                            Log("[SWITCH_PAIRING_CONFIRMATION] state=pending request="+reason);
                        }
                        else if(linkState==6&&reason is >=1 and <=4)
                        {
                            Volatile.Write(ref pairingResult,reason);
                            Interlocked.Exchange(ref pairingRequest,0);
                            Log("[SWITCH_PAIRING_CONFIRMATION] state=finished reason="+reason);
                        }
                        continue;
                    }
                    if(linkState>4)continue;
                    Interlocked.Exchange(ref pairingRequest,0);
                    Volatile.Write(ref pairingResult,0);
                    lock(protocolGate)
                    {
                        if(outputMode==WirelessPadMode.Pro1&&(protocol==null||linkState==0))
                        {
                            if(protocol!=null)Log("[WIRELESS_PROTOCOL_RESET] reason=new_hid_handshake prior_host_commands="+protocol.HostCommands+" prior_nfc_commands="+protocol.NfcCommands+" old_report_and_mcu_mode_discarded=true");
                            protocol=new WirelessPro1Protocol(frame.Payload[..6],Log);if(snapshot!=null)protocol.SetTag(snapshot);
                            pro1Rumble = new();
                        }
                        else if(outputMode!=WirelessPadMode.Pro1&&alternateProtocol==null)alternateProtocol=new(outputMode);
                        pro1Delivery=0;receiverConnected=linkState==1;receiverRecovering=linkState==4;
                        if(receiverConnected)
                        {
                            Log("[WIRELESS_CADENCE_RESET] reason=new_hid_session old_skipped="+(cadence?.Skipped??0)+" old_stale_transitions="+(cadence?.StaleTransitions??0)+" old_resyncs="+(cadence?.Resyncs??0));
                            cadence=outputMode==WirelessPadMode.Pro1?null:new(targetHz,Stopwatch.Frequency);
                        }
                    }
                    if(receiverConnected)QueuePlayerLights(1);
                    if(!receiverConnected&&frame.Payload[6]>=2)
                    {
                        feedbackCancellation?.Cancel();Interlocked.Increment(ref feedbackGeneration);
                        WriteFeedback(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"wireless-link-stop"));
                    }
                    if(linkState is 2 or 3){radioEnded=true;radioEndReason="无线链路已停止 · 原因 "+frame.Payload[7]+" · 查看本轮日志";}
                    Log("[RELAY_LINK] state="+frame.Payload[6]+" reason="+frame.Payload[7]+" actual_local_address="+Convert.ToHexString(frame.Payload.AsSpan(0,6)));
                    continue;
                }
                if(frame.Kind!=WirelessRelayMessage.HostOutput)continue;
                if(alternateProtocol!=null)
                {
                    if(!source!.TryGetLatest(out _,out var feedbackAge)||feedbackAge.TotalMilliseconds>500)
                    {Log("[HOST_COMMAND_REJECTED] physical_input_stale=true");continue;}
                    if(alternateProtocol.TryFeedback(frame.Payload,out var feedback,out int duration,out string detail)&&feedback!=null)
                    {
                        long generation=Interlocked.Increment(ref feedbackGeneration);
                        feedbackCancellation?.Cancel();feedbackCancellation?.Dispose();feedbackCancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
                        if(outputMode==WirelessPadMode.Xbox)
                            _=PlayFeedbackAsync(feedback,duration,frame.Payload[7]*10,frame.Payload[8]+1,generation,feedbackCancellation.Token);
                        else WriteFeedback(feedback);
                        Log("[WIRELESS_FEEDBACK] profile="+outputMode+" bytes="+frame.Payload.Length+" duration_ms="+duration+" "+detail);
                    }
                    else Log("[WIRELESS_FEEDBACK_IGNORED] profile="+outputMode+" bytes="+frame.Payload.Length+" reason="+detail);
                    continue;
                }
                if(!source!.TryGetLatest(out var latest,out var age)||age.TotalMilliseconds>500)
                {Log("[HOST_COMMAND_REJECTED] physical_input_stale=true");continue;}
                WirelessPro1Output output;
                lock(protocolGate)
                {
                    output=protocol!.HandleOutput(frame.Payload,latest,Stopwatch.GetTimestamp(),Stopwatch.Frequency);
                }
                if(output.PlayerLights is {} mask)QueuePlayerLights(mask);
                // btkeyLib responds to host commands on the native HID loop.
                // Managed parsing supplies only physical feedback and NFC
                // capture; its 0x21/0x31 replies must never compete on the wire.
                if(output.Rumble!=null)
                {
                    byte[] feedback=new byte[10];output.Rumble.CopyTo(feedback,0);feedback[8]=1;
                    if(pro1Rumble.TryMap(feedback,out var physical,out string why)&&((IGamepadOutputSink)source).TryWriteOutputPacket(physical,out why))Interlocked.Increment(ref rumblePackets);
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
    private void WriteFeedback(Pro2OutputPacket packet)
    {
        if(source is IGamepadOutputSink sink&&sink.TryWriteOutputPacket(packet,out string why))Interlocked.Increment(ref rumblePackets);
        else{Interlocked.Increment(ref rumbleFailures);Log("[WIRELESS_RUMBLE_REJECTED] physical_writer_unavailable_or_rejected=true");}
    }
    private void QueuePlayerLights(byte mask)
    {
        if(PhysicalSource==null||appliedLights==mask||lightsTask is{IsCompleted:false})return;
        lightsTask=ApplyLightsAsync(mask);
    }
    private async Task ApplyLightsAsync(byte mask)
    {
        try
        {
            if(PhysicalSource is {} physical&&await physical.SetPlayerLightsAsync(mask,lifetime.Token).ConfigureAwait(false)){appliedLights=mask;Log("[RELAY_PLAYER_LIGHTS] mask="+mask+" physical_ack=true");}
        }
        catch(Exception error){Log("[RELAY_PLAYER_LIGHTS_FAILED] "+error.GetType().Name+" input_connection_preserved=true");}
    }
    private async Task PlayFeedbackAsync(Pro2OutputPacket packet,int duration,int delay,int cycles,long generation,CancellationToken token)
    {
        try
        {
            if(duration==0){WriteFeedback(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"wireless-duration-zero"));return;}
            if(delay>0)WriteFeedback(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"wireless-start-delay"));
            for(int i=0;i<cycles;i++)
            {
                if(delay>0)await Task.Delay(delay,token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();if(generation!=Interlocked.Read(ref feedbackGeneration))return;
                WriteFeedback(packet);await Task.Delay(duration,token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();if(generation!=Interlocked.Read(ref feedbackGeneration))return;
                WriteFeedback(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"wireless-duration-stop"));
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error){Log("[WIRELESS_FEEDBACK_FAILED] "+error.GetType().Name);}
    }
    private async Task CaptureFromHostAsync(CancellationToken token)
    {
        try{await CaptureNfcAsync(token).ConfigureAwait(false);}
        catch(Exception e)
        {
            if(UseUpstreamSwitch)
            {
                lock(protocolGate)
                    if(snapshot==null&&upstreamTagGeneration==0)upstreamTagProgress="未能读取实体卡。贴稳后点「读取实体 NFC」重试。";
            }
            Log("[HOST_NFC_CAPTURE_FAILED] "+e.Message);
        }
    }
    public Task<NintendoNfcTag> CaptureNfcAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        lock(protocolGate)
        {
            if(!CanReadNfc)throw new InvalidOperationException("当前无法开始 NFC 读取。");
            hostReadAttempted=true;
            captureTask=CaptureNfcCoreAsync(token);return captureTask;
        }
    }
    private async Task<NintendoNfcTag> CaptureNfcCoreAsync(CancellationToken token)
    {
        long generation;lock(protocolGate)generation=++tagGeneration;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        var tag=ValidateRelayTag(PhysicalSource is {} usb?await usb.ReadUsbNfcAsync(linked.Token).ConfigureAwait(false):await ((Pro2BleInputSource)source!).ReadNfcAsync(linked.Token).ConfigureAwait(false));
        // Keep a verified local dump so a receiver failure doesn't lose the
        // physical read; the snapshot is explicitly separate from live RF.
        var saved=await Task.Run(()=>NintendoNfcExport.Save(Path.Combine(DirectoryPath,"last-read.bin"),tag,false,true),linked.Token).ConfigureAwait(false);
        bool applied=await PublishRelayTagAsync(tag,generation,linked.Token).ConfigureAwait(false);
        Log("[RELAY_NFC_CAPTURED] bytes=540 file_sha256="+saved.Sha256+" snapshot_mode=true applied="+applied+" tag_writes=0");return tag;
    }
    public async Task LoadTagAsync(string path,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        long generation;lock(protocolGate){if(protocol==null||radioEnded)throw new IOException("先启动发射端，等待蓝牙就绪。");hostReadAttempted=true;generation=++tagGeneration;}
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        byte[] raw;
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,true))
        {
            if(file.Length is not (540 or 572))throw new IOException("只支持 540 或 572 字节的 NTAG215 文件。");
            raw=new byte[(int)file.Length];
            await file.ReadExactlyAsync(raw,linked.Token).ConfigureAwait(false);
        }
        byte[] data=raw[..540],uid=[data[0],data[1],data[2],data[4],data[5],data[6],data[7]];
        var tag=ValidateRelayTag(new NintendoNfcTag(uid,data,"local_snapshot","file",DateTimeOffset.Now){Signature=raw.Length==572?raw[540..]:[]});
        bool applied=await PublishRelayTagAsync(tag,generation,linked.Token).ConfigureAwait(false);
        Log("[RELAY_NFC_FILE_LOADED] bytes="+raw.Length+" native_snapshot_bytes=540 applied="+applied+" sha256="+Convert.ToHexString(SHA256.HashData(raw))+" authenticity_verified=false tag_contents_logged=false");
    }
    private async Task<bool> PublishRelayTagAsync(NintendoNfcTag tag,long generation,CancellationToken token)
    {
        if(UseUpstreamSwitch)return await PublishUpstreamTagAsync(tag,generation,token).ConfigureAwait(false);
        string temporary=await WriteRelayTagTemporaryAsync(tag,token).ConfigureAwait(false);
        try{lock(protocolGate){token.ThrowIfCancellationRequested();return ApplyRelayTagLocked(tag,generation,temporary);}}
        finally{DeleteRelayTagTemporary(temporary);}
    }
    public async Task RemoveTagAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        long generation;
        lock(protocolGate){hostReadAttempted=true;generation=++tagGeneration;}
        if(UseUpstreamSwitch)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
            if(!await PublishUpstreamTagAsync(null,generation,linked.Token).ConfigureAwait(false))return;
        }
        else lock(protocolGate)
        {
            if(generation!=tagGeneration)return;
            ClearLocalTagLocked();
        }
        Log("[RELAY_NFC_REMOVED] snapshot_removed=true native_snapshot_removed=true tag_writes=0");
    }
    private void ClearLocalTagLocked()
    {
        File.Delete(RelayTagPath);snapshot=null;protocol?.SetTag(null);
    }
    private NintendoNfcTag ValidateRelayTag(NintendoNfcTag tag)
    {
        var copy=tag with{Uid=(byte[])tag.Uid.Clone(),Data=(byte[])tag.Data.Clone(),Signature=(byte[])tag.Signature.Clone()};
        if(UseUpstreamSwitch){ValidateSwitchTag(copy);return copy;}
        if(!Pro1NfcProtocol.IsNtag215(copy.Data,copy.Uid))throw new InvalidDataException("需要完整且通过 UID/BCC 校验的 540 字节 NTAG215 快照。");
        return copy;
    }
    private async Task<string> WriteRelayTagTemporaryAsync(NintendoNfcTag tag,CancellationToken token)
    {
        // The fresh session directory's protected user-only DACL is inherited
        // by both files. System.IO keeps Unicode session paths intact; native
        // receives the same directory through its UTF-8 session-file argument.
        string temporary=Path.Combine(DirectoryPath,".relay-ntag215-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,true))
            {await stream.WriteAsync(tag.Data,token).ConfigureAwait(false);await stream.FlushAsync(token).ConfigureAwait(false);}
            byte[] written=await File.ReadAllBytesAsync(temporary,token).ConfigureAwait(false);
            if(!written.AsSpan().SequenceEqual(tag.Data)||!Pro1NfcProtocol.IsNtag215(written,tag.Uid))throw new IOException("NFC 私有快照临时文件校验失败。");
            return temporary;
        }
        catch{DeleteRelayTagTemporary(temporary);throw;}
    }
    private bool ApplyRelayTagLocked(NintendoNfcTag tag,long generation,string temporary)
    {
        if(generation!=tagGeneration||Volatile.Read(ref disposed)!=0)return false;
        File.Move(temporary,RelayTagPath,true);
        snapshot=ValidateRelayTag(tag);protocol?.SetTag(snapshot);
        return true;
    }
    private void DeleteRelayTagTemporary(string path)
    {
        try{File.Delete(path);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException)
        {Log("[RELAY_NFC_TEMP_CLEANUP_FAILED] error="+error.GetType().Name+" tag_contents_logged=false");}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0){await disposeComplete.Task.ConfigureAwait(false);return;}
        try
        {
            feedbackCancellation?.Cancel();Interlocked.Increment(ref feedbackGeneration);
            if(ipc!=null&&radio is{HasExited:false})
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try{await SendAsync(WirelessRelayMessage.Stop,[],Stopwatch.GetTimestamp(),stop.Token).ConfigureAwait(false);}catch(Exception e){Log("[RADIO_STOP_REQUEST_FAILED] "+e.Message);}
            }
            lifetime.Cancel();
            if(UseUpstreamSwitch)CloseUpstreamInput();
            if(radio is{HasExited:false})
            {
                using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try{await radio.WaitForExitAsync(wait.Token).ConfigureAwait(false);}catch(OperationCanceledException){Log("[RADIO_STOP_TIMEOUT] action=terminate_own_child");radio.Kill(entireProcessTree:true);await radio.WaitForExitAsync().ConfigureAwait(false);}
            }
            foreach(var task in new[]{receiveTask,producerTask,heartbeatTask,stdoutTask,stderrTask})if(task!=null)try{await task.ConfigureAwait(false);}catch(Exception e){Log("[WORKER_STOP] "+e.GetType().Name);}
            if(captureTask!=null)try{await captureTask.ConfigureAwait(false);}catch(Exception e){Log("[NFC_STOP] "+e.GetType().Name);}
            if(lightsTask!=null)await lightsTask.ConfigureAwait(false);
            object? physical=PhysicalSource?.Snapshot();if(source is Pro2BleInputSource ble)physical=ble.GetRateEvidence();if(source!=null)await source.DisposeAsync().ConfigureAwait(false);
            await RestoreRadioAsync();
            object result=new{version="0.3",assistantVersion=SystemBluetoothDiagnostics.CurrentAppVersion,integrated,windows=Environment.OSVersion.Version.ToString(),physical,selectedInputs,usbBaselineConsumed,rumblePackets,rumbleFailures,invalidIpc,ipcPortUnreachable,backendIpcReady=backendReady.Task.IsCompletedSuccessfully,
                hostCommands=protocol?.HostCommands,nfcCommands=protocol?.NfcCommands,rejectedCommands=protocol?.RejectedCommands,
                outputMode=outputMode.ToString(),engine=UseUpstreamSwitch?"upstream_realtek_usb":outputMode==WirelessPadMode.Pro1?"btkeyLib":"managed_multimode",targetHz,
                referenceNormalHz=outputMode==WirelessPadMode.Pro1?60:(int?)null,requestedUpperCapHz=outputMode==WirelessPadMode.Pro1?targetHz:(double?)null,
                nativeUpperCapHz=outputMode==WirelessPadMode.Pro1&&!UseUpstreamSwitch?Math.Min(targetHz,60):(double?)null,sourceSnapshotUpdates=outputMode==WirelessPadMode.Pro1?selectedInputs:(long?)null,
                upstreamUsbFrames=UseUpstreamSwitch?Interlocked.Read(ref upstreamFramesConsumed):(long?)null,
                upstreamTransportObserved=UseUpstreamSwitch?UpstreamTransportObserved:(bool?)null,
                upstreamHomeObserved=ProbeUpstreamHome?UpstreamHomeObserved:(bool?)null,
                upstreamNfc=UseUpstreamSwitch?new {hostScans=upstreamScans,readRequests=upstreamReads,completeRepliesSent=upstreamDataSent}:null,
                cadenceSkipped=cadence?.Skipped,staleTransitions=cadence?.StaleTransitions,
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
                try{lock(protocolGate)ClearLocalTagLocked();}catch(Exception error)when(error is IOException or UnauthorizedAccessException){Log("[RELAY_NFC_PRIVATE_CLEANUP_FAILED] error="+error.GetType().Name+" tag_contents_logged=false");}
                source=null;receiverConnected=false;receiverRecovering=false;relayStarted=false;
            }
            finally{journal.Writer.TryComplete();await journalTask.ConfigureAwait(false);disposeComplete.TrySetResult();}
        }
    }
    private async Task RequestRadioStopAsync()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try{if(UseUpstreamSwitch)CloseUpstreamInput();else if(radio is{HasExited:false})await SendAsync(WirelessRelayMessage.Stop,[],Stopwatch.GetTimestamp(),stop.Token).ConfigureAwait(false);}
        catch(Exception e){Log("[FAILURE_STOP_REQUEST] "+e.GetType().Name);}
        // Keep the USB baseline consumer alive until explicit Stop/Dispose.
        // A failed radio must not leave an unconsumed queue and a misleading
        // connected screen while the window remains open.
        receiverConnected=false;radioEnded=true;radioEndReason="发射已停止，正在恢复所选蓝牙；查看本轮错误记录。";
        if(radio is{HasExited:false})
        {
            using var grace=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try{await radio.WaitForExitAsync(grace.Token).ConfigureAwait(false);}
            catch(OperationCanceledException){Log("[RADIO_FAILURE_STOP_TIMEOUT] graceful_wait_ms=8000 action=guarded_exact_child_cleanup");}
        }
        await RestoreRadioAsync();
    }
    private async Task RestoreRadioAsync(){if(radioLease!=null&&!radioLease.Restored)await radioLease.RestoreAsync();}
}
