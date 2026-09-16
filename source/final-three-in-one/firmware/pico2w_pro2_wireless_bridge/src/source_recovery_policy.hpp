#pragma once

#include <cstdint>

namespace source_recovery {

enum class Action : uint8_t {
    None,
    AbortInput,
    SubmitReceive,
    BeginBusReset,
    EndBusReset,
};

enum class Phase : uint8_t {
    Monitoring,
    RearmAfterAbort,
    AwaitInput,
    BusResetActive,
    AwaitBusRecovery,
    Backoff,
};

struct Snapshot {
    Phase phase = Phase::Monitoring;
    uint32_t incidents = 0;
    uint32_t abort_attempts = 0;
    uint32_t receive_rearms = 0;
    uint32_t bus_resets = 0;
    uint32_t recoveries = 0;
};

class Controller {
public:
    Action tick(uint64_t now_us, bool mounted, bool reports_running,
                uint64_t source_started_us, uint64_t last_report_us);
    bool note_report();
    void reset_runtime();
    Snapshot snapshot() const;
    static const char *phase_name(Phase phase);

private:
    Phase phase_ = Phase::Monitoring;
    uint64_t deadline_us_ = 0;
    uint32_t incidents_ = 0;
    uint32_t abort_attempts_ = 0;
    uint32_t receive_rearms_ = 0;
    uint32_t bus_resets_ = 0;
    uint32_t recoveries_ = 0;
};

} // namespace source_recovery
