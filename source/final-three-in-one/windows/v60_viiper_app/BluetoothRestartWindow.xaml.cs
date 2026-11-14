using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Y700Switch2V60Viiper;

public partial class BluetoothRestartWindow : Window
{
    public BluetoothRestartTarget? SelectedTarget { get; private set; }
    public BluetoothRestartWindow(IReadOnlyList<BluetoothRestartTarget> targets,bool reinitialize=false)
    {
        InitializeComponent();
        if(reinitialize)
        {
            Height=540;MinHeight=500;
            Title=Heading.Text="恢复蓝牙连接";
            Description.Text="释放旧 Windows 请求，重新启用所选适配器，再重开助手并按原槽位连接。";
            Consequence.Text="助手会退出并重新打开，游戏输出会中断。所选适配器上的蓝牙耳机、鼠标、键盘也会断开；请先保存游戏。";
            RecoveryHint.Text="需要管理员授权。先备份，再禁用／启用同一适配器；提供启用恢复脚本，不更换驱动、不清除配对。若 Windows 仍要求重启，会明确提示。";
            ConfirmButton.Content="确认恢复并重开助手";
        }
        DataContext = targets;
        if (targets.Count == 1) AdapterPicker.SelectedIndex = 0;
        else SelectedDeviceText.Text = "有多个适配器，请自行选择；系统默认设备不代表 Pro2 的连接归属。";
    }
    private void AdapterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmButton == null || SelectedDeviceText == null) return;
        var target = AdapterPicker.SelectedItem as BluetoothRestartTarget;
        ConfirmButton.IsEnabled = target?.CanRestart == true;
        SelectedDeviceText.Text = target == null ? "请选择适配器。" : target.CanRestart ? target.PnpId : "无法核实此适配器的安全重启目标；不会重启其他设备。";
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterPicker.SelectedItem is not BluetoothRestartTarget target || !target.CanRestart) return;
        SelectedTarget = target;
        DialogResult = true;
    }
}
