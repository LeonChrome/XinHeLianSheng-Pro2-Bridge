using System;
using System.IO;
using System.Windows;

namespace Y700Switch2V60Viiper;

/// <summary>Managed startup journal starts before constructing the WPF application.
/// Native process faults are recorded by the independent distribution launcher.</summary>
public static class Bootstrap
{
    public static bool SafeStartup => Environment.GetEnvironmentVariable("PRO2_STARTUP_SAFE") == "1";
    private static string? logPath;
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Write("managed_entry version=" + SystemBluetoothDiagnostics.CurrentAppVersion);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("unhandled " + e.ExceptionObject);
            var app = new App();
            app.DispatcherUnhandledException += (_, e) =>
            {
                Write("dispatcher_exception " + e.Exception);
                e.Handled = true;
                System.Windows.MessageBox.Show("程序启动或界面出现错误。\n" + e.Exception.Message + "\n\n记录：" + logPath,
                    "PRO2 手柄助手", MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown(1);
            };
            // App.xaml has no StartupUri or application-level resources; WPF's
            // generated entry point also constructs App and calls Run directly.
            Write("wpf_initialized");
            return app.Run();
        }
        catch (Exception ex)
        {
            Write("startup_exception " + ex);
            System.Windows.MessageBox.Show("程序未能启动。\n" + ex.Message + "\n\n记录：" + logPath,
                "PRO2 手柄助手", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    public static void Write(string text)
    {
        try
        {
            if (logPath == null)
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PRO2WirelessReceiverControlBoard", "v6_logs");
                if (Environment.GetEnvironmentVariable("V60_UI_SMOKE") == "1") root = Path.Combine(Path.GetTempPath(), "PRO2-ui-smoke-logs");
                Directory.CreateDirectory(root);
                logPath = Path.Combine(root, "startup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Environment.ProcessId + ".log");
            }
            File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
        }
        catch { /* A read-only log directory must not prevent the window. */ }
    }
}
