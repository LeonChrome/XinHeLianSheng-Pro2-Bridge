using System.Windows;

namespace Y700Switch2V60Viiper;
public partial class MultiControllerWindow : Window
{
    public MultiControllerWindow(MainViewModel model) { InitializeComponent(); DataContext = model; }
}
