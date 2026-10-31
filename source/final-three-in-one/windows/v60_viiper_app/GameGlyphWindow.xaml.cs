using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Y700Switch2V60Viiper;

public partial class GameGlyphWindow : Window
{
    private readonly GameGlyphPatchService service = new();
    private GameGlyphPatchSnapshot? snapshot;
    private bool busy, initialized;
    public bool IsOperationBusy => busy;
    private static string LastPathFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PRO2ControllerAssistant", "GameGlyphs", "last-path.txt");

    public GameGlyphWindow()
    {
        InitializeComponent();
        initialized = true;
        Loaded += async (_, _) => await DiscoverAsync();
        Closing += (_, e) => { if (busy) e.Cancel = true; };
    }

    private void SetBusy(bool value, string? text = null)
    {
        busy = value;
        PathControls.IsEnabled = !value;
        InspectButton.IsEnabled = !value;
        ApplyButton.IsEnabled = !value && snapshot?.CanInstall == true;
        ApplyButton.Content = snapshot?.Status == GameGlyphPatchStatus.Installed && snapshot.CanInstall ? "更新 Pro2 图标" : "使用 Pro2 图标";
        RestoreButton.IsEnabled = !value && snapshot?.CanRestore == true;
        CloseButton.IsEnabled = !value;
        if (text != null) { StatusHeading.Text = value ? "正在处理" : "检测结果"; StatusMessage.Text = text; }
    }

    private async Task DiscoverAsync()
    {
        if (busy) return;
        SetBusy(true, "正在查找 Steam 游戏库…");
        try
        {
            var paths = await Task.Run(() => service.DiscoverSteamInstallations());
            InstallationsBox.ItemsSource = paths;
            InstallationsBox.Visibility = paths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            string previous = "";
            try { if (File.Exists(LastPathFile) && new FileInfo(LastPathFile).Length < 32768) previous = File.ReadAllText(LastPathFile); } catch { }
            if (!string.IsNullOrWhiteSpace(GamePathBox.Text)) previous = GamePathBox.Text;
            var selected = paths.FirstOrDefault(p => p.GameRoot.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? paths.FirstOrDefault();
            if (selected != null) { InstallationsBox.SelectedItem = selected; GamePathBox.Text = selected.GameRoot; }
            else if (!string.IsNullOrWhiteSpace(previous)) GamePathBox.Text = previous;
            else { snapshot = null; StatusHeading.Text = "未找到游戏"; StatusMessage.Text = "请选择《影之刃零》的游戏目录，再点击检测。"; }
        }
        catch (Exception ex) { snapshot = null; StatusHeading.Text = "查找未完成"; StatusMessage.Text = ex.Message; }
        finally { SetBusy(false); }
        if (!string.IsNullOrWhiteSpace(GamePathBox.Text)) await InspectAsync();
    }

    private async Task InspectAsync()
    {
        if (busy) return;
        SetBusy(true, "正在检查游戏版本和图标补丁…");
        try
        {
            snapshot = await service.InspectAsync(GamePathBox.Text.Trim());
            RenderSnapshot();
            if (Directory.Exists(snapshot.GameRoot))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LastPathFile)!);
                File.WriteAllText(LastPathFile, snapshot.GameRoot);
            }
        }
        catch (Exception ex) { snapshot = null; StatusHeading.Text = "检测未完成"; StatusMessage.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void RenderSnapshot()
    {
        if (snapshot == null) return;
        StatusHeading.Text = snapshot.Status.ToString() switch
        {
            "Installed" => "已安装 Pro2 图标",
            "NotInstalled" => "尚未安装图标补丁",
            "Partial" => "补丁文件不完整",
            "Conflict" => "检测到文件冲突",
            "UnsupportedVersion" => "当前游戏版本尚未适配",
            "Busy" => "请先退出游戏",
            "MissingGame" => "未找到有效游戏目录",
            _ => "检测结果"
        };
        StatusMessage.Text = snapshot.Message;
    }

    private async Task RunOperationAsync(bool restore)
    {
        if (busy) return;
        string path = GamePathBox.Text.Trim();
        SetBusy(true, restore ? "正在恢复原图标…" : "正在安装独立图标补丁…");
        try
        {
            var result = restore ? await service.RestoreAsync(path) : await service.ApplyAsync(path);
            if (result.RequiresElevation)
            {
                StatusMessage.Text = "此游戏目录需要管理员授权。请在系统弹窗中确认。";
                var elevated = await GameGlyphPatchCommand.LaunchElevatedAsync(path, restore);
                StatusMessage.Text = elevated.Message;
            }
            else StatusMessage.Text = result.Message;
            string operationMessage = StatusMessage.Text;
            snapshot = await service.InspectAsync(path);
            RenderSnapshot();
            StatusMessage.Text = operationMessage + "\n" + snapshot.Message;
        }
        catch (Exception ex) { snapshot = null; StatusHeading.Text = "操作未完成"; StatusMessage.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择《影之刃零》游戏根目录", Multiselect = false };
        if (dialog.ShowDialog(this) == true) { GamePathBox.Text = dialog.FolderName; await InspectAsync(); }
    }
    private void Path_Changed(object sender, TextChangedEventArgs e)
    {
        if (!initialized || busy) return;
        snapshot = null; ApplyButton.IsEnabled = false; RestoreButton.IsEnabled = false;
        StatusHeading.Text = "等待检测"; StatusMessage.Text = "路径已修改，请点击检测。";
    }
    private async void Installation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || busy || InstallationsBox.SelectedItem is not GameGlyphInstallation selected) return;
        GamePathBox.Text = selected.GameRoot; await InspectAsync();
    }
    private async void Discover_Click(object sender, RoutedEventArgs e) => await DiscoverAsync();
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await InspectAsync();
    private async void Apply_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(false);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(true);
    private void Return_Click(object sender, RoutedEventArgs e) { if (!busy) Close(); }
}
