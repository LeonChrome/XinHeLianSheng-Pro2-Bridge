using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Y700Switch2V60Viiper;
using Button=System.Windows.Controls.Button;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using FontFamily=System.Windows.Media.FontFamily;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using OpenFileDialog=Microsoft.Win32.OpenFileDialog;
using MessageBox=System.Windows.MessageBox;

namespace Pro2WirelessExperiment;

public sealed class ExperimentWindow:Window
{
    private readonly string runtimeDirectory;
    private readonly bool integrated;
    private readonly TaskCompletionSource closeComplete=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ComboBox devices=new(){DisplayMemberPath="Label",MinWidth=420};
    private readonly ComboBox radios=new(){DisplayMemberPath="Label",MinWidth=420};
    private readonly ComboBox cadence=new(){ItemsSource=new[]{"Pro1 标准 · 66.7Hz","133Hz · 接收端频率待验证","250Hz · 接收端频率待验证"},SelectedIndex=1,MinWidth=300};
    private readonly TextBlock status=new(){Text="检查本机设备中…",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock metrics=new(){Text="未连接",TextWrapping=TextWrapping.Wrap,FontSize=13};
    private readonly TextBlock radioHint=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,7,0,0),Foreground=Brushes.DarkGoldenrod};
    private readonly TextBox log=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Consolas"),FontSize=11,Background=new SolidColorBrush(Color.FromRgb(247,249,248))};
    private readonly Button refresh=new(){Content="刷新设备"},connect=new(){Content="连接 USB"},start=new(){Content="准备并开始无线"},restore=new(){Content="恢复系统蓝牙"},capture=new(){Content="读取 amiibo"},load=new(){Content="载入 .bin"},stop=new(){Content="停止"};
    private readonly CancellationTokenSource lifetime=new();
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly DispatcherTimer logTimer=new(){Interval=TimeSpan.FromMilliseconds(200)};
    private readonly ConcurrentQueue<string> displayLog=new();
    private ExperimentSession? session;
    private string? lastRadioChoice;
    private bool busy,closing,closed;
    private TaskCompletionSource? actionComplete;
    public ExperimentWindow(string? runtimeDirectory=null,bool integrated=false)
    {
        this.runtimeDirectory=Path.GetFullPath(runtimeDirectory??AppContext.BaseDirectory);this.integrated=integrated;
        Title=integrated?"PRO2 手柄助手 · 蓝牙转发":"PRO2 / PRO1 · USB 无线转发实验 0.3";Width=1080;Height=790;MinWidth=850;MinHeight=650;Background=Brushes.White;
        FontFamily=new("Segoe UI, Microsoft YaHei UI");FontSize=14;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid{Margin=new Thickness(24)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star)})root.RowDefinitions.Add(new(){Height=height});
        void Row(UIElement item,int row){Grid.SetRow(item,row);root.Children.Add(item);}
        Row(new TextBlock{Text="蓝牙转发 · USB → Pro1",FontSize=27,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(24,65,52)),Margin=new(0,0,0,10)},0);
        Row(new TextBlock{Text="选择 USB 手柄和蓝牙适配器，在接收设备搜索 Pro Controller。只接管选中的适配器。\n"+(integrated?"持续转发，直到手动停止。":"本入口为限时测试。")+"系统蓝牙会先备份，再临时切为发射模式；停止或退出后恢复。NFC 使用只读标签快照。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray,Margin=new(0,0,0,18)},1);
        var selections=new Grid{Margin=new(0,0,0,14)};selections.ColumnDefinitions.Add(new(){Width=GridLength.Auto});selections.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        selections.RowDefinitions.Add(new(){Height=GridLength.Auto});selections.RowDefinitions.Add(new(){Height=GridLength.Auto});selections.RowDefinitions.Add(new(){Height=GridLength.Auto});
        void Selector(string label,ComboBox box,int row){var text=new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,5,16,5)};Grid.SetRow(text,row);selections.Children.Add(text);box.Margin=new(0,4,0,4);Grid.SetColumn(box,1);Grid.SetRow(box,row);selections.Children.Add(box);}
        Selector("实体 USB 输入",devices,0);Selector("蓝牙发射器",radios,1);Selector("无线报告节奏",cadence,2);Row(selections,2);
        var actions=new WrapPanel{Margin=new(0,0,0,14)};
        foreach(var button in new[]{refresh,connect,start,capture,load,stop,restore}){button.Margin=new(0,0,9,8);button.Padding=new(13,8,13,8);actions.Children.Add(button);}
        var remove=new Button{Content="移除标签快照",Padding=new(13,8,13,8),Margin=new(0,0,9,8)};remove.Click+=(_,_)=>session?.RemoveTag();actions.Children.Add(remove);
        var folder=new Button{Content="打开本轮日志",Padding=new(13,8,13,8),Margin=new(0,0,0,8)};actions.Children.Add(folder);Row(actions,3);
        status.FontSize=15;status.FontWeight=FontWeights.SemiBold;status.Margin=new(0,0,0,10);
        var connectionState=new StackPanel();connectionState.Children.Add(status);connectionState.Children.Add(radioHint);
        Row(connectionState,4);metrics.Margin=new(0,8,0,12);Row(metrics,5);Row(log,6);Content=root;
        refresh.Click+=async(_,_)=>await ActAsync(ProbeAsync);
        devices.SelectionChanged+=(_,_)=>UpdateButtons();radios.SelectionChanged+=(_,_)=>
        {if(radios.SelectedItem is WirelessRelayRadio selected)lastRadioChoice=selected.InstanceId;UpdateButtons();};
        connect.Click+=async(_,_)=>await ActAsync(ConnectAsync);
        start.Click+=async(_,_)=>await ActAsync(StartAsync);
        capture.Click+=async(_,_)=>await ActAsync(async()=>{await session!.CaptureNfcAsync(lifetime.Token);status.Text="读卡完成 · 540 字节已保存到本轮日志目录";});
        load.Click+=async(_,_)=>await ActAsync(async()=>{var picker=new OpenFileDialog{Filter="NTAG215 文件|*.bin|所有文件|*.*"};if(picker.ShowDialog(this)==true)await session!.LoadTagAsync(picker.FileName,lifetime.Token);});
        stop.Click+=async(_,_)=>await ActAsync(StopAsync);
        restore.Click+=async(_,_)=>await ActAsync(async()=>
        {
            if(session!=null){await StopAsync();await ProbeAsync();return;}
            if(radios.SelectedItem is not WirelessRelayRadio selected)throw new IOException("先选择要恢复的蓝牙设备。");
            await RadioDriverLease.RestoreLatestAsync(selected.InstanceId,Append,lifetime.Token);await ProbeAsync();status.Text="系统蓝牙已恢复";
        });
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
            if(closed)return;e.Cancel=true;await StopAndCloseAsync();
        };
        UpdateButtons();
    }
    public async Task StopAndCloseAsync()
    {
        if(closed)return;
        if(closing){await closeComplete.Task;return;}
        closing=true;lifetime.Cancel();IsEnabled=false;status.Text="正在停止转发并恢复系统蓝牙…";
        try{if(actionComplete!=null)await actionComplete.Task;await StopAsync();}
        catch(Exception error){MessageBox.Show(this,error.Message,"蓝牙恢复需要处理",MessageBoxButton.OK,MessageBoxImage.Warning);}
        finally{timer.Stop();logTimer.Stop();closed=true;closeComplete.TrySetResult();Close();}
    }
    private static string Root()=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PRO2WirelessReceiverControlBoard","wireless_relay_logs");
    private void Append(string line)
    {displayLog.Enqueue(line);while(displayLog.Count>1500)displayLog.TryDequeue(out _);}
    private async Task ActAsync(Func<Task> action)
    {
        if(busy||closing)return;busy=true;var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);actionComplete=completion;UpdateButtons();
        try{await action();}catch(OperationCanceledException){status.Text="已取消";}catch(Exception e){status.Text=e.Message;session?.Log("[UI_ACTION_FAILED] "+e);Append(e.ToString());}
        finally{busy=false;actionComplete=null;completion.TrySetResult();UpdateButtons();}
    }
    private async Task ProbeAsync()
    {
        var found=await Task.Run(()=> (Devices:NintendoHidDevice.Enumerate().Where(d=>d.Kind!=NintendoInputKind.Pro1Bluetooth).ToArray(),Radios:WirelessRelayEnvironment.EnumerateRadios()));
        string? oldRadio=lastRadioChoice;
        if(session==null){devices.ItemsSource=found.Devices;devices.SelectedIndex=found.Devices.Length>0?0:-1;}
        radios.ItemsSource=found.Radios;radios.SelectedItem=WirelessRelayRadioSelection.Resolve(found.Radios,oldRadio);
        status.Text=found.Devices.Length==0?"未发现实体 USB 手柄 · 请使用数据线连接 Pro2 或 Pro1":found.Radios.Count>1&&radios.SelectedItem==null?"发现多个蓝牙适配器 · 请选择要使用的发射器":"设备已发现 · 选择发射器后开始转发";
        Append("[ENVIRONMENT] windows="+Environment.OSVersion.Version+" usb="+found.Devices.Length+" radios="+found.Radios.Count+" winusb_ready="+found.Radios.Count(r=>r.Ready)+" radio_opened=false");
    }
    private async Task ConnectAsync()
    {
        if(devices.SelectedItem is not NintendoHidDevice selected)throw new InvalidOperationException("请选择实体 USB 手柄。");
        session??=new ExperimentSession(Path.Combine(Root(),DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")),Append,runtimeDirectory,integrated);
        status.Text="初始化实体 USB 输入…";await session.ConnectUsbAsync(selected,lifetime.Token);status.Text=session.State;
    }
    private async Task StartAsync()
    {
        if(radios.SelectedItem is not WirelessRelayRadio selected)throw new InvalidOperationException("未发现可选择的实验发射适配器。");
        if(session?.UsbConnected!=true)await ConnectAsync();
        string radioPath=Path.Combine(runtimeDirectory,"pro2-wireless-pro1-radio.exe");
        string? firmware=Path.Combine(runtimeDirectory,"firmware");
        double hz=cadence.SelectedIndex switch{1=>133,2=>250,_=>1000.0/15};
        try
        {
            status.Text="备份所选发射器并准备无线模式；如有管理员弹窗请允许";
            selected=await session!.PrepareRadioAsync(selected,lifetime.Token,continuous:integrated);
            await session.StartRadioAsync(selected,radioPath,firmware,integrated?0:180,lifetime.Token,hz);status.Text=session.State;
        }
        catch{await StopAsync();throw;}
    }
    private async Task StopAsync(){if(session!=null){await session.DisposeAsync();session=null;}status.Text="已停止 · 本工具临时切换的蓝牙已请求恢复；备份和日志保留";}
    private void UpdateButtons()
    {
        refresh.IsEnabled=!busy&&session?.RadioStarted!=true;devices.IsEnabled=!busy&&session==null;radios.IsEnabled=!busy&&session?.RadioStarted!=true;
        cadence.IsEnabled=!busy&&session?.RadioStarted!=true;
        connect.IsEnabled=!busy&&session?.UsbConnected!=true&&devices.SelectedItem!=null;
        start.IsEnabled=!busy&&session?.RadioStarted!=true&&radios.SelectedItem is WirelessRelayRadio selectedRadio&&(selectedRadio.Ready||selectedRadio.CanPrepare)&&devices.SelectedItem!=null;
        restore.IsEnabled=!busy&&radios.SelectedItem is WirelessRelayRadio;
        capture.IsEnabled=!busy&&session?.CanReadNfc==true;load.IsEnabled=!busy&&session!=null;stop.IsEnabled=!busy&&session!=null;
        radioHint.Text=radios.SelectedItem switch
        {
            WirelessRelayRadio{Ready:true}=>"发射器已准备。USB 连接后可启动无线 Pro1。",
            WirelessRelayRadio{CanPrepare:true} selected=>"将临时使用 "+selected.Name+" 发射；它原有的 Windows 蓝牙功能会暂停，停止后恢复。需要一次管理员授权。",
            WirelessRelayRadio=>"所选蓝牙当前尚无可用外发后端，未修改驱动；可换选已支持的 Realtek / Actions。",
            _=>"请选择一个蓝牙适配器；多个适配器时不会自动替你决定。USB 输入和 Pro2 读卡可单独测试。"
        };
    }
}
