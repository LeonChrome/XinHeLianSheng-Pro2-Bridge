using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Pro2WirelessExperiment;
using Y700Switch2V60Viiper;

internal static class WirelessRelayIntegrationRegressionTests
{
    public static async Task RunAsync(Action<bool,string> check,string output)
    {
        check(WirelessRelayRuntime.IsAvailable,"single EXE includes the optional relay runtime");
        string root=Path.Combine(output,"embedded-relay-"+Guid.NewGuid().ToString("N"));
        string extracted=WirelessRelayRuntime.EnsureExtracted(root);
        foreach(string required in new[]{"pro2-wireless-pro1-radio.exe","RadioDriver.ps1","BTstack-LICENSE.txt","firmware/rtl8852cu_fw"})
            check(File.Exists(Path.Combine(extracted,required)),"embedded relay file extracted: "+required);
        string helper=Path.Combine(extracted,"RadioDriver.ps1");
        byte[] expected=SHA256.HashData(File.ReadAllBytes(helper));
        File.WriteAllText(helper,"damaged cached helper fixture");
        WirelessRelayRuntime.EnsureExtracted(root);
        check(SHA256.HashData(File.ReadAllBytes(helper)).SequenceEqual(expected),"modified cached driver helper is repaired from the embedded hash-checked bundle");
        check(!Directory.EnumerateFiles(extracted,"*.tmp",SearchOption.AllDirectories).Any(),"atomic runtime extraction leaves no partial files");
        var vm=new MainViewModel(previewOnly:true);
        try
        {
            check(await vm.EnterWirelessRelayModeAsync(),"existing manager can transfer exclusive input ownership to relay mode");
            check(vm.WirelessRelayModeActive&&!vm.CanOperate&&!vm.CanStart&&!vm.CanManageRuntime,"local output and runtime actions blocked while the relay owns input");
            check(!await vm.EnterWirelessRelayModeAsync(),"a second relay window cannot acquire input ownership again");
            vm.ConnectPro2InputCommand.Execute(null);
            check(!vm.Running&&!vm.AutoReconnectEnabled,"local connect command cannot restart BLE or virtual USB while relaying");
            vm.ExitWirelessRelayMode();
            check(!vm.WirelessRelayModeActive&&vm.CanOperate&&!vm.AutoReconnectEnabled,"leaving relay restores local controls without automatic reconnection");
        }
        finally{await vm.ShutdownAsync();}
        await TestTransientLinkAsync(check,output,extracted);
        await TestUpstreamInputPipeAsync(check,output);
        await UpstreamNfcRegressionTests.RunAsync(check,output);
        await TestKeySizeFailureMessageAsync(check,output,WirelessPadMode.Pro1);
        await TestKeySizeFailureMessageAsync(check,output,WirelessPadMode.Xbox);
    }
    private static async Task TestUpstreamInputPipeAsync(Action<bool,string> check,string output)
    {
        var raw=new byte[49];raw[0]=0x30;
        new byte[]{0x48,0x10,0x80,0x23,0x61,0x45,0x00,0xf8,0xff}.CopyTo(raw,3);
        for(int i=0;i<12;i++)raw[13+i]=(byte)(240+i);
        byte[] expected=Convert.FromHexString("5031494E48108000230156040008FF0FF0F1F2F3F4F5F6F7F8F9FAFB");
        check(ExperimentSession.EncodeUpstreamSwitchFrame(raw).SequenceEqual(expected),"Realtek relay preserves physical buttons, packed sticks and signed motion without changing wire axes");
        await using var relay=new ExperimentSession(Path.Combine(output,"upstream-pipe-"+Guid.NewGuid().ToString("N")))
            {UseUpstreamSwitch=true,UpstreamPairingState=Path.Combine(output,"prior-home-proof-fixture")};
        using var sink=new MemoryStream();using var buffered=new BufferedStream(sink,4096);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var pipe=typeof(ExperimentSession).GetField("upstreamPipe",flags)!;pipe.SetValue(relay,buffered);
        await (Task)typeof(ExperimentSession).GetMethod("WriteUpstreamFrameAsync",flags)!.Invoke(relay,[raw,CancellationToken.None])!;
        check(sink.ToArray().SequenceEqual(expected),"one 28-byte USB sample reaches the pipe reader without waiting for another frame");
        check(!relay.UpstreamTransportObserved,"no physical Switch transport success is reported from source writes alone");
        pipe.SetValue(relay,null);
        typeof(ExperimentSession).GetField("selectedInputs",flags)!.SetValue(relay,1L);
        string reference=Path.Combine(relay.DirectoryPath,"paired-store.txt");
        typeof(ExperimentSession).GetField("pairingReference",flags)!.SetValue(relay,reference);
        async Task Read(string lines)
        {
            using var reader=new StreamReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(lines)));
            await (Task)typeof(ExperimentSession).GetMethod("ReadUpstreamStatusAsync",flags)!.Invoke(relay,[reader])!;
        }
        await Read("4 ms  CLASSIC_ACL status=0x04\n");
        check(!relay.State.Contains("未响应回连"),"uncorrelated status cannot identify the saved Switch receiver");
        await Read("7 ms  SAVED_RECEIVER_CONNECT status=0x00 attempt=1\n8 ms  CLASSIC_ACL status=0x04\n");
        check(!relay.State.Contains("未响应回连"),"a different peer's ACL event cannot be misidentified as the saved Switch");
        await Read("8 ms  SAVED_RECEIVER_PAGE_RESULT attempt=1 status=0x04 retry_scheduled=1 next_in_ms=8000 exhausted=0\n");
        check(relay.State.Contains("未响应回连")&&!relay.RadioFinished&&!relay.ReceiverConnected,"page timeout shows a waiting hint without ending pairing or claiming a connection");
        check(!File.Exists(reference),"a generic request status alone cannot change the pairing store");
        await Read("8 ms  SAVED_RECEIVER_CONNECT status=0x00 attempt=2 limit=3 bond_source=prior_home_proof\n");
        check(File.ReadAllText(reference)=="prior-home-proof-fixture"&&!relay.ReceiverConnected,"validated stored bond can be remembered while the console is still being paged");
        await Read("9 ms  SAVED_RECEIVER_PAGE_RESULT attempt=2 status=0x00 retry_scheduled=0 next_in_ms=0 exhausted=0\n");
        check(!relay.State.Contains("未响应回连"),"a real ACL connection clears the previous timeout hint");
        await Read("12 ms  UPSTREAM_GAMEPAD_PAIRED\n14 ms  STATUS reports30=1 peer_output=2\n");
        check(!relay.UpstreamTransportObserved,"an upstream handshake and peer replies alone do not verify physical USB consumption");
        await Read("18 ms  RELAY_INPUT relay_frames=3 edge_resyncs=0\n");
        check(relay.UpstreamTransportObserved,"bidirectional upstream telemetry and real input are both required for a passing transport check");
        await Read("80000 ms  END reason=physical_relay_home_observed_60s paired_seen=1\n");
        check(!relay.UpstreamHomeObserved,"ordinary sessions cannot count a diagnostic HOME run");
        await using var homeRun=new ExperimentSession(Path.Combine(output,"upstream-home-"+Guid.NewGuid().ToString("N")))
            {UseUpstreamSwitch=true,ProbeUpstreamHome=true};
        async Task ReadHome(string lines)
        {
            using var reader=new StreamReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(lines)));
            await (Task)typeof(ExperimentSession).GetMethod("ReadUpstreamStatusAsync",flags)!.Invoke(homeRun,[reader])!;
        }
        await ReadHome("22000 ms  END reason=home_transport_observed_20s paired_seen=1\n");
        check(!homeRun.UpstreamHomeObserved,"comparison-only HOME cannot pass the physical USB probe");
        await ReadHome("81200 ms  END reason=physical_relay_home_observed_60s paired_seen=1\n");
        check(!homeRun.UpstreamHomeObserved,"a terminal label without observed press, release and peer traffic cannot pass");
        const string result="HOME_TEST_RESULT requested=1 press_report=1 release_requested=1 release_report=1 elapsed_after_request_ms=60028 acl_up=1 hid_paired=1 peer_closed_interrupt=0 outgoing_reports_after_request=3010 host_output_after_request=109";
        await ReadHome("81200 ms  "+result.Replace("release_report=1","release_report=0")+"\n81200 ms  END reason=physical_relay_home_observed_60s paired_seen=1\n");
        check(!homeRun.UpstreamHomeObserved,"a missing physical release cannot pass even with a success label");
        await ReadHome("81200 ms  "+result+"\n81200 ms  END reason=physical_relay_home_observed_60s paired_seen=1\n");
        check(homeRun.UpstreamHomeObserved,"only the 60 second physical relay HOME observation satisfies the diagnostic");
    }
    private static async Task TestKeySizeFailureMessageAsync(Action<bool,string> check,string output,WirelessPadMode mode)
    {
        await using var relay=new ExperimentSession(Path.Combine(output,"radio-exit-"+Guid.NewGuid().ToString("N")));
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),"/d /c exit 23")
            {UseShellExecute=false,CreateNoWindow=true};
        using var child=Process.Start(start) ?? throw new IOException("Unable to create completed test process.");
        await child.WaitForExitAsync();
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(ExperimentSession).GetField("outputMode",flags)!.SetValue(relay,mode);
        typeof(ExperimentSession).GetField("radio",flags)!.SetValue(relay,child);
        try
        {
            var heartbeat=(Task)typeof(ExperimentSession).GetMethod("HeartbeatAsync",flags)!.Invoke(relay,[CancellationToken.None])!;
            await heartbeat;
            check(relay.RadioFinished && relay.RadioExitCode==23,"native radio failure ends the relay session");
            check(relay.State.Contains("0x1408")== (mode==WirelessPadMode.Pro1),"Pro1 key-size diagnosis persists after cleanup and is scoped to its profile");
        }
        finally{typeof(ExperimentSession).GetField("radio",flags)!.SetValue(relay,null);}
    }
    private static async Task TestTransientLinkAsync(Action<bool,string> check,string output,string runtime)
    {
        await using var relay=new ExperimentSession(Path.Combine(output,"hid-recovery-"+Guid.NewGuid().ToString("N")),runtimeDirectory:runtime,integrated:true);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        FieldInfo Field(string name)=>typeof(ExperimentSession).GetField(name,flags)!;
        using var receive=WirelessRelayLocalIpc.Create();
        using var sender=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        Field("ipc").SetValue(relay,receive);Field("radioEndpoint").SetValue(relay,sender.Client.LocalEndPoint);
        Field("outputMode").SetValue(relay,WirelessPadMode.Pro1);Field("relayStarted").SetValue(relay,true);
        relay.DeferSwitchPairing=true;
        var preview=new MainViewModel(previewOnly:true);
        typeof(MainViewModel).GetField("wirelessOutput",flags)!.SetValue(preview,relay);
        typeof(MainViewModel).GetField("bluetoothOutput",flags)!.SetValue(preview,true);
        byte[] token=(byte[])Field("session").GetValue(relay)!;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var loop=(Task)typeof(ExperimentSession).GetMethod("ReceiveAsync",flags)!.Invoke(relay,[stop.Token])!;
        ulong sequence=0;
        async Task Link(byte state,byte reason=0x13)
        {
            object? before=Field("protocol").GetValue(relay);
            byte[] payload=[1,2,3,4,5,6,state,reason];
            await sender.SendAsync(WirelessRelayDatagram.Encode(token,WirelessRelayMessage.LinkState,++sequence,1,payload),(IPEndPoint)receive.Client.LocalEndPoint!,stop.Token);
            bool Handled()=>state switch{
                0=>Field("protocol").GetValue(relay)!=null&&!ReferenceEquals(before,Field("protocol").GetValue(relay))&&!(bool)Field("receiverRecovering").GetValue(relay)!,
                1=>relay.ReceiverConnected,
                4=>!relay.ReceiverConnected&&(bool)Field("receiverRecovering").GetValue(relay)!,
                5=>relay.PairingConfirmationPending,
                6=>!relay.PairingConfirmationPending,
                7=>relay.ReceiverConnected && Equals(Field("pro1Delivery").GetValue(relay),(int)reason),
                _=>relay.RadioFinished
            };
            while((long)Field("receivedSequence").GetValue(relay)!<(long)sequence||!Handled())await Task.Delay(5,stop.Token);
        }
        try
        {
            await Link(0);
            var pairingProtocol=Field("protocol").GetValue(relay);
            await Link(5,17);
            check(relay.PairingConfirmationPending&&!relay.ReceiverConnected&&!relay.RadioFinished,
                "a Switch pairing request needs user confirmation before HID is reported ready");
            await Link(6,2);
            check(!relay.PairingConfirmationPending&&!relay.ReceiverConnected&&!relay.RadioFinished,
                "an expired pairing request returns to waiting without ending the relay");
            await relay.ConfirmPairingAsync(stop.Token);
            await Link(5,18);

            await Task.WhenAll(relay.ConfirmPairingAsync(stop.Token),relay.ConfirmPairingAsync(stop.Token));
            UdpReceiveResult confirmation=await sender.ReceiveAsync(stop.Token);
            check(WirelessRelayDatagram.TryDecode(confirmation.Buffer,token,out var frame)
                && frame!.Kind==WirelessRelayMessage.PairingConfirm
                && frame.Payload.AsSpan().SequenceEqual(new byte[]{18}),
                "confirmation returns the most recent request identifier through authenticated loopback IPC");
            using(var noDuplicate=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                noDuplicate.CancelAfter(200);
                try
                {
                    await sender.ReceiveAsync(noDuplicate.Token);
                    check(false,"expired requests and repeated clicks cannot send extra confirmation packets");
                }
                catch(OperationCanceledException)when(!stop.IsCancellationRequested)
                {
                    check(true,"expired requests and repeated clicks cannot send extra confirmation packets");
                }
            }
            await Link(6,1);
            check(!relay.PairingConfirmationPending&&!relay.ReceiverConnected,
                "confirmation submission ends the prompt but is not a Bluetooth connection");
            check(ReferenceEquals(pairingProtocol,Field("protocol").GetValue(relay)),
                "pairing control events do not rebuild the existing protocol");
            await Link(1);
            check(relay.ReceiverConnected&&!relay.RadioFinished,"HID-ready IPC connects the receiver without a physical radio");
            var previous=(WirelessPro1Protocol)Field("protocol").GetValue(relay)!;
            check(relay.AwaitingPro1Initialization && preview.UnifiedStateText.Contains("简单输入"),"simple HID is distinguished from host-initialized input");
            await Link(7,1);
            check(!relay.AwaitingPro1Initialization && relay.State.Contains("接收端请求") && ReferenceEquals(previous,Field("protocol").GetValue(relay)),"per-link mode confirmation does not reset the protocol");
            byte[] mode=new byte[12];mode[0]=1;mode[10]=3;mode[11]=0x30;
            previous.HandleOutput(mode,GamepadState.Neutral(),1,1000);
            await Link(4);
            check(!relay.ReceiverConnected&&!relay.RadioFinished&&relay.State.Contains("恢复"),"HID-channel loss is visibly recovering, not connected or terminal");
            check(preview.UnifiedStateText=="正在恢复蓝牙连接","home badge explains recovery instead of asking the user to pair again");
            await Link(0);await Link(1);
            var reopened=(WirelessPro1Protocol)Field("protocol").GetValue(relay)!;
            check(relay.ReceiverConnected&&!relay.RadioFinished,"same native session accepts a recovered HID connection");
            check(!ReferenceEquals(previous,reopened)&&reopened.InputMode==0x3f,"new HID handshake cannot inherit the old report or MCU mode");
            check(relay.AwaitingPro1Initialization,"a new HID link cannot inherit confirmation");
            await Link(7,1);
            check(relay.State.Contains("接收端请求") && !relay.AwaitingPro1Initialization,"recovered HID shows full reports only after new host initialization");
            check(Field("cadence").GetValue(relay)==null,"Pro1 never creates a second managed cadence on HID reconnect");
            await Link(2);
            check(!relay.ReceiverConnected&&relay.RadioFinished,"final reconnect failure remains terminal and visible");
            check(preview.UnifiedStateText=="转发已停止","home badge stops showing recovery when the retry budget ends");
        }
        finally{stop.Cancel();await loop;await preview.ShutdownAsync();}
    }
}
