#pragma once

#include <cstdint>

#include "pro2_rumble.hpp"

namespace pro2_host {

struct Telemetry {
    bool device_connected = false;
    bool hid_ready = false;
    bool vendor_ready = false;
    bool reports_running = false;
    uint32_t parsed_reports = 0;
    uint32_t rejected_reports = 0;
    uint32_t duplicate_sequences = 0;
    uint32_t disconnects = 0;
    uint32_t init_failures = 0;
    uint32_t source_recovery_incidents = 0;
    uint32_t source_recovery_aborts = 0;
    uint32_t source_recovery_rearms = 0;
    uint32_t source_recovery_bus_resets = 0;
    uint32_t source_recoveries = 0;
    const char *source_recovery_phase = "monitoring";
    uint32_t rumble_updates = 0;
    uint32_t rumble_reports = 0;
    uint32_t rumble_submit_errors = 0;
    uint32_t rumble_transfer_errors = 0;
    bool rumble_active = false;
    bool rumble_transfer_pending = false;
    uint8_t rumble_weak = 0;
    uint8_t rumble_strong = 0;
    uint64_t last_report_us = 0;
};

void init();
void task();
// A host rumble command remains active until an explicit zero/stop command.
// Use pulse_rumble only for local diagnostics that need a bounded duration.
void set_rumble(const pro2_rumble::XboxRumbleCommand &command);
void pulse_rumble(const pro2_rumble::XboxRumbleCommand &command,
                  uint32_t duration_ms);
void stop_rumble();
Telemetry telemetry();

} // namespace pro2_host
