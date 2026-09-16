#include "pairing_feedback.hpp"

#include <cstdio>

#include "pico/time.h"
#include "pro2_host.hpp"
#include "pro2_rumble.hpp"

namespace pairing_feedback {
namespace {

constexpr uint64_t kWaitForPro2Us = 30000000;
constexpr uint64_t kPulseUs = 140000;
constexpr uint64_t kGapUs = 180000;
constexpr uint64_t kReassertUs = 40000;

enum class Phase {
    Idle,
    WaitingForPro2,
    PulseOne,
    Gap,
    PulseTwo,
};

Phase g_phase = Phase::Idle;
uint64_t g_deadline_us = 0;
uint64_t g_phase_deadline_us = 0;
uint64_t g_next_reassert_us = 0;
const char *g_reason = "unspecified";

pro2_rumble::XboxRumbleCommand pulse_command() {
    pro2_rumble::XboxRumbleCommand command;
    command.enabled_mask = 0x04;
    command.strong = 36;
    return command;
}

void begin_pulse(Phase phase, uint64_t now) {
    g_phase = phase;
    g_phase_deadline_us = now + kPulseUs;
    g_next_reassert_us = now + kReassertUs;
    pro2_host::set_rumble(pulse_command());
}

} // namespace

void start(const char *reason) {
    const uint64_t now = time_us_64();
    g_phase = Phase::WaitingForPro2;
    g_deadline_us = now + kWaitForPro2Us;
    g_phase_deadline_us = 0;
    g_next_reassert_us = 0;
    g_reason = reason != nullptr ? reason : "unspecified";
    std::printf(
        "[PAIR_FEEDBACK] queued reason=%s pattern=low_frequency_double_pulse "
        "strength=36 wait_for_pro2_ms=%llu\n",
        g_reason,
        static_cast<unsigned long long>(kWaitForPro2Us / 1000));
}

void task() {
    if (g_phase == Phase::Idle) {
        return;
    }

    const uint64_t now = time_us_64();
    if (g_phase == Phase::WaitingForPro2) {
        if (pro2_host::telemetry().reports_running) {
            std::printf("[PAIR_FEEDBACK] started reason=%s\n", g_reason);
            begin_pulse(Phase::PulseOne, now);
        } else if (now >= g_deadline_us) {
            std::printf(
                "[PAIR_FEEDBACK] skipped reason=%s "
                "cause=pro2_reports_not_ready\n",
                g_reason);
            g_phase = Phase::Idle;
        }
        return;
    }

    if (g_phase == Phase::Gap) {
        if (now >= g_phase_deadline_us) {
            begin_pulse(Phase::PulseTwo, now);
        }
        return;
    }

    if (now >= g_phase_deadline_us) {
        pro2_host::stop_rumble();
        if (g_phase == Phase::PulseOne) {
            g_phase = Phase::Gap;
            g_phase_deadline_us = now + kGapUs;
        } else {
            std::printf("[PAIR_FEEDBACK] completed reason=%s\n", g_reason);
            g_phase = Phase::Idle;
        }
        return;
    }

    // A wireless-host disconnect callback also stops rumble. Reassert the
    // short pulse so explicit re-pairing remains tactile during disconnect.
    if (now >= g_next_reassert_us) {
        pro2_host::set_rumble(pulse_command());
        g_next_reassert_us = now + kReassertUs;
    }
}

} // namespace pairing_feedback
