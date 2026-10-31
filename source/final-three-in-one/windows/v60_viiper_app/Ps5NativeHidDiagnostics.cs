using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Y700Switch2V60Viiper;

// Only HID metadata; no feature command, input state, output, pairing or driver write.
public static class Ps5NativeHidDiagnostics
{
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public IntPtr Handle; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] private struct HidCaps
    {
        public ushort Usage, UsagePage, Input, Output, Feature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort LinkCollections, InputButtons, InputValues, InputIndices, OutputButtons,
            OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    public static bool MatchesNativeReportSizes(int input, int output, int feature) =>
        input == 64 && output == 48 && feature == 64;
    public static string Capture()
    {
        try
        {
            uint count=0, size=(uint)Marshal.SizeOf<RawDevice>();
            if(GetRawInputDeviceList(null,ref count,size)==uint.MaxValue || count>128) return "PS5 HID：枚举不可用。";
            var devices=new RawDevice[count];
            if(GetRawInputDeviceList(devices,ref count,size)==uint.MaxValue) return "PS5 HID：枚举失败。";
            var found=new List<string>();
            foreach(var device in devices)
            {
                if(device.Type!=2) continue;
                uint length=0;
                GetRawInputDeviceInfo(device.Handle,0x20000007,null,ref length);
                if(length==0 || length>4096) continue;
                var path=new StringBuilder((int)length+1);
                if(GetRawInputDeviceInfo(device.Handle,0x20000007,path,ref length)==uint.MaxValue ||
                    !path.ToString().Contains("VID_054C&PID_0CE6",StringComparison.OrdinalIgnoreCase)) continue;
                using var handle=CreateFile(path.ToString(),0,3,IntPtr.Zero,3,0,IntPtr.Zero);
                if(handle.IsInvalid || !HidD_GetPreparsedData(handle,out IntPtr data)) continue;
                try
                {
                    if(HidP_GetCaps(data,out HidCaps caps)!=0x00110000 || caps.UsagePage!=1 || caps.Usage!=5) continue;
                    bool matched=MatchesNativeReportSizes(caps.Input,caps.Output,caps.Feature);
                    found.Add($"输入 / 输出 / Feature：{caps.Input} / {caps.Output} / {caps.Feature} 字节；" +
                        (matched ? "报告长度匹配实体 DualSense。" : "报告长度不匹配，需检查后端版本或残留设备。"));
                }
                finally { HidD_FreePreparsedData(data); }
            }
            return "PS5 原生 HID 检查（只读）\n" +
                (found.Count==0 ? "尚未枚举到标准 DualSense HID。" : string.Join("\n",found)) +
                (found.Count>1 ? "\n检测到多个相同 VID/PID，以上结果不证明每个设备的归属。" : "") +
                "\nHID 长度通过不等于游戏或原生触觉已经验收。";
        }
        catch(Exception ex) { return "PS5 HID：检查失败，" + ex.GetType().Name; }
    }
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([Out] RawDevice[]? list,ref uint count,uint size);
    [DllImport("user32.dll",EntryPoint="GetRawInputDeviceInfoW",CharSet=CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device,uint command,StringBuilder? data,ref uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern SafeFileHandle CreateFile(
        string path,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle,out IntPtr data);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data,out HidCaps caps);
}
