using System.Windows;

namespace Y700Switch2V60Viiper;
public partial class MultiControllerWindow : Window
{
    private async void RepairSlotOutput_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement {DataContext:Pro2ControllerSlot slot}||DataContext is not MainViewModel vm||!vm.CanOperate||!slot.CanRepairConfiguration)return;
        if(System.Windows.MessageBox.Show(this,"备份后只重建 "+slot.Name+" 的虚拟 USB 手柄。保留实体连接、其他手柄、驱动包和类筛选器。需要管理员授权。",
            "修复所选虚拟手柄",MessageBoxButton.OKCancel,MessageBoxImage.Information,MessageBoxResult.Cancel)==MessageBoxResult.OK)
            await vm.RepairVirtualOutputAsync(slot);
    }
    public MultiControllerWindow(MainViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Height = Math.Max(MinHeight, Math.Min(880, SystemParameters.WorkArea.Height - 40));
        Width = Math.Max(MinWidth, Math.Min(980, SystemParameters.WorkArea.Width - 40));
    }
}
