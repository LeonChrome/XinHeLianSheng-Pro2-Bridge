using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private string bluetoothRestartStatus = "";
    private string bluetoothRestartDiagnostic = "";
    public string BluetoothRestartStatus => bluetoothRestartStatus;
    public bool HasBluetoothRestartStatus => bluetoothRestartStatus.Length != 0;
    public Task RequestBluetoothRestartAsync(Func<IReadOnlyList<BluetoothRestartTarget>, BluetoothRestartTarget?> confirm) =>
        RunExclusiveAsync("重启蓝牙适配器", async token =>
        {
            try { await RestartBluetoothCoreAsync(confirm, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetBluetoothRestartStatus("蓝牙重启未完成：" + FirstLine(ex.Message));
                bluetoothRestartDiagnostic += Environment.NewLine + "[BLUETOOTH_RESTART_ERROR] type=" + ex.GetType().Name +
                    " hresult=0x" + ex.HResult.ToString("X8");
                AppendLog(bluetoothRestartDiagnostic);
                OnPropertyChanged(nameof(EnvironmentReportText));
            }
        });
    private void SetBluetoothRestartStatus(string text)
    {
        bluetoothRestartStatus = text;
        Status = text;
        OnPropertyChanged(nameof(BluetoothRestartStatus));
        OnPropertyChanged(nameof(HasBluetoothRestartStatus));
        OnPropertyChanged(nameof(EnvironmentReportText));
    }
    private async Task RestartBluetoothCoreAsync(
        Func<IReadOnlyList<BluetoothRestartTarget>, BluetoothRestartTarget?> confirm, CancellationToken token)
    {
        if (previewOnly) { SetBluetoothRestartStatus("界面预览：未执行蓝牙重启。"); return; }
        SetBluetoothRestartStatus("正在读取蓝牙适配器…");
        await RefreshEnvironmentInfoAsync();
        if (environmentSnapshot == null) throw new InvalidOperationException("无法读取蓝牙适配器信息。");
        IReadOnlyList<BluetoothRestartTarget> targets = BluetoothRestartPolicy.Targets(environmentSnapshot);
        if (targets.Count == 0) { SetBluetoothRestartStatus("未找到可用的蓝牙适配器。"); return; }
        BluetoothRestartTarget? selected = confirm(targets);
        if (selected == null) { SetBluetoothRestartStatus("已取消，未重启蓝牙。"); return; }
        _ = BluetoothRestartPolicy.RestartArguments(selected);
        string executable = Environment.ProcessPath ?? "";
        if (!File.Exists(executable)) throw new InvalidOperationException("无法定位当前程序，未请求蓝牙重启。");
        string operation = Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo(executable)
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        foreach (string arg in BluetoothAdapterRestart.HelperArguments(selected, operation)) psi.ArgumentList.Add(arg);
        // Only after the modal confirmation: release OUR game output and GATT owners.
        await StopAsync();
        foreach (Pro2ControllerSlot slot in pro2Slots) await slot.InputSource.StopAsync();
        SetBluetoothRestartStatus("正在申请管理员授权并重启所选蓝牙…");
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        AppendLog("[BLUETOOTH_RESTART_REQUEST] operation=" + operation + " pnp_id=\"" + selected.PnpId +
            "\" interface=\"" + selected.InterfaceId + "\" selected_by_user=true automatic=false");
        bluetoothRestartDiagnostic = "操作：" + operation + " · 适配器：" + selected.Name + " · " + selected.PnpId;
        try
        {
            using Process? child = await Task.Run(() => Process.Start(psi), token);
            if (child == null) throw new InvalidOperationException("管理员重启进程没有启动。");
            // Helper has its own 80s bound. Never kill a privileged device action on UI cancellation.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(100));
            await child.WaitForExitAsync(deadline.Token);
            BluetoothRestartResult? result = BluetoothAdapterRestart.ReadResult(operation, selected, requestedAt);
            if (result == null)
            {
                SetBluetoothRestartStatus("未读取到本次重启结果，不能确认成功。请查看诊断记录。");
                bluetoothRestartDiagnostic = "[BLUETOOTH_RESTART_RESULT] operation=" + operation + " child_exit=" + child.ExitCode + " result_unverified=true";
            }
            else
            {
                bluetoothRestartDiagnostic = result.Telemetry + Environment.NewLine + "结果：" + result.Message + Environment.NewLine +
                    "日志：" + BluetoothAdapterRestart.ResultPath(operation) + Environment.NewLine + result.NativeOutput;
                SetBluetoothRestartStatus(result.Verified && child.ExitCode == 0
                    ? BluetoothRestartPolicy.CompletionMessage(0, true)
                    : !result.RestartAttempted ? "未执行蓝牙重启：" + result.Message
                    : BluetoothRestartPolicy.CompletionMessage(result.NativeExitCode, false));
            }
            AppendLog(bluetoothRestartDiagnostic);
            await RefreshEnvironmentInfoAsync();
            RefreshCompactTelemetry();
            OnPropertyChanged(nameof(EnvironmentReportText));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetBluetoothRestartStatus("已取消管理员授权；未重启蓝牙，助手连接已停止。");
            bluetoothRestartDiagnostic += Environment.NewLine + "[BLUETOOTH_RESTART_RESULT] operation=" + operation + " uac_cancelled=true restart_attempted=false";
            AppendLog(bluetoothRestartDiagnostic);
            OnPropertyChanged(nameof(EnvironmentReportText));
        }
        catch (OperationCanceledException)
        {
            SetBluetoothRestartStatus("重启结果暂未核实，请查看诊断记录；程序不会重复重启蓝牙。");
            throw;
        }
    }
}
