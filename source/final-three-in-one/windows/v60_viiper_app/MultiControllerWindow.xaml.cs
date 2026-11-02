using System.Windows;

namespace Y700Switch2V60Viiper;
public partial class MultiControllerWindow : Window
{
    public MultiControllerWindow(MainViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Height = Math.Max(MinHeight, Math.Min(880, SystemParameters.WorkArea.Height - 40));
        Width = Math.Max(MinWidth, Math.Min(840, SystemParameters.WorkArea.Width - 40));
    }
}
