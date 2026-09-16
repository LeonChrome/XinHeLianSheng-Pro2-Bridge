using System;
using System.Buffers.Binary;
using Y700Switch2V60Viiper;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Pack12(byte[] data, int offset, ushort x, ushort y)
{
    data[offset] = (byte)(x & 0xff);
    data[offset + 1] = (byte)(((x >> 8) & 0x0f) | ((y & 0x0f) << 4));
    data[offset + 2] = (byte)((y >> 4) & 0xff);
}

static void WriteMotion(
    byte[] data,
    int offset,
    short accelX,
    short accelY,
    short accelZ,
    short gyroX,
    short gyroY,
    short gyroZ)
{
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), accelX);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 2, 2), accelY);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 4, 2), accelZ);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 6, 2), gyroX);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 8, 2), gyroY);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 10, 2), gyroZ);
}

static (int Low, int High) DecodeBleAmplitudes(
    ReadOnlySpan<byte> packet,
    int frameOffset)
{
    ulong value = 0;
    for (int i = 0; i < 5; i++)
    {
        value |= (ulong)packet[frameOffset + i] << (8 * i);
    }
    return (
        (int)((value >> 10) & 0x03ff),
        (int)((value >> 30) & 0x03ff));
}

static byte[] HapticFrame(byte kind, ReadOnlySpan<byte> payload)
{
    byte[] frame = new byte[DualSenseHapticFrame.WireSize];
    frame[0] = kind;
    BinaryPrimitives.WriteUInt16LittleEndian(
        frame.AsSpan(1, 2),
        (ushort)payload.Length);
    payload.CopyTo(frame.AsSpan(4));
    return frame;
}

static byte[] HapticPcmPacket(
    ref double leftPhase,
    ref double rightPhase,
    double leftHz,
    double rightHz)
{
    byte[] packet = new byte[384];
    const double sampleRate = 48000;
    for (int i = 0; i < 48; i++)
    {
        short left = (short)Math.Round(Math.Sin(leftPhase) * 12000);
        short right = (short)Math.Round(Math.Sin(rightPhase) * 12000);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(i * 8 + 4, 2),
            left);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(i * 8 + 6, 2),
            right);
        leftPhase += 2 * Math.PI * leftHz / sampleRate;
        rightPhase += 2 * Math.PI * rightHz / sampleRate;
    }
    return packet;
}

var state = new GamepadState
{
    Buttons = GamepadButtons.South |
              GamepadButtons.East |
              GamepadButtons.West |
              GamepadButtons.North |
              GamepadButtons.L1 |
              GamepadButtons.R1 |
              GamepadButtons.L2 |
              GamepadButtons.R2 |
              GamepadButtons.Back |
              GamepadButtons.Start |
              GamepadButtons.LeftStick |
              GamepadButtons.RightStick |
              GamepadButtons.DPadUp |
              GamepadButtons.DPadRight |
              GamepadButtons.Home |
              GamepadButtons.Capture,
    Lx = 0,
    Ly = 4095,
    Rx = 4095,
    Ry = 0,
    L2 = GamepadState.TriggerMax,
    R2 = GamepadState.TriggerMax,
    AccelValid = true,
    AccelY = 8192,
    BatteryPercent = 50,
    BatteryCharging = true
};

byte[] ds = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, state);
Expect(ds.Length == 33, "DualSense wire size");
Expect(unchecked((sbyte)ds[0]) == -128, "DualSense LX min");
Expect(unchecked((sbyte)ds[1]) == -128, "DualSense LY inverted max");
Expect(unchecked((sbyte)ds[2]) == 127, "DualSense RX max");
Expect(unchecked((sbyte)ds[3]) == 127, "DualSense RY inverted min");
uint dsButtons = BinaryPrimitives.ReadUInt32LittleEndian(ds.AsSpan(4, 4));
Expect((dsButtons & 0x000000F0) == 0x000000F0, "DualSense face buttons");
Expect((dsButtons & 0x0003FC00) == 0x0003FC00, "DualSense system/shoulder buttons");
Expect(ds[8] == 0x09, "DualSense dpad bitfield");
Expect(ds[9] == 255 && ds[10] == 255, "DualSense triggers");
Expect(BinaryPrimitives.ReadInt16LittleEndian(ds.AsSpan(31, 2)) == -8192, "DualSense static accel Z");

byte[] ns = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro2, state);
Expect(ns.Length == 24, "NS2Pro wire size");
uint nsButtons = BinaryPrimitives.ReadUInt32LittleEndian(ns.AsSpan(0, 4));
Expect(nsButtons == 0x0003FAFF, "NS2Pro button bitfield");
Expect(BinaryPrimitives.ReadUInt16LittleEndian(ns.AsSpan(4, 2)) == 0, "NS2Pro LX min");
Expect(BinaryPrimitives.ReadUInt16LittleEndian(ns.AsSpan(6, 2)) == 4095, "NS2Pro LY max");
Expect(BinaryPrimitives.ReadInt16LittleEndian(ns.AsSpan(14, 2)) == -8192, "NS2Pro static accel Y");
byte[] nsNeutral = VirtualPadPackets.NeutralInput(ViiperDeviceProfile.Pro2);
Expect(nsNeutral.Length == 24, "NS2Pro neutral wire size");

byte[] xb = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Xbox, state);
Expect(xb.Length == 20, "Xbox wire size");
uint xbButtons = BinaryPrimitives.ReadUInt32LittleEndian(xb.AsSpan(0, 4));
Expect((xbButtons & 0xFFFF) == 0xF7F9, "Xbox button bitfield");
Expect(xb[4] == 255 && xb[5] == 255, "Xbox triggers");
Expect(BinaryPrimitives.ReadInt16LittleEndian(xb.AsSpan(6, 2)) == short.MinValue, "Xbox LX min");
Expect(BinaryPrimitives.ReadInt16LittleEndian(xb.AsSpan(8, 2)) == short.MaxValue, "Xbox LY max");

var motionState = GamepadState.Neutral();
motionState.GyroValid = true;
motionState.AccelValid = true;
motionState.GyroX = 100;
motionState.GyroY = 200;
motionState.GyroZ = -300;
motionState.AccelX = 10;
motionState.AccelY = 8192;
motionState.AccelZ = 20;
byte[] dsMotion = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, motionState);
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(21, 2)) == -100, "DualSense default gyro X uses flipped source X after pitch/roll swap");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(23, 2)) == -300, "DualSense default gyro Y uses SDL source Z");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(25, 2)) == -200, "DualSense default gyro Z takes flipped source Y after pitch/roll swap");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(27, 2)) == -10, "DualSense default accel X uses flipped source X after pitch/roll swap");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(29, 2)) == 20, "DualSense default accel Y uses SDL source Z");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(31, 2)) == -8192, "DualSense default accel Z uses flipped source Y after pitch/roll swap");
byte[] dsMotionInverted = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.DualSenseLike,
    motionState,
    new GyroAxisInversion(false, true, false));
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionInverted.AsSpan(21, 2)) == -100, "DualSense inverted keeps mapped gyro X");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionInverted.AsSpan(23, 2)) == 300, "DualSense inverted flips mapped gyro Y");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionInverted.AsSpan(25, 2)) == -200, "DualSense inverted keeps mapped gyro Z");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionInverted.AsSpan(31, 2)) == -8192, "DualSense inverted leaves accel mapping unchanged");
byte[] dsMotionXzInverted = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.DualSenseLike,
    motionState,
    new GyroAxisInversion(true, false, true));
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionXzInverted.AsSpan(21, 2)) == 100, "DualSense X switch flips mapped gyro X");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionXzInverted.AsSpan(23, 2)) == -300, "DualSense X/Z switch keeps mapped gyro Y standard");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionXzInverted.AsSpan(25, 2)) == 200, "DualSense Z switch flips mapped gyro Z");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionXzInverted.AsSpan(31, 2)) == -8192, "DualSense X/Z switches leave accel mapping unchanged");
Expect(
    Ps5ImuMappingOption.FromLabel("SDL/Nintendo 基线  G=-Y,+Z,-X  A=-Y,+Z,-X").Label ==
    Ps5ImuMappingOption.Default.Label,
    "legacy V6.2.14 default label migrates to the fixed PS5 mapping");
Expect(
    Ps5ImuMappingOption.FromLabel("V6.2.12 配对  G=+Y,-Z,-X  A=-X,-Z,-Y").Label ==
    Ps5ImuMappingOption.Default.Label,
    "legacy selectable PS5 mapping labels migrate to the fixed PS5 mapping");
byte[] nsMotion = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro2, motionState);
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(12, 2)) == 10, "NS2Pro accel X maps directly");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(14, 2)) == -8192, "NS2Pro accel Y flips source Y");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(16, 2)) == 20, "NS2Pro accel Z maps directly");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(18, 2)) == 100, "NS2Pro gyro X maps directly");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(20, 2)) == -200, "NS2Pro gyro Y flips source Y");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(22, 2)) == -300, "NS2Pro gyro Z maps directly");
byte[] nsMotionInverted = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.Pro2,
    motionState,
    new GyroAxisInversion(false, true, false));
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotionInverted.AsSpan(18, 2)) == 100, "NS2Pro inverted keeps gyro X");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotionInverted.AsSpan(20, 2)) == 200, "NS2Pro inverted flips only gyro Y");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotionInverted.AsSpan(22, 2)) == -300, "NS2Pro inverted keeps gyro Z");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotionInverted.AsSpan(14, 2)) == -8192, "NS2Pro inverted leaves accel mapping unchanged");
Expect(new GyroAxisInversion().TelemetryValue == "x0,y0,z0", "gyro axis inversion defaults to standard mapping");
Expect(new GyroAxisInversion(false, true, false).DisplayValue == "X+ Y- Z+", "gyro axis inversion display shows Y flip");

var parser = new Pro2HidReportParser();
byte[] report = new byte[49];
report[0] = 0x30;
report[3] = 0xCF;
report[4] = 0x33;
report[5] = 0xC6;
Pack12(report, 6, 2048, 2048);
Pack12(report, 9, 2048, 2048);
BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(37, 2), 1);
BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(39, 2), 2);
BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(41, 2), 3);
BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(43, 2), 4);
BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(45, 2), 5);
BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(47, 2), 6);

Expect(parser.TryParse(report, out GamepadState parsed, out string source), "parse standard Pro2 HID");
Expect(source == "switch_pro_standard", "parse source");
Expect(parser.TryParseHidInputReport(report, out GamepadState parsedHid, out string hidSource), "strict parse standard Pro2 HID");
Expect(hidSource == "switch_pro_standard", "strict parse source");
Expect(parsedHid.Lx == GamepadState.AxisCenter, "strict parsed centered axes");
Expect(parsed.IsPressed(GamepadButtons.South), "parsed south");
Expect(parsed.IsPressed(GamepadButtons.East), "parsed east");
Expect(parsed.IsPressed(GamepadButtons.West), "parsed west");
Expect(parsed.IsPressed(GamepadButtons.North), "parsed north");
Expect(parsed.IsPressed(GamepadButtons.L1), "parsed L1");
Expect(parsed.IsPressed(GamepadButtons.R1), "parsed R1");
Expect(parsed.IsPressed(GamepadButtons.L2), "parsed L2");
Expect(parsed.IsPressed(GamepadButtons.R2), "parsed R2");
Expect(parsed.Lx == GamepadState.AxisCenter && parsed.Ry == GamepadState.AxisCenter, "parsed centered axes");
Expect(parsed.AccelValid && parsed.GyroValid, "parsed motion");
Expect(parsed.AccelX == 1 && parsed.GyroZ == 6, "parsed motion values");

byte[] fd2 = new byte[60];
BinaryPrimitives.WriteUInt32LittleEndian(
    fd2.AsSpan(4, 4),
    0x00020000u | 0x00000080u | 0x00000004u);
Pack12(fd2, 10, 2048, 2048);
Pack12(fd2, 13, 2048, 2048);
BinaryPrimitives.WriteInt16LittleEndian(fd2.AsSpan(48, 2), -7);
BinaryPrimitives.WriteInt16LittleEndian(fd2.AsSpan(58, 2), 77);
Expect(parser.TryParse(fd2, out GamepadState fd2Parsed, out string fd2Source), "parse FD2 BLE payload");
Expect(fd2Source == "fd2_payload", "FD2 parse source");
Expect(parser.TryParseFd2Payload(fd2, out GamepadState fd2DirectParsed, out string fd2DirectSource), "parse FD2 payload directly");
Expect(fd2DirectSource == "fd2_payload" && fd2DirectParsed.Lx == GamepadState.AxisCenter, "direct FD2 parse source");
Expect(fd2Parsed.IsPressed(GamepadButtons.South), "FD2 parsed south");
Expect(fd2Parsed.IsPressed(GamepadButtons.R2), "FD2 parsed R2");
Expect(fd2Parsed.IsPressed(GamepadButtons.DPadUp), "FD2 parsed dpad up");
Expect(fd2Parsed.R2 == GamepadState.TriggerMax, "FD2 parsed analog R2");
Expect(fd2Parsed.AccelValid && fd2Parsed.GyroValid, "FD2 parsed motion");
Expect(fd2Parsed.AccelX == -7 && fd2Parsed.GyroZ == 77, "FD2 parsed motion values");

var motionParser = new Pro2HidReportParser();
byte[] flatFd2 = new byte[60];
Pack12(flatFd2, 10, 2048, 2048);
Pack12(flatFd2, 13, 2048, 2048);
for (int i = 0; i < 24; i++)
{
    WriteMotion(flatFd2, 48, 120, 8500, -90, 90, -120, 70);
    Expect(motionParser.TryParse(flatFd2, out _, out _), "motion calibration learns flat FD2 sample");
}

WriteMotion(flatFd2, 48, 620, 8700, -90, 290, -120, -330);
Expect(motionParser.TryParse(flatFd2, out GamepadState calibratedMotion, out _), "motion calibration applies to FD2 sample");
Expect(calibratedMotion.AccelX == 500, "motion calibration recenters accel X");
Expect(calibratedMotion.AccelY == 8392, "motion calibration keeps source Y near 1g");
Expect(calibratedMotion.AccelZ == 0, "motion calibration recenters accel Z");
Expect(calibratedMotion.GyroX == 200, "motion calibration subtracts gyro X bias");
Expect(calibratedMotion.GyroY == 0, "motion calibration subtracts gyro Y bias");
Expect(calibratedMotion.GyroZ == -400, "motion calibration subtracts gyro Z bias");
byte[] shortFd2 = new byte[16];
BinaryPrimitives.WriteUInt32LittleEndian(shortFd2.AsSpan(4, 4), 0x00000004u);
Pack12(shortFd2, 10, 2048, 2048);
Expect(!parser.TryParseFd2Payload(shortFd2, out _, out _), "direct FD2 parser rejects short non-full payload");

byte[] viiperLikeReport = new byte[64];
viiperLikeReport[0] = 0x05;
viiperLikeReport[3] = 0xCF;
viiperLikeReport[4] = 0x33;
viiperLikeReport[5] = 0xC6;
Expect(!parser.TryParseHidInputReport(viiperLikeReport, out _, out _), "strict parser rejects VIIPER ns2pro input report");

byte[] nsFeedback = new byte[34];
nsFeedback[0] = 0x50;
nsFeedback[1] = 0x22;
nsFeedback[2] = 0x11;
nsFeedback[3] = 0x44;
nsFeedback[4] = 0x33;
nsFeedback[5] = 0x55;
nsFeedback[16] = 0x50;
nsFeedback[17] = 0xaa;
nsFeedback[18] = 0xbb;
nsFeedback[19] = 0xcc;
nsFeedback[20] = 0xdd;
nsFeedback[21] = 0xee;
nsFeedback[32] = 0x03;
nsFeedback[33] = 0x0f;
Expect(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Pro2, nsFeedback, out Pro2OutputPacket hdPacket, out string hdReason), "map ns2pro feedback");
Expect(hdPacket.Report.Length == 64, "ns2pro output report length");
Expect(hdPacket.Report[0] == 0x02, "ns2pro report id");
Expect(hdPacket.Report[1] == 0x50 && hdPacket.Report[17] == 0x50, "ns2pro motor block headers");
Expect(hdPacket.Report[2] == 0x22 && hdPacket.Report[18] == 0xaa, "ns2pro motor frames copied");
Expect(hdPacket.PlayerLedMask == 0x0f, "ns2pro player LED carried");
Expect(hdPacket.Active, "ns2pro active rumble detected");
byte[] blePacket = new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
Expect(
    Pro2BleRumblePacketEncoder.TryEncodeRaw02(hdPacket.Report, 0x0a, blePacket, out bool bleActive, out string bleError),
    "encode raw02 to BLE packet: " + bleError);
Expect(blePacket[0] == 0x00, "BLE rumble report prefix");
Expect(blePacket[1] == 0x5a && blePacket[17] == 0x5a, "BLE rumble packet id in motor blocks");
Expect(bleActive, "BLE rumble active");
Expect(!hdPacket.Report.AsSpan(1, 32).SequenceEqual(blePacket.AsSpan(1, 32)), "BLE packet is converted, not raw HID copied");

byte[] ledOnly = (byte[])nsFeedback.Clone();
ledOnly[32] = 0x02;
Expect(!Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Pro2, ledOnly, out _, out string ledReason), "skip led-only ns2pro feedback");
Expect(ledReason.Contains("led-only", StringComparison.OrdinalIgnoreCase), "led-only skip reason");

byte[] xinputFeedback = [200, 120];
Expect(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Xbox, xinputFeedback, out Pro2OutputPacket ordinaryPacket, out _), "map xinput feedback");
Expect(ordinaryPacket.Report.Length == 64, "ordinary output report length");
Expect(ordinaryPacket.Report[0] == 0x02, "ordinary report id");
Expect(ordinaryPacket.Report[1] == 0x50 && ordinaryPacket.Report[17] == 0x50, "ordinary motor block headers");
Expect(ordinaryPacket.Active, "ordinary active rumble detected");
Expect(ordinaryPacket.Report[2] != 0 || ordinaryPacket.Report[18] != 0, "ordinary rumble frame populated");

byte[] stopFeedback = [0, 0];
Expect(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.Xbox, stopFeedback, out Pro2OutputPacket stopPacket, out _), "map xinput stop feedback");
Expect(!stopPacket.Active, "ordinary stop is neutral");
byte[] stopBle = new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
Expect(
    Pro2BleRumblePacketEncoder.TryEncodeRaw02(stopPacket.Report, 0x01, stopBle, out bool stopBleActive, out string stopBleError),
    "encode stop raw02 to BLE packet: " + stopBleError);
Expect(!stopBleActive, "BLE stop packet is neutral");

byte[] gainZeroBle = new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
Expect(
    Pro2BleRumblePacketEncoder.TryEncodeRaw02(
        ordinaryPacket.Report,
        0x02,
        gainZeroBle,
        out bool gainZeroActive,
        out string gainZeroError,
        0),
    "encode zero-gain rumble: " + gainZeroError);
(int gainZeroLow, int gainZeroHigh) =
    DecodeBleAmplitudes(gainZeroBle, 2);
Expect(!gainZeroActive, "zero gain disables physical rumble");
Expect(gainZeroLow == 0 && gainZeroHigh == 0, "zero gain encodes zero amplitudes");

byte[] gainOneBle = new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
Expect(
    Pro2BleRumblePacketEncoder.TryEncodeRaw02(
        ordinaryPacket.Report,
        0x03,
        gainOneBle,
        out bool gainOneActive,
        out string gainOneError,
        1),
    "encode 1x rumble: " + gainOneError);
(int gainOneLow, int gainOneHigh) =
    DecodeBleAmplitudes(gainOneBle, 2);
Expect(gainOneActive, "1x gain keeps rumble active");
Expect(gainOneLow is > 0 and < 1023 && gainOneHigh is > 0 and < 1023, "1x amplitudes remain proportional");

byte[] gainThreeBle = new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
Expect(
    Pro2BleRumblePacketEncoder.TryEncodeRaw02(
        ordinaryPacket.Report,
        0x04,
        gainThreeBle,
        out bool gainThreeActive,
        out string gainThreeError,
        3),
    "encode 3x rumble: " + gainThreeError);
(int gainThreeLow, int gainThreeHigh) =
    DecodeBleAmplitudes(gainThreeBle, 2);
Expect(gainThreeActive, "3x gain keeps rumble active");
Expect(gainThreeLow == 1023 && gainThreeHigh == 1023, "3x amplitudes saturate at Pro2 maximum");
Expect(
    V60UserSettings.NormalizeRumbleMultiplier(-1) == 0 &&
    V60UserSettings.NormalizeRumbleMultiplier(3.8) == 3 &&
    V60UserSettings.NormalizeRumbleMultiplier(double.NaN) == 1,
    "rumble multiplier is normalized to 0..3");
Expect(
    ViiperGyroModeOption.Default.Mode == ViiperGyroMode.Hold250Hz &&
    ViiperGyroModeOption.FromLabel("source_60hz（推荐）").Mode == ViiperGyroMode.Hold250Hz &&
    ViiperGyroModeOption.FromLabel("source_60hz_zero（诊断）").Mode == ViiperGyroMode.Source60Hz,
    "gyro default uses latest-held IMU and migrates the old source_60hz recommendation");

var hapticScheduler = new DualSenseHapticRumbleScheduler();
byte[] compatibilityReport = new byte[64];
compatibilityReport[0] = 0x02;
compatibilityReport[1] = 0x01;
compatibilityReport[3] = 72;
compatibilityReport[4] = 180;
Expect(
    hapticScheduler.TryProcess(
        HapticFrame(1, compatibilityReport),
        out Pro2OutputPacket compatibilityPacket,
        out string compatibilitySummary,
        out string compatibilityReason),
    "DualSense compatibility output maps to ordinary rumble: " +
    compatibilityReason);
Expect(
    compatibilityPacket.Source == "dualsense-ordinary" &&
    compatibilityPacket.Active &&
    compatibilitySummary.Contains("compatibility", StringComparison.Ordinal),
    "DualSense compatibility mode selects ordinary rumble");

double blockedLeftPhase = 0;
double blockedRightPhase = 0;
byte[] blockedAudio = HapticPcmPacket(
    ref blockedLeftPhase,
    ref blockedRightPhase,
    140,
    330);
Expect(
    !hapticScheduler.TryProcess(
        HapticFrame(2, blockedAudio),
        out _,
        out _,
        out string blockedReason) &&
    blockedReason.Contains("blocked", StringComparison.OrdinalIgnoreCase),
    "DualSense compatibility mode blocks HD audio");

byte[] audioModeReport = new byte[64];
audioModeReport[0] = 0x02;
Expect(
    hapticScheduler.TryProcess(
        HapticFrame(1, audioModeReport),
        out Pro2OutputPacket transitionStop,
        out string audioModeSummary,
        out string audioModeReason),
    "DualSense audio mode transition emits a stop packet: " +
    audioModeReason);
Expect(
    !transitionStop.Active &&
    audioModeSummary.Contains("audio_haptics", StringComparison.Ordinal),
    "DualSense audio mode is selected by host flags");

double leftPhase = 0;
double rightPhase = 0;
Pro2OutputPacket? dualSenseHdPacket = null;
for (int i = 0; i < 40; i++)
{
    byte[] pcm = HapticPcmPacket(
        ref leftPhase,
        ref rightPhase,
        140,
        330);
    if (hapticScheduler.TryProcess(
            HapticFrame(2, pcm),
            out Pro2OutputPacket candidate,
            out _,
            out _) &&
        candidate.Active)
    {
        dualSenseHdPacket = candidate;
        break;
    }
}
Expect(
    dualSenseHdPacket != null,
    "four-channel DualSense PCM produces HD raw02 output");
Expect(
    dualSenseHdPacket!.Source == "dualsense-hd-audio" &&
    dualSenseHdPacket.Report.Length == 64 &&
    dualSenseHdPacket.Report[0] == 0x02,
    "DualSense HD output is a Pro2 raw02 report");

string originalPath = @"C:\Windows\System32";
string withUsbip = UsbipRuntimeLocator.BuildPathWithUsbipDirectory(
    originalPath,
    new UsbipRuntime(@"C:\USBip\usbip.exe", @"C:\USBip"));
Expect(withUsbip.StartsWith(@"C:\USBip;", StringComparison.OrdinalIgnoreCase), "usbip dir prepended to PATH");
Expect(
    UsbipRuntimeLocator.BuildPathWithUsbipDirectory(withUsbip, new UsbipRuntime(@"C:\USBip\usbip.exe", @"C:\USBip")) == withUsbip,
    "usbip dir is not duplicated in PATH");
Expect(UsbipRuntimeLocator.FindBundledInstaller() != null, "bundled usbip-win2 installer is discoverable");
string[] embeddedResources = typeof(UsbipRuntimeLocator).Assembly.GetManifestResourceNames();
Expect(
    embeddedResources.Contains(
        "Embedded.viiper.haptic.exe",
        StringComparer.Ordinal),
    "single-file app embeds the haptic VIIPER server");
Expect(
    embeddedResources.Contains("Embedded.usbip.installer", StringComparer.Ordinal),
    "single-file app embeds the usbip-win2 installer");
Expect(
    embeddedResources.Contains("Embedded.usbip.license", StringComparer.Ordinal),
    "single-file app embeds the usbip-win2 license");
Expect(
    Pro2BleInputSource.ShouldKeepLiveInput(58.5, 58.5, 173, 173),
    "real 15 ms / 58.5 Hz Pro2 session remains live instead of being rejected");
Expect(
    !Pro2BleInputSource.ShouldKeepLiveInput(20.0, 20.0, 80, 80),
    "unusable low-rate BLE input is rejected");
Expect(
    !Pro2BleInputSource.ShouldKeepLiveInput(65.0, 5.0, 200, 15),
    "mostly unparsed BLE notifications are rejected");

var heldState = new GamepadState
{
    Buttons = GamepadButtons.South,
    Lx = 3000,
    R2 = GamepadState.TriggerMax
};
GamepadState fresh = InputContinuityPolicy.Resolve(
    heldState,
    TimeSpan.FromMilliseconds(18),
    out string freshSource);
Expect(freshSource == "pro2_ble_age_0_20", "18 ms BLE age is normal for 60 Hz source");

GamepadState missedCycle = InputContinuityPolicy.Resolve(
    heldState,
    TimeSpan.FromMilliseconds(42),
    out string missedCycleSource);
Expect(missedCycleSource == "pro2_ble_age_33_50", "42 ms BLE age marks a possible missed source cycle");

GamepadState held = InputContinuityPolicy.Resolve(
    heldState,
    TimeSpan.FromMilliseconds(120),
    out string heldSource);
Expect(heldSource == "pro2_ble_age_gt100", ">100 ms BLE age is marked dangerous but still repeats latest_state briefly");
Expect(held.IsPressed(GamepadButtons.South), "brief dangerous BLE age preserves buttons");
Expect(held.Lx == 3000 && held.R2 == GamepadState.TriggerMax, "brief dangerous BLE age preserves analog state");

GamepadState decayed = InputContinuityPolicy.Resolve(
    heldState,
    TimeSpan.FromMilliseconds(540),
    out string decayedSource);
Expect(decayedSource == "pro2_ble_danger_safe_hold", "540 ms BLE gap enters safe analog hold");
Expect(decayed.Buttons == GamepadButtons.None, "safe hold releases buttons");
Expect(decayed.Lx == heldState.Lx, "safe hold does not create visible stick drift");
Expect(decayed.R2 == 0, "safe hold releases triggers");

GamepadState stale = InputContinuityPolicy.Resolve(
    heldState,
    TimeSpan.FromMilliseconds(900),
    out string staleSource);
Expect(staleSource == "neutral", "disconnected BLE input becomes neutral");
Expect(stale.Buttons == GamepadButtons.None && stale.Lx == GamepadState.AxisCenter, "neutral fallback is safe");

var stability = new Pro2InputStabilityFilter();
GamepadState centerState = GamepadState.Neutral();
Expect(
    stability.ProcessAt(centerState, TimeSpan.Zero).AcceptedState.Lx == GamepadState.AxisCenter,
    "stability filter accepts initial center");
GamepadState smallMove = GamepadState.Neutral();
smallMove.Lx = (ushort)(GamepadState.AxisCenter + 220);
Pro2InputFilterResult smallMoveResult = stability.ProcessAt(smallMove, TimeSpan.FromMilliseconds(15));
Expect(
    smallMoveResult.AcceptedState.Lx == smallMove.Lx &&
    !smallMoveResult.HasAxisIntervention,
    "normal small axis movement passes directly");
var singleFrameSpike = new GamepadState
{
    Buttons = GamepadButtons.South,
    Lx = GamepadState.AxisMax,
    Ly = GamepadState.AxisCenter,
    Rx = GamepadState.AxisCenter,
    Ry = GamepadState.AxisCenter,
    AccelValid = true,
    GyroValid = true,
    AccelX = 11,
    GyroZ = -22
};
var idleSpikeFilter = new Pro2InputStabilityFilter();
idleSpikeFilter.ProcessAt(centerState, TimeSpan.Zero);
Pro2InputFilterResult rejectedSpike = idleSpikeFilter.ProcessAt(singleFrameSpike, TimeSpan.FromMilliseconds(15));
Expect(
    rejectedSpike.HasHoldOrReject &&
    rejectedSpike.AcceptedState.Lx == GamepadState.AxisCenter &&
    rejectedSpike.AcceptedState.IsPressed(GamepadButtons.South) &&
    rejectedSpike.AcceptedState.AccelValid &&
    rejectedSpike.AcceptedState.GyroZ == -22,
    "stability filter holds one-frame axis spike while preserving buttons and motion");
Pro2InputFilterResult acceptedAfterSpike = idleSpikeFilter.ProcessAt(centerState, TimeSpan.FromMilliseconds(30));
Expect(
    acceptedAfterSpike.HasHoldOrReject &&
    acceptedAfterSpike.AcceptedState.Lx == GamepadState.AxisCenter,
    "stability filter rejects candidate that returns to last good");

var recoveryFilter = new Pro2InputStabilityFilter();
recoveryFilter.ProcessAt(centerState, TimeSpan.Zero);
var linkRecoveryMove = GamepadState.Neutral();
linkRecoveryMove.Lx = GamepadState.AxisMax;
Pro2InputFilterResult linkRecovery = recoveryFilter.ProcessAt(
    linkRecoveryMove,
    TimeSpan.FromMilliseconds(120));
Expect(
    linkRecovery.AcceptedState.Lx == GamepadState.AxisMax &&
    !linkRecovery.HasHoldOrReject &&
    !linkRecovery.HasRamp &&
    linkRecovery.Events.Any(e => e.Reason.Contains("link_recovery", StringComparison.Ordinal)),
    "long BLE gap recovery frame follows raw stick input instead of being held as an idle spike");

var burstFilter = new Pro2InputStabilityFilter();
burstFilter.ProcessAt(centerState, TimeSpan.Zero);
for (int i = 1; i <= 2; i++)
{
    Pro2InputFilterResult burst = burstFilter.ProcessAt(
        singleFrameSpike,
        TimeSpan.FromMilliseconds(i * 15));
    if (i == 1)
    {
        Expect(
            burst.HasHoldOrReject &&
            burst.AcceptedState.Lx == GamepadState.AxisCenter,
            "idle spike burst first frame is held while proving continuity");
    }
    else
    {
        Expect(
            burst.AcceptedState.Lx == GamepadState.AxisMax &&
            !burst.HasInputSwallowed,
            "sustained same-direction raw motion follows on the second BLE frame");
    }
}
Pro2InputFilterResult burstFollow = burstFilter.ProcessAt(
    singleFrameSpike,
    TimeSpan.FromMilliseconds(45));
Expect(
    burstFollow.AcceptedState.Lx == GamepadState.AxisMax &&
    !burstFollow.HasInputSwallowed,
    "sustained same-direction raw motion stays fully followed within 30-60 ms");
Pro2InputFilterResult burstReturn = burstFilter.ProcessAt(centerState, TimeSpan.FromMilliseconds(75));
Expect(
    burstReturn.AcceptedState.Lx < burstFollow.AcceptedState.Lx,
    "return after a short burst moves the authoritative state back instead of staying swallowed");

var boundaryConfirmFilter = new Pro2InputStabilityFilter();
boundaryConfirmFilter.ProcessAt(centerState, TimeSpan.Zero);
Pro2InputFilterResult boundaryFirst = boundaryConfirmFilter.ProcessAt(
    singleFrameSpike,
    TimeSpan.FromMilliseconds(15));
Expect(boundaryFirst.HasHoldOrReject, "first healthy-cadence spike is still protected");
Pro2InputFilterResult boundarySecond = boundaryConfirmFilter.ProcessAt(
    singleFrameSpike,
    TimeSpan.FromMilliseconds(29.5));
Expect(
    boundarySecond.AcceptedState.Lx == GamepadState.AxisMax &&
    !boundarySecond.HasHoldOrReject &&
    !boundarySecond.HasRamp,
    "confirmed active motion is not delayed by a 14.5 ms/15 ms confirm boundary");

var fastMoveFilter = new Pro2InputStabilityFilter();
fastMoveFilter.ProcessAt(centerState, TimeSpan.Zero);
Pro2InputFilterResult fastMove = fastMoveFilter.ProcessAt(singleFrameSpike, TimeSpan.FromMilliseconds(15));
Expect(fastMove.HasHoldOrReject, "fast real move starts as suspect");
fastMove = fastMoveFilter.ProcessAt(singleFrameSpike, TimeSpan.FromMilliseconds(30));
Expect(
    fastMove.AcceptedState.Lx == GamepadState.AxisMax &&
    !fastMove.HasInputSwallowed,
    "fast real move follows on the second BLE frame without being swallowed");
fastMove = fastMoveFilter.ProcessAt(singleFrameSpike, TimeSpan.FromMilliseconds(45));
Expect(
    !fastMove.HasRamp &&
    fastMove.AcceptedState.Lx == GamepadState.AxisMax,
    "sustained fast real move bypasses ramp in low-latency mode");
var mediumActiveMove = GamepadState.Neutral();
mediumActiveMove.Lx = (ushort)(GamepadState.AxisMax - 650);
fastMove = fastMoveFilter.ProcessAt(mediumActiveMove, TimeSpan.FromMilliseconds(60));
Expect(
    !fastMove.HasRamp &&
    fastMove.AcceptedState.Lx == mediumActiveMove.Lx,
    "active stick motion never ramps behind raw input in low-latency mode");

var reversalFilter = new Pro2InputStabilityFilter();
reversalFilter.ProcessAt(centerState, TimeSpan.Zero);
Pro2InputFilterResult reversal = reversalFilter.ProcessAt(singleFrameSpike, TimeSpan.FromMilliseconds(15));
reversal = reversalFilter.ProcessAt(singleFrameSpike, TimeSpan.FromMilliseconds(30));
reversal = reversalFilter.ProcessAt(singleFrameSpike, TimeSpan.FromMilliseconds(45));
ushort beforeReversal = reversal.AcceptedState.Lx;
var hardLeft = GamepadState.Neutral();
hardLeft.Lx = 0;
reversal = reversalFilter.ProcessAt(hardLeft, TimeSpan.FromMilliseconds(60));
Expect(
    !reversal.HasRamp &&
    reversal.HasAxisTelemetry &&
    reversal.Events.Any(e => e.FastReversal) &&
    reversal.AcceptedState.Lx == 0,
    "fast reversal is identified and directly follows raw input in low-latency mode");
reversal = reversalFilter.ProcessAt(hardLeft, TimeSpan.FromMilliseconds(75));
Expect(
    reversal.AcceptedState.Lx < beforeReversal &&
    !reversal.HasInputSwallowed,
    "continued fast reversal keeps following without input_swallowed_count");

var independentAxisFilter = new Pro2InputStabilityFilter();
independentAxisFilter.ProcessAt(centerState, TimeSpan.Zero);
var mixedState = GamepadState.Neutral();
mixedState.Lx = GamepadState.AxisMax;
mixedState.Rx = (ushort)(GamepadState.AxisCenter + 200);
mixedState.Ry = (ushort)(GamepadState.AxisCenter - 180);
mixedState.Buttons = GamepadButtons.North;
Pro2InputFilterResult mixedResult = independentAxisFilter.ProcessAt(
    mixedState,
    TimeSpan.FromMilliseconds(15));
Expect(
    mixedResult.AcceptedState.Lx == GamepadState.AxisCenter &&
    mixedResult.AcceptedState.Rx == mixedState.Rx &&
    mixedResult.AcceptedState.Ry == mixedState.Ry &&
    mixedResult.AcceptedState.IsPressed(GamepadButtons.North),
    "left stick spike does not block right stick or buttons");

using (WindowsTimerResolutionScope timerResolution = WindowsTimerResolutionScope.Begin())
{
    Expect(timerResolution.IsActive, "Windows 1 ms timer resolution request");
    using var timer = new HighResolutionPeriodicTimer(TimeSpan.FromMilliseconds(4));
    var timerWatch = System.Diagnostics.Stopwatch.StartNew();
    const int timerSamples = 125;
    for (int i = 0; i < timerSamples; i++)
    {
        Expect(timer.WaitForNextTick(CancellationToken.None), "high-resolution timer tick");
    }

    double timerHz = timerSamples / timerWatch.Elapsed.TotalSeconds;
    Expect(timerHz >= 220, "high-resolution timer cadence, actual=" + timerHz.ToString("F1"));
    Expect(
        timer.Backend == "high_resolution_waitable_timer_absolute",
        "high-resolution waitable timer backend: " + timer.Backend);
}

Console.WriteLine("v60_packet_mapper_test: passed");
