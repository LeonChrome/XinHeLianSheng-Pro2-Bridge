using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Y700Switch2V60Viiper;

public partial class PhysicalInputsWindow : Window
{
    private readonly MainViewModel vm;
    private readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromSeconds(1)};
    private CancellationTokenSource? readCts;
    private NintendoNfcTag? tag;
    private bool working;
    private int SlotIndex => SlotChoice.SelectedIndex+1;
    public PhysicalInputsWindow(MainViewModel vm)
    {
        this.vm=vm; InitializeComponent(); RefreshEvidence();
        Loaded+=async(_,_)=>{ if(vm.CanProbePhysicalInputs) await RefreshDevicesAsync(); timer.Start(); };
        timer.Tick+=(_,_)=>RefreshEvidence();
        Closed+=(_,_)=>{ timer.Stop(); readCts?.Cancel(); };
    }
    private async Task RefreshDevicesAsync()
    {
        if(working) return;
        if(!vm.CanProbePhysicalInputs) { StatusText.Text="界面预览 · 不枚举、不连接实体硬件"; return; }
        working=true; UpdateButtons();
        try
        {
            var devices=await Task.Run(NintendoHidDevice.Enumerate);
            Devices.ItemsSource=devices;
            if(devices.Count>0) Devices.SelectedIndex=0;
            StatusText.Text=devices.Count==0 ? "未发现实体 HID 手柄。Pro1 请先完成 Windows 蓝牙配对；Pro2 BLE 仍从首页连接。" : "发现 "+devices.Count+" 只实体 HID 手柄。";
        }
        catch(Exception e) { StatusText.Text="枚举失败："+e.Message; }
        finally { working=false; UpdateButtons(); RefreshEvidence(); }
    }
    private async Task Operate(Func<Task> action)
    {
        if(working || vm.Busy) { StatusText.Text="正在处理，请等待当前操作完成。"; return; }
        working=true; UpdateButtons();
        try { await action(); StatusText.Text=vm.Status; }
        catch(Exception e) { StatusText.Text=e.Message; }
        finally { working=false; UpdateButtons(); RefreshEvidence(); }
    }
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshDevicesAsync();
    private async void Connect_Click(object sender,RoutedEventArgs e)
    {
        if(Devices.SelectedItem is NintendoHidDevice device) await Operate(()=>vm.ConnectPhysicalInputAsync(SlotIndex,device));
    }
    private async void Disconnect_Click(object sender,RoutedEventArgs e)=>await Operate(()=>vm.DisconnectPhysicalInputAsync(SlotIndex));
    private async void Ble_Click(object sender,RoutedEventArgs e)=>await Operate(()=>vm.UseBleInputAsync(SlotIndex));
    private void Pairing_Click(object sender,RoutedEventArgs e)
    { try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth"){UseShellExecute=true}); } catch(Exception error) { StatusText.Text=error.Message; } }
    private void Device_Changed(object sender,SelectionChangedEventArgs e) { if(ConnectButton!=null) UpdateButtons(); }
    private void Slot_Changed(object sender,SelectionChangedEventArgs e) { if(Evidence!=null) { tag=null; RefreshEvidence(); } }
    private void UpdateButtons()
    {
        bool idle=!working && !vm.Busy;
        RefreshButton.IsEnabled=idle; Devices.IsEnabled=idle; SlotChoice.IsEnabled=idle;
        ConnectButton.IsEnabled=idle && Devices.SelectedItem is NintendoHidDevice;
        DisconnectButton.IsEnabled=BleButton.IsEnabled=idle;
        NfcButton.IsEnabled=idle && vm.Pro2Slots[SlotIndex-1].PhysicalInput?.CanReadNfc==true;
        CancelNfcButton.IsEnabled=readCts!=null;
        SaveNfcButton.IsEnabled=idle && tag!=null;
    }
    private void RefreshEvidence()
    {
        var slot=vm.Pro2Slots[SlotIndex-1];
        Evidence.Text=slot.Name+" · "+slot.ModeSummary+"\n"+slot.RateSummary+"\n\n"+
            (slot.PhysicalInput is { } physical ? physical.CapabilitySummary+"\n\n"+physical.MetricsSummary : "当前输入：Pro2 BLE\nNFC：当前连接方式尚无已验证的读取通路。")+
            (tag!=null?"\n\n标签 UID："+Convert.ToHexString(tag.Uid)+"\n已读取 "+tag.Data.Length+" 字节。未校验 amiibo 密钥与签名。":"");
        UpdateButtons();
    }
    private async void ReadNfc_Click(object sender,RoutedEventArgs e)
    {
        if(working) return;
        working=true; readCts=new(); tag=null; UpdateButtons();
        try { tag=await vm.ReadPhysicalNfcAsync(SlotIndex,readCts.Token); StatusText.Text="标签读取完成。可保存原始文件。"; }
        catch(OperationCanceledException) { StatusText.Text="读取已结束；正在恢复输入模式。"; }
        catch(Exception error) { StatusText.Text="读取失败："+error.Message; }
        finally { readCts.Dispose(); readCts=null; working=false; RefreshEvidence(); }
    }
    private void CancelNfc_Click(object sender,RoutedEventArgs e)=>readCts?.Cancel();
    private void SaveNfc_Click(object sender,RoutedEventArgs e)
    {
        if(tag==null) return;
        var dialog=new Microsoft.Win32.SaveFileDialog {Title="保存 NFC 原始数据",Filter="原始 NTAG215 文件|*.bin",FileName="ntag215-"+Convert.ToHexString(tag.Uid)+".bin"};
        if(dialog.ShowDialog(this)!=true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName,tag.Data);
            File.WriteAllText(dialog.FileName+".json",JsonSerializer.Serialize(new{uid=Convert.ToHexString(tag.Uid),length=tag.Data.Length,
                tag.Controller,tag.Transport,tag.ReadAt,authenticationVerified=false},new JsonSerializerOptions{WriteIndented=true}));
            StatusText.Text="已保存："+dialog.FileName;
        }
        catch(Exception error) { StatusText.Text="保存失败："+error.Message; }
    }
}
