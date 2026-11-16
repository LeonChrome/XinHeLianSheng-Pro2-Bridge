using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Y700Switch2V60Viiper;

namespace Pro2WirelessExperiment;

public sealed class RadioDriverLease:IAsyncDisposable
{
    private readonly Action<string> log;
    private readonly Process? guard;
    private readonly SemaphoreSlim gate=new(1,1);
    public string StateDirectory {get;}
    public string InstanceId {get;}
    public bool Prepared {get;private set;}
    public bool Restored {get;private set;}
    private RadioDriverLease(string state,string instance,Process? helper,Action<string> trace)
    {StateDirectory=state;InstanceId=instance;guard=helper;log=trace;}
    public static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PRO2WirelessReceiverControlBoard","radio");
    public static async Task<string> InspectAsync(string instance,CancellationToken token,string? runtimeDirectory=null)
    {
        string helper=Path.Combine(runtimeDirectory??AppContext.BaseDirectory,"RadioDriver.ps1");
        if(!File.Exists(helper))throw new FileNotFoundException("蓝牙转发组件缺失，请重新启动助手；独立实验包需完整解压。",helper);
        var info=PowerShell(helper,false);Add(info,"-Operation","Inspect","-InstanceId",instance);
        info.RedirectStandardOutput=info.RedirectStandardError=true;
        using var process=Process.Start(info)??throw new IOException("无法启动发射器检查。");
        Task<string> output=process.StandardOutput.ReadToEndAsync(token),error=process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if(process.ExitCode!=0)throw new IOException("发射器检查失败："+await error);
        return await output;
    }
    public static async Task<RadioDriverLease?> PrepareAsync(WirelessRelayRadio radio,Action<string> log,CancellationToken token,string? runtimeDirectory=null,bool continuous=false)
    {
        if(radio.Ready)return null; // Never restore a WinUSB configuration we did not change.
        if(!radio.CanPrepare)throw new NotSupportedException("所选蓝牙尚无可用外发后端，未修改驱动。可选已支持的 Realtek / Actions。");
        runtimeDirectory=Path.GetFullPath(runtimeDirectory??AppContext.BaseDirectory);
        string inspection=await InspectAsync(radio.InstanceId,token,runtimeDirectory);
        using(var parsed=JsonDocument.Parse(inspection))
        {
            if(!parsed.RootElement.GetProperty("known_binding_available").GetBoolean())
                throw new IOException("尚未找到此发射器已信任的 WinUSB 绑定包："+parsed.RootElement.GetProperty("error").GetString()+"。未修改驱动。");
        }
        string state=Path.Combine(Root,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..6]);
        CreatePrivate(state);
        string helper=Path.Combine(state,"RadioDriver.ps1");
        File.Copy(Path.Combine(runtimeDirectory,"RadioDriver.ps1"),helper);
        WriteRestoreLauncher(state,radio.InstanceId,helper);
        using var owner=Process.GetCurrentProcess();
        var info=PowerShell(helper,true);
        Add(info,"-Operation","Prepare","-InstanceId",radio.InstanceId,"-StateDirectory",state,"-OwnerPid",Environment.ProcessId.ToString(),"-OwnerStartTicks",owner.StartTime.ToUniversalTime().Ticks.ToString());
        Add(info,"-BackendPath",Path.Combine(runtimeDirectory,"pro2-wireless-pro1-radio.exe"),"-MaxMinutes",continuous?"0":"12");
        log("[RADIO_PREPARE] instance="+radio.InstanceId+" state="+state+" backup=current_windows_driver scope=exact_instance system_bluetooth_temporarily_unavailable=true restore=automatic_and_cmd");
        Process? helperProcess;
        try{helperProcess=Process.Start(info)??throw new IOException("未能启动管理员准备程序。");}
        catch(Win32Exception e)when(e.NativeErrorCode==1223){throw new IOException("已取消管理员授权，发射器驱动未切换。",e);}
        var lease=new RadioDriverLease(state,radio.InstanceId,helperProcess,log);
        try
        {
            using var bounded=CancellationTokenSource.CreateLinkedTokenSource(token);bounded.CancelAfter(TimeSpan.FromSeconds(90));
            while(true)
            {
                bounded.Token.ThrowIfCancellationRequested();
                if(await ReadResultAsync(Path.Combine(state,"error.json")) is {} error)
                {using(error)throw new IOException("发射器准备失败："+error.RootElement.GetProperty("error").GetString()+"。备份保留："+state);}
                if(await ReadResultAsync(Path.Combine(state,"ready.json")) is {} ready)
                {
                    using(ready)
                    {
                        if(!ready.RootElement.GetProperty("success").GetBoolean())throw new IOException("管理员准备未确认成功。");
                    }
                    if(await ReadResultAsync(Path.Combine(state,"restore-result.json")) is {} restored)
                    {using(restored)throw new IOException("所选发射器在准备后已恢复原驱动；请查看 "+state);}
                    lease.Prepared=true;
                    log("[RADIO_PREPARE_READY] instance="+radio.InstanceId+" backup="+state+" guard_pid="+helperProcess.Id);
                    return lease;
                }
                if(helperProcess.HasExited)throw new IOException("管理员准备程序已退出（"+helperProcess.ExitCode+"），请查看备份目录 driver.log："+state);
                await Task.Delay(200,bounded.Token);
            }
        }
        catch
        {
            // The already elevated guard handles cancellation and restores even
            // if the UI disappears. Signal before returning a prepare error.
            await File.WriteAllTextAsync(Path.Combine(state,"restore.request"),"prepare_aborted",CancellationToken.None);
            throw;
        }
    }
    public void RecordRadio(Process child)
    {
        if(!Prepared)return;
        File.WriteAllText(Path.Combine(StateDirectory,"radio-process.json"),JsonSerializer.Serialize(new
        {pid=child.Id,start_ticks=child.StartTime.ToUniversalTime().Ticks,exe=child.StartInfo.FileName}));
    }
    public async Task RestoreAsync()
    {
        await gate.WaitAsync();
        try
        {
            if(Restored)return;
            await File.WriteAllTextAsync(Path.Combine(StateDirectory,"restore.request"),"owner_stop");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if(await ReadResultAsync(Path.Combine(StateDirectory,"restore-result.json")) is {} result)
                {
                    using(result)
                    {
                        Restored=result.RootElement.GetProperty("success").GetBoolean();
                        if(!Restored)throw new IOException("系统蓝牙恢复未完成，请运行备份目录中的 Restore-Bluetooth.cmd。目录："+StateDirectory);
                    }
                    log("[RADIO_RESTORED] instance="+InstanceId+" original_driver_verified=true state="+StateDirectory);return;
                }
                await Task.Delay(250,timeout.Token);
            }
        }
        catch(OperationCanceledException){throw new IOException("等待系统蓝牙恢复超时；请运行 "+Path.Combine(StateDirectory,"Restore-Bluetooth.cmd"));}
        finally{gate.Release();}
    }
    public async ValueTask DisposeAsync(){await RestoreAsync();guard?.Dispose();}
    public static async Task RestoreLatestAsync(string instance,Action<string> log,CancellationToken token)
    {
        if(!Directory.Exists(Root))throw new IOException("没有本工具创建的当前系统驱动备份。");
        foreach(var state in Directory.GetDirectories(Root).OrderByDescending(p=>p))
        {
            var record=await ReadResultAsync(Path.Combine(state,"baseline.json"));
            if(record==null)continue;
            using(record)
            {
                if(!string.Equals(record.RootElement.GetProperty("adapter").GetProperty("instance").GetString(),instance,StringComparison.OrdinalIgnoreCase))continue;
            }
            if(await ReadResultAsync(Path.Combine(state,"restore-result.json")) is {} old)
            {using(old)if(old.RootElement.TryGetProperty("success",out var success)&&success.GetBoolean())continue;}
            await File.WriteAllTextAsync(Path.Combine(state,"restore.request"),"manual_restore",token);
            var info=PowerShell(Path.Combine(state,"RadioDriver.ps1"),true);Add(info,"-Operation","Restore","-InstanceId",instance,"-StateDirectory",state);
            using var process=Process.Start(info)??throw new IOException("未能启动恢复程序。");
            // A live guard may restore first; the exact helper refuses competing
            // ownership. Read the result, not just the helper process exit code.
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if(await ReadResultAsync(Path.Combine(state,"restore-result.json")) is {} result)
                {using(result){if(!result.RootElement.GetProperty("success").GetBoolean())throw new IOException("恢复失败，请查看 "+state);log("[RADIO_MANUAL_RESTORED] instance="+instance+" backup="+state);return;}}
                await Task.Delay(300,timeout.Token);
            }
        }
        throw new IOException("没有找到这只发射器需要恢复的备份。");
    }
    private static void CreatePrivate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var user=WindowsIdentity.GetCurrent();
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new(user.User!,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }
    private static ProcessStartInfo PowerShell(string file,bool elevate)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"))
        {UseShellExecute=elevate,CreateNoWindow=!elevate,WindowStyle=ProcessWindowStyle.Hidden};
        if(elevate)info.Verb="runas";
        Add(info,"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",file);return info;
    }
    private static void Add(ProcessStartInfo info,params string[] args){foreach(string arg in args)info.ArgumentList.Add(arg);}
    private static async Task<JsonDocument?> ReadResultAsync(string path)
    {
        try{return File.Exists(path)?JsonDocument.Parse(await File.ReadAllTextAsync(path)):null;}
        catch(JsonException){return null;}catch(IOException){return null;}
    }
    private static void WriteRestoreLauncher(string state,string instance,string helper)
    {
        string Quote(string text)=>"'"+text.Replace("'","''")+"'";
        string script="$ErrorActionPreference='Stop'\r\n$state="+Quote(state)+"\r\n"+
            "Set-Content -LiteralPath (Join-Path $state 'restore.request') -Value 'manual_restore'\r\n"+
            "$command=\"& \"+"+Quote(Quote(helper))+"+\" -Operation Restore -InstanceId \"+"+Quote(Quote(instance))+"+\" -StateDirectory \"+"+Quote(Quote(state))+"\r\n"+
            "$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))\r\n"+
            "Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)\r\n";
        File.WriteAllText(Path.Combine(state,"Restore-Bluetooth.ps1"),script,new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(state,"Restore-Bluetooth.cmd"),"@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0Restore-Bluetooth.ps1\"\r\npause\r\n",Encoding.ASCII);
    }
}
