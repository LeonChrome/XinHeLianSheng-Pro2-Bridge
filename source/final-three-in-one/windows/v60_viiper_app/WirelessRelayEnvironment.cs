using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

public sealed record WirelessRelayRadio(string InstanceId,string Name,string Service,bool KnownBackend,bool Realtek,
    string DriverProvider="",string DriverVersion="",string DriverDate="",uint ProblemCode=0,uint DevNodeStatus=8,bool StatusKnown=true,bool BluetoothUsbClass=false)
{
    public bool IsActions=>InstanceId.StartsWith(@"USB\VID_10D7&PID_B012\",StringComparison.OrdinalIgnoreCase);
    private bool IsRealtekVendor=>InstanceId.StartsWith(@"USB\VID_0BDA&PID_",StringComparison.OrdinalIgnoreCase);
    private bool StandardHci=>WirelessRelayEnvironment.IsSupportedUsbInstance(InstanceId)&&!IsRealtekVendor&&!IsActions&&
        (BluetoothUsbClass||WirelessRelayEnvironment.IsKnownStandardHci(InstanceId));
    public bool SwitchReady=>Healthy&&Service.Equals("WinUSB",StringComparison.OrdinalIgnoreCase)&&(Realtek||StandardHci);
    public bool SwitchCanPrepare=>Realtek&&CanPrepare;
    public string SwitchHint=>!Healthy&&!SwitchCanPrepare?DeviceStatus:
        IsActions?"这款 Actions 在一代 Switch 配对时拒绝加密密钥长度查询。请选择其他适配器。":
        SwitchReady?Realtek?"Realtek 0852 · 已验证 Switch 1 外发。":"标准 USB 蓝牙 HCI · 已就绪，兼容性以本次握手为准。":
        SwitchCanPrepare?"将备份所选 Realtek 的驱动，停止后恢复。":
        StandardHci||SystemBluetoothReady?"系统蓝牙驱动无法直接外发。请选择已就绪的 WinUSB 蓝牙发射器。":
        IsRealtekVendor?"此 Realtek 型号尚无匹配的外发固件。":"未识别为可外发的经典蓝牙 USB 接口。";
    public bool Healthy=>StatusKnown&&ProblemCode==0&&(DevNodeStatus&0x400)==0&&(DevNodeStatus&8)!=0;
    public bool SystemBluetoothReady=>Healthy&&Service.Equals("BTHUSB",StringComparison.OrdinalIgnoreCase);
    public bool Ready=>KnownBackend&&Healthy&&Service.Equals("WinUSB",StringComparison.OrdinalIgnoreCase);
    // The guarded driver helper can recover only these two BTHUSB failures.
    // Disabled devices and every other error remain visible, without enabling preparation.
    public bool CanPrepare=>KnownBackend&&Service.Equals("BTHUSB",StringComparison.OrdinalIgnoreCase)&&StatusKnown&&(Healthy||ProblemCode is 10 or 43);
    public string ChipLabel=>Realtek?"Realtek（0BDA:0852）":IsActions?"Actions（10D7:B012）":StandardHci?"USB 蓝牙 HCI":"蓝牙适配器";
    public string DeviceStatus=>!StatusKnown?"设备状态未读取":ProblemCode switch
    {
        10=>"无法启动（代码 10）",22=>"已禁用（代码 22）",43=>"Windows 已停止此设备（代码 43）",
        0 when (DevNodeStatus&0x400)!=0=>"设备报告问题（代码 0）",
        0 when (DevNodeStatus&8)==0=>"设备未启动（代码 0）",0=>"正常",_=>"设备错误（代码 "+ProblemCode+"）"
    };
    public string StateLabel=>!KnownBackend?(SwitchReady?"Switch 1 外发 · 可测试":DeviceStatus+" · "+Service):Ready?"发射模式 · 就绪":CanPrepare?Healthy?"系统蓝牙 · 可准备":DeviceStatus+" · 可尝试恢复":!Healthy?DeviceStatus:"驱动未支持（"+Service+"）";
    public string SelectionHint=>!KnownBackend?"此芯片尚无外发后端。":Ready?"连接时使用这一只发射器。":CanPrepare?Healthy?"连接时备份并准备这一只适配器。":"连接时仅尝试恢复这一只适配器，成功后再准备。":!StatusKnown?"请刷新后重试。":ProblemCode==22?"请先在设备管理器中启用，再刷新。":"请先解决此设备的驱动或状态问题，再刷新。";
    public string Label=>ChipLabel+" · "+Name+" · "+InstanceId.Replace("USB\\","",StringComparison.OrdinalIgnoreCase).Replace('\\',' ')+" · "+StateLabel;
}
public static class WirelessRelayEnvironment
{
    private const uint Present=0x100;
    private const string BluetoothClass="{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    public static bool IsSupportedUsbInstance(string id)=>id.Length<200&&System.Text.RegularExpressions.Regex.IsMatch(id,
        @"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&MI_[0-9A-F]{2})?\\[A-Z0-9&_.-]+\z",System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static bool IsKnownStandardHci(string id)=>id.StartsWith(@"USB\VID_0A5C&PID_21E8\",StringComparison.OrdinalIgnoreCase)||
        id.StartsWith(@"USB\VID_0B05&PID_17CB\",StringComparison.OrdinalIgnoreCase)||id.StartsWith(@"USB\VID_0A5C&PID_22BE\",StringComparison.OrdinalIgnoreCase);
    public static bool HasBluetoothUsbClass(IEnumerable<string> compatibleIds)=>compatibleIds.Any(x=>x.Equals(@"USB\Class_E0&SubClass_01&Prot_01",StringComparison.OrdinalIgnoreCase));
    public static bool HasKnownBackend(string instanceId)=>
        instanceId.StartsWith(@"USB\VID_0BDA&PID_0852\",StringComparison.OrdinalIgnoreCase)||
        instanceId.StartsWith(@"USB\VID_10D7&PID_B012\",StringComparison.OrdinalIgnoreCase);
    public static bool IsRadioDeviceNode(string instanceId,string classGuid,string service)
    {
        if(HasKnownBackend(instanceId))return true;
        // BTH* nodes describe Windows enumerators and paired peripherals, not
        // a physical radio that may be handed to the native USB backend.
        if(instanceId.StartsWith("BTH",StringComparison.OrdinalIgnoreCase)||
            instanceId.StartsWith(@"SWD\",StringComparison.OrdinalIgnoreCase)||
            instanceId.StartsWith(@"ROOT\",StringComparison.OrdinalIgnoreCase)||
            instanceId.StartsWith(@"HID\",StringComparison.OrdinalIgnoreCase))return false;
        return classGuid.Equals(BluetoothClass,StringComparison.OrdinalIgnoreCase)||service.Equals("BTHUSB",StringComparison.OrdinalIgnoreCase);
    }
    public static IReadOnlyList<WirelessRelayRadio> EnumerateRadios()
    {
        var result=new List<WirelessRelayRadio>();
        if(!OperatingSystem.IsWindows())return result;
        // Enumerate present device nodes, not enabled USB interfaces: disabled
        // radios and a BTHUSB radio stopped with code 43 can have no interface.
        char[]? presentIds=null;
        for(int attempt=0;attempt<3;attempt++)
        {
            if(CM_Get_Device_ID_List_SizeW(out uint size,null,Present)!=0||size is <1 or >1048576)return result;
            var buffer=new char[size];
            uint read=CM_Get_Device_ID_ListW(null,buffer,size,Present);
            if(read==0){presentIds=buffer;break;}
            if(read!=0x1a)return result; // CR_BUFFER_SMALL: retry a changing PnP list.
        }
        if(presentIds==null)return result;
        foreach(string id in new string(presentIds).Split('\0',StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
            bool realtek=id.StartsWith(@"USB\VID_0BDA&PID_0852\",StringComparison.OrdinalIgnoreCase);
            using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\"+id);
            string service=key?.GetValue("Service") as string??"unknown";
            bool hciClass=HasBluetoothUsbClass(key?.GetValue("CompatibleIDs") as string[]??Array.Empty<string>());
            if(!IsRadioDeviceNode(id,key?.GetValue("ClassGUID") as string??"",service)&&
                !(IsSupportedUsbInstance(id)&&service.Equals("WinUSB",StringComparison.OrdinalIgnoreCase)&&(hciClass||IsKnownStandardHci(id))))continue;
            if(CM_Locate_DevNodeW(out uint node,id,0)!=0)continue; // Removed since the present-only snapshot.
            bool statusKnown=CM_Get_DevNode_Status(out uint status,out uint problem,node,0)==0;
            string name=key?.GetValue("FriendlyName") as string??key?.GetValue("DeviceDesc") as string??id;
            if(name.Contains(';'))name=name[(name.LastIndexOf(';')+1)..];
            string driverKey=key?.GetValue("Driver") as string??"";
            using var driver=driverKey.Length==0?null:Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+driverKey);
            result.Add(new(id,name,service,HasKnownBackend(id),realtek,driver?.GetValue("ProviderName") as string??"",driver?.GetValue("DriverVersion") as string??"",driver?.GetValue("DriverDate") as string??"",problem,status,statusKnown,hciClass));
            }
            catch(UnauthorizedAccessException){ /* An unrelated protected devnode must not hide all radios. */ }
            catch(System.Security.SecurityException){ }
            catch(System.IO.IOException){ /* Device removed during the registry snapshot. */ }
        }
        return result.DistinctBy(r=>r.InstanceId,StringComparer.OrdinalIgnoreCase).OrderBy(r=>r.ChipLabel,StringComparer.OrdinalIgnoreCase).ThenBy(r=>r.InstanceId,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint size,string? filter,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string? filter,[Out] char[] list,uint length,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node,string id,uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint node,uint flags);
}
public static class WirelessRelayRadioSelection
{
    // Preserve an explicit selection through re-enumeration. With multiple
    // radios, never fall back to another instance if the chosen one disappears.
    public static WirelessRelayRadio? Resolve(IReadOnlyList<WirelessRelayRadio> radios,string? previousInstance)
    {
        if(!string.IsNullOrWhiteSpace(previousInstance))
            return radios.FirstOrDefault(r=>r.InstanceId.Equals(previousInstance,StringComparison.OrdinalIgnoreCase));
        return radios.Count==1?radios[0]:null;
    }
}
