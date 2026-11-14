using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;

namespace Y700Switch2V60Viiper;

// Physical Pro1 calibration and report framing are separate from Pro2 FD2.
// Protocol references and replay coverage are recorded in docs/physical-inputs.md.
public sealed class NintendoPhysicalParser
{
    // Factory correction (Pro1) precedes exactly one time-based bias learner.
    // The framing parser must not apply its own, second gyro calibration.
    private readonly Pro2HidReportParser parser = new(false, applyMotionCalibration: false);
    private readonly Pro2GyroBiasCalibration bias = new();
    private readonly int[] centers = [2048,2048,2048,2048], minus = [1600,1600,1600,1600], plus = [1600,1600,1600,1600];
    private readonly short[] accelBias = new short[3], gyroBias = new short[3];
    private readonly double[] accelScale = [1,1,1], gyroScale = [1,1,1];
    private StickDeadzoneSettings deadzones = new();
    private readonly NintendoInputKind kind;
    private byte lastTimer;
    private bool hasTimer;
    private uint clock;
    private bool manualStickProfile;
    public string Calibration { get; private set; } = "factory_not_read";
    public NintendoPhysicalParser(NintendoInputKind kind) { this.kind = kind; }
    public void SetDeadzones(StickDeadzoneSettings settings) { deadzones = settings.Normalize(); parser.SetStickDeadzones(settings); }
    public string StartGyroCalibration() => bias.StartManualCalibration();
    public string GyroCalibrationSummary => bias.Summary;
    public string GyroCalibrationResult => bias.LastResult;
    public void CancelGyroCalibration() => bias.CancelManualCalibration();
    public Pro2StickCalibrationProfile StickCalibrationProfile => parser.StickCalibrationProfile;
    public string StickCalibrationSummary => parser.StickCalibrationSummary;
    public Pro2PhysicalStickAxes LastPhysicalStickAxes => parser.LastPhysicalStickAxes;
    public string SetStickCalibration(Pro2StickCalibrationProfile? profile)
    {
        manualStickProfile = profile?.CenterCalibrated == true;
        return parser.SetStickCalibration(profile);
    }
    public string StartStickCalibration(bool range) => range ? parser.StartManualStickRangeCalibration() : parser.StartManualStickCenterCalibration();
    public void CancelStickCalibration() => parser.CancelManualStickCalibration();
    public Pro2StickCalibrationResult CompleteStickCalibration(bool range)
    {
        var result = range ? parser.CompleteManualStickRangeCalibration() : parser.CompleteManualStickCenterCalibration();
        if (result.Success) manualStickProfile = true;
        return result;
    }
    public void SetPro1FactoryCalibration(ReadOnlySpan<byte> sticks, ReadOnlySpan<byte> imu)
    {
        bool stickOk = sticks.Length >= 18 && !sticks.ToArray().All(b => b == 0xff || b == 0);
        if (stickOk)
        {
            for (int axis = 0; axis < 4; axis++)
            {
                int side = axis / 2, coordinate = axis % 2, offset = side * 9;
                int center = Unpack(sticks, offset + (side == 0 ? 3 : 0), coordinate);
                int low = Unpack(sticks, offset + (side == 0 ? 6 : 3), coordinate);
                int high = Unpack(sticks, offset + (side == 0 ? 0 : 6), coordinate);
                if (center is < 512 or > 3583 || low is < 256 or > 2048 || high is < 256 or > 2048) { stickOk = false; continue; }
                centers[axis] = center; minus[axis] = low; plus[axis] = high;
            }
        }
        bool imuOk = imu.Length >= 24;
        if (imuOk) for (int axis = 0; axis < 3; axis++)
        {
            short a0 = BinaryPrimitives.ReadInt16LittleEndian(imu.Slice(axis * 2, 2));
            short a1 = BinaryPrimitives.ReadInt16LittleEndian(imu.Slice(6 + axis * 2, 2));
            short g0 = BinaryPrimitives.ReadInt16LittleEndian(imu.Slice(12 + axis * 2, 2));
            short g1 = BinaryPrimitives.ReadInt16LittleEndian(imu.Slice(18 + axis * 2, 2));
            if (a1 - a0 < 2048 || g1 - g0 < 2048) { imuOk = false; continue; }
            accelBias[axis] = a0; accelScale[axis] = 4 * 4096.0 / (a1 - a0);
            gyroBias[axis] = g0; gyroScale[axis] = 936 * 16.384 / (g1 - g0);
        }
        Calibration = $"stick_factory={stickOk} imu_factory={imuOk}; incomplete_fields=explicit_fallback";
    }
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state, long? timestampTicks = null)
    {
        long now = timestampTicks ?? Stopwatch.GetTimestamp();
        state = GamepadState.Neutral();
        if (kind == NintendoInputKind.Pro2Usb)
        {
            // USB report 05 carries the FD2 layout after its report ID. ACKs and
            // vendor responses never increment physical-input counters.
            bool parsed = report.Length >= 61 && report[0] == 5 && parser.TryParseFd2Payload(report[1..], out state, out _);
            if (parsed) bias.Apply(state, now);
            return parsed;
        }
        if (report.Length < 49 || report[0] is not (0x30 or 0x31) || !parser.TryParseHidInputReport(report, out state, out _)) return false;
        if (!manualStickProfile)
        {
            state.Lx = Axis(Unpack(report,6,0),0); state.Ly = Axis(Unpack(report,6,1),1);
            state.Rx = Axis(Unpack(report,9,0),2); state.Ry = Axis(Unpack(report,9,1),3);
        }
        int power = (report[2] >> 5) & 7;
        state.BatteryLevel = (byte)Math.Min(power,4); state.BatteryPercent = (byte)(Math.Min(power,4) * 25);
        state.BatteryPercentApproximate = true; state.BatteryCharging = (report[2] & 0x10) != 0;
        state.BatteryPowerKnown = true; state.BatteryExternalPower = (report[2] & 1) != 0;
        for (int axis = 0; axis < 3; axis++)
        {
            // Use the newest of the three samples once for this button report.
            short a = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(37 + axis*2,2));
            short g = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(43 + axis*2,2));
            short av = Short((a - accelBias[axis]) * accelScale[axis]), gv = Short((g - gyroBias[axis]) * gyroScale[axis]);
            if (axis == 0) { state.AccelX = av; state.GyroX = gv; }
            if (axis == 1) { state.AccelY = av; state.GyroY = gv; }
            if (axis == 2) { state.AccelZ = av; state.GyroZ = gv; }
        }
        if (hasTimer) clock += (uint)(unchecked((byte)(report[1] - lastTimer)) * 5000);
        lastTimer = report[1]; hasTimer = true;
        state.MotionTimestampUs = clock;
        bias.Apply(state, now);
        return true;
    }
    private ushort Axis(int raw, int axis)
    {
        double value = (raw - centers[axis]) / (double)(raw < centers[axis] ? minus[axis] : plus[axis]);
        double dz = (axis < 2 ? deadzones.LeftPercent : deadzones.RightPercent) / 100;
        value = Math.Abs(value) <= dz ? 0 : Math.CopySign(Math.Min(1,(Math.Abs(value)-dz)/(1-dz)),value);
        return (ushort)Math.Clamp(Math.Round(2048 + value * (value < 0 ? 2048 : 2047)),0,4095);
    }
    private static int Unpack(ReadOnlySpan<byte> d, int offset, int axis) => axis == 0 ? d[offset] | ((d[offset+1]&15)<<8) : (d[offset+1]>>4) | (d[offset+2]<<4);
    private static short Short(double n) => (short)Math.Clamp(Math.Round(n),short.MinValue,short.MaxValue);
}

public static class Pro1PhysicalRumble
{
    public static byte[] Neutral => [0,1,0x40,0x40,0,1,0x40,0x40];
    public static byte[] FromPro2(ReadOnlySpan<byte> report, double gain = 1)
    {
        if (report.Length != 64 || report[0] != 2) throw new ArgumentException("Expected Pro2 report 02");
        if ((report[1]&0xf0)!=0x50 || (report[17]&0xf0)!=0x50)
            throw new NotSupportedException("未解码的 Pro2 马达格式不能按五字节音调转换成 Pro1");
        byte[] result = new byte[8];
        for (int side = 0; side < 2; side++)
        {
            int offset = 2 + side * 16;
            ulong bits = 0; for (int i = 0; i < 5; i++) bits |= (ulong)report[offset+i] << (i*8);
            double high = bits & 1023, low = (bits >> 20) & 1023;
            // Cross-generation frequency controls are not calibrated physical Hz.
            // Match the existing bounded proxy; native Pro1->Pro1 bypasses this.
            double highAmp = ((bits >> 10) & 1023) / 453.0 * gain, lowAmp = ((bits >> 30) & 1023) / 453.0 * gain;
            Encode(low, lowAmp, high, highAmp).CopyTo(result, side*4);
        }
        return result;
    }
    public static byte[] ScaleNative(ReadOnlySpan<byte> native, double gain)
    {
        if (native.Length != 8) throw new ArgumentException("Expected eight Pro1 rumble bytes");
        if (gain == 1) return native.ToArray();
        byte[] result = new byte[8];
        for(int side=0;side<2;side++)
        {
            var tones=Switch1RumbleConverter.Decode(native.Slice(side*4,4));
            byte[] encoded=Encode(tones.LowHz,tones.LowAmplitude/29000.0*gain,tones.HighHz,tones.HighAmplitude/29000.0*gain);
            // Amplitude-only adjustment preserves native frequency bits.
            result[side*4]=native[side*4]; result[side*4+1]=(byte)((native[side*4+1]&1)|(encoded[1]&0xfe));
            result[side*4+2]=(byte)((native[side*4+2]&0x7f)|(encoded[2]&0x80)); result[side*4+3]=encoded[3];
        }
        return result;
    }
    public static byte[] Encode(double lowHz, double lowAmplitude, double highHz, double highAmplitude)
    {
        int hc = Math.Clamp((int)Math.Round(Math.Log2(Math.Clamp(highHz,82,1253)/10)*32)-0x60,0,127)<<2;
        int lc = Math.Clamp((int)Math.Round(Math.Log2(Math.Clamp(lowHz,41,626)/10)*32)-0x40,0,127);
        int ha = AmplitudeCode(highAmplitude), la = AmplitudeCode(lowAmplitude);
        return [(byte)hc,(byte)((hc>>8)|(ha<<1)),(byte)(lc|((la&1)<<7)),(byte)(0x40+(la>>1))];
    }
    private static int AmplitudeCode(double amplitude)
    {
        double target = Math.Clamp(double.IsFinite(amplitude) ? amplitude : 0,0,1)*29000;
        int best = 0; double error = double.MaxValue;
        for (int i = 0; i <= 100; i++)
        {
            var value = Switch1RumbleConverter.Decode([0,(byte)(i<<1),0,0x40]).HighAmplitude;
            double delta = Math.Abs(value-target); if (delta < error) { best = i; error = delta; }
        }
        return best;
    }
}

public static class Pro1NfcProtocol
{
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data) { crc ^= b; for (int i=0;i<8;i++) crc = (byte)((crc<<1)^((crc&0x80)!=0 ? 7 : 0)); }
        return crc;
    }
    public static byte[] Request(byte command, byte packetId = 0, byte[]? data = null)
    {
        data ??= []; if (data.Length > 31) throw new ArgumentException("NFC request too large");
        byte[] request = new byte[38]; request[0]=command; request[2]=packetId; request[3]=8; request[4]=(byte)data.Length;
        data.CopyTo(request,5); request[36]=Crc8(request.AsSpan(0,36)); return request;
    }
    public static bool TryGetMcu(ReadOnlySpan<byte> report, out byte type, out byte[] data)
    {
        type=0; data=[];
        if (report.Length < 72 || report[0] != 0x31) return false;
        type=report[49]; data=report.Slice(50,Math.Min(317,report.Length-50)).ToArray();
        // Transport lengths differ (BT 362, some libraries pad to 368).
        // Do not invent a CRC placement from the library's padded C structure.
        // Read data is checked by declared lengths and tag UID/BCC instead.
        return type is 0x01 or 0x2a or 0x3a;
    }
    public static bool TryGetTag(byte type, ReadOnlySpan<byte> data, out byte[] uid)
    {
        uid=[];
        if (type!=0x2a || data.Length<22 || data[0]!=0 || data[1]!=5 || data[6] is not (4 or 9) || data[14]!=7) return false;
        uid=data.Slice(15,7).ToArray(); return uid.Any(b=>b!=0);
    }
    public static bool IsNtag215(ReadOnlySpan<byte> dump, ReadOnlySpan<byte> uid)
    {
        if (dump.Length!=540 || uid.Length!=7 || dump[0]!=4) return false;
        return dump[3]==(byte)(0x88^dump[0]^dump[1]^dump[2]) && dump[8]==(byte)(dump[4]^dump[5]^dump[6]^dump[7]) &&
            dump[..3].SequenceEqual(uid[..3]) && dump.Slice(4,4).SequenceEqual(uid[3..]);
    }
}
