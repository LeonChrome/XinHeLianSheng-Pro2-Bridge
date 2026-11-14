using System;
using System.Threading;
using System.Windows;

namespace Y700Switch2V60Viiper;

public partial class App : System.Windows.Application
{
    internal static BluetoothRecoveryLaunch? RecoveryLaunch;
    internal static BluetoothRestartResult? RecoveryResult;
    private Mutex? singleInstanceMutex;
    private bool ownsSingleInstanceMutex;

    public App()
    {
        // Before OnStartup, MainWindow.InitializeComponent, or tray creation.
        // A dedicated compatibility EXE enables this without asking a user whose
        // normal window crashes to find an in-app setting.
        StartupRenderingPolicy.Apply(Environment.GetCommandLineArgs());
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if(VirtualDeviceConfigurationRepair.IsCommand(e.Args))
        {
            base.OnStartup(e);
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunVirtualRepairHelperAsync(e.Args);
            return; // No manager mutex, BLE discovery or stale-process cleanup.
        }
        if (GameGlyphPatchCommand.IsCommand(e.Args))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunGameGlyphHelperAsync(e.Args);
            return; // File-only helper must not acquire the assistant mutex or touch BLE.
        }
        if (BluetoothAdapterRestart.IsCommand(e.Args))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunBluetoothRestartHelperAsync(e.Args);
            return; // Must precede the manager mutex and stale-process cleanup.
        }
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

        if(Environment.GetEnvironmentVariable("V60_UI_SMOKE")!="1"&&BluetoothRecoveryLaunch.Parse(e.Args) is { } recovery)
        {
            base.OnStartup(e);ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=CompleteBluetoothRecoveryLaunchAsync(recovery);
            return; // Do not construct a VM or enumerate Bluetooth while the adapter is being cycled.
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
                "PRO2 手柄助手已在运行。请从任务栏或系统托盘打开现有窗口；若正在退出，请稍后再试。",
                "PRO2 手柄助手",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // Acquire the shared mutex before stale-process cleanup. A second click
        // must not terminate the live manager and drop its BLE connection.
        if (!bleDiagnosticCli && !uiSmoke && !Bootstrap.SafeStartup)
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
        Bootstrap.Write("window_constructed");
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
                "UI_RENDERING=" + StartupRenderingPolicy.Telemetry + "\r\n" +
                "STARTUP_SAFE=" + Bootstrap.SafeStartup + "\r\n" +
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

    private async System.Threading.Tasks.Task RunGameGlyphHelperAsync(string[] args)
    {
        int code = await GameGlyphPatchCommand.ExecuteAsync(args);
        Shutdown(code);
        Environment.Exit(code);
    }

    private async System.Threading.Tasks.Task RunBluetoothRestartHelperAsync(string[] args)
    {
        int exitCode;
        try { exitCode = await BluetoothAdapterRestart.ExecuteAsync(args); }
        catch { exitCode = 5; }
        Shutdown(exitCode);
        Environment.Exit(exitCode);
    }

    private async System.Threading.Tasks.Task RunVirtualRepairHelperAsync(string[] args)
    {
        int exit;
        try{exit=await VirtualDeviceConfigurationRepair.ExecuteAsync(args);}
        catch{exit=5;}
        Shutdown(exit);Environment.Exit(exit);
    }

    private async System.Threading.Tasks.Task CompleteBluetoothRecoveryLaunchAsync(BluetoothRecoveryLaunch request)
    {
        var text=new System.Windows.Controls.TextBlock{Text="正在释放旧连接并重新初始化蓝牙…",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24),FontSize=15};
        var waiting=new Window{Title="PRO2 · 恢复蓝牙连接",Width=460,Height=160,Content=text,WindowStartupLocation=WindowStartupLocation.CenterScreen,ResizeMode=ResizeMode.NoResize};
        using var cancelled=new CancellationTokenSource(TimeSpan.FromSeconds(120));
        waiting.Closed+=(_,_)=>cancelled.Cancel();waiting.Show();
        bool completed=false;
        try
        {
            DateTimeOffset requestedAt=DateTimeOffset.UtcNow.AddSeconds(-30);
            BluetoothRestartResult? result=null;
            while(result==null)
            {
                cancelled.Token.ThrowIfCancellationRequested();
                try{result=BluetoothAdapterRestart.ReadResult(request.Operation,request.Target,requestedAt);}catch(System.IO.IOException){}catch(System.Text.Json.JsonException){}
                if(result==null)await System.Threading.Tasks.Task.Delay(400,cancelled.Token);
            }
            if(result.Mode!="reinitialize")throw new InvalidOperationException("恢复操作类型不匹配。");
            singleInstanceMutex=new Mutex(true,@"Global\Y700Switch2V60ViiperManager",out bool created);
            ownsSingleInstanceMutex=created;
            if(!created)
            {
                try{ownsSingleInstanceMutex=singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(5));}
                catch(AbandonedMutexException){ownsSingleInstanceMutex=true;}
            }
            if(!ownsSingleInstanceMutex)throw new InvalidOperationException("另一助手仍在运行，未叠加蓝牙连接。");
            RecoveryLaunch=request;RecoveryResult=result;
            var window=new MainWindow();MainWindow=window;window.Show();
            completed=true;waiting.Close();
        }
        catch(Exception error)
        {
            Bootstrap.Write("ble_recovery_wait_failed operation="+request.Operation+" "+error);
            if(error is not OperationCanceledException||waiting.IsVisible)
                System.Windows.MessageBox.Show("恢复结果未完成核实："+(error is OperationCanceledException?"超过 120 秒未收到可核实的结果。":error.Message)+
                    "\n请检查所选蓝牙是否已启用，再重新打开助手；不要连续重复操作。\n记录："+BluetoothAdapterRestart.ResultPath(request.Operation),"PRO2 蓝牙恢复");
        }
        finally{if(!completed){waiting.Close();Shutdown(1);}}
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
