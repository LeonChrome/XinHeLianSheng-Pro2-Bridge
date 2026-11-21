using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Y700Switch2V60Viiper;

namespace Pro2WirelessExperiment;

public sealed partial class ExperimentSession
{
    public const string UpstreamFilename="Pro1UpstreamComparisonRealtek.exe";
    private const string LegacyComparisonRadio=@"USB\VID_0BDA&PID_0852\00E04C000001";
    public bool UseUpstreamSwitch {get;init;}
    public bool ProbeUpstreamHome {get;init;}
    public bool UpstreamHomeObserved {get;private set;}
    private bool upstreamHomeEvidence;
    public string? UpstreamPairingState {get;init;}
    private bool upstreamUsbInput;
    private Stream? upstreamPipe;
    private string? pairingReference;
    private bool upstreamUsingPriorState,upstreamReferenceSaved,upstreamDialAccepted;
    private volatile bool upstreamPageTimeout;
    private const string UpstreamPageTimeoutHint="Switch 未响应回连，请唤醒主机并打开「更改握法／顺序」。";
    private bool upstreamPairSeen,upstreamFullReportSeen,upstreamHostOutputSeen;
    private long upstreamFramesConsumed;
    public bool UpstreamTransportObserved=>upstreamPairSeen&&upstreamFullReportSeen&&upstreamHostOutputSeen&&
        Interlocked.Read(ref upstreamFramesConsumed)>0&&Interlocked.Read(ref selectedInputs)>0;

    private static void ValidateUpstreamRadio(WirelessRelayRadio selected)
    {
        if(!WirelessRelayEnvironment.IsSupportedUsbInstance(selected.InstanceId)||(!selected.SwitchReady&&!selected.SwitchCanPrepare))
            throw new IOException(selected.SwitchHint);
    }

    public static string SwitchPairingFolder(string instance)
    {
        if(!WirelessRelayEnvironment.IsSupportedUsbInstance(instance))throw new ArgumentException("无效的 USB 发射器实例。",nameof(instance));
        if(instance.Equals(LegacyComparisonRadio,StringComparison.OrdinalIgnoreCase))return "upstream-realtek-00E04C000001";
        return "upstream-switch-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(instance.ToUpperInvariant())));
    }

    private string GetPrivateUpstreamState(string instance)
    {
        string root=RadioDriverLease.Root;
        string stable=Path.Combine(root,SwitchPairingFolder(instance));
        bool legacyRadio=instance.Equals(LegacyComparisonRadio,StringComparison.OrdinalIgnoreCase);
        if(UpstreamPairingState!=null&&!legacyRadio)throw new IOException("旧测试配对目录只适用于创建它的发射器。");
        string path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(UpstreamPairingState??stable));
        if(!string.Equals(Path.GetDirectoryName(path),root,StringComparison.OrdinalIgnoreCase))
            throw new IOException("配对记录仅可从当前用户的发射器备份目录复用。");
        for(string? dir=path;dir!=null;dir=Path.GetDirectoryName(dir))
            if(Directory.Exists(dir)&&File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("配对目录路径包含重定向链接。");
        if(!Directory.Exists(path))
        {
            if(UpstreamPairingState!=null)throw new DirectoryNotFoundException(path);
            CreatePrivateDirectory(path);
        }
        using var identity=WindowsIdentity.GetCurrent();
        void Check(string entry,bool rootDirectory=false)
        {
            if(File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))throw new IOException("配对记录包含重定向链接。");
            FileSystemSecurity acl=Directory.Exists(entry)?new DirectoryInfo(entry).GetAccessControl():new FileInfo(entry).GetAccessControl();
            if(rootDirectory&&!acl.AreAccessRulesProtected||!Equals(identity.User,acl.GetOwner(typeof(SecurityIdentifier)))||
                acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r=>r.AccessControlType==AccessControlType.Allow&&!Equals(identity.User,r.IdentityReference)))
                throw new UnauthorizedAccessException("配对记录权限异常，请使用当前用户的私有目录。");
        }
        if(!Directory.Exists(stable))CreatePrivateDirectory(stable);
        Check(stable,true);
        pairingReference=Path.Combine(stable,"paired-store.txt");
        if(legacyRadio&&File.Exists(pairingReference))
        {
            Check(pairingReference);
            string saved=File.ReadAllText(pairingReference).Trim();
            if(UpstreamPairingState==null&&saved.Length>0&&saved.IndexOfAny(Path.GetInvalidFileNameChars())<0&&
                saved!="."&&saved!=".."&&Directory.Exists(Path.Combine(root,saved)))path=Path.Combine(root,saved);
        }
        Check(path,true);
        upstreamUsingPriorState=!path.Equals(stable,StringComparison.OrdinalIgnoreCase);
        string data=Path.Combine(path,"PRO2-Upstream-Comparison");
        if(Directory.Exists(data))
        {
            Check(data);
            string db=Path.Combine(data,"btstack");
            if(Directory.Exists(db))
            {
                Check(db);
                foreach(string entry in Directory.EnumerateFileSystemEntries(db))Check(entry);
            }
        }
        return path;
    }

    private async Task StartUpstreamSwitchAsync(WirelessRelayRadio selected,int seconds,CancellationToken token)
    {
        string executable=Path.Combine(runtimeDirectory,UpstreamFilename);
        if(!File.Exists(executable))throw new FileNotFoundException("缺少 Switch 1 专用转发组件。",executable);
        var start=new ProcessStartInfo(executable){
            UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=runtimeDirectory,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false)
        };
        start.Environment["LOCALAPPDATA"]=GetPrivateUpstreamState(selected.InstanceId);
        start.Environment.Remove("PRO2_REALTEK_RESUME_HOME_SESSION");
        if(upstreamUsingPriorState)start.Environment["PRO2_REALTEK_RESUME_HOME_SESSION"]="1";
        start.ArgumentList.Add("--relay-stdio");start.ArgumentList.Add("--log");start.ArgumentList.Add(Path.Combine(DirectoryPath,"upstream.log"));
        start.ArgumentList.Add("--instance");start.ArgumentList.Add(selected.InstanceId);
        if(ProbeUpstreamHome)
        {
            if(integrated)throw new InvalidOperationException("自动 HOME 仅允许在单独的诊断命令中运行。");
            start.ArgumentList.Add("--probe-home");
        }
        radio=Process.Start(start)??throw new IOException("无法启动 Switch 1 发射端。");
        relayStarted=true;
        upstreamPipe=radio.StandardInput.BaseStream;
        radioLease?.RecordRadio(radio);
        lock(protocolGate)protocol=new WirelessPro1Protocol(new byte[6]);
        stdoutTask=Task.Run(()=>ReadUpstreamStatusAsync(radio.StandardOutput));
        stderrTask=Task.Run(()=>PumpLogAsync(radio.StandardError,"UPSTREAM_ERROR"));
        heartbeatTask=Task.Run(()=>WatchUpstreamAsync(radio,seconds,lifetime.Token));
        await backendReady.Task.WaitAsync(TimeSpan.FromSeconds(30),token).ConfigureAwait(false);
        NintendoNfcTag? initial;long revision;
        lock(protocolGate){initial=snapshot;revision=tagGeneration;}
        if(initial!=null)await PublishUpstreamTagAsync(initial,revision,token).ConfigureAwait(false);
        Log("[RELAY_START] profile=Pro1 engine=btkeyLib_upstream physical_input=USB transport=stdin report_clock=upstream nfc_relay=read_only_snapshot");
    }

    private async Task ReadUpstreamStatusAsync(StreamReader reader)
    {
        while(await reader.ReadLineAsync().ConfigureAwait(false) is {} line)
        {
            int prefix=line.IndexOf(" ms  ",StringComparison.Ordinal);
            string message=prefix>=0?line[(prefix+5)..]:line;
            if(message.StartsWith("UPSTREAM_RUMBLE hex=",StringComparison.Ordinal))
            {
                string hex=message["UPSTREAM_RUMBLE hex=".Length..].Split(' ')[0];
                if(hex.Length!=16||!hex.All(Uri.IsHexDigit)||source?.TryGetLatest(out _,out var age)!=true||age.TotalMilliseconds>500)continue;
                var feedback=new byte[10];Convert.FromHexString(hex).CopyTo(feedback,0);feedback[8]=1;
                if(pro1Rumble.TryMap(feedback,out var mapped,out _)&&((IGamepadOutputSink)source).TryWriteOutputPacket(mapped,out _))
                    Interlocked.Increment(ref rumblePackets);
                else Interlocked.Increment(ref rumbleFailures);
                continue;
            }
            Log("[UPSTREAM] "+line);
            if(message.StartsWith("NFC_",StringComparison.Ordinal))ObserveUpstreamNfc(message);
            else if(message.StartsWith("RADIO_READY ",StringComparison.Ordinal))backendReady.TrySetResult();
            else if(message=="SAVED_RECEIVER_CONNECT status=0x00"||message.StartsWith("SAVED_RECEIVER_CONNECT status=0x00 ",StringComparison.Ordinal))
            {
                // The pinned backend checks a saved receiver and its bond before
                // it submits the outgoing request. Acceptance proves no link yet.
                upstreamDialAccepted=true;
                if(message.Contains(" bond_source=prior_home_proof",StringComparison.Ordinal)||
                    message.Contains(" bond_source=saved_handshake",StringComparison.Ordinal))
                    SaveUpstreamPairingReference();
            }
            else if(upstreamDialAccepted&&message.StartsWith("SAVED_RECEIVER_PAGE_RESULT ",StringComparison.Ordinal))
            {
                var fields=message.Split(' ');
                if(fields.Contains("status=0x04"))upstreamPageTimeout=true;
                else if(fields.Contains("status=0x00"))upstreamPageTimeout=false;
            }
            else if(message=="UPSTREAM_GAMEPAD_PAIRED")
            {
                upstreamPageTimeout=false;upstreamPairSeen=true;pro1Delivery=1;receiverConnected=true;QueuePlayerLights(1);
                SaveUpstreamPairingReference();
            }
            else if(message.StartsWith("ACL_DOWN ",StringComparison.Ordinal))
            {
                pro1Rumble = new();
                receiverConnected=false;pro1Delivery=0;
                WriteFeedback(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"wireless-link-stop"));
            }
            else if(ProbeUpstreamHome&&message.StartsWith("HOME_TEST_RESULT ",StringComparison.Ordinal))
            {
                string[] fields=message.Split(' ');
                long Value(string key)
                {
                    string? entry=Array.Find(fields,field=>field.StartsWith(key,StringComparison.Ordinal));
                    return entry!=null&&long.TryParse(entry.AsSpan(key.Length),out long value)?value:-1;
                }
                upstreamHomeEvidence=fields.Contains("requested=1")&&fields.Contains("press_report=1")&&
                    fields.Contains("release_requested=1")&&fields.Contains("release_report=1")&&fields.Contains("acl_up=1")&&
                    fields.Contains("hid_paired=1")&&fields.Contains("peer_closed_interrupt=0")&&
                    Value("elapsed_after_request_ms=")>=60000&&Value("outgoing_reports_after_request=")>0&&Value("host_output_after_request=")>0;
            }
            else if(message.StartsWith("STATUS ",StringComparison.Ordinal)||message.StartsWith("END ",StringComparison.Ordinal))
            {
                if(ProbeUpstreamHome&&message.StartsWith("END reason=physical_relay_home_observed_60s ",StringComparison.Ordinal))
                    UpstreamHomeObserved=upstreamHomeEvidence;
                foreach(string field in message.Split(' '))
                {
                    if(field.StartsWith("reports30=")&&uint.TryParse(field.AsSpan(10),out uint reports)&&reports>0)upstreamFullReportSeen=true;
                    if(field.StartsWith("peer_output=")&&uint.TryParse(field.AsSpan(12),out uint received)&&received>0)upstreamHostOutputSeen=true;
                }
            }
            else if(message.StartsWith("RELAY_INPUT relay_frames=",StringComparison.Ordinal))
            {
                string value=message["RELAY_INPUT relay_frames=".Length..].Split(' ')[0];
                if(long.TryParse(value,out var frames))Interlocked.Exchange(ref upstreamFramesConsumed,frames);
            }
        }
    }

    private void SaveUpstreamPairingReference()
    {
        if(UpstreamPairingState==null||pairingReference==null||upstreamReferenceSaved)return;
        string temporary=pairingReference+"."+Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary,Path.GetFileName(Path.TrimEndingDirectorySeparator(UpstreamPairingState)));
            File.Move(temporary,pairingReference,true);
            upstreamReferenceSaved=true;
            Log("[UPSTREAM_BOND_REFERENCE] saved=true scope=current_user_exact_radio");
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){Log("[UPSTREAM_BOND_REFERENCE] "+e.Message);}
        finally
        {
            try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    private async Task WatchUpstreamAsync(Process child,int seconds,CancellationToken token)
    {
        if(seconds>0)
        {
            var exit=child.WaitForExitAsync(token);
            if(await Task.WhenAny(exit,Task.Delay(TimeSpan.FromSeconds(seconds),token)).ConfigureAwait(false)!=exit)
            {token.ThrowIfCancellationRequested();CloseUpstreamInput();}
        }
        await child.WaitForExitAsync(token).ConfigureAwait(false);
        if(stdoutTask!=null)await stdoutTask.ConfigureAwait(false);
        RadioExitCode=child.ExitCode;
        receiverConnected=false;
        radioEndReason=upstreamPageTimeout&&!upstreamPairSeen?UpstreamPageTimeoutHint:
            RadioExitCode switch {
                0=>"Switch 转发已结束",
                24=>"所选适配器缺少经典蓝牙（BR/EDR）支持，请选择其他发射器。",
                65=>"此适配器需要配套固件，当前版本尚未提供。",
                _=>"Switch 转发已停止 · 退出码 "+RadioExitCode+"，请查看本轮日志"
            };
        backendReady.TrySetException(new IOException(radioEndReason));
        radioEnded=true;
        Log("[UPSTREAM_EXIT] code="+RadioExitCode+" source_frames="+Interlocked.Read(ref selectedInputs)+" paired="+upstreamPairSeen+
            " native_consumed="+Interlocked.Read(ref upstreamFramesConsumed)+" reports30_seen="+upstreamFullReportSeen+
            " peer_output_seen="+upstreamHostOutputSeen+" transport_observed="+UpstreamTransportObserved);
        await RestoreRadioAsync().ConfigureAwait(false);
    }

    public static byte[] EncodeUpstreamSwitchFrame(ReadOnlySpan<byte> snapshot)
    {
        if(snapshot.Length!=49||snapshot[0]!=0x30)throw new ArgumentException("Pro1 完整报告格式无效。",nameof(snapshot));
        byte[] frame=new byte[28];
        "P1IN"u8.CopyTo(frame);
        snapshot.Slice(3,3).CopyTo(frame.AsSpan(4));
        for(int stick=0;stick<2;stick++)
        {
            var packed=snapshot[(6+3*stick)..];
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8+4*stick),(ushort)(packed[0]|(packed[1]&15)<<8));
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10+4*stick),(ushort)(packed[1]>>4|packed[2]<<4));
        }
        snapshot.Slice(13,12).CopyTo(frame.AsSpan(16));
        return frame;
    }

    private async Task WriteUpstreamFrameAsync(byte[] report,CancellationToken token)
    {
        var pipe=Volatile.Read(ref upstreamPipe);
        if(pipe==null)return;
        try { await WriteUpstreamPayloadAsync(EncodeUpstreamSwitchFrame(report),token).ConfigureAwait(false); }
        catch(Exception e)when((e is IOException or ObjectDisposedException)&&(Volatile.Read(ref upstreamPipe)!=pipe||radio?.HasExited==true||token.IsCancellationRequested)){}
    }

    private void CloseUpstreamInput()
    {
        Interlocked.Exchange(ref upstreamPipe,null)?.Dispose();
        lock(protocolGate)upstreamTagAck?.TrySetResult(false);
    }
}
