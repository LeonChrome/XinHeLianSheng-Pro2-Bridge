#include "pairing_chord.hpp"

#include "pro2_protocol.hpp"

namespace pairing_chord {
namespace {

constexpr uint32_t kGuardMask =
    pro2::ButtonGL | pro2::ButtonGR | pro2::ButtonZL | pro2::ButtonZR |
    pro2::ButtonPlus | pro2::ButtonMinus;
constexpr uint32_t kModeSelectorMask =
    pro2::ButtonA | pro2::ButtonB | pro2::ButtonX | pro2::ButtonY;

} // namespace

bool decode(uint32_t buttons) {
    return (buttons & kGuardMask) == kGuardMask &&
           (buttons & kModeSelectorMask) == 0;
}

bool Detector::update(uint32_t buttons, bool source_fresh, uint64_t now_us) {
    if (!source_fresh || !decode(buttons)) {
        reset();
        return false;
    }
    if (latched_) {
        return false;
    }
    if (!candidate_active_) {
        candidate_active_ = true;
        candidate_started_at_us_ = now_us;
        return false;
    }
    if (now_us - candidate_started_at_us_ < kHoldDurationUs) {
        return false;
    }
    latched_ = true;
    return true;
}

void Detector::reset() {
    candidate_started_at_us_ = 0;
    candidate_active_ = false;
    latched_ = false;
}

bool self_test() {
    const uint32_t chord =
        pro2::ButtonGL | pro2::ButtonGR | pro2::ButtonZL | pro2::ButtonZR |
        pro2::ButtonPlus | pro2::ButtonMinus;
    if (!decode(chord) || decode(chord | pro2::ButtonA) ||
        decode(chord & ~pro2::ButtonMinus)) {
        return false;
    }

    Detector detector;
    const uint64_t started = 100;
    if (detector.update(chord, true, started) ||
        detector.update(chord, true, started + kHoldDurationUs - 1) ||
        !detector.update(chord, true, started + kHoldDurationUs) ||
        detector.update(chord, true, started + kHoldDurationUs + 1)) {
        return false;
    }

    detector.update(0, true, started + kHoldDurationUs + 2);
    if (detector.update(chord, true, started + kHoldDurationUs + 3) ||
        !detector.update(chord, true, started + 2 * kHoldDurationUs + 3)) {
        return false;
    }

    detector.reset();
    detector.update(chord, true, started);
    detector.update(chord, false, started + kHoldDurationUs);
    return !detector.update(chord, true, started + kHoldDurationUs + 1);
}

} // namespace pairing_chord
