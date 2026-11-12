using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private string usbipLocationText = "正在查找 USBIP…";
    public string UsbipLocationText => usbipLocationText;
    public ICommand ResetUsbipPathCommand { get; private set; } = null!;
    public Func<CancellationToken, Task<string?>>? RequestUsbipExecutableAsync { get; set; }
    private bool usbipPickerOffered;

    public async Task InitializeAfterWindowShownAsync()
    {
        if (previewOnly || shuttingDown) return;
        if (Bootstrap.SafeStartup)
        {
            Status = "已使用兼容启动。请手动连接手柄；系统托盘与启动时自动检测已暂停。";
            AppendLog("[SAFE_STARTUP] native_startup_fault_fallback=true auto_connect=false tray=false diagnostics=on_demand");
            return;
        }
        InitializeEnvironmentInfo();
        await RunExclusiveAsync("检查 USBIP", async token =>
        {
            await RefreshRuntimeReadinessAsync(token);
            if (FindConfiguredUsbip() == null)
            {
                await AskForMissingUsbipAsync(token);
                await RefreshRuntimeReadinessAsync(token);
            }
        }, isStartupAutomationOperation: true);
    }

    private async Task<bool> AskForMissingUsbipAsync(CancellationToken token)
    {
        if (RequestUsbipExecutableAsync == null || shuttingDown || usbipPickerOffered) return false;
        usbipPickerOffered = true;
        AppendLog("[USBIP_PATH] missing_executable manual_picker_requested=true installation_state=unknown");
        string? selected = await RequestUsbipExecutableAsync(token);
        if (string.IsNullOrWhiteSpace(selected))
        { AppendLog("[USBIP_PATH] manual_picker=cancelled saved_path_unchanged=true"); return false; }
        try { await SetUsbipPathAsync(selected, token); return FindConfiguredUsbip()?.Source == "saved_manual_path"; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Status = "USBIP 绑定失败：" + FirstLine(ex.Message); AppendLog("[USBIP_PATH] rejected=" + ex.Message); return false; }
    }

    private void InitializeUsbipPathCommands() => ResetUsbipPathCommand = new RelayCommand(_ =>
        RunExclusiveAsync("自动查找 USBIP", token => SetUsbipPathAsync("", token)));

    private UsbipRuntime? FindConfiguredUsbip() => UsbipRuntimeLocator.Find(userSettings.UsbipExecutablePath);

    public Task SelectUsbipPathAsync(string path) => RunExclusiveAsync("选择 USBIP 程序", token =>
        SetUsbipPathAsync(path, token));

    private async Task SetUsbipPathAsync(string path, CancellationToken token)
    {
        if (Running)
        {
            Status = "请先停止虚拟手柄，再更改 USBIP 路径。";
            return;
        }
        string normalized = UsbipRuntimeLocator.NormalizeExecutablePath(path);
        if (path.Length > 0 && (normalized.Length == 0 || !File.Exists(normalized)))
            throw new InvalidOperationException("请选择已经存在的 usbip.exe 文件，不是 USBIP 安装器。路径未保存。");

        if (normalized.Length > 0 && !previewOnly)
        {
            var runtime = new UsbipRuntime(normalized, Path.GetDirectoryName(normalized)!, "saved_manual_path");
            UsbipProbeResult probe = await UsbipRuntimeLocator.ProbeAsync(runtime, token, "--version");
            if (!probe.Ready || !System.Text.RegularExpressions.Regex.IsMatch(probe.Detail, @"\b\d+\.\d+\.\d+(\.\d+)?\b"))
                throw new InvalidOperationException("该文件未通过 USBIP 程序检查，未更改绑定：" + probe.Detail);
            // A missing/not-started kernel driver must not prevent saving the
            // correct nondefault client location. Readiness is checked below.
            AppendLog("[USBIP_PATH] client_verified=true version=" + FirstLine(probe.Detail));
        }

        string previous = userSettings.UsbipExecutablePath;
        try
        {
            userSettings.UsbipExecutablePath = normalized;
            if (!previewOnly) userSettings.Save();
        }
        catch { userSettings.UsbipExecutablePath = previous; throw; }
        AppendLog("[USBIP_PATH] " + (normalized.Length > 0 ? "saved exe=\"" + normalized + "\"" : "automatic_discovery=true") +
            " driver_changed=false ble_settings_changed=false");
        await RefreshRuntimeReadinessAsync(token);
        Status = RuntimeReadinessText;
    }

    private void UpdateUsbipLocation(UsbipRuntime? runtime)
    {
        string configured = userSettings.UsbipExecutablePath;
        string warning = configured.Length > 0 && !File.Exists(configured)
            ? "已保存的路径失效，已尝试自动查找。\n" : "";
        usbipLocationText = runtime == null
            ? warning + "未找到 usbip.exe；若已安装，请手动选择文件。"
            : warning + runtime.ExePath + "\n来源：" + DescribeUsbipSource(runtime.Source);
        OnPropertyChanged(nameof(UsbipLocationText));
    }

    private static string DescribeUsbipSource(string source) => source switch
    {
        "saved_manual_path" => "手动指定",
        "near_application" => "程序附近目录",
        "common_directory" => "常用安装目录",
        "running_client" => "正在运行的 USBIP 客户端",
        "per_user_install" => "当前用户安装目录",
        _ when source.StartsWith("path_", StringComparison.Ordinal) => "环境变量",
        _ when source.StartsWith("uninstall_", StringComparison.Ordinal) || source.StartsWith("app_paths_", StringComparison.Ordinal) => "安装记录",
        _ => "自动查找"
    };
}
