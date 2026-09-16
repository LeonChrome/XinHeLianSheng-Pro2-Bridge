#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef enum {
    PRO2_RUMBLE_SOURCE_NONE = 0,
    PRO2_RUMBLE_SOURCE_HD,
    PRO2_RUMBLE_SOURCE_ORDINARY,
} pro2_rumble_source_t;

typedef enum {
    PRO2_RUMBLE_HOST_AUDIO_HAPTICS = 0,
    PRO2_RUMBLE_HOST_COMPATIBILITY,
} pro2_rumble_host_mode_t;

typedef struct {
    bool ordinary_active;
    bool hd_active;
    int64_t ordinary_until_us;
    int64_t hd_until_us;
    pro2_rumble_host_mode_t host_mode;
    pro2_rumble_source_t selected_source;
    uint8_t stop_packets_pending;
    uint32_t source_transitions;
    uint32_t host_mode_transitions;
    uint32_t audio_haptics_updates;
    uint32_t compatibility_updates;
    uint32_t hd_updates_blocked_by_compatibility;
    uint32_t hd_preemptions;
    uint32_t ordinary_fallbacks;
} pro2_rumble_arbiter_t;

typedef struct {
    pro2_rumble_source_t previous_source;
    pro2_rumble_source_t selected_source;
    bool source_changed;
    bool send_stop;
} pro2_rumble_arbiter_decision_t;

void pro2_rumble_arbiter_init(pro2_rumble_arbiter_t *arbiter);
void pro2_rumble_arbiter_set_host_mode(
    pro2_rumble_arbiter_t *arbiter,
    pro2_rumble_host_mode_t mode);
void pro2_rumble_arbiter_update_ordinary(pro2_rumble_arbiter_t *arbiter,
                                         bool active,
                                         int64_t now_us,
                                         uint32_t hold_ms);
void pro2_rumble_arbiter_update_hd(pro2_rumble_arbiter_t *arbiter,
                                   bool active,
                                   int64_t now_us,
                                   uint32_t hold_ms);
pro2_rumble_arbiter_decision_t pro2_rumble_arbiter_tick(
    pro2_rumble_arbiter_t *arbiter,
    int64_t now_us,
    uint8_t stop_packet_count);
const char *pro2_rumble_arbiter_host_mode_string(
    pro2_rumble_host_mode_t mode);
