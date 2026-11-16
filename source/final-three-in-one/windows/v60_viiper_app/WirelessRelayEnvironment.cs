using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

public sealed record WirelessRelayRadio(string InstanceId,string Name,string Service,bool KnownBackend,bool Realtek,
    string DriverProvider="",string DriverVersion="",string DriverDate="")
{
    public bool Ready=>KnownBackend&&Service.Equals("WinUSB",StringComparison.OrdinalIgnoreCase);
    public bool CanPrepare=>KnownBackend&&Service.Equals("BTHUSB",StringComparison.OrdinalIgnoreCase);
    public string Label=>Name+" · "+InstanceId.Replace("USB\\","",StringComparison.OrdinalIgnoreCase).Replace('\\',' ')+" · "+(Ready?"发射模式":CanPrepare?"系统蓝牙 · 可准备":"当前后端未支持");
}
public static class WirelessRelayEnvironment
{
    public static IReadOnlyList<WirelessRelayRadio> EnumerateRadios()
    {
        var result=new List<WirelessRelayRadio>();
        foreach(var item in HidNative.Interfaces(new Guid("a5dcbf10-6530-11d2-901f-00c04fb951ed")))
        {
            string id=item.InstanceId;
            bool realtek=id.StartsWith(@"USB\VID_0BDA&PID_0852\",StringComparison.OrdinalIgnoreCase);
            bool actions=id.StartsWith(@"USB\VID_10D7&PID_B012\",StringComparison.OrdinalIgnoreCase);
            using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\"+id);
            string service=key?.GetValue("Service") as string??"unknown";
            bool bluetooth=string.Equals(key?.GetValue("ClassGUID") as string,"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}",StringComparison.OrdinalIgnoreCase)||service.Equals("BTHUSB",StringComparison.OrdinalIgnoreCase);
            if(!realtek&&!actions&&!bluetooth)continue;
            string name=key?.GetValue("FriendlyName") as string??key?.GetValue("DeviceDesc") as string??id;
            if(name.Contains(';'))name=name[(name.LastIndexOf(';')+1)..];
            string driverKey=key?.GetValue("Driver") as string??"";
            using var driver=driverKey.Length==0?null:Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+driverKey);
            result.Add(new(id,name,service,realtek||actions,realtek,driver?.GetValue("ProviderName") as string??"",driver?.GetValue("DriverVersion") as string??"",driver?.GetValue("DriverDate") as string??""));
        }
        return result.DistinctBy(r=>r.InstanceId,StringComparer.OrdinalIgnoreCase).ToArray();
    }
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
