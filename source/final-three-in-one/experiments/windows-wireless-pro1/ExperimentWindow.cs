using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Y700Switch2V60Viiper;

namespace Pro2WirelessExperiment;

internal sealed class ExperimentWindow:Window
{
    private readonly ComboBox devices=new(){DisplayMemberPath="Label",MinWidth=420};
    private readonly ComboBox radios=new(){DisplayMemberPath="Label",MinWidth=420};
    private readonly ComboBox cadence=new(){ItemsSource=new[]{"Pro1 标准 · 66.7Hz（优先验证体感 / NFC）","133Hz · 高速实验，接收端待验证","250Hz · 高速实验，接收端待验证"},SelectedIndex=0,MinWidth=420};
    private readonly TextBlock status=new(){Text="检查本机设备中…",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock metrics=new(){Text="未连接",TextWrapping=TextWrapping.Wrap,FontSize=13};
    private readonly TextBox log=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Consolas"),FontSize=11,Background=new SolidColorBrush(Color.FromRgb(247,249,248))};
    private readonly Button refresh=new(){Content="刷新设备"},connect=new(){Content="连接 USB"},start=new(){Content="开始无线 Pro1"},capture=new(){Content="读取 amiibo"},load=new(){Content="载入 .bin"},stop=new(){Content="停止"};
    private readonly CancellationTokenSource lifetime=new();
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly DispatcherTimer logTimer=new(){Interval=TimeSpan.FromMilliseconds(200)};
    private readonly ConcurrentQueue<string> displayLog=new();
    private ExperimentSession? session;
    private bool busy,closing,closed;
    public ExperimentWindow()
    {
        Title="PRO2 / PRO1 · USB 无线转发实验 0.1";Width=1030;Height=770;MinWidth=850;MinHeight=620;Background=Brushes.White;
        var root=new Grid{Margin=new Thickness(24)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star)})root.RowDefinitions.Add(new(){Height=height});
        void Row(UIElement item,int row){Grid.SetRow(item,row);root.Children.Add(item);}
        Row(new TextBlock{Text="USB → 无线 Pro1",FontSize=27,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(24,65,52)),Margin=new(0,0,0,10)},0);
        Row(new TextBlock{Text="复用现有助手输入与震动。接收端搜索 Pro Controller；NFC 使用只读标签快照。\n仅接管已绑定 WinUSB 的实验蓝牙适配器，本工具不会更换驱动。完整 Pro1 / amiibo 兼容性待实机验收。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray,Margin=new(0,0,0,18)},1);
        var selections=new Grid{Margin=new(0,0,0,14)};selections.ColumnDefinitions.Add(new(){Width=GridLength.Auto});selections.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        selections.RowDefinitions.Add(new(){Height=GridLength.Auto});selections.RowDefinitions.Add(new(){Height=GridLength.Auto});selections.RowDefinitions.Add(new(){Height=GridLength.Auto});
        void Selector(string label,ComboBox box,int row){var text=new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,5,16,5)};Grid.SetRow(text,row);selections.Children.Add(text);box.Margin=new(0,4,0,4);Grid.SetColumn(box,1);Grid.SetRow(box,row);selections.Children.Add(box);}
        Selector("实体 USB 输入",devices,0);Selector("蓝牙发射器",radios,1);Selector("无线报告节奏",cadence,2);Row(selections,2);
        var actions=new WrapPanel{Margin=new(0,0,0,14)};
        foreach(var button in new[]{refresh,connect,start,capture,load,stop}){button.Margin=new(0,0,9,8);button.Padding=new(13,8,13,8);actions.Children.Add(button);}
        var remove=new Button{Content="移除标签快照",Padding=new(13,8,13,8),Margin=new(0,0,9,8)};remove.Click+=(_,_)=>session?.RemoveTag();actions.Children.Add(remove);
        var folder=new Button{Content="打开本轮日志",Padding=new(13,8,13,8),Margin=new(0,0,0,8)};actions.Children.Add(folder);Row(actions,3);
        status.FontSize=15;status.FontWeight=FontWeights.SemiBold;status.Margin=new(0,0,0,10);Row(status,4);metrics.Margin=new(0,0,0,12);Row(metrics,5);Row(log,6);Content=root;
        refresh.Click+=async(_,_)=>await ActAsync(ProbeAsync);
        connect.Click+=async(_,_)=>await ActAsync(ConnectAsync);
        start.Click+=async(_,_)=>await ActAsync(StartAsync);
        capture.Click+=async(_,_)=>await ActAsync(async()=>{await session!.CaptureNfcAsync(lifetime.Token);status.Text="读卡完成 · 540 字节已保存到本轮日志目录";});
        load.Click+=async(_,_)=>await ActAsync(async()=>{var picker=new OpenFileDialog{Filter="NTAG215 文件|*.bin|所有文件|*.*"};if(picker.ShowDialog(this)==true)await session!.LoadTagAsync(picker.FileName,lifetime.Token);});
        stop.Click+=async(_,_)=>await ActAsync(StopAsync);
        folder.Click+=(_,_)=>
        {
            string path=session?.DirectoryPath??Root();Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={path}});
        };
        Loaded+=async(_,_)=>await ActAsync(ProbeAsync);
        timer.Tick+=(_,_)=>{if(session!=null){metrics.Text=session.Summary;if(!busy)status.Text=session.State;}UpdateButtons();};timer.Start();
        logTimer.Tick+=(_,_)=>
        {
            var lines=new System.Text.StringBuilder();for(int i=0;i<300&&displayLog.TryDequeue(out var line);i++)lines.AppendLine(line);
            if(lines.Length==0)return;if(log.Text.Length>200000)log.Text=log.Text[^100000..];log.AppendText(lines.ToString());log.ScrollToEnd();
        };logTimer.Start();
        Closing+=async(_,e)=>
        {
            if(closed)return;e.Cancel=true;if(closing)return;closing=true;lifetime.Cancel();IsEnabled=false;status.Text="正在释放实体输入和实验发射端…";
            try{await StopAsync();}finally{timer.Stop();logTimer.Stop();closed=true;_ = Dispatcher.BeginInvoke(new Action(Close));}
        };
        UpdateButtons();
    }
    private static string Root()=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PRO2WirelessReceiverControlBoard","wireless_relay_logs");
    private void Append(string line)
    {displayLog.Enqueue(line);while(displayLog.Count>1500)displayLog.TryDequeue(out _);}
    private async Task ActAsync(Func<Task> action)
    {
        if(busy)return;busy=true;UpdateButtons();
        try{await action();}catch(OperationCanceledException){status.Text="已取消";}catch(Exception e){status.Text=e.Message;session?.Log("[UI_ACTION_FAILED] "+e);Append(e.ToString());}
        finally{busy=false;UpdateButtons();}
    }
    private async Task ProbeAsync()
    {
        var found=await Task.Run(()=> (Devices:NintendoHidDevice.Enumerate().Where(d=>d.Kind!=NintendoInputKind.Pro1Bluetooth).ToArray(),Radios:WirelessRelayEnvironment.EnumerateRadios()));
        devices.ItemsSource=found.Devices;devices.SelectedIndex=found.Devices.Length>0?0:-1;radios.ItemsSource=found.Radios;radios.SelectedIndex=found.Radios.Count>0?0:-1;
        status.Text=found.Devices.Length==0?"未发现实体 USB 手柄 · 请使用数据线连接 Pro2 或 Pro1":found.Radios.Any(r=>r.Ready)?"设备已发现 · 可连接 USB 后开始转发":"可测试 USB 输入 / Pro2 读卡；发射器尚未处于 WinUSB 实验状态";
        Append("[ENVIRONMENT] windows="+Environment.OSVersion.Version+" usb="+found.Devices.Length+" radios="+found.Radios.Count+" winusb_ready="+found.Radios.Count(r=>r.Ready)+" radio_opened=false");
    }
    private async Task ConnectAsync()
    {
        if(devices.SelectedItem is not NintendoHidDevice selected)throw new InvalidOperationException("请选择实体 USB 手柄。");
        session??=new ExperimentSession(Path.Combine(Root(),DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")),Append);
        status.Text="初始化实体 USB 输入…";await session.ConnectUsbAsync(selected,lifetime.Token);status.Text=session.State;
    }
    private async Task StartAsync()
    {
        if(radios.SelectedItem is not WirelessRelayRadio selected)throw new InvalidOperationException("未发现可选择的实验发射适配器。");
        if(session?.UsbConnected!=true)await ConnectAsync();
        string radioPath=Path.Combine(AppContext.BaseDirectory,"pro2-wireless-pro1-radio.exe");
        string? firmware=Path.Combine(AppContext.BaseDirectory,"firmware");
        double hz=cadence.SelectedIndex switch{1=>133,2=>250,_=>1000.0/15};
        await session!.StartRadioAsync(selected,radioPath,firmware,180,lifetime.Token,hz);status.Text=session.State;
    }
    private async Task StopAsync(){if(session!=null){await session.DisposeAsync();session=null;}status.Text="已停止 · 保留本轮日志，未修改驱动或注册表";}
    private void UpdateButtons()
    {
        refresh.IsEnabled=!busy&&session==null;devices.IsEnabled=!busy&&session==null;radios.IsEnabled=!busy&&session==null;
        cadence.IsEnabled=!busy&&session?.RadioStarted!=true;
        connect.IsEnabled=!busy&&session?.UsbConnected!=true&&devices.SelectedItem!=null;
        start.IsEnabled=!busy&&session?.RadioStarted!=true&&radios.SelectedItem is WirelessRelayRadio{Ready:true};
        capture.IsEnabled=!busy&&session?.CanReadNfc==true;load.IsEnabled=!busy&&session!=null;stop.IsEnabled=!busy&&session!=null;
    }
}
