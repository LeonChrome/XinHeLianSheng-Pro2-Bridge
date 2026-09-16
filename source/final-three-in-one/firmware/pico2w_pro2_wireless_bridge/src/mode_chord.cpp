#include "mode_chord.hpp"

#include "pro2_protocol.hpp"

namespace mode_chord {
namespace {

constexpr uint32_t kGuardMask =
    pro2::ButtonGL | pro2::ButtonGR | pro2::ButtonZL | pro2::ButtonZR;
constexpr uint32_t kSelectorMask =
    pro2::ButtonA | pro2::ButtonB | pro2::ButtonX | pro2::ButtonY;

} // namespace

bool decode(uint32_t buttons, bridge::Mode *target) {
    if (target == nullptr || (buttons & kGuardMask) != kGuardMask) {
        return false;
    }

    const uint32_t selector = buttons & kSelectorMask;
    switch (selector) {
    case pro2::ButtonA:
        *target = bridge::Mode::DualSense;
        return true;
    case pro2::ButtonB:
        *target = bridge::Mode::DualSenseEdge;
        return true;
    case pro2::ButtonX:
        *target = bridge::Mode::XboxBle;
        return true;
    case pro2::ButtonY:
        *target = bridge::Mode::SwitchProClassic;
        return true;
    default:
        // No selector or multiple selectors must never choose a mode.
        return false;
    }
}

bool Detector::update(uint32_t buttons, bool source_fresh, uint64_t now_us,
                      bridge::Mode *target) {
    bridge::Mode decoded = bridge::kDefaultMode;
    if (!source_fresh || !decode(buttons, &decoded)) {
        reset();
        return false;
    }

    if (latched_) {
        return false;
    }

    if (!candidate_active_ || candidate_ != decoded) {
        candidate_ = decoded;
        candidate_started_at_us_ = now_us;
        candidate_active_ = true;
        return false;
    }

    if (now_us - candidate_started_at_us_ < kHoldDurationUs) {
        return false;
    }

    latched_ = true;
    if (target != nullptr) {
        *target = candidate_;
    }
    return true;
}

void Detector::reset() {
    candidate_ = bridge::kDefaultMode;
    candidate_started_at_us_ = 0;
    candidate_active_ = false;
    latched_ = false;
}

bool self_test() {
    const uint32_t guard = kGuardMask;
    bridge::Mode target = bridge::kDefaultMode;

    if (!decode(guard | pro2::ButtonA, &target) ||
        target != bridge::Mode::DualSense ||
        !decode(guard | pro2::ButtonB, &target) ||
        target != bridge::Mode::DualSenseEdge ||
        !decode(guard | pro2::ButtonX, &target) ||
        target != bridge::Mode::XboxBle ||
        !decode(guard | pro2::ButtonY, &target) ||
        target != bridge::Mode::SwitchProClassic ||
        decode(guard, &target) ||
        decode(guard | pro2::ButtonA | pro2::ButtonB, &target) ||
        decode(pro2::ButtonA, &target)) {
        return false;
    }

    Detector detector;
    const uint64_t started = 100;
    if (detector.update(guard | pro2::ButtonA, true, started, &target) ||
        detector.update(guard | pro2::ButtonA, true,
                        started + kHoldDurationUs - 1, &target) ||
        !detector.update(guard | pro2::ButtonA, true,
                         started + kHoldDurationUs, &target) ||
        target != bridge::Mode::DualSense ||
        detector.update(guard | pro2::ButtonA, true,
                        started + kHoldDurationUs + 1, &target)) {
        return false;
    }

    detector.update(0, true, started + kHoldDurationUs + 2, &target);
    if (detector.update(guard | pro2::ButtonB, true,
                        started + kHoldDurationUs + 3, &target) ||
        !detector.update(guard | pro2::ButtonB, true,
                         started + 2 * kHoldDurationUs + 3, &target) ||
        target != bridge::Mode::DualSenseEdge) {
        return false;
    }

    detector.reset();
    detector.update(guard | pro2::ButtonX, true, started, &target);
    detector.update(guard | pro2::ButtonX, false,
                    started + kHoldDurationUs, &target);
    return !detector.update(guard | pro2::ButtonX, true,
                            started + kHoldDurationUs + 1, &target);
}

} // namespace mode_chord
