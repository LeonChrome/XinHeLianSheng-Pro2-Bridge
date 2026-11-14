using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace Y700Switch2V60Viiper;

public sealed record VirtualDeviceRepairResult(string Operation, string InstanceId, DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc, bool Attempted, int? RemoveExit, string Message)
{
    public bool Removed => Attempted && RemoveExit==0;
    public bool RebootRequired => Attempted && RemoveExit==3010;
}

public static class VirtualDeviceConfigurationRepair
{
    public const string Command="--repair-virtual-configuration";
    public static bool IsCommand(IReadOnlyList<string> args)=>args.Contains(Command,StringComparer.OrdinalIgnoreCase);
    public static bool TryIdentity(string instance,out string vid,out string pid)
    {
        foreach(var profile in new[]{ViiperDeviceProfile.Pro2,ViiperDeviceProfile.Pro1,ViiperDeviceProfile.Xbox,ViiperDeviceProfile.DualSenseLike})
        {
            for(int slot=1;slot<=4;slot++)
                if(instance.Equals(@"USB\VID_"+profile.ExpectedVid+"&PID_"+profile.ExpectedPid+@"\"+
                    ViiperDeviceProfile.SlotSerialNumber(profile.Mode,slot),StringComparison.OrdinalIgnoreCase))
                {vid=profile.ExpectedVid;pid=profile.ExpectedPid;return true;}
        }
        vid=pid="";return false;
    }
    public static bool MatchesTarget(VirtualPnpDevice device,string instance,int port)=>
        TryIdentity(instance,out _,out _)&&device.Problem==19&&device.Port==port&&
        device.InstanceId.Equals(instance,StringComparison.OrdinalIgnoreCase);
    public static bool MatchesProfile(string instance,ViiperDeviceProfile profile)=>
        TryIdentity(instance,out _,out _)&&instance.Equals(@"USB\VID_"+profile.ExpectedVid+"&PID_"+profile.ExpectedPid+@"\"+
            profile.DeviceSpecificSerialNumber,StringComparison.OrdinalIgnoreCase);
    public static async Task<int> RemoveValidatedAsync(string instance,int port,Func<VirtualPnpSnapshot> capture,
        Func<VirtualPnpDevice,Task> backup,Func<Task<int>> remove)
    {
        VirtualPnpDevice Current()
        {
            var snapshot=capture();
            var target=snapshot.Readable?snapshot.Devices.SingleOrDefault(d=>MatchesTarget(d,instance,port)):null;
            return target??throw new InvalidOperationException("目标已变化、已恢复或未确认属于 USBIP；未移除设备。");
        }
        await backup(Current()); // Any incomplete backup fails closed, before removal.
        _=Current(); // Do not act on an identity or problem code observed before backup.
        return await remove();
    }
    public static string ResultPath(string operation)
    {
        if(!Guid.TryParseExact(operation,"N",out _))throw new ArgumentException("无效的操作编号。");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PRO2ControllerAssistant","VirtualDeviceRepair",operation,"result.json");
    }
    public static async Task<VirtualDeviceRepairResult> RequestAsync(string instance,int port,CancellationToken token)
    {
        if(!TryIdentity(instance,out _,out _)||port is <0 or >127)throw new ArgumentException("不是本程序的虚拟手柄。");
        token.ThrowIfCancellationRequested();
        string operation=Guid.NewGuid().ToString("N");
        var info=new ProcessStartInfo(Environment.ProcessPath??throw new InvalidOperationException("无法定位助手程序。"))
        {UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        foreach(string arg in new[]{Command,"--operation-id",operation,"--instance-id",instance,"--port",port.ToString()})info.ArgumentList.Add(arg);
        var requested=DateTimeOffset.UtcNow;
        using var child=await Task.Run(()=>Process.Start(info),token)??throw new InvalidOperationException("管理员修复进程未启动。");
        // The helper has its own deadline. Do not kill a privileged device operation on UI cancellation.
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await child.WaitForExitAsync(deadline.Token);
        string path=ResultPath(operation);
        if(!File.Exists(path)||new FileInfo(path).Length>65536)throw new IOException("未读取到本次修复结果，不能确认成功。");
        var result=JsonSerializer.Deserialize<VirtualDeviceRepairResult>(File.ReadAllText(path));
        if(result==null||result.Operation!=operation||!result.InstanceId.Equals(instance,StringComparison.OrdinalIgnoreCase)||
            result.StartedUtc<requested.AddSeconds(-2)||result.CompletedUtc<result.StartedUtc||
            result.CompletedUtc>DateTimeOffset.UtcNow.AddSeconds(5))throw new IOException("修复结果身份或时间不匹配。");
        return result;
    }
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if(args.Length!=7||args[0]!=Command||args[1]!="--operation-id"||args[3]!="--instance-id"||args[5]!="--port"||
            !Guid.TryParseExact(args[2],"N",out _)||!TryIdentity(args[4],out string vid,out string pid)||
            !int.TryParse(args[6],out int port)||port is <0 or >127)return 7;
        string operation=args[2],instance=args[4],path=ResultPath(operation);
        DateTimeOffset started=DateTimeOffset.UtcNow;bool attempted=false;int? exit=null;string message="";
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return 5;
            if(Environment.OSVersion.Version.Build<19041)throw new InvalidOperationException("需要 Windows 10 2004 或更新版本。");
            EnsureSafeLocation(path);
            string directory=Path.GetDirectoryName(path)!;
            using var gate=new Semaphore(1,1,@"Global\PRO2VirtualConfigurationRepair");
            if(!gate.WaitOne(0))throw new InvalidOperationException("另一个虚拟手柄修复正在运行。");
            try
            {
                // A fresh USBIP ancestry, port and serial match is mandatory. A VID/PID or CLI string alone never authorizes removal.
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                exit=await RemoveValidatedAsync(instance,port,()=>WindowsVirtualDeviceProbe.Capture(vid,pid),async target=>
                {
                    WriteNew(Path.Combine(directory,"before.json"),JsonSerializer.Serialize(target,new JsonSerializerOptions{WriteIndented=true}));
                    WriteNew(Path.Combine(directory,"Restore.cmd"),
                        "@echo off\r\n\"%SystemRoot%\\System32\\pnputil.exe\" /scan-devices\r\n"+
                        "echo Reconnect this controller in PRO2 Controller Assistant. Driver packages and class filters were not removed.\r\npause\r\n");
                    string backup=Path.Combine(directory,"before.reg");
                    if(File.Exists(backup))throw new IOException("备份文件已存在，未执行修复。");
                    var exported=await RunAsync("reg.exe",["export",@"HKLM\SYSTEM\CurrentControlSet\Enum\"+instance,backup],timeout.Token);
                    if(exported.Exit!=0||!File.Exists(backup)||new FileInfo(backup).Length<16)throw new IOException("原设备配置备份失败，未移除设备。");
                    WriteNew(Path.Combine(directory,"backup.log"),exported.Output);
                },async()=>
                {
                    attempted=true;
                    var removed=await RunAsync("pnputil.exe",["/remove-device",instance],timeout.Token);
                    WriteNew(Path.Combine(directory,"remove.log"),removed.Output);
                    return removed.Exit;
                });
                message=exit==0 ? "已移除当前虚拟设备实例，等待助手重新挂载并验证输入接口。"
                    :exit==3010 ? "Windows 要求重启后完成设备重建；程序不会自动重启。"
                    :"Windows 未完成设备移除，退出码 "+exit+"；不自动重试。";
            }
            finally{gate.Release();}
        }
        catch(Exception e){message=e is OperationCanceledException?"修复超时，结果未核实；不自动重试。":e.Message;}
        var result=new VirtualDeviceRepairResult(operation,instance,started,DateTimeOffset.UtcNow,attempted,exit,message);
        try{WriteNew(path,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));}catch{return 5;}
        return result.Removed?0:result.RebootRequired?2:1;
    }
    private static void EnsureSafeLocation(string path)
    {
        for(string? current=Path.GetDirectoryName(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if(Directory.Exists(current)&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("日志路径不可用。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if(File.Exists(path))throw new IOException("操作编号已使用，未重复修复。");
    }
    private static void WriteNew(string path,string value)
    {
        EnsureSafeLocation(path);
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false));writer.Write(value);
    }
    private static async Task<(int Exit,string Output)> RunAsync(string tool,string[] args,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,tool))
        {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var process=Process.Start(info)??throw new IOException("未启动 "+tool);
        var stdout=ReadBoundedAsync(process.StandardOutput.BaseStream);
        var stderr=ReadBoundedAsync(process.StandardError.BaseStream);
        try{await process.WaitForExitAsync(token);}catch{try{process.Kill();}catch{}throw;}
        return(process.ExitCode,(await stdout)+"\n"+(await stderr));
    }
    private static async Task<string> ReadBoundedAsync(Stream stream)
    {
        using var buffer=new MemoryStream();byte[] block=new byte[2048];int count;
        while((count=await stream.ReadAsync(block))>0)
            if(buffer.Length<65536)buffer.Write(block,0,(int)Math.Min(count,65536-buffer.Length));
        return BluetoothRestartPolicy.DecodeNativeOutput(buffer.ToArray());
    }
}
