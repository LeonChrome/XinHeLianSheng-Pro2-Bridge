#include "switch1_protocol.hpp"

#include <algorithm>
#include <array>
#include <cstring>

#include "imu_converter.hpp"

namespace switch1 {
namespace {

constexpr uint8_t kReportDescriptor[] = {
    0x05, 0x01, 0x09, 0x05, 0xa1, 0x01, 0x06, 0x01,
    0xff, 0x85, 0x21, 0x09, 0x21, 0x75, 0x08, 0x95,
    0x30, 0x81, 0x02, 0x85, 0x30, 0x09, 0x30, 0x75,
    0x08, 0x95, 0x30, 0x81, 0x02, 0x85, 0x31, 0x09,
    0x31, 0x75, 0x08, 0x96, 0x69, 0x01, 0x81, 0x02,
    0x85, 0x32, 0x09, 0x32, 0x75, 0x08, 0x96, 0x69,
    0x01, 0x81, 0x02, 0x85, 0x33, 0x09, 0x33, 0x75,
    0x08, 0x96, 0x69, 0x01, 0x81, 0x02, 0x85, 0x3f,
    0x05, 0x09, 0x19, 0x01, 0x29, 0x10, 0x15, 0x00,
    0x25, 0x01, 0x75, 0x01, 0x95, 0x10, 0x81, 0x02,
    0x05, 0x01, 0x09, 0x39, 0x15, 0x00, 0x25, 0x07,
    0x75, 0x04, 0x95, 0x01, 0x81, 0x42, 0x05, 0x09,
    0x75, 0x04, 0x95, 0x01, 0x81, 0x01, 0x05, 0x01,
    0x09, 0x30, 0x09, 0x31, 0x09, 0x33, 0x09, 0x34,
    0x16, 0x00, 0x00, 0x27, 0xff, 0xff, 0x00, 0x00,
    0x75, 0x10, 0x95, 0x04, 0x81, 0x02, 0x06, 0x01,
    0xff, 0x85, 0x01, 0x09, 0x01, 0x75, 0x08, 0x95,
    0x30, 0x91, 0x02, 0x85, 0x10, 0x09, 0x10, 0x75,
    0x08, 0x95, 0x09, 0x91, 0x02, 0x85, 0x11, 0x09,
    0x11, 0x75, 0x08, 0x95, 0x30, 0x91, 0x02, 0x85,
    0x12, 0x09, 0x12, 0x75, 0x08, 0x95, 0x30, 0x91,
    0x02, 0xc0,
};

constexpr std::array<uint16_t, 101> kRumbleAmplitudes = {
    0,   10,  12,  14,  17,  20,  24,  28,  33,  40,  47,
    56,  67,  80,  95,  112, 117, 123, 128, 134, 140, 146,
    152, 159, 166, 173, 181, 189, 198, 206, 215, 225, 230,
    235, 240, 245, 251, 256, 262, 268, 273, 279, 286, 292,
    298, 305, 311, 318, 325, 332, 340, 347, 355, 362, 370,
    378, 387, 395, 404, 413, 422, 431, 440, 450, 460, 470,
    480, 491, 501, 512, 524, 535, 547, 559, 571, 584, 596,
    609, 623, 636, 650, 665, 679, 694, 709, 725, 741, 757,
    773, 790, 808, 825, 843, 862, 881, 900, 920, 940, 960,
    981, 1003,
};

void write_u16(uint8_t *target, int16_t value) {
    target[0] = static_cast<uint8_t>(value);
    target[1] = static_cast<uint8_t>(
        static_cast<uint16_t>(value) >> 8);
}

void write_u16_unsigned(uint8_t *target, uint16_t value) {
    target[0] = static_cast<uint8_t>(value);
    target[1] = static_cast<uint8_t>(value >> 8);
}

void write_u32(uint8_t *target, uint32_t value) {
    target[0] = static_cast<uint8_t>(value);
    target[1] = static_cast<uint8_t>(value >> 8);
    target[2] = static_cast<uint8_t>(value >> 16);
    target[3] = static_cast<uint8_t>(value >> 24);
}

void pack_stick(uint16_t x, uint16_t y, uint8_t *target) {
    x = pro2::normalize_stick_to_raw12(x);
    y = pro2::normalize_stick_to_raw12(y);
    target[0] = static_cast<uint8_t>(x);
    target[1] = static_cast<uint8_t>((x >> 8) | (y << 4));
    target[2] = static_cast<uint8_t>(y >> 4);
}

void set_standard_input(const pro2::InputState &input, Report *report) {
    (*report)[2] =
        static_cast<uint8_t>((input.received_at_us / 5000u) & 0xffu);
    (*report)[3] = 0x80;
    uint8_t &right = (*report)[4];
    uint8_t &center = (*report)[5];
    uint8_t &left = (*report)[6];
    const uint32_t buttons = input.buttons;
    if (buttons & pro2::ButtonZR) right |= 0x80;
    if (buttons & pro2::ButtonR) right |= 0x40;
    if (buttons & pro2::ButtonA) right |= 0x08;
    if (buttons & pro2::ButtonB) right |= 0x04;
    if (buttons & pro2::ButtonX) right |= 0x02;
    if (buttons & pro2::ButtonY) right |= 0x01;
    if (buttons & pro2::ButtonCapture) center |= 0x20;
    if (buttons & pro2::ButtonHome) center |= 0x10;
    if (buttons & pro2::ButtonLeftStick) center |= 0x08;
    if (buttons & pro2::ButtonRightStick) center |= 0x04;
    if (buttons & pro2::ButtonPlus) center |= 0x02;
    if (buttons & pro2::ButtonMinus) center |= 0x01;
    if (buttons & pro2::ButtonZL) left |= 0x80;
    if (buttons & pro2::ButtonL) left |= 0x40;
    if (buttons & pro2::ButtonUp) left |= 0x02;
    if (buttons & pro2::ButtonRight) left |= 0x04;
    if (buttons & pro2::ButtonDown) left |= 0x01;
    if (buttons & pro2::ButtonLeft) left |= 0x08;
    pack_stick(input.left_x, input.left_y, report->data() + 7);
    pack_stick(input.right_x, input.right_y, report->data() + 10);
    (*report)[13] = 0x08;
}

uint8_t simple_hat(uint32_t buttons) {
    const bool up = (buttons & pro2::ButtonUp) != 0;
    const bool right = (buttons & pro2::ButtonRight) != 0;
    const bool down = (buttons & pro2::ButtonDown) != 0;
    const bool left = (buttons & pro2::ButtonLeft) != 0;
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

uint16_t simple_axis(uint16_t raw12) {
    const uint32_t normalized = pro2::normalize_stick_to_raw12(raw12);
    if (normalized <= 0x0800u) {
        return static_cast<uint16_t>(normalized * 0x8000u / 0x0800u);
    }
    return static_cast<uint16_t>(
        0x8000u +
        (normalized - 0x0800u) * 0x7fffu / 0x07ffu);
}

void set_imu_sample(const pro2::InputState &input, uint8_t *target) {
    const imu::RawAxes axes = imu::to_switch1(input);
    write_u16(target + 0, axes.accel_x);
    write_u16(target + 2, axes.accel_y);
    write_u16(target + 4, axes.accel_z);
    write_u16(target + 6, axes.gyro_x);
    write_u16(target + 8, axes.gyro_y);
    write_u16(target + 10, axes.gyro_z);
}

void make_imu_calibration(uint8_t *target) {
    for (int axis = 0; axis < 3; ++axis) {
        write_u16(target + axis * 2, 0);
        write_u16(target + 6 + axis * 2, 16384);
        write_u16(target + 12 + axis * 2, 0);
        write_u16(target + 18 + axis * 2, 13371);
    }
}

void set_spi_reply(const uint8_t *body, size_t body_size, Report *reply) {
    if (body_size < 15) {
        return;
    }
    const uint32_t address =
        static_cast<uint32_t>(body[10]) |
        (static_cast<uint32_t>(body[11]) << 8) |
        (static_cast<uint32_t>(body[12]) << 16) |
        (static_cast<uint32_t>(body[13]) << 24);
    const uint8_t requested = std::min<uint8_t>(body[14], 29);
    (*reply)[14] = 0x90;
    (*reply)[15] = 0x10;
    write_u32(reply->data() + 16, address);
    (*reply)[20] = requested;
    uint8_t *data = reply->data() + 21;
    std::fill_n(data, requested, static_cast<uint8_t>(0xff));

    static constexpr std::array<uint8_t, 25> kStickCalibration = {
        0xf0, 0x07, 0x7f, 0xf0, 0x07, 0x7f, 0xf0, 0x07, 0x7f,
        0xf0, 0x07, 0x7f, 0xf0, 0x07, 0x7f, 0xf0, 0x07, 0x7f,
        0x0f, 0x0f, 0x00, 0x00, 0x00, 0x00, 0x00,
    };
    static constexpr std::array<uint8_t, 24> kFactoryParameters = {
        0x5e, 0x01, 0x00, 0x00, 0xf1, 0x0f, 0x19, 0xd0,
        0x4c, 0xae, 0x40, 0xe1, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
    };
    static constexpr std::array<uint8_t, 18> kStickParameters = {
        0x19, 0xd0, 0x4c, 0xae, 0x40, 0xe1, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
    };
    std::array<uint8_t, 24> imu_cal{};
    make_imu_calibration(imu_cal.data());
    if (address == 0x603d) {
        std::copy_n(kStickCalibration.begin(),
                    std::min<size_t>(requested, kStickCalibration.size()),
                    data);
    } else if (address == 0x6046) {
        std::copy_n(kStickCalibration.begin() + 9,
                    std::min<size_t>(requested, 9), data);
    } else if (address == 0x6020) {
        std::copy_n(imu_cal.begin(), std::min<size_t>(requested, 24), data);
    } else if (address == 0x6000) {
        std::fill_n(data, requested, static_cast<uint8_t>(0xff));
    } else if (address == 0x6050 && requested >= 12) {
        const uint8_t colors[] = {
            0x32, 0x32, 0x32, 0xff, 0xff, 0xff,
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff};
        std::copy_n(colors, 12, data);
    } else if (address == 0x6080) {
        std::copy_n(kFactoryParameters.begin(),
                    std::min<size_t>(requested, kFactoryParameters.size()),
                    data);
    } else if (address == 0x6098) {
        std::copy_n(kStickParameters.begin(),
                    std::min<size_t>(requested, kStickParameters.size()),
                    data);
    }
}

uint8_t decode_side_amplitude(const uint8_t *encoded) {
    const size_t index =
        std::min<size_t>((encoded[1] & 0xfeu) / 2u,
                         kRumbleAmplitudes.size() - 1);
    return static_cast<uint8_t>(
        (static_cast<uint32_t>(kRumbleAmplitudes[index]) * 255u + 501u) /
        1003u);
}

} // namespace

const uint8_t *report_descriptor() {
    return kReportDescriptor;
}

size_t report_descriptor_size() {
    return sizeof(kReportDescriptor);
}

Report make_input_report(const pro2::InputState *samples, size_t count,
                         const RuntimeState &runtime) {
    Report report{};
    report[0] = 0xa1;
    report[1] = 0x30;
    pro2::InputState neutral;
    const pro2::InputState &latest =
        count > 0 && samples != nullptr ? samples[count - 1] : neutral;
    set_standard_input(latest, &report);
    if (runtime.imu_enabled) {
        for (size_t slot = 0; slot < 3; ++slot) {
            const size_t source =
                count == 0 ? 0 : std::min(slot, count - 1);
            set_imu_sample(
                count > 0 && samples != nullptr ? samples[source] : neutral,
                report.data() + 14 + slot * 12);
        }
    }
    return report;
}

SimpleReport make_simple_input_report(const pro2::InputState &input) {
    SimpleReport report{};
    report[0] = 0xa1;
    report[1] = 0x3f;
    uint8_t &face_and_shoulders = report[2];
    uint8_t &shared = report[3];
    if (input.buttons & pro2::ButtonB) face_and_shoulders |= 0x01;
    if (input.buttons & pro2::ButtonA) face_and_shoulders |= 0x02;
    if (input.buttons & pro2::ButtonY) face_and_shoulders |= 0x04;
    if (input.buttons & pro2::ButtonX) face_and_shoulders |= 0x08;
    if (input.buttons & pro2::ButtonL) face_and_shoulders |= 0x10;
    if (input.buttons & pro2::ButtonR) face_and_shoulders |= 0x20;
    if (input.buttons & pro2::ButtonZL) face_and_shoulders |= 0x40;
    if (input.buttons & pro2::ButtonZR) face_and_shoulders |= 0x80;
    if (input.buttons & pro2::ButtonMinus) shared |= 0x01;
    if (input.buttons & pro2::ButtonPlus) shared |= 0x02;
    if (input.buttons & pro2::ButtonLeftStick) shared |= 0x04;
    if (input.buttons & pro2::ButtonRightStick) shared |= 0x08;
    if (input.buttons & pro2::ButtonHome) shared |= 0x10;
    if (input.buttons & pro2::ButtonCapture) shared |= 0x20;
    report[4] = simple_hat(input.buttons);
    write_u16_unsigned(report.data() + 5, simple_axis(input.left_x));
    write_u16_unsigned(report.data() + 7, simple_axis(input.left_y));
    write_u16_unsigned(report.data() + 9, simple_axis(input.right_x));
    write_u16_unsigned(report.data() + 11, simple_axis(input.right_y));
    return report;
}

bool make_subcommand_reply(const uint8_t *body, size_t body_size,
                           const std::array<uint8_t, 6> &address,
                           const pro2::InputState *live_input,
                           RuntimeState *runtime, Report *reply) {
    if (body == nullptr || runtime == nullptr || reply == nullptr ||
        body_size < 10) {
        return false;
    }
    *reply = {};
    (*reply)[0] = 0xa1;
    (*reply)[1] = 0x21;
    pro2::InputState neutral;
    set_standard_input(live_input != nullptr ? *live_input : neutral, reply);
    const uint8_t subcommand = body[9];
    (*reply)[15] = subcommand;
    switch (subcommand) {
    case 0x01:
        (*reply)[14] = 0x80;
        break;
    case 0x02:
        (*reply)[14] = 0x82;
        (*reply)[16] = 0x03;
        (*reply)[17] = 0x48;
        (*reply)[18] = 0x03;
        (*reply)[19] = 0x02;
        std::copy(address.begin(), address.end(), reply->begin() + 20);
        (*reply)[26] = 0x01;
        (*reply)[27] = 0x02;
        break;
    case 0x03:
        (*reply)[14] = 0x80;
        if (body_size >= 11) {
            const uint8_t requested_mode = body[10];
            if (requested_mode == 0x30 || requested_mode == 0x3f) {
                runtime->input_mode = requested_mode;
            }
        }
        break;
    case 0x04:
        (*reply)[14] = 0x83;
        {
            static constexpr std::array<uint8_t, 9> kElapsedTime = {
                0x00, 0x6a, 0x01, 0xbb, 0x01,
                0x93, 0x01, 0x95, 0x01,
            };
            std::copy(kElapsedTime.begin(), kElapsedTime.end(),
                      reply->begin() + 16);
        }
        break;
    case 0x08:
        (*reply)[14] = 0x80;
        break;
    case 0x10:
        set_spi_reply(body, body_size, reply);
        break;
    case 0x21:
        (*reply)[14] = 0x80;
        break;
    case 0x22:
        (*reply)[14] = 0x80;
        break;
    case 0x30:
        (*reply)[14] = 0x80;
        if (body_size >= 11) {
            runtime->player_lights = body[10];
        }
        break;
    case 0x40:
        (*reply)[14] = 0x80;
        if (body_size >= 11) {
            runtime->imu_enabled = body[10] != 0;
        }
        break;
    case 0x41:
        (*reply)[14] = 0x80;
        break;
    case 0x48:
        (*reply)[14] = 0x80;
        if (body_size >= 11) {
            runtime->vibration_enabled = body[10] != 0;
        }
        break;
    default:
        (*reply)[14] = 0x80;
        break;
    }
    return true;
}

pro2_rumble::XboxRumbleCommand decode_rumble(const uint8_t *body,
                                             size_t body_size) {
    pro2_rumble::XboxRumbleCommand result;
    if (body == nullptr || body_size < 9) {
        return result;
    }
    const uint8_t left = decode_side_amplitude(body + 1);
    const uint8_t right = decode_side_amplitude(body + 5);
    result.enabled_mask = 0x03;
    // This bridge intentionally reduces Nintendo HD rumble to ordinary
    // stereo rumble. The trigger fields are used as explicit left/right
    // amplitudes so the shared ordinary strong/weak fields remain available
    // for Xbox and DualSense motor semantics.
    result.left_trigger = left;
    result.right_trigger = right;
    return result;
}

bool self_test() {
    RuntimeState runtime;
    std::array<pro2::InputState, 3> inputs{};
    inputs[0].left_x = 0x000;
    inputs[1].left_x = 0x800;
    inputs[2].left_x = 0xfff;
    inputs[2].left_y = 0xfff;
    inputs[2].buttons =
        pro2::ButtonA | pro2::ButtonUp | pro2::ButtonZL;
    inputs[0].gyro_x = 14247 / 10;
    inputs[1].gyro_x = 14247 / 5;
    inputs[2].gyro_x = 14247 / 2;
    const Report input = make_input_report(inputs.data(), inputs.size(), runtime);
    const SimpleReport simple = make_simple_input_report(inputs[2]);
    const uint8_t request[] = {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0x40, 1};
    Report reply{};
    const std::array<uint8_t, 6> address = {1, 2, 3, 4, 5, 6};
    const bool replied = make_subcommand_reply(
        request, sizeof(request), address, nullptr, &runtime, &reply);
    const uint8_t rumble_body[] = {
        0, 0, 0xc8, 0x72, 0x00, 0, 0x64, 0x59, 0x00};
    const auto rumble = decode_rumble(rumble_body, sizeof(rumble_body));
    return report_descriptor_size() > 150 &&
           input[0] == 0xa1 && input[1] == 0x30 &&
           (input[4] & 0x08) != 0 && (input[6] & 0x82) == 0x82 &&
           input[7] == 0xff && (input[8] & 0x0f) == 0x0f &&
           input[9] == 0xff && input[13] == 0x08 &&
           input[20] == input[32] &&
           simple[0] == 0xa1 && simple[1] == 0x3f &&
           simple[2] == 0x42 && simple[3] == 0 && simple[4] == 0 &&
           simple[5] == 0xff && simple[6] == 0xff &&
           replied && reply[1] == 0x21 && reply[14] == 0x80 &&
           runtime.imu_enabled && rumble.enabled_mask == 0x03 &&
           rumble.left_trigger != rumble.right_trigger;
}

} // namespace switch1
