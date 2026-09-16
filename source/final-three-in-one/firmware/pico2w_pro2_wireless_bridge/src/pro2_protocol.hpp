#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

namespace pro2 {

constexpr uint16_t kVendorId = 0x057e;
constexpr uint16_t kProductId = 0x2069;
constexpr uint8_t kHidInterface = 0;
constexpr uint8_t kVendorInterface = 1;
constexpr uint8_t kHidInEndpoint = 0x81;
constexpr uint8_t kHidOutEndpoint = 0x01;
constexpr uint8_t kBulkOutEndpoint = 0x02;
constexpr uint8_t kBulkInEndpoint = 0x82;
constexpr uint8_t kCommonReportId = 0x05;
constexpr size_t kInputReportSize = 64;
constexpr uint16_t kStickCenter = 0x0800;
constexpr uint16_t kStickMaximum = 0x0fff;
// Pro2's reported 12-bit ADC domain includes factory calibration headroom.
// The physical gate reaches roughly +/-1600 counts, matching the proven
// Windows bridge calibration. Treating all 2047/2048 counts as travel leaves
// every emulated controller unable to reach its advertised endpoint.
constexpr uint16_t kStickDeadzone = 64;
constexpr uint16_t kStickFullScaleRange = 1600;

enum Button : uint32_t {
    ButtonB = 1u << 0,
    ButtonA = 1u << 1,
    ButtonY = 1u << 2,
    ButtonX = 1u << 3,
    ButtonR = 1u << 4,
    ButtonZR = 1u << 5,
    ButtonPlus = 1u << 6,
    ButtonRightStick = 1u << 7,
    ButtonDown = 1u << 8,
    ButtonRight = 1u << 9,
    ButtonLeft = 1u << 10,
    ButtonUp = 1u << 11,
    ButtonL = 1u << 12,
    ButtonZL = 1u << 13,
    ButtonMinus = 1u << 14,
    ButtonLeftStick = 1u << 15,
    ButtonHome = 1u << 16,
    ButtonCapture = 1u << 17,
    ButtonGR = 1u << 18,
    ButtonGL = 1u << 19,
    ButtonC = 1u << 20,
    ButtonHeadset = 1u << 21,
};

struct InputState {
    uint32_t sequence = 0;
    uint32_t buttons = 0;
    uint16_t left_x = kStickCenter;
    uint16_t left_y = kStickCenter;
    uint16_t right_x = kStickCenter;
    uint16_t right_y = kStickCenter;
    int16_t accel_x = 0;
    int16_t accel_y = 0;
    int16_t accel_z = 0;
    int16_t gyro_x = 0;
    int16_t gyro_y = 0;
    int16_t gyro_z = 0;
    uint64_t received_at_us = 0;
};

struct InitCommand {
    const uint8_t *data;
    size_t size;
    const char *name;
};

const std::array<InitCommand, 10> &initialization_commands();
bool parse_common_input_report(const uint8_t *report, size_t size, uint64_t received_at_us,
                               InputState *state);
float normalize_stick(uint16_t value);
uint16_t normalize_stick_to_raw12(uint16_t value);
bool self_test();

} // namespace pro2
