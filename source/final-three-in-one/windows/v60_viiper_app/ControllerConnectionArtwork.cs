using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Y700Switch2V60Viiper;

// Decode embedded product photos once. All cards share small frozen bitmaps.
internal static class ControllerConnectionArtwork
{
    private static readonly Lazy<BitmapImage> Pro2 = new(() => Load("Controller-pro2-official.png"));
    private static readonly Lazy<BitmapImage> Pro1 = new(() => Load("Controller-pro1-official.png"));
    internal static ImageSource For(ControllerInputKind kind) =>
        kind is ControllerInputKind.Pro2Ble or ControllerInputKind.Pro2Usb ? Pro2.Value : Pro1.Value;

    private static BitmapImage Load(string name)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/PRO2ControllerAssistant;component/Assets/" + name);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 640;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
