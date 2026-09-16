using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace XinHeLianSheng.Pico2WFlasher;

public partial class App : Application
{
    private const string MutexName = @"Local\XinHeLianSheng.Pico2WFlasher";
    private Mutex? appMutex;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int verifyIndex = Array.FindIndex(
            e.Args,
            value => string.Equals(value, "--verify-package", StringComparison.OrdinalIgnoreCase));
        if (verifyIndex >= 0)
        {
            string? output = verifyIndex + 1 < e.Args.Length ? e.Args[verifyIndex + 1] : null;
            VerifyPackage(output);
            return;
        }

        appMutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("刷机工具已经在运行。", "新和联胜 Pico 2 W 固件工具",
                MessageBoxButton.OK, MessageBoxImage.Information);
            appMutex.Dispose();
            appMutex = null;
            Shutdown(0);
            return;
        }
        ownsMutex = true;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex)
        {
            try { appMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        appMutex?.Dispose();
        base.OnExit(e);
    }

    private static void VerifyPackage(string? outputPath)
    {
        try
        {
            FirmwareImage image = FirmwareImage.LoadEmbedded();
            string result = string.Join(Environment.NewLine,
                "result=passed",
                "revision=" + FirmwareManifest.Revision,
                "version=" + FirmwareManifest.Version,
                "sha256=" + image.Sha256,
                "blocks=" + image.MainFlashBlockCount,
                "bytes=" + image.Bytes.Length,
                "family=rp2350-arm-s",
                "preserves_settings=true");
            WriteVerification(outputPath, result);
            Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            WriteVerification(outputPath, "result=failed" + Environment.NewLine + ex);
            Current.Shutdown(2);
        }
    }

    private static void WriteVerification(string? outputPath, string text)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return;
        string path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string path = SessionLog.WriteCrash(e.Exception);
        MessageBox.Show(
            "刷机工具发生未处理错误：\n\n" + e.Exception.Message +
            "\n\n诊断日志：\n" + path,
            "新和联胜 Pico 2 W 固件工具",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Current.Shutdown(1);
    }
}
