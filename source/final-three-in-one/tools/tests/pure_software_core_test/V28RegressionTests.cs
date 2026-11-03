using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class V28RegressionTests
{
    private static readonly GamepadButtons[] Keys = [GamepadButtons.North, GamepadButtons.East, GamepadButtons.South, GamepadButtons.West];
    public static void Run(Action<bool,string> check)
    {
        var targets = Keys.Append(GamepadButtons.None).ToArray();
        foreach (var mode in Pro2SlotModes.Options.Select(o => o.Mode))
        {
            foreach (int a in Enumerable.Range(0,5)) foreach (int b in Enumerable.Range(0,5))
            foreach (int c in Enumerable.Range(0,5)) foreach (int d in Enumerable.Range(0,5))
            {
                GamepadButtons[] map = [targets[a],targets[b],targets[c],targets[d]];
                var p = new FaceButtonProfile { Preset = FaceLayoutPreset.Custom, North=map[0],East=map[1],South=map[2],West=map[3] };
                var mapper = new FaceButtonMapper();
                // Exhaust all held/released combinations, including many-to-one and disabled outputs.
                for (int bits=0;bits<16;bits++)
                {
                    var pressed=GamepadButtons.R1 | GamepadButtons.Aux;
                    var expected=pressed;
                    for (int k=0;k<4;k++) if ((bits & (1<<k))!=0) { pressed |= Keys[k]; expected |= map[k]; }
                    var source = new GamepadState { Buttons=pressed, Updates=123, RawNotificationSequence=456, SourceTimestampTicks=789,
                        Lx=999, GyroValid=true, AccelValid=true, GyroX=-321, AccelZ=4096, MotionTimestampUs=1000 };
                    var output = mapper.Apply(source,p);
                    check(output.Buttons==expected && source.Buttons==pressed, "face mapping combinations/source immutable " + mode);
                    check(output.Updates==123 && output.RawNotificationSequence==456 && output.SourceTimestampTicks==789 &&
                        output.MotionTimestampUs==1000 && output.GyroX==-321 && output.AccelZ==4096 && output.Lx==999,
                        "ABXY mapping cannot alter physical cadence/motion/axes " + mode);
                }
            }
            foreach (var preset in new[] {FaceLayoutPreset.Position,FaceLayoutPreset.Xbox,FaceLayoutPreset.PlayStation})
            {
                var input = new GamepadState { Buttons = GamepadButtons.North | GamepadButtons.East };
                check(ReferenceEquals(new FaceButtonMapper().Apply(input,FaceButtonProfile.ForPreset(preset,mode)),input),
                    "position presets preserve physical directions " + mode + "/" + preset);
            }
        }
        var names=FaceButtonProfile.ForPreset(FaceLayoutPreset.NintendoNames,ViiperVirtualMode.Xbox);
        check(names.North==GamepadButtons.West && names.East==GamepadButtons.South && names.South==GamepadButtons.East && names.West==GamepadButtons.North,
            "Nintendo X/A/B/Y map to Xbox X/A/B/Y by explicit target positions");
        var runtime=new FaceButtonMapper();
        var held=new GamepadState {Buttons=GamepadButtons.East};
        runtime.Apply(held,new());
        check(runtime.Apply(held,names).Buttons==GamepadButtons.None, "live remap releases held key without firing new target");
        for(int i=0;i<100;i++) check(runtime.Apply(held,names).Buttons==GamepadButtons.None,"held key cannot repeat after remap");
        runtime.Apply(new(),names);
        check(runtime.Apply(held,names).Buttons==GamepadButtons.South,"release then press rearms remapped key");
        runtime.Reset();
        var rear = new RearButtonMapper();
        var rearConfig = new RearButtonConfiguration { Xbox=new() {Left=new() {Enabled=true,Buttons=GamepadButtons.North}} };
        rear.Apply(runtime.Apply(new(),names),ViiperVirtualMode.Xbox,rearConfig,0);
        var mapped = rear.Apply(runtime.Apply(new() {Buttons=GamepadButtons.PaddleLeft | GamepadButtons.East},names),ViiperVirtualMode.Xbox,rearConfig,100000);
        check(mapped.IsPressed(GamepadButtons.North | GamepadButtons.South) && !mapped.IsPressed(GamepadButtons.West),"rear output is not face-remapped a second time");
        var wire=VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Xbox,mapped);
        check((BinaryPrimitives.ReadUInt32LittleEndian(wire) & 0x9000)==0x9000,"ABXY mapping and rear output reach exact Xbox A/Y wire bits");
        var config=new FaceButtonConfiguration().WithMode(ViiperVirtualMode.Xbox,names);
        var roundTrip=JsonSerializer.Deserialize<FaceButtonConfiguration>(JsonSerializer.Serialize(config))!.Normalize();
        check(roundTrip.Xbox.SameMapping(names) && roundTrip.Ps5.SameMapping(new()) && roundTrip.Pro1.SameMapping(new()),"mapping persistence isolated by mode");
        check(JsonSerializer.Deserialize<V60UserSettings>("{}")!.FaceButtons.Normalize().Xbox.SameMapping(new()),"old settings migrate to identity mapping");
        var vm=new MainViewModel(previewOnly:true);
        vm.FaceEditingMode=ViiperVirtualMode.Xbox; vm.SelectedFacePreset=FaceLayoutPreset.NintendoNames;
        vm.FaceEditingMode=ViiperVirtualMode.DualSenseLike; vm.FaceRows[0].Target=GamepadButtons.South;
        vm.FaceEditingMode=ViiperVirtualMode.Xbox;
        check(vm.SelectedFacePreset==FaceLayoutPreset.NintendoNames && vm.FaceRows[0].Target==GamepadButtons.West,"offline mode switching preserves drafts");
        vm.SaveFaceMappingCommand.Execute(null);
        check(vm.FaceDraftStatus=="已保存" && vm.FaceStatus.StartsWith("已保存"),"mapping can save while no controller is connected");
        vm.FaceEditingMode=ViiperVirtualMode.DualSenseLike;
        check(vm.SelectedFacePreset==FaceLayoutPreset.Custom && vm.FaceRows[0].Target==GamepadButtons.South,"saving another mode preserves unsaved PS5 draft");
        var outputSession = new ViiperBridgeSession(new ViiperProtocolClient("localhost",65530),ViiperDeviceProfile.Xbox,new DirectProgress<string>(_ => {}));
        typeof(ViiperBridgeSession).GetField("outputHealth",BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(outputSession,
            new VirtualOutputHealth(VirtualOutputState.Failed,"虚拟输出中断","fixture: code10"));
        var slot=vm.Pro2Slots[0]; slot.Session=outputSession; slot.VirtualDeviceRunning=true;
        typeof(Pro2BleInputSource).GetField("transportConnected",BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slot.InputSource,true);
        slot.RefreshFromSource();
        check(vm.HasVirtualOutputWarning && vm.CompactConnectionTitle=="虚拟输出中断" && slot.DisplayStatus=="虚拟输出中断",
            "physical BLE connected status cannot overwrite virtual output fault");
        check(vm.EnvironmentReportText.Contains("fixture: code10"),"system report includes cached virtual device failure without probing again");
        slot.Session=null; outputSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
        typeof(Pro2BleInputSource).GetField("transportConnected",BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slot.InputSource,false);
        vm.ShutdownAsync().GetAwaiter().GetResult();

        Health(check);
        string audit="<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing'/></System><EventData><Data>PRIVATE_SENTINEL</Data></EventData></Event>";
        string bt="<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='BTHUSB'/><EventID>5</EventID><TimeCreated SystemTime='2026-11-03T11:30:11Z'/></System><EventData><Data Name='Device'>USB_TEST</Data></EventData></Event>";
        string evidence=WindowsDeviceEventEvidence.Format(audit+bt);
        check(evidence.Contains("BTHUSB") && evidence.Contains("USB_TEST") && !evidence.Contains("PRIVATE_SENTINEL"),"device evidence excludes unrelated Security audit payload");
        check(WindowsDeviceEventEvidence.Format("").Contains("不能据此证明"),"no events does not prove healthy Bluetooth");
    }
    private static void Health(Action<bool,string> check)
    {
        var good=new VirtualPnpDevice(5,"USB_TEST",0,true,true,"HidUsb");
        var ready=new VirtualPnpSnapshot(true,[good],"");
        check(UsbipAttachmentHealth.Evaluate(true,[5],ready).State==UsbipAttachmentState.Ready,"native HID started confirms ready, port 005 has no special rule");
        check(UsbipAttachmentHealth.Evaluate(true,[1],ready with {Devices=[good with {Port=1}]}).State==UsbipAttachmentState.Ready,"port 001 can equally be ready");
        check(UsbipAttachmentHealth.Evaluate(false,[],ready).State==UsbipAttachmentState.Unknown,"failed port query is unknown, not missing");
        check(UsbipAttachmentHealth.Evaluate(true,[],ready).State==UsbipAttachmentState.Missing,"successful empty query confirms missing");
        check(UsbipAttachmentHealth.Evaluate(true,[5,6],ready).State==UsbipAttachmentState.Duplicate,"duplicate target mounts identified");
        check(UsbipAttachmentHealth.Evaluate(true,[5],ready with {Devices=[]}).State==UsbipAttachmentState.Unknown,"unattributed PnP tree never justifies detach");
        check(UsbipAttachmentHealth.Evaluate(true,[5],ready with {Devices=[good with {Problem=10}]}).State==UsbipAttachmentState.DeviceError,"allocated port plus code10 never marked healthy");
        var unknown=new UsbipRecoveryPolicy();
        for(int i=0;i<100;i++) check(!unknown.Observe(UsbipAttachmentState.Unknown,TimeSpan.FromSeconds(i*5)),"metadata failure never triggers destructive retry");
        var policy=new UsbipRecoveryPolicy();
        check(!policy.Observe(UsbipAttachmentState.Missing,TimeSpan.Zero) && !policy.Observe(UsbipAttachmentState.Missing,TimeSpan.FromSeconds(14)),"transient missing mount grace");
        check(policy.Observe(UsbipAttachmentState.Missing,TimeSpan.FromSeconds(15)),"persistent confirmed loss authorizes targeted recovery");
        int approved=1;
        for(int i=16;i<200;i++) if(policy.Observe(UsbipAttachmentState.DeviceError,TimeSpan.FromSeconds(i))) approved++;
        check(approved==2 && policy.Attempts==2,"runtime recovery bounded, no attach storm");
        var sibling=new UsbipRecoveryPolicy();
        check(sibling.Attempts==0 && !sibling.Observe(UsbipAttachmentState.Ready,TimeSpan.FromSeconds(200)),"healthy sibling has independent recovery budget");
        string ports="Port 01: device in use\n -> usbip://localhost:3241/379-10\nPort 05: device in use\n -> usbip://localhost:3241/379-1\n";
        check(ControllerEnumerationDiagnostics.FindUsbipPortsForRemoteDevice(ports,3241,379,"1").SequenceEqual(new[]{5}),"remote device1 never matches device10 or another slot");
    }
}
