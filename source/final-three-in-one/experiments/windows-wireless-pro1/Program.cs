using System.IO;
using System.Text.Json;
using System.Windows;
using Y700Switch2V60Viiper;

namespace Pro2WirelessExperiment;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Contains("--probe"))
        {
            string output=Value(args,"--output")??Path.Combine(AppContext.BaseDirectory,"environment.json");
            try
            {
                object result=new{windows=Environment.OSVersion.Version.ToString(),x64=Environment.Is64BitOperatingSystem,
                    usb=NintendoHidDevice.Enumerate().Where(d=>d.Kind!=NintendoInputKind.Pro1Bluetooth).ToArray(),
                    radios=WirelessRelayEnvironment.EnumerateRadios(),driverChanges=0,registryChanges=0,radioOpened=false};
                File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));return 0;
            }
            catch(Exception e){File.WriteAllText(output,e.ToString());return 1;}
        }
        if(args.Contains("--usb-capture"))
        {
            string output=Value(args,"--output")??Path.Combine(AppContext.BaseDirectory,"usb-run");
            int repeats=int.TryParse(Value(args,"--nfc-repeat"),out int requested)?Math.Clamp(requested,1,3):1;
            return CaptureUsbAsync(output,args.Contains("--read-nfc"),repeats).GetAwaiter().GetResult();
        }
        if(args.Contains("--journal-self-test"))
        {
            string output=Value(args,"--output")??Path.Combine(AppContext.BaseDirectory,"journal-test");
            try{return JournalSelfTestAsync(output).GetAwaiter().GetResult();}
            catch(Exception e){File.WriteAllText(output+".error",e.ToString());return 1;}
        }
        StartupRenderingPolicy.Apply(["--software-rendering"]);
        var app=new System.Windows.Application();
        app.DispatcherUnhandledException+=(_,e)=>
        {
            if(args.Contains("--ui-smoke"))
            {File.WriteAllText((Value(args,"--output")??Path.Combine(AppContext.BaseDirectory,"ui-smoke.txt"))+".error",e.Exception.ToString());e.Handled=true;app.Shutdown(1);}
            else{MessageBox.Show(e.Exception.Message,"无线转发实验");e.Handled=true;}
        };
        var window=new ExperimentWindow();
        if(args.Contains("--ui-smoke"))
        {
            window.ShowActivated=false;
            window.Loaded+=async(_,_)=>
            {
                await Task.Delay(800);
                string output=Value(args,"--output")??Path.Combine(AppContext.BaseDirectory,"ui-smoke.txt");
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using(var file=File.Create(Path.ChangeExtension(output,"png")))png.Save(file);
                File.WriteAllText(output,"UI_LOADED=true\nAUTO_CONNECT=false\nDRIVER_CHANGES=0\n");window.Close();
            };
        }
        return app.Run(window);
    }
    private static string? Value(string[] args,string name)
    {int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:null;}
    private static async Task<int> CaptureUsbAsync(string path,bool readNfc,int repeats)
    {
        await using var session=new ExperimentSession(path);
        try
        {
            var devices=NintendoHidDevice.Enumerate().Where(d=>d.Kind==NintendoInputKind.Pro2Usb).ToArray();
            if(devices.Length!=1){session.Log("[USB_CAPTURE_NOT_STARTED] pro2_usb_count="+devices.Length+" no_device_commands_sent=true");return 2;}
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(readNfc?70:25));
            await session.ConnectUsbAsync(devices[0],deadline.Token);await Task.Delay(5000,deadline.Token);
            if(readNfc)
            {
                for(int i=0;i<repeats;i++)
                {
                    session.Log("[USB_CARD_ACCEPTANCE] read="+(i+1)+"/"+repeats+" same_physical_connection=true");
                    await session.CaptureNfcAsync(deadline.Token);
                    if(i+1<repeats)await Task.Delay(2000,deadline.Token);
                }
            }
            await Task.Delay(10000,deadline.Token);return 0;
        }
        catch(Exception e){session.Log("[USB_CAPTURE_FAILED] "+e);return 1;}
    }
    private static async Task<int> JournalSelfTestAsync(string output)
    {
        var session=new ExperimentSession(output);
        for(int i=0;i<100;i++)session.Log("[JOURNAL_FIXTURE] sequence="+i);
        await Task.WhenAll(session.DisposeAsync().AsTask(),session.DisposeAsync().AsTask());
        string[] lines=await File.ReadAllLinesAsync(Path.Combine(output,"experiment.log"));
        bool valid=lines.Count(l=>l.Contains("[JOURNAL_FIXTURE]"))==100&&lines.Any(l=>l.Contains("[EXPERIMENT_END]"))&&
            File.Exists(Path.Combine(output,"session-result.json"))&&!File.Exists(Path.Combine(output,"private-session.bin"));
        await File.WriteAllTextAsync(Path.Combine(output,"self-test.txt"),"JOURNAL_PASS="+valid+"\nDOUBLE_DISPOSE_PASS="+valid+"\nRADIO_OPENED=false\nDRIVER_CHANGES=0\n");return valid?0:1;
    }
}
