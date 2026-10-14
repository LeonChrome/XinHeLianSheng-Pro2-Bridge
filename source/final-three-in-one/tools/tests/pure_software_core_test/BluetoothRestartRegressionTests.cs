using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Y700Switch2V60Viiper;

internal static class BluetoothRestartRegressionTests
{
    internal static BluetoothRestartTarget Target(string vid, string pid, bool preferred = false) => new(
        vid == "8087" ? "Intel Bluetooth" : vid == "0E8D" ? "MediaTek Bluetooth" : vid == "10D7" ? "Generic Bluetooth Adapter" : "Realtek Bluetooth",
        $@"USB\VID_{vid}&PID_{pid}\INSTANCE", $@"\\?\USB#VID_{vid}&PID_{pid}#INSTANCE#{{92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2}}",
        "Test Provider", "1.2.3.4", preferred);
    public static void Run(Action<bool, string> check)
    {
        foreach (var target in new[] { Target("0BDA", "0852"), Target("8087", "0033"), Target("0E8D", "0616"), Target("10D7", "B012") })
        {
            check(target.CanRestart, "Mainstream radio target independent of vendor/hash " + target.Name);
            check(BluetoothRestartPolicy.RestartArguments(target).SequenceEqual(new[] { "/restart-device", target.PnpId }), "Exact instance only, no wildcard/group/force/reboot");
            string op = Guid.NewGuid().ToString("N");
            var args = BluetoothAdapterRestart.HelperArguments(target, op);
            check(args.Length == 7 && args[0] == BluetoothAdapterRestart.Command && args[2] == op && args[4] == target.PnpId && args[6] == target.InterfaceId,
                "Elevation has only fixed tokens plus explicit verified instance/interface/operation data");
        }
        var valid = Target("8087", "0033");
        foreach (var bad in new[] { valid with { PnpId = @"USB\VID_0BDA&PID_0852\INSTANCE" },
            valid with { PnpId = "*" }, valid with { PnpId = "/class Bluetooth" },
            valid with { PnpId = valid.PnpId + "\n/reboot" }, valid with { InterfaceId = valid.InterfaceId.Replace("92383b0e", "00000000") },
            valid with { PnpId = @"BTHLEDEVICE\child\ID", InterfaceId = @"\\?\BTHLEDEVICE#child#ID#{92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2}" },
            valid with { PnpId = @"ROOT\Bluetooth\ID", InterfaceId = @"\\?\ROOT#Bluetooth#ID#{92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2}" } })
        {
            check(!bad.CanRestart, "Mismatched, child, virtual or injected target refused");
            bool refused = false;
            try { BluetoothRestartPolicy.RestartArguments(bad); } catch (ArgumentException) { refused = true; }
            check(refused, "Unsafe target cannot generate a native command");
        }
        check(BluetoothRestartPolicy.IsVerifiedSuccess(0, true, true, true), "Restart requires all verified success evidence");
        foreach (var tuple in new[] { (3010, true, true, true), (0, false, true, true), (0, true, false, true), (0, true, true, false), (5, true, true, true) })
            check(!BluetoothRestartPolicy.IsVerifiedSuccess(tuple.Item1, tuple.Item2, tuple.Item3, tuple.Item4), "Exit or postcheck failure never falsely accepted");
        check(BluetoothRestartPolicy.CompletionMessage(3010, false).Contains("未完成") && BluetoothRestartPolicy.CompletionMessage(3010, false).Contains("保存工作"), "3010 is pending reboot, not successful hot restart");
        check(BluetoothRestartPolicy.CompletionMessage(0, true).Contains("需重新测量"), "Restart itself is not 133Hz proof");
        const string nativeText = "Microsoft PnP 工具\r\n设备已重启";
        check(BluetoothRestartPolicy.DecodeNativeOutput(System.Text.Encoding.Unicode.GetBytes(nativeText)) == nativeText, "PnPUtil UTF16 localized output with its native header readable without interleaved nulls");
        check(BluetoothRestartPolicy.DecodeNativeOutput(System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("设备已重启")).ToArray()) == "设备已重启", "UTF16 BOM localized output is unambiguous");
        check(BluetoothRestartPolicy.DecodeNativeOutput(System.Text.Encoding.UTF8.GetBytes("Device restarted")) == "Device restarted", "UTF8 native output preserved");
        check(BluetoothRestartPolicy.DecodeNativeOutput(Array.Empty<byte>()) == "", "Empty native output tolerated independently of result evidence");
        var now = DateTimeOffset.UtcNow;
        string nonce = Guid.NewGuid().ToString("N");
        var evidence = new BluetoothRestartResult(nonce, valid.PnpId, valid.InterfaceId, now, now,
            true, 0, true, true, true, "synthetic", "");
        check(evidence.Verified && !evidence.RebootRequired && BluetoothAdapterRestart.MatchesRequest(evidence, nonce, valid, now, now), "Only current matching verified evidence accepted");
        foreach (var badResult in new[] { evidence with { OperationId = Guid.NewGuid().ToString("N") }, evidence with { PnpId = Target("0BDA", "0852").PnpId },
            evidence with { InterfaceId = Target("0BDA", "0852").InterfaceId }, evidence with { StartedUtc = now.AddHours(-1) },
            evidence with { CompletedUtc = now.AddHours(1) }, evidence with { CompletedUtc = now.AddMinutes(-1) } })
            check(!BluetoothAdapterRestart.MatchesRequest(badResult, nonce, valid, now, now), "Stale, future, different-operation or different-device result rejected");
        check(!(evidence with { RestartAttempted = false }).Verified && !(evidence with { RestartAttempted = false, NativeExitCode = 3010 }).RebootRequired,
            "No attempt cannot claim restart success or reboot required");
        check(BluetoothAdapterRestart.ExecuteAsync(new[] { BluetoothAdapterRestart.Command, "/reboot" }).GetAwaiter().GetResult() == 7, "Malformed helper CLI exits without privileged/device access");
        check(BluetoothAdapterRestart.ExecuteAsync(new[] { BluetoothAdapterRestart.Command, "--operation-id", "../../bad", "--adapter-pnp", valid.PnpId, "--adapter-interface", valid.InterfaceId }).GetAwaiter().GetResult() == 7, "Result path injection rejected before administrator operation");
        var vm = new MainViewModel(previewOnly: true);
        try
        {
            bool called = false;
            vm.RequestBluetoothRestartAsync(_ => { called = true; return valid; }).GetAwaiter().GetResult();
            check(!called && !vm.Running && vm.Pro2Slots.All(slot => !slot.InputSource.IsRunning), "Preview cannot open radio, elevate or stop live hardware");
            check(vm.BluetoothRestartStatus.Contains("未执行") && vm.HasBluetoothRestartStatus, "Preview status honestly reports no actual operation");
        }
        finally { vm.ShutdownAsync().GetAwaiter().GetResult(); }
    }
    public static void RunUi(Window owner, MainViewModel vm, Action<bool, string> check, Action pump, Action<Window,string> save)
    {
        var button = (Button)owner.FindName("RestartBluetoothAdapterButton");
        check(button.IsVisible && button.IsEnabled && button.Content.ToString()!.Contains("重启蓝牙"), "Settings exposes standalone explicit restart action");
        using var marker = new DisposableDialog(new BluetoothRestartWindow(new[] { Target("8087", "0033", true), Target("10D7", "B012") }) { Owner = owner, ShowActivated = false });
        var dialog = marker.Window;
        dialog.Show(); pump();
        var picker = (ComboBox)dialog.FindName("AdapterPicker");
        var confirm = (Button)dialog.FindName("ConfirmButton");
        check(picker.SelectedIndex == -1 && !confirm.IsEnabled, "Two radios require explicit selection, not system-default ownership assumption");
        save(dialog, "ui-bluetooth-restart-multiple.png");
        picker.SelectedIndex = 1; pump();
        check(confirm.IsEnabled && dialog.SelectedTarget == null, "Selection enables confirmation but cannot mutate devices");
        check(((TextBlock)dialog.FindName("SelectedDeviceText")).Text == Target("10D7", "B012").PnpId, "Selected target is shown precisely");
        save(dialog, "ui-bluetooth-restart-selected.png");
        dialog.Close();
        using var single = new DisposableDialog(new BluetoothRestartWindow(new[] { Target("0BDA", "0852") }) { Owner = owner, ShowActivated = false });
        single.Window.Show(); pump();
        check(((ComboBox)single.Window.FindName("AdapterPicker")).SelectedIndex == 0 && ((Button)single.Window.FindName("ConfirmButton")).IsEnabled,
            "One radio can be preselected but still requires the explicit confirm button");
        save(single.Window, "ui-bluetooth-restart-single.png");
        using var invalid = new DisposableDialog(new BluetoothRestartWindow(new[] { Target("0BDA", "0852") with { PnpId = "unknown" } }) { Owner = owner, ShowActivated = false });
        invalid.Window.Show(); pump();
        check(!((Button)invalid.Window.FindName("ConfirmButton")).IsEnabled, "Unverified adapter cannot be confirmed in UI");
    }
    private sealed class DisposableDialog : IDisposable
    {
        internal BluetoothRestartWindow Window { get; }
        internal DisposableDialog(BluetoothRestartWindow window) => Window = window;
        public void Dispose() => Window.Close();
    }
}
