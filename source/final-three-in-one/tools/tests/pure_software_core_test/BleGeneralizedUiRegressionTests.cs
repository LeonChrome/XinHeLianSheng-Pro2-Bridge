using System;
using System.Linq;
using System.Windows.Controls;
using Y700Switch2V60Viiper;

internal static class BleGeneralizedUiRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var experimental = new BleCompatibilityPlan(BleCompatibilityRoute.Win10ExperimentalStack,
            "Win10 · 可恢复试验", "尚无本机生效证据", false, false, true);
        var audited = experimental with { Route = BleCompatibilityRoute.Win10AuditedStack };
        var disabled = experimental with { Route = BleCompatibilityRoute.NativeNotifyOnly,
            CanConfigureWin10Stack = false };
        foreach (Win10BlePresetDefinition preset in Win10BlePresetPolicy.Presets)
        {
            var args = BleTurboUiPresentation.Arguments(false, experimental, preset, experimentalConsent: true);
            check(args.Count == 3 && args[0] == "--ble-turbo-stack-apply" &&
                args[1] == Win10BlePresetPolicy.SelectorPrefix + preset.Id &&
                args.Count(arg => arg == Win10BleStackProfile.ExperimentalArgument) == 1,
                "explicit unknown Win10 UI action sends one experiment permission and selected preset " + preset.Id);
            var knownArgs = BleTurboUiPresentation.Arguments(false, audited, preset);
            check(knownArgs.Count == 2 && !knownArgs.Contains(Win10BleStackProfile.ExperimentalArgument),
                "audited Win10 UI does not mislabel operation as unknown stack trial " + preset.Id);
            string confirmation = BleTurboUiPresentation.Confirmation(experimental, preset);
            check(confirmation.Length < 230 && confirmation.Contains(preset.Title) && confirmation.Contains("当前系统尚未验证") &&
                confirmation.Contains("全局") && confirmation.Contains("备份") &&
                confirmation.Contains("正常重启 Windows") && confirmation.Contains("不更换驱动") &&
                confirmation.Contains("不保证 133Hz") && confirmation.Contains("首页恢复原参数"),
                "concise unknown stack consent explains scope evidence recovery restart and no false frequency promise " + preset.Id);
        }
        foreach (BleCompatibilityPlan plan in new[] { experimental, audited, disabled })
            check(BleTurboUiPresentation.Arguments(true, plan, Win10BlePresetPolicy.Default)
                .SequenceEqual(new[] { "--ble-turbo-stack-restore" }),
                "restore remains reachable regardless of current stack permission " + plan.Route);
        bool refused = false;
        try { BleTurboUiPresentation.Arguments(false, disabled, Win10BlePresetPolicy.Default); }
        catch (InvalidOperationException) { refused = true; }
        check(refused, "unreadable or unsupported plan cannot stop input and then submit unauthorized system write");
        bool consentRequired = false;
        try { BleTurboUiPresentation.Arguments(false, experimental, Win10BlePresetPolicy.Default); }
        catch (InvalidOperationException) { consentRequired = true; }
        check(consentRequired, "unknown hash requires UI experiment consent even if command is invoked without its dialog");
        check(BleTurboUiPresentation.SafetyNote(experimental).Contains("不因新设备或哈希不同而直接拒绝") &&
            BleTurboUiPresentation.SafetyNote(audited).Contains("仍须实测"),
            "experimental eligibility is distinct from audited reading mechanism and actual radio result");

        var facts = new BleEnvironmentFacts(true, 19045, false, false,
            "F46BF2E7B20D4A6D2158738C587E8482828D62C2E0E8210874EEB79493DC853D", false);
        var compatibility = new BleCompatibilitySnapshot(facts, experimental, "");
        var rate = new Pro2RateEvidence(Pro2ConnectionPhase.RateLimited, true, true, false,
            33, 33, 1000, 1000, 26, 0, "write_success", "unsupported_pre_win11", "not_applicable_win10",
            new(169, 30, 30.25, 60, 28, 60, 0, 0, 169), true);
        foreach (SystemBluetoothSnapshot? snapshot in new SystemBluetoothSnapshot?[] {
            CompatibleRateRegressionTests.Win10Fixture(false),
            CompatibleRateRegressionTests.Win10Fixture(false, multiple: true), null })
        {
            var diagnosis = BleLowRateDiagnoser.Evaluate(rate, compatibility, snapshot);
            check(diagnosis.Visible && diagnosis.CanOfferWin10Configuration &&
                diagnosis.Advice.Contains("备份") && diagnosis.Advice.Contains("试验") &&
                diagnosis.Advice.Contains("全局") && !diagnosis.Advice.Contains("未验证，不能"),
                "user Win10 33Hz trace offers explicit reversible global trial without guessing ownership");
        }
    }

    public static void RunBindings(MainWindow window, MainViewModel vm, Action<bool, string> check)
    {
        var picker = (ComboBox)window.FindName("Win10BlePresetComboBox");
        check(picker.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path ==
            nameof(MainViewModel.SelectedWin10BlePreset) &&
            picker.ToolTip?.ToString()?.Contains("固定 7.5ms 优先") == true,
            "Win10 preset picker carries selected experiment and dynamic evidence warning");
        var configure = (Button)window.FindName("Win10BleConfigureButton");
        check(configure.GetBindingExpression(Button.IsEnabledProperty)?.ParentBinding.Path.Path ==
            nameof(MainViewModel.CanConfigureWin10Stack) &&
            configure.GetBindingExpression(Button.ToolTipProperty)?.ParentBinding.Path.Path ==
            nameof(MainViewModel.Win10BlePresetSafetyNote),
            "Win10 action follows trial eligibility not a hardcoded adapter whitelist");
        var restore = (Button)window.FindName("BleRestoreParametersButton");
        check(restore.GetBindingExpression(Button.IsEnabledProperty) == null,
            "restore action is not gated by audited or experimental environment eligibility");
        var optimize = (CheckBox)window.FindName("AutoOptimizeBleCheckBox");
        check(optimize.ToolTip?.ToString()?.Contains("Win10 全局参数必须单独确认") == true,
            "automatic optimizer checkbox distinguishes Win11 link trials from Win10 explicit global consent");
        check(!vm.IsBleOptimizationRunning && !vm.Running,
            "new compatibility controls perform no radio or virtual-output operations in offline fixture");
    }
}
