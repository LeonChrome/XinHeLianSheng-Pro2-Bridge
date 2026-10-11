using System;
using System.Threading;
using System.Windows;

namespace Y700Switch2V60Viiper;

public partial class App : System.Windows.Application
{
    private Mutex? singleInstanceMutex;
    private bool ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (BleTurboConfigurator.IsCommand(e.Args))
        {
            base.OnStartup(e);
            BleTurboCommandResult result = BleTurboConfigurator.Execute(e.Args);
            System.Windows.MessageBox.Show(
                result.Message,
                result.Success ? "PRO2 手柄助手" : "PRO2 手柄助手 · 操作失败",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            int exitCode = result.Success ? 0 : 1;
            Shutdown(exitCode);
            return;
        }

        bool bleDiagnosticCli = Array.Exists(
            e.Args,
            argument => string.Equals(
                argument,
                "--ble-diagnostic",
                StringComparison.OrdinalIgnoreCase));
        bool pureSoftware133 = Array.Exists(
            e.Args,
            argument => string.Equals(
                argument,
                "--pure-software-133",
                StringComparison.OrdinalIgnoreCase));
        if (pureSoftware133)
        {
            PureSoftware133Mode.EnableForCurrentProcess();
        }
        bool uiSmoke = Environment.GetEnvironmentVariable("V60_UI_SMOKE") == "1";

        singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: uiSmoke ? @"Local\PRO2UiSmoke" : @"Global\Y700Switch2V60ViiperManager",
            createdNew: out bool createdNew);
        ownsSingleInstanceMutex = createdNew;
        if (!ownsSingleInstanceMutex)
        {
            try
            {
                ownsSingleInstanceMutex = singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                ownsSingleInstanceMutex = true;
            }
        }

        if (!ownsSingleInstanceMutex)
        {
            if (bleDiagnosticCli)
            {
                Environment.ExitCode = 6;
                Shutdown(6);
                Environment.Exit(6);
                return;
            }

            System.Windows.MessageBox.Show(
                "PRO2 手柄助手已在运行。请双击系统托盘中的助手图标打开；若正在退出，请稍后再试。",
                "PRO2 手柄助手",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // Acquire the shared mutex before stale-process cleanup. A second click
        // must not terminate the live manager and drop its BLE connection.
        if (!bleDiagnosticCli && !uiSmoke)
            StartupProcessGuard.CleanupConflictingProcesses();
        base.OnStartup(e);
        if (bleDiagnosticCli)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunBleDiagnosticCliAsync(e.Args);
            return;
        }

        MainWindow window = new();
        if (uiSmoke) window.ShowActivated = false;
        MainWindow = window;
        window.Show();
        if (uiSmoke) _ = FinishUiSmokeAsync(window);
    }

    private async System.Threading.Tasks.Task FinishUiSmokeAsync(MainWindow window)
    {
        await System.Threading.Tasks.Task.Delay(300);
        string? path = Environment.GetEnvironmentVariable("V60_UI_SMOKE_RESULT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path,
                "UI_LOADED=true\r\n" +
                "VERSION=" + System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    typeof(App).Assembly)?.InformationalVersion + "\r\n" +
                "GYRO_QUALITY=" + Pro2QualityMode.GyroEnabled + "\r\n" +
                "HAPTICS_QUALITY=" + Pro2QualityMode.HapticsEnabled + "\r\n" +
                "BATTERY_INIT_SNAPSHOT=" + Pro2InitialBatterySnapshot.IsRequested(Environment.GetCommandLineArgs()) + "\r\n" +
                "RUNNING=" + ((MainViewModel)window.DataContext).Running + "\r\n" +
                "MODE=" + ((MainViewModel)window.DataContext).SelectedModeLabel + "\r\n");
        }
        window.Close();
    }

    private async System.Threading.Tasks.Task RunBleDiagnosticCliAsync(string[] args)
    {
        int exitCode = await BleDiagnosticCli.RunAsync(args);
        Shutdown(exitCode);
        Environment.Exit(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsSingleInstanceMutex)
        {
            try
            {
                singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
