using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Y700Switch2V60Viiper;
public partial class ControllerConnectionView : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ShowHintProperty=DependencyProperty.Register(nameof(ShowHint),typeof(bool),typeof(ControllerConnectionView),new PropertyMetadata(true));
    public bool ShowHint { get=>(bool)GetValue(ShowHintProperty);set=>SetValue(ShowHintProperty,value); }
    public ControllerConnectionView()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>
        {
            if(Window.GetWindow(this)?.DataContext is MainViewModel vm)
                await vm.InitializeControllerChoicesAsync();
        };
    }
    private void Pairing_Click(object sender,RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth"){UseShellExecute=true}); }
        catch(Exception error) { if(Window.GetWindow(this)?.DataContext is MainViewModel vm) vm.Status="无法打开 Windows 蓝牙设置："+error.Message; }
    }
}
