using System;
using System.Diagnostics;
using System.Globalization;

namespace Y700Switch2V60Viiper;

// Independent implementation of time-based stillness/bias learning. Design
// reference: JibbSmart/GamepadMotionHelpers (MIT), not a source-code port.
// Never fuse gravity into raw gyro or apply a gameplay deadzone.
public sealed class Pro2GyroBiasCalibration
{
    private const double MaximumStationaryRaw = 24; // about 1.46 dps, not 15.6 dps
    private const int MinimumSamples = 150;
    private int count;
    private long startTicks, lastTicks;
    private readonly double[] mean = new double[7], m2 = new double[7], min = new double[7], max = new double[7];
    private bool manual;
    private double biasX, biasY, biasZ;
    public bool Calibrated { get; private set; }
    public string LastResult { get; private set; } = "auto_waiting_stationary";
    public double BiasX => biasX;
    public double BiasY => biasY;
    public double BiasZ => biasZ;
    public double WindowSeconds => count == 0 ? 0 : (lastTicks - startTicks) / (double)Stopwatch.Frequency;
    public string Summary => "mode=quality status=" + (manual ? "manual_collecting" : Calibrated ? "calibrated" : "not_calibrated") +
        " samples=" + count + " elapsed_s=" + WindowSeconds.ToString("F2", CultureInfo.InvariantCulture) +
        " bias_raw=" + biasX.ToString("F3", CultureInfo.InvariantCulture) + "," +
        biasY.ToString("F3", CultureInfo.InvariantCulture) + "," + biasZ.ToString("F3", CultureInfo.InvariantCulture) +
        " result=" + LastResult + " automatic_bias_updates_after_calibration=0";

    public string StartManualCalibration()
    {
        manual = true;
        ResetWindow();
        LastResult = "manual_collecting_keep_controller_still";
        return "陀螺仪三秒校准已开始；请将手柄静置在稳定平面。移动会拒绝本次采样，保留旧校准。";
    }

    public void Apply(GamepadState state, long nowTicks)
    {
        if (!state.GyroValid || !state.AccelValid) return;
        double gx = state.GyroX, gy = state.GyroY, gz = state.GyroZ;
        if (manual || !Calibrated)
        {
            double norm = Math.Sqrt((double)state.AccelX * state.AccelX +
                (double)state.AccelY * state.AccelY + (double)state.AccelZ * state.AccelZ);
            bool idle = state.Buttons == GamepadButtons.None &&
                Math.Abs(state.Lx - GamepadState.AxisCenter) <= 128 && Math.Abs(state.Ly - GamepadState.AxisCenter) <= 128 &&
                Math.Abs(state.Rx - GamepadState.AxisCenter) <= 128 && Math.Abs(state.Ry - GamepadState.AxisCenter) <= 128;
            bool still = idle && norm is >= 3686 and <= 4506 &&
                Math.Abs(gx - biasX) <= MaximumStationaryRaw && Math.Abs(gy - biasY) <= MaximumStationaryRaw &&
                Math.Abs(gz - biasZ) <= MaximumStationaryRaw;
            if (!still)
                RejectWindow("motion");
            else if (count > 0 && nowTicks <= lastTicks)
            {
                // Duplicate/batched clock readings are not more stillness time.
            }
            else
            {
                if (count > 0 && (nowTicks - lastTicks) / (double)Stopwatch.Frequency > 0.1)
                    ResetWindow();
                if (count == 0) startTicks = nowTicks;
                lastTicks = nowTicks;
                count++;
                Span<double> values = stackalloc double[] { gx, gy, gz, state.AccelX, state.AccelY, state.AccelZ, norm };
                for (int i = 0; i < values.Length; i++)
                {
                    double value = values[i];
                    if (count == 1) min[i] = max[i] = value;
                    min[i] = Math.Min(min[i], value); max[i] = Math.Max(max[i], value);
                    double delta = value - mean[i]; mean[i] += delta / count;
                    m2[i] += delta * (value - mean[i]);
                }
                bool stable = true;
                for (int i = 0; i < 3; i++) stable &= max[i] - min[i] <= 12;
                for (int i = 3; i < 6; i++) stable &= max[i] - min[i] <= 48;
                if (!stable) RejectWindow("direction_or_noise");
                else if (count >= MinimumSamples && WindowSeconds >= (manual ? 3 : 2))
                {
                    bool lowVariance = Math.Sqrt(m2[6] / count) <= 8;
                    for (int i = 0; i < 3; i++) lowVariance &= Math.Sqrt(m2[i] / count) <= 2.5;
                    if (!lowVariance) RejectWindow("variance");
                    else
                    {
                        biasX = mean[0]; biasY = mean[1]; biasZ = mean[2];
                        Calibrated = true;
                        LastResult = manual ? "manual_committed_3s" : "auto_committed_2s";
                        manual = false;
                        ResetWindow();
                    }
                }
            }
        }
        if (Calibrated)
        {
            state.GyroX = Correct(gx - biasX); state.GyroY = Correct(gy - biasY); state.GyroZ = Correct(gz - biasZ);
        }
    }

    private void RejectWindow(string reason)
    {
        LastResult = (manual ? "manual_rejected_" : "auto_waiting_") + reason;
        manual = false;
        ResetWindow();
    }
    private void ResetWindow()
    {
        count = 0; startTicks = lastTicks = 0;
        Array.Clear(mean); Array.Clear(m2); Array.Clear(min); Array.Clear(max);
    }
    private static short Correct(double value) => (short)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
}
