using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Y700Switch2V60Viiper;

public partial class MainWindow : Window
{
    private Forms.NotifyIcon? trayIcon;
    private Forms.ContextMenuStrip? trayMenu;
    private Forms.ToolStripMenuItem? launchAtLoginMenuItem;
    private Forms.ToolStripMenuItem? autoReconnectOnStartupMenuItem;
    private bool shutdownInProgress;
    private bool shutdownComplete;
    private bool trayTipShown;
    private async void SelectUsbipPath_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanManageRuntime) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择已安装的 usbip.exe（不是安装器）",
            Filter = "USBIP 程序 (usbip.exe)|usbip.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await vm.SelectUsbipPathAsync(dialog.FileName);
    }
    private ManualConnectWindow? manualConnectWindow;
    private EnvironmentInfoWindow? environmentInfoWindow;
    private readonly DispatcherTimer presentationTimer = new(DispatcherPriority.Background)
    { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(IsUiSmokeRun());
        presentationTimer.Tick += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.RefreshCompactTelemetry();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && WindowState != WindowState.Minimized) presentationTimer.Start();
            else presentationTimer.Stop();
        };
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged += MainViewModel_PropertyChanged;
            viewModel.UserNotificationRequested += MainViewModel_UserNotificationRequested;
        }
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        if (!IsUiSmokeRun()) CreateTrayIcon();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int enabled = 0;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // Dark title bars are cosmetic; unsupported Windows builds keep the system default.
        }
    }

    private void CreateTrayIcon()
    {
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("切换 PS5 模式", null, (_, _) => ExecuteTrayCommand(vm => vm.StartDualSenseCommand));
        trayMenu.Items.Add("切换 Pro1 / Switch Pro", null, (_, _) => ExecuteTrayCommand(vm => vm.StartPro1Command));
        trayMenu.Items.Add("切换 Pro2 / Nintendo", null, (_, _) => ExecuteTrayCommand(vm => vm.StartPro2Command));
        trayMenu.Items.Add("切换 Xbox / XInput", null, (_, _) => ExecuteTrayCommand(vm => vm.StartXboxCommand));
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("连接 PRO2 · 进入游戏", null, (_, _) => ExecuteTrayCommand(vm => vm.ConnectPro2InputCommand));
        trayMenu.Items.Add("停止虚拟设备", null, (_, _) => ExecuteTrayCommand(vm => vm.StopCommand));
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        launchAtLoginMenuItem = new Forms.ToolStripMenuItem("开机自启动") { CheckOnClick = true };
        launchAtLoginMenuItem.Click += (_, _) => ToggleTraySetting(
            vm => vm.LaunchAtLoginEnabled = launchAtLoginMenuItem.Checked);
        trayMenu.Items.Add(launchAtLoginMenuItem);
        autoReconnectOnStartupMenuItem = new Forms.ToolStripMenuItem("启动后自动进入上次模式") { CheckOnClick = true };
        autoReconnectOnStartupMenuItem.Click += (_, _) => ToggleTraySetting(
            vm => vm.AutoReconnectOnStartupEnabled = autoReconnectOnStartupMenuItem.Checked);
        trayMenu.Items.Add(autoReconnectOnStartupMenuItem);
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(new Action(RequestExitFromTray)));
        UpdateTrayToggleChecks();

        trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "PRO2 手柄助手 · 纯软件版",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized) presentationTimer.Start();
        if (!IsUiSmokeRun()) FitStartupWindow();
        if (DataContext is MainViewModel initial) initial.RefreshCompactTelemetry();
        if (IsUiSmokeRun())
        {
            return;
        }

        if (DataContext is MainViewModel viewModel)
        {
            if (Array.Exists(Environment.GetCommandLineArgs(), argument =>
                string.Equals(argument, "--connect-pro2", StringComparison.OrdinalIgnoreCase)))
            {
                await viewModel.ConnectForLaunchAsync();
                if (Environment.GetCommandLineArgs().Contains("--ble-diagnostic-after-connect", StringComparer.OrdinalIgnoreCase))
                    await viewModel.DiagnoseForLaunchAsync();
            }
            else
                await viewModel.RunStartupAutomationAsync();
#if PRO2_BATTERY_READ_EXPERIMENT
            if (Environment.GetCommandLineArgs().Contains("--battery-followup-test", StringComparer.OrdinalIgnoreCase))
            {
                Title = "PRO2 手柄助手 v1.3.8 · 电量持续验证";
                _ = viewModel.TestBatteryFollowupForLaunchAsync();
            }
            if (Environment.GetCommandLineArgs().Contains("--battery-live-read-once-test", StringComparer.OrdinalIgnoreCase))
            {
                Title = "PRO2 手柄助手 v1.3.8 · 电量只读实验";
                _ = viewModel.TestLiveBatteryReadForLaunchAsync();
            }
#endif
        }
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Win10BleHomePanelExpanded))
        {
            // The panel's Auto row and compact hero change in one binding update.
            // Re-measure after both settle, so Auto does not retain an empty scrollbar.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => HomeScrollViewer.InvalidateMeasure()));
        }
        if (e.PropertyName == nameof(MainViewModel.LaunchAtLoginEnabled) ||
            e.PropertyName == nameof(MainViewModel.AutoReconnectOnStartupEnabled))
        {
            _ = Dispatcher.BeginInvoke(new Action(UpdateTrayToggleChecks));
        }
    }

    private void MainViewModel_UserNotificationRequested(string title, string message)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (trayIcon != null)
            {
                trayIcon.BalloonTipTitle = title;
                trayIcon.BalloonTipText = message;
                trayIcon.ShowBalloonTip(5000);
            }
            else
            {
                System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }));
    }

    private void RefreshSteamControllerCache_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            "刷新会先正常拔出当前虚拟手柄，然后请求 Steam 正常退出并重新打开。运行中的 Steam 游戏可能阻止退出，请先保存游戏进度。程序不会强制结束 Steam。是否继续？",
            "刷新 Steam 控制器缓存",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.RefreshSteamControllerCacheCommand.CanExecute(null))
        {
            viewModel.RefreshSteamControllerCacheCommand.Execute(null);
        }
    }

    private async void TryWin11Compatibility_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanTryWin11Compatibility) return;
        MessageBoxResult answer = System.Windows.MessageBox.Show(this,
            "在当前连接上依次测试有限组蓝牙参数，首页会显示方案进度和真实 FD2 速率。按实测保留较好结果，未达 133Hz 仍可使用可用输入。部分参数对象属于实验兼容路线，驱动可能拒绝或断联；请求成功不等于真实提速。本轮不改注册表、驱动或配对，不重建虚拟手柄。是否开始？",
            "自动优化蓝牙", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) await vm.TryWin11CompatibilityAsync();
    }

    private void CopyLowRateDiagnosis_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try { System.Windows.Clipboard.SetText(vm.EnvironmentReportText); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "复制未完成", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void EnvironmentInfo_Click(object sender, RoutedEventArgs e)
    {
        if (environmentInfoWindow != null) { environmentInfoWindow.Activate(); return; }
        if (DataContext is not MainViewModel vm) return;
        var dialog = new EnvironmentInfoWindow(vm) { Owner = this, Width = Math.Min(800, ActualWidth),
            Height = Math.Min(640, ActualHeight), MinWidth = Math.Min(440, ActualWidth),
            MinHeight = Math.Min(340, ActualHeight) };
        environmentInfoWindow = dialog;
        dialog.Closed += (_, _) => environmentInfoWindow = null;
        dialog.Show();
    }

    private async void ManualConnect_Click(object sender, RoutedEventArgs e)
    {
        if (manualConnectWindow != null) { manualConnectWindow.Activate(); return; }
        if (DataContext is not MainViewModel vm || vm.Busy) return;
        bool success = await vm.RunManualConnectionSessionAsync(async token =>
        {
            using var model = new ManualConnectionViewModel(vm.ScanManualCandidatesAsync,
                vm.ConnectManualCandidateAsync, token);
            var dialog = new ManualConnectWindow(model) { Owner = this, Width = Math.Min(800, ActualWidth),
                Height = Math.Min(600, ActualHeight), MinWidth = Math.Min(480, ActualWidth),
                MinHeight = Math.Min(360, ActualHeight) };
            manualConnectWindow = dialog;
            try
            {
                dialog.ShowDialog();
                await model.StopAsync();
                return dialog.Connected;
            }
            finally { manualConnectWindow = null; }
        });
        if (success) { MainTabs.SelectedIndex = 0; Activate(); }
    }

    private void FitStartupWindow()
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        Rect bounds = StartupWindowLayout.Fit(new Rect(area.Left / dpi.DpiScaleX,
            area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY));
        MinWidth = Math.Min(720, bounds.Width); MinHeight = Math.Min(560, bounds.Height);
        MaxWidth = area.Width / dpi.DpiScaleX; MaxHeight = area.Height / dpi.DpiScaleY;
        Width = bounds.Width; Height = bounds.Height;
        Left = bounds.Left; Top = bounds.Top;
    }

    private void ToggleWin10BleHomePanel_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.ToggleWin10BleHomePanel();
    }

    private void ConfigureBleTurbo_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        bool experimentalConsent = viewModel.IsWin10BleExperimental;
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            viewModel.Win10BleConfigurationConfirmation,
            "应用并测试 Win10 BLE 预设",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        ExecuteConfirmedCommand(answer, vm => vm.ConfigureBleTurboCommand, experimentalConsent);
    }

    private void RestoreBleTurbo_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            "将恢复首次备份中的四项 BLE 参数。其他蓝牙设备可能暂时断开；无法安全重启蓝牙时，会提示正常重启 Windows。继续？",
            "恢复蓝牙参数",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        ExecuteConfirmedCommand(answer, vm => vm.RestoreBleTurboCommand);
    }

    private void ExecuteConfirmedCommand(
        MessageBoxResult answer,
        Func<MainViewModel, System.Windows.Input.ICommand> selector,
        object? commandParameter = null)
    {
        if (answer != MessageBoxResult.Yes || DataContext is not MainViewModel viewModel)
        {
            return;
        }
        System.Windows.Input.ICommand command = selector(viewModel);
        if (command.CanExecute(commandParameter))
        {
            command.Execute(commandParameter);
        }
    }

    private void ToggleTraySetting(Action<MainViewModel> action)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is MainViewModel viewModel)
            {
                action(viewModel);
                UpdateTrayToggleChecks();
            }
        }));
    }

    private void UpdateTrayToggleChecks()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (launchAtLoginMenuItem != null)
        {
            launchAtLoginMenuItem.Checked = viewModel.LaunchAtLoginEnabled;
        }
        if (autoReconnectOnStartupMenuItem != null)
        {
            autoReconnectOnStartupMenuItem.Checked = viewModel.AutoReconnectOnStartupEnabled;
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null)
                {
                    return icon;
                }
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        if (IsUiSmokeRun())
        {
            return;
        }

        Hide();
        if (!trayTipShown && trayIcon != null)
        {
            trayTipShown = true;
            trayIcon.BalloonTipTitle = "PRO2 手柄助手仍在运行";
            trayIcon.BalloonTipText = "右键托盘图标可后台切换手柄模式或进入游戏；双击可打开主界面。";
            trayIcon.ShowBalloonTip(2500);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ExecuteTrayCommand(Func<MainViewModel, System.Windows.Input.ICommand> commandSelector)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            System.Windows.Input.ICommand command = commandSelector(viewModel);
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        }));
    }

    private void RequestExitFromTray()
    {
        Close();
    }

    private static bool IsUiSmokeRun()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("V60_UI_SMOKE"),
            "1",
            StringComparison.Ordinal);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (shutdownInProgress)
        {
            return;
        }

        shutdownInProgress = true;
        try
        {
            if (DataContext is MainViewModel viewModel)
            {
                await viewModel.ShutdownAsync();
            }
        }
        finally
        {
            shutdownComplete = true;
            shutdownInProgress = false;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Close));
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        presentationTimer.Stop();
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= MainViewModel_PropertyChanged;
            viewModel.UserNotificationRequested -= MainViewModel_UserNotificationRequested;
        }
        trayIcon?.Dispose();
        trayMenu?.Dispose();
        trayIcon = null;
        trayMenu = null;
        System.Windows.Application.Current.Shutdown(0);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);
}


