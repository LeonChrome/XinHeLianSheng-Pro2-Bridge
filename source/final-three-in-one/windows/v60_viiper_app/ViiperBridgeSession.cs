using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed class ViiperBridgeSession : IAsyncDisposable
{
    private const ulong PerformanceLogIntervalFrames = 1000;
    private string feedbackSource = "等待游戏反馈";
    public string FeedbackSource => Volatile.Read(ref feedbackSource);
    private long feedbackQueued;
    public long FeedbackQueued => Interlocked.Read(ref feedbackQueued);
    private readonly ViiperProtocolClient client;
    private readonly ViiperDeviceProfile profile;
    private readonly IProgress<string> progress;
    private readonly IProgress<Exception>? faultProgress;
    private readonly IGamepadInputSource? inputSource;
    private readonly IGamepadOutputSink? outputSink;
    private readonly RearButtonMapper rearButtonMapper = new();
    private readonly MotionValidityPolicy motionValidityPolicy = new();
    private RearButtonConfiguration rearButtonConfiguration = new();
    private int rearCancelRequested;
    private readonly IProgress<ProfessionalImuUiSnapshot>? professionalImuUiProgress;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim streamRecoveryGate = new(1, 1);
    private readonly ViiperGyroMode gyroMode;
    private readonly GyroAxisInversion gyroAxisInversion;
    private readonly Ps5ImuMapping ps5ImuMapping;
    private readonly Ps5OutputImuTuning ps5OutputImuTuning;
    private readonly ProfessionalImuOptions professionalImuOptions;
    private readonly ProfessionalHidAuditController professionalHidAuditController;
    private readonly object streamSync = new();
    private ViiperDeviceStream? stream;
    private ViiperDevice? device;
    private ProfessionalImuRuntime? professionalImuRuntime;
    private uint busId;
    private bool createdBus;
    private Task? inputTask;
    private Task? feedbackTask;
    private int faultReported;
    private const int StreamRecoveryAttempts = 5;
    private ulong usbipAttachCount;
    private ulong UsbipDetachCount { get; set; }
    private ulong DeviceRecreateCount { get; set; }
    private ulong viiperStreamDisconnectCount;
    private ulong viiperStreamRecoveryCount;
    private ulong SteamDeviceHashChangeCount { get; set; }
    private string lastViiperError = "";
    private string lastUsbipLifecycleEvent = "none";

    public ViiperBridgeSession(
        ViiperProtocolClient client,
        ViiperDeviceProfile profile,
        IProgress<string> progress,
        IGamepadInputSource? inputSource = null,
        IGamepadOutputSink? outputSink = null,
        IProgress<Exception>? faultProgress = null,
        ViiperGyroMode gyroMode = ViiperGyroMode.Hold250Hz,
        GyroAxisInversion gyroAxisInversion = default,
        Ps5ImuMapping? ps5ImuMapping = null,
        Ps5OutputImuTuning? ps5OutputImuTuning = null,
        ProfessionalImuOptions? professionalImuOptions = null,
        ProfessionalHidAuditController? professionalHidAuditController = null,
        IProgress<ProfessionalImuUiSnapshot>? professionalImuUiProgress = null,
        RearButtonConfiguration? rearButtons = null)
    {
        this.client = client;
        this.profile = profile;
        this.progress = progress;
        this.inputSource = inputSource;
        this.outputSink = outputSink ?? inputSource as IGamepadOutputSink;
        this.faultProgress = faultProgress;
        this.gyroMode = gyroMode;
        this.gyroAxisInversion = gyroAxisInversion;
        this.ps5ImuMapping = ps5ImuMapping ?? Ps5ImuMappingOption.Default.Mapping;
        this.ps5OutputImuTuning = (ps5OutputImuTuning ?? Ps5OutputImuTuning.Default).Normalize();
        this.professionalImuOptions = professionalImuOptions ?? ProfessionalImuOptions.Default;
        this.professionalHidAuditController = professionalHidAuditController ?? new ProfessionalHidAuditController();
        this.professionalImuUiProgress = professionalImuUiProgress;
        rearButtonConfiguration = (rearButtons ?? new()).Normalize();
    }

    public void SetRearButtons(RearButtonConfiguration configuration)
    {
        var next = configuration.Normalize();
        var previous = Interlocked.Exchange(ref rearButtonConfiguration, next);
        if (!RearButtonMapper.ProfilesEquivalent(previous.ForMode(profile.Mode), next.ForMode(profile.Mode)))
            CancelRearButtons();
    }
    public void CancelRearButtons() => Interlocked.Or(ref rearCancelRequested, 1);

    public ViiperDeviceProfile Profile => profile;

    public bool HasProfessionalImuRuntime => professionalImuRuntime != null;

    public static bool ShouldPublishDisconnectNeutral(
        bool sourceIsRunning,
        bool neutralAlreadyPublished) =>
        !sourceIsRunning && !neutralAlreadyPublished;

    public string StartGyroBiasCalibration()
    {
        string result = professionalImuRuntime?.StartGyroBiasCalibration() ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string ResetGyroBias()
    {
        string result = professionalImuRuntime?.ResetGyroBias() ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string ResetProfessionalIntegral()
    {
        string result = professionalImuRuntime?.ResetIntegral() ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string SetProfessionalGyroInversion(bool pitch, bool yaw, bool roll)
    {
        string result = professionalImuRuntime?.SetOutputGyroInversion(pitch, yaw, roll) ??
                        "Professional IMU runtime is not active; inversion will apply on next Professional IMU session.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string StartNinetyDegreeTest(ProfessionalImuTestAxis axis)
    {
        string result = professionalImuRuntime?.StartNinetyDegreeTest(axis) ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string StopNinetyDegreeTest()
    {
        string result = professionalImuRuntime?.StopNinetyDegreeTest() ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        motionValidityPolicy.Reset();
        string ping = await client.PingAsync(cancellationToken);
        progress.Report("[VIIPER] ping " + ping);

        busId = await client.BusCreateAsync(cancellationToken);
        createdBus = true;
        progress.Report("[VIIPER] created dedicated bus " + busId);

        var deviceSpecific = profile.DeviceSpecificArguments();
        device = await client.AddDeviceAsync(
            busId,
            profile.DeviceType,
            deviceSpecific,
            cancellationToken);
        usbipAttachCount = 1;
        lastUsbipLifecycleEvent = "initial_auto_attach";
        progress.Report($"[VIIPER] added {profile.Label} device bus={device.BusId} dev={device.DevId} vid={device.Vid} pid={device.Pid}");
        if (deviceSpecific.Count > 0)
        {
            progress.Report("[VIIPER_DEVICE_SPECIFIC] profile=\"" + profile.Label +
                            "\" serial_number=\"" + profile.DeviceSpecificSerialNumber + "\"" +
                            " source_paced=" + profile.SourcePaced +
                            " usb_motion_policy=" + (profile.Mode == ViiperVirtualMode.Pro2 && profile.SourcePaced
                                ? "4ms_zero_order_hold_source_repeats_counted" : "source_paced_or_explicit_legacy") +
                            " physical_input_rate_scope=fd2_notify_only");
        }
        bool identityOk = profile.MatchesIdentity(device);
        progress.Report(
            "[VIIPER_IDENTITY] profile=\"" + profile.Label +
            "\" expected_type=" + profile.DeviceType +
            " expected_vid=0x" + profile.ExpectedVid +
            " expected_pid=0x" + profile.ExpectedPid +
            " actual_type=" + device.Type +
            " actual_vid=" + device.Vid +
            " actual_pid=" + device.Pid +
            " result=" + (identityOk ? "ok" : "mismatch"));
        if (!identityOk)
        {
            throw new InvalidOperationException(
                "VIIPER returned a virtual device identity that does not match the selected mode. " +
                "profile=" + profile.Label +
                " expected=" + profile.DeviceType + "/0x" + profile.ExpectedVid + ":0x" + profile.ExpectedPid +
                " actual=" + device.Type + "/" + device.Vid + ":" + device.Pid +
                ". Please use 清理残留虚拟设备 and retry.");
        }

        UsbipAttachmentRepairResult singletonResult =
            await ControllerEnumerationDiagnostics.EnsureSingleUsbipAttachmentAsync(
                device,
                client.Host,
                client.Port,
                cancellationToken);
        foreach (string line in singletonResult.Lines)
        {
            progress.Report(line);
        }
        UsbipDetachCount += (ulong)singletonResult.DetachedPortCount;
        usbipAttachCount += (ulong)singletonResult.AttachCount;
        if (singletonResult.DetachedPortCount > 0 || singletonResult.AttachCount > 0)
        {
            lastUsbipLifecycleEvent = "singleton_repair";
        }
        if (singletonResult.FinalPortCount != 1)
        {
            throw new InvalidOperationException(
                "USBIP virtual-controller attachment validation failed. Expected exactly one port, actual=" +
                singletonResult.FinalPortCount + ". Please retry after using 清理残留虚拟设备.");
        }

        SetStream(await client.OpenStreamAsync(device.BusId, device.DevId, cancellationToken));
        if (inputSource is IGamepadSessionInputSource sessionInputSource)
        {
            int discardedFrames = sessionInputSource.PrepareForConsumerSession();
            progress.Report(
                "[INPUT_SESSION] sequential backlog reset before consumer start" +
                " discarded_frames=" + discardedFrames +
                " policy=no_cross_session_replay");
        }
        progress.Report(inputSource is { IsRunning: true }
            ? "[VIIPER] stream connected; feeding Pro2 BLE input."
            : "[VIIPER] stream connected; feeding neutral input until Pro2 BLE source is connected.");
        progress.Report(outputSink is { IsOutputReady: true }
            ? "[VIIPER] Pro2 output writeback is enabled for rumble."
            : "[VIIPER] Pro2 output writeback is not ready; host rumble will be logged only.");
        if (profile.IsPs5Family)
            progress.Report("[PS5_MOTION_VALIDITY] invalid_accel=session_last_valid_gravity" +
                " no_cache_gravity=0,8192,0 invalid_gyro=zero cache=session_local" +
                " cache_reset=start_stop source_state_mutation=false");
        if (profile.IsProfessionalImuTest && professionalImuOptions.Enabled)
        {
            professionalImuRuntime = new ProfessionalImuRuntime(professionalImuOptions, profile.Mode.ToString(), progress);
            progress.Report("[PRO_IMU] enabled " + professionalImuOptions.TelemetryValue +
                            " csv=\"" + professionalImuRuntime.CsvPath + "\"");
            professionalImuUiProgress?.Report(professionalImuRuntime.GetSnapshot());
        }

        inputTask = Task.Run(() => RunLoopAsync("input", InputLoopAsync, cts.Token));
        feedbackTask = Task.Run(() => RunLoopAsync("feedback", FeedbackLoopAsync, cts.Token));
    }

    private void ReportProfessionalSnapshot()
    {
        if (professionalImuRuntime != null)
        {
            professionalImuUiProgress?.Report(professionalImuRuntime.GetSnapshot());
        }
    }

    public async Task StopAsync()
    {
        cts.Cancel();
        ViiperDeviceStream? activeStream = ExchangeStream(null);
        if (activeStream != null)
        {
            await activeStream.DisposeAsync();
        }

        await DrainTaskAsync(inputTask);
        await DrainTaskAsync(feedbackTask);
        rearButtonMapper.Reset();
        motionValidityPolicy.Reset();
        // Stop the physical actuators only after the feedback loop has drained,
        // so no late game frame can overwrite this neutral during mode switch.
        if (outputSink?.IsOutputReady == true)
        {
            var stop = Pro2OutputPacketMapper.BuildOrdinaryPacket(0, 0, "session-stop");
            outputSink.TryWriteOutputReport(stop.Report, out _);
        }
        professionalImuRuntime?.Dispose();
        professionalImuRuntime = null;

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (device != null)
        {
            try
            {
                IReadOnlyList<string> detachLines =
                    await ControllerEnumerationDiagnostics.DetachViiperDeviceAsync(
                        device,
                        client.Host,
                        client.Port,
                        cleanup.Token);
                foreach (string line in detachLines)
                {
                    progress.Report(line);
                }
                if (detachLines.Any(line => line.Contains("confirmed_absent=true", StringComparison.Ordinal)))
                {
                    UsbipDetachCount++;
                    lastUsbipLifecycleEvent = "session_stop_orderly_usbip_detach";
                }
            }
            catch (Exception ex)
            {
                progress.Report("[USBIP_ORDERLY_DETACH] warning=" + ex.Message);
            }

            try
            {
                await client.RemoveDeviceAsync(device.BusId, device.DevId, cleanup.Token);
                if (lastUsbipLifecycleEvent != "session_stop_orderly_usbip_detach")
                {
                    lastUsbipLifecycleEvent = "session_stop_remove_device_fallback";
                }
                progress.Report("[VIIPER] removed device " + device.DevId);
            }
            catch (Exception ex)
            {
                progress.Report("[VIIPER] device cleanup warning: " + ex.Message);
            }
            device = null;
        }

        if (createdBus)
        {
            try
            {
                await client.RemoveBusAsync(busId, cleanup.Token);
                progress.Report("[VIIPER] removed bus " + busId);
            }
            catch (Exception ex)
            {
                progress.Report("[VIIPER] bus cleanup warning: " + ex.Message);
            }
        }
    }

    private async Task InputLoopAsync(CancellationToken cancellationToken)
    {
        bool sourceLatestScheduling =
            profile.SourcePaced &&
            inputSource is IGamepadRealtimeInputSource;
        IGamepadSignaledInputSource? signaledSource = sourceLatestScheduling
            ? inputSource as IGamepadSignaledInputSource : null;
        using WindowsTimerResolutionScope timerResolution =
            WindowsTimerResolutionScope.Begin(signaledSource == null);
        TimeSpan schedulerInterval = sourceLatestScheduling
            ? TimeSpan.FromMilliseconds(1)
            : profile.SendInterval;
        using var timer = signaledSource == null
            ? new HighResolutionPeriodicTimer(schedulerInterval) : null;
        var rateWatch = Stopwatch.StartNew();
        double pushTargetHz = 1.0 / profile.SendInterval.TotalSeconds;
        double effectivePushTargetHz = pushTargetHz;
        long lastVirtualWriteTicks = 0;
        ulong frames = 0;
        ulong lastRateFrames = 0;
        var inputRateWindow = new InputUpdateRateWindow(
            inputSource != null && inputSource.TryGetLatest(out var rateBaseline, out _)
                ? rateBaseline.Updates : 0);
        uint lastSeenUpdates = 0;
        long lastRateTicks = 0;
        long lastLoopTicks = 0;
        uint? lastPushedUpdate = null;
        uint lastMotionUpdate = 0;
        GamepadState? filteredMotionState = null;
        ulong statePushCount = 0;
        ulong stateReuseCount = 0;
        ulong currentSameStatePushes = 0;
        ulong sameStatePushMax = 0;
        ulong age0To20 = 0;
        ulong age20To33 = 0;
        ulong age33To50 = 0;
        ulong age50To100 = 0;
        ulong ageOver100 = 0;
        ulong realtimeSupersededTotal = 0;
        ulong realtimeFreshWrites = 0;
        bool sourceNeutralPublished = false;
        bool sourceGapHoldLogged = false;
        GamepadState? pendingRearRelease = null;
        async Task<bool> FlushRearReleaseAsync(ViiperDeviceStream current)
        {
            if (pendingRearRelease == null) return true;
            try
            {
                // An exceptional control-release frame, not a new measured FD2
                // input. Do not add it to frames/Hz/IMU telemetry counters.
                await current.WriteAsync(MapInputPacket(pendingRearRelease), cancellationToken).ConfigureAwait(false);
                pendingRearRelease = null;
                progress.Report("[REAR_SAFETY_RELEASE] mapped_controls_only=true physical_keys_preserved=true counted_as_fd2=false");
                return true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Retain the release until the reopened stream accepts it.
                if (await RecoverStreamAsync("rear_release", current, ex, cancellationToken).ConfigureAwait(false)) return false;
                throw;
            }
        }
        var intervalSourceAgeSamples = new List<double>((int)PerformanceLogIntervalFrames);
        var intervalLoopGapSamples = new List<double>((int)PerformanceLogIntervalFrames);
        var intervalWriteSamplesMs = new List<double>((int)PerformanceLogIntervalFrames);
        double intervalWriteMsSum = 0;
        double intervalWriteMaxMs = 0;
        ulong intervalWriteSamples = 0;
        ulong intervalWriteOver2Ms = 0;
        ulong intervalWriteOver8Ms = 0;
        double intervalLoopGapMaxMs = 0;
        ulong intervalLoopGapOver8Ms = 0;
        ulong intervalLoopGapOver16Ms = 0;
        double intervalSourceAgeMaxMs = 0;
        string lastSource = "";
        bool inputRateDegraded = false;
        string lastProfessionalImuTelemetry = "";
        string lastProfessionalHidAuditTelemetry = "";
        long lastProfessionalHidAuditLogTicks = 0;
        var imuTelemetry = new VirtualImuTelemetryStats();
        string timerLine =
            "[VIIPER_TIMER] requested_ms=" + (signaledSource == null ? "1" : "0") +
            " active=" + timerResolution.IsActive +
            " result=" + timerResolution.Result +
            " backend=" + (timer?.Backend ?? "notify_signal") +
            " scheduler=" + (signaledSource != null ? "source_notify_edges" :
                sourceLatestScheduling ? "source_latest_1ms" : "fixed_latest") +
            " scheduler_interval_ms=" + (signaledSource != null ? "event" :
                schedulerInterval.TotalMilliseconds.ToString("F1")) +
            " push_target_hz=" + (sourceLatestScheduling ? "ble_source" : pushTargetHz.ToString("F1")) +
            " report_rate_mode=" + professionalImuOptions.OutputReportRateMode +
            " gyro_mode=" + GyroModeLabel(gyroMode) +
            " gyro_axis_inv=" + gyroAxisInversion.TelemetryValue;
        if (profile.IsPs5Family)
        {
            timerLine +=
                " ps5_imu_map=" + ps5ImuMapping.TelemetryValue +
                " ps5_output_imu=" + ps5OutputImuTuning.TelemetryValue;
        }
        progress.Report(timerLine);
        while (!cancellationToken.IsCancellationRequested &&
               (signaledSource != null
                   ? await WaitForSourceAsync(signaledSource, cancellationToken).ConfigureAwait(false)
                   : timer!.WaitForNextTick(cancellationToken)))
        {
            long loopTicks = Stopwatch.GetTimestamp();
            if (lastLoopTicks != 0)
            {
                double loopGapMs = TicksToMilliseconds(loopTicks - lastLoopTicks);
                intervalLoopGapMaxMs = Math.Max(intervalLoopGapMaxMs, loopGapMs);
                if (loopGapMs >= 8)
                {
                    intervalLoopGapOver8Ms++;
                }
                if (loopGapMs >= 16)
                {
                    intervalLoopGapOver16Ms++;
                }
                intervalLoopGapSamples.Add(loopGapMs);
            }
            lastLoopTicks = loopTicks;

            double sourceRateHz = inputSource is IGamepadInputRateSource rateSource
                ? rateSource.CurrentParsedRateHz
                : 0;
            effectivePushTargetHz = sourceLatestScheduling
                ? sourceRateHz
                : professionalImuOptions.AutoReduceVirtualReportRate &&
                professionalImuOptions.OutputReportRateMode == OutputReportRateMode.AutoByBleSourceRate
                    ? VirtualReportRateGovernor.SelectAutoRateHz(sourceRateHz, pushTargetHz)
                    : pushTargetHz;

            ViiperDeviceStream? current = GetStream();
            if (current == null)
            {
                // The feedback loop may temporarily clear the stream while
                // reopening it. Do not silently terminate the input consumer.
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                continue;
            }
            int rearCancel = Interlocked.Exchange(ref rearCancelRequested, 0);
            if (rearCancel != 0 &&
                rearButtonMapper.TryCancelOutput(out var requestedRelease, forceRelease: (rearCancel & 2) != 0))
                pendingRearRelease = requestedRelease;
            if (!await FlushRearReleaseAsync(current).ConfigureAwait(false)) continue;

            byte[] packet;
            string source;
            GamepadState? packetSourceState = null;
            ProfessionalImuFrame professionalFrame = default;
            GamepadState state = GamepadState.Neutral();
            TimeSpan age = TimeSpan.MaxValue;
            bool hasInput;
            if (sourceLatestScheduling &&
                inputSource is IGamepadRealtimeInputSource realtimeLatest)
            {
                hasInput = realtimeLatest.TryGetNewest(
                    out state,
                    out age,
                    out int supersededCount);
                realtimeSupersededTotal += (ulong)Math.Max(0, supersededCount);
                if (!hasInput &&
                    lastVirtualWriteTicks == 0 &&
                    inputSource != null &&
                    inputSource.TryGetLatest(out GamepadState initialLatest, out TimeSpan initialAge))
                {
                    state = initialLatest;
                    age = initialAge;
                    hasInput = true;
                }
                if (!hasInput && lastVirtualWriteTicks != 0)
                {
                    bool sourceIsRunning = inputSource?.IsRunning == true;
                    if (rearButtonMapper.HasPendingAction && (!sourceIsRunning ||
                        (inputSource!.TryGetLatest(out _, out TimeSpan rearInputAge) && rearInputAge > TimeSpan.FromMilliseconds(100))))
                    {
                        if (rearButtonMapper.TryCancelOutput(out var staleRearRelease)) pendingRearRelease = staleRearRelease;
                        progress.Report("[REAR_SAFETY_CANCEL] reason=input_gap_or_disconnect rearm=release_then_press");
                        if (!await FlushRearReleaseAsync(current).ConfigureAwait(false)) continue;
                    }
                    if (!ShouldPublishDisconnectNeutral(
                            sourceIsRunning,
                            sourceNeutralPublished))
                    {
                        if (sourceIsRunning &&
                            !sourceGapHoldLogged &&
                            inputSource!.TryGetLatest(out _, out TimeSpan latestAge) &&
                            latestAge > TimeSpan.FromMilliseconds(100))
                        {
                            progress.Report(
                                "[INPUT_GAP_HOLD] source_age_ms=" +
                                latestAge.TotalMilliseconds.ToString("F1") +
                                " controls=hold_latest neutral_injected=false");
                            sourceGapHoldLogged = true;
                        }
                        continue;
                    }
                }

                if (hasInput)
                {
                    realtimeFreshWrites++;
                    sourceNeutralPublished = false;
                    sourceGapHoldLogged = false;
                }
                else
                {
                    sourceNeutralPublished = true;
                    sourceGapHoldLogged = false;
                    progress.Report(
                        "[INPUT_DISCONNECT_NEUTRAL] source_running=false sent_once=true");
                }
            }
            else
            {
                if (inputSource is IGamepadRealtimeInputSource latestSource &&
                    latestSource.TryGetNewest(
                        out state,
                        out age,
                        out int supersededCount))
                {
                    hasInput = true;
                    realtimeSupersededTotal += (ulong)Math.Max(0, supersededCount);
                }
                else
                {
                    hasInput = inputSource != null &&
                        inputSource.TryGetLatest(out state, out age);
                }
            }
            if (hasInput)
            {
                lastSeenUpdates = state.Updates;
                bool reusedState = lastPushedUpdate.HasValue &&
                                   state.Updates == lastPushedUpdate.Value;
                statePushCount++;
                if (reusedState)
                {
                    stateReuseCount++;
                    currentSameStatePushes++;
                }
                else
                {
                    currentSameStatePushes = 1;
                    lastPushedUpdate = state.Updates;
                }
                sameStatePushMax = Math.Max(sameStatePushMax, currentSameStatePushes);

                if (age < TimeSpan.FromMinutes(1))
                {
                    double ageMs = age.TotalMilliseconds;
                    intervalSourceAgeMaxMs = Math.Max(intervalSourceAgeMaxMs, ageMs);
                    intervalSourceAgeSamples.Add(ageMs);
                    CountAgeBucket(
                        ageMs,
                        ref age0To20,
                        ref age20To33,
                        ref age33To50,
                        ref age50To100,
                        ref ageOver100);
                }
                GamepadState continuous =
                    InputContinuityPolicy.Resolve(state, age, out source);
                if (profile.IsProfessionalImuTest && professionalImuRuntime != null)
                {
                    professionalFrame = professionalImuRuntime.Process(state, age.TotalMilliseconds);
                    lastProfessionalImuTelemetry = professionalFrame.Telemetry;
                    if (professionalFrame.UiSnapshot != null)
                    {
                        professionalImuUiProgress?.Report(professionalFrame.UiSnapshot);
                    }
                }
                continuous = ApplyGyroMode(
                    continuous,
                    state.Updates,
                    effectivePushTargetHz,
                    ref lastMotionUpdate,
                    ref filteredMotionState);
                GamepadState physicalControls = continuous;
                if (inputSource?.IsRunning == true && age <= TimeSpan.FromMilliseconds(100))
                    continuous = rearButtonMapper.Apply(continuous, profile.Mode,
                        Volatile.Read(ref rearButtonConfiguration), loopTicks);
                else
                    rearButtonMapper.Reset();
                packetSourceState = continuous;
                packet = MapInputPacket(
                    continuous,
                    professionalFrame.DualSenseRaw,
                    ReferenceEquals(physicalControls, continuous) ? professionalFrame.XboxState :
                        RearButtonMapper.MergeProfessionalControls(continuous, professionalFrame.XboxState));
            }
            else
            {
                rearButtonMapper.Reset();
                currentSameStatePushes = 0;
                packet = MapInputPacket(null);
                source = "neutral";
            }

            if (!sourceLatestScheduling &&
                ShouldSkipVirtualWrite(loopTicks, lastVirtualWriteTicks, effectivePushTargetHz, pushTargetHz))
            {
                continue;
            }

            if (profile.Mode == ViiperVirtualMode.DualSenseProfessionalImuTest &&
                professionalImuRuntime != null &&
                packetSourceState is { GyroValid: true, AccelValid: true } &&
                professionalFrame.DualSenseRaw is { } professionalDualSenseRaw)
            {
                ProfessionalHidAuditControlState auditControl =
                    professionalHidAuditController.Snapshot(loopTicks, out string? auditEvent);
                if (!string.IsNullOrWhiteSpace(auditEvent))
                {
                    progress.Report(auditEvent);
                }

                ProfessionalHidAuditSnapshot audit =
                    ProfessionalHidReportAuditor.ApplyAndAudit(
                        packet,
                        professionalDualSenseRaw,
                        professionalFrame.OutputPhysical,
                        auditControl);
                professionalImuRuntime.CommitFinalHidAudit(audit);
                lastProfessionalHidAuditTelemetry = audit.Summary;

                bool auditMismatch = audit.Result is
                    ProfessionalHidAuditResult.MISMATCH_GYRO or
                    ProfessionalHidAuditResult.MISMATCH_ACCEL or
                    ProfessionalHidAuditResult.REPORT_TOO_SHORT or
                    ProfessionalHidAuditResult.OFFSET_UNKNOWN or
                    ProfessionalHidAuditResult.WRONG_BUILDER;
                long auditElapsedTicks = rateWatch.ElapsedTicks;
                bool auditDue =
                    auditMismatch ||
                    frames % PerformanceLogIntervalFrames == 0 ||
                    auditElapsedTicks - lastProfessionalHidAuditLogTicks >= Stopwatch.Frequency * 5L;
                if (auditDue)
                {
                    lastProfessionalHidAuditLogTicks = auditElapsedTicks;
                    progress.Report("[PRO_IMU_AUDIT] " + audit.Summary);
                    if (auditMismatch)
                    {
                        progress.Report("[PRO_IMU_AUDIT_ERROR] final HID gyro mismatch " + audit.Detail);
                    }
                    else if (audit.Result == ProfessionalHidAuditResult.OK)
                    {
                        progress.Report("[PRO_IMU_AUDIT_OK] final HID gyro matches selected output. " + audit.Summary);
                    }
                }
            }

            try
            {
                long writeStartTicks = Stopwatch.GetTimestamp();
                await current.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                lastVirtualWriteTicks = Stopwatch.GetTimestamp();
                imuTelemetry.Record(profile, packet, packetSourceState);
                double writeMs = TicksToMilliseconds(lastVirtualWriteTicks - writeStartTicks);
                intervalWriteSamples++;
                intervalWriteMsSum += writeMs;
                intervalWriteSamplesMs.Add(writeMs);
                intervalWriteMaxMs = Math.Max(intervalWriteMaxMs, writeMs);
                if (writeMs >= 2)
                {
                    intervalWriteOver2Ms++;
                }
                if (writeMs >= 8)
                {
                    intervalWriteOver8Ms++;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (await RecoverStreamAsync("input", current, ex, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                throw;
            }
            frames++;
            long elapsedRateTicks = rateWatch.ElapsedTicks;
            bool rateDue =
                frames % PerformanceLogIntervalFrames == 0 ||
                (frames > lastRateFrames &&
                 elapsedRateTicks - lastRateTicks >= Stopwatch.Frequency * 10L);
            string sourceFamily = SourceFamily(source);
            bool sourceChanged = sourceFamily != lastSource;
            if (rateDue || sourceChanged)
            {
                lastSource = sourceFamily;
                string rateSummary =
                    "target_hz=" + (sourceLatestScheduling ? sourceRateHz : effectivePushTargetHz).ToString("F1") +
                    " configured_target_hz=" + (sourceLatestScheduling ? "source" : pushTargetHz.ToString("F1")) +
                    " source_rate_hz=" + sourceRateHz.ToString("F1") +
                    " report_rate_mode=" + professionalImuOptions.OutputReportRateMode;
                if (rateDue)
                {
                    long nowTicks = rateWatch.ElapsedTicks;
                    double elapsedSeconds = (nowTicks - lastRateTicks) / (double)Stopwatch.Frequency;
                    double actualHz = elapsedSeconds > 0
                        ? (frames - lastRateFrames) / elapsedSeconds
                        : 0;
                    if (inputSource is IGamepadRuntimeTelemetrySink telemetrySink)
                    {
                        telemetrySink.ReportViiperPushRate(actualHz);
                    }
                    double bleHz = inputRateWindow.Measure(lastSeenUpdates, elapsedSeconds);
                    double stateReuseRatio = statePushCount == 0
                        ? 0
                        : stateReuseCount / (double)statePushCount;
                    if (PureSoftware133Mode.IsEnabled &&
                        stateReuseRatio > PureSoftware133Mode.MaximumRepeatedStateRatio)
                    {
                        throw new InvalidOperationException(
                            "纯软件真实输入转发校验失败（不允许补帧）：source_hz=" +
                            sourceRateHz.ToString("F1") +
                            " output_hz=" + actualHz.ToString("F1") +
                            " repeated_state_ratio=" + stateReuseRatio.ToString("F3"));
                    }
                    bool degraded = PureSoftware133Mode.IsEnabled &&
                        (bleHz < PureSoftware133Mode.MinimumAcceptedHz ||
                         actualHz < PureSoftware133Mode.MinimumAcceptedHz);
                    if (degraded != inputRateDegraded)
                    {
                        inputRateDegraded = degraded;
                        progress.Report(degraded
                            ? "[VIIPER_RATE_DEGRADED] real input/output below 133Hz target; compatible gameplay keeps fresh-only output alive, no synthetic frames."
                            : "[VIIPER_RATE_RECOVERED] real input/output returned above acceptance threshold; same virtual device retained.");
                    }
                    lastRateTicks = nowTicks;
                    lastRateFrames = frames;
                    rateSummary +=
                        " actual_hz=" + actualHz.ToString("F1") +
                        " ble_hz=" + bleHz.ToString("F1") +
                        " viiper_push_hz=" + actualHz.ToString("F1") +
                        " scheduler=" + (sourceLatestScheduling ? "source_latest_1ms" : "fixed_latest") +
                        (sourceLatestScheduling
                            ? " host_poll_cap_hz="
                            : " host_hid_target_hz=") +
                        pushTargetHz.ToString("F1") +
                        " realtime_fresh_writes=" + realtimeFreshWrites +
                        " realtime_superseded_total=" + realtimeSupersededTotal +
                        " state_reuse_count=" + stateReuseCount +
                        " state_reuse_ratio=" + stateReuseRatio.ToString("F3") +
                        " same_state_push_max=" + sameStatePushMax +
                        " source_age_bucket=0_20:" + age0To20 +
                        ",20_33:" + age20To33 +
                        ",33_50:" + age33To50 +
                        ",50_100:" + age50To100 +
                        ",gt100:" + ageOver100 +
                        " source_age_bucket_0_20=" + age0To20 +
                        " source_age_bucket_20_33=" + age20To33 +
                        " source_age_bucket_33_50=" + age33To50 +
                        " source_age_bucket_50_100=" + age50To100 +
                        " source_age_bucket_over100=" + ageOver100;
                    double avgWriteMs = intervalWriteSamples == 0
                        ? 0
                        : intervalWriteMsSum / intervalWriteSamples;
                    rateSummary +=
                        " loop_gap_max_ms=" + intervalLoopGapMaxMs.ToString("F2") +
                        " loop_gap_p95_ms=" + Percentile(intervalLoopGapSamples, 95).ToString("F2") +
                        " loop_gap_p99_ms=" + Percentile(intervalLoopGapSamples, 99).ToString("F2") +
                        " loop_gap_over8=" + intervalLoopGapOver8Ms +
                        " loop_gap_over16=" + intervalLoopGapOver16Ms +
                        " viiper_write_avg_ms=" + avgWriteMs.ToString("F3") +
                        " viiper_write_max_ms=" + intervalWriteMaxMs.ToString("F3") +
                        " viiper_write_p95_ms=" + Percentile(intervalWriteSamplesMs, 95).ToString("F3") +
                        " viiper_write_p99_ms=" + Percentile(intervalWriteSamplesMs, 99).ToString("F3") +
                        " viiper_write_over2=" + intervalWriteOver2Ms +
                        " viiper_write_over8=" + intervalWriteOver8Ms +
                        " source_age_max_ms=" + intervalSourceAgeMaxMs.ToString("F1") +
                        " source_age_p95_ms=" + Percentile(intervalSourceAgeSamples, 95).ToString("F1") +
                        " source_age_p99_ms=" + Percentile(intervalSourceAgeSamples, 99).ToString("F1") +
                        " usbip_attach_count=" + usbipAttachCount +
                        " usbip_detach_count=" + UsbipDetachCount +
                        " device_recreate_count=" + DeviceRecreateCount +
                        " viiper_stream_disconnect_count=" + viiperStreamDisconnectCount +
                        " viiper_stream_recovery_count=" + viiperStreamRecoveryCount +
                        " steam_device_hash_change_count=" + SteamDeviceHashChangeCount +
                        " last_viiper_error=\"" + SanitizeLogValue(lastViiperError) + "\"" +
                        " last_usbip_lifecycle_event=" + lastUsbipLifecycleEvent +
                        " gyro_axis_inv=" + gyroAxisInversion.TelemetryValue;
                    if (profile.IsPs5Family)
                    {
                        string ps5ImuPipe = imuTelemetry.Ps5PipelineSummary();
                        rateSummary += " ps5_imu_map=" + ps5ImuMapping.TelemetryValue;
                        rateSummary += " " + imuTelemetry.SummaryAndMaybeReset(rateDue);
                        if (rateDue && !string.IsNullOrWhiteSpace(ps5ImuPipe))
                        {
                            progress.Report("[PS5_IMU_PIPE] " + ps5ImuPipe);
                        }
                    }
                    else if (profile.Mode == ViiperVirtualMode.Pro2)
                    {
                        rateSummary += " " + imuTelemetry.SummaryAndMaybeReset(rateDue);
                    }
                    if (profile.IsProfessionalImuTest)
                    {
                        rateSummary += " " + SanitizeLogValue(lastProfessionalImuTelemetry);
                        if (!string.IsNullOrWhiteSpace(lastProfessionalHidAuditTelemetry))
                        {
                            rateSummary += " hid_audit=\"" + SanitizeLogValue(lastProfessionalHidAuditTelemetry) + "\"";
                        }
                    }
                    intervalWriteMsSum = 0;
                    intervalWriteMaxMs = 0;
                    intervalWriteSamples = 0;
                    intervalWriteOver2Ms = 0;
                    intervalWriteOver8Ms = 0;
                    intervalLoopGapMaxMs = 0;
                    intervalLoopGapOver8Ms = 0;
                    intervalLoopGapOver16Ms = 0;
                    intervalLoopGapSamples.Clear();
                    intervalSourceAgeMaxMs = 0;
                    intervalSourceAgeSamples.Clear();
                    intervalWriteSamplesMs.Clear();
                    statePushCount = 0;
                    stateReuseCount = 0;
                    sameStatePushMax = currentSameStatePushes;
                    age0To20 = 0;
                    age20To33 = 0;
                    age33To50 = 0;
                    age50To100 = 0;
                    ageOver100 = 0;
                }

                string inputMetrics = inputSource is IGamepadInputMetricsSource metrics
                    ? " " + metrics.MetricsSummary
                    : "";
                progress.Report("[VIIPER] fed " + frames + " " + source + " " + profile.Label +
                                " frames " + rateSummary + inputMetrics);
            }
        }
    }

    private byte[] MapInputPacket(
        GamepadState? source,
        DualSenseImuRawSample? professionalDualSenseImu = null,
        GamepadState? professionalXboxState = null)
    {
        GamepadState? output = source;
        if (profile.IsPs5Family)
        {
            output = motionValidityPolicy.Resolve(source);
            // An independently cached/diagnostic raw sample must not bypass
            // the invalid-gyro stop or session-local gravity policy.
            if (source == null || !source.GyroValid || !source.AccelValid)
                professionalDualSenseImu = null;
        }
        return VirtualPadPackets.FromGamepad(
            profile, output, gyroAxisInversion, ps5ImuMapping, ps5OutputImuTuning,
            professionalDualSenseImu, professionalXboxState);
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        double position = (sorted.Length - 1) * Math.Clamp(percentile, 0, 100) / 100.0;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double weight = position - lower;
        return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
    }

    private static void CountAgeBucket(
        double ageMs,
        ref ulong age0To20,
        ref ulong age20To33,
        ref ulong age33To50,
        ref ulong age50To100,
        ref ulong ageOver100)
    {
        switch (ageMs)
        {
            case < 20:
                age0To20++;
                break;
            case < 33:
                age20To33++;
                break;
            case < 50:
                age33To50++;
                break;
            case < 100:
                age50To100++;
                break;
            default:
                ageOver100++;
                break;
        }
    }

    private static bool ShouldSkipVirtualWrite(
        long nowTicks,
        long lastWriteTicks,
        double effectivePushTargetHz,
        double configuredPushTargetHz)
    {
        if (lastWriteTicks == 0 ||
            effectivePushTargetHz <= 0 ||
            effectivePushTargetHz >= configuredPushTargetHz - 0.1)
        {
            return false;
        }

        double minimumGapMs = 1000.0 / effectivePushTargetHz;
        double elapsedMs = TicksToMilliseconds(nowTicks - lastWriteTicks);
        return elapsedMs < minimumGapMs * 0.92;
    }

    private GamepadState ApplyGyroMode(
        GamepadState state,
        uint sourceUpdate,
        double pushTargetHz,
        ref uint lastMotionUpdate,
        ref GamepadState? filteredMotionState)
    {
        switch (gyroMode)
        {
            case ViiperGyroMode.Hold250Hz:
                lastMotionUpdate = sourceUpdate;
                return state;
            case ViiperGyroMode.Source60Hz:
                if (sourceUpdate == lastMotionUpdate)
                {
                    return WithoutMotion(state);
                }
                lastMotionUpdate = sourceUpdate;
                return state;
            case ViiperGyroMode.Scaled250Hz:
                lastMotionUpdate = sourceUpdate;
                return ScaleGyro(
                    state,
                    Math.Clamp(66.7 / Math.Max(pushTargetHz, 1.0), 0.25, 1.0));
            case ViiperGyroMode.Filtered250Hz:
                lastMotionUpdate = sourceUpdate;
                filteredMotionState = FilterMotion(filteredMotionState, state);
                return CopyMotion(state, filteredMotionState);
            default:
                lastMotionUpdate = sourceUpdate;
                return state;
        }
    }

    private static GamepadState WithoutMotion(GamepadState state)
    {
        GamepadState result = state.Clone();
        result.GyroValid = false;
        result.AccelValid = false;
        result.GyroX = 0;
        result.GyroY = 0;
        result.GyroZ = 0;
        result.AccelX = 0;
        result.AccelY = 0;
        result.AccelZ = 0;
        return result;
    }

    private static GamepadState ScaleGyro(GamepadState state, double scale)
    {
        if (!state.GyroValid || Math.Abs(scale - 1.0) < 0.001)
        {
            return state;
        }

        GamepadState result = state.Clone();
        result.GyroX = ScaleInt16(result.GyroX, scale);
        result.GyroY = ScaleInt16(result.GyroY, scale);
        result.GyroZ = ScaleInt16(result.GyroZ, scale);
        return result;
    }

    private static GamepadState FilterMotion(GamepadState? previous, GamepadState current)
    {
        if (previous == null)
        {
            return current.Clone();
        }

        const double alpha = 0.35;
        GamepadState filtered = current.Clone();
        if (current.GyroValid && previous.GyroValid)
        {
            filtered.GyroX = LerpInt16(previous.GyroX, current.GyroX, alpha);
            filtered.GyroY = LerpInt16(previous.GyroY, current.GyroY, alpha);
            filtered.GyroZ = LerpInt16(previous.GyroZ, current.GyroZ, alpha);
        }
        if (current.AccelValid && previous.AccelValid)
        {
            filtered.AccelX = LerpInt16(previous.AccelX, current.AccelX, alpha);
            filtered.AccelY = LerpInt16(previous.AccelY, current.AccelY, alpha);
            filtered.AccelZ = LerpInt16(previous.AccelZ, current.AccelZ, alpha);
        }
        return filtered;
    }

    private static GamepadState CopyMotion(GamepadState current, GamepadState? motion)
    {
        if (motion == null)
        {
            return current;
        }

        GamepadState result = current.Clone();
        result.GyroValid = motion.GyroValid;
        result.GyroX = motion.GyroX;
        result.GyroY = motion.GyroY;
        result.GyroZ = motion.GyroZ;
        result.AccelValid = motion.AccelValid;
        result.AccelX = motion.AccelX;
        result.AccelY = motion.AccelY;
        result.AccelZ = motion.AccelZ;
        result.MotionTimestampUs = motion.MotionTimestampUs;
        return result;
    }

    private static short ScaleInt16(short value, double scale)
    {
        return ClampInt16((int)Math.Round(value * scale));
    }

    private static short LerpInt16(short previous, short current, double alpha)
    {
        return ClampInt16((int)Math.Round(previous + (current - previous) * alpha));
    }

    private static short ClampInt16(int value)
    {
        if (value < short.MinValue) return short.MinValue;
        if (value > short.MaxValue) return short.MaxValue;
        return (short)value;
    }

    private static string GyroModeLabel(ViiperGyroMode mode)
    {
        return mode switch
        {
            ViiperGyroMode.Hold250Hz => "hold_latest",
            ViiperGyroMode.Source60Hz => "source_60hz_zero",
            ViiperGyroMode.Scaled250Hz => "scaled_250hz",
            ViiperGyroMode.Filtered250Hz => "filtered_hold",
            _ => mode.ToString()
        };
    }

    private static string SourceFamily(string source)
    {
        if (source.StartsWith("pro2_ble_age_", StringComparison.Ordinal))
        {
            return "pro2_ble_live";
        }
        return source;
    }

    private static async Task<bool> WaitForSourceAsync(
        IGamepadSignaledInputSource source, CancellationToken token)
    {
        // Idle checks release stalled rear macros. They never create fresh input
        // or generate a timer-driven repeat stream; active delivery is event driven.
        await source.WaitForInputAsync(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
        return !token.IsCancellationRequested;
    }

    private async Task FeedbackLoopAsync(CancellationToken cancellationToken)
    {
        DualSenseHapticRumbleScheduler? dualSenseScheduler =
            profile.UsesDualSenseHaptics
                ? new DualSenseHapticRumbleScheduler(Pro2QualityMode.HapticsEnabled)
                : null;
        ulong frames = 0;
        progress.Report("[QUALITY_PIPELINE] gyro_quality=" + Pro2QualityMode.GyroEnabled +
            " haptics_quality=" + Pro2QualityMode.HapticsEnabled +
            " native_pcm_supported=" + profile.UsesDualSenseHaptics +
            " input_rate_path=unchanged native_pro2=payload_preserving_ordered" +
            " pcm_idle_guard=independent_120ms guard_check_max_wait_ms=40" +
            " host_sensor_enable=not_observable_in_feedback_stream");
        ulong writes = 0;
        ulong failures = 0;
        Dictionary<byte, string> lastFeedbackSummaryByKind = new();
        Dictionary<byte, ulong> feedbackFramesByKind = new();
        string lastOutputState = "";
        HashSet<string> reportedSkipReasons = new(StringComparer.Ordinal);
        Task<byte[]>? pendingRead = null;
        long lastHapticTelemetry = Stopwatch.GetTimestamp();
        void QueueFeedbackStop(string source, bool idleOnly)
        {
            Pro2OutputPacket stop = default!;
            bool needed = dualSenseScheduler != null && (idleOnly
                ? dualSenseScheduler.TryStopIdle(out stop)
                : dualSenseScheduler.TryStop(source, out stop));
            if (!needed) return;
            Volatile.Write(ref feedbackSource, "触觉音频通道 · 待信号");
            bool queued = outputSink?.IsOutputReady == true &&
                outputSink.TryWriteOutputPacket(stop, out _);
            if (queued) Interlocked.Increment(ref feedbackQueued);
            progress.Report("[HAPTIC_STOP] source=" + stop.Source + " queued=" + queued +
                " " + dualSenseScheduler!.StateTelemetry);
        }
        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Check the PCM clock on every pass, even while LED/HID traffic is
            // continuous. Unrelated host messages must not postpone PCM stop.
            QueueFeedbackStop("dualsense-hd-idle-stop", idleOnly: true);
            if (dualSenseScheduler != null &&
                Stopwatch.GetElapsedTime(lastHapticTelemetry).TotalSeconds >= 5)
            {
                lastHapticTelemetry = Stopwatch.GetTimestamp();
                progress.Report("[PS5_FEEDBACK_STATE] " + dualSenseScheduler.StateTelemetry);
            }
            ViiperDeviceStream? current = GetStream();
            if (current == null)
            {
                return;
            }

            byte[] feedback;
            try
            {
                pendingRead ??= current.ReadExactAsync(profile.FeedbackSize, cancellationToken);
                if (dualSenseScheduler != null)
                {
                    try
                    {
                        feedback = await pendingRead.WaitAsync(
                            TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Do not cancel or replace the outstanding read: otherwise
                        // the next frame could be consumed by an abandoned task.
                        QueueFeedbackStop("dualsense-hd-idle-stop", idleOnly: true);
                        continue;
                    }
                }
                else
                    feedback = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                pendingRead = null;
                QueueFeedbackStop("dualsense-feedback-stream-lost-stop", idleOnly: false);
                if (await RecoverStreamAsync("feedback", current, ex, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                throw;
            }
            frames++;
            Pro2OutputPacket packet;
            string reason;
            bool mapped;
            string feedbackSummary;
            if (dualSenseScheduler != null)
            {
                mapped = dualSenseScheduler.TryProcess(
                    feedback,
                    out packet,
                    out feedbackSummary,
                    out reason);
                Volatile.Write(ref feedbackSource,
                    dualSenseScheduler.CompatibilityMode ? "普通震动 · 游戏反馈" :
                    dualSenseScheduler.NativeOutputActive ? "原生触觉音频 · 转换中" :
                    dualSenseScheduler.HasNativeAudio ? "触觉音频通道 · 待信号" : "等待原生触觉音频");
            }
            else
            {
                feedbackSummary = VirtualPadPackets.FeedbackSummary(profile, feedback);
                mapped = Pro2OutputPacketMapper.TryMapFeedback(
                    profile,
                    feedback,
                    out packet,
                    out reason);
                if (mapped)
                    Volatile.Write(ref feedbackSource, profile.Mode switch
                    {
                        ViiperVirtualMode.Pro2 => "Pro2 原始震动",
                        ViiperVirtualMode.Pro1 => "Pro1 震动 · 转换中",
                        _ => "普通震动 · 游戏反馈"
                    });
            }
            byte feedbackKind = dualSenseScheduler != null && feedback.Length > 0
                ? feedback[0]
                : (byte)0;
            ulong kindFrames = feedbackFramesByKind.TryGetValue(feedbackKind, out ulong previousKindFrames)
                ? previousKindFrames + 1
                : 1;
            feedbackFramesByKind[feedbackKind] = kindFrames;
            if (dualSenseScheduler != null && Pro2QualityMode.HapticsEnabled &&
                feedbackKind == (byte)DualSenseHapticFrameKind.AudioPcm && kindFrames % 1000 == 0)
                progress.Report("[HAPTIC_CONVERSION] " + dualSenseScheduler.ConversionTelemetry);
            bool summaryChanged =
                !lastFeedbackSummaryByKind.TryGetValue(feedbackKind, out string? lastFeedbackSummary) ||
                feedbackSummary != lastFeedbackSummary;
            ulong periodicLogFrames = feedbackKind == (byte)DualSenseHapticFrameKind.AudioPcm
                ? 1000UL
                : 100UL;
            if (summaryChanged || kindFrames <= 4 || kindFrames % periodicLogFrames == 0)
            {
                lastFeedbackSummaryByKind[feedbackKind] = feedbackSummary;
                progress.Report("[HOST_OUTPUT] " + feedbackSummary);
            }

            if (!mapped)
            {
                if (!string.IsNullOrWhiteSpace(reason) && reportedSkipReasons.Add(reason))
                {
                    progress.Report("[PRO2_OUTPUT] skipped: " + reason);
                }
                continue;
            }

            if (outputSink == null)
            {
                if (lastOutputState != "no_sink")
                {
                    lastOutputState = "no_sink";
                    progress.Report("[PRO2_OUTPUT] no real Pro2 output sink; source=" + packet.Source);
                }
                continue;
            }

            if (!outputSink.IsOutputReady)
            {
                string state = "not_ready:" + packet.Source;
                if (lastOutputState != state)
                {
                    lastOutputState = state;
                    progress.Report("[PRO2_OUTPUT] real Pro2 output is not ready; source=" +
                                    packet.Source + "; host feedback remains log-only.");
                }
                continue;
            }

            if (outputSink.TryWriteOutputPacket(packet, out string error))
            {
                writes++;
                Interlocked.Increment(ref feedbackQueued);
                string state = packet.Source + "/" + (packet.Active ? "active" : "neutral");
                if (state != lastOutputState || writes <= 4 || writes % 100 == 0)
                {
                    lastOutputState = state;
                    progress.Report("[PRO2_OUTPUT] queued " + state + " count=" + writes);
                    if (!string.IsNullOrEmpty(packet.ConversionTelemetry))
                        progress.Report("[RUMBLE_CONVERSION] source=" + packet.Source + " " + packet.ConversionTelemetry);
                }
            }
            else
            {
                failures++;
                string state = "write_failed:" + error;
                if (state != lastOutputState || failures <= 4 || failures % 20 == 0)
                {
                    lastOutputState = state;
                    progress.Report("[PRO2_OUTPUT] write failed count=" + failures + " error=" + error);
                }
            }
        }
        }
        finally
        {
            // This loop is the sole owner of the scheduler: reset DSP/history
            // before recovery, mode replacement, shutdown or a cancelled read.
            QueueFeedbackStop("dualsense-feedback-loop-stop", idleOnly: false);
        }
    }

    private async Task RunLoopAsync(
        string name,
        Func<CancellationToken, Task> loop,
        CancellationToken cancellationToken)
    {
        try
        {
            await loop(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            progress.Report("[VIIPER] " + name + " loop failed: " + ex.Message);
            cts.Cancel();
            if (Interlocked.Exchange(ref faultReported, 1) == 0)
            {
                faultProgress?.Report(ex);
            }
        }
    }

    private async Task<bool> RecoverStreamAsync(
        string loopName,
        ViiperDeviceStream failedStream,
        Exception error,
        CancellationToken cancellationToken)
    {
        if (cts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        await streamRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            ViiperDevice? currentDevice = device;
            if (currentDevice == null)
            {
                return false;
            }

            ViiperDeviceStream? currentStream = GetStream();
            if (currentStream != null && !ReferenceEquals(currentStream, failedStream))
            {
                progress.Report("[VIIPER] " + loopName + " stream resumed on recovered API stream.");
                return true;
            }

            if (ReferenceEquals(currentStream, failedStream))
            {
                _ = ExchangeStream(null);
            }

            progress.Report("[VIIPER] " + loopName +
                            " stream interrupted: " + error.Message +
                            " Reopening API stream without detaching USB device. SEVERE_LINK_ANOMALY=true");
            viiperStreamDisconnectCount++;
            lastViiperError = error.Message;
            try
            {
                await failedStream.DisposeAsync();
            }
            catch
            {
            }

            Exception? lastError = error;
            for (int attempt = 1; attempt <= StreamRecoveryAttempts; attempt++)
            {
                try
                {
                    ViiperDeviceStream replacement = await client.OpenStreamAsync(
                        currentDevice.BusId,
                        currentDevice.DevId,
                        cancellationToken).ConfigureAwait(false);
                    SetStream(replacement);
                    // The last computed frame might not have reached the USB
                    // device. Force a physical-only release even in a repeat gap.
                    Interlocked.Or(ref rearCancelRequested, 3);
                    viiperStreamRecoveryCount++;
                    lastUsbipLifecycleEvent = "api_stream_reopened_without_detach";
                    progress.Report("[VIIPER] API stream recovered attempt=" + attempt +
                                    " bus=" + currentDevice.BusId +
                                    " dev=" + currentDevice.DevId +
                                    " SEVERE_LINK_ANOMALY=true");
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    lastViiperError = ex.Message;
                    int delayMs = Math.Min(250 * attempt, 1000);
                    progress.Report("[VIIPER] API stream recover retry " + attempt +
                                    "/" + StreamRecoveryAttempts +
                                    " failed: " + ex.Message);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }

            progress.Report("[VIIPER] API stream recovery exhausted; session restart required. last=" +
                            (lastError?.Message ?? error.Message));
            lastViiperError = lastError?.Message ?? error.Message;
            return false;
        }
        finally
        {
            streamRecoveryGate.Release();
        }
    }

    private ViiperDeviceStream? GetStream()
    {
        lock (streamSync)
        {
            return stream;
        }
    }

    private void SetStream(ViiperDeviceStream? value)
    {
        lock (streamSync)
        {
            stream = value;
        }
    }

    private ViiperDeviceStream? ExchangeStream(ViiperDeviceStream? value)
    {
        lock (streamSync)
        {
            ViiperDeviceStream? previous = stream;
            stream = value;
            return previous;
        }
    }

    private static async Task DrainTaskAsync(Task? task)
    {
        if (task == null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            // The user-facing log already records stream state; cleanup should not throw.
        }
    }

    private static string SanitizeLogValue(string value)
    {
        return value.Replace("\"", "'", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        streamRecoveryGate.Dispose();
        cts.Dispose();
    }

    private sealed class VirtualImuTelemetryStats
    {
        private ulong samples;
        private MotionSample lastOutput;
        private MotionSample lastSource;
        private MotionSample maxAbsOutput;
        private MotionSample maxAbsSource;

        public void Record(ViiperDeviceProfile profile, byte[] packet, GamepadState? source)
        {
            if (!TryDecodeOutput(profile.Mode, packet, out MotionSample output))
            {
                return;
            }

            samples++;
            lastOutput = output;
            maxAbsOutput = MaxAbs(maxAbsOutput, output);

            if (source != null)
            {
                lastSource = new MotionSample(
                    source.GyroX,
                    source.GyroY,
                    source.GyroZ,
                    source.AccelX,
                    source.AccelY,
                    source.AccelZ);
                maxAbsSource = MaxAbs(maxAbsSource, lastSource);
            }
        }

        public string SummaryAndMaybeReset(bool reset)
        {
            string summary =
                " imu_samples=" + samples +
                " imu_out_gyro_last=" + lastOutput.GyroX + "," + lastOutput.GyroY + "," + lastOutput.GyroZ +
                " imu_out_gyro_max_abs=" + maxAbsOutput.GyroX + "," + maxAbsOutput.GyroY + "," + maxAbsOutput.GyroZ +
                " imu_out_accel_last=" + lastOutput.AccelX + "," + lastOutput.AccelY + "," + lastOutput.AccelZ +
                " imu_out_accel_max_abs=" + maxAbsOutput.AccelX + "," + maxAbsOutput.AccelY + "," + maxAbsOutput.AccelZ +
                " imu_src_gyro_last=" + lastSource.GyroX + "," + lastSource.GyroY + "," + lastSource.GyroZ +
                " imu_src_gyro_max_abs=" + maxAbsSource.GyroX + "," + maxAbsSource.GyroY + "," + maxAbsSource.GyroZ +
                " imu_src_accel_last=" + lastSource.AccelX + "," + lastSource.AccelY + "," + lastSource.AccelZ +
                " imu_src_accel_max_abs=" + maxAbsSource.AccelX + "," + maxAbsSource.AccelY + "," + maxAbsSource.AccelZ;
            if (reset)
            {
                samples = 0;
                lastOutput = default;
                lastSource = default;
                maxAbsOutput = default;
                maxAbsSource = default;
            }
            return summary;
        }

        public string Ps5PipelineSummary()
        {
            if (samples == 0)
            {
                return "";
            }

            double pro2GyroRawPerDps = ProfessionalImuConverter.SwitchGyroRawPerDps;
            double pro2AccelRawPerG = ProfessionalImuConverter.SwitchAccelRawPerG;
            double dsGyroRawPerDps = ProfessionalImuConverter.DualSenseGyroRawPerDps;
            double dsAccelRawPerG = ProfessionalImuConverter.DualSenseAccelRawPerG;
            string rawPro2 =
                "gyro:" + lastSource.GyroX + "," + lastSource.GyroY + "," + lastSource.GyroZ +
                ";accel:" + lastSource.AccelX + "," + lastSource.AccelY + "," + lastSource.AccelZ;
            string physicalPro2 =
                "gyro_dps:" + Format(lastSource.GyroX / pro2GyroRawPerDps) + "," +
                Format(lastSource.GyroY / pro2GyroRawPerDps) + "," +
                Format(lastSource.GyroZ / pro2GyroRawPerDps) +
                ";accel_g:" + Format(lastSource.AccelX / pro2AccelRawPerG) + "," +
                Format(lastSource.AccelY / pro2AccelRawPerG) + "," +
                Format(lastSource.AccelZ / pro2AccelRawPerG);
            string mappedDs =
                "gyro_dps:" + Format(lastOutput.GyroX / dsGyroRawPerDps) + "," +
                Format(lastOutput.GyroY / dsGyroRawPerDps) + "," +
                Format(lastOutput.GyroZ / dsGyroRawPerDps) +
                ";accel_g:" + Format(lastOutput.AccelX / dsAccelRawPerG) + "," +
                Format(lastOutput.AccelY / dsAccelRawPerG) + "," +
                Format(lastOutput.AccelZ / dsAccelRawPerG);
            string dsRaw =
                "gyro:" + lastOutput.GyroX + "," + lastOutput.GyroY + "," + lastOutput.GyroZ +
                ";accel:" + lastOutput.AccelX + "," + lastOutput.AccelY + "," + lastOutput.AccelZ;
            return
                "raw_pro2=" + rawPro2 +
                " physical_pro2=" + physicalPro2 +
                " mapped_ds=" + mappedDs +
                " ds_raw=" + dsRaw +
                " neutral_accel=0,8192,0" +
                " gyro_raw_per_dps=" + Format(dsGyroRawPerDps) +
                " pro2_accel_raw_per_g=" + Format(pro2AccelRawPerG) +
                " ds_accel_raw_per_g=" + Format(dsAccelRawPerG);
        }

        private static bool TryDecodeOutput(
            ViiperVirtualMode mode,
            byte[] packet,
            out MotionSample sample)
        {
            sample = default;
            if (mode is ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest)
            {
                if (packet.Length < 33)
                {
                    return false;
                }

                sample = new MotionSample(
                    ReadI16(packet, 21),
                    ReadI16(packet, 23),
                    ReadI16(packet, 25),
                    ReadI16(packet, 27),
                    ReadI16(packet, 29),
                    ReadI16(packet, 31));
                return true;
            }

            if (mode is ViiperVirtualMode.Pro2 or ViiperVirtualMode.Pro1)
            {
                if (packet.Length < 24)
                {
                    return false;
                }

                sample = new MotionSample(
                    ReadI16(packet, 18),
                    ReadI16(packet, 20),
                    ReadI16(packet, 22),
                    ReadI16(packet, 12),
                    ReadI16(packet, 14),
                    ReadI16(packet, 16));
                return true;
            }

            return false;
        }

        private static short ReadI16(byte[] packet, int offset) =>
            BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(offset, 2));

        private static MotionSample MaxAbs(MotionSample current, MotionSample next) =>
            new(
                MaxAbs(current.GyroX, next.GyroX),
                MaxAbs(current.GyroY, next.GyroY),
                MaxAbs(current.GyroZ, next.GyroZ),
                MaxAbs(current.AccelX, next.AccelX),
                MaxAbs(current.AccelY, next.AccelY),
                MaxAbs(current.AccelZ, next.AccelZ));

        private static short MaxAbs(short currentAbs, short value)
        {
            int abs = Math.Abs((int)value);
            return abs > currentAbs ? (short)Math.Min(abs, short.MaxValue) : currentAbs;
        }

        private static string Format(double value) =>
            value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private readonly record struct MotionSample(
            short GyroX,
            short GyroY,
            short GyroZ,
            short AccelX,
            short AccelY,
            short AccelZ);
    }
}
