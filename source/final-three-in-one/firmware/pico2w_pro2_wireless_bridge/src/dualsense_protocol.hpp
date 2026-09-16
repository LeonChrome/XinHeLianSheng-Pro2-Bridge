#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

#include "pro2_protocol.hpp"
#include "pro2_rumble.hpp"

namespace dualsense {

constexpr uint8_t kInputReportId = 0x31;
constexpr uint8_t kOutputReportId = 0x31;
constexpr size_t kInputReportSize = 78;

using InputReport = std::array<uint8_t, kInputReportSize>;

enum class OutputAction : uint8_t {
    Invalid,
    NoMotorUpdate,
    MotorUpdate,
};

enum class MotorTransition : uint8_t {
    Ignore,
    ApplyUpdate,
    ReleaseCompatibleMotor,
};

struct OutputReport {
    OutputAction action = OutputAction::Invalid;
    pro2_rumble::XboxRumbleCommand rumble{};
    bool crc_valid = false;
    bool compatible_v2 = false;
};

const uint8_t *report_descriptor();
size_t report_descriptor_size();
InputReport make_input_report(const pro2::InputState &input, bool edge,
                              uint8_t sequence);
InputReport make_neutral_report(bool edge);
size_t make_feature_report(uint8_t report_id,
                           const std::array<uint8_t, 6> &address, bool edge,
                           uint8_t *body, size_t body_capacity);
OutputReport decode_output_report(const uint8_t *body, size_t body_size);
MotorTransition resolve_motor_transition(const OutputReport &report,
                                         bool compatible_motor_active);
const char *output_action_name(OutputAction action);
const char *motor_transition_name(MotorTransition transition);
bool self_test();

} // namespace dualsense
