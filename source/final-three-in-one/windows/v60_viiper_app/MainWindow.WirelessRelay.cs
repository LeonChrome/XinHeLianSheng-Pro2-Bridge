using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Y700Switch2V60Viiper;

public partial class MainWindow
{
    private async void LoadWirelessTag_Click(object sender,RoutedEventArgs e)
    {
        if(DataContext is not MainViewModel vm||!vm.CanUseWirelessNfc)return;
        var picker=new Microsoft.Win32.OpenFileDialog{Title="载入只读 NTAG215 快照",Filter="标签文件 (*.bin)|*.bin|所有文件 (*.*)|*.*",CheckFileExists=true};
        if(picker.ShowDialog(this)==true)await vm.LoadWirelessTagAsync(picker.FileName);
    }
    private void OpenWirelessLog_Click(object sender,RoutedEventArgs e)
    {
        if(DataContext is MainViewModel vm&&Directory.Exists(vm.WirelessLogDirectory))
            Process.Start(new ProcessStartInfo(vm.WirelessLogDirectory){UseShellExecute=true});
    }
}
