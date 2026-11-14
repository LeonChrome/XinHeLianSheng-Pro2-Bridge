using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Y700Switch2V60Viiper;

internal static class UnifiedConnectionsRegressionTests
{
    internal static void Run(Action<bool,string> check)
    {
        var defaults=ControllerInputConfiguration.Normalize(null);
        check(defaults.Length==4&&defaults.All(x=>x.Kind==ControllerInputKind.Pro2Ble),"old settings default to four BLE connection choices");
        var saved=new[]{new ControllerInputConfiguration(ControllerInputKind.Pro2Usb,"usb-a"),new(ControllerInputKind.Pro1Bluetooth,"bt-b"),new(ControllerInputKind.Pro1Usb,"usb-c"),new(ControllerInputKind.Pro2Ble)};
        var restored=ControllerInputConfiguration.Normalize(JsonSerializer.Deserialize<ControllerInputConfiguration[]>(JsonSerializer.Serialize(saved)));
        check(restored.SequenceEqual(saved),"mixed input choices and physical identities survive serialization independently");
        check(ControllerInputConfiguration.Normalize([new((ControllerInputKind)77,"bad")])[0].Kind==ControllerInputKind.Pro2Ble,"unknown input choice falls back safely");
        var slot=new Pro2ControllerSlot(2,false);
        var pro2=new NintendoHidDevice("fixture","instance","usb-a",NintendoInputKind.Pro2Usb,64,64,"Pro2");
        var pro1=pro2 with{PhysicalId="bt-b",Kind=NintendoInputKind.Pro1Bluetooth};
        slot.SelectedInputKind=ControllerInputKind.Pro2Usb;
        slot.UpdateDeviceChoices([pro2,pro1]);
        check(slot.PhysicalDeviceChoices.Count==1&&slot.SelectedPhysicalDevice==pro2&&slot.SelectedPhysicalId=="usb-a","only matching transport is offered and unambiguous first device can be selected");
        slot.UpdateDeviceChoices([pro2 with{PhysicalId="usb-other"}]);
        check(slot.SelectedPhysicalDevice==null&&slot.SelectedPhysicalId=="usb-a","missing remembered device never silently selects another controller");
        slot.SelectedPhysicalDevice=pro1;
        check(slot.SelectedPhysicalDevice==null,"foreign transport cannot become this slot input");
        int writes=0;slot.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(Pro2ControllerSlot.InputConfiguration))writes++;};
        for(int i=0;i<100;i++)slot.RefreshFromSource();
        check(writes==0,"telemetry refresh does not repeatedly save connection settings");
        check(slot.InputChoicePending&&slot.InputSource.ConnectedAddress.Length==0&&!slot.ActiveInput.IsRunning,"offline selection does not connect, detach or manufacture state");
    }
    internal static void RunUi(Window owner,MainViewModel vm,Action<bool,string> check,Action pump,Action<Window,string> save)
    {
        check(owner.FindName("PrimaryConnectionView") is ControllerConnectionView home&&ReferenceEquals(home.DataContext,vm.Pro2Slots[0]),"home uses the shared per-slot connection component");
        var panel=new MultiControllerWindow(vm){Owner=owner};panel.Show();pump();
        try
        {
            var controls=Children<ControllerConnectionView>(panel).ToArray();
            check(controls.Length==4&&controls.Select(c=>c.DataContext).SequenceEqual(vm.Pro2Slots),"all four slots use the same connection UI as home");
            var before=vm.Pro2Slots.Select(s=>s.InputConfiguration).ToArray();
            try
            {
                var combo=Children<ComboBox>(controls[1]).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="实体手柄连接方式");
                combo.SelectedValue=ControllerInputKind.Pro1Bluetooth;pump();
                check(Children<Button>(controls[1]).Any(b=>b.Content?.ToString()=="Windows 配对…"&&b.IsVisible),"Pro1 wireless shows the Windows pairing action");
                combo.SelectedValue=ControllerInputKind.Pro1Usb;pump();
                check(vm.Pro2Slots[1].SelectedInputKind==ControllerInputKind.Pro1Usb&&vm.Pro2Slots[0].SelectedInputKind==before[0].Kind,
                    "connection selector changes only its own slot");
                check(vm.Pro2Slots.All(s=>!s.ActiveInput.IsRunning&&!s.VirtualDeviceRunning),"editing connections offline never starts hardware or a virtual device");
                var settings=(V60UserSettings)typeof(MainViewModel).GetField("userSettings",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
                check(settings.SlotInputs[1].Kind==ControllerInputKind.Pro1Usb,"UI choice persists independently of virtual output mode");
                var priorAddresses=settings.LastConnectedPro2Addresses;
                bool priorEnabled=vm.Pro2Slots[1].Enabled;
                try
                {
                    settings.LastConnectedPro2Addresses=["","11:22:33:44:55:66","",""];
                    vm.Pro2Slots[1].Enabled=true;
                    var exclusions=(HashSet<string>)typeof(MainViewModel).GetMethod("ConnectedPro2AddressesExcept",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                        .Invoke(vm,[vm.Pro2Slots[0]])!;
                    check(!exclusions.Contains("11:22:33:44:55:66"),"HID slot cannot reserve an obsolete BLE address and block another controller");
                }
                finally{settings.LastConnectedPro2Addresses=priorAddresses;vm.Pro2Slots[1].Enabled=priorEnabled;}
                save(panel,"ui-unified-mixed-inputs.png");
            }
            finally{for(int i=0;i<4;i++)vm.Pro2Slots[i].LoadInputConfiguration(before[i]);}
        }
        finally{panel.Close();}
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);if(child is T item)yield return item;foreach(var next in Children<T>(child))yield return next;}
    }
}
