using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Y700Switch2V60Viiper;

internal static class Win10HomeUiRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (int build in new[] { 10240, 14393, 17763, 19041, 19045, 21999 })
            check(BleTurboUiPresentation.ShowHomeAction(true, build),
                "Win10 home entry is discoverable independently of supported parameter-write eligibility " + build);
        foreach (int build in new[] { 0, 7601, 9600, 22000, 22631, 26100, 26200 })
            check(!BleTurboUiPresentation.ShowHomeAction(true, build),
                "Win10 home entry is absent outside the Windows 10 build family " + build);
        foreach (int build in new[] { 19041, 19045, 26200 })
            check(!BleTurboUiPresentation.ShowHomeAction(false, build),
                "non-Windows environment never offers Windows system parameter action " + build);

        var vm = new MainViewModel(previewOnly: true);
        try
        {
            bool currentWin10 = BleTurboUiPresentation.ShowHomeAction(
                OperatingSystem.IsWindows(), Environment.OSVersion.Version.Build);
            check(vm.ShowWin10BleHomeAction == currentWin10 && !vm.Win10BleHomePanelExpanded &&
                !vm.ShowWin10BleHomePanel,
                "home parameter entry follows actual OS while details start collapsed");
            var counters = vm.Pro2Slots.Select(slot => slot.InputSource.GetCompactCounters()).ToArray();
            string initialLog = vm.LogText;
            vm.ToggleWin10BleHomePanel();
            if (currentWin10)
            {
                check(vm.Win10BleHomePanelExpanded && vm.ShowWin10BleHomePanel,
                    "Win10 home action expands parameter options without starting them");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(vm, true);
                vm.ToggleWin10BleHomePanel();
                vm.Win10BleHomePanelExpanded = false;
                check(vm.Win10BleHomePanelExpanded,
                    "busy home panel cannot toggle or collapse underneath a running operation");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(vm, false);
                vm.ToggleWin10BleHomePanel();
                check(!vm.Win10BleHomePanelExpanded && !vm.ShowWin10BleHomePanel,
                    "Win10 home options close again when idle");
            }
            else
                check(!vm.ShowWin10BleHomePanel,
                    "non-Win10 VM never exposes Win10 parameter panel");
            check(!vm.Running && !vm.Busy && vm.Pro2Slots.All(slot => !slot.InputSource.IsRunning) &&
                counters.SequenceEqual(vm.Pro2Slots.Select(slot => slot.InputSource.GetCompactCounters())) &&
                vm.LogText == initialLog,
                "opening home options creates no input reports radio command UAC or configuration operation");

            // External diagnostic snapshots must never change the available host-system action.
            typeof(MainViewModel).GetMethod("ApplyEnvironmentSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [currentWin10 ? EnvironmentDiagnosticsRegressionTests.Fixture() :
                    CompatibleRateRegressionTests.Win10Fixture(false)]);
            check(vm.ShowWin10BleHomeAction == currentWin10,
                "foreign computer metadata cannot expose or hide this computer's Win10 home entry");
        }
        finally { vm.ShutdownAsync().GetAwaiter().GetResult(); }
    }

    public static void RunBindings(MainWindow window, MainViewModel vm, Action<bool, string> check, Action pump)
    {
        var tabs = (TabControl)window.FindName("MainTabs");
        int originalTab = tabs.SelectedIndex;
        tabs.SelectedIndex = 0;
        vm.Win10BleHomePanelExpanded = false;
        pump();
        var entry = (Button)window.FindName("Win10BleOptimizeButton");
        var panel = (Border)window.FindName("Win10BleHomePanel");
        var picker = (ComboBox)window.FindName("Win10BlePresetComboBox");
        var apply = (Button)window.FindName("Win10BleConfigureButton");
        var restore = (Button)window.FindName("BleRestoreParametersButton");
        check(entry.Content?.ToString() == "优化蓝牙 · Win10" &&
            entry.GetBindingExpression(UIElement.VisibilityProperty)?.ParentBinding.Path.Path ==
                nameof(MainViewModel.ShowWin10BleHomeAction) &&
            entry.GetBindingExpression(UIElement.IsEnabledProperty)?.ParentBinding.Path.Path ==
                nameof(MainViewModel.CanOperate),
            "concise Win10 home button binds runtime visibility and exclusive-operation guard");
        check(panel.GetBindingExpression(UIElement.VisibilityProperty)?.ParentBinding.Path.Path ==
            nameof(MainViewModel.ShowWin10BleHomePanel) && panel.Visibility == Visibility.Collapsed,
            "Win10 parameter panel is collapsed at ordinary startup");
        foreach (FrameworkElement control in new FrameworkElement[] { entry, panel, picker, apply, restore })
            check(FindTabAncestor(control) == tabs.Items[0],
                "parameter control is on home rather than buried in settings " + control.Name);
        check(picker.GetBindingExpression(ComboBox.ItemsSourceProperty)?.ParentBinding.Path.Path ==
            nameof(MainViewModel.Win10BlePresetOptions) &&
            picker.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path ==
                nameof(MainViewModel.SelectedWin10BlePreset) && picker.Items.Count == 3 &&
            vm.SelectedWin10BlePreset == Win10BlePresetPolicy.Default,
            "moved home picker preserves three presets and default fixed 7.5ms target");
        check(apply.GetBindingExpression(UIElement.IsEnabledProperty)?.ParentBinding.Path.Path ==
                nameof(MainViewModel.CanConfigureWin10Stack) &&
            picker.GetBindingExpression(UIElement.IsEnabledProperty)?.ParentBinding.Path.Path ==
                nameof(MainViewModel.CanConfigureWin10Stack),
            "opening options does not override safe parameter-write capability");
        check(restore.GetBindingExpression(UIElement.IsEnabledProperty) == null,
            "restoration remains independent of stack write eligibility");
        check(entry.IsVisible == vm.ShowWin10BleHomeAction && !panel.IsVisible,
            "home button is visible only on Win10 and options start hidden");

        if (vm.ShowWin10BleHomeAction)
        {
            string logBefore = vm.LogText;
            var counters = vm.Pro2Slots.Select(slot => slot.InputSource.GetCompactCounters()).ToArray();
            entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pump();
            check(panel.IsVisible && vm.Win10BleHomePanelExpanded && !vm.Busy && !vm.Running &&
                vm.LogText == logBefore &&
                counters.SequenceEqual(vm.Pro2Slots.Select(slot => slot.InputSource.GetCompactCounters())),
                "clicking Win10 home button only reveals controls without silently applying parameters");
            check(picker.IsVisible && apply.IsVisible && restore.IsVisible,
                "all parameter choices apply and recovery actions are reachable on home");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(vm, true);
            pump();
            check(!entry.IsEnabled && !picker.IsEnabled && !apply.IsEnabled && !restore.IsEnabled,
                "busy operation disables all home parameter actions including restore");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(vm, false);
            pump();
            check(entry.IsEnabled && restore.IsEnabled,
                "home entry and recovery return when operation ends");
            entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pump();
            check(!panel.IsVisible && !vm.Win10BleHomePanelExpanded,
                "same home entry closes options without disturbing connection");
        }
        tabs.SelectedIndex = originalTab;
        pump();
    }

    private static TabItem? FindTabAncestor(DependencyObject child)
    {
        for (DependencyObject? current = child; current != null;)
        {
            if (current is TabItem tab) return tab;
            DependencyObject? logical = LogicalTreeHelper.GetParent(current);
            current = logical ?? (current is Visual ? VisualTreeHelper.GetParent(current) : null);
        }
        return null;
    }
}
