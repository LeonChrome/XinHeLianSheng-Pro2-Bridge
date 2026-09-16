#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"

#define BLE_DUAL_PROBE_PAD_COUNT 2

typedef enum {
    BLE_DUAL_PROBE_SIM_OFF = 0,
    BLE_DUAL_PROBE_SIM_MIRROR = 1,
    BLE_DUAL_PROBE_SIM_SYNTHETIC = 2
} ble_dual_probe_sim_mode_t;

typedef struct {
    bool running;
    bool host_ready;
    bool scanning;
    ble_dual_probe_sim_mode_t sim_mode;
    uint16_t sim_rate_hz;
    uint32_t scan_seen_count;
    uint32_t candidate_seen_count;
    uint32_t target_count;
    uint32_t total_notify_count;
    uint32_t total_notify_actual_millihz;
    uint32_t total_notify_last_gap_us;
    uint32_t total_notify_max_gap_us;
    bool pad_target_valid[BLE_DUAL_PROBE_PAD_COUNT];
    bool pad_connected[BLE_DUAL_PROBE_PAD_COUNT];
    bool pad_ready[BLE_DUAL_PROBE_PAD_COUNT];
    bool pad_simulated[BLE_DUAL_PROBE_PAD_COUNT];
    uint16_t pad_conn_handle[BLE_DUAL_PROBE_PAD_COUNT];
    uint16_t pad_input_handle[BLE_DUAL_PROBE_PAD_COUNT];
    uint16_t pad_interval_units[BLE_DUAL_PROBE_PAD_COUNT];
    uint16_t pad_latency[BLE_DUAL_PROBE_PAD_COUNT];
    uint16_t pad_supervision_timeout[BLE_DUAL_PROBE_PAD_COUNT];
    int pad_last_connect_status[BLE_DUAL_PROBE_PAD_COUNT];
    int pad_last_disconnect_reason[BLE_DUAL_PROBE_PAD_COUNT];
    int pad_last_update_status[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_connect_start_count[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_connect_success_count[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_connect_failure_count[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_disconnect_count[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_notify_count[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_notify_actual_millihz[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_notify_last_gap_us[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_notify_max_gap_us[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_unique_count[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_repeat_count[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_unique_actual_millihz[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_unique_last_gap_us[BLE_DUAL_PROBE_PAD_COUNT];
    uint32_t pad_unique_max_gap_us[BLE_DUAL_PROBE_PAD_COUNT];
    char pad_addr[BLE_DUAL_PROBE_PAD_COUNT][32];
    char pad_name[BLE_DUAL_PROBE_PAD_COUNT][32];
} ble_dual_probe_metrics_t;

void ble_dual_probe_host_ready(uint8_t own_addr_type);
esp_err_t ble_dual_probe_start(void);
void ble_dual_probe_stop(void);
esp_err_t ble_dual_probe_set_simulation(ble_dual_probe_sim_mode_t mode, uint16_t rate_hz);
const char *ble_dual_probe_sim_mode_string(ble_dual_probe_sim_mode_t mode);
void ble_dual_probe_get_metrics(ble_dual_probe_metrics_t *out_metrics);
void ble_dual_probe_format_status_json(char *out, size_t out_len);
const char *ble_dual_probe_state_string(void);
