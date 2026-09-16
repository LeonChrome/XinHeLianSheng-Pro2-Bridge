#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

#include "pro2_protocol.hpp"
#include "pro2_rumble.hpp"

namespace switch1 {

constexpr size_t kReportSize = 50;
constexpr size_t kSimpleReportSize = 13;
using Report = std::array<uint8_t, kReportSize>;
using SimpleReport = std::array<uint8_t, kSimpleReportSize>;

struct RuntimeState {
    bool imu_enabled = false;
    bool vibration_enabled = false;
    uint8_t input_mode = 0x3f;
    uint8_t player_lights = 0x01;
};

const uint8_t *report_descriptor();
size_t report_descriptor_size();
Report make_input_report(const pro2::InputState *samples, size_t count,
                         const RuntimeState &runtime);
SimpleReport make_simple_input_report(const pro2::InputState &input);
bool make_subcommand_reply(const uint8_t *body, size_t body_size,
                           const std::array<uint8_t, 6> &address,
                           const pro2::InputState *live_input,
                           RuntimeState *runtime, Report *reply);
pro2_rumble::XboxRumbleCommand decode_rumble(const uint8_t *body,
                                             size_t body_size);
bool self_test();

} // namespace switch1
