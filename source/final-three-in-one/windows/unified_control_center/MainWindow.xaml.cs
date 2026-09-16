using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace XinHeLianSheng.ControlCenter;

public partial class MainWindow : Window
{
    private BackendDescriptor _selected = BackendCatalog.Pico;
    private BackendDescriptor? _activeBackend;
    private Process? _activeProcess;
    private CancellationTokenSource? _launchCancellation;

    public MainWindow()
    {
        InitializeComponent();
        SelectBackend(BackendCatalog.Pico);
    }

    private void SelectPico_Click(object sender, RoutedEventArgs e) =>
        SelectBackend(BackendCatalog.Pico);

    private void SelectEsp_Click(object sender, RoutedEventArgs e) =>
        SelectBackend(BackendCatalog.Esp);

    private void SelectViiper_Click(object sender, RoutedEventArgs e) =>
        SelectBackend(BackendCatalog.Viiper);

    private void SelectBackend(BackendDescriptor backend)
    {
        _selected = backend;
        SelectedNameText.Text = backend.Name;
        SelectedVersionText.Text = backend.Version;
        SelectedBadgeText.Text = backend.Badge;
        SelectedDescriptionText.Text = backend.Description;
        SelectedGuidanceText.Text = backend.Guidance;
        CapabilityList.ItemsSource = backend.Capabilities;
        SwitchExperimentButton.Visibility =
            backend == BackendCatalog.Esp ? Visibility.Visible : Visibility.Collapsed;

        (SelectedBadge.Background, SelectedBadgeText.Foreground) = backend switch
        {
            var value when value == BackendCatalog.Pico =>
                (BrushFrom("#174439"), BrushFrom("#6AE9BD")),
            var value when value == BackendCatalog.Esp =>
                (BrushFrom("#49351D"), BrushFrom("#FFC76F")),
            _ => (BrushFrom("#242C59"), BrushFrom("#AEB7FF"))
        };
        SessionLog.Write($"[SELECT] key={backend.Key} version={backend.Version}");
    }

    private async void Launch_Click(object sender, RoutedEventArgs e) =>
        await LaunchBackendAsync(_selected);

    private async void LaunchSwitchExperiment_Click(object sender, RoutedEventArgs e) =>
        await LaunchBackendAsync(BackendCatalog.EspSwitch);

    private async Task LaunchBackendAsync(BackendDescriptor backend)
    {
        if (_activeProcess is { HasExited: false })
        {
            if (_activeBackend?.Key == backend.Key)
            {
                BringToFront(_activeProcess);
                OperationStatusText.Text = backend.Name + " 已经在运行。";
                return;
            }

            MessageBox.Show(
                $"当前正在运行：{_activeBackend?.Name}\n\n请先在原程序中结束操作并关闭窗口，再切换到 {backend.Name}。",
                "后端互斥保护", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _launchCancellation?.Cancel();
        _launchCancellation = new CancellationTokenSource();
        LaunchButton.IsEnabled = false;
        SwitchExperimentButton.IsEnabled = false;
        OperationStatusText.Text = $"正在校验并准备 {backend.Name}，首次启动可能需要一些时间……";
        HeaderStatusText.Text = "正在准备后端";
        HeaderStatusDot.Fill = BrushFrom("#FFB84D");

        try
        {
            string executable = await EmbeddedBackendStore.EnsureExtractedAsync(
                backend, _launchCancellation.Token);
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows 没有返回已启动的进程。");
            process.EnableRaisingEvents = true;
            process.Exited += ActiveProcess_Exited;
            _activeProcess = process;
            _activeBackend = backend;
            HeaderStatusText.Text = backend.Name + " 运行中";
            HeaderStatusDot.Fill = BrushFrom("#55D6A5");
            OperationStatusText.Text =
                $"{backend.Name} 已启动。控制中心会阻止其它后端同时抢占硬件或驱动。";
            FooterProcessText.Text = $"活动后端：{backend.Name} · PID {process.Id}";
            SessionLog.Write($"[LAUNCH] key={backend.Key} pid={process.Id} path={executable}");
        }
        catch (OperationCanceledException)
        {
            OperationStatusText.Text = "后端准备已取消。";
        }
        catch (Exception ex)
        {
            SessionLog.Write($"[LAUNCH_ERROR] key={backend.Key} error={ex}");
            OperationStatusText.Text = "启动失败：" + ex.Message;
            HeaderStatusText.Text = "启动失败";
            HeaderStatusDot.Fill = BrushFrom("#FF657A");
            MessageBox.Show("无法启动该路线：\n\n" + ex.Message,
                "新和联胜", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LaunchButton.IsEnabled = true;
            SwitchExperimentButton.IsEnabled = true;
        }
    }

    private void ActiveProcess_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process) return;
        int exitCode;
        try { exitCode = process.ExitCode; } catch { exitCode = -1; }
        Dispatcher.Invoke(() =>
        {
            SessionLog.Write($"[EXIT] key={_activeBackend?.Key} pid={process.Id} exit={exitCode}");
            OperationStatusText.Text =
                $"{_activeBackend?.Name ?? "后端"} 已退出，exit={exitCode}。现在可以打开其它路线。";
            HeaderStatusText.Text = "等待选择路线";
            HeaderStatusDot.Fill = BrushFrom("#55D6A5");
            FooterProcessText.Text = "无活动后端";
            _activeProcess = null;
            _activeBackend = null;
        });
    }

    private void StopBackend_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcess is not { HasExited: false } process)
        {
            OperationStatusText.Text = "当前没有正在运行的后端。";
            return;
        }
        if (!process.CloseMainWindow())
        {
            MessageBox.Show("当前后端正忙或没有可关闭的主窗口。请回到该窗口完成刷写/连接操作后正常退出。",
                "未强制结束", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OperationStatusText.Text = "已请求后端正常关闭；不会在刷写过程中强制杀进程。";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        string directory = _selected.LogDirectory;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void OpenBackendDirectory_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(EmbeddedBackendStore.RootDirectory);
        Process.Start(new ProcessStartInfo(EmbeddedBackendStore.RootDirectory)
        {
            UseShellExecute = true
        });
    }

    private static SolidColorBrush BrushFrom(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private static void BringToFront(Process process)
    {
        try
        {
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
