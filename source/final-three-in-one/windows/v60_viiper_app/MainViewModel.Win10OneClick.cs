using System.Windows.Input;

namespace Y700Switch2V60Viiper;

// v2.5 one-click Win10 optimisation. Reuses the audited elevated path
// (backup -> write 4 BTHPORT values -> verify -> adapter restart -> reconnect -> real FD2 measure);
// it only removes the preset panel step and always targets fixed 7.5ms.
public sealed partial class MainViewModel
{
    private ICommand? oneClickWin10OptimizeCommand;

    public bool Win10StackConfigured => BleCompatibilityInspector.Current.Facts.Win10StackValuesConfigured;

    /// <summary>Win10, parameters not yet set, and the system check allows configuring.</summary>
    public bool ShowWin10OneClick =>
        ShowWin10BleHomeAction && CanConfigureWin10Stack && !Win10StackConfigured && !previewOnly;

    public string Win10OneClickHint => Win10StackConfigured
        ? "Win10 蓝牙已优化为 7.5ms（全局设置，重启后仍有效）。"
        : "Win10 默认蓝牙间隔较长，通常只有十几 Hz；一键优化后可达 133Hz。只需授权一次。";

    public string Win10OneClickConfirmation =>
        "一键优化 Win10 蓝牙（固定 7.5ms）\n\n" +
        "• 会弹出一次管理员授权（UAC）。Win10 没有按连接调整参数的接口，只能修改系统蓝牙默认值。\n" +
        "• 先自动备份原参数，再写入并重启蓝牙适配器，然后自动重连手柄并实测速率。\n" +
        "• 这是全局设置：蓝牙鼠标、键盘等其他 BLE 设备也会使用更短的间隔，耗电略增；过程中它们可能短暂断开。\n" +
        "• 只需做一次，之后启动不会再询问；可随时在“Win10 蓝牙 · 高级”里恢复原参数。\n" +
        (IsWin10BleExperimental ? "• 本机蓝牙系统文件版本尚未在实机验证，属于可恢复的试验。\n" : "") +
        "\n是否继续？";

    public ICommand OneClickWin10OptimizeCommand => oneClickWin10OptimizeCommand ??=
        new RelayCommand(_ => RunExclusiveAsync("一键优化 Win10 蓝牙", async token =>
        {
            selectedWin10BlePreset = Win10BlePresetPolicy.Default;
            OnPropertyChanged(nameof(SelectedWin10BlePreset));
            AppendLog("[BLE_ONE_CLICK] begin preset=" + Win10BlePresetPolicy.Default.Id +
                      " experimental=" + IsWin10BleExperimental + " configured_before=" + Win10StackConfigured);
            try
            {
                // The user confirmed the dialog that states the experimental nature when applicable.
                await RunBleTurboConfiguratorAsync(restore: false, token, experimentalConsent: IsWin10BleExperimental);
            }
            finally
            {
                AppendLog("[BLE_ONE_CLICK] end configured_after=" + Win10StackConfigured);
                RefreshWin10OneClickPresentation();
            }
        }));

    private void RefreshWin10OneClickPresentation()
    {
        OnPropertyChanged(nameof(Win10StackConfigured));
        OnPropertyChanged(nameof(ShowWin10OneClick));
        OnPropertyChanged(nameof(Win10OneClickHint));
        OnPropertyChanged(nameof(Win10OneClickConfirmation));
    }
}
