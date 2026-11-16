using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

public sealed record WirelessRelayRadio(string InstanceId,string Name,string Service,bool KnownBackend,bool Realtek,
    string DriverProvider="",string DriverVersion="",string DriverDate="")
{
    public bool Ready=>KnownBackend&&Service.Equals("WinUSB",StringComparison.OrdinalIgnoreCase);
    public string Label=>Name+" · "+Service+" · "+(Ready?"可用于实验":"保留系统驱动，未接管");
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
            if(!realtek&&!actions)continue;
            using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\"+id);
            string service=key?.GetValue("Service") as string??"unknown";
            string name=key?.GetValue("FriendlyName") as string??key?.GetValue("DeviceDesc") as string??id;
            if(name.Contains(';'))name=name[(name.LastIndexOf(';')+1)..];
            string driverKey=key?.GetValue("Driver") as string??"";
            using var driver=driverKey.Length==0?null:Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+driverKey);
            result.Add(new(id,name,service,true,realtek,driver?.GetValue("ProviderName") as string??"",driver?.GetValue("DriverVersion") as string??"",driver?.GetValue("DriverDate") as string??""));
        }
        return result.DistinctBy(r=>r.InstanceId,StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
