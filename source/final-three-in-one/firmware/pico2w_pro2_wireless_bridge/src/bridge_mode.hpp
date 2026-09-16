#pragma once

#include <cstdint>

namespace bridge {

enum class Mode : uint8_t {
    XboxBle = 0,
    DualSense = 1,
    DualSenseEdge = 2,
    SwitchProClassic = 3,
    Pro2Quiet = 4,
};

constexpr Mode kDefaultMode = Mode::DualSenseEdge;

constexpr const char *mode_name(Mode mode) {
    switch (mode) {
    case Mode::XboxBle:
        return "xbox_ble";
    case Mode::DualSense:
        return "dualsense";
    case Mode::DualSenseEdge:
        return "dualsense_edge";
    case Mode::SwitchProClassic:
        return "switch_pro_classic";
    case Mode::Pro2Quiet:
        return "pro2_quiet";
    }
    return "unknown";
}

constexpr const char *mode_display_name(Mode mode) {
    switch (mode) {
    case Mode::XboxBle:
        return "Xbox";
    case Mode::DualSense:
        return "PS5";
    case Mode::DualSenseEdge:
        return "PS5 Edge";
    case Mode::SwitchProClassic:
        return "NS1 Pro";
    case Mode::Pro2Quiet:
        return "Pro2 Native / Bridge Off";
    }
    return "Unknown";
}

} // namespace bridge
