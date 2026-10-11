using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

internal sealed record BleTurboCommandResult(bool Success, string Message);

internal static class BleTurboConfigurator
{
    public const string ApplyArgument = "--ble-turbo-apply";
    public const string RestoreArgument = "--ble-turbo-restore";
    public const string StackApplyArgument = "--ble-turbo-stack-apply";
    public const string StackRestoreArgument = "--ble-turbo-stack-restore";

    private static readonly string[] ParameterNames =
    [
        "ConnectionIntervalMin",
        "ConnectionIntervalMax",
        "ConnectionLatency",
        "LinkSupervisionTimeout"
    ];

    public static bool IsCommand(IReadOnlyList<string> args) =>
        args.Any(argument =>
            string.Equals(argument, ApplyArgument, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, RestoreArgument, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, StackApplyArgument, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, StackRestoreArgument, StringComparison.OrdinalIgnoreCase));

    public static BleTurboCommandResult Execute(IReadOnlyList<string> args)
    {
        try
        {
            // Validate the complete selector before elevation, snapshots, or any writes.
            Win10BlePresetDefinition preset = Win10BlePresetPolicy.ParseCommand(args);
            bool experimentalConsent = Win10BleStackProfile.ParseExperimentalConsent(args);
            AssertAdministrator();
            if (args.Any(argument => string.Equals(argument, StackRestoreArgument, StringComparison.OrdinalIgnoreCase)))
                return ConfigureStack(restore: true, preset, experimentalConsent: false);
            if (args.Any(argument => string.Equals(argument, StackApplyArgument, StringComparison.OrdinalIgnoreCase)))
                return ConfigureStack(restore: false, preset, experimentalConsent);
            return args.Any(argument =>
                    string.Equals(argument, RestoreArgument, StringComparison.OrdinalIgnoreCase))
                ? Restore()
                : ApplyProfileA();
        }
        catch (Exception ex)
        {
            return new BleTurboCommandResult(false, ex.Message);
        }
    }

    private static BleTurboCommandResult ConfigureStack(bool restore, Win10BlePresetDefinition preset,
        bool experimentalConsent)
    {
        string root = StateRoot();
        Directory.CreateDirectory(root);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string logPath = Path.Combine(root, (restore ? "restore-stack-" : "apply-stack-") + stamp + ".log");
        string originalPath = Path.Combine(root, "original-stack-state.json");
        string nativePath = @"HKEY_LOCAL_MACHINE\" + Win10BleStackProfile.RegistrySubKey;
        IReadOnlyDictionary<string, int> targetValues = preset.Values;
        string[] names = targetValues.Keys.ToArray();
        var log = new StringBuilder();
        log.AppendLine("=== Win10 reversible BTHPORT global BLE defaults experiment ===");
        log.AppendLine("time=" + DateTimeOffset.Now.ToString("O"));
        log.AppendLine("operation=" + (restore ? "restore" : "apply"));
        log.AppendLine("preset=" + (restore ? "original" : preset.Id));
        log.AppendLine("registry=" + nativePath);
        log.AppendLine("experimental_consent=" + experimentalConsent);
        Win10StackParameterAssessment? assessment = null;
        try
        {
            // Restore remains available after an update. Unknown Win10 hashes are
            // allowed only as explicitly confirmed experiments, never as proven readers.
            if (!restore)
            {
                assessment = Win10StackParameterAssessment.ReadCurrent();
                log.AppendLine("windows_build=" + assessment.WindowsBuild);
                log.AppendLine("bthport_path=" + assessment.StackPath);
                log.AppendLine("bthport_version=" + assessment.StackVersion);
                log.AppendLine("bthport_sha256=" + assessment.StackSha256);
                log.AppendLine("evidence_level=" + assessment.EvidenceLevel);
                log.AppendLine("parameter_names_found=" + string.Join(",", assessment.ParameterNamesFound));
                log.AppendLine("evidence_reason=" + OneLine(assessment.Reason));
                log.AppendLine("audited_hash_match=" + assessment.IsAudited);
                if (!assessment.CanExperiment)
                    throw new InvalidOperationException(
                        assessment.EvidenceSummary + " 未修改任何参数。\n日志：" + logPath);
                if (!assessment.CanApply(experimentalConsent))
                    throw new InvalidOperationException(
                        "当前 Win10 系统栈未审查；需明确确认可恢复实验（--ble-turbo-stack-experimental）后再应用。\n" +
                        assessment.EvidenceSummary + "\n未修改任何参数。\n日志：" + logPath);
            }

            BleState? original = File.Exists(originalPath) ? ReadState(originalPath, names) : null;
            if (restore && original == null)
                throw new InvalidOperationException("缺少首次修改前的系统栈备份；未修改任何参数。 ");
            if (original != null && !string.Equals(NormalizeRegistrySubKey(original.RegistryPath),
                    Win10BleStackProfile.RegistrySubKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份路径不是目标 BTHPORT Parameters；未修改任何参数。 ");

            BleAdapter? adapter = null;
            string restartUnavailable = "";
            // These four values are global. Do not block backup/apply/restore on
            // a non-USB radio or several radios, and never guess a restart target.
            try { adapter = FindSingleUsbBluetoothAdapter(); }
            catch (Exception ex)
            {
                restartUnavailable = "没有明确的单一当前 USB 蓝牙适配器，不会猜测重启设备；全局值操作后请正常重启 Windows。 " + ex.Message;
                log.AppendLine("adapter_restart_skipped=" + OneLine(ex.Message));
            }
            if (adapter != null)
            {
                log.AppendLine("adapter=" + adapter.Name);
                log.AppendLine("pnp_id=" + adapter.PnpDeviceId);
                log.AppendLine("driver_provider=" + adapter.DriverProvider);
                log.AppendLine("driver_version=" + adapter.DriverVersion);
            }
            using RegistryKey key = Registry.LocalMachine.OpenSubKey(Win10BleStackProfile.RegistrySubKey, writable: true) ??
                throw new InvalidOperationException("BTHPORT Parameters 不存在；未修改任何参数。 ");
            Dictionary<string, BleValue> before = Snapshot(key, names);
            AppendSnapshot(log, "before", before, names);

            string backupReg = Path.Combine(root, "bthport-before-" + stamp + ".reg");
            ExportRegistry(nativePath, backupReg);
            var state = new BleState(DateTimeOffset.Now.ToString("O"), adapter ?? original?.Adapter ??
                new BleAdapter("BTHPORT global defaults (no unique current USB radio)", "", "", "", ""),
                nativePath, before, backupReg);
            WriteJson(Path.Combine(root, "before-stack-" + stamp + ".json"), state);
            if (!restore && original == null)
                WriteJson(originalPath, state);
            log.AppendLine("original_state=" + originalPath);
            log.AppendLine("backup_reg=" + backupReg);

            try
            {
                if (restore)
                    RestoreSnapshot(key, original!.Values, names);
                else
                {
                    foreach ((string name, int value) in targetValues)
                        key.SetValue(name, value, RegistryValueKind.DWord);
                    key.Flush();
                }
                Dictionary<string, BleValue> after = Snapshot(key, names);
                AppendSnapshot(log, "after", after, names);
                foreach (string name in names)
                {
                    BleValue expected = restore ? original!.Values[name] : new(true, targetValues[name]);
                    if (after[name] != expected)
                        throw new InvalidOperationException("写入后校验失败：" + name);
                }
            }
            catch (Exception operationError)
            {
                log.AppendLine("operation_error=" + OneLine(operationError.ToString()));
                try
                {
                    RestoreSnapshot(key, before, names);
                    Dictionary<string, BleValue> rolledBack = Snapshot(key, names);
                    AppendSnapshot(log, "rollback", rolledBack, names);
                    if (names.Any(name => rolledBack[name] != before[name]))
                        throw new InvalidOperationException("回滚后校验失败。");
                    log.AppendLine("operation_rolled_back=true");
                }
                catch (Exception rollbackError)
                {
                    log.AppendLine("operation_rolled_back=false");
                    log.AppendLine("rollback_error=" + OneLine(rollbackError.ToString()));
                    throw new AggregateException("BLE 操作和回滚均失败；保留备份，请检查日志。", operationError, rollbackError);
                }
                throw;
            }

            RestartResult restart = adapter != null ? RestartAdapter(adapter.PnpDeviceId) :
                new RestartResult(false, restartUnavailable);
            log.AppendLine("restart_success=" + restart.Success);
            log.AppendLine("restart_output=" + OneLine(restart.Output));
            WriteJson(Path.Combine(root, "last-stack-operation.json"), new
            {
                operation = restore ? "restore" : "apply", completedAt = DateTimeOffset.Now.ToString("O"),
                preset = restore ? "original" : preset.Id,
                restartSucceeded = restart.Success, restartRequired = !restart.Success, originalPath, backupReg, logPath,
                evidenceLevel = assessment?.EvidenceLevel.ToString() ?? "restore_original",
                stackSha256 = assessment?.StackSha256 ?? "", stackVersion = assessment?.StackVersion ?? "",
                experimental = assessment?.RequiresExperimentalConsent ?? false,
                parameterNamesFound = assessment?.ParameterNamesFound ?? Array.Empty<string>()
            });
            log.AppendLine("result=success");
            return new BleTurboCommandResult(true,
                (restore ? "已恢复首次修改前的四项系统 BLE 默认值；原来不存在的值已移除。" :
                    "已写入 Win10 系统栈实验预设：" + preset.Title + "、Latency 0、Timeout 2s。\n" +
                    assessment!.EvidenceSummary + "\n写入成功不代表采用成功；真实 133Hz / 66Hz 均以连接后的 FD2 实测为准。") +
                "\n\n" + (restart.Success ? "适配器已安全重启。" : "参数已保存，但热重启未成功；请正常重启 Windows 后连接。") +
                "\n\n原始备份：\n" + originalPath + "\n\n日志：\n" + logPath);
        }
        catch (Exception ex)
        {
            log.AppendLine("result=failed");
            log.AppendLine(ex.ToString());
            return new BleTurboCommandResult(false, ex.Message + "\n\n日志：\n" + logPath);
        }
        finally
        {
            File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
        }
    }

    private static BleTurboCommandResult ApplyProfileA()
    {
        string stateRoot = StateRoot();
        Directory.CreateDirectory(stateRoot);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string logPath = Path.Combine(stateRoot, "apply-A-" + stamp + ".log");
        var log = new StringBuilder();
        log.AppendLine("=== PRO2 Controller Assistant / BLE Turbo Profile A ===");
        log.AppendLine("time=" + DateTimeOffset.Now.ToString("O"));

        BleAdapter adapter = FindSingleUsbBluetoothAdapter();
        string subKeyPath = @"SYSTEM\CurrentControlSet\Enum\" +
                            adapter.PnpDeviceId + @"\Device Parameters";
        string nativePath = @"HKEY_LOCAL_MACHINE\" + subKeyPath;
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true) ??
            throw new InvalidOperationException("蓝牙适配器 Device Parameters 不存在：" + nativePath);

        Dictionary<string, BleValue> before = Snapshot(key);
        log.AppendLine("adapter=" + adapter.Name);
        log.AppendLine("pnp_id=" + adapter.PnpDeviceId);
        log.AppendLine("driver_provider=" + adapter.DriverProvider);
        log.AppendLine("driver_version=" + adapter.DriverVersion);
        AppendSnapshot(log, "before", before);

        string backupReg = Path.Combine(stateRoot, "device-parameters-before-A-" + stamp + ".reg");
        ExportRegistry(nativePath, backupReg);

        var state = new BleState(
            DateTimeOffset.Now.ToString("O"),
            adapter,
            nativePath,
            before,
            backupReg);
        string beforeJson = Path.Combine(stateRoot, "before-A-" + stamp + ".json");
        WriteJson(beforeJson, state);
        string originalState = Path.Combine(stateRoot, "original-state.json");
        if (!File.Exists(originalState))
        {
            WriteJson(originalState, state);
            log.AppendLine("original_state_created=" + originalState);
        }
        else
        {
            log.AppendLine("original_state_retained=" + originalState);
        }

        try
        {
            key.SetValue("ConnectionIntervalMin", 6, RegistryValueKind.DWord);
            key.SetValue("ConnectionIntervalMax", 12, RegistryValueKind.DWord);
            key.SetValue("ConnectionLatency", 0, RegistryValueKind.DWord);
            key.SetValue("LinkSupervisionTimeout", 200, RegistryValueKind.DWord);
            key.Flush();
        }
        catch
        {
            RestoreSnapshot(key, before);
            throw;
        }

        Dictionary<string, BleValue> after = Snapshot(key);
        AppendSnapshot(log, "after", after);
        ValidateAppliedProfile(after);
        WriteJson(
            Path.Combine(stateRoot, "active-profile.json"),
            new
            {
                profile = "A",
                appliedAt = DateTimeOffset.Now.ToString("O"),
                adapter,
                values = new
                {
                    ConnectionIntervalMin = 6,
                    ConnectionIntervalMax = 12,
                    ConnectionLatency = 0,
                    LinkSupervisionTimeout = 200
                }
            });

        RestartResult restart = RestartAdapter(adapter.PnpDeviceId);
        log.AppendLine("restart_success=" + restart.Success.ToString().ToLowerInvariant());
        log.AppendLine("restart_output=" + OneLine(restart.Output));
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));

        string restartText = restart.Success
            ? "蓝牙适配器已安全重启，请重新连接手柄。"
            : "参数已写入，但适配器无法安全热重启；请正常重启 Windows 一次。";
        return new BleTurboCommandResult(
            true,
            "旧版适配器参数 A 已写入（不代表 133Hz 验收通过）：Min=6、Max=12、Latency=0、Timeout=200。\n\n" +
            restartText + "\n\n修改前备份：\n" + backupReg + "\n\n日志：\n" + logPath);
    }

    private static BleTurboCommandResult Restore()
    {
        string stateRoot = StateRoot();
        string originalState = Path.Combine(stateRoot, "original-state.json");
        if (!File.Exists(originalState))
        {
            throw new FileNotFoundException("找不到修改前备份，因此没有更改任何注册表值。", originalState);
        }

        BleState state = ReadState(originalState);
        string subKeyPath = NormalizeRegistrySubKey(state.RegistryPath);
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true) ??
            throw new InvalidOperationException("备份对应的注册表键不存在：" + state.RegistryPath);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string logPath = Path.Combine(stateRoot, "restore-" + stamp + ".log");
        var log = new StringBuilder();
        log.AppendLine("=== PRO2 Controller Assistant / BLE Turbo Restore ===");
        log.AppendLine("time=" + DateTimeOffset.Now.ToString("O"));
        AppendSnapshot(log, "before_restore", Snapshot(key));
        RestoreSnapshot(key, state.Values);
        Dictionary<string, BleValue> restored = Snapshot(key);
        AppendSnapshot(log, "after_restore", restored);

        string activeProfile = Path.Combine(stateRoot, "active-profile.json");
        if (File.Exists(activeProfile))
        {
            File.Delete(activeProfile);
        }

        RestartResult restart = RestartAdapter(state.Adapter.PnpDeviceId);
        log.AppendLine("restart_success=" + restart.Success.ToString().ToLowerInvariant());
        log.AppendLine("restart_output=" + OneLine(restart.Output));
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));

        string restartText = restart.Success
            ? "蓝牙适配器已安全重启。"
            : "原值已恢复；请正常重启 Windows 一次使恢复结果生效。";
        return new BleTurboCommandResult(
            true,
            "已按首次修改前的备份恢复蓝牙参数。\n\n" + restartText +
            "\n\n原始备份继续保留：\n" + originalState + "\n\n日志：\n" + logPath);
    }

    private static void AssertAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("此操作需要管理员权限；请从程序内点击按钮并接受 UAC。 ");
        }
    }

    private static BleAdapter FindSingleUsbBluetoothAdapter()
    {
        var adapters = new List<BleAdapter>();
        using RegistryKey usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB") ??
            throw new InvalidOperationException("无法读取 Windows USB 设备注册表。 ");
        foreach (string hardware in usb.GetSubKeyNames().Where(name =>
                     name.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) &&
                     name.Contains("&PID_", StringComparison.OrdinalIgnoreCase)))
        {
            using RegistryKey? hardwareKey = usb.OpenSubKey(hardware);
            if (hardwareKey == null)
            {
                continue;
            }

            foreach (string instance in hardwareKey.GetSubKeyNames())
            {
                using RegistryKey? device = hardwareKey.OpenSubKey(instance);
                string classGuid = Convert.ToString(device?.GetValue("ClassGUID")) ?? "";
                if (!string.Equals(
                        classGuid,
                        "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string pnpId = @"USB\" + hardware + @"\" + instance;
                // Enum retains unplugged radios. NORMAL CM lookup deliberately
                // rejects phantom nodes before counting or choosing a restart target.
                if (!IsPresentDeviceInstance(pnpId)) continue;
                string driver = Convert.ToString(device?.GetValue("Driver")) ?? "";
                string deviceName = LastResourcePart(Convert.ToString(device?.GetValue("DeviceDesc")) ?? "");
                string provider = "";
                string driverVersion = "";
                string driverDate = "";
                if (!string.IsNullOrWhiteSpace(driver))
                {
                    using RegistryKey? driverKey = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\Class\" + driver);
                    deviceName = Convert.ToString(driverKey?.GetValue("DriverDesc")) ?? deviceName;
                    provider = Convert.ToString(driverKey?.GetValue("ProviderName")) ?? "";
                    driverVersion = Convert.ToString(driverKey?.GetValue("DriverVersion")) ?? "";
                    driverDate = Convert.ToString(driverKey?.GetValue("DriverDate")) ?? "";
                }

                adapters.Add(new BleAdapter(
                    string.IsNullOrWhiteSpace(deviceName) ? pnpId : deviceName,
                    pnpId,
                    provider,
                    driverVersion,
                    driverDate));
            }
        }

        if (adapters.Count == 0)
        {
            throw new InvalidOperationException("没有找到当前存在的 USB 蓝牙适配器；当前一键配置仅支持可识别的 USB 蓝牙适配器。 ");
        }
        if (adapters.Count > 1)
        {
            throw new InvalidOperationException(
                "检测到多个 USB 蓝牙适配器，为避免改错设备，本次没有修改：\n" +
                string.Join("\n", adapters.Select(adapter => adapter.Name + " = " + adapter.PnpDeviceId)));
        }
        return adapters[0];
    }

    private static bool IsPresentDeviceInstance(string pnpDeviceId) =>
        OperatingSystem.IsWindows() && CM_Locate_DevNode(out _, pnpDeviceId, 0) == 0;

    [DllImport("CfgMgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    private static Dictionary<string, BleValue> Snapshot(RegistryKey key, IEnumerable<string>? names = null)
    {
        var presentNames = key.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, BleValue>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names ?? ParameterNames)
        {
            bool present = presentNames.Contains(name);
            if (present && key.GetValueKind(name) != RegistryValueKind.DWord)
                throw new InvalidDataException("现有参数不是 DWORD，不能保证精确恢复；未修改：" + name);
            long value = present ? Convert.ToInt64(key.GetValue(name, 0)) : 0;
            values[name] = new BleValue(present, value);
        }
        return values;
    }

    private static void RestoreSnapshot(RegistryKey key, IReadOnlyDictionary<string, BleValue> snapshot,
        IEnumerable<string>? names = null)
    {
        foreach (string name in names ?? ParameterNames)
        {
            if (snapshot.TryGetValue(name, out BleValue? saved) && saved.Present)
            {
                key.SetValue(name, checked((int)saved.Value), RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
        key.Flush();
    }

    private static void ValidateAppliedProfile(IReadOnlyDictionary<string, BleValue> values)
    {
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionIntervalMin"] = 6,
            ["ConnectionIntervalMax"] = 12,
            ["ConnectionLatency"] = 0,
            ["LinkSupervisionTimeout"] = 200
        };
        foreach ((string name, long expectedValue) in expected)
        {
            if (!values.TryGetValue(name, out BleValue? actual) ||
                !actual.Present ||
                actual.Value != expectedValue)
            {
                throw new InvalidOperationException("写入后校验失败：" + name);
            }
        }
    }

    private static void ExportRegistry(string nativePath, string destination)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "reg.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("export");
        process.StartInfo.ArgumentList.Add(nativePath);
        process.StartInfo.ArgumentList.Add(destination);
        process.StartInfo.ArgumentList.Add("/y");
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 reg.exe 导出修改前备份。 ");
        }
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(destination))
        {
            throw new InvalidOperationException(
                "注册表备份失败，因此没有写入参数。reg.exe exit=" + process.ExitCode + " " + OneLine(output));
        }
    }

    private static RestartResult RestartAdapter(string pnpDeviceId)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("/restart-device");
            process.StartInfo.ArgumentList.Add(pnpDeviceId);
            if (!process.Start())
            {
                return new RestartResult(false, "pnputil.exe start failed");
            }
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            bool reportedFailure = output.Contains("waiting for a system restart", StringComparison.OrdinalIgnoreCase) ||
                                   output.Contains("failed to restart", StringComparison.OrdinalIgnoreCase) ||
                                   output.Contains("等待系统重新启动", StringComparison.OrdinalIgnoreCase) ||
                                   output.Contains("重新启动设备失败", StringComparison.OrdinalIgnoreCase);
            return new RestartResult(process.ExitCode == 0 && !reportedFailure, output);
        }
        catch (Exception ex)
        {
            return new RestartResult(false, ex.Message);
        }
    }

    private static BleState ReadState(string path, IEnumerable<string>? names = null)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        JsonElement adapterElement = GetProperty(root, "adapter");
        var adapter = new BleAdapter(
            GetString(adapterElement, "name"),
            GetString(adapterElement, "pnpDeviceId"),
            GetString(adapterElement, "driverProvider"),
            GetString(adapterElement, "driverVersion"),
            GetString(adapterElement, "driverDate"));
        string registryPath = GetString(root, "registryPath");
        var values = new Dictionary<string, BleValue>(StringComparer.OrdinalIgnoreCase);
        JsonElement valuesElement = GetProperty(root, "values");
        foreach (string name in names ?? ParameterNames)
        {
            JsonElement valueElement = GetProperty(valuesElement, name);
            bool present = GetProperty(valueElement, "present").GetBoolean();
            long value = present ? GetProperty(valueElement, "value").GetInt64() : 0;
            // Validate the whole backup before opening a writable target key;
            // malformed later entries must not cause a partially applied restore.
            if (present) _ = checked((int)value);
            values[name] = new BleValue(present, value);
        }
        return new BleState(
            GetString(root, "capturedAt"),
            adapter,
            registryPath,
            values,
            GetString(root, "backupReg"));
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        throw new InvalidDataException("备份 JSON 缺少字段：" + name);
    }

    private static string GetString(JsonElement element, string name)
    {
        JsonElement value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Null ? "" : value.GetString() ?? "";
    }

    private static string NormalizeRegistrySubKey(string value)
    {
        const string providerPrefix = "Registry::HKEY_LOCAL_MACHINE\\";
        const string nativePrefix = "HKEY_LOCAL_MACHINE\\";
        if (value.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value[providerPrefix.Length..];
        }
        if (value.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value[nativePrefix.Length..];
        }
        return value.TrimStart('\\');
    }

    private static void AppendSnapshot(
        StringBuilder builder,
        string prefix,
        IReadOnlyDictionary<string, BleValue> snapshot,
        IEnumerable<string>? names = null)
    {
        foreach (string name in names ?? ParameterNames)
        {
            BleValue entry = snapshot[name];
            builder.AppendLine(prefix + "." + name + "=" +
                               (entry.Present ? entry.Value.ToString() : "absent"));
        }
    }

    private static string StateRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "XinHeLianSheng-Pro2-Bridge",
        "BLE-Turbo");

    private static void WriteJson<T>(string path, T value)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }),
            new UTF8Encoding(false));
    }

    private static string LastResourcePart(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Split(';')[^1];

    private static string OneLine(string value) =>
        (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    private sealed record BleValue(bool Present, long Value);
    private sealed record RestartResult(bool Success, string Output);
    private sealed record BleAdapter(
        string Name,
        string PnpDeviceId,
        string DriverProvider,
        string DriverVersion,
        string DriverDate);
    private sealed record BleState(
        string CapturedAt,
        BleAdapter Adapter,
        string RegistryPath,
        Dictionary<string, BleValue> Values,
        string BackupReg);
}
