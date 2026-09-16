using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XinHeLianSheng.Pico2WFlasher;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer scanTimer;
    private readonly SessionLog sessionLog = new();
    private FirmwareImage? firmware;
    private BootselDevice? selectedBootsel;
    private IReadOnlyList<string> picoPorts = Array.Empty<string>();
    private bool busy;
    private bool hardwareConflict;
    private string lastHardwareSignature = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        scanTimer.Tick += (_, _) => RefreshHardware();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            scanTimer.Stop();
            sessionLog.Dispose();
        };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            firmware = FirmwareImage.LoadEmbedded();
            FirmwareBadge.Text = FirmwareManifest.Version;
            FirmwareDetail.Text =
                $"内置固件 {FirmwareManifest.Version}  |  " +
                $"{firmware.Bytes.Length / 1024.0:F1} KiB  |  " +
                $"SHA256 {firmware.Sha256[..16]}…\n" +
                $"写入范围 0x{firmware.MinimumAddress:X8}–0x{firmware.MaximumAddressExclusive:X8}，" +
                "不会覆盖模式与蓝牙绑定存储区。";
            AddLog($"[FIRMWARE] integrity=pass revision={FirmwareManifest.Revision} " +
                   $"blocks={firmware.MainFlashBlockCount} sha256={firmware.Sha256}");
        }
        catch (Exception ex)
        {
            FirmwareBadge.Text = "内置固件损坏";
            FirmwareDetail.Text = ex.Message;
            SetStatus("内置固件不可用", ex.Message, Colors.IndianRed);
            AddLog("[FIRMWARE] integrity=failed " + ex);
        }

        RefreshHardware(forceLog: true);
        scanTimer.Start();
    }

    private void RefreshHardware(bool forceLog = false)
    {
        if (busy) return;

        BootselScanResult scan = BootselDeviceLocator.Scan();
        picoPorts = PicoSerialDevice.FindConnectedPorts();
        selectedBootsel = scan.Supported.Count == 1 ? scan.Supported[0] : null;
        hardwareConflict = selectedBootsel is not null && picoPorts.Count > 0;
        string signature = string.Join(',', scan.Supported.Select(item => item.RootPath)) + "|" +
                           string.Join(',', scan.Unsupported.Select(item => item.RootPath + item.Reason)) + "|" +
                           string.Join(',', picoPorts);

        if (scan.Supported.Count > 1)
        {
            SetStatus("检测到多个 RP2350 BOOTSEL 设备",
                "请只保留准备刷写的那一块 Pico 2 W，防止刷错设备。", Colors.IndianRed);
        }
        else if (hardwareConflict)
        {
            SetStatus("同时检测到 BOOTSEL 和另一块运行中的 Pico",
                "请只保留准备刷写的 Pico 2 W，避免刷后版本验证到错误设备。", Colors.IndianRed);
        }
        else if (selectedBootsel is not null)
        {
            SetStatus("Pico 2 W 已进入 BOOTSEL",
                $"磁盘 {selectedBootsel.RootPath}  Board-ID: " +
                (string.IsNullOrWhiteSpace(selectedBootsel.BoardId) ? "RP2350" : selectedBootsel.BoardId),
                Color.FromRgb(54, 211, 153));
        }
        else if (scan.Unsupported.Count > 0)
        {
            SetStatus("检测到不兼容的 UF2 开发板",
                scan.Unsupported[0].Reason, Colors.IndianRed);
        }
        else if (picoPorts.Count > 0)
        {
            SetStatus("已连接运行中的 Pico 2 W",
                $"调试串口 {string.Join(", ", picoPorts)}。可让它自动进入 BOOTSEL。",
                Color.FromRgb(89, 170, 255));
        }
        else
        {
            SetStatus("等待 Pico 2 W",
                "按住 BOOTSEL 再连接 Pico 的电脑 USB；看到 RP2350 磁盘后即可刷入。",
                Color.FromRgb(244, 183, 64));
        }

        UpdateButtons();
        if (forceLog || signature != lastHardwareSignature)
        {
            AddLog($"[DETECT] bootsel={scan.Supported.Count} unsupported={scan.Unsupported.Count} " +
                   $"runtime_ports={string.Join(',', picoPorts)}");
            lastHardwareSignature = signature;
        }
    }

    private async void AutoBootselButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || picoPorts.Count != 1) return;
        SetBusy(true, "正在请求 Pico 进入 BOOTSEL…");
        try
        {
            string port = picoPorts[0];
            AddLog($"[BOOTSEL] sending command port={port}");
            await Task.Run(() => PicoSerialDevice.RebootToBootsel(port));
            selectedBootsel = await WaitForBootselAsync(TimeSpan.FromSeconds(12));
            if (selectedBootsel is null)
                throw new TimeoutException(
                    "Pico 没有在 12 秒内出现 RP2350 磁盘。请关闭背挂电源，按住 BOOTSEL 后重新插电脑 USB。");
            AddLog($"[BOOTSEL] ready root={selectedBootsel.RootPath} board_id={selectedBootsel.BoardId}");
            SetStatus("Pico 2 W 已进入 BOOTSEL",
                $"磁盘 {selectedBootsel.RootPath} 已准备好。", Color.FromRgb(54, 211, 153));
            OperationStatus.Text = "BOOTSEL 已就绪，可以开始刷入。";
        }
        catch (Exception ex)
        {
            AddLog("[BOOTSEL] failed " + ex.Message);
            MessageBox.Show(ex.Message, "无法自动进入 BOOTSEL",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, OperationStatus.Text);
            RefreshHardware();
        }
    }

    private async void FlashButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || firmware is null || selectedBootsel is null) return;
        if (MessageBox.Show(
                $"即将刷入内置 {FirmwareManifest.Revision} 候选固件。\n\n" +
                "请确认：\n• Pro2 已从 Pico 断开\n• 背挂电池电源已关闭\n• 仅连接 Pico 的电脑 USB\n\n" +
                "刷写不会主动清除已有模式和蓝牙配对资料。",
                "确认刷入 Pico 2 W",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        BootselDevice target = selectedBootsel;
        SetBusy(true, "正在写入固件，请勿拔线…");
        FlashProgress.Value = 0;
        try
        {
            AddLog($"[FLASH] begin root={target.RootPath} bytes={firmware.Bytes.Length} " +
                   $"sha256={firmware.Sha256}");
            var progress = new Progress<double>(value =>
            {
                FlashProgress.Value = value * 100;
                OperationStatus.Text = $"正在刷入… {value:P0}";
            });
            FlashWriteResult result = await FirmwareFlasher.WriteAsync(
                target, firmware, progress, CancellationToken.None);
            AddLog($"[FLASH] write_complete bytes={result.BytesWritten} " +
                   $"reset_during_flush={result.DeviceRebootedDuringFinalFlush}");
            FlashProgress.Value = 100;

            if (VerifyAfterFlashCheckBox.IsChecked == true)
            {
                OperationStatus.Text = "固件已写入，等待 Pico 完成自检和无线初始化…";
                string identity = await WaitForIdentityAsync(TimeSpan.FromSeconds(30));
                AddLog("[VERIFY] response=" + identity.Replace(Environment.NewLine, " | ").Trim());
                if (!FirmwareIdentity.IsExpectedAndReady(identity, out string reason))
                    throw new InvalidDataException(
                        "UF2 已完整写入，但 30 秒内没有通过完整启动验证：" + reason +
                        "\n请重新插拔电脑 USB 后再检测；不要仅凭磁盘消失判断成功。");

                OperationStatus.Text =
                    $"刷写成功，{FirmwareManifest.Revision} 已完成启动自检和无线初始化。";
                AddLog("[FLASH] result=success revision=" + FirmwareManifest.Revision +
                       " runtime_ready=true");
                MessageBox.Show(
                    "刷写、版本和完整启动验证已经完成。\n\n" +
                    "现在可断开电脑 USB、恢复背挂供电，再连接 Pro2 测试。",
                    "Pico 2 W 刷写成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                OperationStatus.Text = "UF2 写入完成；用户已跳过启动验证。";
                AddLog("[FLASH] result=written_unverified revision=" + FirmwareManifest.Revision);
                MessageBox.Show(
                    "UF2 已完整写入，但没有执行版本和启动状态验证。\n\n" +
                    "建议保持默认勾选，再刷一次完成验证。",
                    "写入完成但未验证",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "刷写未通过完整验收。";
            AddLog("[FLASH] result=failed " + ex);
            MessageBox.Show(ex.Message + "\n\n日志：\n" + sessionLog.Path,
                "刷写未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, OperationStatus.Text);
            RefreshHardware(forceLog: true);
        }
    }

    private async Task<BootselDevice?> WaitForBootselAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            BootselScanResult scan = BootselDeviceLocator.Scan();
            if (scan.Supported.Count == 1) return scan.Supported[0];
            if (scan.Supported.Count > 1)
                throw new InvalidOperationException("出现多个 RP2350 BOOTSEL 设备，已停止操作。");
        }
        return null;
    }

    private static async Task<string> WaitForIdentityAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        string lastResponse = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            foreach (string port in PicoSerialDevice.FindConnectedPorts())
            {
                try
                {
                    string response = await Task.Run(() =>
                        PicoSerialDevice.ReadFirmwareIdentity(port, TimeSpan.FromSeconds(3)));
                    if (!string.IsNullOrWhiteSpace(response)) lastResponse = response;
                    if (FirmwareIdentity.IsExpectedAndReady(response, out _))
                        return response;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(lastResponse)) return lastResponse;
        return lastError is null ? string.Empty : "last_error=" + lastError.Message;
    }

    private void SetStatus(string title, string detail, Color color)
    {
        DeviceStatusTitle.Text = title;
        DeviceStatusDetail.Text = detail;
        StatusDot.Background = new SolidColorBrush(color);
    }

    private void SetBusy(bool value, string message)
    {
        busy = value;
        OperationStatus.Text = message;
        RefreshButton.IsEnabled = !value;
        VerifyAfterFlashCheckBox.IsEnabled = !value;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        AutoBootselButton.IsEnabled = !busy && firmware is not null &&
                                       selectedBootsel is null && picoPorts.Count == 1;
        FlashButton.IsEnabled = !busy && firmware is not null &&
                                selectedBootsel is not null && !hardwareConflict;
        WirelessStatusButton.IsEnabled = !busy && picoPorts.Count == 1 &&
                                         selectedBootsel is null;
        ClearPairingButton.IsEnabled = !busy && picoPorts.Count == 1 &&
                                       selectedBootsel is null;
    }

    private async void WirelessStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || picoPorts.Count != 1) return;
        await RunWirelessCommandAsync("wireless status", "当前无线状态", false);
    }

    private async void ClearPairingButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || picoPorts.Count != 1) return;
        if (MessageBox.Show(
                "这会清除当前无线模式保存的主机密钥，并立即重新开放配对。\n\n" +
                "请同时在电脑或手机的蓝牙设置中删除旧手柄记录，然后重新搜索。",
                "确认清除旧配对", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await RunWirelessCommandAsync("wireless forget", "配对已重置", true);
    }

    private async Task RunWirelessCommandAsync(string command, string title,
                                                bool pairingReset)
    {
        SetBusy(true, pairingReset ? "正在清除旧配对…" : "正在读取无线状态…");
        try
        {
            string port = picoPorts[0];
            string response = await Task.Run(() => PicoSerialDevice.ExecuteCommand(
                port, command, TimeSpan.FromSeconds(3)));
            string[] useful = response.Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("[CDC_CMD]", StringComparison.Ordinal) ||
                               line.Contains("[BT_CLASSIC_STATUS]", StringComparison.Ordinal) ||
                               line.Contains("[BLE_STATUS]", StringComparison.Ordinal) ||
                               line.Contains("[BT_CLASSIC_BOND]", StringComparison.Ordinal) ||
                               line.Contains("[BLE_BOND]", StringComparison.Ordinal))
                .TakeLast(8).ToArray();
            string summary = useful.Length > 0
                ? string.Join(Environment.NewLine, useful)
                : "Pico 串口可用，但没有返回预期的无线状态。请重新检测后再试。";
            AddLog($"[WIRELESS_COMMAND] port={port} command={command} response=" +
                   summary.Replace(Environment.NewLine, " | "));
            OperationStatus.Text = pairingReset
                ? "旧配对已清除；请在主机端删除旧记录后重新搜索手柄。"
                : "无线状态读取完成。";
            MessageBox.Show(summary, title, MessageBoxButton.OK,
                pairingReset ? MessageBoxImage.Information : MessageBoxImage.None);
        }
        catch (Exception ex)
        {
            AddLog($"[WIRELESS_COMMAND] command={command} failed={ex.Message}");
            OperationStatus.Text = "无线操作失败。";
            MessageBox.Show(ex.Message, "无线操作失败", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, OperationStatus.Text);
            RefreshHardware();
        }
    }

    private void AddLog(string message)
    {
        sessionLog.Write(message);
        if (LogTextBox.Text.Length > 96 * 1024) LogTextBox.Clear();
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshHardware(forceLog: true);

    private void OpenLogDirectory_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SessionLog.LogDirectory);
        Process.Start(new ProcessStartInfo(SessionLog.LogDirectory) { UseShellExecute = true });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true;
        MessageBox.Show("固件操作仍在进行，请等待当前步骤结束后再关闭。",
            "暂时不能关闭", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
