#include <ctype.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "esp_err.h"
#include "esp_system.h"
#include "app_log.h"
#include "ble_central.h"
#include "ble_dual_probe.h"
#include "device_config.h"
#include "hid_report.h"
#include "report_mapper.h"
#include "report_rate_stats.h"
#include "switch2_gatt.h"
#include "switch2_state.h"
#include "usb_hid_device.h"
#include "usb_switch2_vendor.h"
#include "xbox_paddle_mapper.h"
#include "control_protocol.h"

static const char *TAG = "control";

#define RAW02_HEX_LEFT_RIGHT_LEN 64
#define RAW02_HEX_FULL_LEN 128
#define RAW02_HEX_MAX_LEN RAW02_HEX_FULL_LEN
#define RAW02_PAYLOAD_LEN 64
#define RAW02_LEFT_OFFSET 1
#define RAW02_RIGHT_OFFSET 17

static void trim(char *text)
{
    int len = (int)strlen(text);
    while (len > 0 && isspace((unsigned char)text[len - 1])) {
        text[--len] = 0;
    }
    int start = 0;
    while (text[start] && isspace((unsigned char)text[start])) {
        start++;
    }
    if (start > 0) {
        memmove(text, text + start, strlen(text + start) + 1);
    }
}

static bool parse_long_token(const char **cursor, long *value)
{
    const char *p = *cursor;
    while (*p && isspace((unsigned char)*p)) {
        p++;
    }
    if (!*p) {
        return false;
    }

    char *end = NULL;
    long parsed = strtol(p, &end, 10);
    if (end == p) {
        return false;
    }
    while (*end && isspace((unsigned char)*end)) {
        end++;
    }

    *cursor = end;
    *value = parsed;
    return true;
}

static bool copy_trimmed_arg(const char *src, char *out, size_t out_len)
{
    if (!src || !out || out_len == 0) {
        return false;
    }

    while (*src && isspace((unsigned char)*src)) {
        src++;
    }

    size_t len = strlen(src);
    while (len > 0 && isspace((unsigned char)src[len - 1])) {
        len--;
    }
    if (len >= out_len) {
        return false;
    }

    memcpy(out, src, len);
    out[len] = 0;
    return true;
}

static bool read_token(const char **cursor, char *out, size_t out_len)
{
    if (!cursor || !*cursor || !out || out_len == 0) {
        return false;
    }

    const char *p = *cursor;
    while (*p && isspace((unsigned char)*p)) {
        p++;
    }
    if (!*p) {
        return false;
    }

    size_t len = 0;
    while (p[len] && !isspace((unsigned char)p[len])) {
        len++;
    }
    if (len == 0 || len >= out_len) {
        return false;
    }

    memcpy(out, p, len);
    out[len] = 0;
    p += len;
    while (*p && isspace((unsigned char)*p)) {
        p++;
    }
    *cursor = p;
    return true;
}

static bool token_equals_ci(const char *left, const char *right)
{
    if (!left || !right) {
        return false;
    }
    while (*left && *right) {
        if (tolower((unsigned char)*left) != tolower((unsigned char)*right)) {
            return false;
        }
        left++;
        right++;
    }
    return *left == 0 && *right == 0;
}

static void xbox_paddle_status_json(char *out, size_t out_len)
{
    xbox_paddle_binding_t left;
    xbox_paddle_binding_t right;
    char left_targets[96];
    char right_targets[96];

    xbox_paddle_mapper_get(XBOX_PADDLE_SIDE_LEFT, &left);
    xbox_paddle_mapper_get(XBOX_PADDLE_SIDE_RIGHT, &right);
    xbox_paddle_mapper_format_targets(left.target_mask, left_targets, sizeof(left_targets));
    xbox_paddle_mapper_format_targets(right.target_mask, right_targets, sizeof(right_targets));

    snprintf(out,
             out_len,
             "\"xbox_paddle_left_enabled\":%s,"
             "\"xbox_paddle_left_mode\":\"%s\","
             "\"xbox_paddle_left_targets\":\"%s\","
             "\"xbox_paddle_left_tap_ms\":%u,"
             "\"xbox_paddle_left_turbo_on_ms\":%u,"
             "\"xbox_paddle_left_turbo_off_ms\":%u,"
             "\"xbox_paddle_right_enabled\":%s,"
             "\"xbox_paddle_right_mode\":\"%s\","
             "\"xbox_paddle_right_targets\":\"%s\","
             "\"xbox_paddle_right_tap_ms\":%u,"
             "\"xbox_paddle_right_turbo_on_ms\":%u,"
             "\"xbox_paddle_right_turbo_off_ms\":%u",
             left.enabled ? "true" : "false",
             xbox_paddle_mapper_action_string(left.action),
             left_targets,
             (unsigned)left.tap_ms,
             (unsigned)left.turbo_on_ms,
             (unsigned)left.turbo_off_ms,
             right.enabled ? "true" : "false",
             xbox_paddle_mapper_action_string(right.action),
             right_targets,
             (unsigned)right.tap_ms,
             (unsigned)right.turbo_on_ms,
             (unsigned)right.turbo_off_ms);
}

static int hex_value(char c)
{
    if (c >= '0' && c <= '9') {
        return c - '0';
    }
    if (c >= 'a' && c <= 'f') {
        return c - 'a' + 10;
    }
    if (c >= 'A' && c <= 'F') {
        return c - 'A' + 10;
    }
    return -1;
}

static bool is_hex_string(const char *hex, size_t hex_len)
{
    if (!hex) {
        return false;
    }
    for (size_t i = 0; i < hex_len; i++) {
        if (hex_value(hex[i]) < 0) {
            return false;
        }
    }
    return true;
}

static bool decode_hex_exact(const char *hex, size_t hex_len, uint8_t *out, size_t out_len)
{
    if (!hex || !out || hex_len != out_len * 2 || (hex_len % 2) != 0) {
        return false;
    }
    for (size_t i = 0; i < out_len; i++) {
        int hi = hex_value(hex[i * 2]);
        int lo = hex_value(hex[i * 2 + 1]);
        if (hi < 0 || lo < 0) {
            return false;
        }
        out[i] = (uint8_t)((hi << 4) | lo);
    }
    return true;
}

static void bytes_to_hex(const uint8_t *bytes, size_t len, char *out, size_t out_len)
{
    static const char lut[] = "0123456789abcdef";
    if (!out || out_len == 0) {
        return;
    }
    if (!bytes || out_len < len * 2 + 1) {
        out[0] = 0;
        return;
    }
    for (size_t i = 0; i < len; i++) {
        out[i * 2] = lut[(bytes[i] >> 4) & 0x0f];
        out[i * 2 + 1] = lut[bytes[i] & 0x0f];
    }
    out[len * 2] = 0;
}

static esp_err_t json_ok(char *reply, int reply_len, const char *cmd, const char *extra)
{
    if (extra && extra[0]) {
        snprintf(reply, reply_len, "{\"ok\":true,\"cmd\":\"%s\",%s}", cmd, extra);
    } else {
        snprintf(reply, reply_len, "{\"ok\":true,\"cmd\":\"%s\"}", cmd);
    }
    printf("%s\n", reply);
    return ESP_OK;
}

static esp_err_t json_error(char *reply, int reply_len, const char *cmd, const char *error)
{
    snprintf(reply, reply_len, "{\"ok\":false,\"cmd\":\"%s\",\"error\":\"%s\"}", cmd, error);
    printf("%s\n", reply);
    return ESP_FAIL;
}

void control_protocol_init(void)
{
    APP_LOGI(TAG, "serial control protocol ready on CH343P console");
    APP_LOGI(TAG, "manager integration ready: status, rate, BLE reconnect, and rumble test commands are available");
}

esp_err_t control_protocol_handle_line(const char *line, char *reply, int reply_len)
{
    char cmd[192];
    snprintf(cmd, sizeof(cmd), "%s", line ? line : "");
    trim(cmd);
    APP_LOGI(TAG, "command: %s", cmd);

    if (strcmp(cmd, "status") == 0 || strcmp(cmd, "status lite") == 0) {
        uint32_t live_updates = 0;
        int64_t live_age_us = 0;
        uint16_t rumble_scale_percent = 0;
        uint16_t rumble_hold_ms = 0;
        uint16_t rumble_tick_ms = 0;
        uint8_t rumble_stop_packets = 0;
        report_rate_stats_snapshot_t report_stats;
        switch2_live_stats_t live_stats;
        ble_central_conn_metrics_t ble_conn;
        int32_t gyro_bias[3] = {0, 0, 0};
        bool gyro_bias_valid = report_mapper_get_gyro_bias(gyro_bias);
        uint16_t gyro_cal_remaining = report_mapper_get_gyro_calibration_remaining();
        const char *gyro_bias_state = gyro_bias_valid ? "valid" :
                                      gyro_cal_remaining > 0 ? "calibrating" : "off";
        bool live_valid = switch2_state_get_live(NULL, &live_updates, &live_age_us);
        report_rate_stats_get(&report_stats);
        switch2_state_get_live_stats(&live_stats);
        ble_central_get_conn_metrics(&ble_conn);
        usb_switch2_vendor_get_hd_rumble_tuning(&rumble_scale_percent,
                                                &rumble_hold_ms,
                                                &rumble_tick_ms,
                                                &rumble_stop_packets);
        char xbox_paddle_json[512];
        char dual_probe_json[2400];
        char combined_status_json[3000];
        xbox_paddle_status_json(xbox_paddle_json, sizeof(xbox_paddle_json));
        ble_dual_probe_format_status_json(dual_probe_json, sizeof(dual_probe_json));
        snprintf(combined_status_json,
                 sizeof(combined_status_json),
                 "%s,%s",
                 xbox_paddle_json,
                 dual_probe_json);
        static char extra[16384];
        snprintf(extra, sizeof(extra),
                 "\"mode\":\"%s\",\"usb\":\"%s\",\"hid_out\":%lu,\"hid_out_last\":\"%02x/%02x/%02x/%02x/%u\",\"hid_get\":%lu,\"hid_get_last\":\"%02x/%02x/%u/%u\",\"bulk\":\"%s\",\"bulk_rx\":%lu,\"bulk_tx\":%lu,\"bulk_tx_done\":%lu,\"bulk_tx_sent\":%lu,\"bulk_last\":\"%02x/%02x\",\"bulk_addr\":\"%08lx\",\"bulk_rx_len\":%u,\"bulk_tx_len\":%u,\"bulk_pending\":\"%u/%u\",\"hid_guard\":\"%s\",\"ble\":\"%s\",\"ble_auto\":\"%s\",\"ble_target\":\"%s\",\"ble_conn_interval_units\":%u,\"ble_conn_interval_us\":%lu,\"ble_conn_latency\":%u,\"ble_conn_supervision\":%u,\"ble_conn_update_start_rc\":%d,\"ble_conn_update_status\":%d,\"ble_conn_update_requests\":%lu,\"ble_fast_drop_count\":%lu,\"ble_fast_block_ms\":%lu,\"ble_input_actual_hz\":%lu,\"ble_input_actual_mhz\":%lu,\"ble_input_last_gap_us\":%lu,\"ble_input_max_gap_us\":%lu,\"ble_notify_actual_hz\":%lu,\"ble_notify_actual_mhz\":%lu,\"ble_notify_last_gap_us\":%lu,\"ble_notify_max_gap_us\":%lu,\"ble_notify_min_gap_us\":%lu,\"ble_notify_short_gap_count\":%lu,\"ble_notify_sub_interval_count\":%lu,\"ble_notify_gap_lt3ms\":%lu,\"ble_notify_gap_3_6p5ms\":%lu,\"ble_notify_gap_6p5_10ms\":%lu,\"ble_notify_gap_ge10ms\":%lu,\"ble_notify_sub_raw_unique\":%lu,\"ble_notify_sub_control_unique\":%lu,\"ble_notify_sub_motion_unique\":%lu,\"ble_notify_raw_unique\":%lu,\"ble_notify_raw_repeat\":%lu,\"ble_notify_raw_repeat_streak\":%lu,\"ble_notify_raw_repeat_streak_max\":%lu,\"ble_notify_raw_unique_mhz\":%lu,\"ble_notify_raw_unique_gap_us\":%lu,\"ble_notify_raw_unique_max_gap_us\":%lu,\"ble_notify_parsed_actual_hz\":%lu,\"ble_notify_parsed_actual_mhz\":%lu,\"ble_notify_parsed_last_gap_us\":%lu,\"ble_notify_parsed_max_gap_us\":%lu,\"ble_notify_control_unique\":%lu,\"ble_notify_control_repeat\":%lu,\"ble_notify_control_unique_mhz\":%lu,\"ble_notify_control_unique_gap_us\":%lu,\"ble_notify_control_unique_max_gap_us\":%lu,\"ble_notify_motion_unique\":%lu,\"ble_notify_motion_repeat\":%lu,\"ble_notify_motion_unique_mhz\":%lu,\"ble_notify_motion_unique_gap_us\":%lu,\"ble_notify_motion_unique_max_gap_us\":%lu,\"ble_read_poll_active\":%s,\"ble_read_poll_rate_hz\":%u,\"ble_read_poll_handle\":%u,\"ble_read_poll_start_rc\":%d,\"ble_read_poll_status\":%d,\"ble_read_poll_starts\":%lu,\"ble_read_poll_start_fails\":%lu,\"ble_read_poll_rsp\":%lu,\"ble_read_poll_errors\":%lu,\"ble_read_poll_actual_mhz\":%lu,\"ble_read_poll_gap_us\":%lu,\"ble_read_poll_max_gap_us\":%lu,\"ble_read_poll_raw_unique\":%lu,\"ble_read_poll_raw_repeat\":%lu,\"ble_read_poll_control_unique\":%lu,\"ble_read_poll_control_repeat\":%lu,\"ble_read_poll_control_unique_mhz\":%lu,\"ble_read_poll_control_unique_gap_us\":%lu,\"ble_read_poll_control_unique_max_gap_us\":%lu,\"ble_read_poll_motion_unique\":%lu,\"ble_read_poll_motion_repeat\":%lu,\"ble_read_poll_motion_unique_mhz\":%lu,\"ble_read_poll_motion_unique_gap_us\":%lu,\"ble_read_poll_motion_unique_max_gap_us\":%lu,\"hid\":\"%s\",\"test_mode\":\"%s\",\"imu_passthrough\":\"%s\",\"imu_usb_offset\":%u,\"imu_ble_offset\":%u,\"imu_ble_full\":\"%s\",\"imu_transform\":\"%s\",\"imu_usbtest\":\"%s\",\"gyro_bias\":\"%s\",\"gyro_bias_xyz\":\"%ld/%ld/%ld\",\"gyro_cal_remaining\":%u,\"gyro_scale\":%u,\"gyro_deadband\":%d,\"rate_hz\":%u,\"report_actual_hz\":%lu,\"report_actual_mhz\":%lu,\"report_sent\":%lu,\"report_failed\":%lu,\"report_last_gap_us\":%lu,\"report_max_gap_us\":%lu,\"live\":\"%s\",\"live_updates\":%lu,\"live_age_ms\":%lld,\"rumble\":\"%s\",\"rumble_updates\":%lu,\"rumble_writes\":%lu,\"rumble_stops\":%lu,\"rumble_errors\":%lu,\"rumble_preset_ignored\":%lu,\"rumble_scale_percent\":%u,\"rumble_hold_ms\":%u,\"rumble_tick_ms\":%u,\"rumble_stop_packets\":%u,%s,\"version\":\"%s\"",
                 device_mode_to_string(device_config_get_mode()),
                 usb_hid_device_state_string(),
                 (unsigned long)usb_hid_device_out_count(),
                 usb_hid_device_last_out_report_id(),
                 usb_hid_device_last_out_effective_report_id(),
                 usb_hid_device_last_out_type(),
                 usb_hid_device_last_out_first_byte(),
                 (unsigned)usb_hid_device_last_out_len(),
                 (unsigned long)usb_hid_device_get_count(),
                 usb_hid_device_last_get_report_id(),
                 usb_hid_device_last_get_type(),
                 (unsigned)usb_hid_device_last_get_req_len(),
                 (unsigned)usb_hid_device_last_get_resp_len(),
                 usb_switch2_vendor_mounted() ? "mounted" : "not_mounted",
                 (unsigned long)usb_switch2_vendor_rx_count(),
                 (unsigned long)usb_switch2_vendor_tx_count(),
                 (unsigned long)usb_switch2_vendor_tx_done_count(),
                 (unsigned long)usb_switch2_vendor_last_sent_bytes(),
                 usb_switch2_vendor_last_cmd(),
                 usb_switch2_vendor_last_arg(),
                 (unsigned long)usb_switch2_vendor_last_address(),
                 (unsigned)usb_switch2_vendor_last_rx_len(),
                 (unsigned)usb_switch2_vendor_last_tx_len(),
                 (unsigned)usb_switch2_vendor_pending_offset(),
                 (unsigned)usb_switch2_vendor_pending_len(),
                 usb_switch2_vendor_hid_guard_state(),
                 ble_central_state_string(),
                 device_config_get_ble_autoconnect() ? "on" : "off",
                 device_config_get_ble_target(),
                 (unsigned)ble_conn.interval_units,
                 (unsigned long)ble_conn.interval_units * 1250UL,
                 (unsigned)ble_conn.latency,
                 (unsigned)ble_conn.supervision_timeout,
                 ble_conn.last_update_start_rc,
                 ble_conn.last_update_event_status,
                 (unsigned long)ble_conn.update_request_count,
                 (unsigned long)ble_conn.fast_param_drop_count,
                 (unsigned long)ble_conn.fast_param_block_remaining_ms,
                 (unsigned long)((live_stats.actual_millihz + 500u) / 1000u),
                 (unsigned long)live_stats.actual_millihz,
                 (unsigned long)live_stats.last_gap_us,
                 (unsigned long)live_stats.max_gap_us,
                 (unsigned long)((ble_conn.notify_actual_millihz + 500u) / 1000u),
                 (unsigned long)ble_conn.notify_actual_millihz,
                 (unsigned long)ble_conn.notify_last_gap_us,
                 (unsigned long)ble_conn.notify_max_gap_us,
                 (unsigned long)ble_conn.notify_min_gap_us,
                 (unsigned long)ble_conn.notify_short_gap_count,
                 (unsigned long)ble_conn.notify_sub_interval_count,
                 (unsigned long)ble_conn.notify_gap_lt3ms_count,
                 (unsigned long)ble_conn.notify_gap_3_6p5ms_count,
                 (unsigned long)ble_conn.notify_gap_6p5_10ms_count,
                 (unsigned long)ble_conn.notify_gap_ge10ms_count,
                 (unsigned long)ble_conn.notify_sub_interval_raw_unique_count,
                 (unsigned long)ble_conn.notify_sub_interval_control_unique_count,
                 (unsigned long)ble_conn.notify_sub_interval_motion_unique_count,
                 (unsigned long)ble_conn.notify_raw_unique_count,
                 (unsigned long)ble_conn.notify_raw_repeat_count,
                 (unsigned long)ble_conn.notify_raw_repeat_streak,
                 (unsigned long)ble_conn.notify_raw_repeat_streak_max,
                 (unsigned long)ble_conn.notify_raw_unique_actual_millihz,
                 (unsigned long)ble_conn.notify_raw_unique_last_gap_us,
                 (unsigned long)ble_conn.notify_raw_unique_max_gap_us,
                 (unsigned long)((ble_conn.notify_parsed_actual_millihz + 500u) / 1000u),
                 (unsigned long)ble_conn.notify_parsed_actual_millihz,
                 (unsigned long)ble_conn.notify_parsed_last_gap_us,
                 (unsigned long)ble_conn.notify_parsed_max_gap_us,
                 (unsigned long)ble_conn.notify_control_unique_count,
                 (unsigned long)ble_conn.notify_control_repeat_count,
                 (unsigned long)ble_conn.notify_control_unique_actual_millihz,
                 (unsigned long)ble_conn.notify_control_unique_last_gap_us,
                 (unsigned long)ble_conn.notify_control_unique_max_gap_us,
                 (unsigned long)ble_conn.notify_motion_unique_count,
                 (unsigned long)ble_conn.notify_motion_repeat_count,
                 (unsigned long)ble_conn.notify_motion_unique_actual_millihz,
                 (unsigned long)ble_conn.notify_motion_unique_last_gap_us,
                 (unsigned long)ble_conn.notify_motion_unique_max_gap_us,
                 ble_conn.read_poll_active ? "true" : "false",
                 (unsigned)ble_conn.read_poll_rate_hz,
                 (unsigned)ble_conn.read_poll_handle,
                 ble_conn.read_poll_last_start_rc,
                 ble_conn.read_poll_last_status,
                 (unsigned long)ble_conn.read_poll_start_count,
                 (unsigned long)ble_conn.read_poll_start_fail_count,
                 (unsigned long)ble_conn.read_poll_rsp_count,
                 (unsigned long)ble_conn.read_poll_error_count,
                 (unsigned long)ble_conn.read_poll_actual_millihz,
                 (unsigned long)ble_conn.read_poll_last_gap_us,
                 (unsigned long)ble_conn.read_poll_max_gap_us,
                 (unsigned long)ble_conn.read_poll_raw_unique_count,
                 (unsigned long)ble_conn.read_poll_raw_repeat_count,
                 (unsigned long)ble_conn.read_poll_control_unique_count,
                 (unsigned long)ble_conn.read_poll_control_repeat_count,
                 (unsigned long)ble_conn.read_poll_control_unique_actual_millihz,
                 (unsigned long)ble_conn.read_poll_control_unique_last_gap_us,
                 (unsigned long)ble_conn.read_poll_control_unique_max_gap_us,
                 (unsigned long)ble_conn.read_poll_motion_unique_count,
                 (unsigned long)ble_conn.read_poll_motion_repeat_count,
                 (unsigned long)ble_conn.read_poll_motion_unique_actual_millihz,
                 (unsigned long)ble_conn.read_poll_motion_unique_last_gap_us,
                 (unsigned long)ble_conn.read_poll_motion_unique_max_gap_us,
                 device_config_bridge_running() ? "running" : "stopped",
                 hid_test_mode_to_string(device_config_get_hid_test_mode()),
                 report_mapper_get_nintendo_motion_passthrough() ? "on" : "off",
                 (unsigned)report_mapper_get_nintendo_motion_offset(),
                 (unsigned)switch2_gatt_get_motion_source_offset(),
                 switch2_gatt_get_motion_full_only() ? "on" : "off",
                 report_mapper_motion_transform_string(report_mapper_get_motion_transform()),
                 report_mapper_motion_usb_test_string(report_mapper_get_motion_usb_test()),
                 gyro_bias_state,
                 (long)gyro_bias[0],
                 (long)gyro_bias[1],
                 (long)gyro_bias[2],
                 (unsigned)gyro_cal_remaining,
                 (unsigned)report_mapper_get_gyro_scale(),
                 (int)report_mapper_get_gyro_deadband(),
                 (unsigned)device_config_get_report_rate_hz(),
                 (unsigned long)((report_stats.actual_millihz + 500u) / 1000u),
                 (unsigned long)report_stats.actual_millihz,
                 (unsigned long)report_stats.sent_total,
                 (unsigned long)report_stats.failed_total,
                 (unsigned long)report_stats.last_gap_us,
                 (unsigned long)report_stats.max_gap_us,
                 live_valid ? "active" : "none",
                 (unsigned long)live_updates,
                 live_valid ? (long long)(live_age_us / 1000) : -1LL,
                 usb_switch2_vendor_hd_rumble_active() ? "active" : "idle",
                 (unsigned long)usb_switch2_vendor_hd_rumble_update_count(),
                 (unsigned long)usb_switch2_vendor_hd_rumble_write_count(),
                 (unsigned long)usb_switch2_vendor_hd_rumble_stop_count(),
                 (unsigned long)usb_switch2_vendor_hd_rumble_error_count(),
                 (unsigned long)usb_switch2_vendor_hd_rumble_preset_ignored_count(),
                 (unsigned)rumble_scale_percent,
                 (unsigned)rumble_hold_ms,
                 (unsigned)rumble_tick_ms,
                 (unsigned)rumble_stop_packets,
                 combined_status_json,
                 device_config_get_version());
        return json_ok(reply, reply_len, "status", extra);
    }
    if (strcmp(cmd, "hidguard arm") == 0) {
        usb_switch2_vendor_arm_hid_guard();
        return json_ok(reply, reply_len, "hidguard", "\"state\":\"active\"");
    }
    if (strcmp(cmd, "hidguard release") == 0) {
        usb_switch2_vendor_release_hid_guard();
        return json_ok(reply, reply_len, "hidguard", "\"state\":\"done\"");
    }
    if (strcmp(cmd, "mode generic") == 0) {
        esp_err_t err = device_config_save_mode(GENERIC_HID_MODE);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "mode", "failed to save generic mode");
        }
        return json_ok(reply, reply_len, "mode", "\"mode\":\"generic\",\"saved\":true,\"reboot_required\":true,\"note\":\"run reboot, then replug native USB if needed\"");
    }
    if (strcmp(cmd, "mode nintendo") == 0) {
        esp_err_t err = device_config_save_mode(NINTENDO_EXPERIMENT_MODE);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "mode", "failed to save nintendo mode");
        }
        return json_ok(reply, reply_len, "mode", "\"mode\":\"nintendo\",\"saved\":true,\"experimental\":true,\"reboot_required\":true,\"note\":\"run reboot, then replug native USB if needed\"");
    }
    if (strcmp(cmd, "mode xinput") == 0 || strcmp(cmd, "mode xbox") == 0) {
        esp_err_t err = device_config_save_mode(XINPUT_EXPERIMENT_MODE);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "mode", "failed to save xinput mode");
        }
        return json_ok(reply, reply_len, "mode", "\"mode\":\"xinput\",\"saved\":true,\"experimental\":true,\"reboot_required\":true,\"note\":\"run reboot, then replug native USB if needed\"");
    }
    if (strcmp(cmd, "mode dual") == 0 ||
        strcmp(cmd, "mode dual_pro2") == 0 ||
        strcmp(cmd, "mode dualpro2") == 0) {
        esp_err_t err = device_config_save_mode(DUAL_PRO2_EXPERIMENT_MODE);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "mode", "failed to save dual Pro2 mode");
        }
        return json_ok(reply, reply_len, "mode", "\"mode\":\"dual_pro2\",\"saved\":true,\"experimental\":true,\"reboot_required\":true,\"note\":\"run reboot, then replug native USB if needed\"");
    }
    if (strcmp(cmd, "start") == 0) {
        device_config_set_bridge_running(true);
        return json_ok(reply, reply_len, "start", "\"hid\":\"running\"");
    }
    if (strcmp(cmd, "stop") == 0) {
        device_config_set_bridge_running(false);
        return json_ok(reply, reply_len, "stop", "\"hid\":\"stopped\"");
    }
    if (strncmp(cmd, "rate ", 5) == 0 || strncmp(cmd, "report rate ", 12) == 0) {
        const char *rate_text = cmd[0] == 'r' && cmd[4] == ' ' ? cmd + 5 : cmd + 12;
        char *end = NULL;
        long rate_hz = strtol(rate_text, &end, 10);
        while (end && *end && isspace((unsigned char)*end)) {
            end++;
        }
        if (rate_text == end || rate_hz < 20 || rate_hz > 1000 || (end && *end)) {
            return json_error(reply, reply_len, "rate", "usage: rate <20..1000>");
        }
        esp_err_t err = device_config_save_report_rate_hz((uint16_t)rate_hz);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "rate", "failed to save report rate");
        }
        char extra[64];
        snprintf(extra, sizeof(extra), "\"rate_hz\":%u,\"saved\":true", (unsigned)device_config_get_report_rate_hz());
        return json_ok(reply, reply_len, "rate", extra);
    }
    if (strcmp(cmd, "reboot") == 0) {
        json_ok(reply, reply_len, "reboot", "\"note\":\"restarting\"");
        esp_restart();
        return ESP_OK;
    }
    if (strcmp(cmd, "loglevel debug") == 0) {
        app_log_set_debug(true);
        return json_ok(reply, reply_len, "loglevel", "\"level\":\"debug\"");
    }
    if (strcmp(cmd, "loglevel info") == 0) {
        app_log_set_debug(false);
        return json_ok(reply, reply_len, "loglevel", "\"level\":\"info\"");
    }
    if (strcmp(cmd, "ble scan") == 0) {
        return ble_central_start_scan() == ESP_OK ?
            json_ok(reply, reply_len, "ble scan", "\"ble\":\"scanning\"") :
            json_error(reply, reply_len, "ble scan", "scan start failed");
    }
    if (strcmp(cmd, "ble list") == 0 || strcmp(cmd, "ble candidates") == 0) {
        static char extra[1800];
        ble_central_format_scan_results_json(extra, sizeof(extra));
        return json_ok(reply, reply_len, "ble list", extra);
    }
    if (strncmp(cmd, "ble connect", 11) == 0) {
        char target[96];
        if (!copy_trimmed_arg(cmd + 11, target, sizeof(target))) {
            return json_error(reply, reply_len, "ble connect", "BLE target is too long");
        }
        esp_err_t err = ble_central_connect(target[0] ? target : NULL);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "ble connect", "connect start failed; run ble scan and use ble connect last, ble connect <addr>, or ble connect <name>");
        }
        return json_ok(reply, reply_len, "ble connect", "\"ble\":\"connecting\"");
    }
    if (strcmp(cmd, "ble reconnect") == 0 || strcmp(cmd, "ble auto connect") == 0) {
        if (device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE) {
            esp_err_t err = ble_dual_probe_start();
            if (err != ESP_OK) {
                return json_error(reply, reply_len, "ble reconnect", "dual Pro2 probe start failed");
            }
            return json_ok(reply, reply_len, "ble reconnect", "\"dual_pro2\":\"scanning\"");
        }
        esp_err_t err = ble_central_reconnect_saved_or_scan();
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "ble reconnect", "reconnect start failed");
        }
        return json_ok(reply, reply_len, "ble reconnect", "\"ble\":\"connecting\"");
    }
    if (strcmp(cmd, "ble multiprobe") == 0 ||
        strcmp(cmd, "ble multi probe") == 0 ||
        strcmp(cmd, "ble fast") == 0 ||
        strcmp(cmd, "ble interval fast") == 0 ||
        strcmp(cmd, "ble event probe") == 0) {
        esp_err_t err = ble_central_start_multi_report_probe();
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "ble multiprobe", "multi-report probe requires an active BLE connection");
        }
        return json_ok(reply,
                       reply_len,
                       "ble multiprobe",
                       "\"ble_multi_probe\":\"started\",\"interval_request\":\"fast_7p5ms\",\"note\":\"move sticks hard; inspect sub_control_unique and gap buckets\"");
    }
    if (strcmp(cmd, "ble multiprobe reset") == 0 ||
        strcmp(cmd, "ble probe reset") == 0) {
        ble_central_reset_multi_probe_metrics();
        return json_ok(reply, reply_len, "ble multiprobe reset", "\"ble_multi_probe\":\"reset\"");
    }
    if (strcmp(cmd, "ble auto on") == 0 || strcmp(cmd, "ble autoconnect on") == 0) {
        esp_err_t err = device_config_save_ble_autoconnect(true);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "ble auto", "failed to save BLE autoconnect");
        }
        ble_central_start_auto_reconnect();
        return json_ok(reply, reply_len, "ble auto", "\"ble_auto\":\"on\"");
    }
    if (strcmp(cmd, "ble auto off") == 0 || strcmp(cmd, "ble autoconnect off") == 0) {
        esp_err_t err = device_config_save_ble_autoconnect(false);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "ble auto", "failed to save BLE autoconnect");
        }
        return json_ok(reply, reply_len, "ble auto", "\"ble_auto\":\"off\"");
    }
    if (strcmp(cmd, "ble forget") == 0 || strcmp(cmd, "ble target clear") == 0) {
        ble_central_disconnect();
        esp_err_t err = device_config_save_ble_target("");
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "ble forget", "failed to clear BLE target");
        }
        return json_ok(reply, reply_len, "ble forget", "\"ble\":\"idle\",\"ble_target\":\"\"");
    }
    if (strncmp(cmd, "ble target ", 11) == 0) {
        char target[96];
        if (!copy_trimmed_arg(cmd + 11, target, sizeof(target))) {
            return json_error(reply, reply_len, "ble target", "BLE target is too long");
        }
        esp_err_t err = device_config_save_ble_target(target);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "ble target", "failed to save BLE target");
        }
        char extra[96];
        snprintf(extra, sizeof(extra), "\"ble_target\":\"%s\"", device_config_get_ble_target());
        return json_ok(reply, reply_len, "ble target", extra);
    }
    if (strcmp(cmd, "ble disconnect") == 0) {
        if (device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE) {
            ble_dual_probe_stop();
            return json_ok(reply, reply_len, "ble disconnect", "\"dual_pro2\":\"stopped\"");
        }
        ble_central_disconnect();
        return json_ok(reply, reply_len, "ble disconnect", "\"ble\":\"idle\"");
    }
    if (strcmp(cmd, "dual start") == 0 ||
        strcmp(cmd, "dual pro2 start") == 0 ||
        strcmp(cmd, "dual scan") == 0) {
        esp_err_t err = ble_dual_probe_start();
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "dual start", "dual Pro2 probe start failed");
        }
        return json_ok(reply, reply_len, "dual start", "\"dual_pro2\":\"scanning\"");
    }
    if (strcmp(cmd, "dual stop") == 0 ||
        strcmp(cmd, "dual pro2 stop") == 0) {
        ble_dual_probe_stop();
        return json_ok(reply, reply_len, "dual stop", "\"dual_pro2\":\"stopped\"");
    }
    if (strcmp(cmd, "dual sim off") == 0 ||
        strcmp(cmd, "dual simulation off") == 0) {
        esp_err_t err = ble_dual_probe_set_simulation(BLE_DUAL_PROBE_SIM_OFF, 0);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "dual sim", "failed to disable simulation");
        }
        return json_ok(reply, reply_len, "dual sim", "\"dual_sim_mode\":\"off\"");
    }
    if (strcmp(cmd, "dual sim mirror") == 0 ||
        strcmp(cmd, "dual simulation mirror") == 0 ||
        strcmp(cmd, "dual mirror") == 0) {
        esp_err_t err = ble_dual_probe_set_simulation(BLE_DUAL_PROBE_SIM_MIRROR, 0);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "dual sim", "failed to enable mirror simulation");
        }
        return json_ok(reply, reply_len, "dual sim", "\"dual_sim_mode\":\"mirror\",\"note\":\"pad1 mirrors the first real Pro2 notify stream\"");
    }
    if (strncmp(cmd, "dual sim synthetic", 18) == 0 ||
        strncmp(cmd, "dual simulation synthetic", 25) == 0 ||
        strncmp(cmd, "dual synthetic", 14) == 0) {
        const char *p = strncmp(cmd, "dual simulation synthetic", 25) == 0 ? cmd + 25 :
                        strncmp(cmd, "dual sim synthetic", 18) == 0 ? cmd + 18 :
                        cmd + 14;
        long rate = 133;
        while (*p && isspace((unsigned char)*p)) {
            p++;
        }
        if (*p && (!parse_long_token(&p, &rate) || *p || rate < 20 || rate > 250)) {
            return json_error(reply, reply_len, "dual sim", "usage: dual sim synthetic [20..250]");
        }
        esp_err_t err = ble_dual_probe_set_simulation(BLE_DUAL_PROBE_SIM_SYNTHETIC, (uint16_t)rate);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "dual sim", "failed to enable synthetic simulation");
        }
        char extra[96];
        snprintf(extra, sizeof(extra), "\"dual_sim_mode\":\"synthetic\",\"dual_sim_rate_hz\":%ld", rate);
        return json_ok(reply, reply_len, "dual sim", extra);
    }
    if (strcmp(cmd, "dual status") == 0 ||
        strcmp(cmd, "dual pro2 status") == 0) {
        static char dual_extra[2400];
        ble_dual_probe_format_status_json(dual_extra, sizeof(dual_extra));
        return json_ok(reply, reply_len, "dual status", dual_extra);
    }
    if (strncmp(cmd, "imu debug on", 12) == 0 || strncmp(cmd, "ble raw on", 10) == 0) {
        const char *p = cmd[0] == 'i' ? cmd + 12 : cmd + 10;
        long every = 32;
        while (*p && isspace((unsigned char)*p)) {
            p++;
        }
        if (*p && (!parse_long_token(&p, &every) || *p || every < 1 || every > 512)) {
            return json_error(reply, reply_len, "imu debug", "usage: imu debug on [every 1..512]");
        }
        ble_central_set_imu_debug(true, (uint32_t)every);
        char extra[80];
        snprintf(extra, sizeof(extra), "\"imu_debug\":\"on\",\"every\":%lu", (unsigned long)every);
        return json_ok(reply, reply_len, "imu debug", extra);
    }
    if (strcmp(cmd, "imu debug off") == 0 || strcmp(cmd, "ble raw off") == 0) {
        ble_central_set_imu_debug(false, 32);
        return json_ok(reply, reply_len, "imu debug", "\"imu_debug\":\"off\"");
    }
    if (strcmp(cmd, "imu passthrough on") == 0 || strcmp(cmd, "motion passthrough on") == 0) {
        report_mapper_set_nintendo_motion_passthrough(true);
        char extra[96];
        snprintf(extra, sizeof(extra),
                 "\"imu_passthrough\":\"on\",\"usb_offset\":%u,\"size\":%u",
                 (unsigned)report_mapper_get_nintendo_motion_offset(),
                 (unsigned)REPORT_MAPPER_NINTENDO_MOTION_SAMPLE_SIZE);
        return json_ok(reply, reply_len, "imu passthrough", extra);
    }
    if (strcmp(cmd, "imu passthrough off") == 0 || strcmp(cmd, "motion passthrough off") == 0) {
        report_mapper_set_nintendo_motion_passthrough(false);
        return json_ok(reply, reply_len, "imu passthrough", "\"imu_passthrough\":\"off\"");
    }
    if (strcmp(cmd, "imu passthrough") == 0 || strcmp(cmd, "motion passthrough") == 0) {
        char extra[96];
        snprintf(extra, sizeof(extra), "\"imu_passthrough\":\"%s\",\"usb_offset\":%u,\"size\":%u",
                 report_mapper_get_nintendo_motion_passthrough() ? "on" : "off",
                 (unsigned)report_mapper_get_nintendo_motion_offset(),
                 (unsigned)REPORT_MAPPER_NINTENDO_MOTION_SAMPLE_SIZE);
        return json_ok(reply, reply_len, "imu passthrough", extra);
    }
    if (strncmp(cmd, "imu passthrough offset ", 23) == 0 || strncmp(cmd, "motion passthrough offset ", 27) == 0) {
        const char *p = cmd[0] == 'i' ? cmd + 23 : cmd + 27;
        long offset = 0;
        if (!parse_long_token(&p, &offset) || *p || offset < 0 ||
            offset > (NINTENDO_REPORT_SIZE - REPORT_MAPPER_NINTENDO_MOTION_SAMPLE_SIZE)) {
            return json_error(reply, reply_len, "imu passthrough offset", "usage: imu passthrough offset <0..52>");
        }
        if (!report_mapper_set_nintendo_motion_offset((uint8_t)offset)) {
            return json_error(reply, reply_len, "imu passthrough offset", "offset does not fit 12-byte motion sample");
        }
        char extra[96];
        snprintf(extra, sizeof(extra), "\"imu_passthrough\":\"%s\",\"usb_offset\":%ld,\"size\":%u",
                 report_mapper_get_nintendo_motion_passthrough() ? "on" : "off",
                 offset,
                 (unsigned)REPORT_MAPPER_NINTENDO_MOTION_SAMPLE_SIZE);
        return json_ok(reply, reply_len, "imu passthrough offset", extra);
    }
    if (strcmp(cmd, "imu source") == 0 || strcmp(cmd, "motion source") == 0) {
        char extra[128];
        snprintf(extra, sizeof(extra),
                 "\"ble_offset\":%u,\"full_only\":\"%s\",\"usb_offset\":%u,\"transform\":\"%s\"",
                 (unsigned)switch2_gatt_get_motion_source_offset(),
                 switch2_gatt_get_motion_full_only() ? "on" : "off",
                 (unsigned)report_mapper_get_nintendo_motion_offset(),
                 report_mapper_motion_transform_string(report_mapper_get_motion_transform()));
        return json_ok(reply, reply_len, "imu source", extra);
    }
    if (strncmp(cmd, "imu source offset ", 18) == 0 || strncmp(cmd, "motion source offset ", 21) == 0) {
        const char *p = cmd[0] == 'i' ? cmd + 18 : cmd + 21;
        long offset = 0;
        if (!parse_long_token(&p, &offset) || *p || offset < 0 || offset > 51) {
            return json_error(reply, reply_len, "imu source offset", "usage: imu source offset <0..51>");
        }
        if (!switch2_gatt_set_motion_source_offset((uint8_t)offset)) {
            return json_error(reply, reply_len, "imu source offset", "offset does not fit BLE notify motion block");
        }
        char extra[96];
        snprintf(extra, sizeof(extra), "\"ble_offset\":%ld,\"full_only\":\"%s\"",
                 offset,
                 switch2_gatt_get_motion_full_only() ? "on" : "off");
        return json_ok(reply, reply_len, "imu source offset", extra);
    }
    if (strcmp(cmd, "imu source full") == 0 || strcmp(cmd, "motion source full") == 0) {
        switch2_gatt_set_motion_full_only(true);
        return json_ok(reply, reply_len, "imu source", "\"full_only\":\"on\"");
    }
    if (strcmp(cmd, "imu source any") == 0 || strcmp(cmd, "motion source any") == 0) {
        switch2_gatt_set_motion_full_only(false);
        return json_ok(reply, reply_len, "imu source", "\"full_only\":\"off\"");
    }
    if (strncmp(cmd, "imu transform ", 14) == 0 || strncmp(cmd, "motion transform ", 17) == 0) {
        const char *mode = cmd[0] == 'i' ? cmd + 14 : cmd + 17;
        report_mapper_motion_transform_t transform;
        if (strcmp(mode, "raw") == 0) {
            transform = REPORT_MAPPER_MOTION_RAW;
        } else if (strcmp(mode, "swap") == 0) {
            transform = REPORT_MAPPER_MOTION_SWAP_HALVES;
        } else if (strcmp(mode, "rev") == 0) {
            transform = REPORT_MAPPER_MOTION_REVERSE_SAMPLES;
        } else if (strcmp(mode, "swaprev") == 0) {
            transform = REPORT_MAPPER_MOTION_SWAP_REVERSE;
        } else {
            return json_error(reply, reply_len, "imu transform", "usage: imu transform raw|swap|rev|swaprev");
        }
        if (!report_mapper_set_motion_transform(transform)) {
            return json_error(reply, reply_len, "imu transform", "invalid transform");
        }
        char extra[64];
        snprintf(extra, sizeof(extra), "\"transform\":\"%s\"",
                 report_mapper_motion_transform_string(report_mapper_get_motion_transform()));
        return json_ok(reply, reply_len, "imu transform", extra);
    }
    if (strcmp(cmd, "imu calibrate") == 0 || strcmp(cmd, "gyro calibrate") == 0 ||
        strncmp(cmd, "imu calibrate ", 14) == 0 || strncmp(cmd, "gyro calibrate ", 15) == 0) {
        const char *p = cmd[0] == 'i' ? cmd + 13 : cmd + 14;
        long samples = 512;
        while (*p && isspace((unsigned char)*p)) {
            p++;
        }
        if (*p && (!parse_long_token(&p, &samples) || *p || samples < 16 || samples > 4000)) {
            return json_error(reply, reply_len, "imu calibrate", "usage: imu calibrate [16..4000]");
        }
        report_mapper_start_gyro_calibration((uint16_t)samples);
        char extra[96];
        snprintf(extra, sizeof(extra), "\"gyro_cal_remaining\":%ld,\"gyro_scale\":%u,\"gyro_deadband\":%d",
                 samples,
                 (unsigned)report_mapper_get_gyro_scale(),
                 (int)report_mapper_get_gyro_deadband());
        return json_ok(reply, reply_len, "imu calibrate", extra);
    }
    if (strncmp(cmd, "imu scale ", 10) == 0 || strncmp(cmd, "gyro scale ", 11) == 0) {
        const char *p = cmd[0] == 'i' ? cmd + 10 : cmd + 11;
        long scale = 0;
        if (!parse_long_token(&p, &scale) || *p || scale < 1 || scale > 512) {
            return json_error(reply, reply_len, "imu scale", "usage: imu scale <1..512>");
        }
        if (!report_mapper_set_gyro_scale((uint16_t)scale)) {
            return json_error(reply, reply_len, "imu scale", "invalid scale");
        }
        char extra[64];
        snprintf(extra, sizeof(extra), "\"gyro_scale\":%ld", scale);
        return json_ok(reply, reply_len, "imu scale", extra);
    }
    if (strncmp(cmd, "imu deadband ", 13) == 0 || strncmp(cmd, "gyro deadband ", 14) == 0) {
        const char *p = cmd[0] == 'i' ? cmd + 13 : cmd + 14;
        long deadband = 0;
        if (!parse_long_token(&p, &deadband) || *p || deadband < 0 || deadband > 32767) {
            return json_error(reply, reply_len, "imu deadband", "usage: imu deadband <0..32767>");
        }
        if (!report_mapper_set_gyro_deadband((int16_t)deadband)) {
            return json_error(reply, reply_len, "imu deadband", "invalid deadband");
        }
        char extra[64];
        snprintf(extra, sizeof(extra), "\"gyro_deadband\":%ld", deadband);
        return json_ok(reply, reply_len, "imu deadband", extra);
    }
    if (strcmp(cmd, "imu usbtest") == 0 || strcmp(cmd, "motion usbtest") == 0) {
        char extra[64];
        snprintf(extra, sizeof(extra), "\"usbtest\":\"%s\"",
                 report_mapper_motion_usb_test_string(report_mapper_get_motion_usb_test()));
        return json_ok(reply, reply_len, "imu usbtest", extra);
    }
    if (strncmp(cmd, "imu usbtest ", 12) == 0 || strncmp(cmd, "motion usbtest ", 15) == 0) {
        const char *mode = cmd[0] == 'i' ? cmd + 12 : cmd + 15;
        report_mapper_motion_usb_test_t test_mode;
        if (strcmp(mode, "off") == 0) {
            test_mode = REPORT_MAPPER_MOTION_USB_TEST_OFF;
        } else if (strcmp(mode, "gyro2") == 0) {
            test_mode = REPORT_MAPPER_MOTION_USB_TEST_GYRO_SECOND;
        } else if (strcmp(mode, "gyro1") == 0) {
            test_mode = REPORT_MAPPER_MOTION_USB_TEST_GYRO_FIRST;
        } else if (strcmp(mode, "all") == 0) {
            test_mode = REPORT_MAPPER_MOTION_USB_TEST_ALL_AXES;
        } else {
            return json_error(reply, reply_len, "imu usbtest", "usage: imu usbtest off|gyro2|gyro1|all");
        }
        if (!report_mapper_set_motion_usb_test(test_mode)) {
            return json_error(reply, reply_len, "imu usbtest", "invalid test mode");
        }
        char extra[80];
        snprintf(extra, sizeof(extra), "\"usbtest\":\"%s\",\"usb_offset\":%u",
                 report_mapper_motion_usb_test_string(report_mapper_get_motion_usb_test()),
                 (unsigned)report_mapper_get_nintendo_motion_offset());
        return json_ok(reply, reply_len, "imu usbtest", extra);
    }
    if (strcmp(cmd, "rumble config") == 0 || strcmp(cmd, "rumble tune") == 0 ||
        strncmp(cmd, "rumble tune ", 12) == 0) {
        uint16_t scale_percent = 0;
        uint16_t hold_ms = 0;
        uint16_t tick_ms = 0;
        uint8_t stop_packets = 0;
        usb_switch2_vendor_get_hd_rumble_tuning(&scale_percent, &hold_ms, &tick_ms, &stop_packets);

        if (strncmp(cmd, "rumble tune ", 12) == 0) {
            const char *p = cmd + 12;
            long scale_arg = 0;
            long hold_arg = 0;
            long tick_arg = 0;
            long stop_arg = 0;
            if (!parse_long_token(&p, &scale_arg) ||
                !parse_long_token(&p, &hold_arg) ||
                !parse_long_token(&p, &tick_arg) ||
                !parse_long_token(&p, &stop_arg) ||
                *p ||
                scale_arg < 10 || scale_arg > 250 ||
                hold_arg < 50 || hold_arg > 1000 ||
                tick_arg < 5 || tick_arg > 50 ||
                stop_arg < 1 || stop_arg > 8) {
                return json_error(reply, reply_len, "rumble tune",
                                  "usage: rumble tune <scale_percent 10..250> <hold_ms 50..1000> <tick_ms 5..50> <stop_packets 1..8>");
            }
            usb_switch2_vendor_set_hd_rumble_tuning((uint16_t)scale_arg,
                                                    (uint16_t)hold_arg,
                                                    (uint16_t)tick_arg,
                                                    (uint8_t)stop_arg);
            usb_switch2_vendor_get_hd_rumble_tuning(&scale_percent, &hold_ms, &tick_ms, &stop_packets);
        }

        char extra[128];
        snprintf(extra, sizeof(extra),
                 "\"scale_percent\":%u,\"hold_ms\":%u,\"tick_ms\":%u,\"stop_packets\":%u",
                 (unsigned)scale_percent,
                 (unsigned)hold_ms,
                 (unsigned)tick_ms,
                 (unsigned)stop_packets);
        return json_ok(reply, reply_len, "rumble tune", extra);
    }
    if (strcmp(cmd, "rumble hdtest") == 0 || strcmp(cmd, "rumble test") == 0) {
        usb_switch2_vendor_start_hd_rumble_self_test();
        return json_ok(reply, reply_len, "rumble hdtest", "\"rumble\":\"active\",\"mode\":\"hd_stream_self_test\"");
    }
    if (strncmp(cmd, "rumble hold ", 12) == 0) {
        const char *p = cmd + 12;
        long hold_ms = 0;
        if (!parse_long_token(&p, &hold_ms) || *p || hold_ms < 100 || hold_ms > 10000) {
            return json_error(reply, reply_len, "rumble hold", "usage: rumble hold <ms 100..10000>");
        }
        usb_switch2_vendor_start_hd_rumble_self_test_ms((uint16_t)hold_ms);
        char extra[80];
        snprintf(extra, sizeof(extra), "\"rumble\":\"active\",\"mode\":\"hd_stream_hold\",\"hold_ms\":%ld", hold_ms);
        return json_ok(reply, reply_len, "rumble hold", extra);
    }
    if (strncmp(cmd, "rumble raw02 ", 13) == 0) {
        const char *hex_start = cmd + strlen("rumble raw02 ");
        while (*hex_start && isspace((unsigned char)*hex_start)) {
            hex_start++;
        }
        size_t hex_len = strlen(hex_start);
        while (hex_len > 0 && isspace((unsigned char)hex_start[hex_len - 1])) {
            hex_len--;
        }

        if (hex_len > RAW02_HEX_MAX_LEN) {
            APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=hex_too_long len=%u",
                     (unsigned)hex_len);
            return json_error(reply, reply_len, "rumble raw02", "hex payload is too long");
        }
        if ((hex_len % 2) != 0) {
            APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=odd_hex_len len=%u",
                     (unsigned)hex_len);
            return json_error(reply, reply_len, "rumble raw02", "hex must have an even number of characters");
        }
        if (hex_len != RAW02_HEX_LEFT_RIGHT_LEN && hex_len != RAW02_HEX_FULL_LEN) {
            APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=invalid_hex_len len=%u",
                     (unsigned)hex_len);
            return json_error(reply, reply_len, "rumble raw02", "usage: rumble raw02 <64 hex left+right or 128 hex full payload>");
        }

        char raw_hex[RAW02_HEX_MAX_LEN + 1];
        memcpy(raw_hex, hex_start, hex_len);
        raw_hex[hex_len] = 0;

        if (!is_hex_string(raw_hex, hex_len)) {
            APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=non_hex");
            return json_error(reply, reply_len, "rumble raw02", "hex contains non-hex characters");
        }

        uint8_t payload[RAW02_PAYLOAD_LEN];
        const char *mode = NULL;
        if (hex_len == RAW02_HEX_LEFT_RIGHT_LEN) {
            uint8_t left_right[32];
            (void)decode_hex_exact(raw_hex, hex_len, left_right, sizeof(left_right));
            memset(payload, 0, sizeof(payload));
            payload[0] = 0x02;
            memcpy(payload + RAW02_LEFT_OFFSET, left_right, 16);
            memcpy(payload + RAW02_RIGHT_OFFSET, left_right + 16, 16);
            mode = "left_right_16";
        } else {
            int report_id = (hex_value(raw_hex[0]) << 4) | hex_value(raw_hex[1]);
            if (report_id != 0x02) {
                APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=invalid_report_id report_id=0x%02x",
                         (unsigned)report_id);
                return json_error(reply, reply_len, "rumble raw02", "full payload must start with report_id 0x02");
            }
            (void)decode_hex_exact(raw_hex, hex_len, payload, sizeof(payload));
            mode = "full_payload";
        }

        char left_hex[33];
        char right_hex[33];
        char payload_hex[129];
        bytes_to_hex(payload + RAW02_LEFT_OFFSET, 16, left_hex, sizeof(left_hex));
        bytes_to_hex(payload + RAW02_RIGHT_OFFSET, 16, right_hex, sizeof(right_hex));
        bytes_to_hex(payload, sizeof(payload), payload_hex, sizeof(payload_hex));

        APP_LOGI(TAG, "[RUMBLE_RAW02] mode=%s", mode);
        APP_LOGI(TAG, "[RUMBLE_RAW02] left=%s", left_hex);
        APP_LOGI(TAG, "[RUMBLE_RAW02] right=%s", right_hex);
        APP_LOGI(TAG, "[RUMBLE_RAW02] payload=%s", payload_hex);

        esp_err_t err = usb_switch2_vendor_send_raw02_payload(payload, sizeof(payload));
        bool sent = err == ESP_OK;
        APP_LOGI(TAG, "[RUMBLE_RAW02] sent=%s error=%s",
                 sent ? "true" : "false",
                 sent ? "none" : esp_err_to_name(err));

        char extra[360];
        snprintf(extra, sizeof(extra),
                 "\"rumble\":\"raw02\",\"mode\":\"%s\",\"left\":\"%s\",\"right\":\"%s\",\"payload\":\"%s\",\"sent\":%s,\"error\":\"%s\"",
                 mode,
                 left_hex,
                 right_hex,
                 payload_hex,
                 sent ? "true" : "false",
                 sent ? "none" : esp_err_to_name(err));
        return sent ?
            json_ok(reply, reply_len, "rumble raw02", extra) :
            json_error(reply, reply_len, "rumble raw02", esp_err_to_name(err));
    }
    if (strcmp(cmd, "rumble stop") == 0) {
        usb_switch2_vendor_stop_hd_rumble();
        return json_ok(reply, reply_len, "rumble stop", "\"rumble\":\"stopping\"");
    }
    if (strcmp(cmd, "xbox paddle status") == 0) {
        char extra[512];
        xbox_paddle_status_json(extra, sizeof(extra));
        return json_ok(reply, reply_len, "xbox paddle status", extra);
    }
    if (strcmp(cmd, "xbox paddle reset") == 0) {
        esp_err_t err = xbox_paddle_mapper_reset();
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "xbox paddle reset", esp_err_to_name(err));
        }
        char extra[512];
        xbox_paddle_status_json(extra, sizeof(extra));
        return json_ok(reply, reply_len, "xbox paddle reset", extra);
    }
    if (strncmp(cmd, "xbox paddle ", 12) == 0) {
        const char *cursor = cmd + 12;
        char side_text[16];
        char action_text[16];
        xbox_paddle_side_t side;
        xbox_paddle_action_t action;

        if (!read_token(&cursor, side_text, sizeof(side_text)) ||
            !xbox_paddle_mapper_parse_side(side_text, &side)) {
            return json_error(reply, reply_len, "xbox paddle", "usage: xbox paddle <left|right|gl|gr> <off|hold|tap|turbo> <targets> [tap_ms|on_ms off_ms]");
        }

        if (!read_token(&cursor, action_text, sizeof(action_text))) {
            return json_error(reply, reply_len, "xbox paddle", "usage: xbox paddle <side> <off|hold|tap|turbo> <targets>");
        }

        xbox_paddle_binding_t binding;
        xbox_paddle_mapper_get(side, &binding);

        if (token_equals_ci(action_text, "off") ||
            token_equals_ci(action_text, "disable") ||
            token_equals_ci(action_text, "disabled")) {
            binding.enabled = false;
            binding.target_mask = 0;
            esp_err_t err = xbox_paddle_mapper_set(side, &binding);
            if (err != ESP_OK) {
                return json_error(reply, reply_len, "xbox paddle", esp_err_to_name(err));
            }
            char extra[512];
            xbox_paddle_status_json(extra, sizeof(extra));
            return json_ok(reply, reply_len, "xbox paddle", extra);
        }

        if (!xbox_paddle_mapper_parse_action(action_text, &action)) {
            return json_error(reply, reply_len, "xbox paddle", "action must be hold, tap, single, turbo, repeat, or off");
        }

        char targets_text[96];
        uint64_t target_mask = 0;
        if (!read_token(&cursor, targets_text, sizeof(targets_text)) ||
            !xbox_paddle_mapper_parse_targets(targets_text, &target_mask)) {
            return json_error(reply, reply_len, "xbox paddle", "target must use Pro2 keys, e.g. B, A, ZR, B+A, ZR+B");
        }

        binding.enabled = target_mask != 0;
        binding.action = action;
        binding.target_mask = target_mask;

        if (action == XBOX_PADDLE_ACTION_TAP) {
            if (*cursor) {
                long tap_ms = 0;
                if (!parse_long_token(&cursor, &tap_ms) || tap_ms < 20 || tap_ms > 1000) {
                    return json_error(reply, reply_len, "xbox paddle", "tap_ms must be 20..1000");
                }
                binding.tap_ms = (uint16_t)tap_ms;
            }
        } else if (action == XBOX_PADDLE_ACTION_TURBO) {
            if (*cursor) {
                long on_ms = 0;
                long off_ms = 0;
                if (!parse_long_token(&cursor, &on_ms) ||
                    !parse_long_token(&cursor, &off_ms) ||
                    on_ms < 20 || on_ms > 1000 ||
                    off_ms < 20 || off_ms > 1000) {
                    return json_error(reply, reply_len, "xbox paddle", "turbo timings must be on_ms off_ms, both 20..1000");
                }
                binding.turbo_on_ms = (uint16_t)on_ms;
                binding.turbo_off_ms = (uint16_t)off_ms;
            }
        }

        if (*cursor) {
            return json_error(reply, reply_len, "xbox paddle", "too many arguments");
        }

        esp_err_t err = xbox_paddle_mapper_set(side, &binding);
        if (err != ESP_OK) {
            return json_error(reply, reply_len, "xbox paddle", esp_err_to_name(err));
        }

        char extra[512];
        xbox_paddle_status_json(extra, sizeof(extra));
        return json_ok(reply, reply_len, "xbox paddle", extra);
    }
    if (strcmp(cmd, "hid test_a") == 0) {
        device_config_set_hid_test_mode(HID_TEST_A_HELD);
        return json_ok(reply, reply_len, "hid test_a", "\"test_mode\":\"a_held\"");
    }
    if (strcmp(cmd, "hid neutral") == 0) {
        device_config_set_hid_test_mode(HID_TEST_NEUTRAL);
        return json_ok(reply, reply_len, "hid neutral", "\"test_mode\":\"neutral\"");
    }
    if (strcmp(cmd, "hid auto_a") == 0) {
        device_config_set_hid_test_mode(HID_TEST_AUTO_A);
        return json_ok(reply, reply_len, "hid auto_a", "\"test_mode\":\"auto_a\"");
    }
    if (strcmp(cmd, "version") == 0) {
        char extra[64];
        snprintf(extra, sizeof(extra), "\"version\":\"%s\"", device_config_get_version());
        return json_ok(reply, reply_len, "version", extra);
    }

    return json_error(reply, reply_len, cmd[0] ? cmd : "empty", "unknown command");
}


