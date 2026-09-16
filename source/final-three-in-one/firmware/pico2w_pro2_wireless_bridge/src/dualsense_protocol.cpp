#include "dualsense_protocol.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>

#include "imu_converter.hpp"

namespace dualsense {
namespace {

constexpr uint8_t kReportDescriptor[] = {
    0x05, 0x01, 0x09, 0x05, 0xa1, 0x01, 0x85, 0x01,
    0x09, 0x30, 0x09, 0x31, 0x09, 0x32, 0x09, 0x35,
    0x15, 0x00, 0x26, 0xff, 0x00, 0x75, 0x08, 0x95,
    0x04, 0x81, 0x02, 0x09, 0x39, 0x15, 0x00, 0x25,
    0x07, 0x35, 0x00, 0x46, 0x3b, 0x01, 0x65, 0x14,
    0x75, 0x04, 0x95, 0x01, 0x81, 0x42, 0x65, 0x00,
    0x05, 0x09, 0x19, 0x01, 0x29, 0x0e, 0x15, 0x00,
    0x25, 0x01, 0x75, 0x01, 0x95, 0x0e, 0x81, 0x02,
    0x75, 0x06, 0x95, 0x01, 0x81, 0x01, 0x05, 0x01,
    0x09, 0x33, 0x09, 0x34, 0x15, 0x00, 0x26, 0xff,
    0x00, 0x75, 0x08, 0x95, 0x02, 0x81, 0x02, 0x06,
    0x00, 0xff, 0x15, 0x00, 0x26, 0xff, 0x00, 0x75,
    0x08, 0x95, 0x4d, 0x85, 0x31, 0x09, 0x31, 0x91,
    0x02, 0x09, 0x3b, 0x81, 0x02, 0x85, 0x32, 0x09,
    0x32, 0x95, 0x8d, 0x91, 0x02, 0x85, 0x33, 0x09,
    0x33, 0x95, 0xcd, 0x91, 0x02, 0x85, 0x34, 0x09,
    0x34, 0x96, 0x0d, 0x01, 0x91, 0x02, 0x85, 0x35,
    0x09, 0x35, 0x96, 0x4d, 0x01, 0x91, 0x02, 0x85,
    0x36, 0x09, 0x36, 0x96, 0x8d, 0x01, 0x91, 0x02,
    0x85, 0x37, 0x09, 0x37, 0x96, 0xcd, 0x01, 0x91,
    0x02, 0x85, 0x38, 0x09, 0x38, 0x96, 0x0d, 0x02,
    0x91, 0x02, 0x85, 0x39, 0x09, 0x39, 0x96, 0x22,
    0x02, 0x91, 0x02, 0x06, 0x80, 0xff, 0x85, 0x05,
    0x09, 0x33, 0x95, 0x28, 0xb1, 0x02, 0x85, 0x08,
    0x09, 0x34, 0x95, 0x2f, 0xb1, 0x02, 0x85, 0x09,
    0x09, 0x24, 0x95, 0x13, 0xb1, 0x02, 0x85, 0x20,
    0x09, 0x26, 0x95, 0x3f, 0xb1, 0x02, 0x85, 0x22,
    0x09, 0x40, 0x95, 0x3f, 0xb1, 0x02, 0x85, 0x80,
    0x09, 0x28, 0x95, 0x3f, 0xb1, 0x02, 0x85, 0x81,
    0x09, 0x29, 0x95, 0x3f, 0xb1, 0x02, 0x85, 0x82,
    0x09, 0x2a, 0x95, 0x09, 0xb1, 0x02, 0x85, 0x83,
    0x09, 0x2b, 0x95, 0x3f, 0xb1, 0x02, 0x85, 0xf1,
    0x09, 0x31, 0x95, 0x3f, 0xb1, 0x02, 0x85, 0xf2,
    0x09, 0x32, 0x95, 0x0f, 0xb1, 0x02, 0x85, 0xf0,
    0x09, 0x30, 0x95, 0x3f, 0xb1, 0x02, 0xc0,
};

// Sony's raw Bluetooth dump is commonly published as 280 bytes including a
// trailing zero pad. SDP carries only the 279-byte HID descriptor: exposing
// the pad makes Windows hidbth parse it as a reserved Main item and fail with
// CM_PROB_FAILED_START.
static_assert(sizeof(kReportDescriptor) == 279);

void write_u16(uint8_t *target, int16_t value) {
    target[0] = static_cast<uint8_t>(value);
    target[1] = static_cast<uint8_t>(
        static_cast<uint16_t>(value) >> 8);
}

void write_u32(uint8_t *target, uint32_t value) {
    target[0] = static_cast<uint8_t>(value);
    target[1] = static_cast<uint8_t>(value >> 8);
    target[2] = static_cast<uint8_t>(value >> 16);
    target[3] = static_cast<uint8_t>(value >> 24);
}

uint32_t crc32_update(uint32_t crc, const uint8_t *data, size_t size) {
    for (size_t index = 0; index < size; ++index) {
        crc ^= data[index];
        for (int bit = 0; bit < 8; ++bit) {
            crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320u : 0u);
        }
    }
    return crc;
}

void finish_crc(uint8_t seed, uint8_t report_id, uint8_t *body,
                size_t body_size) {
    if (body == nullptr || body_size < 4) {
        return;
    }
    uint32_t crc = crc32_update(0xffffffffu, &seed, 1);
    crc = crc32_update(crc, &report_id, 1);
    crc = crc32_update(crc, body, body_size - 4);
    write_u32(body + body_size - 4, ~crc);
}

bool valid_output_crc(const uint8_t *body, size_t body_size) {
    if (body == nullptr || body_size != 77) {
        return false;
    }
    const uint8_t seed = 0xa2;
    uint32_t crc = crc32_update(0xffffffffu, &seed, 1);
    crc = crc32_update(crc, &kOutputReportId, 1);
    crc = crc32_update(crc, body, body_size - 4);
    const uint32_t expected = ~crc;
    const uint8_t *stored = body + body_size - 4;
    const uint32_t actual = static_cast<uint32_t>(stored[0]) |
                            (static_cast<uint32_t>(stored[1]) << 8) |
                            (static_cast<uint32_t>(stored[2]) << 16) |
                            (static_cast<uint32_t>(stored[3]) << 24);
    return actual == expected;
}

uint8_t map_stick(uint16_t value, bool invert) {
    float normalized = pro2::normalize_stick(value);
    if (invert) {
        normalized = -normalized;
    }
    return static_cast<uint8_t>(
        std::clamp<long>(std::lround((normalized + 1.0f) * 127.5f),
                         0, 255));
}

uint8_t make_hat(uint32_t buttons) {
    const bool up = (buttons & pro2::ButtonUp) != 0;
    const bool down = (buttons & pro2::ButtonDown) != 0;
    const bool left = (buttons & pro2::ButtonLeft) != 0;
    const bool right = (buttons & pro2::ButtonRight) != 0;
    if (up && right) return 1;
    if (right && down) return 3;
    if (down && left) return 5;
    if (left && up) return 7;
    if (up) return 0;
    if (right) return 2;
    if (down) return 4;
    if (left) return 6;
    return 8;
}

void set_if(uint8_t &byte, uint8_t mask, bool value) {
    if (value) {
        byte |= mask;
    }
}

} // namespace

const uint8_t *report_descriptor() {
    return kReportDescriptor;
}

size_t report_descriptor_size() {
    return sizeof(kReportDescriptor);
}

InputReport make_input_report(const pro2::InputState &input, bool edge,
                              uint8_t sequence) {
    InputReport report{};
    report[0] = kInputReportId;
    report[1] = 0x01;
    uint8_t *common = report.data() + 2;
    common[0] = map_stick(input.left_x, false);
    common[1] = map_stick(input.left_y, true);
    common[2] = map_stick(input.right_x, false);
    common[3] = map_stick(input.right_y, true);
    common[4] = (input.buttons & pro2::ButtonZL) != 0 ? 0xff : 0x00;
    common[5] = (input.buttons & pro2::ButtonZR) != 0 ? 0xff : 0x00;
    common[6] = sequence;
    common[7] = make_hat(input.buttons);
    set_if(common[7], 0x10, (input.buttons & pro2::ButtonY) != 0);
    set_if(common[7], 0x20, (input.buttons & pro2::ButtonB) != 0);
    set_if(common[7], 0x40, (input.buttons & pro2::ButtonA) != 0);
    set_if(common[7], 0x80, (input.buttons & pro2::ButtonX) != 0);
    set_if(common[8], 0x01, (input.buttons & pro2::ButtonL) != 0);
    set_if(common[8], 0x02, (input.buttons & pro2::ButtonR) != 0);
    set_if(common[8], 0x04, (input.buttons & pro2::ButtonZL) != 0);
    set_if(common[8], 0x08, (input.buttons & pro2::ButtonZR) != 0);
    set_if(common[8], 0x10, (input.buttons & pro2::ButtonMinus) != 0);
    set_if(common[8], 0x20, (input.buttons & pro2::ButtonPlus) != 0);
    set_if(common[8], 0x40, (input.buttons & pro2::ButtonLeftStick) != 0);
    set_if(common[8], 0x80, (input.buttons & pro2::ButtonRightStick) != 0);
    set_if(common[9], 0x01, (input.buttons & pro2::ButtonHome) != 0);
    set_if(common[9], 0x02, (input.buttons & pro2::ButtonCapture) != 0);
    set_if(common[9], 0x04, (input.buttons & pro2::ButtonHeadset) != 0);
    if (edge) {
        set_if(common[9], 0x40, (input.buttons & pro2::ButtonGL) != 0);
        set_if(common[9], 0x80, (input.buttons & pro2::ButtonGR) != 0);
    }

    const imu::RawAxes axes = imu::to_dualsense(input);
    write_u16(common + 15, axes.gyro_x);
    write_u16(common + 17, axes.gyro_y);
    write_u16(common + 19, axes.gyro_z);
    write_u16(common + 21, axes.accel_x);
    write_u16(common + 23, axes.accel_y);
    write_u16(common + 25, axes.accel_z);
    write_u32(common + 27,
              static_cast<uint32_t>(input.received_at_us * 3u));
    common[32] = 0x80;
    common[36] = 0x80;
    common[52] = 0x0b;
    common[53] = 0x00;
    common[54] = 0x00;

    uint8_t seed = 0xa1;
    uint32_t crc = crc32_update(0xffffffffu, &seed, 1);
    crc = crc32_update(crc, report.data(), report.size() - 4);
    write_u32(report.data() + report.size() - 4, ~crc);
    return report;
}

InputReport make_neutral_report(bool edge) {
    pro2::InputState neutral;
    neutral.accel_y = 4096;
    return make_input_report(neutral, edge, 0);
}

size_t make_feature_report(uint8_t report_id,
                           const std::array<uint8_t, 6> &address, bool edge,
                           uint8_t *body, size_t body_capacity) {
    size_t size = 0;
    switch (report_id) {
    case 0x05:
        size = 40;
        break;
    case 0x09:
        size = 19;
        break;
    case 0x20:
        size = 63;
        break;
    case 0x08:
        size = 47;
        break;
    case 0x82:
        size = 9;
        break;
    case 0xf2:
        size = 15;
        break;
    default:
        size = 63;
        break;
    }
    if (body == nullptr || body_capacity < size) {
        return 0;
    }
    std::memset(body, 0, size);

    if (report_id == 0x05) {
        write_u16(body + 0, 0);
        write_u16(body + 2, 0);
        write_u16(body + 4, 0);
        write_u16(body + 6, 16384);
        write_u16(body + 8, -16384);
        write_u16(body + 10, 16384);
        write_u16(body + 12, -16384);
        write_u16(body + 14, 16384);
        write_u16(body + 16, -16384);
        write_u16(body + 18, 1000);
        write_u16(body + 20, 1000);
        write_u16(body + 22, 8192);
        write_u16(body + 24, -8192);
        write_u16(body + 26, 8192);
        write_u16(body + 28, -8192);
        write_u16(body + 30, 8192);
        write_u16(body + 32, -8192);
    } else if (report_id == 0x09) {
        std::copy(address.begin(), address.end(), body);
    } else if (report_id == 0x20) {
        static constexpr uint8_t kBuildDate[] = {
            '2', '0', '2', '6', '-', '0', '7', '-', '3', '0', 0, 0};
        std::copy(std::begin(kBuildDate), std::end(kBuildDate), body);
        write_u32(body + 23, edge ? 0x00000df2u : 0x00000ce6u);
        write_u32(body + 27, 0x00010700u);
        write_u16(body + 43, edge ? 0x0300 : 0x0215);
    }
    finish_crc(0xa3, report_id, body, size);
    return size;
}

OutputReport decode_output_report(const uint8_t *body, size_t body_size) {
    OutputReport result;
    if (body == nullptr || body_size != 77 || body[1] != 0x10) {
        return result;
    }

    result.crc_valid = valid_output_crc(body, body_size);
    if (!result.crc_valid) {
        return result;
    }

    const bool select_haptics = (body[2] & 0x02) != 0;
    const bool compatible_v1 = (body[2] & 0x01) != 0;
    result.compatible_v2 = (body[40] & 0x04) != 0;
    if (!select_haptics || (!compatible_v1 && !result.compatible_v2)) {
        result.action = OutputAction::NoMotorUpdate;
        return result;
    }

    result.action = OutputAction::MotorUpdate;
    result.rumble.enabled_mask = 0x0c;
    result.rumble.weak = body[4];
    result.rumble.strong = body[5];
    return result;
}

MotorTransition resolve_motor_transition(const OutputReport &report,
                                         bool compatible_motor_active) {
    switch (report.action) {
    case OutputAction::Invalid:
        return MotorTransition::Ignore;
    case OutputAction::MotorUpdate:
        return MotorTransition::ApplyUpdate;
    case OutputAction::NoMotorUpdate:
        // SDL removes the compatible-rumble enable bits when a zero rumble
        // request returns the controller to audio haptics. Treat that valid
        // transition as a release only while ordinary motors are active;
        // otherwise an LED-only report remains a no-op.
        return compatible_motor_active
                   ? MotorTransition::ReleaseCompatibleMotor
                   : MotorTransition::Ignore;
    }
    return MotorTransition::Ignore;
}

const char *output_action_name(OutputAction action) {
    switch (action) {
    case OutputAction::Invalid:
        return "invalid";
    case OutputAction::NoMotorUpdate:
        return "no_motor_update";
    case OutputAction::MotorUpdate:
        return "motor_update";
    }
    return "unknown";
}

const char *motor_transition_name(MotorTransition transition) {
    switch (transition) {
    case MotorTransition::Ignore:
        return "ignore";
    case MotorTransition::ApplyUpdate:
        return "apply_update";
    case MotorTransition::ReleaseCompatibleMotor:
        return "release_compatible_motor";
    }
    return "unknown";
}

bool self_test() {
    pro2::InputState input;
    input.left_x = pro2::kStickMaximum;
    input.left_y = 0;
    input.right_x = 0;
    input.right_y = pro2::kStickMaximum;
    input.buttons = pro2::ButtonB | pro2::ButtonA | pro2::ButtonUp |
                    pro2::ButtonRight | pro2::ButtonGL | pro2::ButtonGR;
    input.accel_y = 4096;
    input.gyro_x = 14247 / 10;
    const InputReport report = make_input_report(input, true, 7);
    const InputReport neutral = make_neutral_report(false);
    std::array<uint8_t, 64> feature{};
    const std::array<uint8_t, 6> address = {1, 2, 3, 4, 5, 6};
    const size_t calibration_size =
        make_feature_report(0x05, address, false, feature.data(),
                            feature.size());
    std::array<uint8_t, 77> output{};
    output[1] = 0x10;
    output[2] = 0x03;
    output[4] = 33;
    output[5] = 77;
    finish_crc(0xa2, kOutputReportId, output.data(), output.size());
    const auto decoded = decode_output_report(output.data(), output.size());
    auto release = output;
    release[2] = 0;
    finish_crc(0xa2, kOutputReportId, release.data(), release.size());
    const auto release_decoded =
        decode_output_report(release.data(), release.size());
    return report_descriptor_size() == 279 &&
           kReportDescriptor[sizeof(kReportDescriptor) - 1] == 0xc0 &&
           report[0] == kInputReportId && report[2] == 0xff &&
           report[3] == 0xff && report[4] == 0x00 &&
           report[5] == 0x00 && (report[9] & 0x20) != 0 &&
           (report[9] & 0x40) != 0 && (report[11] & 0xc0) == 0xc0 &&
           neutral[2] == 0x80 && neutral[3] == 0x80 &&
           neutral[4] == 0x80 && neutral[5] == 0x80 &&
           calibration_size == 40 && feature[6] == 0x00 &&
           feature[7] == 0x40 &&
           decoded.action == OutputAction::MotorUpdate &&
           decoded.rumble.enabled_mask == 0x0c &&
           decoded.rumble.weak == 33 && decoded.rumble.strong == 77 &&
           resolve_motor_transition(decoded, false) ==
               MotorTransition::ApplyUpdate &&
           resolve_motor_transition(release_decoded, true) ==
               MotorTransition::ReleaseCompatibleMotor &&
           resolve_motor_transition(release_decoded, false) ==
               MotorTransition::Ignore;
}

} // namespace dualsense
