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
        bool diagnosticsCompleted = false;
        AdaptiveDiagnosticCapture diagnosticCapture = StartAdaptiveDiagnosticEvidence();
        void Report(string message)
        {
            RecordAdaptiveStage(diagnosticCapture, message);
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
                    RecordAdaptiveMeasurement(diagnosticCapture, measured);
                    var sample = new BleAdaptiveMeasurement(ToMeasuredTrialSample(measured, SameConnection()),
                        MaximumGapMs: measured.Gaps.MaxMs);
                    AdoptAdaptiveMeasurement(measured);
                    return sample;
                },
                async (candidate, ct) =>
                {
                    if (!SameConnection()) return false;
                    if (candidate != null) win11CompatibilityTrialAttempted = true;
                    bool submitted = SubmitAdaptiveConnectionPreset(owner, candidate, original, diagnosticCapture, progress);
                    // Allow parameter negotiation before counting a full 10s window.
                    if (submitted && ct.CanBeCanceled) await Task.Delay(1500, ct);
                    return submitted;
                }, () => ReleaseAdaptiveTrialRequest(diagnosticCapture, progress), Report);
            BleAdaptiveRunResult result = await BleAdaptiveSessionRunner.RunAsync(capabilities, backend, cached?.Preset, deadline.Token);
            CompleteAdaptiveDiagnosticEvidence(diagnosticCapture, result);
            diagnosticsCompleted = true;
            if (!SameConnection())
            {
                MarkAdaptiveDiagnosticInterrupted(diagnosticCapture, "connection_changed");
                return new(false, "connection_changed", "测试中连接已改变，未把历史频率当作成功结果。");
            }
            if (result.Code == "native_target_preserved") adaptiveOptimizationRunAttempted = false;
            bool high = result.FinalVerified && BleAdaptivePresetPolicy.MeetsTarget(result.Final);
            bool playable = BleAdaptivePresetPolicy.IsPlayable(result.Final);
            string rate = result.Final.EffectiveHz.ToString("F1") + " Hz";
            string selected = result.Selected?.DisplayName ?? "原连接";
            string summary = result.Code == "native_measurement_not_ready"
                ? "原连接测量无效，未进行参数测试；请查看系统与蓝牙信息。"
                : result.Code == "win11_api_unavailable"
                ? "系统参数请求 API 不可用，未进行预设测试；请查看系统与蓝牙信息。"
                : high ? "已选用 " + selected + " · 实测 " + rate + " · 高速复核通过，继续监测"
                : result.Trials.Count == 0 ? "已测原连接 · 当前 " + rate + "；未进行参数预设测试"
                : System.Linq.Enumerable.All(result.Trials, trial => !trial.RequestAccepted)
                    ? "预设请求未被接受 · 当前 " + rate + "；查看系统与蓝牙信息中的返回状态"
                : playable ? "已完成预设测试 · 当前 " + rate + "，可以游玩；未达到 133Hz，建议查看诊断"
                : "已完成当前可用路线测试 · 实测 " + rate + "，未达到 60Hz 游玩门槛；查看下方诊断";
            if (!capabilities.Win11 && !high)
                summary += capabilities.Win10AuditedStack
                    ? "；可在首页授权 Win10 参数测试，配置后仍需实测"
                    : capabilities.Win10ExperimentalStack
                        ? "；可在首页授权可恢复 Win10 参数实验，写入后仍需实测"
                        : "；当前 Win10 系统文件检查未通过，未修改系统；查看诊断";
            if (!result.FinalVerified && result.Trials.Count > 0) summary += "（最终稳定性复核未通过）";
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
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { progress.Report("[BLE_AUTO_PRESET] result_save_error=" + ex.GetType().Name); }
            return new(result.Selected != null && result.FinalVerified, result.Code, summary);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            MarkAdaptiveDiagnosticInterrupted(diagnosticCapture, "bounded_timeout");
            compatibilityOptimizationSummary = "预设测试达到时间上限，已结束；按当前真实输入决定是否可游玩。";
            progress.Report("[BLE_AUTO_PRESET] bounded_timeout_s=150 unlimited_retry=false");
            return new(false, "bounded_timeout", compatibilityOptimizationSummary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkAdaptiveDiagnosticInterrupted(diagnosticCapture, "trial_error");
            compatibilityOptimizationSummary = "蓝牙预设测试未完成：" + ex.GetType().Name + "；保留连接并按真实输入显示结果。";
            progress.Report("[BLE_AUTO_PRESET] error_hresult=0x" + ex.HResult.ToString("X8") + " type=" + ex.GetType().Name);
            return new(false, "trial_error", compatibilityOptimizationSummary);
        }
        finally
        {
            if (!diagnosticsCompleted && token.IsCancellationRequested) MarkAdaptiveDiagnosticInterrupted(diagnosticCapture, "cancelled");
            await SaveAdaptiveDiagnosticEvidenceAsync(diagnosticCapture, environmentKey, progress);
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
        BluetoothLEPreferredConnectionParameters? original, AdaptiveDiagnosticCapture diagnosticCapture, IProgress<string> progress)
    {
        string phase = candidate == null ? "restore_original" : "apply_or_retain";
        string source = candidate == null ? "previous_preference" : candidate.IsExperimental ? "own_abi" : "system_preset";
        BluetoothLEPreferredConnectionParametersRequest? pendingRequest = null;
        BluetoothLEPreferredConnectionParameters? parameters = null;
        bool apiCalled = false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            RecordAdaptiveRequest(diagnosticCapture, BleAdaptiveRequestEvidence.FromCandidate(candidate, phase,
                "not_applicable_win10", false, apiCalled: false, parameterSource: source));
            return false;
        }
        try
        {
            Win11CustomConnectionParameters? payload = candidate is { IsExperimental: true }
                ? new(candidate.MinUnits!.Value, candidate.MaxUnits!.Value, candidate.Latency!.Value, candidate.TimeoutUnits!.Value) : null;
            parameters = candidate == null ? original
                : payload?.Parameters ?? BluetoothLEPreferredConnectionParameters.ThroughputOptimized;
            if (parameters == null)
            {
                var released = connectionParametersRequest;
                connectionParametersRequest = null;
                customConnectionParameters = null; retainedConnectionPreference = null;
                bool releaseSucceeded = DisposeAdaptiveRequest(released, "release_native", diagnosticCapture, progress);
                lock (gate) connectionPreferenceStatus = releaseSucceeded
                    ? "adaptive_released_native_interval_not_guaranteed" : "adaptive_native_release_failed";
                RecordAdaptiveRequest(diagnosticCapture, BleAdaptiveRequestEvidence.FromCandidate(null, phase,
                    releaseSucceeded ? "Released" : "ReleaseFailed", null, apiCalled: false, parameterSource: "native_release"));
                progress.Report("[BLE_AUTO_PRESET_REQUEST] restore=native request_released=" + releaseSucceeded + " actual_interval_rollback_not_guaranteed=true");
                return releaseSucceeded;
            }
            apiCalled = true;
            pendingRequest = owner.RequestPreferredConnectionParameters(parameters);
            BluetoothLEPreferredConnectionParametersRequestStatus status = pendingRequest.Status;
            bool accepted = status == BluetoothLEPreferredConnectionParametersRequestStatus.Success;
            RecordAdaptiveRequest(diagnosticCapture, new(candidate?.Id ?? "restore_original", phase,
                parameters.MinConnectionInterval, parameters.MaxConnectionInterval, parameters.ConnectionLatency,
                parameters.LinkTimeout, status.ToString(), accepted, ApiCalled: true, ParameterSource: source));
            progress.Report("[BLE_AUTO_PRESET_REQUEST] preset=" + (candidate?.Id ?? "restore_original") +
                " " + FormatPreferredConnectionParameters(parameters) + " status=" + status +
                " parameter_source=" + (candidate == null ? "previous_preference" : payload == null ? "system_preset" : "own_abi") +
                " actual_fd2_acceptance=pending");
            if (!accepted)
            {
                lock (gate) connectionPreferenceStatus = "adaptive_" + (candidate?.Id ?? "restore") + "_" + status;
                DisposeAdaptiveRequest(pendingRequest, "rejected_request", diagnosticCapture, progress);
                pendingRequest = null;
                return false;
            }
            var previous = connectionParametersRequest;
            connectionParametersRequest = pendingRequest; pendingRequest = null;
            retainedConnectionPreference = parameters; customConnectionParameters = payload;
            lock (gate) connectionPreferenceStatus = "adaptive_" + (candidate?.Id ?? "restore") + "_Success";
            // A cleanup error on the old request is not rejection of the accepted new request.
            DisposeAdaptiveRequest(previous, "previous_request", diagnosticCapture, progress);
            return true;
        }
        catch (Exception ex)
        {
            lock (gate) connectionPreferenceStatus = "adaptive_" + (candidate?.Id ?? "restore") + "_error_0x" + ex.HResult.ToString("X8");
            RecordAdaptiveRequest(diagnosticCapture, BleAdaptiveRequestEvidence.FromCandidate(candidate, phase, "Exception",
                false, ex.HResult, ex.GetType().Name, apiCalled, source));
            DisposeAdaptiveRequest(pendingRequest, "failed_request", diagnosticCapture, progress);
            progress.Report("[BLE_AUTO_PRESET_REQUEST] preset=" + (candidate?.Id ?? "restore_original") +
                " rejected_hresult=0x" + ex.HResult.ToString("X8") + " type=" + ex.GetType().Name);
            return false;
        }
    }

    private bool DisposeAdaptiveRequest(BluetoothLEPreferredConnectionParametersRequest? request,
        string phase, AdaptiveDiagnosticCapture diagnosticCapture, IProgress<string> progress)
    {
        if (request == null) return true;
        try { request.Dispose(); return true; }
        catch (Exception ex)
        {
            RecordAdaptiveRequest(diagnosticCapture, new("cleanup_request", phase, null, null, null, null,
                "CleanupException", null, ex.HResult, ex.GetType().Name, false, "request_release"));
            progress.Report("[BLE_AUTO_PRESET] cleanup_error phase=" + phase + " hresult=0x" + ex.HResult.ToString("X8") +
                " type=" + ex.GetType().Name + " accepted_new_request_not_reclassified=true");
            return false;
        }
    }

    private void ReleaseAdaptiveTrialRequest(AdaptiveDiagnosticCapture diagnosticCapture, IProgress<string> progress)
    {
        var request = connectionParametersRequest;
        connectionParametersRequest = null; retainedConnectionPreference = null; customConnectionParameters = null;
        bool releaseSucceeded = DisposeAdaptiveRequest(request, "release_after_restore_failure", diagnosticCapture, progress);
        lock (gate) connectionPreferenceStatus = releaseSucceeded
            ? "adaptive_request_released_after_restore_failure" : "adaptive_request_release_failed_after_restore_failure";
        RecordAdaptiveRequest(diagnosticCapture, BleAdaptiveRequestEvidence.FromCandidate(null, "release_after_restore_failure",
            releaseSucceeded ? "Released" : "ReleaseFailed", null, apiCalled: false, parameterSource: "request_release"));
        progress.Report("[BLE_AUTO_PRESET] experimental_request_released=" + releaseSucceeded + " actual_interval_rollback_not_guaranteed=true");
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
