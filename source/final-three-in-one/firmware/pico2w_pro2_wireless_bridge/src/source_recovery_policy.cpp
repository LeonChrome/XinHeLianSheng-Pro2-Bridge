#include "source_recovery_policy.hpp"

namespace source_recovery {
namespace {

constexpr uint64_t kStaleInputUs = 2000000;
constexpr uint64_t kAbortSettleUs = 20000;
constexpr uint64_t kReceiveGraceUs = 1000000;
constexpr uint64_t kBusResetPulseUs = 20000;
constexpr uint64_t kBusRecoveryGraceUs = 2000000;
constexpr uint64_t kBackoffUs = 10000000;

} // namespace

Action Controller::tick(uint64_t now_us, bool mounted, bool reports_running,
                        uint64_t source_started_us, uint64_t last_report_us) {
    if (!mounted || !reports_running) {
        reset_runtime();
        return Action::None;
    }

    // A report timestamp from the previous USB enumeration must not make a
    // newly mounted controller look stale before its first fresh report.
    const uint64_t reference_us =
        last_report_us >= source_started_us && last_report_us != 0
            ? last_report_us
            : source_started_us;
    const bool stale = reference_us != 0 && now_us >= reference_us &&
                       now_us - reference_us >= kStaleInputUs;

    if (!stale) {
        if (phase_ != Phase::Monitoring) {
            phase_ = Phase::Monitoring;
            deadline_us_ = 0;
            ++recoveries_;
        }
        return Action::None;
    }

    switch (phase_) {
    case Phase::Monitoring:
        phase_ = Phase::RearmAfterAbort;
        deadline_us_ = now_us + kAbortSettleUs;
        ++incidents_;
        ++abort_attempts_;
        return Action::AbortInput;
    case Phase::RearmAfterAbort:
        if (now_us < deadline_us_) {
            return Action::None;
        }
        phase_ = Phase::AwaitInput;
        deadline_us_ = now_us + kReceiveGraceUs;
        ++receive_rearms_;
        return Action::SubmitReceive;
    case Phase::AwaitInput:
        if (now_us < deadline_us_) {
            return Action::None;
        }
        phase_ = Phase::BusResetActive;
        deadline_us_ = now_us + kBusResetPulseUs;
        ++bus_resets_;
        return Action::BeginBusReset;
    case Phase::BusResetActive:
        if (now_us < deadline_us_) {
            return Action::None;
        }
        phase_ = Phase::AwaitBusRecovery;
        deadline_us_ = now_us + kBusRecoveryGraceUs;
        return Action::EndBusReset;
    case Phase::AwaitBusRecovery:
        if (now_us < deadline_us_) {
            return Action::None;
        }
        phase_ = Phase::Backoff;
        deadline_us_ = now_us + kBackoffUs;
        return Action::None;
    case Phase::Backoff:
        if (now_us < deadline_us_) {
            return Action::None;
        }
        phase_ = Phase::RearmAfterAbort;
        deadline_us_ = now_us + kAbortSettleUs;
        ++incidents_;
        ++abort_attempts_;
        return Action::AbortInput;
    }
    return Action::None;
}

bool Controller::note_report() {
    if (phase_ == Phase::Monitoring) {
        return false;
    }
    phase_ = Phase::Monitoring;
    deadline_us_ = 0;
    ++recoveries_;
    return true;
}

void Controller::reset_runtime() {
    phase_ = Phase::Monitoring;
    deadline_us_ = 0;
}

Snapshot Controller::snapshot() const {
    return {phase_, incidents_, abort_attempts_, receive_rearms_, bus_resets_,
            recoveries_};
}

const char *Controller::phase_name(Phase phase) {
    switch (phase) {
    case Phase::Monitoring:
        return "monitoring";
    case Phase::RearmAfterAbort:
        return "rearm_after_abort";
    case Phase::AwaitInput:
        return "await_input";
    case Phase::BusResetActive:
        return "bus_reset_active";
    case Phase::AwaitBusRecovery:
        return "await_bus_recovery";
    case Phase::Backoff:
        return "backoff";
    }
    return "unknown";
}

} // namespace source_recovery
