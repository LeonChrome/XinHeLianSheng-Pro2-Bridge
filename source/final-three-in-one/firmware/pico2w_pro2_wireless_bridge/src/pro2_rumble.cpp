#include "pro2_rumble.hpp"

#include <algorithm>
#include <cstddef>

namespace pro2_rumble {
namespace {

constexpr uint16_t kMaximumAmplitude = 640;

void build_neutral_side_payload(uint8_t *out) {
    constexpr uint16_t kNeutralLowFrequency = 0x0e1;
    constexpr uint16_t kNeutralHighFrequency = 0x1e1;
    uint64_t packed = 0;
    packed |= static_cast<uint64_t>(kNeutralLowFrequency);
    packed |= static_cast<uint64_t>(kNeutralHighFrequency) << 20;
    for (size_t index = 0; index < 5; ++index) {
        out[index] = static_cast<uint8_t>(packed >> (index * 8));
    }
}

uint16_t scale_amplitude(uint8_t value) {
    return static_cast<uint16_t>(
        (static_cast<uint32_t>(value) * kMaximumAmplitude + 127u) / 255u);
}

uint16_t mix_frequency(uint16_t low, uint16_t high, uint8_t value) {
    return static_cast<uint16_t>(
        low + ((static_cast<uint32_t>(high - low) * value + 127u) / 255u));
}

void build_side_payload(uint8_t weak, uint8_t strong, uint8_t *out) {
    if (weak == 0 && strong == 0) {
        // Pro2 expects a syntactically valid vibration frame even when both
        // amplitudes are zero. Five zero bytes are not its neutral encoding.
        build_neutral_side_payload(out);
        return;
    }

    const uint16_t low_amplitude = scale_amplitude(strong);
    const uint16_t high_amplitude = scale_amplitude(weak);
    const uint16_t low_frequency =
        low_amplitude == 0 ? 0x0e1 : mix_frequency(0x0b8, 0x122, strong);
    const uint16_t high_frequency =
        high_amplitude == 0 ? 0x1e1 : mix_frequency(0x160, 0x1f0, weak);

    uint64_t packed = 0;
    packed |= static_cast<uint64_t>(low_frequency);
    packed |= static_cast<uint64_t>(low_amplitude & 0x03ff) << 10;
    packed |= static_cast<uint64_t>(high_frequency) << 20;
    packed |= static_cast<uint64_t>(high_amplitude & 0x03ff) << 30;
    for (size_t index = 0; index < 5; ++index) {
        out[index] = static_cast<uint8_t>(packed >> (index * 8));
    }
}

} // namespace

bool active(const XboxRumbleCommand &command) {
    const uint8_t left_trigger =
        (command.enabled_mask & 0x01) != 0 ? command.left_trigger : 0;
    const uint8_t right_trigger =
        (command.enabled_mask & 0x02) != 0 ? command.right_trigger : 0;
    const uint8_t strong =
        (command.enabled_mask & 0x04) != 0 ? command.strong : 0;
    const uint8_t weak =
        (command.enabled_mask & 0x08) != 0 ? command.weak : 0;
    return left_trigger != 0 || right_trigger != 0 || strong != 0 || weak != 0;
}

bool equivalent(const XboxRumbleCommand &left,
                const XboxRumbleCommand &right) {
    const auto effective = [](const XboxRumbleCommand &command,
                              uint8_t mask, uint8_t value) {
        return (command.enabled_mask & mask) != 0 ? value : 0;
    };
    return effective(left, 0x01, left.left_trigger) ==
               effective(right, 0x01, right.left_trigger) &&
           effective(left, 0x02, left.right_trigger) ==
               effective(right, 0x02, right.right_trigger) &&
           effective(left, 0x04, left.strong) ==
               effective(right, 0x04, right.strong) &&
           effective(left, 0x08, left.weak) ==
               effective(right, 0x08, right.weak);
}

std::array<uint8_t, kReportSize> build_report(
    uint8_t counter, const XboxRumbleCommand &command) {
    std::array<uint8_t, kReportSize> report{};
    report[0] = kReportId;

    const uint8_t left_trigger =
        (command.enabled_mask & 0x01) != 0 ? command.left_trigger : 0;
    const uint8_t right_trigger =
        (command.enabled_mask & 0x02) != 0 ? command.right_trigger : 0;
    const uint8_t strong =
        (command.enabled_mask & 0x04) != 0 ? command.strong : 0;
    const uint8_t weak =
        (command.enabled_mask & 0x08) != 0 ? command.weak : 0;

    const uint8_t sequence = static_cast<uint8_t>(0x50 | (counter & 0x0f));
    report[1] = sequence;
    report[17] = sequence;

    // Match the proven ESP bridge packet policy: both Pro2 actuators receive
    // the ordinary low/high frequency pair. Trigger contributions stay local
    // to their side, while normal game rumble remains balanced and audible.
    build_side_payload(std::max(weak, left_trigger), strong,
                       report.data() + 2);
    build_side_payload(std::max(weak, right_trigger), strong,
                       report.data() + 18);
    return report;
}

bool self_test() {
    XboxRumbleCommand stop;
    stop.enabled_mask = 0x0f;
    const auto stopped = build_report(0x2a, stop);
    if (stopped[0] != kReportId || stopped[1] != 0x5a ||
        stopped[17] != 0x5a) {
        return false;
    }
    std::array<uint8_t, 5> neutral{};
    build_neutral_side_payload(neutral.data());
    if (!std::equal(neutral.begin(), neutral.end(), stopped.begin() + 2) ||
        !std::equal(neutral.begin(), neutral.end(), stopped.begin() + 18)) {
        return false;
    }

    XboxRumbleCommand active_command;
    active_command.enabled_mask = 0x0f;
    active_command.left_trigger = 32;
    active_command.right_trigger = 64;
    active_command.strong = 128;
    active_command.weak = 96;
    const auto active_report = build_report(3, active_command);
    const bool left_payload_nonzero =
        std::any_of(active_report.begin() + 2, active_report.begin() + 7,
                    [](uint8_t value) { return value != 0; });
    const bool right_payload_nonzero =
        std::any_of(active_report.begin() + 18, active_report.begin() + 23,
                    [](uint8_t value) { return value != 0; });
    XboxRumbleCommand equivalent_command = active_command;
    equivalent_command.duration = 37;
    equivalent_command.loop_count = 9;
    XboxRumbleCommand different_command = equivalent_command;
    different_command.weak = 95;
    XboxRumbleCommand masked_stop;
    masked_stop.enabled_mask = 0x0c;
    masked_stop.left_trigger = 255;
    masked_stop.right_trigger = 255;
    XboxRumbleCommand strong_only;
    strong_only.enabled_mask = 0x04;
    strong_only.strong = 255;
    const auto strong_report = build_report(0, strong_only);
    XboxRumbleCommand weak_only;
    weak_only.enabled_mask = 0x08;
    weak_only.weak = 255;
    const auto weak_report = build_report(0, weak_only);
    const bool strong_is_balanced =
        std::any_of(strong_report.begin() + 2,
                    strong_report.begin() + 7,
                    [](uint8_t value) { return value != 0; }) &&
        std::any_of(strong_report.begin() + 18,
                    strong_report.begin() + 23,
                    [](uint8_t value) { return value != 0; });
    const bool weak_is_balanced =
        std::any_of(weak_report.begin() + 2,
                    weak_report.begin() + 7,
                    [](uint8_t value) { return value != 0; }) &&
        std::any_of(weak_report.begin() + 18,
                    weak_report.begin() + 23,
                    [](uint8_t value) { return value != 0; });
    return active(active_command) &&
           equivalent(active_command, equivalent_command) &&
           !equivalent(active_command, different_command) &&
           equivalent(stop, XboxRumbleCommand{}) &&
           equivalent(stop, masked_stop) &&
           active_report[0] == kReportId &&
           active_report[1] == 0x53 && active_report[17] == 0x53 &&
           left_payload_nonzero && right_payload_nonzero &&
           strong_is_balanced && weak_is_balanced;
}

} // namespace pro2_rumble
