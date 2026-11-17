using System.IO;
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
        var preview=new MainViewModel(previewOnly:true);
        typeof(MainViewModel).GetField("wirelessOutput",flags)!.SetValue(preview,relay);
        typeof(MainViewModel).GetField("bluetoothOutput",flags)!.SetValue(preview,true);
        byte[] token=(byte[])Field("session").GetValue(relay)!;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var loop=(Task)typeof(ExperimentSession).GetMethod("ReceiveAsync",flags)!.Invoke(relay,[stop.Token])!;
        ulong sequence=0;
        async Task Link(byte state)
        {
            object? before=Field("protocol").GetValue(relay);
            byte[] payload=[1,2,3,4,5,6,state,0x13];
            await sender.SendAsync(WirelessRelayDatagram.Encode(token,WirelessRelayMessage.LinkState,++sequence,1,payload),(IPEndPoint)receive.Client.LocalEndPoint!,stop.Token);
            bool Handled()=>state switch{
                0=>Field("protocol").GetValue(relay)!=null&&!ReferenceEquals(before,Field("protocol").GetValue(relay))&&!(bool)Field("receiverRecovering").GetValue(relay)!,
                1=>relay.ReceiverConnected,
                4=>!relay.ReceiverConnected&&(bool)Field("receiverRecovering").GetValue(relay)!,
                _=>relay.RadioFinished
            };
            while((long)Field("receivedSequence").GetValue(relay)!<(long)sequence||!Handled())await Task.Delay(5,stop.Token);
        }
        try
        {
            await Link(0);await Link(1);
            check(relay.ReceiverConnected&&!relay.RadioFinished,"HID-ready IPC connects the receiver without a physical radio");
            var previous=(WirelessPro1Protocol)Field("protocol").GetValue(relay)!;
            byte[] mode=new byte[12];mode[0]=1;mode[10]=3;mode[11]=0x30;
            previous.HandleOutput(mode,GamepadState.Neutral(),1,1000);
            await Link(4);
            check(!relay.ReceiverConnected&&!relay.RadioFinished&&relay.State.Contains("恢复"),"HID-channel loss is visibly recovering, not connected or terminal");
            check(preview.UnifiedStateText=="正在恢复蓝牙连接","home badge explains recovery instead of asking the user to pair again");
            await Link(0);await Link(1);
            var reopened=(WirelessPro1Protocol)Field("protocol").GetValue(relay)!;
            check(relay.ReceiverConnected&&!relay.RadioFinished,"same native session accepts a recovered HID connection");
            check(!ReferenceEquals(previous,reopened)&&reopened.InputMode==0x3f,"new HID handshake cannot inherit the old report or MCU mode");
            await Link(2);
            check(!relay.ReceiverConnected&&relay.RadioFinished,"final reconnect failure remains terminal and visible");
            check(preview.UnifiedStateText=="转发已停止","home badge stops showing recovery when the retry budget ends");
        }
        finally{stop.Cancel();await loop;await preview.ShutdownAsync();}
    }
}
