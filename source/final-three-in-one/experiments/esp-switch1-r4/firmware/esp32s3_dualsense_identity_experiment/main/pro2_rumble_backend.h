#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"
#include "pro2_rumble_arbiter.h"

#define PRO2_RUMBLE_OUTPUT_PREVIEW_BYTES 12

typedef struct {
    uint32_t output_updates;
    uint32_t active_updates;
    uint32_t non_rumble_updates;
    uint32_t ignored_nonzero_updates;
    uint32_t ordinary_ble_writes;
    uint32_t ordinary_ble_errors;
    uint32_t raw02_submissions;
    uint32_t raw02_ble_writes;
    uint32_t raw02_ble_errors;
    uint32_t stop_ble_writes;
    uint32_t source_transitions;
    uint32_t host_mode_transitions;
    uint32_t audio_haptics_updates;
    uint32_t compatibility_updates;
    uint32_t hd_updates_blocked_by_compatibility;
    uint32_t hd_preemptions;
    uint32_t ordinary_fallbacks;
    uint32_t ordinary_updates_while_hd;
    uint32_t task_stack_high_watermark_bytes;
    int64_t ordinary_age_us;
    int64_t raw02_age_us;
    pro2_rumble_host_mode_t host_mode;
    pro2_rumble_source_t selected_source;
    bool ordinary_source_active;
    bool raw02_source_active;
    bool compatibility_selected;
    bool compatibility_v1;
    bool compatibility_v2;
    bool audio_haptics_allowed;
    bool enabled;
    bool active;
    uint8_t valid_flag0;
    uint8_t valid_flag1;
    uint8_t valid_flag2;
    uint8_t right_light;
    uint8_t left_heavy;
    uint8_t preview_len;
    uint8_t preview[PRO2_RUMBLE_OUTPUT_PREVIEW_BYTES];
} pro2_rumble_backend_stats_t;

void pro2_rumble_backend_init(void);
esp_err_t pro2_rumble_backend_send_raw02_payload(const uint8_t *payload, uint16_t len);
bool pro2_rumble_backend_handle_dualsense_output(
    uint8_t report_id,
    const uint8_t *buffer,
    uint16_t len);
void pro2_rumble_backend_snapshot(pro2_rumble_backend_stats_t *out);
const char *pro2_rumble_backend_source_string(pro2_rumble_source_t source);
const char *pro2_rumble_backend_host_mode_string(
    pro2_rumble_host_mode_t mode);
