using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Y700Switch2V60Viiper;

public sealed record SystemDiagnosticInfo(string Family, string Edition, string Release,
    string Version, string Architecture, string ProcessArchitecture, string AppVersion, string StackVersion);

public sealed record RadioDiagnosticInfo(BluetoothAdapterDiagnosticInfo Driver, bool IsDefault,
    string Address, bool LowEnergy, bool Central, bool Peripheral, bool Classic);

public sealed record SystemBluetoothSnapshot(DateTimeOffset CapturedAt, SystemDiagnosticInfo System,
    IReadOnlyList<RadioDiagnosticInfo> Adapters, string CompatibilityTelemetry, string Warning,
    Win10ConfigurationAudit? Win10Configuration = null, Win10StackParameterAssessment? Win10Assessment = null);

public sealed record Win10ConfigurationAudit(bool Readable, bool Configured, string Values,
    bool OriginalBackupExists, bool RestartPending, string ProcessElevated)
{
    // A prior apply marker is only a clue; an intervening reboot invalidates it.
    public static bool RequiresRestart(string markerJson, DateTimeOffset boot, DateTimeOffset now)
    {
        if (markerJson.Length > 65536 || boot > now) return false;
        try
        {
            using JsonDocument json = JsonDocument.Parse(markerJson);
            var item = json.RootElement;
            return item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("restartRequired", out var needed) && needed.ValueKind == JsonValueKind.True &&
                item.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String &&
                operation.GetString() == "apply" && item.TryGetProperty("completedAt", out var time) &&
                time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out var completed) &&
                completed > boot && completed <= now;
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>OS/adapter metadata only. No BLE device opens, GATT access, or system writes.</summary>
public static class SystemBluetoothDiagnostics
{
    public static string WindowsFamily(int build) => build >= 22000 ? "Windows 11" : "Windows 10";
    public static string Value(string? value) => string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) ? "未获取" :
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
    public static string CurrentAppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "未获取";
    public static string Footer => WindowsFamily(Environment.OSVersion.Version.Build) + " · " +
        Environment.OSVersion.Version.Build + " · " + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() +
        " · v" + CurrentAppVersion;

    public static async Task<SystemBluetoothSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        // Keep registry/file/device enumeration off the WPF thread and Notify callbacks.
        return await Task.Run(async () =>
        {
            var warnings = new List<string>();
            SystemDiagnosticInfo system = ReadSystem(warnings);
            Win10ConfigurationAudit? config = Environment.OSVersion.Version.Build < 22000 ? ReadWin10Configuration() : null;
            var adapters = new List<RadioDiagnosticInfo>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                BluetoothAdapter? preferred = await BluetoothAdapter.GetDefaultAsync().AsTask(timeout.Token);
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
                    BluetoothAdapter.GetDeviceSelector()).AsTask(timeout.Token);
                var ids = devices.Select(d => d.Id).ToList();
                if (preferred != null && !ids.Contains(preferred.DeviceId, StringComparer.OrdinalIgnoreCase))
                    ids.Insert(0, preferred.DeviceId);
                foreach (string id in ids.Distinct(StringComparer.OrdinalIgnoreCase).Take(16))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    try
                    {
                        BluetoothAdapter? radio = preferred != null && string.Equals(id, preferred.DeviceId,
                            StringComparison.OrdinalIgnoreCase) ? preferred :
                            await BluetoothAdapter.FromIdAsync(id).AsTask(timeout.Token);
                        if (radio == null) { warnings.Add("适配器接口不可用"); continue; }
                        BluetoothAdapterDiagnosticInfo driver = await Win10BleDiagnostic.CaptureAdapterAsync(radio)
                            .WaitAsync(timeout.Token);
                        string hexAddress = radio.BluetoothAddress.ToString("X12");
                        string address = string.Join(":", Enumerable.Range(0, 6).Select(i => hexAddress.Substring(i * 2, 2)));
                        adapters.Add(new(driver, preferred != null && string.Equals(id, preferred.DeviceId,
                            StringComparison.OrdinalIgnoreCase), address,
                            radio.IsLowEnergySupported, radio.IsCentralRoleSupported, radio.IsPeripheralRoleSupported,
                            radio.IsClassicSupported));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { warnings.Add("适配器读取失败：" + ex.GetType().Name); }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { warnings.Add("蓝牙信息读取超时，保留已获取的设备"); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add("蓝牙信息不可用：" + ex.GetType().Name); }
            cancellationToken.ThrowIfCancellationRequested();
            return new SystemBluetoothSnapshot(DateTimeOffset.Now, system, adapters.ToArray(),
                BleCompatibilityInspector.Current.Telemetry, string.Join("；", warnings), config,
                BleCompatibilityInspector.Current.Win10Assessment);
        }, cancellationToken);
    }

    private static SystemDiagnosticInfo ReadSystem(List<string> warnings)
    {
        string edition = "", release = "", stack = "";
        string version = Environment.OSVersion.Version.ToString();
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            edition = key?.GetValue("EditionID")?.ToString() ?? "";
            release = key?.GetValue("DisplayVersion")?.ToString() ?? key?.GetValue("ReleaseId")?.ToString() ?? "";
            if (key?.GetValue("UBR") is int revision && revision >= 0)
                version = Environment.OSVersion.Version.ToString(3) + "." + revision;
        }
        catch (Exception ex) { warnings.Add("系统版本补充信息：" + ex.GetType().Name); }
        try { stack = FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, "drivers", "bthport.sys")).FileVersion ?? ""; }
        catch (Exception ex) { warnings.Add("蓝牙栈版本：" + ex.GetType().Name); }
        return new(WindowsFamily(Environment.OSVersion.Version.Build), Value(edition), Value(release), version,
            RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
            CurrentAppVersion, Value(stack));
    }

    private static Win10ConfigurationAudit ReadWin10Configuration()
    {
        string elevated;
        try { using var identity = WindowsIdentity.GetCurrent(); elevated = new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator).ToString(); }
        catch { elevated = "unavailable"; }
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "XinHeLianSheng-Pro2-Bridge", "BLE-Turbo");
        bool backup = File.Exists(Path.Combine(directory, "original-stack-state.json"));
        bool pending = false;
        try
        {
            string marker = Path.Combine(directory, "last-stack-operation.json");
            if (File.Exists(marker) && new FileInfo(marker).Length <= 65536)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset boot = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
                pending = Win10ConfigurationAudit.RequiresRestart(File.ReadAllText(marker), boot, now);
            }
        }
        catch { /* Optional marker is evidence, never authority to change settings. */ }
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(Win10BleStackProfile.RegistrySubKey, writable: false);
            var values = new List<string>(); bool configured = key != null;
            foreach (var pair in Win10BleStackProfile.Values)
            {
                object? actual = key?.GetValue(pair.Key);
                configured &= actual is int number && number == pair.Value;
                values.Add(pair.Key + "=" + (actual == null ? "absent" : actual is int n ? n.ToString() : "non_dword"));
            }
            return new(true, configured, string.Join(" ", values), backup, pending, elevated);
        }
        catch { return new(false, false, "unavailable", backup, pending, elevated); }
    }

    public static string Format(SystemBluetoothSnapshot snapshot)
    {
        SystemDiagnosticInfo os = snapshot.System;
        var text = new StringBuilder();
        text.AppendLine("系统与蓝牙信息");
        text.AppendLine("采集时间：" + snapshot.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        text.AppendLine("系统：" + Value(os.Family) + " / " + Value(os.Edition) + " / " + Value(os.Release));
        text.AppendLine("系统版本 / Build：" + Value(os.Version));
        text.AppendLine("架构：系统 " + Value(os.Architecture) + " / 程序 " + Value(os.ProcessArchitecture));
        text.AppendLine("程序版本：" + Value(os.AppVersion));
        text.AppendLine("Windows 蓝牙栈（bthport）：" + Value(os.StackVersion));
        text.AppendLine("BLE 策略：" + Value(snapshot.CompatibilityTelemetry));
        if (snapshot.Win10Assessment is { } assessment)
        {
            text.AppendLine("Win10 参数机制证据：" + assessment.EvidenceSummary);
            text.AppendLine("检查结果：" + assessment.EvidenceLevel + " / 文件 " + Value(assessment.StackVersion) +
                " / 参数名 " + assessment.ParameterNamesFound.Count + "/4");
            text.AppendLine("找到的参数名：" + (assessment.ParameterNamesFound.Count == 0 ? "无（不代表参数一定不可用）" :
                string.Join(", ", assessment.ParameterNamesFound)));
            text.AppendLine("可恢复实验入口：" + assessment.CanExperiment + " / 需明确实验确认：" + assessment.RequiresExperimentalConsent);
            text.AppendLine("未知哈希不会单独禁止试验；字符串及注册表写入均不证明实际连接采用 7.5ms。");
        }
        if (snapshot.Win10Configuration is { } config)
        {
            text.AppendLine("Win10 BLE 参数：" + (config.Readable ? config.Configured ? "已配置固定 7.5ms 默认值" : "尚未匹配固定 7.5ms 配置" : "读取失败"));
            text.AppendLine("配置值：" + config.Values);
            text.AppendLine("原始备份存在：" + config.OriginalBackupExists + " / 待重启线索：" + config.RestartPending);
            text.AppendLine("主程序管理员权限：" + config.ProcessElevated + "（提权运行本身不等于已完成 BLE 配置）");
        }
        text.AppendLine();
        text.AppendLine("蓝牙适配器：" + snapshot.Adapters.Count);
        if (snapshot.Adapters.Count == 0) text.AppendLine("没有获取到可用蓝牙接口；不代表手柄或驱动必然损坏。");
        for (int i = 0; i < snapshot.Adapters.Count; i++)
        {
            RadioDiagnosticInfo radio = snapshot.Adapters[i];
            BluetoothAdapterDiagnosticInfo driver = radio.Driver;
            text.AppendLine("[适配器 " + (i + 1) + "] " + Value(driver.Name) + (radio.IsDefault ? "（系统默认）" : ""));
            text.AppendLine("  PNP ID：" + Value(driver.PnpDeviceId));
            text.AppendLine("  VID / PID：" + Value(driver.Vid) + " / " + Value(driver.Pid));
            text.AppendLine("  驱动厂商：" + Value(driver.DriverProvider));
            text.AppendLine("  驱动版本：" + Value(driver.DriverVersion));
            text.AppendLine("  驱动日期：" + Value(driver.DriverDate));
            text.AppendLine("  蓝牙地址：" + Value(radio.Address));
            text.AppendLine("  能力：BLE=" + radio.LowEnergy + " / Central=" + radio.Central +
                " / Peripheral=" + radio.Peripheral + " / Classic=" + radio.Classic);
            text.AppendLine("  WinRT ID：" + Value(driver.WinRtDeviceId));
            text.AppendLine();
        }
        text.AppendLine("蓝牙协议版本（如 5.0 / 5.3）：未获取，不能由驱动版本或 VID/PID 推断。");
        text.AppendLine("系统默认适配器不等于已确认的每条连接归属；设备能力也不保证 133Hz。");
        if (!string.IsNullOrWhiteSpace(snapshot.Warning)) text.AppendLine("读取提示：" + Value(snapshot.Warning));
        return text.ToString();
    }

    public static IReadOnlyList<string> LogLines(SystemBluetoothSnapshot snapshot)
    {
        SystemDiagnosticInfo os = snapshot.System;
        var lines = new List<string>
        {
            "[SYSTEM_INFO] family=\"" + Value(os.Family) + "\" edition=" + Value(os.Edition) +
            " display_version=" + Value(os.Release) + " os_version=" + Value(os.Version) +
            " os_arch=" + Value(os.Architecture) + " process_arch=" + Value(os.ProcessArchitecture) +
            " app_version=" + Value(os.AppVersion) + " bthport_version=\"" + Value(os.StackVersion) + "\"",
            "[BLE_ENVIRONMENT] adapter_count=" + snapshot.Adapters.Count + " metadata_only=true warning=\"" +
                (string.IsNullOrWhiteSpace(snapshot.Warning) ? "" : Value(snapshot.Warning)) + "\""
        };
        for (int i = 0; i < snapshot.Adapters.Count; i++)
        {
            RadioDiagnosticInfo radio = snapshot.Adapters[i];
            BluetoothAdapterDiagnosticInfo driver = radio.Driver;
            lines.Add("[BLE_ADAPTER_DRIVER] index=" + (i + 1) + " system_default=" + radio.IsDefault +
                " name=\"" + Value(driver.Name) + "\" pnp_id=\"" + Value(driver.PnpDeviceId) + "\" vid=" +
                Value(driver.Vid) + " pid=" + Value(driver.Pid) + " provider=\"" + Value(driver.DriverProvider) +
                "\" driver_version=" + Value(driver.DriverVersion) + " driver_date=\"" + Value(driver.DriverDate) +
                "\" address=" + Value(radio.Address) + " le=" + radio.LowEnergy + " central=" + radio.Central +
                " peripheral=" + radio.Peripheral + " classic=" + radio.Classic);
        }
        if (snapshot.Win10Assessment is { } assessment)
            lines.Add("[WIN10_STACK_PARAMETER_EVIDENCE] level=" + assessment.EvidenceLevel +
                " bthport_sha256=" + assessment.StackSha256 + " parameter_names=" + assessment.ParameterNamesFound.Count +
                "/4 valid_system_x64_image=" + assessment.ValidSystemX64Image + " experiment_available=" + assessment.CanExperiment +
                " explicit_experiment_required=" + assessment.RequiresExperimentalConsent + " strings_are_not_reader_proof=true");
        if (snapshot.Win10Configuration is { } config)
            lines.Add("[WIN10_BLE_CONFIGURATION] readable=" + config.Readable + " configured=" + config.Configured +
                " " + config.Values + " original_backup=" + config.OriginalBackupExists + " restart_pending=" + config.RestartPending +
                " process_elevated=" + config.ProcessElevated + " elevation_alone_does_not_apply_parameters=true");
        return lines;
    }
}
