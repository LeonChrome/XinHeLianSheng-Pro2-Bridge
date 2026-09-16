#include "imu_converter.hpp"

#include <algorithm>
#include <cmath>

namespace imu {
namespace {

constexpr uint32_t kCalibrationSamples = 250;
constexpr float kStationaryAccelMinG = 0.85f;
constexpr float kStationaryAccelMaxG = 1.15f;
constexpr float kStationaryGyroMaxRaw = 256.0f;
constexpr float kMaximumAcceptedStdRaw = 3.0f;

struct AxisAccumulator {
    uint32_t count = 0;
    double mean = 0.0;
    double m2 = 0.0;

    void clear() {
        count = 0;
        mean = 0.0;
        m2 = 0.0;
    }

    void add(double value) {
        ++count;
        const double delta = value - mean;
        mean += delta / count;
        const double delta2 = value - mean;
        m2 += delta * delta2;
    }

    double stddev() const {
        return count > 1 ? std::sqrt(m2 / (count - 1)) : 0.0;
    }
};

struct CalibrationState {
    bool calibrated = false;
    float bias_x = 0.0f;
    float bias_y = 0.0f;
    float bias_z = 0.0f;
    AxisAccumulator x;
    AxisAccumulator y;
    AxisAccumulator z;
};

CalibrationState g_calibration;

int16_t clamp_round(float value) {
    const long rounded = std::lround(value);
    return static_cast<int16_t>(
        std::clamp<long>(rounded, INT16_MIN, INT16_MAX));
}

void clear_accumulators() {
    g_calibration.x.clear();
    g_calibration.y.clear();
    g_calibration.z.clear();
}

bool is_stationary(const pro2::InputState &input) {
    const float ax = input.accel_x / kPro2AccelRawPerG;
    const float ay = input.accel_y / kPro2AccelRawPerG;
    const float az = input.accel_z / kPro2AccelRawPerG;
    const float norm = std::sqrt(ax * ax + ay * ay + az * az);
    const float max_gyro = std::max(
        {std::fabs(static_cast<float>(input.gyro_x)),
         std::fabs(static_cast<float>(input.gyro_y)),
         std::fabs(static_cast<float>(input.gyro_z))});
    return norm >= kStationaryAccelMinG && norm <= kStationaryAccelMaxG &&
           max_gyro <= kStationaryGyroMaxRaw;
}

float corrected(int16_t value, float bias) {
    return static_cast<float>(value) - bias;
}

} // namespace

void reset_bias() {
    g_calibration = {};
}

void observe(const pro2::InputState &input) {
    if (g_calibration.calibrated) {
        return;
    }
    if (!is_stationary(input)) {
        clear_accumulators();
        return;
    }

    g_calibration.x.add(input.gyro_x);
    g_calibration.y.add(input.gyro_y);
    g_calibration.z.add(input.gyro_z);
    if (g_calibration.x.count < kCalibrationSamples) {
        return;
    }

    if (g_calibration.x.stddev() <= kMaximumAcceptedStdRaw &&
        g_calibration.y.stddev() <= kMaximumAcceptedStdRaw &&
        g_calibration.z.stddev() <= kMaximumAcceptedStdRaw) {
        g_calibration.bias_x = static_cast<float>(g_calibration.x.mean);
        g_calibration.bias_y = static_cast<float>(g_calibration.y.mean);
        g_calibration.bias_z = static_cast<float>(g_calibration.z.mean);
        g_calibration.calibrated = true;
        return;
    }
    clear_accumulators();
}

BiasStatus bias_status() {
    return {
        g_calibration.calibrated,
        g_calibration.x.count,
        g_calibration.bias_x,
        g_calibration.bias_y,
        g_calibration.bias_z,
    };
}

RawAxes to_dualsense(const pro2::InputState &input) {
    const float gx =
        corrected(input.gyro_x, g_calibration.bias_x) / kPro2GyroRawPerDps;
    const float gy =
        corrected(input.gyro_y, g_calibration.bias_y) / kPro2GyroRawPerDps;
    const float gz =
        corrected(input.gyro_z, g_calibration.bias_z) / kPro2GyroRawPerDps;
    const float ax = input.accel_x / kPro2AccelRawPerG;
    const float ay = input.accel_y / kPro2AccelRawPerG;
    const float az = input.accel_z / kPro2AccelRawPerG;

    RawAxes result;
    result.gyro_x = clamp_round(+gx * kDualSenseGyroRawPerDps);
    result.gyro_y = clamp_round(+gz * kDualSenseGyroRawPerDps);
    result.gyro_z = clamp_round(-gy * kDualSenseGyroRawPerDps);
    result.accel_x = clamp_round(+ax * kDualSenseAccelRawPerG);
    result.accel_y = clamp_round(+az * kDualSenseAccelRawPerG);
    result.accel_z = clamp_round(-ay * kDualSenseAccelRawPerG);
    return result;
}

RawAxes to_switch1(const pro2::InputState &input) {
    RawAxes result;
    result.gyro_x = clamp_round(
        corrected(input.gyro_x, g_calibration.bias_x) /
        kPro2GyroRawPerDps * kSwitch1GyroRawPerDps);
    result.gyro_y = clamp_round(
        corrected(input.gyro_y, g_calibration.bias_y) /
        kPro2GyroRawPerDps * kSwitch1GyroRawPerDps);
    result.gyro_z = clamp_round(
        corrected(input.gyro_z, g_calibration.bias_z) /
        kPro2GyroRawPerDps * kSwitch1GyroRawPerDps);
    result.accel_x = clamp_round(
        input.accel_x / kPro2AccelRawPerG * kSwitch1AccelRawPerG);
    result.accel_y = clamp_round(
        input.accel_y / kPro2AccelRawPerG * kSwitch1AccelRawPerG);
    result.accel_z = clamp_round(
        input.accel_z / kPro2AccelRawPerG * kSwitch1AccelRawPerG);
    return result;
}

bool self_test() {
    reset_bias();
    pro2::InputState input;
    input.accel_x = 4096;
    input.accel_y = -2048;
    input.accel_z = 1024;
    input.gyro_x = 14247 / 10;
    input.gyro_y = -(14247 / 20);
    input.gyro_z = 14247 / 5;

    const RawAxes ds = to_dualsense(input);
    const RawAxes ns = to_switch1(input);
    const bool ds_ok =
        std::abs(ds.accel_x - 8192) <= 1 &&
        std::abs(ds.accel_y - 2048) <= 1 &&
        std::abs(ds.accel_z - 4096) <= 1 &&
        std::abs(ds.gyro_x - 1638) <= 2 &&
        std::abs(ds.gyro_y - 3277) <= 2 &&
        std::abs(ds.gyro_z - 819) <= 2;
    const bool ns_ok =
        ns.accel_x == 4096 && ns.accel_y == -2048 && ns.accel_z == 1024 &&
        std::abs(ns.gyro_x - 1640) <= 2 &&
        std::abs(ns.gyro_y + 820) <= 2 &&
        std::abs(ns.gyro_z - 3280) <= 2;
    reset_bias();
    return ds_ok && ns_ok;
}

} // namespace imu
