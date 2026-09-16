using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XinHeLianSheng.ControlCenter;

public partial class App : Application
{
    private const string MutexName = @"Local\XinHeLianSheng.ControlCenter";
    private Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--render-preview")
        {
            var window = new MainWindow();
            if (window.Content is FrameworkElement root)
            {
                const int width = 1260;
                const int height = 840;
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(
                    width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream output = File.Create(e.Args[1]);
                encoder.Save(output);
            }
            Shutdown(0);
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--verify-package")
        {
            string verifyRoot = Path.Combine(
                Path.GetTempPath(), "XHLSControlCenterVerify",
                Guid.NewGuid().ToString("N"));
            try
            {
                EmbeddedBackendStore.RootOverride = verifyRoot;
                var lines = new List<string>
                {
                    "result=started",
                    "version=1.0.0-integration-test"
                };
                foreach (BackendDescriptor backend in BackendCatalog.All)
                {
                    string path = await EmbeddedBackendStore.EnsureExtractedAsync(backend);
                    string sha256 = await EmbeddedBackendStore.ComputeSha256Async(path);
                    lines.Add($"backend={backend.Key} version={backend.Version} sha256={sha256} verified=true");
                }
                lines[0] = "result=passed";
                File.WriteAllLines(e.Args[1], lines);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[1], "result=failed\nerror=" + ex);
                Shutdown(2);
            }
            finally
            {
                try { Directory.Delete(verifyRoot, recursive: true); } catch { }
            }
            return;
        }

        _singleInstance = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("新和联胜控制中心已经在运行。", "新和联胜",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        SessionLog.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            SessionLog.Write("[CRASH] " + args.Exception);
            MessageBox.Show("控制中心遇到异常，详情已写入日志。\n\n" +
                            args.Exception.Message, "新和联胜",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        new MainWindow().Show();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        SessionLog.Write("[APP] control center exited");
        base.OnExit(e);
    }
}
