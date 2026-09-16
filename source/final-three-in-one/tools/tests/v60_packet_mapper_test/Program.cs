using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Y700Switch2V60Viiper;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void ExpectNear(double actual, double expected, double tolerance, string message)
{
    if (Math.Abs(actual - expected) > tolerance)
    {
        throw new InvalidOperationException(
            message + " actual=" + actual.ToString("0.######") +
            " expected=" + expected.ToString("0.######"));
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
    double rightHz,
    bool frontPair = false)
{
    byte[] packet = new byte[384];
    const double sampleRate = 48000;
    int leftOffset = frontPair ? 0 : 4;
    int rightOffset = frontPair ? 2 : 6;
    for (int i = 0; i < 48; i++)
    {
        short left = (short)Math.Round(Math.Sin(leftPhase) * 12000);
        short right = (short)Math.Round(Math.Sin(rightPhase) * 12000);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(i * 8 + leftOffset, 2),
            left);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(i * 8 + rightOffset, 2),
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
    AccelY = 4096,
    BatteryPercent = 50,
    BatteryCharging = true
};

var sequentialQueue = new Pro2SequentialInputQueue(capacity: 3);
var queuedOne = GamepadState.Neutral();
queuedOne.Updates = 1;
queuedOne.MotionTimestampUs = 15000;
var queuedTwo = GamepadState.Neutral();
queuedTwo.Updates = 2;
queuedTwo.MotionTimestampUs = 30000;
sequentialQueue.Enqueue(queuedOne, 100);
sequentialQueue.Enqueue(queuedTwo, 200);
Expect(
    sequentialQueue.TryDequeue(out GamepadState dequeuedOne, out long dequeuedTicksOne) &&
    dequeuedOne.Updates == 1 && dequeuedOne.MotionTimestampUs == 15000 && dequeuedTicksOne == 100,
    "sequential FD2 queue preserves the first parsed sample");
Expect(
    sequentialQueue.TryDequeue(out GamepadState dequeuedTwo, out long dequeuedTicksTwo) &&
    dequeuedTwo.Updates == 2 && dequeuedTwo.MotionTimestampUs == 30000 && dequeuedTicksTwo == 200,
    "sequential FD2 queue preserves sample order and timestamp");
var overflowQueue = new Pro2SequentialInputQueue(capacity: 2);
overflowQueue.Enqueue(queuedOne, 100);
overflowQueue.Enqueue(queuedTwo, 200);
var queuedThree = GamepadState.Neutral();
queuedThree.Updates = 3;
overflowQueue.Enqueue(queuedThree, 300);
Expect(
    overflowQueue.OverflowDropCount == 1 &&
    overflowQueue.TryDequeue(out GamepadState overflowFirst, out _) &&
    overflowFirst.Updates == 2,
    "sequential FD2 queue reports overflow and drops only the oldest sample");
var realtimeQueue = new Pro2SequentialInputQueue(capacity: 4);
realtimeQueue.Enqueue(queuedOne, 100);
realtimeQueue.Enqueue(queuedTwo, 200);
realtimeQueue.Enqueue(queuedThree, 300);
Expect(
    realtimeQueue.TryDequeueNewest(
        out GamepadState realtimeNewest,
        out long realtimeNewestTicks,
        out int realtimeSuperseded) &&
    realtimeNewest.Updates == 3 &&
    realtimeNewestTicks == 300 &&
    realtimeSuperseded == 2 &&
    realtimeQueue.RealtimeSupersededCount == 2 &&
    realtimeQueue.DequeuedCount == 3,
    "real-time FD2 dequeue publishes newest state and accounts for stale queued samples");

var sessionBoundaryQueue = new Pro2SequentialInputQueue(capacity: 4);
sessionBoundaryQueue.Enqueue(queuedOne, 100);
sessionBoundaryQueue.Enqueue(queuedTwo, 200);
sessionBoundaryQueue.Reset();
Expect(
    sessionBoundaryQueue.Count == 0 &&
    sessionBoundaryQueue.EnqueuedCount == 0 &&
    sessionBoundaryQueue.DequeuedCount == 0 &&
    sessionBoundaryQueue.OverflowDropCount == 0 &&
    !sessionBoundaryQueue.TryDequeue(out _, out _),
    "new virtual consumer session cannot replay frames queued for the previous device");

double[] sourceGyroDps = [120.0, -80.0, 45.0, -15.0];
double sourceIntegralDeg = sourceGyroDps.Sum(value => value * 0.015);
double usbHeldIntegralDeg = sourceGyroDps.Sum(value =>
    value * (0.004 + 0.004 + 0.004 + 0.003));
ExpectNear(
    usbHeldIntegralDeg,
    sourceIntegralDeg,
    1e-12,
    "diagnostic zero-order hold does not scale the source angular integral");

byte[] ds = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, state);
Expect(ds.Length == 33, "DualSense wire size");
Expect(unchecked((sbyte)ds[0]) == -128, "DualSense LX min");
Expect(unchecked((sbyte)ds[1]) == -128, "DualSense LY inverted max");
Expect(unchecked((sbyte)ds[2]) == 127, "DualSense RX max");
Expect(unchecked((sbyte)ds[3]) == 127, "DualSense RY inverted min");
uint dsButtons = BinaryPrimitives.ReadUInt32LittleEndian(ds.AsSpan(4, 4));
Expect((dsButtons & 0x000000F0) == 0x000000F0, "DualSense face buttons");
Expect((dsButtons & 0x0003FC00) == 0x0003FC00, "DualSense system/shoulder buttons");
Expect((dsButtons & 0x00C00000) == 0, "ordinary DualSense does not expose Edge L4/R4 paddles");
Expect(ds[8] == 0x09, "DualSense dpad bitfield");
Expect(ds[9] == 255 && ds[10] == 255, "DualSense triggers");
Expect(BinaryPrimitives.ReadInt16LittleEndian(ds.AsSpan(29, 2)) == 0, "DualSense PS5 output accel Y is neutral when source Z is flat");
Expect(BinaryPrimitives.ReadInt16LittleEndian(ds.AsSpan(31, 2)) == -8192, "DualSense PS5 output accel Z aligns with VIIPER DefaultAccelZRaw");

var edgeState = state.Clone();
edgeState.Buttons |= GamepadButtons.PaddleLeft | GamepadButtons.PaddleRight;
byte[] edge = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseEdge, edgeState);
Expect(edge.Length == 33, "DualSense Edge wire size");
Expect(
    ViiperDeviceProfile.DualSenseEdge.DeviceType == "dualsenseedge" &&
    ViiperDeviceProfile.DualSenseEdge.ExpectedVid == "054c" &&
    ViiperDeviceProfile.DualSenseEdge.ExpectedPid == "0df2" &&
    ViiperDeviceProfile.DualSenseEdge.FeedbackSize == 6,
    "DualSense Edge profile uses VIIPER dualsenseedge identity and 6-byte feedback");
Expect(
    ViiperDeviceProfile.DualSenseLike.MatchesIdentity(
        new ViiperDevice(1, "1", "0x054c", "0x0ce6", "dualsensehaptic")),
    "DualSense identity guard accepts expected VIIPER identity");
Expect(
    ViiperDeviceProfile.DualSenseEdge.MatchesIdentity(
        new ViiperDevice(1, "1", "054c", "0df2", "dualsenseedge")),
    "DualSense Edge identity guard accepts expected VIIPER identity");
Expect(
    ViiperDeviceProfile.Pro2.MatchesIdentity(
        new ViiperDevice(1, "1", "057e", "2069", "ns2pro")),
    "Pro2 identity guard accepts expected VIIPER identity");
Expect(
    ViiperDeviceProfile.Xbox.MatchesIdentity(
        new ViiperDevice(1, "1", "045e", "028e", "xbox360")),
    "Xbox identity guard accepts expected VIIPER identity");
Expect(
    !ViiperDeviceProfile.Pro2.MatchesIdentity(
        new ViiperDevice(1, "1", "054c", "0ce6", "dualsensehaptic")),
    "Pro2 identity guard rejects a DualSense descriptor");
ViiperDeviceProfile pro2Slot2 = ViiperDeviceProfile.Pro2 with
{
    DeviceSpecificSerialNumber =
        ViiperDeviceProfile.SlotSerialNumber(ViiperVirtualMode.Pro2, 2)
};
IReadOnlyDictionary<string, object?> pro2SlotSpecific =
    pro2Slot2.DeviceSpecificArguments();
Expect(
    pro2SlotSpecific.TryGetValue("serial_number", out object? pro2Serial) &&
    string.Equals(pro2Serial?.ToString(), "LC-V624-NS2PRO-S2", StringComparison.Ordinal),
    "Pro2 VIIPER deviceSpecific carries a stable slot serial to avoid Steam name cache collisions");
Expect(
    pro2SlotSpecific.TryGetValue("input_interval_ms", out object? pro2Interval) &&
    Convert.ToInt32(pro2Interval) == 4,
    "Pro2 VIIPER deviceSpecific carries the configured HID input interval");
ViiperDeviceProfile pro2SourcePaced = pro2Slot2 with
{
    SendInterval = TimeSpan.FromMilliseconds(4),
    SourcePaced = true
};
Expect(
    Convert.ToInt32(pro2SourcePaced.DeviceSpecificArguments()["input_interval_ms"]) == 4 &&
    Convert.ToBoolean(pro2SourcePaced.DeviceSpecificArguments()["source_paced"]),
    "Pro2 source-paced profile advertises a 4 ms host poll cap and fresh-input delivery policy");
Expect(
    ViiperPushRateOption.Default.Mode == ViiperPushRateMode.SourceAdaptive &&
    ViiperPushRateOption.Default.SourcePaced &&
    ViiperPushRateOption.Default.Hz == 0.0,
    "V6 defaults to the real Pro2 BLE source cadence instead of a synthetic fixed rate");
Expect(
    ViiperPushRateOption.FromLabel("125Hz（推荐）").Mode == ViiperPushRateMode.SourceAdaptive,
    "legacy fixed-rate user settings migrate to source-adaptive cadence");
Expect(
    !ViiperBridgeSession.ShouldPublishDisconnectNeutral(
        sourceIsRunning: true,
        neutralAlreadyPublished: false),
    "a transient BLE input gap holds the latest controls instead of injecting a false release");
Expect(
    ViiperBridgeSession.ShouldPublishDisconnectNeutral(
        sourceIsRunning: false,
        neutralAlreadyPublished: false),
    "an explicitly stopped BLE source publishes one neutral report");
Expect(
    !ViiperBridgeSession.ShouldPublishDisconnectNeutral(
        sourceIsRunning: false,
        neutralAlreadyPublished: true),
    "a stopped BLE source does not repeat neutral reports");
Expect(
    !Pro2BleDisconnectSignal.IsAbnormalBluetoothError(null) &&
    !Pro2BleDisconnectSignal.IsAbnormalBluetoothError(BluetoothError.Success) &&
    !Pro2BleDisconnectSignal.IsAbnormalBluetoothError(BluetoothError.DeviceNotConnected),
    "missing reason and a plain remote disconnect remain honest unknown-offline classifications");
Expect(
    Pro2BleDisconnectSignal.IsAbnormalBluetoothError(BluetoothError.RadioNotAvailable) &&
    Pro2BleDisconnectSignal.IsAbnormalBluetoothError(BluetoothError.ResourceInUse) &&
    Pro2BleDisconnectSignal.IsAbnormalBluetoothError(BluetoothError.OtherError),
    "explicit Windows Bluetooth failures are classified as abnormal disconnects");
var disconnectTelemetry = new Pro2BleDisconnectSignal(
    DateTimeOffset.UtcNow,
    ConnectionSequence: 7,
    Detector: "gatt_session_closed",
    ConnectedAddress: "001122334455",
    WindowsConnectionStatus: "Disconnected",
    BluetoothErrorCode: "DeviceNotConnected",
    LastInputAgeMs: 88,
    LastBatteryPercent: GamepadState.BatteryUnknown,
    LastBatteryCharging: false,
    IsAbnormal: false);
Expect(
    disconnectTelemetry.TelemetryValue.Contains("connection_seq=7") &&
    disconnectTelemetry.TelemetryValue.Contains("battery=unknown") &&
    disconnectTelemetry.TelemetryValue.Contains("bluetooth_error=DeviceNotConnected"),
    "disconnect telemetry preserves session identity, Windows evidence, and unknown battery state");
uint edgeButtons = BinaryPrimitives.ReadUInt32LittleEndian(edge.AsSpan(4, 4));
Expect((edgeButtons & 0x00400000) != 0, "DualSense Edge maps Pro2 left paddle to L4");
Expect((edgeButtons & 0x00800000) != 0, "DualSense Edge maps Pro2 right paddle to R4");
byte[] ordinaryWithPaddles = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, edgeState);
uint ordinaryPaddleButtons = BinaryPrimitives.ReadUInt32LittleEndian(ordinaryWithPaddles.AsSpan(4, 4));
Expect((ordinaryPaddleButtons & 0x00C00000) == 0, "ordinary DualSense still drops Edge paddle bits when source has paddles");

byte[] ns = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Pro2, state);
Expect(ns.Length == 28, "NS2Pro wire size includes native motion timestamp");
uint nsButtons = BinaryPrimitives.ReadUInt32LittleEndian(ns.AsSpan(0, 4));
Expect(nsButtons == 0x0003FAFF, "NS2Pro button bitfield");
Expect(BinaryPrimitives.ReadUInt16LittleEndian(ns.AsSpan(4, 2)) == 0, "NS2Pro LX min");
Expect(BinaryPrimitives.ReadUInt16LittleEndian(ns.AsSpan(6, 2)) == 4095, "NS2Pro LY max");
Expect(BinaryPrimitives.ReadInt16LittleEndian(ns.AsSpan(14, 2)) == 4096, "NS2Pro static accel Y preserves Pro2 source coordinates");
byte[] nsNeutral = VirtualPadPackets.NeutralInput(ViiperDeviceProfile.Pro2);
Expect(nsNeutral.Length == 28, "NS2Pro neutral wire size");

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
motionState.AccelY = 4096;
motionState.AccelZ = 20;
motionState.MotionTimestampUs = 0x12345678;
byte[] dsMotion = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, motionState);
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(21, 2)) == 100, "DualSense PS5 gyro X converts Switch 2 raw through 16.384 dps scale");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(23, 2)) == -300, "DualSense PS5 gyro Y follows +source Z after physical conversion");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(25, 2)) == -200, "DualSense PS5 gyro Z follows -source Y after physical conversion");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(27, 2)) == 20, "DualSense PS5 accel X converts Pro2 4096/g state to DualSense 8192/g raw");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(29, 2)) == 40, "DualSense PS5 accel Y maps +source Z with 8192/g final scale");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotion.AsSpan(31, 2)) == -8192, "DualSense PS5 accel Z follows -source Y with 8192/g final scale");
byte[] edgeMotion = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseEdge, motionState);
Expect(edgeMotion.AsSpan(21, 12).SequenceEqual(dsMotion.AsSpan(21, 12)), "DualSense Edge shares the fixed PS5 output-layer IMU tuning");
byte[] dsMotionScaled = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.DualSenseLike,
    motionState,
    ps5OutputImuTuning: new Ps5OutputImuTuning(2.0, 0.5, 1.5));
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionScaled.AsSpan(21, 2)) == 200, "DualSense PS5 gyro pitch scale is configurable after physical conversion");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionScaled.AsSpan(23, 2)) == -150, "DualSense PS5 gyro yaw scale follows +source Z after physical conversion");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionScaled.AsSpan(25, 2)) == -300, "DualSense PS5 gyro roll scale follows -source Y after physical conversion");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionScaled.AsSpan(31, 2)) == -8192, "DualSense PS5 gyro scale does not affect accel");
byte[] dsMotionInverted = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.DualSenseLike,
    motionState,
    new GyroAxisInversion(false, true, false));
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionInverted.AsSpan(21, 2)) == 100, "DualSense inverted keeps final gyro X correction");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionInverted.AsSpan(23, 2)) == 300, "DualSense inverted Y flips final gyro Y");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionInverted.AsSpan(25, 2)) == -200, "DualSense inverted keeps final gyro Z correction");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionInverted.AsSpan(31, 2)) == -8192, "DualSense inverted leaves accel output correction unchanged");
byte[] dsMotionXzInverted = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.DualSenseLike,
    motionState,
    new GyroAxisInversion(true, false, true));
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionXzInverted.AsSpan(21, 2)) == -100, "DualSense X switch flips final gyro X");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionXzInverted.AsSpan(23, 2)) == -300, "DualSense X/Z switch keeps final gyro Y correction");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionXzInverted.AsSpan(25, 2)) == 200, "DualSense Z switch flips final gyro Z");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsMotionXzInverted.AsSpan(31, 2)) == -8192, "DualSense X/Z switches leave accel output correction unchanged");
var rawImuState = GamepadState.Neutral();
rawImuState.GyroValid = true;
rawImuState.AccelValid = true;
rawImuState.AccelY = 4096;
rawImuState.GyroX = 2000;
rawImuState.SwitchRawImuSamples =
[
    new SwitchImuRawSample(
        AccelX: 0,
        AccelY: 4096,
        AccelZ: 0,
        GyroX: (short)Math.Round(ProfessionalImuConverter.SwitchGyroRawPerDps * 10.0),
        GyroY: 0,
        GyroZ: 0,
        SampleIndex: 0,
        Offset: 48,
        SourceTimestampTicks: 0,
        SourceSequence: 1)
];
byte[] dsRawImu = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, rawImuState);
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsRawImu.AsSpan(21, 2)) == 2000, "DualSense PS5 uses calibrated GamepadState gyro instead of bypassing parser bias with raw IMU samples");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsRawImu.AsSpan(31, 2)) == -8192, "DualSense PS5 uses calibrated GamepadState accel instead of bypassing parser rest offset with raw IMU samples");
var latestRawImuState = GamepadState.Neutral();
latestRawImuState.GyroValid = true;
latestRawImuState.AccelValid = true;
latestRawImuState.GyroX = 290;
latestRawImuState.GyroY = 220;
latestRawImuState.GyroZ = -300;
latestRawImuState.AccelX = 10;
latestRawImuState.AccelY = 4096;
latestRawImuState.AccelZ = 20;
latestRawImuState.SwitchRawImuSamples =
[
    new SwitchImuRawSample(12, 3900, 30, 100, 80, -90, 0, 13, 0, 1),
    new SwitchImuRawSample(14, 4100, 50, 200, 160, -180, 1, 25, 0, 1),
    new SwitchImuRawSample(16, 4300, 70, 300, 240, -270, 2, 37, 0, 1)
];
byte[] dsLatestRawImu = VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike, latestRawImuState);
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsLatestRawImu.AsSpan(21, 2)) == 290, "DualSense PS5 uses freshest calibrated gyro X without 3-sample group delay");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsLatestRawImu.AsSpan(23, 2)) == -300, "DualSense PS5 uses freshest calibrated gyro Z for yaw");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsLatestRawImu.AsSpan(25, 2)) == -220, "DualSense PS5 uses freshest calibrated gyro Y for roll");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsLatestRawImu.AsSpan(27, 2)) == 20, "DualSense PS5 uses freshest calibrated accel X");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsLatestRawImu.AsSpan(29, 2)) == 40, "DualSense PS5 uses freshest calibrated accel Z");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsLatestRawImu.AsSpan(31, 2)) == -8192, "DualSense PS5 uses freshest calibrated accel Y");
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
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(14, 2)) == 4096, "NS2Pro accel Y preserves source Y without PS5 conversion");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(16, 2)) == 20, "NS2Pro accel Z maps directly");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(18, 2)) == 100, "NS2Pro gyro X maps directly");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(20, 2)) == 200, "NS2Pro gyro Y preserves source Y");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotion.AsSpan(22, 2)) == -300, "NS2Pro gyro Z maps directly");
Expect(BinaryPrimitives.ReadUInt32LittleEndian(nsMotion.AsSpan(24, 4)) == 0x12345678, "NS2Pro wire carries native FD2 motion timestamp");
byte[] nsMotionInverted = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.Pro2,
    motionState,
    new GyroAxisInversion(false, true, false));
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotionInverted.AsSpan(18, 2)) == 100, "NS2Pro inverted keeps gyro X");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotionInverted.AsSpan(20, 2)) == -200, "NS2Pro inverted flips only gyro Y");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotionInverted.AsSpan(22, 2)) == -300, "NS2Pro inverted keeps gyro Z");
Expect(BinaryPrimitives.ReadInt16LittleEndian(nsMotionInverted.AsSpan(14, 2)) == 4096, "NS2Pro inverted leaves accel mapping unchanged");
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
Expect(parsed.SwitchRawImuSamples.Length == 3, "standard Pro2 HID exposes three raw IMU samples when present");
Expect(parsed.SwitchRawImuOffset == 13, "standard raw IMU block offset is recorded");
Expect(parsed.SwitchRawImuSamples[2].AccelX == 1 && parsed.SwitchRawImuSamples[2].GyroZ == 6, "third standard raw IMU sample carries the legacy motion sample");
Expect(parsed.SwitchRawImuBytesHex.Length == 72, "standard raw IMU hex captures 36 bytes");

byte[] fd2 = new byte[60];
BinaryPrimitives.WriteUInt32LittleEndian(
    fd2.AsSpan(4, 4),
    0x00020000u | 0x00000080u | 0x00000004u);
Pack12(fd2, 10, 2048, 2048);
Pack12(fd2, 13, 2048, 2048);
BinaryPrimitives.WriteUInt32LittleEndian(fd2.AsSpan(42, 4), 0x89ABCDEF);
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
Expect(fd2Parsed.SwitchRawImuSamples.Length == 1, "FD2 60-byte payload exposes one raw IMU sample");
Expect(fd2Parsed.SwitchRawImuOffset == 48, "FD2 raw IMU block offset is recorded");
Expect(fd2Parsed.MotionTimestampUs == 0x89ABCDEF, "FD2 native motion timestamp is preserved");
Expect(fd2Parsed.SwitchRawImuSamples[0].AccelX == -7 && fd2Parsed.SwitchRawImuSamples[0].GyroZ == 77, "FD2 raw IMU sample carries signed values");

var stickCalibrationParser = new Pro2HidReportParser();
byte[] stickCalibrationFd2 = new byte[60];
Expect(
    stickCalibrationParser.StartManualStickCenterCalibration().Contains("status=started", StringComparison.Ordinal),
    "stick center calibration starts");
for (int i = 0; i < 40; i++)
{
    Pack12(stickCalibrationFd2, 10, 2060, 2030);
    Pack12(stickCalibrationFd2, 13, 2075, 2010);
    Expect(
        stickCalibrationParser.TryParseFd2Payload(stickCalibrationFd2, out _, out _),
        "stick center calibration parses source sample");
}
Pro2StickCalibrationResult stickCenterResult =
    stickCalibrationParser.CompleteManualStickCenterCalibration();
Expect(stickCenterResult.Success, "stick center calibration succeeds");
Expect(
    stickCenterResult.Profile is
    {
        CenterCalibrated: true,
        CenterLx: 2060,
        CenterLy: 2030,
        CenterRx: 2075,
        CenterRy: 2010
    },
    "stick center calibration preserves four independent centers");

Expect(
    stickCalibrationParser.StartManualStickRangeCalibration().Contains("status=started", StringComparison.Ordinal),
    "stick range calibration starts after center calibration");
for (int i = 0; i < 80; i++)
{
    bool minimum = (i & 1) == 0;
    Pack12(
        stickCalibrationFd2,
        10,
        minimum ? (ushort)700 : (ushort)3500,
        minimum ? (ushort)600 : (ushort)3420);
    Pack12(
        stickCalibrationFd2,
        13,
        minimum ? (ushort)760 : (ushort)3600,
        minimum ? (ushort)650 : (ushort)3380);
    Expect(
        stickCalibrationParser.TryParseFd2Payload(stickCalibrationFd2, out _, out _),
        "stick range calibration parses source sample");
}
Pro2StickCalibrationResult stickRangeResult =
    stickCalibrationParser.CompleteManualStickRangeCalibration();
Expect(
    stickRangeResult.Success && stickRangeResult.Profile?.RangeCalibrated == true,
    "stick range calibration succeeds with all eight directions");

Pack12(stickCalibrationFd2, 10, 700, 3420);
Pack12(stickCalibrationFd2, 13, 3600, 650);
Expect(
    stickCalibrationParser.TryParseFd2Payload(
        stickCalibrationFd2,
        out GamepadState calibratedEndpoints,
        out _),
    "calibrated endpoint report parses");
Expect(
    calibratedEndpoints.Lx == 0 &&
    calibratedEndpoints.Ly == GamepadState.AxisMax &&
    calibratedEndpoints.Rx == GamepadState.AxisMax &&
    calibratedEndpoints.Ry == 0,
    "asymmetric calibrated endpoints reach full output");

Pro2StickCalibrationProfile savedStickProfile = stickRangeResult.Profile!;
var restoredStickParser = new Pro2HidReportParser();
restoredStickParser.SetStickCalibration(savedStickProfile);
Pack12(stickCalibrationFd2, 10, 2060, 2030);
Pack12(stickCalibrationFd2, 13, 2075, 2010);
Expect(
    restoredStickParser.TryParseFd2Payload(
        stickCalibrationFd2,
        out GamepadState restoredStickCenter,
        out _),
    "saved stick calibration parses after restore");
Expect(
    restoredStickCenter.Lx == GamepadState.AxisCenter &&
    restoredStickCenter.Ly == GamepadState.AxisCenter &&
    restoredStickCenter.Rx == GamepadState.AxisCenter &&
    restoredStickCenter.Ry == GamepadState.AxisCenter,
    "saved stick calibration restores all centers");

Expect(
    restoredStickParser.StartManualStickRangeCalibration().Contains("status=started", StringComparison.Ordinal),
    "incomplete range retry starts");
for (int i = 0; i < 80; i++)
{
    Pack12(stickCalibrationFd2, 10, 1900, 1900);
    Pack12(stickCalibrationFd2, 13, 2200, 2200);
    restoredStickParser.TryParseFd2Payload(stickCalibrationFd2, out _, out _);
}
Pro2StickCalibrationResult incompleteRange =
    restoredStickParser.CompleteManualStickRangeCalibration();
Expect(!incompleteRange.Success, "incomplete range calibration is rejected");
Expect(
    restoredStickParser.StickCalibrationProfile == savedStickProfile,
    "rejected range calibration does not overwrite saved profile");

Expect(
    restoredStickParser.StartManualStickRangeCalibration().Contains("status=started", StringComparison.Ordinal),
    "range calibration starts for idempotent profile reload regression");
restoredStickParser.SetStickCalibration(savedStickProfile);
for (int i = 0; i < 80; i++)
{
    bool minimum = (i & 1) == 0;
    Pack12(
        stickCalibrationFd2,
        10,
        minimum ? (ushort)700 : (ushort)3500,
        minimum ? (ushort)600 : (ushort)3420);
    Pack12(
        stickCalibrationFd2,
        13,
        minimum ? (ushort)760 : (ushort)3600,
        minimum ? (ushort)650 : (ushort)3380);
    restoredStickParser.TryParseFd2Payload(stickCalibrationFd2, out _, out _);
}
Pro2StickCalibrationResult profileReloadRange =
    restoredStickParser.CompleteManualStickRangeCalibration();
Expect(
    profileReloadRange.Success,
    "reapplying the same saved profile does not cancel active range capture");

Dictionary<string, Pro2StickCalibrationProfile> normalizedStickProfiles =
    V60UserSettings.NormalizeStickCalibrations(
        new Dictionary<string, Pro2StickCalibrationProfile>
        {
            ["c8-48-05-5c-35-31"] = savedStickProfile
        });
Expect(
    normalizedStickProfiles.ContainsKey("C8:48:05:5C:35:31"),
    "stick calibration settings normalize BLE address keys");

byte[] primaryPro = new byte[63];
primaryPro[1] = 0x20;
primaryPro[2] = 0x01 | 0x20 | 0x40;
primaryPro[3] = 0x08 | 0x10 | 0x40;
primaryPro[4] = 0x01 | 0x08;
Pack12(primaryPro, 5, 2048, 2048);
Pack12(primaryPro, 8, 2048, 2048);
Expect(parser.TryParsePrimaryProPayload(primaryPro, out GamepadState primaryParsed, out string primarySource), "parse primary 0x000E Pro2 payload");
Expect(primarySource == "primary_pro_payload", "primary Pro2 parse source");
Expect(primaryParsed.IsPressed(GamepadButtons.South), "primary Pro2 parsed B/South");
Expect(primaryParsed.IsPressed(GamepadButtons.R2) && primaryParsed.IsPressed(GamepadButtons.Start), "primary Pro2 parsed ZR and Plus");
Expect(primaryParsed.IsPressed(GamepadButtons.DPadUp) && primaryParsed.IsPressed(GamepadButtons.L1), "primary Pro2 parsed dpad and L");
Expect(primaryParsed.IsPressed(GamepadButtons.Back) && primaryParsed.IsPressed(GamepadButtons.Home), "primary Pro2 parsed Minus and Home");
Expect(primaryParsed.IsPressed(GamepadButtons.PaddleLeft), "primary Pro2 parsed left grip button");
Expect(primaryParsed.L2 == 0 && primaryParsed.R2 == GamepadState.TriggerMax, "primary Pro2 digital triggers map correctly");
Expect(primaryParsed.Lx == GamepadState.AxisCenter && primaryParsed.Ry == GamepadState.AxisCenter, "primary Pro2 centered axes");
Expect(!primaryParsed.AccelValid && !primaryParsed.GyroValid, "primary Pro2 leaves motion to FD2 channel");

var proCalibration = new ImuCalibrationState();
var rawFormula = new SwitchImuRawSample(
    AccelX: 4096,
    AccelY: -4096,
    AccelZ: 2048,
    GyroX: 142,
    GyroY: -284,
    GyroZ: 14,
    SampleIndex: 0,
    Offset: 48,
    SourceTimestampTicks: Stopwatch.GetTimestamp(),
    SourceSequence: 1);
ImuPhysicalSample physicalFormula =
    ProfessionalImuConverter.ToPhysical(rawFormula, proCalibration);
ExpectNear(physicalFormula.AccelXG, 1.0, 0.0001, "professional accel X raw->g");
ExpectNear(physicalFormula.AccelYG, 0.5, 0.0001, "professional project accel Y maps source Z");
ExpectNear(physicalFormula.AccelZG, 1.0, 0.0001, "professional project accel Z maps negative source Y");
ExpectNear(physicalFormula.GyroXDps, 142 / ProfessionalImuConverter.SwitchGyroRawPerDps, 0.0001, "professional project gyro pitch maps source X");
ExpectNear(physicalFormula.GyroYDps, 14 / ProfessionalImuConverter.SwitchGyroRawPerDps, 0.0001, "professional project gyro yaw maps source Z");
ExpectNear(physicalFormula.GyroZDps, 284 / ProfessionalImuConverter.SwitchGyroRawPerDps, 0.0001, "professional project gyro roll maps negative source Y");
DualSenseImuRawSample dsProfessionalRaw =
    ProfessionalImuConverter.ToDualSenseRaw(
        physicalFormula,
        ProfessionalImuOptions.ForTestModes(Ps5OutputImuTuning.Default));
Expect(dsProfessionalRaw.AccelX == 8192, "professional DualSense accel X raw scale");
Expect(dsProfessionalRaw.AccelY == 4096, "professional DualSense accel Y raw scale");
Expect(dsProfessionalRaw.AccelZ == 8192, "professional DualSense accel Z raw scale/sign");
Expect(dsProfessionalRaw.GyroX == 142, "professional DualSense gyro X preserves Switch 2 angular scale");
Expect(dsProfessionalRaw.GyroY == 14, "professional DualSense gyro Y preserves Switch 2 angular scale");
Expect(dsProfessionalRaw.GyroZ == 284, "professional DualSense gyro Z preserves Switch 2 angular scale");
Expect(
    ProfessionalImuConverter.ToDualSenseRaw(
        new ImuPhysicalSample(0, 0, 0, 1.0, 10.0, 819.2, 0, 0, 0),
        ProfessionalImuOptions.ForTestModes(Ps5OutputImuTuning.Default)) is
    { GyroX: 16, GyroY: 164, GyroZ: 13422 },
    "professional DualSense gyro raw scale examples: 1dps=16.384, 10dps=163.84, 819.2dps=13421.773");

var biasCalibration = new ImuCalibrationState();
long biasT0 = Stopwatch.GetTimestamp();
biasCalibration.BeginManual3s();
GyroBiasCalibrationEvent? biasEvent = null;
for (int i = 0; i <= 180; i++)
{
    biasEvent = biasCalibration.ObserveManualCalibration(
        [rawFormula with
        {
            AccelX = 0,
            AccelY = 4096,
            AccelZ = 0,
            GyroX = 100,
            GyroY = -50,
            GyroZ = 25,
            SourceTimestampTicks = biasT0 + (Stopwatch.Frequency * i / 60),
            SourceSequence = (ulong)i
        }],
        sampleAgeMs: 12);
}
Expect(
    biasEvent is { Committed: true } &&
    biasCalibration.Calibrated &&
    biasCalibration.BiasStatus == GyroBiasStatus.CalibratedAndApplied &&
    biasCalibration.BiasUpdateCount == 1,
    "professional gyro raw bias commits only through manual 3s stationary calibration");
ImuPhysicalSample biasCorrected =
    ProfessionalImuConverter.ToPhysical(rawFormula with
    {
        GyroX = 100,
        GyroY = -50,
        GyroZ = 25
    }, biasCalibration);
ExpectNear(biasCorrected.GyroXDps, 0, 0.001, "professional gyro X subtracts raw bias");
ExpectNear(biasCorrected.GyroYDps, 0, 0.001, "professional gyro Y subtracts raw bias");
ExpectNear(biasCorrected.GyroZDps, 0, 0.001, "professional gyro Z subtracts raw bias");

var rejectedCalibration = new ImuCalibrationState();
rejectedCalibration.BeginManual3s();
GyroBiasCalibrationEvent? rejectedBias = rejectedCalibration.ObserveManualCalibration(
    [rawFormula with
    {
        AccelX = 0,
        AccelY = 9000,
        AccelZ = 0,
        SourceTimestampTicks = biasT0,
        SourceSequence = 1
    }],
    sampleAgeMs: 12);
Expect(
    rejectedBias is { Committed: false } &&
    !rejectedCalibration.Calibrated &&
    rejectedCalibration.BiasStatus == GyroBiasStatus.CalibrationRejectedMoving,
    "professional manual gyro bias rejects moving/out-of-range calibration without updating bias");

var runtimeMessages = new List<string>();
using (var professionalRuntime = new ProfessionalImuRuntime(
           ProfessionalImuOptions.ForTestModes(Ps5OutputImuTuning.Default),
           "unit_test_professional_imu",
           new ImmediateProgress<string>(runtimeMessages.Add)))
{
    long rt0 = Stopwatch.GetTimestamp();
    var uncalibratedState = GamepadState.Neutral();
    uncalibratedState.SwitchRawImuSamples =
    [
        rawFormula with
        {
            AccelX = 0,
            AccelY = 4096,
            AccelZ = 0,
            GyroX = 120,
            GyroY = -80,
            GyroZ = 40,
            SourceTimestampTicks = rt0,
            SourceSequence = 100
        }
    ];
    uncalibratedState.SourceTimestampTicks = rt0;
    uncalibratedState.RawNotificationSequence = 100;
    ProfessionalImuFrame uncalibratedFrame = professionalRuntime.Process(uncalibratedState, 8);
    Expect(
        uncalibratedFrame.DualSenseRaw is { GyroX: 0, GyroY: 0, GyroZ: 0 },
        "professional runtime zeros DualSense gyro output before manual bias calibration");
    Expect(
        uncalibratedFrame.Telemetry.Contains("professional_gyro_uncalibrated_behavior=ZeroOutput", StringComparison.Ordinal) &&
        uncalibratedFrame.Telemetry.Contains("output_gyro_muted_until_calibrated=true", StringComparison.Ordinal) &&
        uncalibratedFrame.Telemetry.Contains("selected_output_gyro_x_dps=0", StringComparison.Ordinal) &&
        uncalibratedFrame.Telemetry.Contains("integral_state=Disabled", StringComparison.Ordinal) &&
        uncalibratedFrame.Telemetry.Contains("integral_running=false", StringComparison.Ordinal),
        "professional telemetry proves uncalibrated gyro is muted and integral is disabled");

    string startBias = professionalRuntime.StartGyroBiasCalibration();
    Expect(startBias.Contains("started", StringComparison.OrdinalIgnoreCase), "calibrate gyro bias command returns started");
    Expect(
        runtimeMessages.Any(m => m.Contains("Gyro bias calibration started duration=3s", StringComparison.Ordinal)),
        "calibrate gyro bias command reaches runtime backend log");

    ProfessionalImuFrame calibratedFrame = default;
    for (int i = 0; i <= 180; i++)
    {
        long ticks = rt0 + Stopwatch.Frequency + (Stopwatch.Frequency * i / 60);
        var calibrationState = GamepadState.Neutral();
        calibrationState.SwitchRawImuSamples =
        [
            rawFormula with
            {
                AccelX = 0,
                AccelY = 4096,
                AccelZ = 0,
                GyroX = 120,
                GyroY = -80,
                GyroZ = 40,
                SourceTimestampTicks = ticks,
                SourceSequence = (ulong)(200 + i)
            }
        ];
        calibrationState.SourceTimestampTicks = ticks;
        calibrationState.RawNotificationSequence = (ulong)(200 + i);
        calibratedFrame = professionalRuntime.Process(calibrationState, 8);
    }

    Expect(
        runtimeMessages.Any(m =>
            m.Contains("Gyro bias committed", StringComparison.Ordinal) &&
            m.Contains("is_bias_applied_to_output=true", StringComparison.Ordinal)),
        "manual 3s calibration commits bias and marks output as bias-applied");
    Expect(
        calibratedFrame.DualSenseRaw is { GyroX: 0, GyroY: 0, GyroZ: 0 } &&
        calibratedFrame.Telemetry.Contains("output_gyro_muted_until_calibrated=false", StringComparison.Ordinal) &&
        calibratedFrame.Telemetry.Contains("is_bias_applied_to_output=true", StringComparison.OrdinalIgnoreCase),
        "calibrated stationary gyro output is unmuted and corrected to zero");

    var directionState = GamepadState.Neutral();
    long directionTicks = rt0 + 4 * Stopwatch.Frequency;
    directionState.SwitchRawImuSamples =
    [
        rawFormula with
        {
            AccelX = 0,
            AccelY = 4096,
            AccelZ = 0,
            GyroX = 262,
            GyroY = 62,
            GyroZ = 182,
            SourceTimestampTicks = directionTicks,
            SourceSequence = 450
        }
    ];
    directionState.SourceTimestampTicks = directionTicks;
    directionState.RawNotificationSequence = 450;
    ProfessionalImuFrame directionFrame = professionalRuntime.Process(directionState, 8);
    Expect(
        directionFrame.DualSenseRaw is { GyroX: 142, GyroY: 142, GyroZ: -142 },
        "professional default output uses ProjectGyro +X,+Z,-Y without implicit inversion");
    string inversionResult = professionalRuntime.SetOutputGyroInversion(false, false, false);
    Expect(inversionResult.Contains("yaw=false", StringComparison.Ordinal), "professional gyro inversion can be changed at runtime");
    directionState.RawNotificationSequence = 451;
    directionState.SwitchRawImuSamples =
    [
        directionState.SwitchRawImuSamples[0] with
        {
            SourceSequence = 451,
            SourceTimestampTicks = directionTicks + Stopwatch.Frequency / 60
        }
    ];
    ProfessionalImuFrame normalDirectionFrame = professionalRuntime.Process(directionState, 8);
    Expect(
        normalDirectionFrame.DualSenseRaw is { GyroX: 142, GyroY: 142, GyroZ: -142 },
        "professional gyro inversion false,false,false keeps ProjectGyro +X,+Z,-Y output");

    string startPitch = professionalRuntime.StartNinetyDegreeTest(ProfessionalImuTestAxis.Pitch);
    Expect(startPitch.Contains("Pitch", StringComparison.Ordinal), "90 degree pitch test starts after calibration");
    short pitchRaw = (short)Math.Round(120 + ProfessionalImuConverter.SwitchGyroRawPerDps * 360.0);
    for (int i = 0; i < 2; i++)
    {
        long ticks = rt0 + 5 * Stopwatch.Frequency + i * Stopwatch.Frequency / 4;
        var pitchState = GamepadState.Neutral();
        pitchState.SwitchRawImuSamples =
        [
            rawFormula with
            {
                AccelX = 0,
                AccelY = 4096,
                AccelZ = 0,
                GyroX = pitchRaw,
                GyroY = -80,
                GyroZ = 40,
                SourceTimestampTicks = ticks,
                SourceSequence = (ulong)(500 + i)
            }
        ];
        pitchState.SourceTimestampTicks = ticks;
        pitchState.RawNotificationSequence = (ulong)(500 + i);
        professionalRuntime.Process(pitchState, 8);
    }

    string stopPitch = professionalRuntime.StopNinetyDegreeTest();
    Expect(
        stopPitch.Contains("pass", StringComparison.Ordinal) &&
        runtimeMessages.Any(m => m.Contains("90deg test result", StringComparison.Ordinal)),
        "90 degree pitch test integrates only during test state and reports result");
}

var professionalIntegrator = new ImuIntegrator();
long t0 = Stopwatch.GetTimestamp();
professionalIntegrator.Integrate(
    [new ImuPhysicalSample(0, 0, 1, 360, 0, 0, 0, t0, 1)],
    1,
    t0);
professionalIntegrator.Integrate(
    [new ImuPhysicalSample(0, 0, 1, 360, 0, 0, 0, t0 + Stopwatch.Frequency / 4, 2)],
    2,
    t0 + Stopwatch.Frequency / 4);
ExpectNear(professionalIntegrator.PitchDegrees, 90, 0.001, "professional integrator uses source timestamp dt");
professionalIntegrator.Integrate(
    [new ImuPhysicalSample(0, 0, 1, 360, 0, 0, 0, t0 + Stopwatch.Frequency / 2, 2)],
    2,
    t0 + Stopwatch.Frequency / 2);
ExpectNear(professionalIntegrator.PitchDegrees, 90, 0.001, "professional integrator ignores duplicate source sequence");

byte[] dsProfessionalPacket = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.DualSenseProfessionalImuTest,
    GamepadState.Neutral(),
    professionalDualSenseImu: new DualSenseImuRawSample(1, 2, 3, 4, 5, 6, true));
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsProfessionalPacket.AsSpan(21, 2)) == 4, "professional PS5 packet writes gyro X raw directly");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsProfessionalPacket.AsSpan(31, 2)) == 3, "professional PS5 packet writes accel Z raw directly");
var normalAudit = ProfessionalHidReportAuditor.ApplyAndAudit(
    dsProfessionalPacket,
    new DualSenseImuRawSample(1, 2, 3, 4, 5, 6, true),
    new ImuPhysicalSample(0, 0, 0, 0.25, 0.5, 0.75, 0, 0, 0),
    new ProfessionalHidAuditControlState(
        ProfessionalHidAuditMode.Normal,
        0,
        0,
        0,
        false,
        0,
        0,
        0,
        "none"));
Expect(normalAudit.Result == ProfessionalHidAuditResult.OK, "professional HID audit normal result OK");
Expect(normalAudit.FinalReportDecodedGyroXRaw == 4 &&
       normalAudit.FinalReportDecodedGyroYRaw == 5 &&
       normalAudit.FinalReportDecodedGyroZRaw == 6,
    "professional HID audit decodes gyro from final report offsets");
Expect(normalAudit.LegacyPs5MapperAppliedAfterProfessionalOutput == false, "professional HID audit bypasses legacy PS5 mapper");
var zeroAudit = ProfessionalHidReportAuditor.ApplyAndAudit(
    dsProfessionalPacket,
    new DualSenseImuRawSample(1, 2, 3, 4, 5, 6, true),
    null,
    new ProfessionalHidAuditControlState(
        ProfessionalHidAuditMode.ForceFinalGyroZero,
        0,
        0,
        0,
        false,
        0,
        0,
        0,
        "none"));
Expect(zeroAudit.Result == ProfessionalHidAuditResult.FORCED_ZERO, "professional HID audit force-zero result");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsProfessionalPacket.AsSpan(21, 2)) == 0 &&
       BinaryPrimitives.ReadInt16LittleEndian(dsProfessionalPacket.AsSpan(23, 2)) == 0 &&
       BinaryPrimitives.ReadInt16LittleEndian(dsProfessionalPacket.AsSpan(25, 2)) == 0,
    "professional HID audit force-zero mutates only final gyro fields");
Expect(BinaryPrimitives.ReadInt16LittleEndian(dsProfessionalPacket.AsSpan(27, 2)) == 1 &&
       BinaryPrimitives.ReadInt16LittleEndian(dsProfessionalPacket.AsSpan(29, 2)) == 2 &&
       BinaryPrimitives.ReadInt16LittleEndian(dsProfessionalPacket.AsSpan(31, 2)) == 3,
    "professional HID audit force-zero keeps accel fields");
var pulseAudit = ProfessionalHidReportAuditor.ApplyAndAudit(
    dsProfessionalPacket,
    new DualSenseImuRawSample(1, 2, 3, 4, 5, 6, true),
    null,
    new ProfessionalHidAuditControlState(
        ProfessionalHidAuditMode.ForceFinalGyroSyntheticPulse,
        0,
        0,
        0,
        true,
        0,
        8192,
        0,
        "Y"));
Expect(pulseAudit.Result == ProfessionalHidAuditResult.SYNTHETIC_PULSE &&
       pulseAudit.FinalReportDecodedGyroXRaw == 0 &&
       pulseAudit.FinalReportDecodedGyroYRaw == 8192 &&
       pulseAudit.FinalReportDecodedGyroZRaw == 0,
    "professional HID audit synthetic pulse writes final report gyro axis");
var staticAuditPacket = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.DualSenseProfessionalImuTest,
    GamepadState.Neutral(),
    professionalDualSenseImu: new DualSenseImuRawSample(1, 2, 3, 4, 5, 6, true));
var staticAudit = ProfessionalHidReportAuditor.ApplyAndAudit(
    staticAuditPacket,
    new DualSenseImuRawSample(1, 2, 3, 4, 5, 6, true),
    null,
    new ProfessionalHidAuditControlState(
        ProfessionalHidAuditMode.ForceFinalGyroStaticRaw,
        8192,
        0,
        0,
        false,
        0,
        0,
        0,
        "none"));
Expect(staticAudit.FinalPackGyroXRaw == 8192 &&
       staticAudit.FinalReportDecodedGyroXRaw == 8192,
    "professional HID audit static raw writes final report gyro directly");
byte[] xboxProfessionalPacket = VirtualPadPackets.FromGamepad(
    ViiperDeviceProfile.XboxProfessionalImuTest,
    motionState);
Expect(xboxProfessionalPacket.AsSpan().SequenceEqual(VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Xbox, motionState)), "professional Xbox defaults to diagnostic-only output");

var motionParser = new Pro2HidReportParser();
byte[] flatFd2 = new byte[60];
Pack12(flatFd2, 10, 2048, 2048);
Pack12(flatFd2, 13, 2048, 2048);
for (int i = 0; i < 64; i++)
{
    WriteMotion(flatFd2, 48, 120, -500, 4150, 90, -120, 70);
    Expect(motionParser.TryParse(flatFd2, out _, out _), "motion calibration learns stationary 4096/g FD2 sample");
}

WriteMotion(flatFd2, 48, 620, -300, 4050, 290, -120, -330);
Expect(motionParser.TryParse(flatFd2, out GamepadState calibratedMotion, out _), "motion calibration applies to FD2 sample");
Expect(calibratedMotion.AccelX == 620, "gyro calibration does not alter accel X");
Expect(calibratedMotion.AccelY == -300, "gyro calibration does not alter accel Y");
Expect(calibratedMotion.AccelZ == 4050, "gyro calibration does not alter accel Z");
Expect(calibratedMotion.GyroX == 200, "motion calibration subtracts gyro X bias");
Expect(calibratedMotion.GyroY == 0, "motion calibration subtracts gyro Y bias");
Expect(calibratedMotion.GyroZ == -400, "motion calibration subtracts gyro Z bias");
Expect(
    motionParser.StartManualGyroCalibration().Contains("三秒", StringComparison.Ordinal),
    "manual source gyro calibration starts explicitly");
for (int i = 0; i < 200; i++)
{
    WriteMotion(flatFd2, 48, 120, -500, 4150, 101, -84, 39);
    Expect(motionParser.TryParse(flatFd2, out _, out _), "manual source gyro calibration collects stationary FD2 samples");
}
WriteMotion(flatFd2, 48, 120, -500, 4150, 101, -84, 39);
Expect(motionParser.TryParse(flatFd2, out GamepadState manuallyCalibratedMotion, out _), "manual source gyro calibration applies committed bias");
Expect(manuallyCalibratedMotion.GyroX == 0 &&
       manuallyCalibratedMotion.GyroY == 0 &&
       manuallyCalibratedMotion.GyroZ == 0,
    "manual source gyro calibration removes measured bias without deadzone or filtering");
Expect(motionParser.GyroCalibrationSummary.Contains("manual_committed", StringComparison.Ordinal),
    "manual source gyro calibration exposes committed telemetry");
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

byte[] edgeFeedback = [44, 180, 0, 0, 64, 1];
Expect(Pro2OutputPacketMapper.TryMapFeedback(ViiperDeviceProfile.DualSenseEdge, edgeFeedback, out Pro2OutputPacket edgeRumble, out _), "map DualSense Edge ordinary feedback");
Expect(edgeRumble.Source == "dualsense-edge-ordinary" && edgeRumble.Active, "DualSense Edge feedback uses ordinary rumble path");
Expect(
    ViiperDeviceProfile.DualSenseProfessionalImuTest.UsesDualSenseHaptics &&
    ViiperDeviceProfile.DualSenseProfessionalImuTest.FeedbackSize == DualSenseHapticFrame.WireSize,
    "DualSense Professional IMU test reuses the ordinary PS5 HD haptic feedback contract");

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
    V60UserSettings.NormalizePs5GyroScale(0.01) == 0.1 &&
    V60UserSettings.NormalizePs5GyroScale(4.8) == 4 &&
    V60UserSettings.NormalizePs5GyroScale(double.NaN) == 1,
    "PS5 gyro scale is normalized to 0.1..4.0");
Expect(
    new Ps5OutputImuTuning(double.NaN, 2.349, 9).Normalize() ==
    new Ps5OutputImuTuning(1.0, 2.35, 4.0),
    "PS5 output IMU tuning normalizes per-axis gyro scale");
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

var frontAudioScheduler = new DualSenseHapticRumbleScheduler();
Expect(
    frontAudioScheduler.TryProcess(
        HapticFrame(1, audioModeReport),
        out _,
        out _,
        out _),
    "front-audio scheduler enters audio mode");
double frontLeftPhase = 0;
double frontRightPhase = 0;
Pro2OutputPacket? frontAudioPacket = null;
for (int i = 0; i < 40; i++)
{
    byte[] pcm = HapticPcmPacket(
        ref frontLeftPhase,
        ref frontRightPhase,
        140,
        330,
        frontPair: true);
    if (frontAudioScheduler.TryProcess(
            HapticFrame(2, pcm),
            out Pro2OutputPacket candidate,
            out _,
            out _) &&
        candidate.Active)
    {
        frontAudioPacket = candidate;
        break;
    }
}
Expect(
    frontAudioPacket == null,
    "DualSense front audio channels never become haptics when rear actuator channels are silent");

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
    Pro2BleInputSource.ShouldKeepLiveInput(20.0, 20.0, 80, 80),
    "V6.2.22 keeps 20 Hz BLE live and lets virtual report rate auto-reduce");
Expect(
    !Pro2BleInputSource.ShouldKeepLiveInput(9.0, 9.0, 80, 80),
    "sub-10 Hz BLE input is rejected");
Expect(
    !Pro2BleInputSource.ShouldKeepLiveInput(65.0, 5.0, 200, 15),
    "mostly unparsed BLE notifications are rejected");
Expect(
    VirtualReportRateGovernor.SelectAutoRateHz(11.0, 125) == 10 &&
    VirtualReportRateGovernor.SelectAutoRateHz(18.0, 125) == 20 &&
    VirtualReportRateGovernor.SelectAutoRateHz(35.0, 125) == 30 &&
    VirtualReportRateGovernor.SelectAutoRateHz(66.0, 125) == 60 &&
    VirtualReportRateGovernor.SelectAutoRateHz(133.0, 125) == 125 &&
    VirtualReportRateGovernor.SelectAutoRateHz(160.0, 250) == 250,
    "auto report governor maps BLE source rate to safe virtual output buckets");
Expect(
    !ProfessionalImuOptions.Default.AutoReduceVirtualReportRate &&
    ProfessionalImuOptions.Default.OutputReportRateMode == OutputReportRateMode.Fixed,
    "normal runtime honors the selected USB report rate and holds latest BLE state");

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

const string usbipPortFixture = """
Imported USB devices
====================
Port 01: device in use at Full Speed(12Mbps)
         Sony Corp. : DualSense Edge wireless controller (PS5) (054c:0df2)
           -> usbip://localhost:3241/1-1
           -> remote bus/dev 001/001
Port 02: device in use at Full Speed(12Mbps)
         Nintendo Co., Ltd. : Switch 2 Pro Controller (057e:2069)
           -> usbip://localhost:3241/2-1
           -> remote bus/dev 002/001
""";
IReadOnlyList<int> edgePorts = ControllerEnumerationDiagnostics.FindUsbipPortsForRemoteDevice(
    usbipPortFixture,
    3241,
    1,
    "1");
Expect(edgePorts.SequenceEqual([1]), "orderly USBIP detach selects only the exact Edge bus/dev port");
IReadOnlyList<int> pro2Ports = ControllerEnumerationDiagnostics.FindUsbipPortsForRemoteDevice(
    usbipPortFixture,
    3241,
    2,
    "1");
Expect(pro2Ports.SequenceEqual([2]), "orderly USBIP detach selects only the exact Pro2 bus/dev port");
IReadOnlyList<int> wrongServerPorts = ControllerEnumerationDiagnostics.FindUsbipPortsForRemoteDevice(
    usbipPortFixture,
    33241,
    1,
    "1");
Expect(wrongServerPorts.Count == 0, "orderly USBIP detach never matches another VIIPER USB server port");

const string steamIfHidFixture = """
[2026-07-14 22:04:03] Local Device Found
  type: 057e 2069
  path: sdl://31
  serial_number:  - 0
[2026-07-14 22:04:03]   Manufacturer:
[2026-07-14 22:04:03]   Product:      If_Hid
[2026-07-14 22:04:03]   Release:      0
[2026-07-14 22:04:03]   Interface:    -1
""";
SteamIfHidObservation? ifHidObservation =
    SteamControllerCacheService.FindLatestIfHidObservation(steamIfHidFixture);
Expect(
    ifHidObservation is { Vid: "057E", Pid: "2069" } &&
    ifHidObservation.ObservedAt == new DateTime(2026, 7, 14, 22, 4, 3),
    "Steam If_Hid fallback block is parsed even when detail lines repeat timestamps");

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

internal sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
