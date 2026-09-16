#pragma once

#include <cstdint>

namespace pairing_chord {

constexpr uint64_t kHoldDurationUs = 2000000;

class Detector {
public:
    bool update(uint32_t buttons, bool source_fresh, uint64_t now_us);
    void reset();

private:
    uint64_t candidate_started_at_us_ = 0;
    bool candidate_active_ = false;
    bool latched_ = false;
};

bool decode(uint32_t buttons);
bool self_test();

} // namespace pairing_chord
