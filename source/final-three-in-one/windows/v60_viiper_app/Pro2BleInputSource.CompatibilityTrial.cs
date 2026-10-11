using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;

namespace Y700Switch2V60Viiper;

public sealed record Pro2CompatibilityTrialOutcome(bool Retained, string Code, string Summary);

public sealed partial class Pro2BleInputSource
{
    private bool win11CompatibilityTrialAttempted;
    private CancellationTokenSource? compatibilityTrialCts;
    private BluetoothLEPreferredConnectionParameters? retainedConnectionPreference;

    public bool CanTryWindows11Compatibility
    {
        get
        {
            Pro2RateEvidence rate = GetRateEvidence();
            BleCompatibilitySnapshot compatibility = BleCompatibilityInspector.Current;
            return rate.RecentWindow && rate.LastInputAgeMs is >= 0 and < 500 &&
                rate.ParsedHz >= rate.Fd2Hz * .95 && BleMeasuredTrialPolicy.CanTryInApp(
                OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                compatibility.Plan.CanRequestPreferredParameters, compatibility.Plan.CanReadConnectionParameters,
                rate.Connected && rate.Running, compatibilityTrialCts != null,
                rate.Phase is Pro2ConnectionPhase.Initializing or Pro2ConnectionPhase.Measuring,
                rate.CompatibilityTrialAttempted, rate.Fd2Hz);
        }
    }

    // Explicit in-app consent only. No pairing, descriptor rewrites, registry,
    // driver changes, synthetic reports or successful-hardware certification.
    public async Task<Pro2CompatibilityTrialOutcome> TryWindows11CompatibilityAsync(
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !CanTryWindows11Compatibility)
            return new(false, "not_eligible", "当前连接不需要或不允许再次尝试；稳定高速连接不会被改动。");
        BluetoothLEDevice? owner;
        long sequence;
        lock (gate)
        {
            if (compatibilityTrialCts != null || win11CompatibilityTrialAttempted)
                return new(false, "already_attempted", "本次物理连接已尝试过，不连续修改连接参数。");
            owner = device; sequence = connectionSequence;
            if (owner == null || activeBleDiagnosticCapture != null)
                return new(false, "capture_busy", "已有测量正在进行，请等待完成。");
            win11CompatibilityTrialAttempted = true;
        }
        using var trialCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        trialCts.CancelAfter(TimeSpan.FromSeconds(50));
        compatibilityTrialCts = trialCts;
        bool keep = false, preferenceTouched = false;
        bool batteryEnabled = RuntimeBatteryRefreshEnabled;
        BluetoothLEPreferredConnectionParameters? previousPreference = retainedConnectionPreference;
        bool SameLiveConnection()
        {
            lock (gate)
                return IsRunning && transportConnected && sequence == connectionSequence && ReferenceEquals(owner, device);
        }
        try
        {
            // Disable only the worker's next query; never change persisted user settings.
            SetRuntimeBatteryRefreshEnabled(false);
            while (runtimeBatteryRefreshStatus == "refreshing")
                await Task.Delay(100, trialCts.Token);
            progress.Report("[BLE_COMPAT_TRIAL] consent=explicit once_per_connection=true phase=before duration_s=10 system_writes=false virtual_device_recreated=false");
            BleTrialMeasurement before = await CaptureParameterTrialAsync("IN_APP_BEFORE", 10000, progress, trialCts.Token);
            trialCts.Token.ThrowIfCancellationRequested();
            if (!SameLiveConnection()) return new(false, "connection_changed", "测量期间物理连接已改变，未采用本次结果。");
            if (before.Fd2Hz >= 128 && before.ParsedHz >= 128)
                return new(false, "native_recovered", "原连接已经恢复高速，未发送任何新的参数请求。");
            win11ParameterRouteStatus = "ExplicitInAppTrial";
            preferenceTouched = true;
            if (!TryRequestWindows11CustomParameters(owner, progress))
                return new(false, "request_rejected", "系统拒绝了 7.5ms 请求；这不是手柄损坏的证明。");
            // Do not clear gameplay counters or the sequential input queue.
            BleTrialMeasurement after = await CaptureParameterTrialAsync("IN_APP_AFTER", 10000, progress, trialCts.Token);
            TryCaptureWindows11ConnectionParameters(owner, "in_app_settled", progress);
            BleMeasuredTrialResult decision = BleMeasuredTrialPolicy.Evaluate(
                ToMeasuredTrialSample(before, true), ToMeasuredTrialSample(after, SameLiveConnection()));
            keep = decision.RetainRequest;
            progress.Report("[BLE_COMPAT_TRIAL] before_fd2_hz=" + before.Fd2Hz.ToString("F2") +
                " after_fd2_hz=" + after.Fd2Hz.ToString("F2") + " before_parsed_hz=" + before.ParsedHz.ToString("F2") +
                " after_parsed_hz=" + after.ParsedHz.ToString("F2") + " retained=" + keep + " result=" + decision.Code +
                " stable_133_proven=false synthetic_reports=0");
            return new(keep, decision.Code, decision.Summary + (keep
                ? " 已保留到本次连接结束；继续以真实输入窗口监测稳定性。"
                : " 未改善，不保留本次实验请求。"));
        }
        finally
        {
            if (preferenceTouched && !keep && SameLiveConnection())
            {
                connectionParametersRequest?.Dispose();
                connectionParametersRequest = null;
                customConnectionParameters = null;
                retainedConnectionPreference = null;
                if (previousPreference != null)
                {
                    try
                    {
                        connectionParametersRequest = owner.RequestPreferredConnectionParameters(previousPreference);
                        retainedConnectionPreference = connectionParametersRequest.Status == BluetoothLEPreferredConnectionParametersRequestStatus.Success
                            ? previousPreference : null;
                        connectionPreferenceStatus = "trial_restore_" + connectionParametersRequest.Status;
                    }
                    catch (Exception ex) { connectionPreferenceStatus = "trial_restore_error_0x" + ex.HResult.ToString("X8"); }
                }
                else connectionPreferenceStatus = "trial_released_native";
                progress.Report("[BLE_COMPAT_TRIAL] request_released=true previous_preference_restore_attempted=" +
                    (previousPreference != null) + " reconnect=false registry_written=false");
            }
            if (SameLiveConnection()) SetRuntimeBatteryRefreshEnabled(batteryEnabled);
            if (ReferenceEquals(compatibilityTrialCts, trialCts)) compatibilityTrialCts = null;
            ConnectionStateChanged?.Invoke();
        }
    }

    private BleMeasuredTrialSample ToMeasuredTrialSample(BleTrialMeasurement measurement, bool connected)
    {
        return new(measurement.DurationSeconds, measurement.Fd2Count, measurement.ParsedCount,
            measurement.LastInputAgeMs, connected && measurement.ConnectedAtEnd,
            true, measurement.Gaps.P95Ms, measurement.ConnectionIntervalUnits);
    }
}
