using System;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;
using Y700Switch2V60Viiper;

internal static class BleOptimizationUiRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        check(new V60UserSettings().AutoOptimizeBle &&
            JsonSerializer.Deserialize<V60UserSettings>("{}")!.AutoOptimizeBle,
            "BLE optimization enabled by default for new and legacy settings");
        check(!JsonSerializer.Deserialize<V60UserSettings>(
            JsonSerializer.Serialize(new V60UserSettings { AutoOptimizeBle = false }))!.AutoOptimizeBle,
            "BLE optimization opt-out survives JSON roundtrip");

        var vm = new MainViewModel(previewOnly: true);
        try
        {
            check(vm.AutoOptimizeBle && vm.Pro2Slots.All(slot => slot.InputSource.AutoOptimizeBleEnabled),
                "BLE optimization preference initializes all input slots");
            check(!vm.IsBleOptimizationRunning && !vm.ShowBleOptimizationSummary &&
                !vm.CanTryWin11Compatibility && vm.CanChangeBleOptimizationSettings,
                "offline optimization UI does not start a radio operation or invent a result");
            check(vm.BleConnectionTitle == vm.CompactConnectionTitle &&
                vm.BleConnectionHint == vm.CompactConnectionHint,
                "ordinary connection presentation preserved outside optimizer");
            vm.AutoOptimizeBle = false;
            check(!vm.AutoOptimizeBle && vm.Pro2Slots.All(slot => !slot.InputSource.AutoOptimizeBleEnabled),
                "offline optimization opt-out reaches every input source");
            check(!vm.Running && vm.Pro2Slots.All(slot => !slot.InputSource.IsRunning),
                "changing optimization setting opens no physical or virtual controller");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(vm, true);
            vm.AutoOptimizeBle = true;
            check(!vm.CanChangeBleOptimizationSettings && !vm.AutoOptimizeBle,
                "optimization preference cannot change in the middle of an exclusive operation");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(vm, false);
            vm.AutoOptimizeBle = true;
            check(vm.CanChangeBleOptimizationSettings && vm.AutoOptimizeBle &&
                vm.Pro2Slots.All(slot => slot.InputSource.AutoOptimizeBleEnabled),
                "optimization preference can be restored after exclusive operation");
        }
        finally { vm.ShutdownAsync().GetAwaiter().GetResult(); }
    }

    public static void RunBindings(MainWindow window, MainViewModel vm, Action<bool, string> check)
    {
        var toggle = (CheckBox)window.FindName("AutoOptimizeBleCheckBox");
        check(toggle.GetBindingExpression(CheckBox.IsCheckedProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.AutoOptimizeBle) &&
            toggle.GetBindingExpression(CheckBox.IsEnabledProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.CanChangeBleOptimizationSettings),
            "automatic BLE optimization settings checkbox bound to persistent guarded preference");
        var title = (TextBlock)window.FindName("BleConnectionTitleText");
        var hint = (TextBlock)window.FindName("BleConnectionHintText");
        var summary = (TextBlock)window.FindName("BleOptimizationSummaryText");
        check(title.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.BleConnectionTitle) &&
            hint.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.BleConnectionHint) &&
            summary.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.BleOptimizationSummary),
            "home optimization progress binds source-backed connection text and measured summary");
        var button = (Button)window.FindName("BleAutoOptimizeButton");
        check(button.Content?.ToString() == "自动优化蓝牙…" &&
            button.GetBindingExpression(Button.IsEnabledProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.CanTryWin11Compatibility),
            "home exposes bounded automatic optimizer instead of opaque single 7.5ms action");
        check(!vm.IsBleOptimizationRunning && title.Text == vm.CompactConnectionTitle && hint.Text == vm.CompactConnectionHint,
            "offline home keeps existing connection message until a real optimizer is running");
    }
}
