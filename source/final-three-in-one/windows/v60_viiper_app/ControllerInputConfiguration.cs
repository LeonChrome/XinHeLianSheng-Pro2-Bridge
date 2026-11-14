using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Y700Switch2V60Viiper;

public enum ControllerInputKind { Pro2Ble, Pro2Usb, Pro1Bluetooth, Pro1Usb }
public sealed record ControllerInputOption(ControllerInputKind Kind, string Label)
{
    public bool IsWireless => Kind is ControllerInputKind.Pro2Ble or ControllerInputKind.Pro1Bluetooth;
    public string ModelName => Kind is ControllerInputKind.Pro2Ble or ControllerInputKind.Pro2Usb ? "Pro2" : "Pro1";
    public string TransportName => IsWireless ? "无线" : "USB 有线";
    public System.Windows.Media.ImageSource Artwork => ControllerConnectionArtwork.For(Kind);
    public System.Windows.Rect ArtworkViewbox => ModelName == "Pro2" ? new(0, .14, 1, .72) : new(.20, 0, .60, 1);
    public string TransportIcon => IsWireless
        ? "M 8,1 L 14,6 L 3,14 L 8,18 L 8,1 M 3,5 L 14,13 L 8,18"
        : "M 6,1 L 6,5 M 12,1 L 12,5 M 4,5 L 14,5 L 14,9 C 14,12 12,14 9,14 C 6,14 4,12 4,9 Z M 9,14 L 9,18";
}
public sealed record ControllerInputConfiguration(ControllerInputKind Kind = ControllerInputKind.Pro2Ble, string PhysicalId = "")
{
    public static readonly IReadOnlyList<ControllerInputOption> Options = new[]
    {
        new ControllerInputOption(ControllerInputKind.Pro2Ble,"Pro2 · 无线"),
        new ControllerInputOption(ControllerInputKind.Pro2Usb,"Pro2 · 有线 USB"),
        new ControllerInputOption(ControllerInputKind.Pro1Bluetooth,"Pro1 · 无线"),
        new ControllerInputOption(ControllerInputKind.Pro1Usb,"Pro1 · 有线 USB")
    };
    public static ControllerInputKind FromPhysical(NintendoInputKind kind) => kind switch
    { NintendoInputKind.Pro2Usb=>ControllerInputKind.Pro2Usb, NintendoInputKind.Pro1Usb=>ControllerInputKind.Pro1Usb, _=>ControllerInputKind.Pro1Bluetooth };
    public static ControllerInputConfiguration[] Normalize(ControllerInputConfiguration[]? source) => Enumerable.Range(0,4).Select(i=>
    {
        var value=source!=null && i<source.Length ? source[i] : null;
        return value==null || !Enum.IsDefined(value.Kind) ? new() : new ControllerInputConfiguration(value.Kind,
            value.Kind==ControllerInputKind.Pro2Ble || value.PhysicalId==null || value.PhysicalId.Length>1024 ? "" : value.PhysicalId.Trim());
    }).ToArray();
}

public sealed partial class Pro2ControllerSlot
{
    private readonly System.Windows.Threading.Dispatcher? connectionDispatcher = System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
    private ControllerInputKind selectedInputKind;
    private string selectedPhysicalId="";
    private NintendoHidDevice? selectedPhysicalDevice;
    private string lastConnectionError="";
    public string LastConnectionError { get=>lastConnectionError; set { if(lastConnectionError==value)return;lastConnectionError=value;OnPropertyChanged(); } }
    public IReadOnlyList<ControllerInputOption> InputOptions => ControllerInputConfiguration.Options;
    public ObservableCollection<NintendoHidDevice> PhysicalDeviceChoices { get; } = new();
    public ControllerInputKind SelectedInputKind
    {
        get=>selectedInputKind;
        set
        {
            if(connectionDispatcher!=null&&!connectionDispatcher.CheckAccess()) { connectionDispatcher.Invoke(()=>SelectedInputKind=value); return; }
            if(!Enum.IsDefined(value) || selectedInputKind==value) return;
            selectedInputKind=value; selectedPhysicalId=""; selectedPhysicalDevice=null;
            PhysicalDeviceChoices.Clear(); OnPropertyChanged(); OnPropertyChanged(nameof(SelectedPhysicalDevice));
            OnPropertyChanged(nameof(InputConfiguration));
            RefreshConnectionChoice();
        }
    }
    public NintendoHidDevice? SelectedPhysicalDevice
    {
        get=>selectedPhysicalDevice;
        set
        {
            if(connectionDispatcher!=null&&!connectionDispatcher.CheckAccess()) { connectionDispatcher.Invoke(()=>SelectedPhysicalDevice=value); return; }
            if(value!=null && ControllerInputConfiguration.FromPhysical(value.Kind)!=selectedInputKind) return;
            if(Equals(selectedPhysicalDevice,value)) return;
            string previousId=selectedPhysicalId;
            selectedPhysicalDevice=value;
            if(value!=null) selectedPhysicalId=value.PhysicalId;
            OnPropertyChanged();
            if(previousId!=selectedPhysicalId) OnPropertyChanged(nameof(InputConfiguration));
            RefreshConnectionChoice();
        }
    }
    public string SelectedPhysicalId => selectedPhysicalId;
    public ControllerInputConfiguration InputConfiguration => new(selectedInputKind,selectedPhysicalId);
    public bool IsPhysicalInputSelected => selectedInputKind!=ControllerInputKind.Pro2Ble;
    public bool IsPro1WirelessSelected => selectedInputKind==ControllerInputKind.Pro1Bluetooth;
    public bool IsBleInputSelected => !IsPhysicalInputSelected;
    public bool CanOptimizeBleInput => IsBleInputSelected && PhysicalInput==null;
    public bool CanChangeConnection => PhysicalInput?.IsReadingNfc != true;
    public string InputChoiceLabel => InputOptions.Single(x=>x.Kind==selectedInputKind).Label;
    public bool InputChoicePending => (PhysicalInput is { } physical ? ControllerInputConfiguration.FromPhysical(physical.Device.Kind) : ControllerInputKind.Pro2Ble)!=selectedInputKind ||
        PhysicalInput!=null && IsPhysicalInputSelected && selectedPhysicalId.Length>0 && !string.Equals(PhysicalInput.Device.PhysicalId,selectedPhysicalId,StringComparison.OrdinalIgnoreCase);
    public string InputChoiceHint => selectedInputKind switch
    {
        ControllerInputKind.Pro2Ble=>"开启 Pro2 配对广播后连接，无需先在 Windows 配对。",
        ControllerInputKind.Pro2Usb=>"使用 USB 数据线；先退出 Steam，接入助手后再启动 Steam。",
        ControllerInputKind.Pro1Bluetooth=>"先在 Windows 蓝牙设置配对 Pro Controller，再刷新列表并连接。",
        _=>"使用 USB 数据线，刷新后选择实体 Pro1。"
    };
    public string DeviceChoiceHint => !IsPhysicalInputSelected || PhysicalDeviceChoices.Count>0 ? "" : selectedPhysicalId.Length>0 ?
        "尚未发现上次选择的手柄；接入后刷新，或选择另一只。" : "尚未发现此类手柄，检查连接后刷新。";
    public string InputConnectionSummary => InputChoicePending ? "待应用："+InputChoiceLabel+" · 点击连接后生效" :
        "当前输入："+(PhysicalInput!=null ? PhysicalInput.Device.SelectionLabel : "Pro2 · 无线");
    public void LoadInputConfiguration(ControllerInputConfiguration value)
    {
        if(connectionDispatcher!=null&&!connectionDispatcher.CheckAccess()) { connectionDispatcher.Invoke(()=>LoadInputConfiguration(value)); return; }
        selectedInputKind=value.Kind; selectedPhysicalId=value.PhysicalId;
        OnPropertyChanged(nameof(SelectedInputKind)); RefreshConnectionChoice();
    }
    public void UpdateDeviceChoices(IEnumerable<NintendoHidDevice> devices)
    {
        if(connectionDispatcher!=null&&!connectionDispatcher.CheckAccess())
        { var snapshot=devices.ToArray();connectionDispatcher.Invoke(()=>UpdateDeviceChoices(snapshot));return; }
        string previousId=selectedPhysicalId;
        var matches=devices.Where(d=>ControllerInputConfiguration.FromPhysical(d.Kind)==selectedInputKind).ToArray();
        PhysicalDeviceChoices.Clear(); foreach(var d in matches) PhysicalDeviceChoices.Add(d);
        selectedPhysicalDevice=matches.FirstOrDefault(d=>string.Equals(d.PhysicalId,selectedPhysicalId,StringComparison.OrdinalIgnoreCase));
        if(selectedPhysicalDevice==null && selectedPhysicalId.Length==0 && matches.Length==1)
        { selectedPhysicalDevice=matches[0]; selectedPhysicalId=matches[0].PhysicalId; }
        OnPropertyChanged(nameof(SelectedPhysicalDevice));
        if(previousId!=selectedPhysicalId) OnPropertyChanged(nameof(InputConfiguration));
        RefreshConnectionChoice();
    }
    public void RefreshConnectionChoice()
    {
        foreach(string name in new[]{nameof(IsPhysicalInputSelected),nameof(IsPro1WirelessSelected),nameof(IsBleInputSelected),nameof(CanOptimizeBleInput),nameof(CanChangeConnection),
            nameof(InputChoiceLabel),nameof(InputChoiceHint),nameof(DeviceChoiceHint),nameof(InputChoicePending),nameof(InputConnectionSummary)}) OnPropertyChanged(name);
    }
}
