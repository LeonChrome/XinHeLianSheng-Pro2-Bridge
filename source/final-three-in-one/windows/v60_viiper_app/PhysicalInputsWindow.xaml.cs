using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    private bool working,saving;
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
                "Pro2 无线 · NFC 只读 · NTAG215 / amiibo\n"+slot.InputSource.NfcStatus+
                "\n完整性校验通过后，可保存 540 字节原始文件。")+
            (tag!=null?"\n\n标签 UID："+Convert.ToHexString(tag.Uid)+"\n已读取 "+tag.Data.Length+
                " 字节 · 长度与 UID/BCC 检查通过；未验证真实性。":"");
        SlotChoice.IsEnabled=!working&&!saving;
        NfcButton.IsEnabled=!working&&!saving&&!vm.Busy&&(slot.PhysicalInput is {} physical?physical.CanReadNfc:slot.InputSource.CanReadNfc);
        CancelNfcButton.IsEnabled=readCts!=null;
        SaveNfcButton.IsEnabled=!working&&!saving&&tag!=null;
        if(working)StatusText.Text=slot.PhysicalInput?.NfcStatus??slot.InputSource.NfcStatus;
    }
    private async void ReadNfc_Click(object sender,RoutedEventArgs e)
    {
        if(working||saving||vm.Busy) return;
        working=true;readCts=new();tag=null;RefreshEvidence();
        try{tag=await vm.ReadPhysicalNfcAsync(SlotIndex,readCts.Token);StatusText.Text="标签读取完成，可保存原始文件。";}
        catch(OperationCanceledException){StatusText.Text="读取已结束，输入模式已尝试恢复。";}
        catch(Exception error){StatusText.Text="读取失败："+error.Message;}
        finally{readCts.Dispose();readCts=null;working=false;RefreshEvidence();}
    }
    private void CancelNfc_Click(object sender,RoutedEventArgs e)=>readCts?.Cancel();
    private async void SaveNfc_Click(object sender,RoutedEventArgs e)
    {
        if(tag==null||working||saving)return;
        var current=tag;int slot=SlotIndex;
        var dialog=new Microsoft.Win32.SaveFileDialog{Title="保存 NFC 原始数据",DefaultExt=".bin",AddExtension=true,OverwritePrompt=true,Filter=current.Signature.Length==32?
            "原始文件（540 字节，推荐）|*.bin|附带签名字段（572 字节，未认证）|*.bin":"原始文件（540 字节）|*.bin",FileName="ntag215-"+Convert.ToHexString(current.Uid)+".bin"};
        if(dialog.ShowDialog(this)!=true)return;
        saving=true;RefreshEvidence();StatusText.Text="正在保存并校验文件…";
        try
        {
            bool includeSignature=dialog.FilterIndex==2;
            var result=await Task.Run(()=>NintendoNfcExport.Save(dialog.FileName,current,includeSignature,overwrite:true));
            vm.RecordNfcExport(slot,current.Transport,result);
            StatusText.Text="已保存并校验 "+result.Bytes+" 字节："+dialog.FileName+
                (result.MetadataSaved?"":"\n说明文件未保存；原始文件已保存并校验。");
        }
        catch(Exception error){vm.RecordNfcExportFailure(slot,error);StatusText.Text="保存失败："+error.Message;}
        finally{saving=false;RefreshEvidence();}
    }
}
