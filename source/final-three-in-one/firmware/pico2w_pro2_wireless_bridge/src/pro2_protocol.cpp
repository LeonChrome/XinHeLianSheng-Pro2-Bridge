#include "pro2_protocol.hpp"

#include <algorithm>
#include <cmath>

namespace pro2 {
namespace {

constexpr uint8_t kInitFeatureInfo[] = {0x07, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00};
constexpr uint8_t kInitFeatureMask[] = {
    0x0c, 0x91, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0x27, 0x00, 0x00, 0x00};
constexpr uint8_t kInitFeatureInfo2[] = {0x11, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00};
constexpr uint8_t kInitProfile[] = {
    0x0a, 0x91, 0x00, 0x08, 0x00, 0x14, 0x00, 0x00, 0x01, 0xff,
    0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x35, 0x00, 0x46,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
constexpr uint8_t kInitFeatureEnable[] = {
    0x0c, 0x91, 0x00, 0x04, 0x00, 0x04, 0x00, 0x00, 0x27, 0x00, 0x00, 0x00};
constexpr uint8_t kInitUsbState[] = {0x01, 0x91, 0x00, 0x0c, 0x00, 0x00, 0x00, 0x00};
constexpr uint8_t kInitFeatureInfo3[] = {0x01, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00};
constexpr uint8_t kInitFeatureButtons[] = {
    0x08, 0x91, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00};
constexpr uint8_t kSelectCommonReport[] = {
    0x03, 0x91, 0x00, 0x0a, 0x00, 0x04, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00};
constexpr uint8_t kStartReports[] = {
    0x03, 0x91, 0x00, 0x0d, 0x00, 0x08, 0x00, 0x00,
    0x01, 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff};

constexpr std::array<InitCommand, 10> kInitializationCommands{{
    {kInitFeatureInfo, sizeof(kInitFeatureInfo), "feature_info_1"},
    {kInitFeatureMask, sizeof(kInitFeatureMask), "feature_mask"},
    {kInitFeatureInfo2, sizeof(kInitFeatureInfo2), "feature_info_2"},
    {kInitProfile, sizeof(kInitProfile), "profile"},
    {kInitFeatureEnable, sizeof(kInitFeatureEnable), "feature_enable"},
    {kInitUsbState, sizeof(kInitUsbState), "usb_state"},
    {kInitFeatureInfo3, sizeof(kInitFeatureInfo3), "feature_info_3"},
    {kInitFeatureButtons, sizeof(kInitFeatureButtons), "feature_buttons"},
    {kSelectCommonReport, sizeof(kSelectCommonReport), "select_report_05"},
    {kStartReports, sizeof(kStartReports), "start_reports"},
}};

uint16_t read_u16_le(const uint8_t *data) {
    return static_cast<uint16_t>(data[0]) |
           static_cast<uint16_t>(static_cast<uint16_t>(data[1]) << 8);
}

uint32_t read_u32_le(const uint8_t *data) {
    return static_cast<uint32_t>(data[0]) |
           (static_cast<uint32_t>(data[1]) << 8) |
           (static_cast<uint32_t>(data[2]) << 16) |
           (static_cast<uint32_t>(data[3]) << 24);
}

uint16_t unpack_stick_x(const uint8_t *data) {
    return static_cast<uint16_t>(data[0]) |
           static_cast<uint16_t>((data[1] & 0x0f) << 8);
}

uint16_t unpack_stick_y(const uint8_t *data) {
    return static_cast<uint16_t>((data[1] >> 4) & 0x0f) |
           static_cast<uint16_t>(data[2] << 4);
}

} // namespace

const std::array<InitCommand, 10> &initialization_commands() {
    return kInitializationCommands;
}

bool parse_common_input_report(const uint8_t *report, size_t size, uint64_t received_at_us,
                               InputState *state) {
    if (report == nullptr || state == nullptr || size < kInputReportSize ||
        report[0] != kCommonReportId) {
        return false;
    }

    InputState parsed;
    parsed.sequence = read_u32_le(report + 1);

    if (report[5] & 0x01) parsed.buttons |= ButtonY;
    if (report[5] & 0x02) parsed.buttons |= ButtonX;
    if (report[5] & 0x04) parsed.buttons |= ButtonB;
    if (report[5] & 0x08) parsed.buttons |= ButtonA;
    if (report[5] & 0x40) parsed.buttons |= ButtonR;
    if (report[5] & 0x80) parsed.buttons |= ButtonZR;

    if (report[6] & 0x01) parsed.buttons |= ButtonMinus;
    if (report[6] & 0x02) parsed.buttons |= ButtonPlus;
    if (report[6] & 0x04) parsed.buttons |= ButtonRightStick;
    if (report[6] & 0x08) parsed.buttons |= ButtonLeftStick;
    if (report[6] & 0x10) parsed.buttons |= ButtonHome;
    if (report[6] & 0x20) parsed.buttons |= ButtonCapture;
    if (report[6] & 0x40) parsed.buttons |= ButtonC;

    if (report[7] & 0x01) parsed.buttons |= ButtonDown;
    if (report[7] & 0x02) parsed.buttons |= ButtonUp;
    if (report[7] & 0x04) parsed.buttons |= ButtonRight;
    if (report[7] & 0x08) parsed.buttons |= ButtonLeft;
    if (report[7] & 0x40) parsed.buttons |= ButtonL;
    if (report[7] & 0x80) parsed.buttons |= ButtonZL;

    if (report[8] & 0x01) parsed.buttons |= ButtonGR;
    if (report[8] & 0x02) parsed.buttons |= ButtonGL;
    if (report[8] & 0x10) parsed.buttons |= ButtonHeadset;

    parsed.left_x = unpack_stick_x(report + 11);
    parsed.left_y = unpack_stick_y(report + 11);
    parsed.right_x = unpack_stick_x(report + 14);
    parsed.right_y = unpack_stick_y(report + 14);
    parsed.accel_x = static_cast<int16_t>(read_u16_le(report + 0x31));
    parsed.accel_y = static_cast<int16_t>(read_u16_le(report + 0x33));
    parsed.accel_z = static_cast<int16_t>(read_u16_le(report + 0x35));
    parsed.gyro_x = static_cast<int16_t>(read_u16_le(report + 0x37));
    parsed.gyro_y = static_cast<int16_t>(read_u16_le(report + 0x39));
    parsed.gyro_z = static_cast<int16_t>(read_u16_le(report + 0x3b));
    parsed.received_at_us = received_at_us;
    *state = parsed;
    return true;
}

float normalize_stick(uint16_t value) {
    const int centered =
        static_cast<int>(std::min(value, kStickMaximum)) -
        static_cast<int>(kStickCenter);
    const int magnitude = centered < 0 ? -centered : centered;
    if (magnitude <= static_cast<int>(kStickDeadzone)) {
        return 0.0f;
    }

    const float usable = static_cast<float>(
        kStickFullScaleRange - kStickDeadzone);
    const float normalized = static_cast<float>(
        magnitude - kStickDeadzone) / usable;
    return std::clamp(centered < 0 ? -normalized : normalized,
                      -1.0f, 1.0f);
}

uint16_t normalize_stick_to_raw12(uint16_t value) {
    const float normalized = normalize_stick(value);
    if (normalized < 0.0f) {
        return static_cast<uint16_t>(std::clamp<long>(
            std::lround(static_cast<float>(kStickCenter) *
                        (normalized + 1.0f)),
            0, kStickCenter));
    }
    return static_cast<uint16_t>(std::clamp<long>(
        std::lround(static_cast<float>(kStickCenter) +
                    normalized * static_cast<float>(
                        kStickMaximum - kStickCenter)),
        kStickCenter, kStickMaximum));
}

bool self_test() {
    std::array<uint8_t, kInputReportSize> report{};
    report[0] = kCommonReportId;
    report[1] = 0x78;
    report[2] = 0x56;
    report[3] = 0x34;
    report[4] = 0x12;
    report[5] = 0x8d;
    report[6] = 0x3f;
    report[7] = 0xcf;
    report[8] = 0x03;

    report[11] = 0x23;
    report[12] = 0x61;
    report[13] = 0x45;
    report[14] = 0x89;
    report[15] = 0xc7;
    report[16] = 0xab;

    const auto write_i16 = [&](size_t offset, int16_t value) {
        const uint16_t raw = static_cast<uint16_t>(value);
        report[offset] = static_cast<uint8_t>(raw);
        report[offset + 1] = static_cast<uint8_t>(raw >> 8);
    };
    write_i16(0x31, 0x1122);
    write_i16(0x33, -0x1234);
    write_i16(0x35, 0x3344);
    write_i16(0x37, -0x0102);
    write_i16(0x39, 0x5566);
    write_i16(0x3b, -0x0777);

    InputState state;
    if (!parse_common_input_report(report.data(), report.size(), 987654, &state)) {
        return false;
    }
    const uint32_t expected_buttons =
        ButtonY | ButtonB | ButtonA | ButtonZR | ButtonMinus | ButtonPlus |
        ButtonRightStick | ButtonLeftStick | ButtonHome | ButtonCapture | ButtonDown |
        ButtonUp | ButtonRight | ButtonLeft | ButtonL | ButtonZL | ButtonGR | ButtonGL;
    return state.sequence == 0x12345678 && state.buttons == expected_buttons &&
           state.left_x == 0x123 && state.left_y == 0x456 && state.right_x == 0x789 &&
           state.right_y == 0xabc && state.accel_x == 0x1122 && state.accel_y == -0x1234 &&
           state.accel_z == 0x3344 && state.gyro_x == -0x0102 && state.gyro_y == 0x5566 &&
           state.gyro_z == -0x0777 && state.received_at_us == 987654 &&
           normalize_stick(0) == -1.0f &&
           normalize_stick(kStickCenter - kStickDeadzone) == 0.0f &&
           normalize_stick(kStickCenter) == 0.0f &&
           normalize_stick(kStickCenter + kStickDeadzone) == 0.0f &&
           normalize_stick(kStickCenter - kStickFullScaleRange) == -1.0f &&
           normalize_stick(kStickCenter + kStickFullScaleRange) == 1.0f &&
           normalize_stick(kStickMaximum) == 1.0f &&
           normalize_stick_to_raw12(kStickCenter) == kStickCenter &&
           normalize_stick_to_raw12(
               kStickCenter - kStickFullScaleRange) == 0 &&
           normalize_stick_to_raw12(
               kStickCenter + kStickFullScaleRange) == kStickMaximum;
}

} // namespace pro2
