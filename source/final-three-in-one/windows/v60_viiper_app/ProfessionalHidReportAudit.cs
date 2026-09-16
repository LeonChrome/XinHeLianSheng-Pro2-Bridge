using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Y700Switch2V60Viiper;

public enum ProfessionalHidAuditMode
{
    Normal,
    ForceFinalGyroZero,
    ForceFinalGyroSyntheticPulse,
    ForceFinalGyroStaticRaw
}

public enum ProfessionalHidAuditResult
{
    OK,
    MISMATCH_GYRO,
    MISMATCH_ACCEL,
    REPORT_TOO_SHORT,
    OFFSET_UNKNOWN,
    WRONG_BUILDER,
    FORCED_ZERO,
    SYNTHETIC_PULSE
}

public static class DualSenseProfessionalHidLayout
{
    public const int ReportLength = 33;
    public const int ReportId = -1;
    public const int GyroXOffset = 21;
    public const int GyroYOffset = 23;
    public const int GyroZOffset = 25;
    public const int AccelXOffset = 27;
    public const int AccelYOffset = 29;
    public const int AccelZOffset = 31;
    public const string BuilderName = "VirtualPadPackets.DualSenseFromGamepad(professionalImu)";
    public const string ReportIdLabel = "none";
}

public readonly record struct ProfessionalHidAuditControlState(
    ProfessionalHidAuditMode Mode,
    short StaticGyroX,
    short StaticGyroY,
    short StaticGyroZ,
    bool PulseActive,
    short PulseGyroX,
    short PulseGyroY,
    short PulseGyroZ,
    string PulseAxis);

public sealed class ProfessionalHidAuditController
{
    private readonly object gate = new();
    private long pulseUntilTicks;
    private short pulseGyroX;
    private short pulseGyroY;
    private short pulseGyroZ;
    private string pulseAxis = "none";
    private bool pulseStopLogged = true;

    public ProfessionalHidAuditMode Mode { get; private set; } = ProfessionalHidAuditMode.Normal;
    public short StaticGyroX { get; private set; }
    public short StaticGyroY { get; private set; }
    public short StaticGyroZ { get; private set; }

    public string SetMode(ProfessionalHidAuditMode mode)
    {
        lock (gate)
        {
            Mode = mode;
            if (mode != ProfessionalHidAuditMode.ForceFinalGyroSyntheticPulse)
            {
                pulseUntilTicks = 0;
                pulseGyroX = 0;
                pulseGyroY = 0;
                pulseGyroZ = 0;
                pulseAxis = "none";
                pulseStopLogged = true;
            }

            return SummaryNoLock();
        }
    }

    public string SetStaticRaw(short x, short y, short z)
    {
        lock (gate)
        {
            StaticGyroX = x;
            StaticGyroY = y;
            StaticGyroZ = z;
            return SummaryNoLock();
        }
    }

    public string StartPulse(string axis, short raw, TimeSpan duration, long nowTicks)
    {
        lock (gate)
        {
            Mode = ProfessionalHidAuditMode.ForceFinalGyroSyntheticPulse;
            pulseGyroX = axis == "X" ? raw : (short)0;
            pulseGyroY = axis == "Y" ? raw : (short)0;
            pulseGyroZ = axis == "Z" ? raw : (short)0;
            pulseAxis = axis;
            pulseUntilTicks = nowTicks + (long)(duration.TotalSeconds * StopwatchFrequency);
            pulseStopLogged = false;
            return SummaryNoLock();
        }
    }

    public ProfessionalHidAuditControlState Snapshot(long nowTicks, out string? eventMessage)
    {
        lock (gate)
        {
            bool pulseActive =
                Mode == ProfessionalHidAuditMode.ForceFinalGyroSyntheticPulse &&
                pulseUntilTicks > 0 &&
                nowTicks <= pulseUntilTicks;
            eventMessage = null;
            if (Mode == ProfessionalHidAuditMode.ForceFinalGyroSyntheticPulse &&
                !pulseActive &&
                pulseUntilTicks > 0 &&
                !pulseStopLogged)
            {
                eventMessage = "[PRO_IMU_AUDIT] synthetic pulse stop axis=" + pulseAxis +
                               " raw=" + pulseGyroX + "," + pulseGyroY + "," + pulseGyroZ;
                pulseStopLogged = true;
                pulseUntilTicks = 0;
                pulseGyroX = 0;
                pulseGyroY = 0;
                pulseGyroZ = 0;
                pulseAxis = "none";
            }

            return new ProfessionalHidAuditControlState(
                Mode,
                StaticGyroX,
                StaticGyroY,
                StaticGyroZ,
                pulseActive,
                pulseActive ? pulseGyroX : (short)0,
                pulseActive ? pulseGyroY : (short)0,
                pulseActive ? pulseGyroZ : (short)0,
                pulseActive ? pulseAxis : "none");
        }
    }

    public string Summary()
    {
        lock (gate)
        {
            return SummaryNoLock();
        }
    }

    private string SummaryNoLock() =>
        "mode=" + Mode +
        " static_raw=" + StaticGyroX + "," + StaticGyroY + "," + StaticGyroZ +
        " pulse_axis=" + pulseAxis;

    private static long StopwatchFrequency => System.Diagnostics.Stopwatch.Frequency;
}

public readonly record struct ProfessionalHidAuditSnapshot(
    ProfessionalHidAuditMode Mode,
    ProfessionalHidAuditResult Result,
    string Error,
    string ReportBuilderName,
    bool LegacyPs5MapperAppliedAfterProfessionalOutput,
    int ReportId,
    string ReportIdLabel,
    int ReportLength,
    short SelectedOutputDsRawX,
    short SelectedOutputDsRawY,
    short SelectedOutputDsRawZ,
    double SelectedOutputGyroXDps,
    double SelectedOutputGyroYDps,
    double SelectedOutputGyroZDps,
    short FinalPackGyroXRaw,
    short FinalPackGyroYRaw,
    short FinalPackGyroZRaw,
    short FinalReportDecodedGyroXRaw,
    short FinalReportDecodedGyroYRaw,
    short FinalReportDecodedGyroZRaw,
    double ExternalEstimatedGyroXDps,
    double ExternalEstimatedGyroYDps,
    double ExternalEstimatedGyroZDps,
    short FinalPackAccelXRaw,
    short FinalPackAccelYRaw,
    short FinalPackAccelZRaw,
    short FinalReportDecodedAccelXRaw,
    short FinalReportDecodedAccelYRaw,
    short FinalReportDecodedAccelZRaw,
    int GyroReportOffsetX,
    int GyroReportOffsetY,
    int GyroReportOffsetZ,
    int AccelReportOffsetX,
    int AccelReportOffsetY,
    int AccelReportOffsetZ,
    string GyroReportBytesHex,
    string AccelReportBytesHex,
    string ReportHexHead,
    string ReportHexImuWindow)
{
    public string CsvValue => string.Join(
        ',',
        Mode,
        ReportIdLabel,
        ReportLength.ToString(CultureInfo.InvariantCulture),
        FinalPackGyroXRaw.ToString(CultureInfo.InvariantCulture),
        FinalPackGyroYRaw.ToString(CultureInfo.InvariantCulture),
        FinalPackGyroZRaw.ToString(CultureInfo.InvariantCulture),
        FinalReportDecodedGyroXRaw.ToString(CultureInfo.InvariantCulture),
        FinalReportDecodedGyroYRaw.ToString(CultureInfo.InvariantCulture),
        FinalReportDecodedGyroZRaw.ToString(CultureInfo.InvariantCulture),
        ExternalEstimatedGyroXDps.ToString("0.######", CultureInfo.InvariantCulture),
        ExternalEstimatedGyroYDps.ToString("0.######", CultureInfo.InvariantCulture),
        ExternalEstimatedGyroZDps.ToString("0.######", CultureInfo.InvariantCulture),
        FinalPackAccelXRaw.ToString(CultureInfo.InvariantCulture),
        FinalPackAccelYRaw.ToString(CultureInfo.InvariantCulture),
        FinalPackAccelZRaw.ToString(CultureInfo.InvariantCulture),
        FinalReportDecodedAccelXRaw.ToString(CultureInfo.InvariantCulture),
        FinalReportDecodedAccelYRaw.ToString(CultureInfo.InvariantCulture),
        FinalReportDecodedAccelZRaw.ToString(CultureInfo.InvariantCulture),
        GyroReportOffsetX.ToString(CultureInfo.InvariantCulture),
        GyroReportOffsetY.ToString(CultureInfo.InvariantCulture),
        GyroReportOffsetZ.ToString(CultureInfo.InvariantCulture),
        AccelReportOffsetX.ToString(CultureInfo.InvariantCulture),
        AccelReportOffsetY.ToString(CultureInfo.InvariantCulture),
        AccelReportOffsetZ.ToString(CultureInfo.InvariantCulture),
        GyroReportBytesHex,
        AccelReportBytesHex,
        ReportHexImuWindow,
        Result,
        string.IsNullOrWhiteSpace(Error) ? "none" : Error);

    public string Summary =>
        "mode=" + Mode +
        " report_builder=" + ReportBuilderName +
        " legacy_ps5_mapper_after_professional=" + LegacyPs5MapperAppliedAfterProfessionalOutput.ToString().ToLowerInvariant() +
        " report_id=" + ReportIdLabel +
        " len=" + ReportLength +
        " selected_ds_raw=" + SelectedOutputDsRawX + "," + SelectedOutputDsRawY + "," + SelectedOutputDsRawZ +
        " final_pack=" + FinalPackGyroXRaw + "," + FinalPackGyroYRaw + "," + FinalPackGyroZRaw +
        " decoded=" + FinalReportDecodedGyroXRaw + "," + FinalReportDecodedGyroYRaw + "," + FinalReportDecodedGyroZRaw +
        " external_estimated_gyro_dps=" +
        ExternalEstimatedGyroXDps.ToString("0.###", CultureInfo.InvariantCulture) + "," +
        ExternalEstimatedGyroYDps.ToString("0.###", CultureInfo.InvariantCulture) + "," +
        ExternalEstimatedGyroZDps.ToString("0.###", CultureInfo.InvariantCulture) +
        " offsets=" + GyroReportOffsetX + "," + GyroReportOffsetY + "," + GyroReportOffsetZ +
        " result=" + Result;

    public string Detail =>
        Summary +
        " selected_output_gyro_dps=" +
        SelectedOutputGyroXDps.ToString("0.######", CultureInfo.InvariantCulture) + "," +
        SelectedOutputGyroYDps.ToString("0.######", CultureInfo.InvariantCulture) + "," +
        SelectedOutputGyroZDps.ToString("0.######", CultureInfo.InvariantCulture) +
        " final_report_decoded_accel_raw=" +
        FinalReportDecodedAccelXRaw + "," + FinalReportDecodedAccelYRaw + "," + FinalReportDecodedAccelZRaw +
        " gyro_bytes_hex=" + GyroReportBytesHex +
        " accel_bytes_hex=" + AccelReportBytesHex +
        " final_report_hex_head=" + ReportHexHead +
        " final_report_hex_imu_window=" + ReportHexImuWindow;
}

public static class ProfessionalHidReportAuditor
{
    public static ProfessionalHidAuditSnapshot ApplyAndAudit(
        byte[] report,
        DualSenseImuRawSample selectedOutput,
        ImuPhysicalSample? selectedOutputPhysical,
        ProfessionalHidAuditControlState control)
    {
        if (report.Length < DualSenseProfessionalHidLayout.ReportLength)
        {
            return BuildTooShort(report, selectedOutput, selectedOutputPhysical, control);
        }

        short packGyroX = selectedOutput.GyroX;
        short packGyroY = selectedOutput.GyroY;
        short packGyroZ = selectedOutput.GyroZ;
        ProfessionalHidAuditResult? forcedResult = null;

        switch (control.Mode)
        {
            case ProfessionalHidAuditMode.ForceFinalGyroZero:
                packGyroX = 0;
                packGyroY = 0;
                packGyroZ = 0;
                forcedResult = ProfessionalHidAuditResult.FORCED_ZERO;
                break;
            case ProfessionalHidAuditMode.ForceFinalGyroStaticRaw:
                packGyroX = control.StaticGyroX;
                packGyroY = control.StaticGyroY;
                packGyroZ = control.StaticGyroZ;
                break;
            case ProfessionalHidAuditMode.ForceFinalGyroSyntheticPulse:
                packGyroX = control.PulseGyroX;
                packGyroY = control.PulseGyroY;
                packGyroZ = control.PulseGyroZ;
                forcedResult = control.PulseActive
                    ? ProfessionalHidAuditResult.SYNTHETIC_PULSE
                    : ProfessionalHidAuditResult.FORCED_ZERO;
                break;
        }

        BinaryPrimitives.WriteInt16LittleEndian(
            report.AsSpan(DualSenseProfessionalHidLayout.GyroXOffset, 2),
            packGyroX);
        BinaryPrimitives.WriteInt16LittleEndian(
            report.AsSpan(DualSenseProfessionalHidLayout.GyroYOffset, 2),
            packGyroY);
        BinaryPrimitives.WriteInt16LittleEndian(
            report.AsSpan(DualSenseProfessionalHidLayout.GyroZOffset, 2),
            packGyroZ);

        short decodedGyroX = ReadI16(report, DualSenseProfessionalHidLayout.GyroXOffset);
        short decodedGyroY = ReadI16(report, DualSenseProfessionalHidLayout.GyroYOffset);
        short decodedGyroZ = ReadI16(report, DualSenseProfessionalHidLayout.GyroZOffset);
        short decodedAccelX = ReadI16(report, DualSenseProfessionalHidLayout.AccelXOffset);
        short decodedAccelY = ReadI16(report, DualSenseProfessionalHidLayout.AccelYOffset);
        short decodedAccelZ = ReadI16(report, DualSenseProfessionalHidLayout.AccelZOffset);

        ProfessionalHidAuditResult result = forcedResult ?? ProfessionalHidAuditResult.OK;
        string error = "";
        if (!NearlyEqual(packGyroX, decodedGyroX) ||
            !NearlyEqual(packGyroY, decodedGyroY) ||
            !NearlyEqual(packGyroZ, decodedGyroZ))
        {
            result = ProfessionalHidAuditResult.MISMATCH_GYRO;
            error = "final_hid_gyro_mismatch";
        }
        else if (!NearlyEqual(selectedOutput.AccelX, decodedAccelX) ||
                 !NearlyEqual(selectedOutput.AccelY, decodedAccelY) ||
                 !NearlyEqual(selectedOutput.AccelZ, decodedAccelZ))
        {
            result = ProfessionalHidAuditResult.MISMATCH_ACCEL;
            error = "final_hid_accel_mismatch";
        }

        return new ProfessionalHidAuditSnapshot(
            control.Mode,
            result,
            error,
            DualSenseProfessionalHidLayout.BuilderName,
            LegacyPs5MapperAppliedAfterProfessionalOutput: false,
            DualSenseProfessionalHidLayout.ReportId,
            DualSenseProfessionalHidLayout.ReportIdLabel,
            report.Length,
            selectedOutput.GyroX,
            selectedOutput.GyroY,
            selectedOutput.GyroZ,
            selectedOutputPhysical?.GyroXDps ?? 0,
            selectedOutputPhysical?.GyroYDps ?? 0,
            selectedOutputPhysical?.GyroZDps ?? 0,
            packGyroX,
            packGyroY,
            packGyroZ,
            decodedGyroX,
            decodedGyroY,
            decodedGyroZ,
            decodedGyroX / ProfessionalImuConverter.DualSenseGyroRawPerDps,
            decodedGyroY / ProfessionalImuConverter.DualSenseGyroRawPerDps,
            decodedGyroZ / ProfessionalImuConverter.DualSenseGyroRawPerDps,
            selectedOutput.AccelX,
            selectedOutput.AccelY,
            selectedOutput.AccelZ,
            decodedAccelX,
            decodedAccelY,
            decodedAccelZ,
            DualSenseProfessionalHidLayout.GyroXOffset,
            DualSenseProfessionalHidLayout.GyroYOffset,
            DualSenseProfessionalHidLayout.GyroZOffset,
            DualSenseProfessionalHidLayout.AccelXOffset,
            DualSenseProfessionalHidLayout.AccelYOffset,
            DualSenseProfessionalHidLayout.AccelZOffset,
            Hex(report.AsSpan(DualSenseProfessionalHidLayout.GyroXOffset, 6)),
            Hex(report.AsSpan(DualSenseProfessionalHidLayout.AccelXOffset, 6)),
            Hex(report.AsSpan(0, Math.Min(report.Length, 16))),
            Hex(report.AsSpan(DualSenseProfessionalHidLayout.GyroXOffset, 12)));
    }

    private static ProfessionalHidAuditSnapshot BuildTooShort(
        byte[] report,
        DualSenseImuRawSample selectedOutput,
        ImuPhysicalSample? selectedOutputPhysical,
        ProfessionalHidAuditControlState control)
    {
        return new ProfessionalHidAuditSnapshot(
            control.Mode,
            ProfessionalHidAuditResult.REPORT_TOO_SHORT,
            "report_too_short",
            DualSenseProfessionalHidLayout.BuilderName,
            false,
            DualSenseProfessionalHidLayout.ReportId,
            DualSenseProfessionalHidLayout.ReportIdLabel,
            report.Length,
            selectedOutput.GyroX,
            selectedOutput.GyroY,
            selectedOutput.GyroZ,
            selectedOutputPhysical?.GyroXDps ?? 0,
            selectedOutputPhysical?.GyroYDps ?? 0,
            selectedOutputPhysical?.GyroZDps ?? 0,
            selectedOutput.GyroX,
            selectedOutput.GyroY,
            selectedOutput.GyroZ,
            0,
            0,
            0,
            0,
            0,
            0,
            selectedOutput.AccelX,
            selectedOutput.AccelY,
            selectedOutput.AccelZ,
            0,
            0,
            0,
            DualSenseProfessionalHidLayout.GyroXOffset,
            DualSenseProfessionalHidLayout.GyroYOffset,
            DualSenseProfessionalHidLayout.GyroZOffset,
            DualSenseProfessionalHidLayout.AccelXOffset,
            DualSenseProfessionalHidLayout.AccelYOffset,
            DualSenseProfessionalHidLayout.AccelZOffset,
            "",
            "",
            Hex(report.AsSpan(0, Math.Min(report.Length, 16))),
            "");
    }

    private static short ReadI16(byte[] report, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(offset, 2));

    private static bool NearlyEqual(short a, short b) =>
        Math.Abs(a - b) <= 1;

    private static string Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
