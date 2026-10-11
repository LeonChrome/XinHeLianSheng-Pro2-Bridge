using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    // Only the main application enables this from its persisted setting. Existing
    // source-only experiments/CLI captures do not silently acquire a new workflow.
    public bool AutoOptimizeBleEnabled { get; set; }
    private volatile string compatibilityOptimizationSummary = "";
    private bool adaptiveOptimizationRunAttempted;
    public string CompatibilityOptimizationSummary => compatibilityOptimizationSummary;
    public bool IsCompatibilityOptimizationRunning => compatibilityTrialCts != null;
    public bool CanOptimizeConnectionCompatibility
    {
        get
        {
            Pro2RateEvidence rate = GetRateEvidence();
            return rate.RecentWindow && rate.LastInputAgeMs is >= 0 and < 500 &&
                rate.ParsedHz >= rate.Fd2Hz * .95 && BleAdaptivePresetPolicy.CanOptimizeInApp(
                    OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                    BleCompatibilityInspector.Current.Plan.CanRequestPreferredParameters,
                    rate.Connected && rate.Running, compatibilityTrialCts != null,
                    rate.Phase is Pro2ConnectionPhase.Initializing or Pro2ConnectionPhase.Measuring,
                    rate.CompatibilityTrialAttempted, rate.Fd2Hz);
        }
    }

    public Task<Pro2CompatibilityTrialOutcome> OptimizeConnectionCompatibilityAsync(
        IProgress<string> progress, CancellationToken token) => CanOptimizeConnectionCompatibility
        ? RunAdaptiveConnectionOptimizationAsync(progress, token, initializing: false)
        : Task.FromResult(new Pro2CompatibilityTrialOutcome(false, "not_eligible",
            "当前连接无需优化、正在测试或本轮预设已测完；保持现有输入。"));

    private async Task<Pro2CompatibilityTrialOutcome> RunAdaptiveConnectionOptimizationAsync(
        IProgress<string> progress, CancellationToken token, bool initializing)
    {
        BluetoothLEDevice owner;
        long sequence;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(150));
        lock (gate)
        {
            if (device == null || !transportConnected || compatibilityTrialCts != null ||
                activeBleDiagnosticCapture != null || win11CompatibilityTrialAttempted)
                return new(false, "busy_or_disconnected", "当前已有测量或连接已改变，未追加参数请求。");
            owner = device; sequence = connectionSequence;
            compatibilityTrialCts = deadline;
            adaptiveOptimizationRunAttempted = true;
        }
        bool SameConnection()
        {
            lock (gate) return transportConnected && ReferenceEquals(owner, device) &&
                sequence == connectionSequence && (initializing || IsRunning);
        }
        bool batteryEnabled = RuntimeBatteryRefreshEnabled;
        BluetoothLEPreferredConnectionParameters? original = retainedConnectionPreference;
        BleAdaptiveEnvironmentKey? environmentKey = null;
        void Report(string message)
        {
            compatibilityOptimizationSummary = message;
            progress.Report("[BLE_AUTO_PRESET] " + message);
            ConnectionStateChanged?.Invoke();
        }
        try
        {
            SetRuntimeBatteryRefreshEnabled(false);
            while (runtimeBatteryRefreshStatus == "refreshing") await Task.Delay(100, deadline.Token);
            BleCompatibilitySnapshot compatibility = BleCompatibilityInspector.Current;
            var capabilities = new BleAdaptiveCapabilities(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                compatibility.Plan.CanRequestPreferredParameters, compatibility.Plan.CanReadConnectionParameters,
                compatibility.Plan.CanConfigureWin10Stack && compatibility.Plan.Route == BleCompatibilityRoute.Win10AuditedStack,
                compatibility.Plan.CanConfigureWin10Stack && compatibility.Plan.Route == BleCompatibilityRoute.Win10ExperimentalStack);
            BleAdaptiveCachedPreset? cached = null;
            if (capabilities.Win11 && capabilities.RequestApi)
            {
                try { environmentKey = await CaptureAdaptiveEnvironmentKeyAsync(deadline.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { progress.Report("[BLE_AUTO_PRESET] metadata_unavailable=" + ex.GetType().Name + " trial_allowed_without_cache=true"); }
                cached = BleAdaptivePresetStore.Load(environmentKey, out string cacheCode);
                progress.Report("[BLE_AUTO_PRESET] cache=" + cacheCode + " remembered_preset=" +
                    (cached?.Preset.ToString() ?? "none") + " cache_is_retest_hint_only=true");
                win11ParameterRouteStatus = "AdaptiveBoundedPresets";
            }
            var backend = new AdaptiveTrialBackend(SameConnection,
                async (phase, ct) =>
                {
                    BleTrialMeasurement measured = await CaptureParameterTrialAsync("AUTO_" + phase, 10000, progress, ct);
                    var sample = new BleAdaptiveMeasurement(ToMeasuredTrialSample(measured, SameConnection()),
                        MaximumGapMs: measured.Gaps.MaxMs);
                    AdoptAdaptiveMeasurement(measured);
                    return sample;
                },
                async (candidate, ct) =>
                {
                    if (!SameConnection()) return false;
                    if (candidate != null) win11CompatibilityTrialAttempted = true;
                    bool submitted = SubmitAdaptiveConnectionPreset(owner, candidate, original, progress);
                    // Allow parameter negotiation before counting a full 10s window.
                    if (submitted && ct.CanBeCanceled) await Task.Delay(1500, ct);
                    return submitted;
                }, () => ReleaseAdaptiveTrialRequest(progress), Report);
            BleAdaptiveRunResult result = await BleAdaptiveSessionRunner.RunAsync(capabilities, backend, cached?.Preset, deadline.Token);
            if (!SameConnection())
                return new(false, "connection_changed", "测试中连接已改变，未把历史频率当作成功结果。");
            if (result.Code == "native_target_preserved") adaptiveOptimizationRunAttempted = false;
            bool high = result.FinalVerified && BleAdaptivePresetPolicy.MeetsTarget(result.Final);
            bool playable = BleAdaptivePresetPolicy.IsPlayable(result.Final);
            string rate = result.Final.EffectiveHz.ToString("F1") + " Hz";
            string selected = result.Selected?.DisplayName ?? "原连接";
            string summary = high ? "已选用 " + selected + " · 实测 " + rate + " · 高速复核通过，继续监测"
                : playable ? "已完成预设测试 · 当前 " + rate + "，可以游玩；未达到 133Hz，建议查看诊断"
                : "已完成当前可用路线测试 · 实测 " + rate + "，未达到 60Hz 游玩门槛；查看下方诊断";
            if (!capabilities.Win11 && !high)
                summary += capabilities.Win10AuditedStack
                    ? "；Win10 全局参数可在设置中授权配置，再自动重连实测"
                    : capabilities.Win10ExperimentalStack
                        ? "；可在设置授权可恢复 Win10 参数实验，参数机制未审查，写入后仍需实测"
                        : "；当前 Win10 系统文件检查未通过，未修改系统；查看诊断";
            if (!result.FinalVerified) summary += "（最终稳定性复核未通过）";
            compatibilityOptimizationSummary = summary;
            progress.Report("[BLE_AUTO_PRESET_RESULT] before_fd2_hz=" + result.Native.Sample.Fd2Hz.ToString("F2") +
                " before_parsed_hz=" + result.Native.Sample.ParsedHz.ToString("F2") +
                " after_fd2_hz=" + result.Final.Sample.Fd2Hz.ToString("F2") +
                " after_parsed_hz=" + result.Final.Sample.ParsedHz.ToString("F2") +
                " selected=" + (result.Selected?.Id ?? "native") + " final_verified=" + result.FinalVerified +
                " target_met=" + high + " gameplay_allowed=" + playable + " result=" + result.Code +
                " trials=" + result.Trials.Count + " synthetic_reports=0 system_writes=false");
            try
            {
                if (environmentKey != null && result.Selected != null && result.FinalVerified)
                    BleAdaptivePresetStore.Save(new(environmentKey, result.Selected.Preset, result.Native, result.Final, DateTimeOffset.UtcNow));
                else BleAdaptivePresetStore.Invalidate(environmentKey);
                string reportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PRO2WirelessReceiverControlBoard", "v6_logs", "ble_optimizer");
                Directory.CreateDirectory(reportFolder);
                string reportPath = Path.Combine(reportFolder, "presets_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".json");
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
                { environment = environmentKey, result, summary, longTermStabilityProven = false },
                    new JsonSerializerOptions { WriteIndented = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }), deadline.Token);
                progress.Report("[BLE_AUTO_PRESET_RESULT] report=\"" + reportPath + "\"");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { progress.Report("[BLE_AUTO_PRESET] result_save_error=" + ex.GetType().Name); }
            return new(result.Selected != null && result.FinalVerified, result.Code, summary);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            compatibilityOptimizationSummary = "预设测试达到时间上限，已结束；按当前真实输入决定是否可游玩。";
            progress.Report("[BLE_AUTO_PRESET] bounded_timeout_s=150 unlimited_retry=false");
            return new(false, "bounded_timeout", compatibilityOptimizationSummary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            compatibilityOptimizationSummary = "蓝牙预设测试未完成：" + ex.GetType().Name + "；保留连接并按真实输入显示结果。";
            progress.Report("[BLE_AUTO_PRESET] error_hresult=0x" + ex.HResult.ToString("X8") + " type=" + ex.GetType().Name);
            return new(false, "trial_error", compatibilityOptimizationSummary);
        }
        finally
        {
            if (SameConnection()) SetRuntimeBatteryRefreshEnabled(batteryEnabled);
            lock (gate) if (ReferenceEquals(compatibilityTrialCts, deadline)) compatibilityTrialCts = null;
            ConnectionStateChanged?.Invoke();
        }
    }

    private async Task<BleAdaptiveEnvironmentKey?> CaptureAdaptiveEnvironmentKeyAsync(CancellationToken token)
    {
        SystemBluetoothSnapshot environment = await SystemBluetoothDiagnostics.CaptureAsync(token);
        BluetoothAdapterDiagnosticInfo? adapter = Win11ValidatedConnectionProfile.SingleAuditedAdapter(environment);
        if (adapter == null) return null; // Default adapter is not proof of this link's owner.
        return new(Environment.OSVersion.Version.Build, BleCompatibilityInspector.Current.Facts.StackSha256,
            adapter.PnpDeviceId, adapter.WinRtDeviceId + "|" + environment.Adapters[0].Address,
            adapter.DriverProvider, adapter.DriverVersion, adapter.DriverDate,
            "ble-adaptive-v1|os=" + environment.System.Version + "|fw=" + (FirmwareInfo?.ControllerVersion ?? "unknown"));
    }

    private bool SubmitAdaptiveConnectionPreset(BluetoothLEDevice owner, BleAdaptivePresetCandidate? candidate,
        BluetoothLEPreferredConnectionParameters? original, IProgress<string> progress)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        try
        {
            Win11CustomConnectionParameters? payload = candidate is { IsExperimental: true }
                ? new(candidate.MinUnits!.Value, candidate.MaxUnits!.Value, candidate.Latency!.Value, candidate.TimeoutUnits!.Value) : null;
            BluetoothLEPreferredConnectionParameters? parameters = candidate == null ? original
                : payload?.Parameters ?? BluetoothLEPreferredConnectionParameters.ThroughputOptimized;
            if (parameters == null)
            {
                connectionParametersRequest?.Dispose(); connectionParametersRequest = null;
                customConnectionParameters = null; retainedConnectionPreference = null;
                connectionPreferenceStatus = "adaptive_released_native_interval_not_guaranteed";
                progress.Report("[BLE_AUTO_PRESET_REQUEST] restore=native request_released=true actual_interval_rollback_not_guaranteed=true");
                return true;
            }
            BluetoothLEPreferredConnectionParametersRequest request = owner.RequestPreferredConnectionParameters(parameters);
            bool accepted = request.Status == BluetoothLEPreferredConnectionParametersRequestStatus.Success;
            progress.Report("[BLE_AUTO_PRESET_REQUEST] preset=" + (candidate?.Id ?? "restore_original") +
                " " + FormatPreferredConnectionParameters(parameters) + " status=" + request.Status +
                " parameter_source=" + (candidate == null ? "previous_preference" : payload == null ? "system_preset" : "own_abi") +
                " actual_fd2_acceptance=pending");
            if (!accepted) { request.Dispose(); return false; }
            var previous = connectionParametersRequest;
            connectionParametersRequest = request; retainedConnectionPreference = parameters; customConnectionParameters = payload;
            previous?.Dispose();
            connectionPreferenceStatus = "adaptive_" + (candidate?.Id ?? "restore") + "_Success";
            return true;
        }
        catch (Exception ex)
        {
            progress.Report("[BLE_AUTO_PRESET_REQUEST] preset=" + (candidate?.Id ?? "restore_original") +
                " rejected_hresult=0x" + ex.HResult.ToString("X8") + " type=" + ex.GetType().Name);
            return false;
        }
    }

    private void ReleaseAdaptiveTrialRequest(IProgress<string> progress)
    {
        var request = connectionParametersRequest;
        connectionParametersRequest = null; retainedConnectionPreference = null; customConnectionParameters = null;
        try { request?.Dispose(); }
        catch (Exception ex) { progress.Report("[BLE_AUTO_PRESET] release_error=" + ex.GetType().Name); }
        connectionPreferenceStatus = "adaptive_request_released_after_restore_failure";
        progress.Report("[BLE_AUTO_PRESET] experimental_request_released=true actual_interval_rollback_not_guaranteed=true");
    }

    private void AdoptAdaptiveMeasurement(BleTrialMeasurement measured)
    {
        lock (gate)
        {
            liveFd2RateHz = measured.Fd2Hz; liveParsedRateHz = measured.ParsedHz;
            liveRateMeasuredTicks = Stopwatch.GetTimestamp();
            liveRateProbeTicks = liveRateMeasuredTicks;
            liveRateProbeRaw = fd2InputNotifyCount; liveRateProbeParsed = fd2InputParsedCount;
            liveNotifyGaps.Reset(); // Measurement boundary, not gameplay queue/counter reset.
            lastLiveGapSummary = measured.ExactGapSummary ?? default;
            rateAccepted = measured.Fd2Hz >= Pro2GameplayRatePolicy.HighRateAcceptanceHz &&
                measured.ParsedHz >= Pro2GameplayRatePolicy.HighRateAcceptanceHz && measured.ConnectedAtEnd && measured.LastInputAgeMs < 500;
            linkRateClass = rateAccepted ? "133hz" : "rate_limited";
        }
        if (IsRunning) SetConnectionPhase(rateAccepted ? Pro2ConnectionPhase.Ready : Pro2ConnectionPhase.RateLimited);
        else ConnectionStateChanged?.Invoke();
    }

    private sealed class AdaptiveTrialBackend(Func<bool> connected,
        Func<string, CancellationToken, Task<BleAdaptiveMeasurement>> measure,
        Func<BleAdaptivePresetCandidate?, CancellationToken, Task<bool>> request, Action release, Action<string> report)
        : IBleAdaptiveTrialBackend
    {
        public bool Connected => connected();
        public Task<BleAdaptiveMeasurement> MeasureAsync(string phase, CancellationToken token) => measure(phase, token);
        public Task<bool> RequestAsync(BleAdaptivePresetCandidate? candidate, CancellationToken token) => request(candidate, token);
        public void ReleaseTrialRequest() => release();
        public void Report(string message) => report(message);
    }
}
