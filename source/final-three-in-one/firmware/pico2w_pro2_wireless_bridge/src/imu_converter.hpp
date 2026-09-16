#pragma once

#include <cstdint>

#include "pro2_protocol.hpp"

namespace imu {

constexpr float kPro2AccelRawPerG = 4096.0f;
constexpr float kPro2GyroRawPerDps = 14.247f;
constexpr float kDualSenseAccelRawPerG = 8192.0f;
constexpr float kDualSenseGyroRawPerDps = 16.384f;
constexpr float kSwitch1AccelRawPerG = 4096.0f;
constexpr float kSwitch1GyroRawPerDps = 16.4f;

struct RawAxes {
    int16_t accel_x = 0;
    int16_t accel_y = 0;
    int16_t accel_z = 0;
    int16_t gyro_x = 0;
    int16_t gyro_y = 0;
    int16_t gyro_z = 0;
};

struct BiasStatus {
    bool calibrated = false;
    uint32_t samples = 0;
    float gyro_x = 0.0f;
    float gyro_y = 0.0f;
    float gyro_z = 0.0f;
};

void reset_bias();
void observe(const pro2::InputState &input);
BiasStatus bias_status();
RawAxes to_dualsense(const pro2::InputState &input);
RawAxes to_switch1(const pro2::InputState &input);
bool self_test();

} // namespace imu
