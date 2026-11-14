using Microsoft.Win32;
using System.Text;

namespace Y700Switch2V60Viiper;

public static class VirtualDeviceConfigurationDiagnosis
{
    public static string Capture(string instance)
    {
        try
        {
            using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\"+instance);
            if(key==null) return "设备配置：未能读取，不能据此认定筛选驱动损坏。";
            var text=new StringBuilder("设备配置（只读；不删除筛选器）\n");
            string service=key.GetValue("Service")?.ToString()??"";
            string driver=key.GetValue("Driver")?.ToString()??"";
            string classId=key.GetValue("ClassGUID")?.ToString()??"";
            text.AppendLine("服务："+ServiceText(service)+" · 类："+classId);
            using var driverKey=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+driver);
            text.AppendLine("驱动 INF："+(driverKey?.GetValue("InfPath")?.ToString()??"未获取"));
            Filters(text,key,"设备");
            if(Guid.TryParse(classId,out _))
            {
                using var classKey=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+classId);
                if(classKey!=null) Filters(text,classKey,"设备类（影响同类设备）");
            }
            text.Append("服务未注册是配置异常线索，不能据此删除整个 UpperFilters / LowerFilters；类筛选器应通过对应软件修复。\n"+
                "参考：https://learn.microsoft.com/windows-hardware/drivers/install/cm-prob-registry");
            return text.ToString();
        }
        catch(Exception e) { return "设备配置未完整读取："+e.GetType().Name+"；未改注册表。"; }
    }
    private static void Filters(StringBuilder text,RegistryKey key,string scope)
    {
        foreach(string name in new[]{"UpperFilters","LowerFilters"})
        {
            object? value=key.GetValue(name);
            var entries=value is string[] array ? array : value is string s ? new[]{s} : Array.Empty<string>();
            text.AppendLine(scope+" "+name+"："+(entries.Length==0 ? "未配置" : string.Join("；",entries.Select(ServiceText))));
        }
    }
    private static string ServiceText(string name)
    {
        if(string.IsNullOrWhiteSpace(name)) return "未配置";
        if(name.Contains('\\')||name.Contains('/')||name.Length>256) return name+"（名称异常，未查询）";
        using var services=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if(services==null) return name+"（注册状态未读取）";
        using var service=services.OpenSubKey(name);
        return name+(service==null ? "（服务未注册）" : "（服务已注册；不代表驱动已成功加载）");
    }
}
