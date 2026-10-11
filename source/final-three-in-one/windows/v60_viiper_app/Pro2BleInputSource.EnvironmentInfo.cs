using System;
using System.Text;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    /// <summary>Cached link facts only: never reads characteristics or invokes parameter APIs.</summary>
    public string GetLinkDiagnosticText()
    {
        LinkDiagnosticSnapshot info;
        lock (gate)
        {
            info = new(connectionPhase, transportConnected, IsHighRateReady, connectedAddress,
                connectionIntervalUnits, connectionLatency, connectionLinkTimeout, connectionPreferenceStatus,
                win11ParameterRouteStatus, fd2ReportRateStatus, fd2ReportRateDescriptorHandle,
                fd2ReportRateDescriptorUuid, fd2ReportRateValueBefore, fd2ReportRateValueAfter);
        }
        // Formatting never holds the input lock or delays a Notify parser.
        var text = new StringBuilder();
        text.AppendLine("连接阶段：" + info.Phase);
        text.AppendLine("BLE 物理连接：" + (info.Connected ? "已连接" : "未连接"));
        text.AppendLine("Pro2 地址：" + (info.Connected ? SystemBluetoothDiagnostics.Value(info.Address) : "未连接"));
        text.AppendLine("真实输入验收：" + (info.Accepted ? "133Hz 验收通过" :
            info.Phase == Pro2ConnectionPhase.Measuring ? "正在测量，不代表最终速率" : "尚未通过 133Hz 验收"));
        text.AppendLine("兼容游玩门槛：真实 FD2 / Parsed 均至少 60Hz；当前 " + (IsGameplayReady ? "可游玩" : "未达到 / 未连接"));
        text.AppendLine("连接间隔：" + (info.Connected && info.Interval > 0
            ? (info.Interval * 1.25).ToString("F2") + " ms（系统报告，不是 Notify 间隔推测）"
            : OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "未读取" : "未读取（Win10 无对应公开读取 API）"));
        text.AppendLine("从机延迟 / 监督超时：" + (info.Connected && info.Interval > 0
            ? info.Latency + " / " + (info.Timeout * 10) + " ms" : "未读取"));
        text.AppendLine("连接参数请求：" + info.Preference);
        text.AppendLine("Win11 7.5ms 路线：" + info.Route);
        text.AppendLine("FD2 请求：" + RequestedFd2ReportRateHz + " Hz / " + info.DescriptorStatus);
        text.AppendLine("FD2 descriptor：0x" + info.Handle.ToString("X4") + " / " + SystemBluetoothDiagnostics.Value(info.Uuid));
        text.AppendLine("descriptor 写前 / 写后：" + info.Before + " / " + info.After);
        text.AppendLine("请求返回成功不等于真实输入达到 133Hz，以 FD2 / Parsed 测量为准。");
        Pro2RateEvidence rate = GetRateEvidence();
        if (rate.Stability is { } health)
        {
            text.AppendLine("持续窗口监测：" + health.State + " / 新鲜：" + health.IsRecent +
                " / 有效窗口：" + health.ValidWindowCount + " / 测量时长：" + health.ValidMeasuredSeconds.ToString("F1") + "s");
            text.AppendLine("窗口有效输入最低 / 最高：" + (health.SessionMinimumHz?.ToString("F2") ?? "尚未测量") +
                " / " + (health.SessionMaximumHz?.ToString("F2") ?? "尚未测量") + "Hz / 低速窗口：" + health.LowWindowCount);
            text.AppendLine("健康门槛：" + health.HighRateThresholdHz + "Hz；连续 2 个低窗提示、连续 3 个高速窗恢复；不是所有蓝牙硬件的性能保证。");
        }
        text.AppendLine("本连接兼容预设测试已使用：" + rate.CompatibilityTrialAttempted);
        text.AppendLine("自动优化：" + AutoOptimizeBleEnabled + "；当前进度／结果：" + CompatibilityOptimizationSummary);
        return text.ToString();
    }

    private readonly record struct LinkDiagnosticSnapshot(Pro2ConnectionPhase Phase, bool Connected, bool Accepted,
        string Address, ushort Interval, ushort Latency, ushort Timeout, string Preference, string Route,
        string DescriptorStatus, ushort Handle, string Uuid, string Before, string After);
}
