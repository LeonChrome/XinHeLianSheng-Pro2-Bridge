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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
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
        CreateTrayIcon();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;
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
        trayMenu.Items.Add("切换 新和联胜 / PS5", null, (_, _) => ExecuteTrayCommand(vm => vm.StartDualSenseCommand));
        trayMenu.Items.Add("切换 PS5 Edge / 背键", null, (_, _) => ExecuteTrayCommand(vm => vm.StartDualSenseEdgeCommand));
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
            Text = "PRO2 控制板 V6.2.32 Test r2",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (IsUiSmokeRun())
        {
            return;
        }

        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.RunStartupAutomationAsync();
        }
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            trayIcon.BalloonTipTitle = "PRO2 控制板仍在运行";
            trayIcon.BalloonTipText = "右键托盘图标可后台切换三模或进入游戏；双击才会打开主界面。";
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


