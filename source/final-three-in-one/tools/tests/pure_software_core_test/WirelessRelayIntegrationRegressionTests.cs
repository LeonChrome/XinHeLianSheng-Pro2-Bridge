using System.IO;
using System.Security.Cryptography;
using Y700Switch2V60Viiper;

internal static class WirelessRelayIntegrationRegressionTests
{
    public static async Task RunAsync(Action<bool,string> check,string output)
    {
        check(WirelessRelayRuntime.IsAvailable,"single EXE includes the optional relay runtime");
        string root=Path.Combine(output,"embedded-relay-"+Guid.NewGuid().ToString("N"));
        string extracted=WirelessRelayRuntime.EnsureExtracted(root);
        foreach(string required in new[]{"pro2-wireless-pro1-radio.exe","RadioDriver.ps1","BTstack-LICENSE.txt","firmware/rtl8852cu_fw"})
            check(File.Exists(Path.Combine(extracted,required)),"embedded relay file extracted: "+required);
        string helper=Path.Combine(extracted,"RadioDriver.ps1");
        byte[] expected=SHA256.HashData(File.ReadAllBytes(helper));
        File.WriteAllText(helper,"damaged cached helper fixture");
        WirelessRelayRuntime.EnsureExtracted(root);
        check(SHA256.HashData(File.ReadAllBytes(helper)).SequenceEqual(expected),"modified cached driver helper is repaired from the embedded hash-checked bundle");
        check(!Directory.EnumerateFiles(extracted,"*.tmp",SearchOption.AllDirectories).Any(),"atomic runtime extraction leaves no partial files");
        var vm=new MainViewModel(previewOnly:true);
        try
        {
            check(await vm.EnterWirelessRelayModeAsync(),"existing manager can transfer exclusive input ownership to relay mode");
            check(vm.WirelessRelayModeActive&&!vm.CanOperate&&!vm.CanStart&&!vm.CanManageRuntime,"local output and runtime actions blocked while the relay owns input");
            check(!await vm.EnterWirelessRelayModeAsync(),"a second relay window cannot acquire input ownership again");
            vm.ConnectPro2InputCommand.Execute(null);
            check(!vm.Running&&!vm.AutoReconnectEnabled,"local connect command cannot restart BLE or virtual USB while relaying");
            vm.ExitWirelessRelayMode();
            check(!vm.WirelessRelayModeActive&&vm.CanOperate&&!vm.AutoReconnectEnabled,"leaving relay restores local controls without automatic reconnection");
        }
        finally{await vm.ShutdownAsync();}
    }
}
