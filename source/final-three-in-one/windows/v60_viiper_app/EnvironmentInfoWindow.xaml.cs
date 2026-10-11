using System;
using System.Windows;

namespace Y700Switch2V60Viiper;

public partial class EnvironmentInfoWindow : Window
{
    private readonly MainViewModel model;
    public EnvironmentInfoWindow(MainViewModel model)
    {
        InitializeComponent();
        this.model = model;
        DataContext = model;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await model.RefreshEnvironmentInfoAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(model.EnvironmentReportText); }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存系统与蓝牙信息", Filter = "诊断文本 (*.txt)|*.txt", DefaultExt = ".txt",
            FileName = "PRO2-system-bluetooth-v" + SystemBluetoothDiagnostics.CurrentAppVersion + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await model.ExportEnvironmentInfoAsync(dialog.FileName); }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private void ShowFailure(Exception ex) => System.Windows.MessageBox.Show(this, ex.Message,
        "信息操作未完成", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Return_Click(object sender, RoutedEventArgs e) => Close();
}
