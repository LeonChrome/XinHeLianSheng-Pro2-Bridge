using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Pro2WirelessExperiment;

namespace Y700Switch2V60Viiper;

public partial class MainWindow
{
    private ExperimentWindow? wirelessRelayWindow;
    private async void WirelessRelay_Click(object sender,RoutedEventArgs e)
    {
        if(wirelessRelayWindow!=null){wirelessRelayWindow.Activate();return;}
        if(DataContext is not MainViewModel vm||!vm.CanOperate)return;
        try
        {
            string runtime=await Task.Run(()=>WirelessRelayRuntime.EnsureExtracted());
            if(!await vm.EnterWirelessRelayModeAsync())return;
            try
            {
                wirelessRelayWindow=new ExperimentWindow(runtime,integrated:true){Owner=this};
                foreach(var type in new[]{typeof(System.Windows.Controls.Button),typeof(System.Windows.Controls.ComboBox),typeof(System.Windows.Controls.TextBlock)})
                    if(TryFindResource(type) is Style style)wirelessRelayWindow.Resources[type]=style;
                wirelessRelayWindow.ShowDialog();
            }
            finally{wirelessRelayWindow=null;vm.ExitWirelessRelayMode();}
        }
        catch(Exception error){System.Windows.MessageBox.Show(this,error.Message,"蓝牙转发",MessageBoxButton.OK,MessageBoxImage.Information);}
    }
}
