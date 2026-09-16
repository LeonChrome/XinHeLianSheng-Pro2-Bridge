using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Y700Switch2V60Viiper;

public enum ProfessionalImuSampleMode
{
    LatestSample,
    Average3Samples,
    PerSampleForIntegration
}

public enum ProfessionalImuPipelineMode
{
    LegacyOutput,
    ProfessionalTest
}

public enum XboxProfessionalImuOutputMode
{
    Off,
    GyroToRightStick
}

public enum OutputReportRateMode
{
    Fixed,
    AutoByBleSourceRate
}

public enum ProfessionalGyroUncalibratedBehavior
{
    ZeroOutput,
    LegacyFallback,
    AllowUncalibratedDebug
}

public enum ImuIntegralState
{
    Disabled,
    TestingPitch90,
    TestingYaw90,
    TestingRoll90,
    TestStopped
}

public enum GyroBiasStatus
{
    NotCalibrated,
    ManualCalibrating,
    CalibratedAndApplied,
    CalibrationRejectedMoving
}

public enum GyroBiasSource
{
    None,
    Manual3s,
    AutoStillnessReserved
}

public enum ProfessionalImuTestAxis
{
    Pitch,
    Yaw,
    Roll
}

public sealed record ProfessionalImuOptions(
    bool Enabled,
    ProfessionalImuSampleMode OutputSampleMode,
    ProfessionalImuSampleMode IntegrationSampleMode,
    double Ps5GyroScalePitch,
    double Ps5GyroScaleYaw,
    double Ps5GyroScaleRoll,
    bool InvertOutputGyroPitch,
    bool InvertOutputGyroYaw,
    bool InvertOutputGyroRoll,
    XboxProfessionalImuOutputMode XboxOutputMode,
    bool AllowLowBleRate,
    double MinimumAllowedBleRateHz,
    bool AutoReduceVirtualReportRate,
    OutputReportRateMode OutputReportRateMode,
    ProfessionalGyroUncalibratedBehavior ProfessionalGyroUncalibratedBehavior,
    int LowRateSafeNeutralTimeoutMs)
{
    public static ProfessionalImuOptions Default { get; } = new(
        Enabled: false,
        OutputSampleMode: ProfessionalImuSampleMode.Average3Samples,
        IntegrationSampleMode: ProfessionalImuSampleMode.PerSampleForIntegration,
        Ps5GyroScalePitch: 1.0,
        Ps5GyroScaleYaw: 1.0,
        Ps5GyroScaleRoll: 1.0,
        InvertOutputGyroPitch: false,
        InvertOutputGyroYaw: false,
        InvertOutputGyroRoll: false,
        XboxOutputMode: XboxProfessionalImuOutputMode.Off,
        AllowLowBleRate: true,
        MinimumAllowedBleRateHz: 10.0,
        // BLE sampling and virtual USB reporting are separate clocks. Hold
        // latest_state at the explicitly selected USB report rate.
        AutoReduceVirtualReportRate: false,
        OutputReportRateMode: OutputReportRateMode.Fixed,
        ProfessionalGyroUncalibratedBehavior: ProfessionalGyroUncalibratedBehavior.ZeroOutput,
        LowRateSafeNeutralTimeoutMs: 800);

    public static ProfessionalImuOptions ForTestModes(
        Ps5OutputImuTuning tuning,
        bool invertOutputGyroPitch = false,
        bool invertOutputGyroYaw = false,
        bool invertOutputGyroRoll = false) => Default with
    {
        Enabled = true,
        Ps5GyroScalePitch = tuning.GyroScalePitch,
        Ps5GyroScaleYaw = tuning.GyroScaleYaw,
        Ps5GyroScaleRoll = tuning.GyroScaleRoll,
        InvertOutputGyroPitch = invertOutputGyroPitch,
        InvertOutputGyroYaw = invertOutputGyroYaw,
        InvertOutputGyroRoll = invertOutputGyroRoll
    };

    public string TelemetryValue =>
        "pipeline=professional" +
        " output=" + OutputSampleMode +
        " integration=" + IntegrationSampleMode +
        " ps5_gyro_scale=" +
        Ps5GyroScalePitch.ToString("0.###", CultureInfo.InvariantCulture) + "," +
        Ps5GyroScaleYaw.ToString("0.###", CultureInfo.InvariantCulture) + "," +
        Ps5GyroScaleRoll.ToString("0.###", CultureInfo.InvariantCulture) +
        " professional_gyro_invert=" +
        InvertOutputGyroPitch.ToString().ToLowerInvariant() + "," +
        InvertOutputGyroYaw.ToString().ToLowerInvariant() + "," +
        InvertOutputGyroRoll.ToString().ToLowerInvariant() +
        " xbox_imu=" + XboxOutputMode +
        " low_ble=" + AllowLowBleRate +
        " min_ble_hz=" + MinimumAllowedBleRateHz.ToString("0.#", CultureInfo.InvariantCulture) +
        " report_rate=" + OutputReportRateMode +
        " professional_gyro_uncalibrated_behavior=" + ProfessionalGyroUncalibratedBehavior;
}

public readonly record struct SwitchImuRawSample(
    short AccelX,
    short AccelY,
    short AccelZ,
    short GyroX,
    short GyroY,
    short GyroZ,
    int SampleIndex,
    int Offset,
    long SourceTimestampTicks,
    ulong SourceSequence);

public readonly record struct SwitchImuRawBlock(
    SwitchImuRawSample[] Samples,
    int Offset,
    string RawBytesHex)
{
    public static SwitchImuRawBlock Empty { get; } = new([], -1, "");
}

public readonly record struct ImuPhysicalSample(
    double AccelXG,
    double AccelYG,
    double AccelZG,
    double GyroXDps,
    double GyroYDps,
    double GyroZDps,
    int SampleIndex,
    long SourceTimestampTicks,
    ulong SourceSequence);

public readonly record struct DualSenseImuRawSample(
    short AccelX,
    short AccelY,
    short AccelZ,
    short GyroX,
    short GyroY,
    short GyroZ,
    bool Valid);

public sealed record ProfessionalImuUiSnapshot(
    string BiasStatusText,
    string RawGyroText,
    string CorrectedGyroText,
    string OutputGyroText,
    string IntegratedAngleText,
    string NinetyDegreeTestText);

public readonly record struct ProfessionalImuFrame(
    DualSenseImuRawSample? DualSenseRaw,
    GamepadState? XboxState,
    ImuPhysicalSample? OutputPhysical,
    int SourceSampleCount,
    string Telemetry,
    ProfessionalImuUiSnapshot? UiSnapshot);

public readonly record struct GyroBiasCalibrationEvent(
    bool Committed,
    string Reason,
    string Summary);

public sealed class ImuCalibrationState
{
    private const double SwitchAccelRawPerG = ProfessionalImuConverter.SwitchAccelRawPerG;
    private const int MinimumManualSampleCount = 150;
    private const double ManualCalibrationDurationSeconds = 3.0;
    private readonly List<SwitchImuRawSample> manualWindow = [];
    private long manualStartTicks;
    private long manualLastTicks;

    public bool Calibrated { get; private set; }
    public double GyroXBiasRaw { get; private set; }
    public double GyroYBiasRaw { get; private set; }
    public double GyroZBiasRaw { get; private set; }
    public GyroBiasStatus BiasStatus { get; private set; } = GyroBiasStatus.NotCalibrated;
    public GyroBiasSource BiasSource { get; private set; } = GyroBiasSource.None;
    public int BiasUpdateCount { get; private set; }
    public DateTimeOffset? LastBiasUpdateTime { get; private set; }
    public string LastBiasUpdateReason { get; private set; } = "none";
    public string CalibrationRejectedReason { get; private set; } = "";
    public int CalibrationWindowSampleCount => manualWindow.Count;
    public bool IsBiasAppliedToOutput => BiasStatus == GyroBiasStatus.CalibratedAndApplied;
    public double ManualCalibrationProgressPercent
    {
        get
        {
            if (BiasStatus != GyroBiasStatus.ManualCalibrating || manualStartTicks <= 0 || manualLastTicks <= 0)
            {
                return BiasStatus == GyroBiasStatus.ManualCalibrating ? 0 : 100;
            }

            double durationSeconds = Math.Max(
                0,
                (manualLastTicks - manualStartTicks) / (double)Stopwatch.Frequency);
            return Math.Clamp(durationSeconds / ManualCalibrationDurationSeconds * 100.0, 0, 100);
        }
    }

    public string TelemetryValue =>
        "bias_status=" + BiasStatus +
        " bias_source=" + BiasSource +
        " bias_update_count=" + BiasUpdateCount +
        " last_bias_update_time=" + (LastBiasUpdateTime?.ToString("O", CultureInfo.InvariantCulture) ?? "none") +
        " last_bias_update_reason=" + LastBiasUpdateReason +
        " is_bias_applied_to_output=" + IsBiasAppliedToOutput.ToString().ToLowerInvariant() +
        " calibration_window_sample_count=" + CalibrationWindowSampleCount +
        " calibration_rejected_reason=" + (string.IsNullOrWhiteSpace(CalibrationRejectedReason) ? "none" : CalibrationRejectedReason) +
        " bias_raw=" +
        GyroXBiasRaw.ToString("0.###", CultureInfo.InvariantCulture) + "," +
        GyroYBiasRaw.ToString("0.###", CultureInfo.InvariantCulture) + "," +
        GyroZBiasRaw.ToString("0.###", CultureInfo.InvariantCulture);

    public void BeginManual3s()
    {
        manualWindow.Clear();
        manualStartTicks = 0;
        manualLastTicks = 0;
        BiasStatus = GyroBiasStatus.ManualCalibrating;
        BiasSource = Calibrated ? GyroBiasSource.Manual3s : GyroBiasSource.None;
        CalibrationRejectedReason = "";
        LastBiasUpdateReason = "manual_3s_started";
    }

    public void Reset()
    {
        manualWindow.Clear();
        manualStartTicks = 0;
        manualLastTicks = 0;
        Calibrated = false;
        GyroXBiasRaw = 0;
        GyroYBiasRaw = 0;
        GyroZBiasRaw = 0;
        BiasStatus = GyroBiasStatus.NotCalibrated;
        BiasSource = GyroBiasSource.None;
        CalibrationRejectedReason = "";
        LastBiasUpdateReason = "reset";
    }

    public GyroBiasCalibrationEvent? ObserveManualCalibration(
        IReadOnlyList<SwitchImuRawSample> samples,
        double sampleAgeMs)
    {
        if (BiasStatus != GyroBiasStatus.ManualCalibrating || samples.Count == 0)
        {
            return null;
        }

        if (sampleAgeMs > 150)
        {
            return Reject("sample_age_too_large");
        }

        foreach (SwitchImuRawSample sample in samples)
        {
            if (sample.SourceTimestampTicks <= 0)
            {
                return Reject("sample_timestamp_missing");
            }

            double accelNorm = AccelNormG(sample);
            if (accelNorm is < 0.85 or > 1.15)
            {
                return Reject("accel_norm_out_of_range");
            }

            if (manualStartTicks == 0)
            {
                manualStartTicks = sample.SourceTimestampTicks;
            }
            manualLastTicks = sample.SourceTimestampTicks;
            manualWindow.Add(sample);
        }

        double durationSeconds = Math.Max(
            0,
            (manualLastTicks - manualStartTicks) / (double)Stopwatch.Frequency);
        if (durationSeconds < ManualCalibrationDurationSeconds)
        {
            return null;
        }

        WindowStats stats = WindowStats.From(manualWindow);
        if (manualWindow.Count < MinimumManualSampleCount)
        {
            return Reject("sample_count_too_low", stats);
        }

        string? rejection = ValidateFinalWindow(stats);
        if (rejection != null)
        {
            return Reject(rejection, stats);
        }

        GyroXBiasRaw = stats.GyroXMean;
        GyroYBiasRaw = stats.GyroYMean;
        GyroZBiasRaw = stats.GyroZMean;
        Calibrated = true;
        BiasStatus = GyroBiasStatus.CalibratedAndApplied;
        BiasSource = GyroBiasSource.Manual3s;
        BiasUpdateCount++;
        LastBiasUpdateTime = DateTimeOffset.UtcNow;
        LastBiasUpdateReason = "manual_3s";
        CalibrationRejectedReason = "";
        string summary =
            "Gyro bias committed: " +
            "bias_raw=" + FormatBiasRaw() +
            " source=manual_3s" +
            " sample_count=" + manualWindow.Count +
            " gyro_std_raw=" + stats.GyroXStd.ToString("0.###", CultureInfo.InvariantCulture) + "," +
            stats.GyroYStd.ToString("0.###", CultureInfo.InvariantCulture) + "," +
            stats.GyroZStd.ToString("0.###", CultureInfo.InvariantCulture) +
            " accel_norm_mean=" + stats.AccelNormMean.ToString("0.######", CultureInfo.InvariantCulture) +
            " accel_norm_std=" + stats.AccelNormStd.ToString("0.######", CultureInfo.InvariantCulture) +
            " is_bias_applied_to_output=true";
        manualWindow.Clear();
        manualStartTicks = 0;
        manualLastTicks = 0;
        return new GyroBiasCalibrationEvent(true, "manual_3s", summary);
    }

    public string FormatBiasRaw() =>
        GyroXBiasRaw.ToString("0.###", CultureInfo.InvariantCulture) + "," +
        GyroYBiasRaw.ToString("0.###", CultureInfo.InvariantCulture) + "," +
        GyroZBiasRaw.ToString("0.###", CultureInfo.InvariantCulture);

    private GyroBiasCalibrationEvent Reject(string reason, WindowStats? stats = null)
    {
        bool oldBiasKept = Calibrated;
        CalibrationRejectedReason = reason;
        BiasStatus = oldBiasKept
            ? GyroBiasStatus.CalibratedAndApplied
            : GyroBiasStatus.CalibrationRejectedMoving;
        if (!oldBiasKept)
        {
            BiasSource = GyroBiasSource.None;
        }
        LastBiasUpdateReason = "rejected_" + reason;
        string summary =
            "Gyro bias calibration rejected: reason=" + reason +
            " sample_count=" + manualWindow.Count +
            " old_bias_kept=" + oldBiasKept.ToString().ToLowerInvariant();
        if (stats.HasValue)
        {
            WindowStats value = stats.Value;
            summary +=
                " gyro_std_raw=" + value.GyroXStd.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                value.GyroYStd.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                value.GyroZStd.ToString("0.###", CultureInfo.InvariantCulture) +
                " accel_norm_mean=" + value.AccelNormMean.ToString("0.######", CultureInfo.InvariantCulture) +
                " accel_norm_std=" + value.AccelNormStd.ToString("0.######", CultureInfo.InvariantCulture);
        }
        manualWindow.Clear();
        manualStartTicks = 0;
        manualLastTicks = 0;
        return new GyroBiasCalibrationEvent(false, reason, summary);
    }

    private static string? ValidateFinalWindow(WindowStats stats)
    {
        if (stats.AccelNormMean is < 0.85 or > 1.15)
        {
            return "accel_norm_out_of_range";
        }
        if (stats.AccelNormStd >= 0.02)
        {
            return "accel_norm_std_too_high";
        }
        if (stats.GyroXStd >= 3 || stats.GyroYStd >= 3 || stats.GyroZStd >= 3)
        {
            return "gyro_std_too_high";
        }
        if (stats.GyroMaxStepRaw >= 40)
        {
            return "gyro_magnitude_step_too_high";
        }
        return null;
    }

    private static double AccelNormG(SwitchImuRawSample sample)
    {
        double ax = sample.AccelX / SwitchAccelRawPerG;
        double ay = sample.AccelY / SwitchAccelRawPerG;
        double az = sample.AccelZ / SwitchAccelRawPerG;
        return Math.Sqrt(ax * ax + ay * ay + az * az);
    }

    private readonly record struct WindowStats(
        double AccelNormMean,
        double AccelNormStd,
        double GyroXMean,
        double GyroYMean,
        double GyroZMean,
        double GyroXStd,
        double GyroYStd,
        double GyroZStd,
        double GyroMaxStepRaw)
    {
        public static WindowStats From(IReadOnlyList<SwitchImuRawSample> samples)
        {
            double[] accelNorms = samples.Select(AccelNormG).ToArray();
            double[] gyroX = samples.Select(s => (double)s.GyroX).ToArray();
            double[] gyroY = samples.Select(s => (double)s.GyroY).ToArray();
            double[] gyroZ = samples.Select(s => (double)s.GyroZ).ToArray();
            double maxStep = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                double dx = samples[i].GyroX - samples[i - 1].GyroX;
                double dy = samples[i].GyroY - samples[i - 1].GyroY;
                double dz = samples[i].GyroZ - samples[i - 1].GyroZ;
                maxStep = Math.Max(maxStep, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }

            return new WindowStats(
                Mean(accelNorms),
                Std(accelNorms),
                Mean(gyroX),
                Mean(gyroY),
                Mean(gyroZ),
                Std(gyroX),
                Std(gyroY),
                Std(gyroZ),
                maxStep);
        }

        private static double Mean(IReadOnlyList<double> values) =>
            values.Count == 0 ? 0 : values.Sum() / values.Count;

        private static double Std(IReadOnlyList<double> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }
            double mean = Mean(values);
            return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
        }
    }
}

public sealed class ImuIntegrator
{
    private ulong lastIntegratedSequence;
    private long lastTimestampTicks;

    public double PitchDegrees { get; private set; }
    public double YawDegrees { get; private set; }
    public double RollDegrees { get; private set; }
    public bool HasIntegrated { get; private set; }

    public bool Integrate(IReadOnlyList<ImuPhysicalSample> samples, ulong sourceSequence, long sourceTimestampTicks)
    {
        if (samples.Count == 0 ||
            sourceTimestampTicks <= 0 ||
            (HasIntegrated && sourceSequence == lastIntegratedSequence))
        {
            return false;
        }

        if (lastTimestampTicks <= 0)
        {
            lastTimestampTicks = sourceTimestampTicks;
            lastIntegratedSequence = sourceSequence;
            HasIntegrated = true;
            return false;
        }

        double packetDt = Math.Max(
            0,
            (sourceTimestampTicks - lastTimestampTicks) / (double)Stopwatch.Frequency);
        if (packetDt <= 0 || packetDt > 0.5)
        {
            lastTimestampTicks = sourceTimestampTicks;
            lastIntegratedSequence = sourceSequence;
            return false;
        }

        double perSampleDt = packetDt / samples.Count;
        foreach (ImuPhysicalSample sample in samples)
        {
            PitchDegrees += sample.GyroXDps * perSampleDt;
            YawDegrees += sample.GyroYDps * perSampleDt;
            RollDegrees += sample.GyroZDps * perSampleDt;
        }

        lastTimestampTicks = sourceTimestampTicks;
        lastIntegratedSequence = sourceSequence;
        return true;
    }

    public void Reset()
    {
        lastIntegratedSequence = 0;
        lastTimestampTicks = 0;
        PitchDegrees = 0;
        YawDegrees = 0;
        RollDegrees = 0;
        HasIntegrated = false;
    }

    public string TelemetryValue =>
        "integral_pitch_deg=" + PitchDegrees.ToString("0.##", CultureInfo.InvariantCulture) +
        " integral_yaw_deg=" + YawDegrees.ToString("0.##", CultureInfo.InvariantCulture) +
        " integral_roll_deg=" + RollDegrees.ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed class ImuDiagnosticsLogger : IDisposable
{
    private readonly StreamWriter writer;
    private readonly long startTicks = Stopwatch.GetTimestamp();
    private long lastSourceTicks;
    private bool disposed;

    public ImuDiagnosticsLogger(string modeLabel)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "v6_logs");
        Directory.CreateDirectory(directory);
        string safeMode = modeLabel
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace(' ', '_');
        FilePath = Path.Combine(
            directory,
            "professional_imu_" + safeMode + "_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
        writer = new StreamWriter(FilePath, append: false);
        writer.WriteLine(
            "time_ms,source_packet_index,sample_index,source_dt_ms,raw_bytes_hex," +
            "raw_accel_x,raw_accel_y,raw_accel_z,raw_gyro_x,raw_gyro_y,raw_gyro_z," +
            "bias_status,bias_source,bias_update_count,bias_raw_gyro_x,bias_raw_gyro_y,bias_raw_gyro_z," +
            "is_bias_applied_to_output,professional_gyro_uncalibrated_behavior,output_gyro_muted_until_calibrated," +
            "calibration_window_sample_count,calibration_rejected_reason," +
            "physical_accel_x_g,physical_accel_y_g,physical_accel_z_g,physical_gyro_x_dps,physical_gyro_y_dps,physical_gyro_z_dps," +
            "project_accel_x_g,project_accel_y_g,project_accel_z_g,project_gyro_x_dps,project_gyro_y_dps,project_gyro_z_dps," +
            "corrected_gyro_x_dps_preview,corrected_gyro_y_dps_preview,corrected_gyro_z_dps_preview," +
            "selected_output_gyro_x_dps,selected_output_gyro_y_dps,selected_output_gyro_z_dps," +
            "output_gyro_invert_pitch,output_gyro_invert_yaw,output_gyro_invert_roll," +
            "dualsense_accel_x_raw,dualsense_accel_y_raw,dualsense_accel_z_raw,dualsense_gyro_x_raw,dualsense_gyro_y_raw,dualsense_gyro_z_raw," +
            "selected_output_ds_raw_x,selected_output_ds_raw_y,selected_output_ds_raw_z," +
            "accel_norm_g,gyro_magnitude_dps,integral_pitch_deg,integral_yaw_deg,integral_roll_deg," +
            "integral_state,integral_running,test_axis," +
            "is_duplicate_source_sample,sample_age_ms,output_mode," +
            "hid_audit_mode,final_report_id,final_report_length," +
            "final_pack_gyro_x_raw,final_pack_gyro_y_raw,final_pack_gyro_z_raw," +
            "final_report_decoded_gyro_x_raw,final_report_decoded_gyro_y_raw,final_report_decoded_gyro_z_raw," +
            "external_estimated_gyro_x_dps,external_estimated_gyro_y_dps,external_estimated_gyro_z_dps," +
            "final_pack_accel_x_raw,final_pack_accel_y_raw,final_pack_accel_z_raw," +
            "final_report_decoded_accel_x_raw,final_report_decoded_accel_y_raw,final_report_decoded_accel_z_raw," +
            "gyro_report_offset_x,gyro_report_offset_y,gyro_report_offset_z," +
            "accel_report_offset_x,accel_report_offset_y,accel_report_offset_z," +
            "gyro_report_bytes_hex,accel_report_bytes_hex,report_hex_imu_window,hid_audit_result,hid_audit_error");
        writer.Flush();
    }

    public string FilePath { get; }

    public void Write(
        SwitchImuRawSample raw,
        string rawBytesHex,
        ImuPhysicalSample sourcePhysical,
        ImuPhysicalSample correctedPreview,
        ImuPhysicalSample selectedOutput,
        DualSenseImuRawSample selectedOutputDualSense,
        ImuIntegrator integrator,
        ImuCalibrationState calibration,
        ProfessionalGyroUncalibratedBehavior uncalibratedBehavior,
        bool outputGyroMutedUntilCalibrated,
        ImuIntegralState integralState,
        bool integralRunning,
        ProfessionalImuTestAxis? testAxis,
        bool invertOutputGyroPitch,
        bool invertOutputGyroYaw,
        bool invertOutputGyroRoll,
        double sampleAgeMs,
        string outputMode,
        ProfessionalHidAuditSnapshot hidAudit)
    {
        if (disposed)
        {
            return;
        }

        double timeMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        double sourceDtMs = lastSourceTicks == 0 || raw.SourceTimestampTicks <= 0
            ? 0
            : (raw.SourceTimestampTicks - lastSourceTicks) * 1000.0 / Stopwatch.Frequency;
        lastSourceTicks = raw.SourceTimestampTicks;
        double accelNormG = Math.Sqrt(
            sourcePhysical.AccelXG * sourcePhysical.AccelXG +
            sourcePhysical.AccelYG * sourcePhysical.AccelYG +
            sourcePhysical.AccelZG * sourcePhysical.AccelZG);
        double gyroMagnitudeDps = Math.Sqrt(
            correctedPreview.GyroXDps * correctedPreview.GyroXDps +
            correctedPreview.GyroYDps * correctedPreview.GyroYDps +
            correctedPreview.GyroZDps * correctedPreview.GyroZDps);

        writer.WriteLine(string.Join(
            ',',
            F(timeMs),
            raw.SourceSequence.ToString(CultureInfo.InvariantCulture),
            raw.SampleIndex.ToString(CultureInfo.InvariantCulture),
            F(sourceDtMs),
            rawBytesHex,
            raw.AccelX.ToString(CultureInfo.InvariantCulture),
            raw.AccelY.ToString(CultureInfo.InvariantCulture),
            raw.AccelZ.ToString(CultureInfo.InvariantCulture),
            raw.GyroX.ToString(CultureInfo.InvariantCulture),
            raw.GyroY.ToString(CultureInfo.InvariantCulture),
            raw.GyroZ.ToString(CultureInfo.InvariantCulture),
            calibration.BiasStatus.ToString(),
            calibration.BiasSource.ToString(),
            calibration.BiasUpdateCount.ToString(CultureInfo.InvariantCulture),
            F(calibration.GyroXBiasRaw),
            F(calibration.GyroYBiasRaw),
            F(calibration.GyroZBiasRaw),
            calibration.IsBiasAppliedToOutput ? "true" : "false",
            uncalibratedBehavior.ToString(),
            outputGyroMutedUntilCalibrated ? "true" : "false",
            calibration.CalibrationWindowSampleCount.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(calibration.CalibrationRejectedReason) ? "none" : calibration.CalibrationRejectedReason,
            F(sourcePhysical.AccelXG),
            F(sourcePhysical.AccelYG),
            F(sourcePhysical.AccelZG),
            F(sourcePhysical.GyroXDps),
            F(sourcePhysical.GyroYDps),
            F(sourcePhysical.GyroZDps),
            F(correctedPreview.AccelXG),
            F(correctedPreview.AccelYG),
            F(correctedPreview.AccelZG),
            F(correctedPreview.GyroXDps),
            F(correctedPreview.GyroYDps),
            F(correctedPreview.GyroZDps),
            F(correctedPreview.GyroXDps),
            F(correctedPreview.GyroYDps),
            F(correctedPreview.GyroZDps),
            F(selectedOutput.GyroXDps),
            F(selectedOutput.GyroYDps),
            F(selectedOutput.GyroZDps),
            invertOutputGyroPitch ? "true" : "false",
            invertOutputGyroYaw ? "true" : "false",
            invertOutputGyroRoll ? "true" : "false",
            selectedOutputDualSense.AccelX.ToString(CultureInfo.InvariantCulture),
            selectedOutputDualSense.AccelY.ToString(CultureInfo.InvariantCulture),
            selectedOutputDualSense.AccelZ.ToString(CultureInfo.InvariantCulture),
            selectedOutputDualSense.GyroX.ToString(CultureInfo.InvariantCulture),
            selectedOutputDualSense.GyroY.ToString(CultureInfo.InvariantCulture),
            selectedOutputDualSense.GyroZ.ToString(CultureInfo.InvariantCulture),
            selectedOutputDualSense.GyroX.ToString(CultureInfo.InvariantCulture),
            selectedOutputDualSense.GyroY.ToString(CultureInfo.InvariantCulture),
            selectedOutputDualSense.GyroZ.ToString(CultureInfo.InvariantCulture),
            F(accelNormG),
            F(gyroMagnitudeDps),
            F(integrator.PitchDegrees),
            F(integrator.YawDegrees),
            F(integrator.RollDegrees),
            integralState.ToString(),
            integralRunning ? "true" : "false",
            testAxis?.ToString() ?? "none",
            "false",
            F(sampleAgeMs),
            outputMode,
            hidAudit.CsvValue));
        writer.Flush();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writer.Dispose();
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class ProfessionalImuRuntime : IDisposable
{
    private readonly object gate = new();
    private readonly ProfessionalImuOptions options;
    private readonly ImuDiagnosticsLogger logger;
    private readonly IProgress<string> progress;
    private readonly ImuCalibrationState calibration = new();
    private readonly ImuIntegrator integrator = new();
    private readonly List<PendingImuCsvRow> pendingCsvRows = [];
    private bool invertOutputGyroPitch;
    private bool invertOutputGyroYaw;
    private bool invertOutputGyroRoll;
    private ulong lastOutputSequence;
    private DualSenseImuRawSample? lastDualSenseRaw;
    private ImuPhysicalSample? lastCorrectedPreviewPhysical;
    private ImuPhysicalSample? lastOutputPhysical;
    private SwitchImuRawSample? lastRawSample;
    private string ninetyDegreeStatus = "90° test idle.";
    private ProfessionalImuTestAxis? activeTestAxis;
    private ImuIntegralState integralState = ImuIntegralState.Disabled;
    private bool lastOutputGyroMuted = true;
    private bool warnedUncalibrated;
    private bool disposed;

    public ProfessionalImuRuntime(
        ProfessionalImuOptions options,
        string modeLabel,
        IProgress<string> progress)
    {
        this.options = options;
        this.progress = progress;
        invertOutputGyroPitch = options.InvertOutputGyroPitch;
        invertOutputGyroYaw = options.InvertOutputGyroYaw;
        invertOutputGyroRoll = options.InvertOutputGyroRoll;
        logger = new ImuDiagnosticsLogger(modeLabel);
    }

    public string CsvPath => logger.FilePath;

    private readonly record struct PendingImuCsvRow(
        SwitchImuRawSample Raw,
        string RawBytesHex,
        ImuPhysicalSample SourcePhysical,
        ImuPhysicalSample CorrectedPreview,
        ImuPhysicalSample SelectedOutput,
        DualSenseImuRawSample SelectedOutputDualSense,
        double SampleAgeMs,
        string OutputMode);

    public ProfessionalImuFrame Process(GamepadState state, double sampleAgeMs)
    {
        lock (gate)
        {
            SwitchImuRawSample[] rawSamples = state.SwitchRawImuSamples;
            if (!options.Enabled || rawSamples.Length == 0)
            {
                pendingCsvRows.Clear();
                return new ProfessionalImuFrame(
                    lastDualSenseRaw,
                    null,
                    lastOutputPhysical,
                    0,
                    "professional_imu=no_raw_samples " + BuildOutputTelemetry(lastOutputPhysical, lastDualSenseRaw),
                    BuildSnapshot());
            }

            if (lastDualSenseRaw.HasValue &&
                state.RawNotificationSequence == lastOutputSequence)
            {
                pendingCsvRows.Clear();
                return new ProfessionalImuFrame(
                    lastDualSenseRaw,
                    null,
                    lastOutputPhysical,
                    rawSamples.Length,
                    "professional_imu=duplicate_source is_duplicate_source_sample=true sequence=" +
                    state.RawNotificationSequence +
                    " samples=" + rawSamples.Length +
                    " " + BuildOutputTelemetry(lastOutputPhysical, lastDualSenseRaw),
                    BuildSnapshot());
            }

            pendingCsvRows.Clear();
            GyroBiasCalibrationEvent? calibrationEvent =
                calibration.ObserveManualCalibration(rawSamples, sampleAgeMs);
            if (calibrationEvent.HasValue)
            {
                progress.Report("[PRO_IMU] " + calibrationEvent.Value.Summary);
                if (calibrationEvent.Value.Committed)
                {
                    ResetIntegralNoLock("gyro_bias_committed", ImuIntegralState.Disabled);
                }
            }

            var sourcePhysicalSamples = new List<ImuPhysicalSample>(rawSamples.Length);
            var correctedPreviewSamples = new List<ImuPhysicalSample>(rawSamples.Length);
            var selectedOutputSamples = new List<ImuPhysicalSample>(rawSamples.Length);
            foreach (SwitchImuRawSample raw in rawSamples)
            {
                sourcePhysicalSamples.Add(ProfessionalImuConverter.ToSourcePhysical(raw, calibration));
                correctedPreviewSamples.Add(ProfessionalImuConverter.ToProjectPhysical(raw, calibration));
            }

            bool gyroMuted = ShouldMuteGyroUntilCalibrated();
            foreach (ImuPhysicalSample sample in correctedPreviewSamples)
            {
                ImuPhysicalSample output = ApplyOutputGyroInversion(sample);
                selectedOutputSamples.Add(gyroMuted ? MuteGyro(output) : output);
            }

            if (IntegralRunning)
            {
                integrator.Integrate(
                    selectedOutputSamples,
                    state.RawNotificationSequence,
                    state.SourceTimestampTicks);
            }

            ImuPhysicalSample correctedPreview = SelectOutputSample(correctedPreviewSamples);
            ImuPhysicalSample outputSource = SelectOutputSample(sourcePhysicalSamples);
            ImuPhysicalSample selectedOutput = SelectOutputSample(selectedOutputSamples);
            SwitchImuRawSample selectedRaw = rawSamples[^1];
            DualSenseImuRawSample dualSenseRaw =
                ProfessionalImuConverter.ToDualSenseRaw(selectedOutput, options);
            lastDualSenseRaw = dualSenseRaw;
            lastCorrectedPreviewPhysical = correctedPreview;
            lastOutputPhysical = selectedOutput;
            lastRawSample = selectedRaw;
            lastOutputGyroMuted = gyroMuted;
            lastOutputSequence = state.RawNotificationSequence;

            for (int i = 0; i < rawSamples.Length; i++)
            {
                ImuPhysicalSample rowOutput = selectedOutputSamples[i];
                DualSenseImuRawSample rowDualSense =
                    i == rawSamples.Length - 1
                        ? dualSenseRaw
                        : ProfessionalImuConverter.ToDualSenseRaw(rowOutput, options);
                pendingCsvRows.Add(new PendingImuCsvRow(
                    rawSamples[i],
                    state.SwitchRawImuBytesHex,
                    sourcePhysicalSamples[i],
                    correctedPreviewSamples[i],
                    rowOutput,
                    rowDualSense,
                    sampleAgeMs,
                    options.OutputSampleMode.ToString()));
            }

            if (gyroMuted && !warnedUncalibrated)
            {
                warnedUncalibrated = true;
                progress.Report("[PRO_IMU] Professional Gyro muted until calibration. behavior=" +
                                options.ProfessionalGyroUncalibratedBehavior +
                                " bias_status=" + calibration.BiasStatus +
                                " output_gyro_muted_until_calibrated=true");
            }

            GamepadState? xboxState = options.XboxOutputMode == XboxProfessionalImuOutputMode.GyroToRightStick
                ? ProfessionalImuConverter.ApplyGyroToRightStick(state, selectedOutput)
                : null;
            string telemetry =
                "professional_imu=ok" +
                " samples=" + rawSamples.Length +
                " raw_offset=" + state.SwitchRawImuOffset +
                " raw_hex=" + state.SwitchRawImuBytesHex +
                " output_sample=" + options.OutputSampleMode +
                " is_duplicate_source_sample=false" +
                " sample_age_ms=" + sampleAgeMs.ToString("0.###", CultureInfo.InvariantCulture) +
                " corrected_gyro_x_dps_preview=" + correctedPreview.GyroXDps.ToString("0.######", CultureInfo.InvariantCulture) +
                " corrected_gyro_y_dps_preview=" + correctedPreview.GyroYDps.ToString("0.######", CultureInfo.InvariantCulture) +
                " corrected_gyro_z_dps_preview=" + correctedPreview.GyroZDps.ToString("0.######", CultureInfo.InvariantCulture) +
                " " + BuildOutputTelemetry(selectedOutput, dualSenseRaw);
            return new ProfessionalImuFrame(
                dualSenseRaw,
                xboxState,
                selectedOutput,
                rawSamples.Length,
                telemetry,
                BuildSnapshot(outputSource, correctedPreview, selectedOutput));
        }
    }

    public ProfessionalImuUiSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return BuildSnapshot();
        }
    }

    public string SetOutputGyroInversion(bool pitch, bool yaw, bool roll)
    {
        lock (gate)
        {
            invertOutputGyroPitch = pitch;
            invertOutputGyroYaw = yaw;
            invertOutputGyroRoll = roll;
            ResetIntegralNoLock("professional_gyro_inversion_changed", ImuIntegralState.Disabled);
            string message = "[PRO_IMU] output gyro inversion changed: pitch=" +
                             pitch.ToString().ToLowerInvariant() +
                             " yaw=" + yaw.ToString().ToLowerInvariant() +
                             " roll=" + roll.ToString().ToLowerInvariant() +
                             " apply=immediate";
            progress.Report(message);
            return "Professional gyro inversion: pitch=" +
                   pitch.ToString().ToLowerInvariant() +
                   ", yaw=" + yaw.ToString().ToLowerInvariant() +
                   ", roll=" + roll.ToString().ToLowerInvariant();
        }
    }

    public void CommitFinalHidAudit(ProfessionalHidAuditSnapshot hidAudit)
    {
        lock (gate)
        {
            foreach (PendingImuCsvRow row in pendingCsvRows)
            {
                logger.Write(
                    row.Raw,
                    row.RawBytesHex,
                    row.SourcePhysical,
                    row.CorrectedPreview,
                    row.SelectedOutput,
                    row.SelectedOutputDualSense,
                    integrator,
                    calibration,
                    options.ProfessionalGyroUncalibratedBehavior,
                    lastOutputGyroMuted,
                    integralState,
                    IntegralRunning,
                    activeTestAxis,
                    invertOutputGyroPitch,
                    invertOutputGyroYaw,
                    invertOutputGyroRoll,
                    row.SampleAgeMs,
                    row.OutputMode,
                    hidAudit);
            }

            pendingCsvRows.Clear();
        }
    }

    public string StartGyroBiasCalibration()
    {
        lock (gate)
        {
            calibration.BeginManual3s();
            string message = "[PRO_IMU] Gyro bias calibration started duration=3s source=manual_3s. Keep the controller still.";
            progress.Report(message);
            return "Gyro bias calibration started.";
        }
    }

    public string ResetGyroBias()
    {
        lock (gate)
        {
            calibration.Reset();
            warnedUncalibrated = false;
            lastOutputGyroMuted = true;
            ResetIntegralNoLock("gyro_bias_reset", ImuIntegralState.Disabled);
            string message = "[PRO_IMU] Gyro bias reset: bias_status=NotCalibrated bias_source=none is_bias_applied_to_output=false output_gyro_muted_until_calibrated=true.";
            progress.Report(message);
            return "Gyro bias reset.";
        }
    }

    public string ResetIntegral()
    {
        lock (gate)
        {
            ResetIntegralNoLock("manual", ImuIntegralState.Disabled);
            return "IMU integral reset.";
        }
    }

    public string StartNinetyDegreeTest(ProfessionalImuTestAxis axis)
    {
        lock (gate)
        {
            if (calibration.BiasStatus != GyroBiasStatus.CalibratedAndApplied)
            {
                string rejected = "[PRO_IMU] 90deg test rejected: gyro bias is not calibrated. Run Calibrate Gyro Bias 3s first.";
                progress.Report(rejected);
                ninetyDegreeStatus = "90° test rejected: calibrate gyro bias first.";
                return "90° test rejected: calibrate gyro bias first.";
            }

            activeTestAxis = axis;
            ResetIntegralNoLock("90deg_test_start", IntegralStateForAxis(axis));
            ninetyDegreeStatus = "90° " + axis + " test running. Rotate one axis, then click Stop.";
            string message = "[PRO_IMU] 90deg test started: test_axis=" + axis +
                             " integral_state=" + integralState +
                             " integral_running=true integral_pitch_deg=0 integral_yaw_deg=0 integral_roll_deg=0";
            progress.Report(message);
            return "90° " + axis + " test started.";
        }
    }

    public string StopNinetyDegreeTest()
    {
        lock (gate)
        {
            if (activeTestAxis == null)
            {
                ninetyDegreeStatus = "90° test idle.";
                return "No 90° test is running.";
            }

            double pitch = integrator.PitchDegrees;
            double yaw = integrator.YawDegrees;
            double roll = integrator.RollDegrees;
            string dominant = DominantAxis(pitch, yaw, roll);
            double target = activeTestAxis.Value switch
            {
                ProfessionalImuTestAxis.Pitch => pitch,
                ProfessionalImuTestAxis.Yaw => yaw,
                ProfessionalImuTestAxis.Roll => roll,
                _ => 0
            };
            string result = BuildNinetyDegreeResult(activeTestAxis.Value, target, dominant);
            string scale = SuggestedScale(Math.Abs(target));
            string fix = SuggestedAxisFix(activeTestAxis.Value, target, dominant);
            string message =
                "[PRO_IMU] 90deg test stopped: test_axis=" + activeTestAxis.Value +
                " integral_state=TestStopped integral_running=false" +
                " integral_pitch_deg=" + pitch.ToString("0.###", CultureInfo.InvariantCulture) +
                " integral_yaw_deg=" + yaw.ToString("0.###", CultureInfo.InvariantCulture) +
                " integral_roll_deg=" + roll.ToString("0.###", CultureInfo.InvariantCulture) +
                " dominant_axis=" + dominant +
                " result=" + result +
                " suggested_gyro_scale=" + scale +
                " suggested_axis_fix=" + fix;
            progress.Report(message);
            progress.Report("[PRO_IMU] 90deg test result: test_axis=" + activeTestAxis.Value +
                            " result=" + result +
                            " dominant_axis=" + dominant +
                            " suggested_gyro_scale=" + scale +
                            " suggested_axis_fix=" + fix);
            ninetyDegreeStatus = "90° result: " + result + " / " + dominant +
                                 " / Pitch " + pitch.ToString("0.0", CultureInfo.InvariantCulture) +
                                 "°, Yaw " + yaw.ToString("0.0", CultureInfo.InvariantCulture) +
                                 "°, Roll " + roll.ToString("0.0", CultureInfo.InvariantCulture) + "°";
            activeTestAxis = null;
            integralState = ImuIntegralState.TestStopped;
            return ninetyDegreeStatus;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        logger.Dispose();
    }

    private void ResetIntegralNoLock(string reason, ImuIntegralState newState)
    {
        integrator.Reset();
        integralState = newState;
        progress.Report("[PRO_IMU] IMU integral reset. reason=" + reason +
                        " integral_state=" + integralState +
                        " integral_running=" + IntegralRunning.ToString().ToLowerInvariant());
    }

    private ProfessionalImuUiSnapshot BuildSnapshot(
        ImuPhysicalSample? sourcePhysical = null,
        ImuPhysicalSample? correctedPreviewPhysical = null,
        ImuPhysicalSample? selectedOutputPhysical = null)
    {
        SwitchImuRawSample raw = lastRawSample ?? default;
        ImuPhysicalSample preview = correctedPreviewPhysical ?? lastCorrectedPreviewPhysical ?? default;
        ImuPhysicalSample output = selectedOutputPhysical ?? lastOutputPhysical ?? default;
        string bias =
            "Bias: " + calibration.BiasStatus +
            " / source=" + calibration.BiasSource +
            " / applied=" + calibration.IsBiasAppliedToOutput.ToString().ToLowerInvariant() +
            " / updates=" + calibration.BiasUpdateCount +
            " / raw=" + calibration.FormatBiasRaw() +
            (calibration.BiasStatus == GyroBiasStatus.ManualCalibrating
                ? " / progress=" + calibration.ManualCalibrationProgressPercent.ToString("0", CultureInfo.InvariantCulture) + "%"
                : "") +
            (string.IsNullOrWhiteSpace(calibration.CalibrationRejectedReason)
                ? ""
                : " / rejected=" + calibration.CalibrationRejectedReason);
        string rawGyro =
            "Raw Gyro raw: X=" + raw.GyroX +
            " Y=" + raw.GyroY +
            " Z=" + raw.GyroZ;
        string corrected =
            "Corrected Gyro Preview (°/s): Pitch Rate=" + preview.GyroXDps.ToString("0.###", CultureInfo.InvariantCulture) +
            " Yaw Rate=" + preview.GyroYDps.ToString("0.###", CultureInfo.InvariantCulture) +
            " Roll Rate=" + preview.GyroZDps.ToString("0.###", CultureInfo.InvariantCulture) +
            (lastOutputGyroMuted ? " · Preview only, output muted until calibration." : "");
        string outputGyro =
            "Output Gyro (°/s): Pitch Rate=" + output.GyroXDps.ToString("0.###", CultureInfo.InvariantCulture) +
            " Yaw Rate=" + output.GyroYDps.ToString("0.###", CultureInfo.InvariantCulture) +
            " Roll Rate=" + output.GyroZDps.ToString("0.###", CultureInfo.InvariantCulture) +
            " · invert P/Y/R=" +
            invertOutputGyroPitch.ToString().ToLowerInvariant() + "/" +
            invertOutputGyroYaw.ToString().ToLowerInvariant() + "/" +
            invertOutputGyroRoll.ToString().ToLowerInvariant() +
            " · DualSense raw≈" +
            (output.GyroXDps * ProfessionalImuConverter.DualSenseGyroRawPerDps).ToString("0.#", CultureInfo.InvariantCulture) + "," +
            (output.GyroYDps * ProfessionalImuConverter.DualSenseGyroRawPerDps).ToString("0.#", CultureInfo.InvariantCulture) + "," +
            (output.GyroZDps * ProfessionalImuConverter.DualSenseGyroRawPerDps).ToString("0.#", CultureInfo.InvariantCulture) +
            " · External≈raw/" + ProfessionalImuConverter.DualSenseGyroRawPerDps.ToString("0.###", CultureInfo.InvariantCulture) +
            (lastOutputGyroMuted ? " · Professional Gyro muted until calibration." : "");
        string integral =
            "Integrated Angle (°): Pitch Angle=" + integrator.PitchDegrees.ToString("0.###", CultureInfo.InvariantCulture) +
            " Yaw Angle=" + integrator.YawDegrees.ToString("0.###", CultureInfo.InvariantCulture) +
            " Roll Angle=" + integrator.RollDegrees.ToString("0.###", CultureInfo.InvariantCulture) +
            " · integral_state=" + integralState +
            " · integral_running=" + IntegralRunning.ToString().ToLowerInvariant();
        return new ProfessionalImuUiSnapshot(bias, rawGyro, corrected, outputGyro, integral, ninetyDegreeStatus);
    }

    private bool IntegralRunning =>
        integralState is ImuIntegralState.TestingPitch90
            or ImuIntegralState.TestingYaw90
            or ImuIntegralState.TestingRoll90;

    private bool ShouldMuteGyroUntilCalibrated() =>
        options.ProfessionalGyroUncalibratedBehavior == ProfessionalGyroUncalibratedBehavior.ZeroOutput &&
        calibration.BiasStatus != GyroBiasStatus.CalibratedAndApplied;

    private static ImuPhysicalSample MuteGyro(ImuPhysicalSample sample) =>
        new(
            sample.AccelXG,
            sample.AccelYG,
            sample.AccelZG,
            0,
            0,
            0,
            sample.SampleIndex,
            sample.SourceTimestampTicks,
            sample.SourceSequence);

    private ImuPhysicalSample ApplyOutputGyroInversion(ImuPhysicalSample sample) =>
        new(
            sample.AccelXG,
            sample.AccelYG,
            sample.AccelZG,
            invertOutputGyroPitch ? -sample.GyroXDps : sample.GyroXDps,
            invertOutputGyroYaw ? -sample.GyroYDps : sample.GyroYDps,
            invertOutputGyroRoll ? -sample.GyroZDps : sample.GyroZDps,
            sample.SampleIndex,
            sample.SourceTimestampTicks,
            sample.SourceSequence);

    private static ImuIntegralState IntegralStateForAxis(ProfessionalImuTestAxis axis) =>
        axis switch
        {
            ProfessionalImuTestAxis.Pitch => ImuIntegralState.TestingPitch90,
            ProfessionalImuTestAxis.Yaw => ImuIntegralState.TestingYaw90,
            ProfessionalImuTestAxis.Roll => ImuIntegralState.TestingRoll90,
            _ => ImuIntegralState.Disabled
        };

    private string BuildOutputTelemetry(
        ImuPhysicalSample? selectedOutput,
        DualSenseImuRawSample? selectedDualSense)
    {
        ImuPhysicalSample output = selectedOutput ?? lastOutputPhysical ?? default;
        DualSenseImuRawSample dualSense = selectedDualSense ?? lastDualSenseRaw ?? default;
        return
            "professional_gyro_uncalibrated_behavior=" + options.ProfessionalGyroUncalibratedBehavior +
            " output_gyro_muted_until_calibrated=" + lastOutputGyroMuted.ToString().ToLowerInvariant() +
            " output_gyro_invert_pitch=" + invertOutputGyroPitch.ToString().ToLowerInvariant() +
            " output_gyro_invert_yaw=" + invertOutputGyroYaw.ToString().ToLowerInvariant() +
            " output_gyro_invert_roll=" + invertOutputGyroRoll.ToString().ToLowerInvariant() +
            " selected_output_gyro_x_dps=" + output.GyroXDps.ToString("0.######", CultureInfo.InvariantCulture) +
            " selected_output_gyro_y_dps=" + output.GyroYDps.ToString("0.######", CultureInfo.InvariantCulture) +
            " selected_output_gyro_z_dps=" + output.GyroZDps.ToString("0.######", CultureInfo.InvariantCulture) +
            " selected_output_ds_raw_x=" + dualSense.GyroX +
            " selected_output_ds_raw_y=" + dualSense.GyroY +
            " selected_output_ds_raw_z=" + dualSense.GyroZ +
            " integral_state=" + integralState +
            " integral_running=" + IntegralRunning.ToString().ToLowerInvariant() +
            " test_axis=" + (activeTestAxis?.ToString() ?? "none") +
            " " + calibration.TelemetryValue +
            " " + integrator.TelemetryValue;
    }

    private ImuPhysicalSample SelectOutputSample(IReadOnlyList<ImuPhysicalSample> samples)
    {
        if (samples.Count == 1 || options.OutputSampleMode == ProfessionalImuSampleMode.LatestSample)
        {
            return samples[^1];
        }

        return new ImuPhysicalSample(
            samples.Average(s => s.AccelXG),
            samples.Average(s => s.AccelYG),
            samples.Average(s => s.AccelZG),
            samples.Average(s => s.GyroXDps),
            samples.Average(s => s.GyroYDps),
            samples.Average(s => s.GyroZDps),
            -1,
            samples[^1].SourceTimestampTicks,
            samples[^1].SourceSequence);
    }

    private static string DominantAxis(double pitch, double yaw, double roll)
    {
        double ap = Math.Abs(pitch);
        double ay = Math.Abs(yaw);
        double ar = Math.Abs(roll);
        if (ap >= ay && ap >= ar) return "Pitch";
        if (ay >= ap && ay >= ar) return "Yaw";
        return "Roll";
    }

    private static string BuildNinetyDegreeResult(
        ProfessionalImuTestAxis axis,
        double target,
        string dominant)
    {
        if (!string.Equals(dominant, axis.ToString(), StringComparison.Ordinal))
        {
            return "fail_axis_map";
        }
        double abs = Math.Abs(target);
        if (abs is >= 80 and <= 100)
        {
            return target > 0 ? "pass" : "fail_sign_reversed";
        }
        if (target < 0 && abs is >= 80 and <= 100)
        {
            return "fail_sign_reversed";
        }
        return "fail_scale";
    }

    private static string SuggestedScale(double absTarget)
    {
        if (absTarget is >= 35 and <= 60) return "x2";
        if (absTarget is >= 150 and <= 210) return "x0.5";
        if (absTarget is >= 80 and <= 100) return "keep";
        return "inspect";
    }

    private static string SuggestedAxisFix(
        ProfessionalImuTestAxis axis,
        double target,
        string dominant)
    {
        if (!string.Equals(dominant, axis.ToString(), StringComparison.Ordinal))
        {
            return "axis_map_wrong";
        }
        if (target < -80)
        {
            return "sign_reversed";
        }
        return "none";
    }
}

public static class ProfessionalImuConverter
{
    public const double SwitchAccelRawPerG = 4096.0;
    // Switch 2 Pro dynamic wired/wireless integration converges near the
    // 16.384-count scale. The old 14.247 value belongs to Switch 1 IMUs.
    public const double SwitchGyroRawPerDps = 16.384;
    public const double DualSenseAccelRawPerG = 8192.0;
    public const double DualSenseGyroRawPerDps = 16.384;

    public static SwitchImuRawBlock DecodeSwitchImuSamples(
        ReadOnlySpan<byte> payload,
        int offset,
        int maxSamples)
    {
        if (offset < 0 || maxSamples <= 0 || payload.Length < offset + 12)
        {
            return SwitchImuRawBlock.Empty;
        }

        int count = Math.Min(maxSamples, (payload.Length - offset) / 12);
        if (count <= 0)
        {
            return SwitchImuRawBlock.Empty;
        }

        var samples = new SwitchImuRawSample[count];
        for (int i = 0; i < count; i++)
        {
            int sampleOffset = offset + i * 12;
            ReadOnlySpan<byte> sample = payload.Slice(sampleOffset, 12);
            samples[i] = new SwitchImuRawSample(
                BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(0, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(2, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(4, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(6, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(8, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(10, 2)),
                i,
                sampleOffset,
                0,
                0);
        }

        string rawHex = Convert
            .ToHexString(payload.Slice(offset, count * 12))
            .ToLowerInvariant();
        return new SwitchImuRawBlock(samples, offset, rawHex);
    }

    public static ImuPhysicalSample ToSourcePhysical(
        SwitchImuRawSample raw,
        ImuCalibrationState calibration)
    {
        double sourceGyroX = (raw.GyroX - calibration.GyroXBiasRaw) / SwitchGyroRawPerDps;
        double sourceGyroY = (raw.GyroY - calibration.GyroYBiasRaw) / SwitchGyroRawPerDps;
        double sourceGyroZ = (raw.GyroZ - calibration.GyroZBiasRaw) / SwitchGyroRawPerDps;
        return new ImuPhysicalSample(
            raw.AccelX / SwitchAccelRawPerG,
            raw.AccelY / SwitchAccelRawPerG,
            raw.AccelZ / SwitchAccelRawPerG,
            sourceGyroX,
            sourceGyroY,
            sourceGyroZ,
            raw.SampleIndex,
            raw.SourceTimestampTicks,
            raw.SourceSequence);
    }

    public static ImuPhysicalSample ToProjectPhysical(
        SwitchImuRawSample raw,
        ImuCalibrationState calibration)
    {
        ImuPhysicalSample source = ToSourcePhysical(raw, calibration);
        return new ImuPhysicalSample(
            source.AccelXG,
            source.AccelZG,
            -source.AccelYG,
            source.GyroXDps,
            source.GyroZDps,
            -source.GyroYDps,
            raw.SampleIndex,
            raw.SourceTimestampTicks,
            raw.SourceSequence);
    }

    public static ImuPhysicalSample ToPhysical(
        SwitchImuRawSample raw,
        ImuCalibrationState calibration) =>
        ToProjectPhysical(raw, calibration);

    public static DualSenseImuRawSample ToDualSenseRaw(
        ImuPhysicalSample projectPhysical,
        ProfessionalImuOptions options)
    {
        return new DualSenseImuRawSample(
            ClampToInt16(projectPhysical.AccelXG * DualSenseAccelRawPerG),
            ClampToInt16(projectPhysical.AccelYG * DualSenseAccelRawPerG),
            ClampToInt16(projectPhysical.AccelZG * DualSenseAccelRawPerG),
            ClampToInt16(projectPhysical.GyroXDps * DualSenseGyroRawPerDps * options.Ps5GyroScalePitch),
            ClampToInt16(projectPhysical.GyroYDps * DualSenseGyroRawPerDps * options.Ps5GyroScaleYaw),
            ClampToInt16(projectPhysical.GyroZDps * DualSenseGyroRawPerDps * options.Ps5GyroScaleRoll),
            Valid: true);
    }

    public static GamepadState ApplyGyroToRightStick(GamepadState state, ImuPhysicalSample physical)
    {
        GamepadState result = state.Clone();
        result.Rx = DpsToAxis(physical.GyroYDps);
        result.Ry = DpsToAxis(-physical.GyroXDps);
        return result;
    }

    public static SwitchImuRawSample Stamp(
        SwitchImuRawSample sample,
        long sourceTimestampTicks,
        ulong sourceSequence)
    {
        return sample with
        {
            SourceTimestampTicks = sourceTimestampTicks,
            SourceSequence = sourceSequence
        };
    }

    private static ushort DpsToAxis(double dps)
    {
        double normalized = Math.Clamp(dps / 300.0, -1.0, 1.0);
        return (ushort)Math.Round(GamepadState.AxisCenter + normalized * GamepadState.AxisCenter);
    }

    private static short ClampToInt16(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < short.MinValue) return short.MinValue;
        if (rounded > short.MaxValue) return short.MaxValue;
        return (short)rounded;
    }
}

public static class VirtualReportRateGovernor
{
    public static double SelectAutoRateHz(double sourceRateHz, double configuredMaximumHz)
    {
        if (sourceRateHz <= 0)
        {
            return configuredMaximumHz;
        }

        double selected = sourceRateHz switch
        {
            < 12.5 => 10.0,
            < 15.0 => 15.0,
            < 25.0 => 20.0,
            < 45.0 => 30.0,
            < 80.0 => 60.0,
            < 140.0 => 125.0,
            _ => configuredMaximumHz >= 250.0 ? 250.0 : 125.0
        };
        return Math.Min(configuredMaximumHz, selected);
    }
}
