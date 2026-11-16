using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Y700Switch2V60Viiper;
public partial class PhysicalInputsWindow : Window
{
    private readonly MainViewModel vm;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private CancellationTokenSource? readCts;
    private NintendoNfcTag? tag;
    private bool working;
    private int SlotIndex=>SlotChoice.SelectedIndex+1;
    public PhysicalInputsWindow(MainViewModel vm)
    {
        this.vm=vm; InitializeComponent(); RefreshEvidence();
        Loaded+=(_,_)=>timer.Start(); timer.Tick+=(_,_)=>RefreshEvidence();
        Closed+=(_,_)=>{timer.Stop();readCts?.Cancel();};
    }
    private void Slot_Changed(object sender,SelectionChangedEventArgs e) { if(Evidence!=null){tag=null;RefreshEvidence();} }
    private void RefreshEvidence()
    {
        var slot=vm.Pro2Slots[SlotIndex-1];
        Evidence.Text=slot.Name+" · "+slot.RateSummary+"\n\n"+
            (slot.PhysicalInput is {} source ? source.CapabilitySummary+"\n\n"+source.MetricsSummary :
                "Pro2 BLE · NFC 只读实验 · NTAG215\n"+slot.InputSource.NfcStatus+
                "\n读取结果可保存为 540 字节原始文件；当前未完成本机实卡验收。")+
            (tag!=null?"\n\n标签 UID："+Convert.ToHexString(tag.Uid)+"\n已读取 "+tag.Data.Length+" 字节。":"");
        SlotChoice.IsEnabled=!working;
        NfcButton.IsEnabled=!working&&!vm.Busy&&(slot.PhysicalInput is {} physical?physical.CanReadNfc:slot.InputSource.CanReadNfc);
        CancelNfcButton.IsEnabled=readCts!=null;
        SaveNfcButton.IsEnabled=!working&&tag!=null;
        if(working)StatusText.Text=slot.PhysicalInput?.NfcStatus??slot.InputSource.NfcStatus;
    }
    private async void ReadNfc_Click(object sender,RoutedEventArgs e)
    {
        if(working||vm.Busy) return;
        working=true;readCts=new();tag=null;RefreshEvidence();
        try{tag=await vm.ReadPhysicalNfcAsync(SlotIndex,readCts.Token);StatusText.Text="标签读取完成，可保存原始文件。";}
        catch(OperationCanceledException){StatusText.Text="读取已结束，输入模式已尝试恢复。";}
        catch(Exception error){StatusText.Text="读取失败："+error.Message;}
        finally{readCts.Dispose();readCts=null;working=false;RefreshEvidence();}
    }
    private void CancelNfc_Click(object sender,RoutedEventArgs e)=>readCts?.Cancel();
    private void SaveNfc_Click(object sender,RoutedEventArgs e)
    {
        if(tag==null)return;
        var dialog=new Microsoft.Win32.SaveFileDialog{Title="保存 NFC 原始数据",Filter=tag.Signature.Length==32?
            "原始 NTAG215（540 字节）|*.bin|含签名字段（572 字节，未校验）|*.bin":"原始 NTAG215（540 字节）|*.bin",FileName="ntag215-"+Convert.ToHexString(tag.Uid)+".bin"};
        if(dialog.ShowDialog(this)!=true)return;
        try
        {
            byte[] data=tag.Data;
            if(dialog.FilterIndex==2&&tag.Signature.Length==32){data=new byte[572];tag.Data.CopyTo(data,0);tag.Signature.CopyTo(data,540);}
            File.WriteAllBytes(dialog.FileName,data);
            File.WriteAllText(dialog.FileName+".json",JsonSerializer.Serialize(new{uid=Convert.ToHexString(tag.Uid),length=data.Length,
                tag.Controller,tag.Transport,tag.ReadAt,authenticationVerified=false},new JsonSerializerOptions{WriteIndented=true}));
            StatusText.Text="已保存："+dialog.FileName;
        }
        catch(Exception error){StatusText.Text="保存失败："+error.Message;}
    }
}
