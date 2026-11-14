using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

public sealed record BluetoothRestartResult(string OperationId, string PnpId, string InterfaceId,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, bool RestartAttempted, int? NativeExitCode,
    bool PresentStarted, bool ParametersUnchanged, bool DriverUnchanged, string Message, string NativeOutput,
    string Mode="restart",int? DisableExitCode=null,bool DisabledConfirmed=false,bool EnableAttempted=false,string RecoveryError="")
{
    public bool Verified => RestartAttempted && NativeExitCode.HasValue &&
        BluetoothRestartPolicy.IsVerifiedSuccess(NativeExitCode.Value, PresentStarted, ParametersUnchanged, DriverUnchanged)&&
        (Mode=="restart"||Mode=="reinitialize"&&DisableExitCode==0&&DisabledConfirmed&&EnableAttempted&&RecoveryError.Length==0);
    public bool RebootRequired => RestartAttempted && (NativeExitCode == 3010||DisableExitCode==3010);
    public string Telemetry => "[BLUETOOTH_RESTART_RESULT] operation=" + OperationId + " pnp_id=\"" + PnpId +
        "\" attempted=" + RestartAttempted + " exit=" + NativeExitCode + " verified=" + Verified +
        " present_started=" + PresentStarted + " parameters_unchanged=" + ParametersUnchanged +
        " driver_unchanged=" + DriverUnchanged + " reboot_required=" + RebootRequired +
        " mode="+Mode+" disable_exit="+DisableExitCode+" disable_confirmed="+DisabledConfirmed+" enable_attempted="+EnableAttempted+" recovery_error="+RecoveryError+
        " driver_replacement=false registry_writes=false pairing_delete=false automatic_reboot=false";
}

public static class BluetoothAdapterRestart
{
    public const string Command = "--bluetooth-adapter-restart";
    public static bool IsCommand(IReadOnlyList<string> args) => args.Contains(Command, StringComparer.OrdinalIgnoreCase);
    public static string ResultPath(string operation)
    {
        if (!Guid.TryParseExact(operation, "N", out _)) throw new ArgumentException("无效的蓝牙重启操作编号。");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PRO2ControllerAssistant", "BluetoothRestart", operation + ".json");
    }
    public static string[] HelperArguments(BluetoothRestartTarget target, string operation)
    {
        _ = BluetoothRestartPolicy.RestartArguments(target); _ = ResultPath(operation);
        return new[] { Command, "--operation-id", operation, "--adapter-pnp", target.PnpId, "--adapter-interface", target.InterfaceId };
    }
    public static string[] ReinitializeArguments(BluetoothRestartTarget target,string operation,string parent)
    {
        if(!BluetoothRecoveryLaunch.IsParentToken(parent))throw new ArgumentException("无效的父进程标识。");
        return HelperArguments(target,operation).Concat(new[]{"--mode","reinitialize","--wait-parent",parent}).ToArray();
    }
    public static BluetoothRestartResult? ReadResult(string operation, BluetoothRestartTarget target, DateTimeOffset requestedAt)
    {
        string path = ResultPath(operation);
        if (!File.Exists(path) || new FileInfo(path).Length > 65536) return null;
        BluetoothRestartResult? result = JsonSerializer.Deserialize<BluetoothRestartResult>(File.ReadAllText(path));
        return result != null && MatchesRequest(result, operation, target, requestedAt, DateTimeOffset.UtcNow) ? result : null;
    }
    public static bool MatchesRequest(BluetoothRestartResult result, string operation, BluetoothRestartTarget target,
        DateTimeOffset requestedAt, DateTimeOffset now) => result.OperationId == operation &&
            result.PnpId.Equals(target.PnpId, StringComparison.OrdinalIgnoreCase) &&
            result.InterfaceId.Equals(target.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
            result.StartedUtc >= requestedAt.AddSeconds(-2) && result.CompletedUtc >= result.StartedUtc &&
            result.CompletedUtc <= now.AddSeconds(5);
    public static async Task<int> ExecuteAsync(string[] args)
    {
        string operation = "", pnp = "", id = "";
        bool reinitialize=args.Length==11&&args[7]=="--mode"&&args[8]=="reinitialize"&&args[9]=="--wait-parent"&&BluetoothRecoveryLaunch.IsParentToken(args[10]);
        if ((args.Length != 7&&!reinitialize) || !IsCommand(args) || args[0] != Command || args[1] != "--operation-id" ||
            args[3] != "--adapter-pnp" || args[5] != "--adapter-interface") return 7;
        operation = args[2]; pnp = args[4]; id = args[6];
        string resultPath;
        try { resultPath = ResultPath(operation); }
        catch { return 7; }
        DateTimeOffset started = DateTimeOffset.UtcNow;
        bool attempted = false, present = false, parameterSame = false, driverSame = false;
        int? exit = null;
        string message = "", output = "";
        BluetoothReinitializationResult? sequence=null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
            if (Environment.OSVersion.Version.Build < 19041) throw new InvalidOperationException("此 Windows 版本不支持设备热重启。");
            EnsureSafeResultLocation(resultPath);
            if (File.Exists(resultPath)) throw new InvalidOperationException("操作编号已使用；未重启设备。");
            // A semaphore can be released after an await on another thread.
            using var gate = new Semaphore(1, 1, @"Global\PRO2BluetoothAdapterRestart");
            bool owned = gate.WaitOne(0);
            if (!owned) throw new InvalidOperationException("其他蓝牙重启操作正在进行。");
            try
            {
                if(reinitialize)await WaitForParentExitAsync(args[10]);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
                BluetoothRestartTarget? target;
                if(reinitialize) target=new("蓝牙适配器",pnp,id,"","",false);
                else
                {
                    SystemBluetoothSnapshot snapshot = await SystemBluetoothDiagnostics.CaptureAsync(timeout.Token);
                    target=BluetoothRestartPolicy.Targets(snapshot).SingleOrDefault(t=>t.PnpId.Equals(pnp,StringComparison.OrdinalIgnoreCase)&&t.InterfaceId.Equals(id,StringComparison.OrdinalIgnoreCase));
                }
                if (target == null || !target.CanRestart || !PresentStarted(pnp))
                    throw new InvalidOperationException("所选适配器已拔出、不可用或身份不匹配；未重启其他设备。");
                string parameters = ParameterSnapshot(), driver = DriverSnapshot(pnp);
                // Verify Bluetooth setup class separately; never accept a child HID, USB hub or Wi-Fi adapter.
                using (RegistryKey? device = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp))
                    if (!Guid.TryParse(device?.GetValue("ClassGUID")?.ToString(), out Guid deviceClass) ||
                        deviceClass != new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974"))
                        throw new InvalidOperationException("目标不是蓝牙适配器设备；未执行重启。");
                string backup = Path.ChangeExtension(resultPath, ".before.json");
                WriteNew(backup, JsonSerializer.Serialize(new { target, parameters, driver, started }));
                if(reinitialize)
                {
                    WriteNew(Path.ChangeExtension(resultPath,".restore.cmd"),RestoreCommand(target));
                    attempted=true;
                    sequence=await BluetoothReinitialization.RunAsync(
                        async()=>{using var down=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);down.CancelAfter(TimeSpan.FromSeconds(20));return await RunAdapterToolAsync(["/disable-device",pnp],line=>output+=line,down.Token);},
                        async()=>{for(int i=0;i<20;i++){if(PresentDisabled(pnp))return true;await Task.Delay(100,timeout.Token);}return false;},
                        async()=>{using var restore=new CancellationTokenSource(TimeSpan.FromSeconds(30));return await RunAdapterToolAsync(["/enable-device",pnp],line=>output+=line,restore.Token);},
                        ()=>Task.Delay(1500,timeout.Token),timeout.Token);
                    exit=sequence.EnableExit;
                }
                else
                {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in BluetoothRestartPolicy.RestartArguments(target)) psi.ArgumentList.Add(arg);
                using Process process = Process.Start(psi) ?? throw new InvalidOperationException("设备重启工具未启动。");
                attempted = true;
                Task<string> stdout = CaptureOutputAsync(process.StandardOutput.BaseStream), stderr = CaptureOutputAsync(process.StandardError.BaseStream);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    throw new TimeoutException("设备重启工具超时，实际结果未核实；不会自动重试或重启 Windows。");
                }
                exit = process.ExitCode;
                output = (await stdout) + "\n" + (await stderr);
                }
                // Localized text may expand sixfold in JSON; keep the result
                // readable under ReadResult's 64 KB limit as well.
                if (output.Length > 6000) output = output[..6000];
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(500, timeout.Token);
                    present = PresentStarted(pnp);
                    if (present) break;
                }
                parameterSame = ParameterSnapshot() == parameters;
                driverSame = DriverSnapshot(pnp) == driver;
                message = reinitialize
                    ? sequence!.SequenceSucceeded&&present&&parameterSame&&driverSame ? "适配器已重新启用；手柄连接和真实频率仍需重新验收。"
                        : sequence.NeedsReboot ? "Windows 仍要求重启；重新初始化未获完整确认，不会自动重启系统。"
                        : "未确认适配器重新初始化成功；已尝试重新启用。"+sequence.Error
                    : BluetoothRestartPolicy.CompletionMessage(exit,exit.HasValue&&BluetoothRestartPolicy.IsVerifiedSuccess(exit.Value,present,parameterSame,driverSame));
            }
            finally { gate.Release(); }
        }
        catch (Exception ex) { message = ex.Message; }
        if(reinitialize&&File.Exists(Path.ChangeExtension(resultPath,".restore.cmd")))
            message+="\n恢复启用脚本（管理员运行）："+Path.ChangeExtension(resultPath,".restore.cmd");
        var result = new BluetoothRestartResult(operation, pnp, id, started, DateTimeOffset.UtcNow,
            attempted, exit, present, parameterSame, driverSame, message, output,reinitialize?"reinitialize":"restart",sequence?.DisableExit,sequence?.DisabledConfirmed??false,sequence?.EnableAttempted??false,sequence?.Error??"");
        try { EnsureSafeResultLocation(resultPath); WriteNew(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); }
        catch { return 5; }
        return result.Verified ? 0 : result.RebootRequired ? 2 : 1;
    }
    private static string ParameterSnapshot()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters");
        return JsonSerializer.Serialize(new[] { "LEConnectionIntervalMin", "LEConnectionIntervalMax", "LEConnectionLatency", "LEConnectionLSTO" }
            .ToDictionary(name => name, name => new { present = key?.GetValueNames().Contains(name) == true,
                value = key?.GetValue(name), kind = key?.GetValueNames().Contains(name) == true ? key.GetValueKind(name).ToString() : "absent" }));
    }
    private static string DriverSnapshot(string pnp)
    {
        using RegistryKey? device = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp);
        string keyName = device?.GetValue("Driver")?.ToString() ?? throw new InvalidOperationException("无法读取目标驱动信息。");
        using RegistryKey? driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + keyName);
        if (driver == null) throw new InvalidOperationException("无法读取目标驱动信息。");
        return JsonSerializer.Serialize(new { keyName, provider = driver.GetValue("ProviderName"), version = driver.GetValue("DriverVersion"),
            date = driver.GetValue("DriverDate"), inf = driver.GetValue("InfPath") });
    }
    public static bool PresentStarted(string pnp) => CM_Locate_DevNodeW(out uint node, pnp, 0) == 0 &&
        CM_Get_DevNode_Status(out uint status, out uint problem, node, 0) == 0 && problem == 0 && (status & 8) != 0;
    private static bool PresentDisabled(string pnp)=>CM_Locate_DevNodeW(out uint node,pnp,0)==0&&
        CM_Get_DevNode_Status(out uint status,out uint problem,node,0)==0&&problem==22&&(status&8)==0;
    public static string RestoreCommand(BluetoothRestartTarget target)
    {
        _=BluetoothRestartPolicy.RestartArguments(target);
        return "@echo off\r\nsetlocal DisableDelayedExpansion\r\nchcp 65001 >nul\r\n\"%SystemRoot%\\System32\\pnputil.exe\" /enable-device \""+
            target.PnpId.Replace("%","%%")+"\"\r\npause\r\n";
    }
    private static async Task WaitForParentExitAsync(string identity)
    {
        var parts=identity.Split(':');int pid=int.Parse(parts[0]);long ticks=long.Parse(parts[1]);
        Process parent;
        try{parent=Process.GetProcessById(pid);}catch(ArgumentException){return;}
        using(parent)
        {
            if(parent.Id==Environment.ProcessId||parent.StartTime.ToUniversalTime().Ticks!=ticks||
                !string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("父进程身份不匹配，未操作蓝牙。");
            using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await parent.WaitForExitAsync(wait.Token);
        }
    }
    private static async Task<int> RunAdapterToolAsync(string[] args,Action<string> log,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"pnputil.exe"))
        {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        using var child=Process.Start(info)??throw new IOException("设备工具未启动。");
        var stdout=CaptureOutputAsync(child.StandardOutput.BaseStream);var stderr=CaptureOutputAsync(child.StandardError.BaseStream);
        try{await child.WaitForExitAsync(token);}catch{try{child.Kill();}catch{}throw;}
        log(string.Join(" ",args)+" exit="+child.ExitCode+"\n"+await stdout+"\n"+await stderr+"\n");
        return child.ExitCode;
    }
    private static void EnsureSafeResultLocation(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        for (string? current = parent; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("日志路径含重解析点，已停止操作。");
        Directory.CreateDirectory(parent!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("日志路径不可用。");
    }
    private static void WriteNew(string path, string text)
    {
        EnsureSafeResultLocation(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }
    private static async Task<string> CaptureOutputAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        byte[] block = new byte[1024];
        int count;
        // Drain beyond the log cap as well, so a full redirected pipe never blocks pnputil.
        while ((count = await stream.ReadAsync(block)) > 0)
            if (buffer.Length < 65536) buffer.Write(block, 0, (int)Math.Min(count, 65536 - buffer.Length));
        return BluetoothRestartPolicy.DecodeNativeOutput(buffer.ToArray());
    }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
}
