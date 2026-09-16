#pragma once

#include <cstdint>

#include "bridge_mode.hpp"

namespace mode_chord {

constexpr uint64_t kHoldDurationUs = 1500000;

class Detector {
public:
    bool update(uint32_t buttons, bool source_fresh, uint64_t now_us,
                bridge::Mode *target);
    void reset();

private:
    bridge::Mode candidate_ = bridge::kDefaultMode;
    uint64_t candidate_started_at_us_ = 0;
    bool candidate_active_ = false;
    bool latched_ = false;
};

bool decode(uint32_t buttons, bridge::Mode *target);
bool self_test();

} // namespace mode_chord
