namespace Y700Switch2V60Viiper;

public sealed record BleConnectionAttemptEvidence
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public string Stage { get; init; } = "scan";
    public string Outcome { get; init; } = "running";
    public string Error { get; init; } = "";
    public string Address { get; init; } = "";
    public int Adapters { get; init; } = -1;
    public int CapableAdapters { get; init; } = -1;
    public string DefaultRadioState { get; init; } = "unknown";
    public int Advertisements { get; init; }
    public int Candidates { get; init; }
    public int Excluded { get; init; }
    public int NonPreferred { get; init; }
    public string ScanError { get; init; } = "Success";
    public string ScanModes { get; init; } = "";
    public string ScanTrace { get; init; } = "";
    public string CandidateTrace { get; init; } = "";
}

public sealed record BleConnectionDiagnosis(bool Visible, string Code, string Title, string Advice, string Evidence)
{
    public static BleConnectionDiagnosis Evaluate(BleConnectionAttemptEvidence? e)
    {
        if (e == null || e.Outcome is "running" or "connected" or "cancelled") return new(false, "none", "", "", "");
        static string Number(int value) => value < 0 ? "未获取" : value.ToString();
        string evidence = $"时间 {e.StartedAt:MM-dd HH:mm:ss} · 阶段 {e.Stage} · 结果 {e.Outcome}" +
            $"\n地址 {e.Address} · BLE 接口 {Number(e.Adapters)} / 中央能力 {Number(e.CapableAdapters)} · 默认蓝牙 {e.DefaultRadioState}" +
            $"\n扫描 {e.ScanModes} · 广播 {e.Advertisements} · 候选 {e.Candidates} · 其他槽位保留 {e.Excluded} · 非本只地址 {e.NonPreferred}" +
            $"\n扫描状态 {e.ScanError} · 返回详情 {e.Error}\n{e.ScanTrace}{e.CandidateTrace}";
        BleConnectionDiagnosis D(string code, string title, string advice) => new(true, code, title, advice, evidence);
        if (e.Adapters == 0)
            return D("no_adapter", "Windows 未提供蓝牙 BLE 接口", "在设备管理器检查蓝牙设备是否启用；安装电脑或适配器厂商的官方驱动。当前无法区分硬件缺失、设备禁用与驱动异常。");
        if (e.Adapters > 0 && e.CapableAdapters == 0)
            return D("no_central", "当前接口未提供 BLE 中央角色", "本程序需要 BLE 中央角色。先检查厂商官方驱动；普通蓝牙音频或专用手柄接收器不能证明支持该接口。");
        if (e.Outcome == "no_candidate" && e.Advertisements == 0 && e.Adapters == 1 && e.DefaultRadioState is "Off" or "Disabled")
            return D("radio_off", "系统报告蓝牙已关闭或禁用", "请打开 Windows 蓝牙；若无法打开，检查飞行模式、硬件开关及设备管理器。");
        if (e.Outcome == "AccessDenied" || e.ScanError is "ConsentRequired" ||
            e.ScanError.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) || e.Error.Contains("0x80070005", StringComparison.OrdinalIgnoreCase))
            return D("access_denied", "系统拒绝了蓝牙访问", "检查 Windows 蓝牙访问权限、设备禁用与组织策略。管理员运行不等于已解除拒绝。");
        if (e.ScanError != "Success")
            return D("scan_error", "蓝牙扫描未正常完成", "查看下方系统返回状态。先确认蓝牙可用；如持续报错，使用设置中的蓝牙重启，或检查厂商官方驱动。此状态不能单独证明驱动过旧。");
        if (e.Outcome == "reserved")
            return D("reserved", "扫描到的手柄由其他槽位保留", "请唤醒本只手柄；若要换手柄，先移除占用地址的槽位。不会把同一只手柄重复连接给两个玩家。");
        if (e.Outcome == "saved_not_seen")
            return D("saved_not_seen", "未发现本槽位保存的手柄", "已看到其他候选，但地址不属于本只。请唤醒本只手柄；换手柄时使用手动选择。");
        if (e.Outcome == "no_candidate")
            return e.Advertisements > 0
                ? D("no_pro2", "收到 BLE 广播，未匹配到 Pro2", "确认手柄处于 PRO2 模式并开启配对广播。可用首页“手动模式”核对附近地址，选择后会先验证 Pro2 接口。此阶段与 USBIP 无关；若广播列表仍无手柄，检查是否连到其他设备及厂商蓝牙驱动。")
                : D("no_advertisements", "扫描期间没有收到 BLE 广播", "确认 Windows 蓝牙已开启，并让 Pro2 靠近电脑开启配对广播。尚不能区分附近没有广播与系统、适配器或驱动异常。");
        if (e.Outcome == "Unreachable")
            return D("unreachable", "目标蓝牙服务不可达", "系统未能完成本次 GATT 通信。确认手柄仍可连接、未被其他设备占用；持续失败再检查官方驱动。不能据此认定蓝牙版本不兼容。");
        if (e.Outcome == "gatt_draining")
            return D("gatt_draining", "Windows 仍在结束上一轮蓝牙请求", "请求释放后会自动重试。若持续卡在这里，可用设置中的“重启蓝牙”恢复适配器。");
        if (e.Outcome == "channels_missing")
            return D("channels_missing", "候选未提供完整的 Pro2 接口", "确认选择了真实 Pro2 和正确模式。保留接口记录供兼容性排查，程序不会向未确认的设备发送初始化命令。");
        if (e.Outcome == "notify_timeout")
            return D("notify_timeout", "已订阅通知，但未收到有效 FD2 输入", "连接尚未完成输入验收。可能是报告模式或传输异常；查看初始化和通知记录，不能用虚拟输出频率代替真实输入。");
        return D("operation_failed", "蓝牙连接在“" + e.Stage + "”阶段失败", "查看系统返回码与失败阶段；原因尚未确认。程序已保留记录，不会仅凭驱动日期或蓝牙版本给出结论。");
    }
}
