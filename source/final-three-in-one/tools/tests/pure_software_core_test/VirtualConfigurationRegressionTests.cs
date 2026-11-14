using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Y700Switch2V60Viiper;

internal static class VirtualConfigurationRegressionTests
{
    internal static void Run(Action<bool,string> check)
    {
        const string id=@"USB\VID_057E&PID_2009\PRO2-NSPRO-S1";
        var broken=new VirtualPnpDevice(5,id,19,false,false,"HidUsb","device-filter-fixture");
        var observed=UsbipAttachmentHealth.Evaluate(true,[5],new(true,[broken],""));
        check(observed.State==UsbipAttachmentState.ConfigurationError&&observed.Problem==19&&observed.InstanceId==id&&observed.Detail.Contains("device-filter-fixture"),
            "Code19 is a configuration error with exact identity and cached evidence, not missing USBIP or BLE");
        var policy=new UsbipRecoveryPolicy();
        for(int i=0;i<200;i++)check(!policy.Observe(observed.State,TimeSpan.FromSeconds(i*5)),"persistent Code19 never triggers automatic detach/attach");
        check(policy.Attempts==0,"Code19 does not consume ordinary loss recovery budget");
        foreach(var mode in Pro2SlotModes.Options)
            for(int slot=1;slot<=4;slot++)
            {
                var profile=Pro2SlotModes.Profile(mode.Mode,slot,ViiperPushRateOption.All[0]);
                string instance=@"USB\VID_"+profile.ExpectedVid+"&PID_"+profile.ExpectedPid+@"\"+profile.DeviceSpecificSerialNumber;
                check(VirtualDeviceConfigurationRepair.TryIdentity(instance,out string vid,out string pid)&&vid==profile.ExpectedVid&&pid==profile.ExpectedPid,
                    "all four output modes and four slot serials are recognized exactly");
            }
        foreach(string foreign in new[]{@"USB\VID_057E&PID_2009\REAL_PRO_CONTROLLER",@"HID\VID_057E&PID_2009\PRO2-NSPRO-S1",id+"-OTHER",@"USB\VID_0BDA&PID_0852\REALTEK",id.Replace("S1","S10")})
            check(!VirtualDeviceConfigurationRepair.TryIdentity(foreign,out _,out _),"physical, foreign, HID child and prefix-matching IDs are refused");
        check(VirtualDeviceConfigurationRepair.MatchesTarget(broken,id,5)&&!VirtualDeviceConfigurationRepair.MatchesTarget(broken,id,6)&&
            !VirtualDeviceConfigurationRepair.MatchesTarget(broken with{Problem=0},id,5),"fresh problem code, port and identity must all match before scoped removal");
        check(VirtualDeviceConfigurationRepair.MatchesProfile(id,Pro2SlotModes.Profile(ViiperVirtualMode.Pro1,1,ViiperPushRateOption.All[0]))&&
            !VirtualDeviceConfigurationRepair.MatchesProfile(id,Pro2SlotModes.Profile(ViiperVirtualMode.Pro1,2,ViiperPushRateOption.All[0])),
            "a stale port mapping can never authorize repair of a different slot serial");
        check(VirtualDeviceConfigurationRepair.ExecuteAsync([VirtualDeviceConfigurationRepair.Command,"/reboot"]).GetAwaiter().GetResult()==7,
            "malformed repair CLI cannot reach device or administrator operations");
        check(VirtualDeviceConfigurationRepair.ExecuteAsync([VirtualDeviceConfigurationRepair.Command,"--operation-id","../../bad","--instance-id",id,"--port","5"]).GetAwaiter().GetResult()==7,
            "repair path injection is rejected before privileged operations");
        int captures=0,removals=0;bool backedUp=false;
        int exit=VirtualDeviceConfigurationRepair.RemoveValidatedAsync(id,5,()=>{captures++;return new(true,[broken],"");},
            _=>{backedUp=true;return Task.CompletedTask;},()=>{check(backedUp&&captures==2,"backup and fresh revalidation precede removal");removals++;return Task.FromResult(0);}).GetAwaiter().GetResult();
        check(exit==0&&removals==1,"authorized reconstruction removes one exact target once");
        removals=0;
        try{VirtualDeviceConfigurationRepair.RemoveValidatedAsync(id,5,()=>new(true,[broken],""),_=>throw new IOException("backup-failed"),
            ()=>{removals++;return Task.FromResult(0);}).GetAwaiter().GetResult();check(false,"backup failure must abort");}catch(IOException){}
        check(removals==0,"backup failure cannot reach removal");
        captures=0;
        try{VirtualDeviceConfigurationRepair.RemoveValidatedAsync(id,5,()=>new(true,[++captures==1?broken:broken with{Port=6}],""),_=>Task.CompletedTask,
            ()=>{removals++;return Task.FromResult(0);}).GetAwaiter().GetResult();check(false,"target movement must abort");}catch(InvalidOperationException){}
        check(removals==0,"port or identity change during backup cannot remove another device");
    }
    internal static void RunUi(Window owner,MainViewModel vm,Action<bool,string> check,Action pump,Action<Task> wait,Action<Window,string> save)
    {
        const string id=@"USB\VID_057E&PID_2009\PRO2-NSPRO-S1";
        var slot=vm.Pro2Slots[0];var previous=slot.Session;bool running=slot.VirtualDeviceRunning;
        var source=slot.InputSource;
        var transport=typeof(Pro2BleInputSource).GetField("transportConnected",BindingFlags.Instance|BindingFlags.NonPublic)!;
        bool oldTransport=(bool)transport.GetValue(source)!;
        var fixture=new ViiperBridgeSession(new("localhost",65530),ViiperDeviceProfile.Pro1,new DirectProgress<string>(_=>{}));
        var health=typeof(ViiperBridgeSession).GetField("outputHealth",BindingFlags.Instance|BindingFlags.NonPublic)!;
        try
        {
            health.SetValue(fixture,new VirtualOutputHealth(VirtualOutputState.Failed,"虚拟手柄配置异常（代码 19）","配置故障\nfilter-fixture-only",0,19,id));
            slot.Session=fixture;slot.VirtualDeviceRunning=true;transport.SetValue(source,true);
            slot.RefreshFromSource();vm.RefreshCompactTelemetry();pump();
            check(vm.ShowVirtualOutputRepair&&vm.CanRepairVirtualOutput&&slot.NeedsConfigurationRepair,"cached Code19 exposes repair for exactly the affected session");
            check(vm.CompactConnectionTitle.Contains("代码 19")&&!vm.LowRateTitle.Contains("可以游玩")&&!vm.ShowBleOptimizationSummary,
                "UI never claims game output is playable while virtual configuration is broken");
            check(vm.CompactConnectText=="虚拟输出待修复"&&vm.CompactStateText=="虚拟配置异常","faulted output is not labeled as started or merely pending");
            check(vm.VirtualOutputConfigurationEvidence.Contains("filter-fixture-only")&&vm.EnvironmentReportText.Contains("filter-fixture-only"),
                "homepage and environment diagnostics share cached filter evidence without extra probes");
            save(owner,"ui-virtual-code19.png");
            wait(vm.RepairVirtualOutputAsync());
            check(vm.Status.Contains("界面预览")&&ReferenceEquals(slot.Session,fixture),"preview repair cannot launch elevation, remove devices or stop BLE");
            health.SetValue(fixture,new VirtualOutputHealth(VirtualOutputState.Ready,"虚拟手柄已就绪","fixture"));
            vm.RefreshCompactTelemetry();pump();
            check(!vm.ShowVirtualOutputRepair&&!vm.CanRepairVirtualOutput,"healthy output hides the configuration repair action");
        }
        finally
        {
            slot.Session=previous;slot.VirtualDeviceRunning=running;transport.SetValue(source,oldTransport);
            fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();vm.RefreshCompactTelemetry();pump();
        }
    }
}
