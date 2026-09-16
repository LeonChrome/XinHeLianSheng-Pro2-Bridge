using System;
using System.Buffers.Binary;

namespace Y700Switch2V60Viiper;

public sealed class Pro2HidReportParser
{
    private const int Fd2FullReportMinLength = 60;
    private const ushort Center12 = GamepadState.AxisCenter;
    private const ushort CenterLearnMaxDelta = 256;
    private const ushort AxisDeadzone = 64;
    private const ushort FullScaleRange = 1600;
    private readonly object gate = new();
    private readonly AxisCalibration standardAxis = new();
    private readonly AxisCalibration legacyAxis = new();
    private readonly MotionCalibration motion = new();

    public string StartManualGyroCalibration()
    {
        lock (gate)
        {
            return motion.StartManualCalibration();
        }
    }

    public string GyroCalibrationSummary
    {
        get
        {
            lock (gate)
            {
                return motion.Summary;
            }
        }
    }

    public string SetStickCalibration(Pro2StickCalibrationProfile? profile)
    {
        lock (gate)
        {
            standardAxis.ApplyProfile(profile);
            legacyAxis.ApplyProfile(profile);
            return standardAxis.Summary;
        }
    }

    public string StartManualStickCenterCalibration()
    {
        lock (gate)
        {
            return standardAxis.StartCenterCapture();
        }
    }

    public Pro2StickCalibrationResult CompleteManualStickCenterCalibration()
    {
        lock (gate)
        {
            Pro2StickCalibrationResult result = standardAxis.CompleteCenterCapture();
            if (result.Success && result.Profile != null)
            {
                legacyAxis.ApplyProfile(result.Profile);
            }
            return result;
        }
    }

    public string StartManualStickRangeCalibration()
    {
        lock (gate)
        {
            return standardAxis.StartRangeCapture();
        }
    }

    public Pro2StickCalibrationResult CompleteManualStickRangeCalibration()
    {
        lock (gate)
        {
            Pro2StickCalibrationResult result = standardAxis.CompleteRangeCapture();
            if (result.Success && result.Profile != null)
            {
                legacyAxis.ApplyProfile(result.Profile);
            }
            return result;
        }
    }

    public Pro2StickCalibrationProfile StickCalibrationProfile
    {
        get
        {
            lock (gate)
            {
                return standardAxis.Profile;
            }
        }
    }

    public Pro2PhysicalStickAxes LastPhysicalStickAxes
    {
        get
        {
            lock (gate)
            {
                return standardAxis.LastRaw;
            }
        }
    }

    public string StickCalibrationSummary
    {
        get
        {
            lock (gate)
            {
                return standardAxis.Summary;
            }
        }
    }

    public bool IsStickCalibrationCaptureActive
    {
        get
        {
            lock (gate)
            {
                return standardAxis.CaptureActive;
            }
        }
    }

    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (gate)
        {
            state = GamepadState.Neutral();
            source = "";
            if (report.IsEmpty)
            {
                return false;
            }

            if (TryParseHidInputReport(report, out state, out source))
            {
                return true;
            }

            // Raw GATT captures used by the ESP32 route are useful in diagnostics and
            // let this parser be tested without a Windows HID handle.
            if (TryParseFd2Payload(report, out state, out source))
            {
                return true;
            }

            if (TryParseLegacyPayload(report, out state, out source))
            {
                return true;
            }

            return false;
        }
    }

    public bool TryParseFd2Payload(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (gate)
        {
            state = GamepadState.Neutral();
            source = "";
            if (report.Length < Fd2FullReportMinLength ||
                !LooksLikeFd2Payload(report))
            {
                return false;
            }

            ParseFd2Payload(report, state);
            source = "fd2_payload";
            return true;
        }
    }

    public bool TryParseLegacyPayload(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (gate)
        {
            state = GamepadState.Neutral();
            source = "";
            if (report.Length < 11 || !LooksLikeLegacyPayload(report))
            {
                return false;
            }

            ParseLegacyPayload(report, state);
            source = "legacy_payload";
            return true;
        }
    }

    public bool TryParsePrimaryProPayload(
        ReadOnlySpan<byte> report,
        out GamepadState state,
        out string source)
    {
        lock (gate)
        {
            state = GamepadState.Neutral();
            source = "";
            if (report.Length < 11 || report[1] != 0x20)
            {
                return false;
            }

            ApplyPrimaryProButtons(state, report[2], report[3], report[4]);
            ApplyAxes(standardAxis,
                state,
                Unpack12X(report, 5),
                Unpack12Y(report, 5),
                Unpack12X(report, 8),
                Unpack12Y(report, 8));
            source = "primary_pro_payload";
            return true;
        }
    }

    public bool TryParseHidInputReport(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (gate)
        {
            state = GamepadState.Neutral();
            source = "";
            if (report.IsEmpty)
            {
                return false;
            }

            byte reportId = report[0];
            if ((reportId == 0x30 || reportId == 0x31 || reportId == 0x21 || reportId == 0x23) &&
                report.Length >= 13)
            {
                ParseStandardReport(report, state);
                source = "switch_pro_standard";
                return true;
            }

            return false;
        }
    }

    private void ParseStandardReport(ReadOnlySpan<byte> report, GamepadState state)
    {
        ApplyStandardButtons(state, report[3], report[4], report[5]);
        ApplyAxes(standardAxis,
            state,
            Unpack12X(report, 6),
            Unpack12Y(report, 6),
            Unpack12X(report, 9),
            Unpack12Y(report, 9));

        if (report.Length >= 49)
        {
            AttachRawImuSamples(state, report, 13, 3);
            ApplyMotionSample(state, report.Slice(37, 12));
        }
        else if (report.Length >= 25)
        {
            AttachRawImuSamples(state, report, 13, 1);
            ApplyMotionSample(state, report.Slice(13, 12));
        }
    }

    private void ParseFd2Payload(ReadOnlySpan<byte> payload, GamepadState state)
    {
        uint buttons = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        ApplyFd2Buttons(state, buttons);
        ApplyAxes(standardAxis,
            state,
            Unpack12X(payload, 10),
            Unpack12Y(payload, 10),
            Unpack12X(payload, 13),
            Unpack12Y(payload, 13));

        if (payload.Length >= 60)
        {
            state.MotionTimestampUs =
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(42, 4));
            AttachRawImuSamples(state, payload, 48, 3);
            // Common FD2 carries one IMU sample after its temperature field.
            ApplyMotionSample(state, payload.Slice(48, 12));
        }
    }

    private void ParseLegacyPayload(ReadOnlySpan<byte> payload, GamepadState state)
    {
        ApplyStandardButtons(state, payload[2], payload[3], payload[4]);
        ApplyAxes(legacyAxis,
            state,
            Unpack12X(payload, 5),
            Unpack12Y(payload, 5),
            Unpack12X(payload, 8),
            Unpack12Y(payload, 8));
    }

    private static bool LooksLikeFd2Payload(ReadOnlySpan<byte> payload)
    {
        uint buttons = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        return (buttons & 0xFC300020u) == 0;
    }

    private static bool LooksLikeLegacyPayload(ReadOnlySpan<byte> payload)
    {
        return payload[0] != 0x30 && payload[0] != 0x31 && payload[0] != 0x21 && payload[0] != 0x23;
    }

    private static void ApplyStandardButtons(GamepadState state, byte right, byte shared, byte left)
    {
        state.Buttons = GamepadButtons.None;
        Set(state, GamepadButtons.West, (right & 0x01) != 0);
        Set(state, GamepadButtons.North, (right & 0x02) != 0);
        Set(state, GamepadButtons.South, (right & 0x04) != 0);
        Set(state, GamepadButtons.East, (right & 0x08) != 0);
        Set(state, GamepadButtons.R1, (right & 0x40) != 0);
        Set(state, GamepadButtons.R2, (right & 0x80) != 0);
        Set(state, GamepadButtons.Back, (shared & 0x01) != 0);
        Set(state, GamepadButtons.Start, (shared & 0x02) != 0);
        Set(state, GamepadButtons.RightStick, (shared & 0x04) != 0);
        Set(state, GamepadButtons.LeftStick, (shared & 0x08) != 0);
        Set(state, GamepadButtons.Home, (shared & 0x10) != 0);
        Set(state, GamepadButtons.Capture, (shared & 0x20) != 0);
        Set(state, GamepadButtons.DPadDown, (left & 0x01) != 0);
        Set(state, GamepadButtons.DPadUp, (left & 0x02) != 0);
        Set(state, GamepadButtons.DPadRight, (left & 0x04) != 0);
        Set(state, GamepadButtons.DPadLeft, (left & 0x08) != 0);
        Set(state, GamepadButtons.L1, (left & 0x40) != 0);
        Set(state, GamepadButtons.L2, (left & 0x80) != 0);
        state.L2 = state.IsPressed(GamepadButtons.L2) ? GamepadState.TriggerMax : (ushort)0;
        state.R2 = state.IsPressed(GamepadButtons.R2) ? GamepadState.TriggerMax : (ushort)0;
    }

    private static void ApplyFd2Buttons(GamepadState state, uint buttons)
    {
        state.Buttons = GamepadButtons.None;
        Set(state, GamepadButtons.West, (buttons & 0x00000001) != 0);
        Set(state, GamepadButtons.North, (buttons & 0x00000002) != 0);
        Set(state, GamepadButtons.South, (buttons & 0x00000004) != 0);
        Set(state, GamepadButtons.East, (buttons & 0x00000008) != 0);
        Set(state, GamepadButtons.R1, (buttons & 0x00000040) != 0);
        Set(state, GamepadButtons.R2, (buttons & 0x00000080) != 0);
        Set(state, GamepadButtons.Back, (buttons & 0x00000100) != 0);
        Set(state, GamepadButtons.Start, (buttons & 0x00000200) != 0);
        Set(state, GamepadButtons.RightStick, (buttons & 0x00000400) != 0);
        Set(state, GamepadButtons.LeftStick, (buttons & 0x00000800) != 0);
        Set(state, GamepadButtons.Home, (buttons & 0x00001000) != 0);
        Set(state, GamepadButtons.Capture, (buttons & 0x00002000) != 0);
        Set(state, GamepadButtons.Aux, (buttons & 0x00004000) != 0);
        Set(state, GamepadButtons.DPadDown, (buttons & 0x00010000) != 0);
        Set(state, GamepadButtons.DPadUp, (buttons & 0x00020000) != 0);
        Set(state, GamepadButtons.DPadRight, (buttons & 0x00040000) != 0);
        Set(state, GamepadButtons.DPadLeft, (buttons & 0x00080000) != 0);
        Set(state, GamepadButtons.L1, (buttons & 0x00400000) != 0);
        Set(state, GamepadButtons.L2, (buttons & 0x00800000) != 0);
        Set(state, GamepadButtons.PaddleRight, (buttons & 0x01000000) != 0);
        Set(state, GamepadButtons.PaddleLeft, (buttons & 0x02000000) != 0);
        state.L2 = state.IsPressed(GamepadButtons.L2) ? GamepadState.TriggerMax : (ushort)0;
        state.R2 = state.IsPressed(GamepadButtons.R2) ? GamepadState.TriggerMax : (ushort)0;
    }

    private static void ApplyPrimaryProButtons(
        GamepadState state,
        byte right,
        byte left,
        byte shared)
    {
        state.Buttons = GamepadButtons.None;
        Set(state, GamepadButtons.South, (right & 0x01) != 0);
        Set(state, GamepadButtons.East, (right & 0x02) != 0);
        Set(state, GamepadButtons.West, (right & 0x04) != 0);
        Set(state, GamepadButtons.North, (right & 0x08) != 0);
        Set(state, GamepadButtons.R1, (right & 0x10) != 0);
        Set(state, GamepadButtons.R2, (right & 0x20) != 0);
        Set(state, GamepadButtons.Start, (right & 0x40) != 0);
        Set(state, GamepadButtons.RightStick, (right & 0x80) != 0);

        Set(state, GamepadButtons.DPadDown, (left & 0x01) != 0);
        Set(state, GamepadButtons.DPadRight, (left & 0x02) != 0);
        Set(state, GamepadButtons.DPadLeft, (left & 0x04) != 0);
        Set(state, GamepadButtons.DPadUp, (left & 0x08) != 0);
        Set(state, GamepadButtons.L1, (left & 0x10) != 0);
        Set(state, GamepadButtons.L2, (left & 0x20) != 0);
        Set(state, GamepadButtons.Back, (left & 0x40) != 0);
        Set(state, GamepadButtons.LeftStick, (left & 0x80) != 0);

        Set(state, GamepadButtons.Home, (shared & 0x01) != 0);
        Set(state, GamepadButtons.Capture, (shared & 0x02) != 0);
        Set(state, GamepadButtons.PaddleRight, (shared & 0x04) != 0);
        Set(state, GamepadButtons.PaddleLeft, (shared & 0x08) != 0);
        Set(state, GamepadButtons.Aux, (shared & 0x10) != 0);
        state.L2 = state.IsPressed(GamepadButtons.L2) ? GamepadState.TriggerMax : (ushort)0;
        state.R2 = state.IsPressed(GamepadButtons.R2) ? GamepadState.TriggerMax : (ushort)0;
    }

    private static void Set(GamepadState state, GamepadButtons button, bool pressed)
    {
        if (pressed)
        {
            state.Buttons |= button;
        }
    }

    private static ushort Unpack12X(ReadOnlySpan<byte> data, int offset)
    {
        return Clamp12(data[offset] | ((data[offset + 1] & 0x0F) << 8));
    }

    private static ushort Unpack12Y(ReadOnlySpan<byte> data, int offset)
    {
        return Clamp12(((data[offset + 1] >> 4) & 0x0F) | (data[offset + 2] << 4));
    }

    private static ushort Clamp12(int value)
    {
        if (value < 0) return 0;
        if (value > GamepadState.AxisMax) return GamepadState.AxisMax;
        return (ushort)value;
    }

    private static void ApplyAxes(
        AxisCalibration calibration,
        GamepadState state,
        ushort lx,
        ushort ly,
        ushort rx,
        ushort ry)
    {
        calibration.ObserveRaw(lx, ly, rx, ry);
        calibration.LearnIfCentered(state.Buttons, lx, ly, rx, ry);
        state.Lx = calibration.Normalize(lx, StickAxis.Lx);
        state.Ly = calibration.Normalize(ly, StickAxis.Ly);
        state.Rx = calibration.Normalize(rx, StickAxis.Rx);
        state.Ry = calibration.Normalize(ry, StickAxis.Ry);
    }

    private void ApplyMotionSample(GamepadState state, ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 12)
        {
            return;
        }

        state.AccelValid = true;
        state.GyroValid = true;
        state.AccelX = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(0, 2));
        state.AccelY = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(2, 2));
        state.AccelZ = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(4, 2));
        state.GyroX = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(6, 2));
        state.GyroY = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(8, 2));
        state.GyroZ = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(10, 2));
        motion.Apply(state);
    }

    private static void AttachRawImuSamples(
        GamepadState state,
        ReadOnlySpan<byte> payload,
        int offset,
        int maxSamples)
    {
        SwitchImuRawBlock block =
            ProfessionalImuConverter.DecodeSwitchImuSamples(payload, offset, maxSamples);
        state.SwitchRawImuSamples = block.Samples;
        state.SwitchRawImuOffset = block.Offset;
        state.SwitchRawImuBytesHex = block.RawBytesHex;
    }

    private sealed class AxisCalibration
    {
        private const int SamplesRequired = 20;
        private const int CenterCaptureSamplesRequired = 16;
        private const int CenterCaptureMaximumSpread = 96;
        private const int RangeCaptureSamplesRequired = 60;
        private const int MinimumDirectionalRange = 900;
        private const double CalibratedEndpointReserve = 0.97;
        private uint sampleCount;
        private uint sumLx;
        private uint sumLy;
        private uint sumRx;
        private uint sumRy;
        private Pro2StickCalibrationProfile profile = new();
        private AxisCapture? capture;

        public ushort CenterLx { get; private set; } = Center12;
        public ushort CenterLy { get; private set; } = Center12;
        public ushort CenterRx { get; private set; } = Center12;
        public ushort CenterRy { get; private set; } = Center12;
        public Pro2PhysicalStickAxes LastRaw { get; private set; } =
            new(Center12, Center12, Center12, Center12);
        public Pro2StickCalibrationProfile Profile => profile;
        public bool CaptureActive => capture != null;
        public string Summary =>
            profile.Summary +
            (capture == null
                ? ""
                : " capture=" + capture.Mode.ToString().ToLowerInvariant() +
                  " samples=" + capture.SampleCount);

        public void ApplyProfile(Pro2StickCalibrationProfile? saved)
        {
            Pro2StickCalibrationProfile normalized =
                Pro2StickCalibrationProfile.Normalize(saved);
            if (profile == normalized)
            {
                return;
            }

            profile = normalized;
            CenterLx = profile.CenterLx;
            CenterLy = profile.CenterLy;
            CenterRx = profile.CenterRx;
            CenterRy = profile.CenterRy;
            capture = null;
            ResetSums();
        }

        public void ObserveRaw(ushort lx, ushort ly, ushort rx, ushort ry)
        {
            LastRaw = new Pro2PhysicalStickAxes(lx, ly, rx, ry);
            capture?.Add(lx, ly, rx, ry);
        }

        public string StartCenterCapture()
        {
            capture = new AxisCapture(AxisCaptureMode.Center);
            return "status=started mode=center duration_seconds=2 keep_sticks_released=1";
        }

        public Pro2StickCalibrationResult CompleteCenterCapture()
        {
            AxisCapture? completed = TakeCapture(AxisCaptureMode.Center);
            if (completed == null)
            {
                return Failed("status=rejected mode=center reason=not_started");
            }
            if (completed.SampleCount < CenterCaptureSamplesRequired)
            {
                return Failed(
                    "status=rejected mode=center reason=insufficient_samples samples=" +
                    completed.SampleCount);
            }
            if (completed.MaximumSpread > CenterCaptureMaximumSpread)
            {
                return Failed(
                    "status=rejected mode=center reason=sticks_moved max_spread=" +
                    completed.MaximumSpread);
            }

            Pro2PhysicalStickAxes center = completed.Average;
            if (!NearFactoryCenterForManualCapture(center.Lx) ||
                !NearFactoryCenterForManualCapture(center.Ly) ||
                !NearFactoryCenterForManualCapture(center.Rx) ||
                !NearFactoryCenterForManualCapture(center.Ry))
            {
                return Failed(
                    "status=rejected mode=center reason=outside_safe_center_window " +
                    center.TelemetryValue);
            }

            Pro2StickCalibrationProfile candidate =
                Pro2StickCalibrationProfile.Normalize(profile with
                {
                    CenterCalibrated = true,
                    CenterLx = center.Lx,
                    CenterLy = center.Ly,
                    CenterRx = center.Rx,
                    CenterRy = center.Ry,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            ApplyProfile(candidate);
            return new Pro2StickCalibrationResult(
                true,
                "status=calibrated mode=center samples=" + completed.SampleCount +
                " max_spread=" + completed.MaximumSpread + " " + profile.Summary,
                profile);
        }

        public string StartRangeCapture()
        {
            if (!profile.CenterCalibrated)
            {
                return "status=rejected mode=range reason=center_calibration_required";
            }

            capture = new AxisCapture(AxisCaptureMode.Range);
            return "status=started mode=range duration_seconds=8 rotate_both_sticks_full_circle=1";
        }

        public Pro2StickCalibrationResult CompleteRangeCapture()
        {
            AxisCapture? completed = TakeCapture(AxisCaptureMode.Range);
            if (completed == null)
            {
                return Failed("status=rejected mode=range reason=not_started");
            }
            if (completed.SampleCount < RangeCaptureSamplesRequired)
            {
                return Failed(
                    "status=rejected mode=range reason=insufficient_samples samples=" +
                    completed.SampleCount);
            }

            Pro2PhysicalStickAxes minimum = completed.Minimum;
            Pro2PhysicalStickAxes maximum = completed.Maximum;
            string spans = RangeSpans(minimum, maximum);
            if (!HasRequiredRange(minimum.Lx, profile.CenterLx, maximum.Lx) ||
                !HasRequiredRange(minimum.Ly, profile.CenterLy, maximum.Ly) ||
                !HasRequiredRange(minimum.Rx, profile.CenterRx, maximum.Rx) ||
                !HasRequiredRange(minimum.Ry, profile.CenterRy, maximum.Ry))
            {
                return Failed(
                    "status=rejected mode=range reason=incomplete_directional_travel " + spans);
            }

            Pro2StickCalibrationProfile candidate =
                Pro2StickCalibrationProfile.Normalize(profile with
                {
                    RangeCalibrated = true,
                    MinLx = minimum.Lx,
                    MaxLx = maximum.Lx,
                    MinLy = minimum.Ly,
                    MaxLy = maximum.Ly,
                    MinRx = minimum.Rx,
                    MaxRx = maximum.Rx,
                    MinRy = minimum.Ry,
                    MaxRy = maximum.Ry,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            ApplyProfile(candidate);
            return new Pro2StickCalibrationResult(
                true,
                "status=calibrated mode=range samples=" + completed.SampleCount +
                " endpoint_reserve_percent=3 " + spans + " " + profile.Summary,
                profile);
        }

        public void LearnIfCentered(GamepadButtons buttons, ushort lx, ushort ly, ushort rx, ushort ry)
        {
            if (profile.CenterCalibrated || capture != null)
            {
                return;
            }

            if (buttons == GamepadButtons.None &&
                NearFactoryCenter(lx) &&
                NearFactoryCenter(ly) &&
                NearFactoryCenter(rx) &&
                NearFactoryCenter(ry))
            {
                sumLx += lx;
                sumLy += ly;
                sumRx += rx;
                sumRy += ry;
                sampleCount++;
                if (sampleCount >= SamplesRequired)
                {
                    CenterLx = (ushort)(sumLx / sampleCount);
                    CenterLy = (ushort)(sumLy / sampleCount);
                    CenterRx = (ushort)(sumRx / sampleCount);
                    CenterRy = (ushort)(sumRy / sampleCount);
                    ResetSums();
                }
            }
            else
            {
                ResetSums();
            }
        }

        public ushort Normalize(ushort value, StickAxis axis)
        {
            (ushort center, ushort minimum, ushort maximum) = AxisParameters(axis);
            int delta = value - center;
            bool negative = delta < 0;
            int magnitude = negative ? -delta : delta;
            if (magnitude <= AxisDeadzone)
            {
                return Center12;
            }

            int directionalRange = FullScaleRange;
            if (profile.RangeCalibrated)
            {
                int measuredRange = negative ? center - minimum : maximum - center;
                directionalRange = Math.Max(
                    AxisDeadzone + 1,
                    (int)Math.Round(measuredRange * CalibratedEndpointReserve));
            }
            int usable = directionalRange - AxisDeadzone;
            int target = negative ? Center12 : GamepadState.AxisMax - Center12;
            int scaled = ((magnitude - AxisDeadzone) * target + usable / 2) / usable;
            if (scaled > target)
            {
                scaled = target;
            }

            int output = negative ? Center12 - scaled : Center12 + scaled;
            return Clamp12(output);
        }

        private (ushort Center, ushort Minimum, ushort Maximum) AxisParameters(StickAxis axis)
        {
            return axis switch
            {
                StickAxis.Lx => (CenterLx, profile.MinLx, profile.MaxLx),
                StickAxis.Ly => (CenterLy, profile.MinLy, profile.MaxLy),
                StickAxis.Rx => (CenterRx, profile.MinRx, profile.MaxRx),
                StickAxis.Ry => (CenterRy, profile.MinRy, profile.MaxRy),
                _ => (Center12, 0, GamepadState.AxisMax)
            };
        }

        private AxisCapture? TakeCapture(AxisCaptureMode expectedMode)
        {
            AxisCapture? completed = capture;
            capture = null;
            return completed?.Mode == expectedMode ? completed : null;
        }

        private Pro2StickCalibrationResult Failed(string message)
        {
            return new Pro2StickCalibrationResult(false, message, profile);
        }

        private static bool HasRequiredRange(ushort minimum, ushort center, ushort maximum)
        {
            return minimum < center &&
                   maximum > center &&
                   center - minimum >= MinimumDirectionalRange &&
                   maximum - center >= MinimumDirectionalRange;
        }

        private string RangeSpans(
            Pro2PhysicalStickAxes minimum,
            Pro2PhysicalStickAxes maximum)
        {
            return "span_lx=" + (CenterLx - minimum.Lx) + "/" + (maximum.Lx - CenterLx) +
                   " span_ly=" + (CenterLy - minimum.Ly) + "/" + (maximum.Ly - CenterLy) +
                   " span_rx=" + (CenterRx - minimum.Rx) + "/" + (maximum.Rx - CenterRx) +
                   " span_ry=" + (CenterRy - minimum.Ry) + "/" + (maximum.Ry - CenterRy);
        }

        private static bool NearFactoryCenterForManualCapture(ushort value)
        {
            return value is >= Center12 - 512 and <= Center12 + 512;
        }

        private static bool NearFactoryCenter(ushort value)
        {
            int delta = value - Center12;
            if (delta < 0)
            {
                delta = -delta;
            }
            return delta <= CenterLearnMaxDelta;
        }

        private void ResetSums()
        {
            sampleCount = 0;
            sumLx = 0;
            sumLy = 0;
            sumRx = 0;
            sumRy = 0;
        }

        private enum AxisCaptureMode
        {
            Center,
            Range
        }

        private sealed class AxisCapture
        {
            private ulong sumLx;
            private ulong sumLy;
            private ulong sumRx;
            private ulong sumRy;

            public AxisCapture(AxisCaptureMode mode)
            {
                Mode = mode;
            }

            public AxisCaptureMode Mode { get; }
            public uint SampleCount { get; private set; }
            public Pro2PhysicalStickAxes Minimum { get; private set; } =
                new(ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);
            public Pro2PhysicalStickAxes Maximum { get; private set; }
            public Pro2PhysicalStickAxes Average =>
                SampleCount == 0
                    ? new Pro2PhysicalStickAxes(Center12, Center12, Center12, Center12)
                    : new Pro2PhysicalStickAxes(
                        RoundedAverage(sumLx, SampleCount),
                        RoundedAverage(sumLy, SampleCount),
                        RoundedAverage(sumRx, SampleCount),
                        RoundedAverage(sumRy, SampleCount));
            public int MaximumSpread => Math.Max(
                Math.Max(Maximum.Lx - Minimum.Lx, Maximum.Ly - Minimum.Ly),
                Math.Max(Maximum.Rx - Minimum.Rx, Maximum.Ry - Minimum.Ry));

            public void Add(ushort lx, ushort ly, ushort rx, ushort ry)
            {
                Minimum = new Pro2PhysicalStickAxes(
                    Math.Min(Minimum.Lx, lx),
                    Math.Min(Minimum.Ly, ly),
                    Math.Min(Minimum.Rx, rx),
                    Math.Min(Minimum.Ry, ry));
                Maximum = new Pro2PhysicalStickAxes(
                    Math.Max(Maximum.Lx, lx),
                    Math.Max(Maximum.Ly, ly),
                    Math.Max(Maximum.Rx, rx),
                    Math.Max(Maximum.Ry, ry));
                sumLx += lx;
                sumLy += ly;
                sumRx += rx;
                sumRy += ry;
                SampleCount++;
            }

            private static ushort RoundedAverage(ulong sum, uint count)
            {
                return (ushort)((sum + count / 2u) / count);
            }
        }
    }

    private enum StickAxis
    {
        Lx,
        Ly,
        Rx,
        Ry
    }

    private sealed class MotionCalibration
    {
        private const int AutoSamplesRequired = 64;
        private const int ManualSamplesRequired = 200;
        private const int GyroStationaryMaxAbs = 256;
        private const int AccelMagnitudeMin = 3000;
        private const int AccelMagnitudeMax = 5400;
        private const double MaxGyroStdRaw = 3.5;
        private const double MaxAccelNormStdRaw = 12.0;

        private long gyroXSum;
        private long gyroYSum;
        private long gyroZSum;
        private long gyroXSquareSum;
        private long gyroYSquareSum;
        private long gyroZSquareSum;
        private double accelNormSum;
        private double accelNormSquareSum;
        private int sampleCount;
        private bool calibrated;
        private bool manualCalibrating;
        private double gyroXBias;
        private double gyroYBias;
        private double gyroZBias;
        private string lastResult = "auto_waiting_stationary";

        public string Summary =>
            "status=" + (manualCalibrating ? "manual_collecting" : calibrated ? "calibrated" : "not_calibrated") +
            " samples=" + sampleCount +
            " bias_raw=" + gyroXBias.ToString("0.###") + "," +
            gyroYBias.ToString("0.###") + "," +
            gyroZBias.ToString("0.###") +
            " result=" + lastResult;

        public string StartManualCalibration()
        {
            manualCalibrating = true;
            lastResult = "manual_collecting_keep_controller_still";
            ResetLearningWindow();
            return "陀螺仪三秒校准已开始；请将手柄静置在稳定平面。";
        }

        public void Apply(GamepadState state)
        {
            int accelX = state.AccelX;
            int accelY = state.AccelY;
            int accelZ = state.AccelZ;
            int gyroX = state.GyroX;
            int gyroY = state.GyroY;
            int gyroZ = state.GyroZ;

            bool canLearn =
                ControlsAreIdle(state) &&
                IsStationary(accelX, accelY, accelZ, gyroX, gyroY, gyroZ);

            if (manualCalibrating)
            {
                if (canLearn)
                {
                    Learn(accelX, accelY, accelZ, gyroX, gyroY, gyroZ, ManualSamplesRequired, true);
                }
                else
                {
                    lastResult = "manual_motion_detected_window_restarted";
                    ResetLearningWindow();
                }
            }
            else if (!calibrated)
            {
                if (canLearn)
                {
                    Learn(accelX, accelY, accelZ, gyroX, gyroY, gyroZ, AutoSamplesRequired, false);
                }
                else
                {
                    ResetLearningWindow();
                }
            }

            if (!calibrated)
            {
                return;
            }

            state.GyroX = ClampInt16((int)Math.Round(gyroX - gyroXBias));
            state.GyroY = ClampInt16((int)Math.Round(gyroY - gyroYBias));
            state.GyroZ = ClampInt16((int)Math.Round(gyroZ - gyroZBias));
        }

        private static bool ControlsAreIdle(GamepadState state)
        {
            const int axisTolerance = 128;
            return state.Buttons == GamepadButtons.None &&
                   Math.Abs(state.Lx - Center12) <= axisTolerance &&
                   Math.Abs(state.Ly - Center12) <= axisTolerance &&
                   Math.Abs(state.Rx - Center12) <= axisTolerance &&
                   Math.Abs(state.Ry - Center12) <= axisTolerance;
        }

        private static bool IsStationary(
            int accelX,
            int accelY,
            int accelZ,
            int gyroX,
            int gyroY,
            int gyroZ)
        {
            long accelMagnitudeSquared =
                (long)accelX * accelX +
                (long)accelY * accelY +
                (long)accelZ * accelZ;
            bool plausibleGravity =
                accelMagnitudeSquared >= (long)AccelMagnitudeMin * AccelMagnitudeMin &&
                accelMagnitudeSquared <= (long)AccelMagnitudeMax * AccelMagnitudeMax;
            bool gyroQuiet =
                Math.Abs(gyroX) <= GyroStationaryMaxAbs &&
                Math.Abs(gyroY) <= GyroStationaryMaxAbs &&
                Math.Abs(gyroZ) <= GyroStationaryMaxAbs;
            return plausibleGravity && gyroQuiet;
        }

        private void Learn(
            int accelX,
            int accelY,
            int accelZ,
            int gyroX,
            int gyroY,
            int gyroZ,
            int samplesRequired,
            bool manual)
        {
            gyroXSum += gyroX;
            gyroYSum += gyroY;
            gyroZSum += gyroZ;
            gyroXSquareSum += (long)gyroX * gyroX;
            gyroYSquareSum += (long)gyroY * gyroY;
            gyroZSquareSum += (long)gyroZ * gyroZ;
            double accelNorm = Math.Sqrt(
                (double)accelX * accelX +
                (double)accelY * accelY +
                (double)accelZ * accelZ);
            accelNormSum += accelNorm;
            accelNormSquareSum += accelNorm * accelNorm;
            sampleCount++;
            if (sampleCount < samplesRequired)
            {
                return;
            }

            double gyroXMean = (double)gyroXSum / sampleCount;
            double gyroYMean = (double)gyroYSum / sampleCount;
            double gyroZMean = (double)gyroZSum / sampleCount;
            double gyroXStd = StandardDeviation(gyroXSquareSum, gyroXMean, sampleCount);
            double gyroYStd = StandardDeviation(gyroYSquareSum, gyroYMean, sampleCount);
            double gyroZStd = StandardDeviation(gyroZSquareSum, gyroZMean, sampleCount);
            double accelNormMean = accelNormSum / sampleCount;
            double accelNormVariance = Math.Max(0, accelNormSquareSum / sampleCount - accelNormMean * accelNormMean);
            double accelNormStd = Math.Sqrt(accelNormVariance);
            if (gyroXStd > MaxGyroStdRaw ||
                gyroYStd > MaxGyroStdRaw ||
                gyroZStd > MaxGyroStdRaw ||
                accelNormStd > MaxAccelNormStdRaw)
            {
                lastResult = (manual ? "manual" : "auto") +
                             "_rejected_unstable std_gyro=" +
                             gyroXStd.ToString("0.###") + "," +
                             gyroYStd.ToString("0.###") + "," +
                             gyroZStd.ToString("0.###") +
                             " accel_norm_std=" + accelNormStd.ToString("0.###");
                ResetLearningWindow();
                return;
            }

            gyroXBias = gyroXMean;
            gyroYBias = gyroYMean;
            gyroZBias = gyroZMean;
            calibrated = true;
            manualCalibrating = false;
            lastResult = (manual ? "manual" : "auto") +
                         "_committed std_gyro=" +
                         gyroXStd.ToString("0.###") + "," +
                         gyroYStd.ToString("0.###") + "," +
                         gyroZStd.ToString("0.###") +
                         " accel_norm_std=" + accelNormStd.ToString("0.###");
            ResetLearningWindow();
        }

        private static double StandardDeviation(long squareSum, double mean, int count)
        {
            double variance = Math.Max(0, (double)squareSum / count - mean * mean);
            return Math.Sqrt(variance);
        }

        private static short ClampInt16(int value)
        {
            if (value < short.MinValue) return short.MinValue;
            if (value > short.MaxValue) return short.MaxValue;
            return (short)value;
        }

        private void ResetLearningWindow()
        {
            gyroXSum = 0;
            gyroYSum = 0;
            gyroZSum = 0;
            gyroXSquareSum = 0;
            gyroYSquareSum = 0;
            gyroZSquareSum = 0;
            accelNormSum = 0;
            accelNormSquareSum = 0;
            sampleCount = 0;
        }
    }
}
