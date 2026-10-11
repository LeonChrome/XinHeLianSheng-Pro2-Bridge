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
        _ when source.StartsWith("path_", StringComparison.Ordinal) => "环境变量",
        _ when source.StartsWith("uninstall_", StringComparison.Ordinal) || source.StartsWith("app_paths_", StringComparison.Ordinal) => "安装记录",
        _ => "自动查找"
    };
}
