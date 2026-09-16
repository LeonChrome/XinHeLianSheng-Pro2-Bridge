using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace Y700Switch2V55Manager;

public partial class App : System.Windows.Application
{
    private const string ManagerMutexName =
        @"Local\PRO2WirelessReceiverControlBoard.Manager";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private Mutex? managerMutex;
    private bool managerMutexOwned;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog(e.ExceptionObject as Exception ?? new Exception("未知未处理异常。"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int verifyIndex = Array.FindIndex(
            e.Args,
            arg => string.Equals(arg, "--verify-package", StringComparison.OrdinalIgnoreCase));
        if (verifyIndex >= 0)
        {
            string? outputPath = verifyIndex + 1 < e.Args.Length ? e.Args[verifyIndex + 1] : null;
            VerifyPackageAndExit(outputPath);
            return;
        }

        managerMutex = new Mutex(
            initiallyOwned: true,
            ManagerMutexName,
            out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show(
                "新和联胜版本已经在运行。\n\n请回到现有窗口操作，避免两个 Manager 同时占用 CH343 串口。",
                "新和联胜版本",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            managerMutex.Dispose();
            managerMutex = null;
            Shutdown(0);
            return;
        }
        managerMutexOwned = true;

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SerialCommandClient.Shutdown();
        if (managerMutexOwned)
        {
            try
            {
                managerMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }
        managerMutexOwned = false;
        managerMutex?.Dispose();
        managerMutex = null;
        base.OnExit(e);
    }

    private void VerifyPackageAndExit(string? outputPath)
    {
        try
        {
            FirmwarePackage package = EmbeddedAssets.EnsurePackage();
            FirmwareProfile ps5 = package.GetProfile("hid_audio_uac1_4ch_dualsense");
            FirmwareProfile edge = package.GetProfile("hid_audio_uac1_4ch_edge");
            FirmwareProfile recovery = package.GetProfile("hid_only");
            FirmwareProfile pro2 = package.GetProfile("pro2_bridge_v5_5");
            FirmwareProfile xinput = package.GetProfile("xinput_bridge_v5_8");
            FirmwareProfile switch1 = package.GetProfile("switch1_pro_bridge_v5_9_18_test");
            FirmwareProfile dualProbe = package.GetProfile("dual_pro2_probe_v5_9");
            int assetCount = package.Manifest.Profiles.Sum(profile => profile.Assets.Count);
            string result = string.Join(Environment.NewLine, new[]
            {
                "result=passed",
                "package=" + package.Manifest.PackageVersion,
                "firmware=" + package.Manifest.FirmwareVersion,
                "profiles=" + string.Join(",", package.Manifest.Profiles.Select(profile => profile.Id)),
                "ps5_assets=" + ps5.Assets.Count,
                "edge_assets=" + edge.Assets.Count,
                "recovery_assets=" + recovery.Assets.Count,
                "pro2_assets=" + pro2.Assets.Count,
                "xinput_assets=" + xinput.Assets.Count,
                "switch1_assets=" + switch1.Assets.Count,
                "dual_probe_assets=" + dualProbe.Assets.Count,
                "asset_count=" + assetCount,
                "esptool=" + package.EsptoolPath,
                "xinput_probe=" + package.XInputProbePath,
                "dual_ns2pro_host=" + package.DualNs2ProHostPath,
                "dual_ns2pro_host_exists=" + File.Exists(package.DualNs2ProHostPath).ToString().ToLowerInvariant()
            });
            WriteVerificationResult(outputPath, result);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            WriteVerificationResult(outputPath, "result=failed" + Environment.NewLine + ex);
            Shutdown(2);
        }
    }

    private static void WriteVerificationResult(string? outputPath, string result)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, result, Utf8NoBom);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string path = WriteCrashLog(e.Exception);
        MessageBox.Show(
            "PRO2 手柄无线接收器控制板在启动或运行时发生错误。\n\n" + e.Exception.Message + "\n\n诊断日志：\n" + path,
            "PRO2 手柄无线接收器控制板",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private static string WriteCrashLog(Exception exception)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PRO2WirelessReceiverControlBoard",
                "logs");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "crash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            var text = new StringBuilder()
                .AppendLine("PRO2 手柄无线接收器控制板崩溃日志")
                .AppendLine("time=" + DateTime.Now.ToString("O"))
                .AppendLine("version=" + typeof(App).Assembly.GetName().Version)
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();
            File.WriteAllText(path, text, Utf8Bom);
            return path;
        }
        catch
        {
            return "(failed to write crash log)";
        }
    }
}


