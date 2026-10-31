using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private SystemBluetoothSnapshot? environmentSnapshot;
    private Task? environmentRefreshTask;
    private bool environmentInfoBusy;
    private string environmentInfoStatus = "正在准备系统与蓝牙信息…";
    private string environmentMetadata = "尚未采集系统与蓝牙信息。";
    private string win10ConfigurationHistoryText = "";
    private string win10ConfigurationAttemptText = "";

    public bool EnvironmentInfoBusy => environmentInfoBusy;
    public string EnvironmentInfoStatus => environmentInfoStatus;
    public string EnvironmentHomeSummary
    {
        get
        {
            if (environmentSnapshot is not { } snapshot)
                return SystemBluetoothDiagnostics.WindowsFamily(Environment.OSVersion.Version.Build) +
                    " Build " + Environment.OSVersion.Version.Build + " · 默认适配器 / 驱动：尚未采集";
            string build = Version.TryParse(snapshot.System.Version, out Version? version) && version.Build >= 0
                ? version.Build + (version.Revision >= 0 ? "." + version.Revision : "")
                : SystemBluetoothDiagnostics.Value(snapshot.System.Version);
            string system = SystemBluetoothDiagnostics.Value(snapshot.System.Family) + " Build " + build;
            RadioDiagnosticInfo? adapter = snapshot.Adapters.FirstOrDefault(a => a.IsDefault);
            if (adapter == null)
                return system + " · 默认适配器：" + (snapshot.Adapters.Count == 0 ? "未获取" :
                    "未识别（" + snapshot.Adapters.Count + " 个接口）") + " · 驱动：未获取";
            return system + " · 默认适配器：" + SystemBluetoothDiagnostics.Value(adapter.Driver.Name) +
                " · 驱动 " + SystemBluetoothDiagnostics.Value(adapter.Driver.DriverVersion);
        }
    }
    public string EnvironmentHomeSummaryHint => EnvironmentHomeSummary + Environment.NewLine +
        "系统默认适配器不代表已确认此手柄的连接归属；完整信息可查看、复制或保存。";
    public string EnvironmentReportText => environmentMetadata + Environment.NewLine +
        "当前手柄与游戏输出（缓存状态，无额外 GATT 查询）" + Environment.NewLine +
        inputSource.GetLinkDiagnosticText() + "Raw / FD2 / Parsed：" + CompactRawRate + " / " +
        CompactInputRate + " Hz / " + CompactParsedRate + "（界面短窗）" + Environment.NewLine +
        "最新输入年龄：" + CompactInputAge + Environment.NewLine + "手柄固件：" + CompactFirmware +
        " / " + CompactFirmwareHint + Environment.NewLine + "游戏输出：" +
        (Running ? CompactActualMode : "未启动；BLE 连接不等于游戏手柄已经创建") + Environment.NewLine +
        "电量更新：" + inputSource.RuntimeBatteryRefreshStatus + Environment.NewLine +
        ((session ?? pro2Slots[0].Session)?.NativeHidDiagnostic is { Length: > 0 } nativeHid
            ? Environment.NewLine + nativeHid + Environment.NewLine : "") +
        Environment.NewLine + inputSource.ProtocolDiagnosticText +
        Environment.NewLine + inputSource.GetAdaptiveDiagnosticText(includeHistory: !previewOnly) +
        (bluetoothRestartDiagnostic.Length > 0 ? "\n蓝牙适配器重启记录\n" + bluetoothRestartDiagnostic + "\n" : "") +
        (win10ConfigurationHistoryText.Length > 0 ? "\n" + win10ConfigurationHistoryText + "\n" : "") +
        (win10ConfigurationAttemptText.Length > 0 ? "\nWin10 本次程序配置操作记录\n" + win10ConfigurationAttemptText : "") +
        (HasLowRateWarning ? "\n低速判定\n" + LowRateReportText : "");

    private void InitializeEnvironmentInfo()
    {
        if (previewOnly) { environmentInfoStatus = "界面预览：未查询系统设备"; return; }
        _ = RefreshEnvironmentInfoAsync();
    }

    public Task RefreshEnvironmentInfoAsync()
    {
        if (previewOnly || shuttingDown) return Task.CompletedTask;
        if (environmentRefreshTask is { IsCompleted: false }) return environmentRefreshTask;
        return environmentRefreshTask = CaptureEnvironmentInfoAsync(lifetimeCts.Token);
    }

    private async Task CaptureEnvironmentInfoAsync(CancellationToken token)
    {
        environmentInfoBusy = true;
        environmentInfoStatus = "只读采集中…不会断开手柄或修改蓝牙设置";
        NotifyEnvironmentInfo();
        try
        {
            SystemBluetoothSnapshot snapshot = await SystemBluetoothDiagnostics.CaptureAsync(token);
            if (shuttingDown || token.IsCancellationRequested) return;
            ApplyEnvironmentSnapshot(snapshot);
            foreach (string line in SystemBluetoothDiagnostics.LogLines(snapshot)) AppendLog(line);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!shuttingDown)
            {
                environmentInfoStatus = "信息读取失败：" + ex.GetType().Name + "；可以重新读取";
                AppendLog("[BLE_ENVIRONMENT] read_failed=" + ex.GetType().Name + " system_writes=false");
            }
        }
        finally
        {
            environmentInfoBusy = false;
            if (!shuttingDown && !token.IsCancellationRequested) NotifyEnvironmentInfo();
        }
    }

    // Kept separate for deterministic UI tests with synthetic metadata, no radio access.
    private void ApplyEnvironmentSnapshot(SystemBluetoothSnapshot snapshot)
    {
        environmentSnapshot = snapshot;
        environmentMetadata = SystemBluetoothDiagnostics.Format(snapshot);
        bool isWin10 = Version.TryParse(snapshot.System.Version, out Version? systemVersion)
            && systemVersion.Build is >= 19041 and < 22000;
        win10ConfigurationHistoryText = isWin10
            ? previewOnly ? Win10BleDiagnosticHistory.Format(null) : Win10BleDiagnosticHistory.ReadAndFormat(true)
            : "";
        environmentInfoStatus = "采集于 " + snapshot.CapturedAt.ToString("HH:mm:ss") +
            (snapshot.Warning.Length > 0 ? " · 部分信息未获取" : " · 只读信息");
        NotifyEnvironmentInfo();
    }

    private void RecordWin10ConfigurationAttempt(string stage, string detail)
    {
        if (win10ConfigurationAttemptText.Length > 8192) win10ConfigurationAttemptText = "较早操作已省略。\n";
        win10ConfigurationAttemptText += DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") +
            " · " + stage.Replace('\r', ' ').Replace('\n', ' ') + " · " +
            detail.Replace('\r', ' ').Replace('\n', ' ') + "\n";
        OnPropertyChanged(nameof(EnvironmentReportText));
    }

    private void NotifyEnvironmentInfo()
    {
        RefreshLowRateDiagnosis();
        OnPropertyChanged(nameof(EnvironmentInfoBusy));
        OnPropertyChanged(nameof(EnvironmentInfoStatus));
        OnPropertyChanged(nameof(EnvironmentHomeSummary));
        OnPropertyChanged(nameof(EnvironmentHomeSummaryHint));
        OnPropertyChanged(nameof(EnvironmentReportText));
        OnPropertyChanged(nameof(CompactSystemText));
    }

    public async Task ExportEnvironmentInfoAsync(string path)
    {
        if (environmentSnapshot == null && !previewOnly) await RefreshEnvironmentInfoAsync();
        string report = EnvironmentReportText;
        await File.WriteAllTextAsync(path, report, new UTF8Encoding(false));
        environmentInfoStatus = "已保存：" + path;
        NotifyEnvironmentInfo();
        AppendLog("[ENVIRONMENT_EXPORT] path=\"" + path + "\" metadata_only=true");
    }
}
