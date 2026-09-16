#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

namespace pro2_rumble {

constexpr uint8_t kReportId = 0x02;
constexpr size_t kReportSize = 64;

struct XboxRumbleCommand {
    uint8_t enabled_mask = 0;
    uint8_t left_trigger = 0;
    uint8_t right_trigger = 0;
    uint8_t strong = 0;
    uint8_t weak = 0;
    uint8_t duration = 0;
    uint8_t start_delay = 0;
    uint8_t loop_count = 0;
};

bool active(const XboxRumbleCommand &command);
bool equivalent(const XboxRumbleCommand &left,
                const XboxRumbleCommand &right);
std::array<uint8_t, kReportSize> build_report(
    uint8_t counter, const XboxRumbleCommand &command);
bool self_test();

} // namespace pro2_rumble
