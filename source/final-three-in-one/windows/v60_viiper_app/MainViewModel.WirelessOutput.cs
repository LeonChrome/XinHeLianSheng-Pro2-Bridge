using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Pro2WirelessExperiment;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private bool bluetoothOutput;
    private ExperimentSession? wirelessOutput;
    private WirelessRelayRadio? selectedWirelessRadio;
    private string? lastWirelessRadio;
    private string wirelessNfcStatus="";
    private string wirelessLastLog="";
    private int wirelessRearCancelRequested;
    private ICommand? outputDestinationCommand,refreshRadiosCommand,readWirelessNfcCommand,removeWirelessTagCommand;
    public bool IsBluetoothOutput=>bluetoothOutput;
    public bool IsLocalOutput=>!bluetoothOutput;
    public bool CanUseNativePro2Output=>!bluetoothOutput;
    public bool CanUseLocalTools=>!Busy&&!bluetoothOutput;
    public bool HasWirelessOutput=>wirelessOutput!=null;
    public ObservableCollection<WirelessRelayRadio> WirelessRadios {get;}=new();
    public IReadOnlyList<double> WirelessRates {get;}=new[]{133.0,250.0,1000.0/15};
    public double SelectedWirelessRate {get;set;}=133;
    public WirelessRelayRadio? SelectedWirelessRadio
    {
        get=>selectedWirelessRadio;
        set{selectedWirelessRadio=value;if(value!=null)lastWirelessRadio=value.InstanceId;OnPropertyChanged();OnPropertyChanged(nameof(WirelessOutputHint));}
    }
    public string WirelessOutputHint=>"外发："+(pro2Slots[0].SelectedMode==ViiperVirtualMode.Pro2?"请选择 Pro1、Xbox 或 PS5":WirelessPadModes.Label(WirelessPadModes.FromVirtual(pro2Slots[0].SelectedMode)))+"。切换身份后，接收端可能需要忘记旧配对再连接。"+
        (pro2Slots[0].IsBleInputSelected||pro2Slots[0].IsPro1WirelessSelected?"无线输入需要另一只由 Windows 管理的蓝牙适配器。":"USB 输入只需一只蓝牙发射器。")+
        "仅接管选中的适配器，先备份，停止后恢复。PS5 支持兼容马达反馈；蓝牙触觉音频、自适应扳机和主机认证未实现。";
    public string UnifiedConnectionTitle=>bluetoothOutput?wirelessOutput?.State??"选择手柄身份与蓝牙适配器，连接后转发":BleConnectionTitle;
    public string UnifiedConnectionHint=>bluetoothOutput?"选择输入与外发身份，停止后恢复系统蓝牙。"+(pro2Slots[0].IsBleInputSelected||pro2Slots[0].IsPro1WirelessSelected?"无线输入需要两只蓝牙适配器。":"USB 输入只需一只蓝牙适配器。"):BleConnectionHint;
    public string UnifiedStateText=>bluetoothOutput?wirelessOutput?.RadioFinished==true?"转发已停止":wirelessOutput?.ReceiverConnected==true?"蓝牙外发中":wirelessOutput?.RadioStarted==true?"等待接收端配对":"蓝牙外发待连接":CompactStateText;
    public string UnifiedFooterText=>bluetoothOutput?"蓝牙转发 · 仅接管选中的适配器":"纯软件 · 无需外置接收器";
    public string Ps5ModeHint=>bluetoothOutput?"蓝牙 · 体感 · 普通震动":"触觉转换 · 陀螺仪";
    public string WirelessFirmware=>wirelessOutput?.Firmware??"未读取";
    public string WirelessHeadset=>wirelessOutput?.LatestState is {HeadsetPresenceKnown:true} state?state.HeadsetPresent?"已插入":"未插入":"未知";
    public string UnifiedConnectText=>bluetoothOutput?(wirelessOutput==null?"连接并转发":"应用选择 / 重新转发"):CompactConnectText;
    public bool UnifiedCanConnect=>bluetoothOutput?!Busy:CompactCanConnect;
    public bool UnifiedCanStop=>!Busy&&(wirelessOutput!=null||CompactCanStop);
    public string WirelessInputSummary=>wirelessOutput?.Summary??"";
    public string WirelessNfcStatus=>wirelessNfcStatus;
    public bool CanReadWirelessNfc=>!Busy&&wirelessOutput?.CanReadNfc==true;
    public bool CanUseWirelessNfc=>!Busy&&wirelessOutput?.OutputMode==WirelessPadMode.Pro1;
    public ICommand SelectOutputDestinationCommand=>outputDestinationCommand??=new RelayCommand(parameter=>RunExclusiveAsync("切换输出位置",async token=>
    {
        bool next=parameter?.ToString()=="bluetooth";if(next==bluetoothOutput)return;
        if(pro2Slots.Any(controller=>!controller.CanChangeConnection))throw new IOException("请先完成或取消 NFC 读取。");
        if(!previewOnly)await StopAsync();bluetoothOutput=next;
        if(next&&pro2Slots[0].SelectedMode==ViiperVirtualMode.Pro2)SelectMode(ViiperVirtualMode.Pro1);
        if(next)await RefreshWirelessRadiosAsync();
        Status=next?"选择发射适配器和身份后，点击连接并转发。":"已切回本机游玩。";
        RefreshWirelessPresentation();
    },allowWirelessOperation:true));
    public ICommand RefreshWirelessRadiosCommand=>refreshRadiosCommand??=new RelayCommand(_=>RefreshWirelessRadiosAsync());
    public ICommand ReadWirelessNfcCommand=>readWirelessNfcCommand??=new RelayCommand(_=>RunExclusiveAsync("读取转发手柄 NFC",async token=>
    {
        if(wirelessOutput==null)throw new IOException("先连接实体手柄。");
        await wirelessOutput.CaptureNfcAsync(token);wirelessNfcStatus="已读取 540 字节只读快照。"+(CanUseWirelessNfc?"接收端发起 NFC 扫描后应答；是否识别以接收端结果为准。":"当前身份不提供 NFC 外发。");OnPropertyChanged(nameof(WirelessNfcStatus));
    },allowWirelessOperation:true));
    public ICommand RemoveWirelessTagCommand=>removeWirelessTagCommand??=new RelayCommand(_=>{wirelessOutput?.RemoveTag();wirelessNfcStatus="标签快照已移除。";OnPropertyChanged(nameof(WirelessNfcStatus));});
    public async Task RefreshWirelessRadiosAsync()
    {
        if(previewOnly)return;
        var found=await Task.Run(WirelessRelayEnvironment.EnumerateRadios);
        WirelessRadios.Clear();foreach(var radio in found)WirelessRadios.Add(radio);
        SelectedWirelessRadio=WirelessRelayRadioSelection.Resolve(found,lastWirelessRadio);
    }
    private async Task StartWirelessOutputCoreAsync(CancellationToken token)
    {
        if(previewOnly){Status="界面预览不连接设备。";return;}
        if(!WirelessRelayRuntime.IsAvailable)throw new IOException("此构建没有无线转发组件。");
        var selected=SelectedWirelessRadio??throw new IOException("请选择蓝牙发射适配器。多个适配器时不会自动替你选择。");
        if(pro2Slots.Any(controller=>!controller.CanChangeConnection))throw new IOException("请先完成或取消 NFC 读取。");
        var slot=pro2Slots[0];var kind=slot.SelectedInputKind;
        var hid=slot.SelectedPhysicalDevice;
        bool wirelessInput=kind is ControllerInputKind.Pro2Ble or ControllerInputKind.Pro1Bluetooth;
        if(wirelessInput&&!WirelessRelayEnvironment.EnumerateRadios().Any(r=>r.InstanceId!=selected.InstanceId&&r.Service.Equals("BTHUSB",StringComparison.OrdinalIgnoreCase)))
            throw new IOException("无线输入需要另一只系统蓝牙接收手柄。可改用 USB 输入，或保留另一只 Windows 蓝牙适配器。");
        var mode=WirelessPadModes.FromVirtual(slot.SelectedMode);
        string runtime=await Task.Run(()=>WirelessRelayRuntime.EnsureExtracted(),token);
        await StopAsync();
        foreach(var controller in pro2Slots){await DisconnectPro2SlotAsync(controller,token);controller.PhysicalInput=null;controller.RefreshFromSource();}
        wirelessNfcStatus="";
        WirelessRelayModeActive=true;
        wirelessLastLog=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PRO2WirelessReceiverControlBoard","wireless_relay_logs",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        var engine=new ExperimentSession(wirelessLastLog,AppendLog,runtime,integrated:true){RumbleGain=rumbleMultiplier};wirelessOutput=engine;
        var face=new FaceButtonMapper();var rear=new RearButtonMapper();ViiperVirtualMode mappedMode=slot.SelectedMode;
        Interlocked.Exchange(ref wirelessRearCancelRequested,0);long previousInputTicks=0;
        engine.TransformInput=(state,ticks)=>
        {
            // Keep all mapper state on the physical-input consumer. Resetting
            // while held arms only after release, matching local mode behavior.
            if(Interlocked.Exchange(ref wirelessRearCancelRequested,0)!=0)rear.Reset();
            if(previousInputTicks>0&&ticks>previousInputTicks&&System.Diagnostics.Stopwatch.GetElapsedTime(previousInputTicks,ticks).TotalMilliseconds>100)
                rear.TryHandleInputGap(System.Diagnostics.Stopwatch.GetElapsedTime(previousInputTicks,ticks),out _);
            previousInputTicks=ticks;
            return rear.Apply(face.Apply(state,userSettings.FaceButtons.ForMode(mappedMode)),mappedMode,userSettings.RearButtons,ticks);
        };
        try
        {
            // Prepare the transmitter before creating a wireless input link.
            // The remaining Windows radio then owns the receiving side.
            if(wirelessInput)selected=await engine.PrepareRadioAsync(selected,token,continuous:true);
            if(kind==ControllerInputKind.Pro2Ble)await engine.ConnectBleAsync(token);
            else
            {
                if(hid==null)throw new IOException("请选择要连接的实体手柄。");
                await engine.ConnectUsbAsync(hid,token);
            }
            if(!wirelessInput)selected=await engine.PrepareRadioAsync(selected,token,continuous:true);
            await engine.StartRadioAsync(selected,Path.Combine(runtime,"pro2-wireless-pro1-radio.exe"),Path.Combine(runtime,"firmware"),0,token,SelectedWirelessRate,mode);
            Status="正在转发为 "+WirelessPadModes.Label(mode)+"；接收端实测性能尚待验证。";
            AppendLog("[UNIFIED_OUTPUT] destination=bluetooth mode="+mode+" input="+kind+" radio="+selected.InstanceId+" requested_hz="+SelectedWirelessRate);
        }
        catch(Exception error)
        {
            engine.Log("[UNIFIED_RELAY_START_FAILED] "+error);await StopWirelessOutputCoreAsync();throw;
        }
        finally{RefreshWirelessPresentation();}
    }
    private async Task StopWirelessOutputCoreAsync()
    {
        var engine=wirelessOutput;
        if(engine!=null)
        {
            try{await engine.DisposeAsync();}
            finally{wirelessOutput=null;WirelessRelayModeActive=false;RefreshWirelessPresentation();}
        }
    }
    public Task LoadWirelessTagAsync(string path)=>RunExclusiveAsync("载入只读标签",async token=>
    {
        if(wirelessOutput==null)throw new IOException("先连接并启动转发。");
        if(wirelessOutput.OutputMode!=WirelessPadMode.Pro1)throw new IOException("仅 Pro1 外发支持 NFC 快照。");
        await wirelessOutput.LoadTagAsync(path,token);wirelessNfcStatus="只读标签快照已载入。";OnPropertyChanged(nameof(WirelessNfcStatus));
    },allowWirelessOperation:true);
    public string WirelessLogDirectory=>wirelessLastLog;
    private void CancelWirelessRearButtons()
    {
        if(wirelessOutput==null)return;
        Interlocked.Exchange(ref wirelessRearCancelRequested,1);
        AppendLog("[WIRELESS_MAPPING_CANCEL] release_on_next_fresh_input=true rearm=release_then_press");
    }
    private void RefreshWirelessTelemetry()
    {
        if(wirelessOutput!=null)wirelessOutput.RumbleGain=rumbleMultiplier;
        var state=wirelessOutput?.LatestState??GamepadState.Neutral();bool live=wirelessOutput?.UsbConnected==true;
        compactInputRate=live?wirelessOutput!.InputHz.ToString("0.0"):"—";
        compactBatteryVisual=BatteryVisual.Create(state,live,state.BatteryLevel!=GamepadState.BatteryUnknown,live,"实体输入");
        compactBattery=Pro2BatteryStatus.Display(state,live);
        foreach(string property in new[]{nameof(CompactInputRate),nameof(CompactBatteryVisual),nameof(CompactBattery),nameof(CompactFirmware),nameof(CompactHeadset),nameof(BleStabilitySummary),nameof(PrimaryInputLabel)})OnPropertyChanged(property);
    }
    private void RefreshWirelessPresentation()
    {
        foreach(string property in new[]{nameof(IsBluetoothOutput),nameof(IsLocalOutput),nameof(Ps5ModeHint),nameof(CanUseLocalTools),nameof(CanUseNativePro2Output),nameof(HasWirelessOutput),nameof(UnifiedConnectionTitle),nameof(UnifiedConnectionHint),nameof(UnifiedStateText),nameof(UnifiedFooterText),nameof(UnifiedConnectText),nameof(UnifiedCanConnect),nameof(UnifiedCanStop),nameof(WirelessOutputHint),nameof(WirelessInputSummary),nameof(WirelessNfcStatus),nameof(CanReadWirelessNfc),nameof(CanUseWirelessNfc),nameof(CanOperate)})OnPropertyChanged(property);
    }
}
