using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Y700Switch2V60Viiper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var output = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(),"Pro2-Glyph-Smoke");
            Directory.CreateDirectory(output);
            var package=GameGlyphPatchPackage.LoadEmbedded();
            if(package.Manifest.Version!="0.2.0" || package.Manifest.PreviousFileSets.Single().Version!="0.1.0")
                throw new InvalidDataException("Embedded upgrade manifest mismatch");
            // Instantiate and render the actual XAML without showing a desktop window.
            var app=new System.Windows.Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            var window=new GameGlyphWindow();
            if(window.IsVisible)throw new InvalidOperationException("Test must not show a window");
            var pathBox=(TextBox)window.FindName("GamePathBox");
            pathBox.Text=Path.Combine(Path.GetTempPath(),"其他电脑 游戏库","PBZ");
            if(((Button)window.FindName("ApplyButton")).IsEnabled || ((Button)window.FindName("RestoreButton")).IsEnabled)
                throw new InvalidOperationException("Uninspected path must not enable mutations");
            var heading=(TextBlock)window.FindName("StatusHeading");heading.Text="已安装 Pro2 图标";
            var message=(TextBlock)window.FindName("StatusMessage");message.Text="已适配当前游戏版本。原游戏包未改动，可以随时恢复原图标。";
            var root=(FrameworkElement)window.Content;
            foreach(var size in new[]{new Size(744,600),new Size(504,400)})
            {
                root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);
                var background=new DrawingVisual();using(var context=background.RenderOpen())context.DrawRectangle(new SolidColorBrush(Color.FromRgb(245,247,246)),null,new Rect(size));
                bitmap.Render(background);bitmap.Render(root);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(output,$"game-glyphs-{size.Width}.png"));encoder.Save(file);
            }
            var assembly=typeof(GameGlyphWindow).Assembly;
            if(assembly.GetName().Version?.ToString()!="2.4.0.0")throw new InvalidDataException("Release version mismatch");
            // Discovery/inspection only: no install, no game execution, no BLE or virtual gamepad.
            var service=new GameGlyphPatchService();
            var installs=service.DiscoverSteamInstallations();
            foreach(var install in installs)
            {
                var result=service.InspectAsync(install.GameRoot).GetAwaiter().GetResult();
                Console.WriteLine($"DISCOVERY app={install.SteamAppId} compatible={result.VersionMatches} status={result.Status} installed_version={result.InstalledPatchVersion}");
            }
            Console.WriteLine("PASS embedded patch/previous version, XAML constructor, path guard, two offscreen layouts, real Steam read-only discovery; no window shown.");
            window.Close();app.Shutdown();return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
