using System;
using System.Collections.Generic;

namespace Y700Switch2V60Viiper;

/// <summary>Explicit UI consent text and child arguments; never changes the system itself.</summary>
public static class BleTurboUiPresentation
{
    public static bool ShowHomeAction(bool windows, int build) => windows && build >= 10240 && build < 22000;
    public static bool IsExperimental(BleCompatibilityPlan plan) =>
        plan.Route == BleCompatibilityRoute.Win10ExperimentalStack;

    public static string SafetyNote(BleCompatibilityPlan plan) => IsExperimental(plan)
        ? "此 Win10 系统栈尚无实机生效证据；可备份后授权试验，不因新设备或哈希不同而直接拒绝。全局设置会影响其他 BLE 连接，是否提速以真实输入为准。"
        : plan.Route == BleCompatibilityRoute.Win10AuditedStack
            ? "此系统栈已完成参数读取机制审查；不同蓝牙设备仍须实测。全局设置影响其他 BLE 连接，已配置不等于达到 133Hz。"
            : plan.Guidance;

    public static string Confirmation(BleCompatibilityPlan plan, Win10BlePresetDefinition preset) =>
        "目标：" + preset.Title + "。" + (IsExperimental(plan) ? "当前系统尚未验证，属于参数试验。" : "") +
        "\n\n将备份并修改系统全局 BLE 参数，暂时断开当前手柄，其他蓝牙设备也可能断开；无法安全重启蓝牙时，会提示正常重启 Windows。" +
        "\n\n完成后重新连接实测，不保证 133Hz。可在首页恢复原参数，不更换驱动。继续？";

    public static IReadOnlyList<string> Arguments(bool restore, BleCompatibilityPlan plan,
        Win10BlePresetDefinition preset, bool experimentalConsent = false)
    {
        if (restore) return new[] { BleTurboConfigurator.StackRestoreArgument };
        if (!plan.CanConfigureWin10Stack)
            throw new InvalidOperationException(plan.Title + "：" + plan.Guidance);
        if (IsExperimental(plan) && !experimentalConsent)
            throw new InvalidOperationException("此系统栈须在确认框明确授权可恢复试验；当前连接未停止，未修改参数。");
        Win10BlePresetDefinition normalized = Win10BlePresetPolicy.Get(preset.Preset);
        var args = new List<string> { BleTurboConfigurator.StackApplyArgument,
            Win10BlePresetPolicy.SelectorPrefix + normalized.Id };
        if (IsExperimental(plan)) args.Add(Win10BleStackProfile.ExperimentalArgument);
        return args;
    }
}
