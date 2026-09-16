#include "v55_control_protocol.h"

#include <ctype.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "app_log.h"
#include "ble_central.h"
#include "device_config.h"
#include "dualsense_haptic_audio.h"
#include "dualsense_runtime_stats.h"
#include "esp_err.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "haptic_audio_to_raw02.h"
#include "pro2_rumble_backend.h"
#include "pro2_input_backend.h"
#include "switch2_gatt.h"

#ifndef DS5_PROFILE_NAME
#define DS5_PROFILE_NAME "unknown"
#endif

static const char *TAG = "v5.5_control";

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

static bool parse_long_arg(const char *text, long *out)
{
    if (!text || !out) {
        return false;
    }
    while (*text && isspace((unsigned char)*text)) {
        text++;
    }
    char *end = NULL;
    long value = strtol(text, &end, 10);
    if (end == text) {
        return false;
    }
    while (*end && isspace((unsigned char)*end)) {
        end++;
    }
    if (*end) {
        return false;
    }
    *out = value;
    return true;
}

static bool parse_float_arg(const char *text, float *out)
{
    if (!text || !out) {
        return false;
    }
    while (*text && isspace((unsigned char)*text)) {
        text++;
    }
    char *end = NULL;
    float value = strtof(text, &end);
    if (end == text) {
        return false;
    }
    while (*end && isspace((unsigned char)*end)) {
        end++;
    }
    if (*end) {
        return false;
    }
    *out = value;
    return true;
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

static void bytes_to_hex(const uint8_t *data, size_t len, char *out, size_t out_len)
{
    static const char hex[] = "0123456789abcdef";
    size_t count = len;
    if (!out || out_len == 0) {
        return;
    }
    if (count > (out_len - 1) / 2) {
        count = (out_len - 1) / 2;
    }
    for (size_t i = 0; i < count; i++) {
        out[i * 2] = hex[(data[i] >> 4) & 0x0f];
        out[i * 2 + 1] = hex[data[i] & 0x0f];
    }
    out[count * 2] = '\0';
}

static long long runtime_age_ms(int64_t now_us, int64_t event_us)
{
    return event_us > 0 && now_us >= event_us ? (long long)((now_us - event_us) / 1000) : -1LL;
}

static const char *reset_reason_string(esp_reset_reason_t reason)
{
    switch (reason) {
    case ESP_RST_POWERON: return "power_on";
    case ESP_RST_EXT: return "external";
    case ESP_RST_SW: return "software";
    case ESP_RST_PANIC: return "panic";
    case ESP_RST_INT_WDT: return "interrupt_watchdog";
    case ESP_RST_TASK_WDT: return "task_watchdog";
    case ESP_RST_WDT: return "watchdog";
    case ESP_RST_DEEPSLEEP: return "deep_sleep";
    case ESP_RST_BROWNOUT: return "brownout";
    case ESP_RST_SDIO: return "sdio";
    case ESP_RST_USB: return "usb";
    case ESP_RST_JTAG: return "jtag";
    case ESP_RST_EFUSE: return "efuse";
    case ESP_RST_PWR_GLITCH: return "power_glitch";
    case ESP_RST_CPU_LOCKUP: return "cpu_lockup";
    default: return "unknown";
    }
}

static void append_runtime_diagnostics(char *out,
                                       size_t out_len,
                                       const ble_central_conn_metrics_t *ble)
{
    if (!out || out_len == 0 || !ble) {
        return;
    }

    dualsense_runtime_stats_t runtime;
    pro2_rumble_backend_stats_t rumble;
    char rumble_preview[PRO2_RUMBLE_OUTPUT_PREVIEW_BYTES * 2 + 1];
    dualsense_runtime_stats_snapshot(&runtime);
    pro2_rumble_backend_snapshot(&rumble);
    bytes_to_hex(rumble.preview, rumble.preview_len, rumble_preview, sizeof(rumble_preview));
    int64_t now_us = esp_timer_get_time();
    size_t used = strlen(out);
    if (used >= out_len) {
        return;
    }

    snprintf(out + used,
             out_len - used,
             ",\"uptime_ms\":%lld,\"reset_reason\":%d,\"reset_reason_name\":\"%s\","
             "\"usb_mounted\":%s,\"usb_suspended\":%s,\"usb_configuration_ready\":%s,"
             "\"usb_mount_count\":%lu,\"usb_umount_count\":%lu,"
             "\"usb_bus_reset_count\":%lu,"
             "\"usb_configuration_reset_count\":%lu,\"usb_configuration_reset_age_ms\":%lld,"
             "\"usb_suspend_count\":%lu,\"usb_resume_count\":%lu,"
             "\"usb_event_age_ms\":%lld,\"hid_report_sent\":%lu,"
             "\"hid_report_completed\":%lu,\"hid_report_failed\":%lu,"
             "\"hid_report_submit_failed\":%lu,\"hid_report_xfer_failed\":%lu,"
             "\"hid_report_submit_failure_streak\":%lu,"
             "\"hid_report_submit_failure_age_ms\":%lld,"
             "\"hid_report_not_ready\":%lu,\"hid_endpoint_kicks\":%lu,"
             "\"hid_endpoint_kick_age_ms\":%lld,"
             "\"usb_recovery_count\":%lu,\"usb_recovery_age_ms\":%lld,"
             "\"hid_report_last_gap_us\":%lu,\"hid_report_max_gap_us\":%lu,"
             "\"hid_report_age_ms\":%lld,\"hid_output_count\":%lu,"
             "\"hid_output_age_ms\":%lld,\"hid_feature_get_count\":%lu,"
             "\"hid_rumble_updates\":%lu,\"hid_rumble_active_updates\":%lu,"
             "\"hid_rumble_ignored_nonzero\":%lu,\"hid_rumble_ble_writes\":%lu,"
             "\"hid_rumble_ble_errors\":%lu,\"hid_rumble_enabled\":%s,"
             "\"hid_rumble_active\":%s,\"hid_rumble_valid0\":%u,"
             "\"hid_rumble_valid1\":%u,\"hid_rumble_valid2\":%u,"
             "\"hid_rumble_right\":%u,\"hid_rumble_left\":%u,\"hid_rumble_preview\":\"%s\","
             "\"ble_scanning\":%s,\"ble_connecting\":%s,"
             "\"ble_reconnect_task\":%s,\"ble_auto_scan\":%s,"
             "\"ble_conn_interval_units\":%u,\"ble_conn_interval_us\":%lu,"
             "\"ble_conn_latency\":%u,\"ble_conn_supervision\":%u,"
             "\"ble_fast_drop_count\":%lu,\"ble_fast_block_ms\":%lu,"
             "\"ble_scan_starts\":%lu,\"ble_scan_completes\":%lu,"
             "\"ble_scan_last_rc\":%d,\"ble_scan_last_reason\":%d,"
             "\"ble_reconnect_schedules\":%lu,\"ble_reconnect_attempts\":%lu,"
             "\"ble_connect_starts\":%lu,\"ble_connect_successes\":%lu,"
             "\"ble_connect_failures\":%lu,\"ble_connect_last_rc\":%d,"
             "\"ble_connect_last_status\":%d,\"ble_connect_age_ms\":%lld,"
             "\"ble_disconnects\":%lu,\"ble_disconnect_reason\":%d,"
             "\"ble_disconnect_age_ms\":%lld,\"ble_notify_rx\":%lu,"
             "\"ble_notify_parsed\":%lu,\"ble_notify_age_ms\":%lld,"
             "\"ble_notify_parsed_age_ms\":%lld,"
             "\"ble_notify_actual_hz\":%lu,\"ble_notify_actual_mhz\":%lu,"
             "\"ble_notify_last_gap_us\":%lu,\"ble_notify_max_gap_us\":%lu,"
             "\"ble_notify_min_gap_us\":%lu,"
             "\"ble_notify_short_gap_count\":%lu,"
             "\"ble_notify_sub_interval_count\":%lu,"
             "\"ble_notify_gap_lt3ms\":%lu,"
             "\"ble_notify_gap_3_6p5ms\":%lu,"
             "\"ble_notify_gap_6p5_10ms\":%lu,"
             "\"ble_notify_gap_ge10ms\":%lu,"
             "\"ble_notify_sub_raw_unique\":%lu,"
             "\"ble_notify_sub_control_unique\":%lu,"
             "\"ble_notify_sub_motion_unique\":%lu,"
             "\"ble_notify_raw_unique\":%lu,\"ble_notify_raw_repeat\":%lu,"
             "\"ble_notify_raw_repeat_streak\":%lu,"
             "\"ble_notify_raw_repeat_streak_max\":%lu,"
             "\"ble_notify_raw_unique_mhz\":%lu,"
             "\"ble_notify_raw_unique_gap_us\":%lu,"
             "\"ble_notify_raw_unique_max_gap_us\":%lu,"
             "\"ble_notify_parsed_actual_hz\":%lu,\"ble_notify_parsed_actual_mhz\":%lu,"
             "\"ble_notify_parsed_last_gap_us\":%lu,\"ble_notify_parsed_max_gap_us\":%lu,"
             "\"ble_notify_control_unique\":%lu,"
             "\"ble_notify_control_repeat\":%lu,"
             "\"ble_notify_control_unique_mhz\":%lu,"
             "\"ble_notify_control_unique_gap_us\":%lu,"
             "\"ble_notify_control_unique_max_gap_us\":%lu,"
             "\"ble_notify_motion_unique\":%lu,"
             "\"ble_notify_motion_repeat\":%lu,"
             "\"ble_notify_motion_unique_mhz\":%lu,"
             "\"ble_notify_motion_unique_gap_us\":%lu,"
             "\"ble_notify_motion_unique_max_gap_us\":%lu,"
             "\"ble_read_poll_active\":%s,"
             "\"ble_read_poll_rate_hz\":%u,"
             "\"ble_read_poll_handle\":%u,"
             "\"ble_read_poll_start_rc\":%d,"
             "\"ble_read_poll_status\":%d,"
             "\"ble_read_poll_starts\":%lu,"
             "\"ble_read_poll_start_fails\":%lu,"
             "\"ble_read_poll_rsp\":%lu,"
             "\"ble_read_poll_errors\":%lu,"
             "\"ble_read_poll_actual_mhz\":%lu,"
             "\"ble_read_poll_gap_us\":%lu,"
             "\"ble_read_poll_max_gap_us\":%lu,"
             "\"ble_read_poll_raw_unique\":%lu,"
             "\"ble_read_poll_raw_repeat\":%lu,"
             "\"ble_read_poll_control_unique\":%lu,"
             "\"ble_read_poll_control_repeat\":%lu,"
             "\"ble_read_poll_control_unique_mhz\":%lu,"
             "\"ble_read_poll_control_unique_gap_us\":%lu,"
             "\"ble_read_poll_control_unique_max_gap_us\":%lu,"
             "\"ble_read_poll_motion_unique\":%lu,"
             "\"ble_read_poll_motion_repeat\":%lu,"
             "\"ble_read_poll_motion_unique_mhz\":%lu,"
             "\"ble_read_poll_motion_unique_gap_us\":%lu,"
             "\"ble_read_poll_motion_unique_max_gap_us\":%lu,"
             "\"ble_stale_recoveries\":%lu,\"ble_stale_recovery_age_ms\":%lld",
             (long long)(now_us / 1000),
             (int)esp_reset_reason(),
             reset_reason_string(esp_reset_reason()),
             runtime.mounted ? "true" : "false",
             runtime.suspended ? "true" : "false",
             runtime.configuration_ready ? "true" : "false",
             (unsigned long)runtime.mount_count,
             (unsigned long)runtime.umount_count,
             (unsigned long)runtime.bus_reset_count,
             (unsigned long)runtime.configuration_reset_count,
             runtime_age_ms(now_us, runtime.last_configuration_reset_us),
             (unsigned long)runtime.suspend_count,
             (unsigned long)runtime.resume_count,
             runtime_age_ms(now_us, runtime.last_usb_event_us),
             (unsigned long)runtime.report_sent,
             (unsigned long)runtime.report_completed,
             (unsigned long)runtime.report_failed,
             (unsigned long)runtime.report_submit_failed,
             (unsigned long)runtime.report_xfer_failed,
             (unsigned long)runtime.report_submit_failure_streak,
             runtime_age_ms(now_us, runtime.first_report_submit_failure_us),
             (unsigned long)runtime.report_not_ready,
             (unsigned long)runtime.hid_endpoint_kick_count,
             runtime_age_ms(now_us, runtime.last_hid_endpoint_kick_us),
             (unsigned long)runtime.usb_recovery_count,
             runtime_age_ms(now_us, runtime.last_usb_recovery_us),
             (unsigned long)runtime.report_last_gap_us,
             (unsigned long)runtime.report_max_gap_us,
             runtime_age_ms(now_us, runtime.last_report_us),
             (unsigned long)runtime.output_count,
             runtime_age_ms(now_us, runtime.last_output_us),
             (unsigned long)runtime.feature_get_count,
             (unsigned long)rumble.output_updates,
             (unsigned long)rumble.active_updates,
             (unsigned long)rumble.ignored_nonzero_updates,
             (unsigned long)rumble.ordinary_ble_writes,
             (unsigned long)rumble.ordinary_ble_errors,
             rumble.enabled ? "true" : "false",
             rumble.active ? "true" : "false",
             (unsigned)rumble.valid_flag0,
             (unsigned)rumble.valid_flag1,
             (unsigned)rumble.valid_flag2,
             (unsigned)rumble.right_light,
             (unsigned)rumble.left_heavy,
             rumble_preview,
             ble->scanning ? "true" : "false",
             ble->connecting ? "true" : "false",
             ble->reconnect_task_running ? "true" : "false",
             ble->auto_scan_connect ? "true" : "false",
             (unsigned)ble->interval_units,
             (unsigned long)ble->interval_units * 1250UL,
             (unsigned)ble->latency,
             (unsigned)ble->supervision_timeout,
             (unsigned long)ble->fast_param_drop_count,
             (unsigned long)ble->fast_param_block_remaining_ms,
             (unsigned long)ble->scan_start_count,
             (unsigned long)ble->scan_complete_count,
             ble->last_scan_start_rc,
             ble->last_scan_complete_reason,
             (unsigned long)ble->reconnect_schedule_count,
             (unsigned long)ble->reconnect_attempt_count,
             (unsigned long)ble->connect_start_count,
             (unsigned long)ble->connect_success_count,
             (unsigned long)ble->connect_failure_count,
             ble->last_connect_start_rc,
             ble->last_connect_status,
             runtime_age_ms(now_us, ble->last_connect_us),
             (unsigned long)ble->disconnect_count,
             ble->last_disconnect_reason,
             runtime_age_ms(now_us, ble->last_disconnect_us),
             (unsigned long)ble->notify_rx_count,
             (unsigned long)ble->notify_parsed_count,
             runtime_age_ms(now_us, ble->last_notify_us),
             runtime_age_ms(now_us, ble->last_parsed_notify_us),
             (unsigned long)((ble->notify_actual_millihz + 500u) / 1000u),
             (unsigned long)ble->notify_actual_millihz,
             (unsigned long)ble->notify_last_gap_us,
             (unsigned long)ble->notify_max_gap_us,
             (unsigned long)ble->notify_min_gap_us,
             (unsigned long)ble->notify_short_gap_count,
             (unsigned long)ble->notify_sub_interval_count,
             (unsigned long)ble->notify_gap_lt3ms_count,
             (unsigned long)ble->notify_gap_3_6p5ms_count,
             (unsigned long)ble->notify_gap_6p5_10ms_count,
             (unsigned long)ble->notify_gap_ge10ms_count,
             (unsigned long)ble->notify_sub_interval_raw_unique_count,
             (unsigned long)ble->notify_sub_interval_control_unique_count,
             (unsigned long)ble->notify_sub_interval_motion_unique_count,
             (unsigned long)ble->notify_raw_unique_count,
             (unsigned long)ble->notify_raw_repeat_count,
             (unsigned long)ble->notify_raw_repeat_streak,
             (unsigned long)ble->notify_raw_repeat_streak_max,
             (unsigned long)ble->notify_raw_unique_actual_millihz,
             (unsigned long)ble->notify_raw_unique_last_gap_us,
             (unsigned long)ble->notify_raw_unique_max_gap_us,
             (unsigned long)((ble->notify_parsed_actual_millihz + 500u) / 1000u),
             (unsigned long)ble->notify_parsed_actual_millihz,
             (unsigned long)ble->notify_parsed_last_gap_us,
             (unsigned long)ble->notify_parsed_max_gap_us,
             (unsigned long)ble->notify_control_unique_count,
             (unsigned long)ble->notify_control_repeat_count,
             (unsigned long)ble->notify_control_unique_actual_millihz,
             (unsigned long)ble->notify_control_unique_last_gap_us,
             (unsigned long)ble->notify_control_unique_max_gap_us,
             (unsigned long)ble->notify_motion_unique_count,
             (unsigned long)ble->notify_motion_repeat_count,
             (unsigned long)ble->notify_motion_unique_actual_millihz,
             (unsigned long)ble->notify_motion_unique_last_gap_us,
             (unsigned long)ble->notify_motion_unique_max_gap_us,
             ble->read_poll_active ? "true" : "false",
             (unsigned)ble->read_poll_rate_hz,
             (unsigned)ble->read_poll_handle,
             ble->read_poll_last_start_rc,
             ble->read_poll_last_status,
             (unsigned long)ble->read_poll_start_count,
             (unsigned long)ble->read_poll_start_fail_count,
             (unsigned long)ble->read_poll_rsp_count,
             (unsigned long)ble->read_poll_error_count,
             (unsigned long)ble->read_poll_actual_millihz,
             (unsigned long)ble->read_poll_last_gap_us,
             (unsigned long)ble->read_poll_max_gap_us,
             (unsigned long)ble->read_poll_raw_unique_count,
             (unsigned long)ble->read_poll_raw_repeat_count,
             (unsigned long)ble->read_poll_control_unique_count,
             (unsigned long)ble->read_poll_control_repeat_count,
             (unsigned long)ble->read_poll_control_unique_actual_millihz,
             (unsigned long)ble->read_poll_control_unique_last_gap_us,
             (unsigned long)ble->read_poll_control_unique_max_gap_us,
             (unsigned long)ble->read_poll_motion_unique_count,
             (unsigned long)ble->read_poll_motion_repeat_count,
             (unsigned long)ble->read_poll_motion_unique_actual_millihz,
             (unsigned long)ble->read_poll_motion_unique_last_gap_us,
             (unsigned long)ble->read_poll_motion_unique_max_gap_us,
             (unsigned long)ble->stale_recovery_count,
             runtime_age_ms(now_us, ble->last_stale_recovery_us));
}

static void format_status_extra(char *out, size_t out_len)
{
    dualsense_haptic_audio_features_t audio;
    haptic_raw02_status_t raw02;
    ble_central_conn_metrics_t ble;
    dualsense_haptic_audio_snapshot(&audio);
    haptic_audio_to_raw02_snapshot(&raw02);
    ble_central_get_conn_metrics(&ble);

    snprintf(out,
             out_len,
             "\"mode\":\"dualsense\",\"profile\":\"%s\",\"usb_audio\":\"uac1_4ch\",\"ble\":\"%s\",\"ble_auto\":\"%s\",\"ble_target\":\"%s\",\"ble_conn_interval_units\":%u,\"ble_conn_interval_us\":%lu,\"audio_streaming\":%s,\"audio_alt\":%u,\"audio_submitted\":%lu,\"audio_dropped\":%lu,\"audio_queue_depth\":%u,\"audio_queue_high\":%u,\"audio_packets\":%lu,\"audio_active\":%lu,\"audio_silence\":%lu,\"audio_parser\":\"%s\",\"audio_pair\":\"%s\",\"hd_candidate\":%s,\"front_rms_l\":%u,\"front_rms_r\":%u,\"rear_rms_l\":%u,\"rear_rms_r\":%u,\"front_peak_l\":%u,\"front_peak_r\":%u,\"rear_peak_l\":%u,\"rear_peak_r\":%u,\"front_env_l\":%u,\"front_env_r\":%u,\"rear_env_l\":%u,\"rear_env_r\":%u,\"transient_l\":%u,\"transient_r\":%u,\"haptic\":\"%s\",\"haptic_live\":%s,\"haptic_dry_run\":%s,\"haptic_mode\":\"%s\",\"haptic_source\":\"%s\",\"haptic_max\":%u,\"haptic_gain\":%.3f,\"haptic_transient_gain\":%.3f,\"haptic_interval_ms\":%u,\"haptic_activity_threshold\":%u,\"haptic_silence_timeout_ms\":%u,\"raw02_hd_candidate_packets\":%lu,\"raw02_dry_packets\":%lu,\"raw02_live_packets\":%lu,\"raw02_dropped_rate\":%lu,\"raw02_dropped_no_ble\":%lu,\"raw02_dropped_silence\":%lu,\"raw02_dropped_pcm\":%lu,\"raw02_ble_writes\":%lu,\"raw02_ble_errors\":%lu,\"raw02_last_mode\":\"%s\",\"raw02_left\":\"%s\",\"raw02_right\":\"%s\",\"raw02_error\":\"%s\",\"version\":\"v5.9.17-ps5-family\"",
             DS5_PROFILE_NAME,
             pro2_input_backend_state(),
             device_config_get_ble_autoconnect() ? "on" : "off",
             device_config_get_ble_target(),
             (unsigned)ble.interval_units,
             (unsigned long)ble.interval_units * 1250UL,
             audio.streaming ? "true" : "false",
               (unsigned)audio.alt_setting,
               (unsigned long)audio.submitted_packet_count,
               (unsigned long)audio.dropped_packet_count,
               (unsigned)audio.queue_depth,
               (unsigned)audio.queue_high_watermark,
               (unsigned long)audio.packet_count,
              (unsigned long)audio.active_packet_count,
              (unsigned long)audio.silence_packet_count,
              dualsense_haptic_audio_parser_string((dualsense_haptic_audio_parser_t)audio.parser_mode),
              audio.selected_front_pair ? "front" : "rear",
              audio.hd_candidate ? "true" : "false",
             audio.front_rms_l,
             audio.front_rms_r,
             audio.rms_l,
             audio.rms_r,
             audio.front_peak_l,
             audio.front_peak_r,
             audio.peak_l,
             audio.peak_r,
             audio.front_envelope_l,
             audio.front_envelope_r,
             audio.envelope_l,
             audio.envelope_r,
             audio.transient_l,
             audio.transient_r,
             raw02.live_forwarding ? (raw02.dry_run ? "dry" : "live") : "off",
             raw02.live_forwarding ? "true" : "false",
             raw02.dry_run ? "true" : "false",
             haptic_audio_to_raw02_mode_string(raw02.mode),
             haptic_audio_to_raw02_source_string(raw02.source),
             (unsigned)raw02.max_intensity,
             (double)raw02.gain,
             (double)raw02.transient_gain,
             (unsigned)raw02.min_interval_ms,
             (unsigned)raw02.activity_threshold,
             (unsigned)raw02.silence_timeout_ms,
             (unsigned long)raw02.hd_candidate_packets,
             (unsigned long)raw02.raw02_dry_packets,
             (unsigned long)raw02.raw02_live_packets,
             (unsigned long)raw02.dropped_rate,
             (unsigned long)raw02.dropped_no_ble,
             (unsigned long)raw02.dropped_silence,
             (unsigned long)raw02.dropped_pcm,
             (unsigned long)raw02.ble_writes,
             (unsigned long)raw02.ble_errors,
             raw02.last_mode,
             raw02.last_left_hex,
             raw02.last_right_hex,
             raw02.last_error);
    append_runtime_diagnostics(out, out_len, &ble);
}

static void format_status_lite_extra(char *out, size_t out_len)
{
    dualsense_haptic_audio_features_t audio;
    haptic_raw02_status_t raw02;
    ble_central_conn_metrics_t ble;
    switch2_live_stats_t input_stats;
    switch2_state_t input_state;
    uint32_t input_updates = 0;
    int64_t input_age_us = INT64_MAX;
    bool input_live = pro2_input_backend_get_live(&input_state,
                                                  &input_updates,
                                                  &input_age_us);
    dualsense_haptic_audio_snapshot(&audio);
    haptic_audio_to_raw02_snapshot(&raw02);
    ble_central_get_conn_metrics(&ble);
    switch2_state_get_live_stats(&input_stats);

    snprintf(out,
             out_len,
             "\"mode\":\"dualsense\",\"profile\":\"%s\",\"ble\":\"%s\",\"audio_streaming\":%s,\"audio_alt\":%u,\"audio_submitted\":%lu,\"audio_dropped\":%lu,\"audio_queue_depth\":%u,\"audio_queue_high\":%u,\"audio_packets\":%lu,\"audio_active\":%lu,\"audio_silence\":%lu,\"audio_parser\":\"%s\",\"audio_pair\":\"%s\",\"hd_candidate\":%s,\"front_env_l\":%u,\"front_env_r\":%u,\"rear_env_l\":%u,\"rear_env_r\":%u,\"front_peak_l\":%u,\"front_peak_r\":%u,\"rear_peak_l\":%u,\"rear_peak_r\":%u,\"haptic\":\"%s\",\"haptic_live\":%s,\"haptic_dry_run\":%s,\"haptic_mode\":\"%s\",\"haptic_source\":\"%s\",\"raw02_hd_candidate_packets\":%lu,\"raw02_live_packets\":%lu,\"raw02_dropped_rate\":%lu,\"raw02_dropped_silence\":%lu,\"raw02_dropped_pcm\":%lu,\"raw02_ble_writes\":%lu,\"raw02_ble_errors\":%lu,\"raw02_last_mode\":\"%s\",\"raw02_left\":\"%s\",\"raw02_right\":\"%s\",\"raw02_error\":\"%s\",\"input_live\":%s,\"input_updates\":%lu,\"input_age_ms\":%lld,\"input_rate_millihz\":%lu,\"input_last_gap_us\":%lu,\"input_max_gap_us\":%lu,\"input_lx\":%u,\"input_ly\":%u,\"input_rx\":%u,\"input_ry\":%u,\"version\":\"v5.9.17-ps5-family\"",
             DS5_PROFILE_NAME,
             pro2_input_backend_state(),
             audio.streaming ? "true" : "false",
               (unsigned)audio.alt_setting,
               (unsigned long)audio.submitted_packet_count,
               (unsigned long)audio.dropped_packet_count,
               (unsigned)audio.queue_depth,
               (unsigned)audio.queue_high_watermark,
               (unsigned long)audio.packet_count,
              (unsigned long)audio.active_packet_count,
              (unsigned long)audio.silence_packet_count,
              dualsense_haptic_audio_parser_string((dualsense_haptic_audio_parser_t)audio.parser_mode),
              audio.selected_front_pair ? "front" : "rear",
              audio.hd_candidate ? "true" : "false",
             audio.front_envelope_l,
             audio.front_envelope_r,
             audio.envelope_l,
             audio.envelope_r,
             audio.front_peak_l,
             audio.front_peak_r,
             audio.peak_l,
             audio.peak_r,
             raw02.live_forwarding ? (raw02.dry_run ? "dry" : "live") : "off",
             raw02.live_forwarding ? "true" : "false",
             raw02.dry_run ? "true" : "false",
             haptic_audio_to_raw02_mode_string(raw02.mode),
             haptic_audio_to_raw02_source_string(raw02.source),
             (unsigned long)raw02.hd_candidate_packets,
             (unsigned long)raw02.raw02_live_packets,
             (unsigned long)raw02.dropped_rate,
             (unsigned long)raw02.dropped_silence,
             (unsigned long)raw02.dropped_pcm,
             (unsigned long)raw02.ble_writes,
             (unsigned long)raw02.ble_errors,
             raw02.last_mode,
             raw02.last_left_hex,
             raw02.last_right_hex,
             raw02.last_error,
             input_live ? "true" : "false",
             (unsigned long)input_updates,
             (long long)(input_age_us == INT64_MAX ? -1 : input_age_us / 1000),
             (unsigned long)input_stats.actual_millihz,
             (unsigned long)input_stats.last_gap_us,
             (unsigned long)input_stats.max_gap_us,
             (unsigned)input_state.lx,
             (unsigned)input_state.ly,
             (unsigned)input_state.rx,
             (unsigned)input_state.ry);
    append_runtime_diagnostics(out, out_len, &ble);
}

static void format_status_diag_extra(char *out, size_t out_len)
{
    dualsense_haptic_audio_features_t audio;
    haptic_raw02_status_t raw02;
    pro2_rumble_backend_stats_t rumble;
    dualsense_runtime_stats_t runtime;
    ble_central_conn_metrics_t ble;
    switch2_state_t input_state;
    uint32_t input_updates = 0;
    int64_t input_age_us = INT64_MAX;
    int64_t now_us = esp_timer_get_time();
    bool input_live = pro2_input_backend_get_live(&input_state,
                                                  &input_updates,
                                                  &input_age_us);

    dualsense_haptic_audio_snapshot(&audio);
    haptic_audio_to_raw02_snapshot(&raw02);
    pro2_rumble_backend_snapshot(&rumble);
    dualsense_runtime_stats_snapshot(&runtime);
    ble_central_get_conn_metrics(&ble);

    snprintf(out,
             out_len,
             "\"mode\":\"dualsense\",\"profile\":\"%s\","
             "\"uptime_ms\":%lld,\"reset_reason\":\"%s\","
             "\"heap_free\":%lu,\"heap_min\":%lu,"
             "\"heap_internal_free\":%lu,\"heap_internal_largest\":%lu,"
             "\"usb_mounted\":%s,\"usb_ready\":%s,\"usb_bus_resets\":%lu,"
             "\"usb_config_resets\":%lu,\"usb_recoveries\":%lu,"
             "\"usb_recovery_inhibited\":%lu,\"usb_recovery_inhibit_reason\":%lu,"
             "\"hid_endpoint_kicks\":%lu,\"hid_endpoint_kick_age_ms\":%lld,"
             "\"hid_report_age_ms\":%lld,\"hid_report_max_gap_us\":%lu,"
             "\"hid_submit_failures\":%lu,\"hid_xfer_failures\":%lu,"
             "\"uac_out_xfer_success\":%lu,\"uac_out_xfer_errors\":%lu,"
             "\"uac_out_rearm_failures\":%lu,\"uac_last_xfer_age_ms\":%lld,"
             "\"uac_last_xfer_result\":%ld,\"uac_last_xfer_bytes\":%lu,"
             "\"uac_set_interfaces\":%lu,\"uac_mic_alt1_attempts\":%lu,"
             "\"uac_mic_alt1_rejects\":%lu,"
             "\"ble\":\"%s\",\"ble_interval_us\":%lu,"
             "\"ble_notify_age_ms\":%lld,\"ble_disconnects\":%lu,"
             "\"ble_disconnect_reason\":%d,"
             "\"input_live\":%s,\"input_updates\":%lu,\"input_age_ms\":%lld,"
             "\"audio_streaming\":%s,\"audio_alt\":%u,"
             "\"audio_submitted\":%lu,\"audio_dropped\":%lu,"
             "\"audio_queue_depth\":%u,\"audio_queue_high\":%u,"
             "\"audio_queue_full\":%lu,\"audio_process_batches\":%lu,"
             "\"audio_process_last_us\":%lu,\"audio_process_max_us\":%lu,"
             "\"audio_front_active\":%lu,\"audio_rear_active\":%lu,"
             "\"audio_front_only\":%lu,\"audio_rear_only\":%lu,"
             "\"audio_both_active\":%lu,\"audio_rear_low_energy\":%lu,"
             "\"audio_stack_free\":%lu,\"audio_hd_candidate\":%s,"
             "\"raw02_targets\":%lu,\"raw02_ble_writes\":%lu,"
             "\"raw02_ble_errors\":%lu,\"raw02_live_packets\":%lu,"
             "\"ordinary_ble_writes\":%lu,\"ordinary_ble_errors\":%lu,"
             "\"rumble_policy\":\"dualsense_host_intent\","
             "\"rumble_host_mode\":\"%s\","
             "\"rumble_host_mode_transitions\":%lu,"
             "\"rumble_audio_haptics_updates\":%lu,"
             "\"rumble_compatibility_updates\":%lu,"
             "\"rumble_hd_blocked_by_compatibility\":%lu,"
             "\"rumble_compatibility_selected\":%s,"
             "\"rumble_compatibility_v1\":%s,"
             "\"rumble_compatibility_v2\":%s,"
             "\"rumble_audio_haptics_allowed\":%s,"
             "\"rumble_source\":\"%s\",\"rumble_source_transitions\":%lu,"
             "\"rumble_hd_preemptions\":%lu,\"rumble_ordinary_fallbacks\":%lu,"
             "\"rumble_ordinary_updates_while_hd\":%lu,"
             "\"rumble_hd_active\":%s,\"rumble_ordinary_active\":%s,"
             "\"rumble_hd_age_ms\":%lld,\"rumble_ordinary_age_ms\":%lld,"
             "\"rumble_stop_writes\":%lu,\"hid_non_rumble_updates\":%lu,"
             "\"rumble_stack_free\":%lu,\"input_stack_free\":%lu,"
             "\"control_stack_free\":%lu,\"version\":\"v5.9.17-ps5-family\"",
             DS5_PROFILE_NAME,
             (long long)(now_us / 1000),
             reset_reason_string(esp_reset_reason()),
             (unsigned long)esp_get_free_heap_size(),
             (unsigned long)esp_get_minimum_free_heap_size(),
             (unsigned long)heap_caps_get_free_size(MALLOC_CAP_INTERNAL |
                                                    MALLOC_CAP_8BIT),
             (unsigned long)heap_caps_get_largest_free_block(
                 MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT),
             runtime.mounted ? "true" : "false",
             runtime.configuration_ready ? "true" : "false",
             (unsigned long)runtime.bus_reset_count,
             (unsigned long)runtime.configuration_reset_count,
             (unsigned long)runtime.usb_recovery_count,
             (unsigned long)runtime.usb_recovery_inhibited_count,
             (unsigned long)runtime.usb_recovery_inhibit_reason,
             (unsigned long)runtime.hid_endpoint_kick_count,
             runtime_age_ms(now_us, runtime.last_hid_endpoint_kick_us),
             runtime_age_ms(now_us, runtime.last_report_us),
             (unsigned long)runtime.report_max_gap_us,
             (unsigned long)runtime.report_submit_failed,
             (unsigned long)runtime.report_xfer_failed,
             (unsigned long)runtime.uac_out_xfer_success,
             (unsigned long)runtime.uac_out_xfer_errors,
             (unsigned long)runtime.uac_out_rearm_failures,
             runtime_age_ms(now_us, runtime.uac_last_xfer_us),
             (long)runtime.uac_last_xfer_result,
             (unsigned long)runtime.uac_last_xfer_bytes,
             (unsigned long)runtime.uac_set_interface_count,
             (unsigned long)runtime.uac_mic_alt1_attempts,
             (unsigned long)runtime.uac_mic_alt1_rejects,
             pro2_input_backend_state(),
             (unsigned long)ble.interval_units * 1250UL,
             runtime_age_ms(now_us, ble.last_notify_us),
             (unsigned long)ble.disconnect_count,
             ble.last_disconnect_reason,
             input_live ? "true" : "false",
             (unsigned long)input_updates,
             (long long)(input_age_us == INT64_MAX ? -1 : input_age_us / 1000),
             audio.streaming ? "true" : "false",
             (unsigned)audio.alt_setting,
             (unsigned long)audio.submitted_packet_count,
             (unsigned long)audio.dropped_packet_count,
             (unsigned)audio.queue_depth,
             (unsigned)audio.queue_high_watermark,
             (unsigned long)audio.queue_full_count,
             (unsigned long)audio.process_batch_count,
             (unsigned long)audio.process_last_us,
             (unsigned long)audio.process_max_us,
             (unsigned long)audio.front_active_packet_count,
             (unsigned long)audio.rear_active_packet_count,
             (unsigned long)audio.front_only_packet_count,
             (unsigned long)audio.rear_only_packet_count,
             (unsigned long)audio.both_active_packet_count,
             (unsigned long)audio.rear_low_energy_packet_count,
             (unsigned long)audio.task_stack_high_watermark_bytes,
             audio.hd_candidate ? "true" : "false",
             (unsigned long)rumble.raw02_submissions,
             (unsigned long)rumble.raw02_ble_writes,
             (unsigned long)rumble.raw02_ble_errors,
             (unsigned long)raw02.raw02_live_packets,
             (unsigned long)rumble.ordinary_ble_writes,
             (unsigned long)rumble.ordinary_ble_errors,
             pro2_rumble_backend_host_mode_string(rumble.host_mode),
             (unsigned long)rumble.host_mode_transitions,
             (unsigned long)rumble.audio_haptics_updates,
             (unsigned long)rumble.compatibility_updates,
             (unsigned long)rumble.hd_updates_blocked_by_compatibility,
             rumble.compatibility_selected ? "true" : "false",
             rumble.compatibility_v1 ? "true" : "false",
             rumble.compatibility_v2 ? "true" : "false",
             rumble.audio_haptics_allowed ? "true" : "false",
             pro2_rumble_backend_source_string(rumble.selected_source),
             (unsigned long)rumble.source_transitions,
             (unsigned long)rumble.hd_preemptions,
             (unsigned long)rumble.ordinary_fallbacks,
             (unsigned long)rumble.ordinary_updates_while_hd,
             rumble.raw02_source_active ? "true" : "false",
             rumble.ordinary_source_active ? "true" : "false",
             (long long)(rumble.raw02_age_us < 0
                             ? -1
                             : rumble.raw02_age_us / 1000),
             (long long)(rumble.ordinary_age_us < 0
                             ? -1
                             : rumble.ordinary_age_us / 1000),
             (unsigned long)rumble.stop_ble_writes,
             (unsigned long)rumble.non_rumble_updates,
             (unsigned long)rumble.task_stack_high_watermark_bytes,
             (unsigned long)runtime.input_task_stack_high_watermark_bytes,
             (unsigned long)runtime.control_task_stack_high_watermark_bytes);
}

static esp_err_t handle_haptic_command(const char *cmd, char *reply, int reply_len)
{
    if (strcmp(cmd, "haptic status lite") == 0 || strcmp(cmd, "haptic lite") == 0) {
        static char extra[16384];
        format_status_lite_extra(extra, sizeof(extra));
        return json_ok(reply, reply_len, "haptic status lite", extra);
    }
    if (strcmp(cmd, "haptic status") == 0 || strcmp(cmd, "haptic") == 0) {
        static char extra[16384];
        format_status_extra(extra, sizeof(extra));
        return json_ok(reply, reply_len, "haptic status", extra);
    }
    if (strcmp(cmd, "haptic raw02 on") == 0) {
        haptic_audio_to_raw02_set_live_forwarding(true);
        return json_ok(reply, reply_len, "haptic raw02", "\"live_forwarding\":true");
    }
    if (strcmp(cmd, "haptic raw02 off") == 0) {
        haptic_audio_to_raw02_set_live_forwarding(false);
        return json_ok(reply, reply_len, "haptic raw02", "\"live_forwarding\":false");
    }
    if (strcmp(cmd, "haptic dryrun on") == 0 || strcmp(cmd, "haptic dry-run on") == 0) {
        haptic_audio_to_raw02_set_dry_run(true);
        return json_ok(reply, reply_len, "haptic dryrun", "\"dry_run\":true");
    }
    if (strcmp(cmd, "haptic dryrun off") == 0 || strcmp(cmd, "haptic dry-run off") == 0) {
        haptic_audio_to_raw02_set_dry_run(false);
        return json_ok(reply, reply_len, "haptic dryrun", "\"dry_run\":false");
    }
    if (strncmp(cmd, "haptic max ", 11) == 0) {
        long value = 0;
        if (!parse_long_arg(cmd + 11, &value) || value < 0 || value > 255) {
            return json_error(reply, reply_len, "haptic max", "usage: haptic max <0..255>");
        }
        haptic_audio_to_raw02_set_max_intensity((uint8_t)value);
        char extra[48];
        snprintf(extra, sizeof(extra), "\"max_intensity\":%ld", value);
        return json_ok(reply, reply_len, "haptic max", extra);
    }
    if (strncmp(cmd, "haptic gain ", 12) == 0) {
        float value = 0.0f;
        if (!parse_float_arg(cmd + 12, &value) || value < 0.0f || value > 8.0f) {
            return json_error(reply, reply_len, "haptic gain", "usage: haptic gain <0.0..8.0>");
        }
        haptic_audio_to_raw02_set_gain(value);
        char extra[48];
        snprintf(extra, sizeof(extra), "\"gain\":%.3f", (double)value);
        return json_ok(reply, reply_len, "haptic gain", extra);
    }
    if (strncmp(cmd, "haptic transient_gain ", 22) == 0) {
        float value = 0.0f;
        if (!parse_float_arg(cmd + 22, &value) || value < 0.0f || value > 8.0f) {
            return json_error(reply, reply_len, "haptic transient_gain", "usage: haptic transient_gain <0.0..8.0>");
        }
        haptic_audio_to_raw02_set_transient_gain(value);
        char extra[64];
        snprintf(extra, sizeof(extra), "\"transient_gain\":%.3f", (double)value);
        return json_ok(reply, reply_len, "haptic transient_gain", extra);
    }
    if (strncmp(cmd, "haptic interval ", 16) == 0) {
        long value = 0;
        if (!parse_long_arg(cmd + 16, &value) || value < 10 || value > 250) {
            return json_error(reply, reply_len, "haptic interval", "usage: haptic interval <10..250>");
        }
        haptic_audio_to_raw02_set_min_interval_ms((uint16_t)value);
        char extra[64];
        snprintf(extra, sizeof(extra), "\"min_interval_ms\":%ld", value);
        return json_ok(reply, reply_len, "haptic interval", extra);
    }
    if (strncmp(cmd, "haptic silence ", 15) == 0) {
        long value = 0;
        if (!parse_long_arg(cmd + 15, &value) || value < 20 || value > 1000) {
            return json_error(reply, reply_len, "haptic silence", "usage: haptic silence <20..1000>");
        }
        haptic_audio_to_raw02_set_silence_timeout_ms((uint16_t)value);
        char extra[64];
        snprintf(extra, sizeof(extra), "\"silence_timeout_ms\":%ld", value);
        return json_ok(reply, reply_len, "haptic silence", extra);
    }
    if (strncmp(cmd, "haptic threshold ", 17) == 0 ||
        strncmp(cmd, "haptic activity ", 16) == 0) {
        const char *arg = strncmp(cmd, "haptic threshold ", 17) == 0 ? cmd + 17 : cmd + 16;
        long value = 0;
        if (!parse_long_arg(arg, &value) || value < 1 || value > 32767) {
            return json_error(reply, reply_len, "haptic threshold", "usage: haptic threshold <1..32767>");
        }
        haptic_audio_to_raw02_set_activity_threshold((uint16_t)value);
        char extra[64];
        snprintf(extra, sizeof(extra), "\"activity_threshold\":%ld", value);
        return json_ok(reply, reply_len, "haptic threshold", extra);
    }
    if (strncmp(cmd, "haptic mode ", 12) == 0) {
        haptic_raw02_mode_t mode;
        if (!haptic_audio_to_raw02_parse_mode(cmd + 12, &mode)) {
            return json_error(reply, reply_len, "haptic mode", "usage: haptic mode auto|spectral|tick|punch|continuous|texture");
        }
        haptic_audio_to_raw02_set_mode(mode);
        char extra[64];
        snprintf(extra, sizeof(extra), "\"mode\":\"%s\"", haptic_audio_to_raw02_mode_string(mode));
        return json_ok(reply, reply_len, "haptic mode", extra);
    }
    if (strncmp(cmd, "haptic source ", 14) == 0) {
        haptic_raw02_source_t source;
        if (!haptic_audio_to_raw02_parse_source(cmd + 14, &source)) {
            return json_error(reply, reply_len, "haptic source", "usage: haptic source hd_only|pcm");
        }
        haptic_audio_to_raw02_set_source(source);
        char extra[64];
        snprintf(extra, sizeof(extra), "\"source\":\"%s\"", haptic_audio_to_raw02_source_string(source));
        return json_ok(reply, reply_len, "haptic source", extra);
    }
    if (strncmp(cmd, "haptic test live ", 17) == 0) {
        char name[24];
        if (!copy_trimmed_arg(cmd + 17, name, sizeof(name))) {
            return json_error(reply, reply_len, "haptic test live", "test name is too long");
        }
        esp_err_t err = haptic_audio_to_raw02_send_test(name, true);
        char extra[112];
        snprintf(extra, sizeof(extra), "\"test\":\"%s\",\"force_live\":true,\"sent\":%s,\"error\":\"%s\"",
                 name,
                 err == ESP_OK ? "true" : "false",
                 err == ESP_OK ? "none" : esp_err_to_name(err));
        return err == ESP_OK ?
            json_ok(reply, reply_len, "haptic test live", extra) :
            json_error(reply, reply_len, "haptic test live", esp_err_to_name(err));
    }
    if (strncmp(cmd, "haptic test ", 12) == 0) {
        char name[24];
        if (!copy_trimmed_arg(cmd + 12, name, sizeof(name))) {
            return json_error(reply, reply_len, "haptic test", "test name is too long");
        }
        esp_err_t err = haptic_audio_to_raw02_send_test(name, false);
        char extra[96];
        snprintf(extra, sizeof(extra), "\"test\":\"%s\",\"sent\":%s,\"error\":\"%s\"",
                 name,
                 err == ESP_OK ? "true" : "false",
                 err == ESP_OK ? "none" : esp_err_to_name(err));
        return json_ok(reply, reply_len, "haptic test", extra);
    }
    if (strcmp(cmd, "haptic default") == 0 || strcmp(cmd, "haptic defaults") == 0) {
        haptic_audio_to_raw02_defaults();
        return json_ok(reply, reply_len, "haptic defaults", "\"restored\":true");
    }
    return json_error(reply, reply_len, "haptic", "unknown haptic command");
}

static esp_err_t handle_audio_command(const char *cmd, char *reply, int reply_len)
{
    if (strncmp(cmd, "audio parser ", 13) == 0 ||
        strncmp(cmd, "haptic parser ", 14) == 0) {
        const char *arg = strncmp(cmd, "audio parser ", 13) == 0 ? cmd + 13 : cmd + 14;
        dualsense_haptic_audio_parser_t parser;
        if (!dualsense_haptic_audio_parse_parser(arg, &parser)) {
            return json_error(reply,
                              reply_len,
                              "audio parser",
                              "usage: audio parser rear|front|strongest");
        }
        dualsense_haptic_audio_set_parser(parser);
        char extra[64];
        snprintf(extra,
                 sizeof(extra),
                 "\"audio_parser\":\"%s\"",
                 dualsense_haptic_audio_parser_string(parser));
        return json_ok(reply, reply_len, "audio parser", extra);
    }
    return ESP_ERR_NOT_SUPPORTED;
}

void v55_control_protocol_init(void)
{
    ESP_LOGI(TAG, "[V55_CONTROL] serial control ready: status/status lite/status diag, BLE, ble multiprobe, haptic, audio parser, raw02, input recalibrate");
}

esp_err_t v55_control_protocol_handle_line(const char *line, char *reply, int reply_len)
{
    char cmd[192];
    snprintf(cmd, sizeof(cmd), "%s", line ? line : "");
    trim(cmd);
    ESP_LOGI(TAG, "[V55_CONTROL] command=%s", cmd);

    if (strcmp(cmd, "status") == 0 || strcmp(cmd, "param get") == 0) {
        static char extra[16384];
        format_status_extra(extra, sizeof(extra));
        return json_ok(reply, reply_len, "status", extra);
    }
    if (strcmp(cmd, "status lite") == 0 || strcmp(cmd, "param get lite") == 0) {
        static char extra[16384];
        format_status_lite_extra(extra, sizeof(extra));
        return json_ok(reply, reply_len, "status lite", extra);
    }
    if (strcmp(cmd, "status diag") == 0 || strcmp(cmd, "diag") == 0) {
        static char extra[16384];
        format_status_diag_extra(extra, sizeof(extra));
        return json_ok(reply, reply_len, "status diag", extra);
    }
    if (strcmp(cmd, "version") == 0) {
        return json_ok(reply, reply_len, "version", "\"version\":\"v5.9.17-ps5-family\",\"profile\":\"" DS5_PROFILE_NAME "\"");
    }
    if (strcmp(cmd, "mode pro2") == 0) {
        return json_ok(reply, reply_len, "mode", "\"mode\":\"pro2\",\"reflash_required\":true,\"note\":\"Flash V5.9.17 Pro2 / Nintendo bridge firmware, then replug native USB\"");
    }
    if (strcmp(cmd, "mode dualsense") == 0) {
        return json_ok(reply, reply_len, "mode", "\"mode\":\"dualsense\",\"reflash_required\":false,\"note\":\"Already running V5.9.17 Xin He Lian Sheng PS5 family identity\"");
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
    esp_err_t audio_rc = handle_audio_command(cmd, reply, reply_len);
    if (audio_rc != ESP_ERR_NOT_SUPPORTED) {
        return audio_rc;
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
        return err == ESP_OK ?
            json_ok(reply, reply_len, "ble connect", "\"ble\":\"connecting\"") :
            json_error(reply, reply_len, "ble connect", "connect start failed");
    }
    if (strcmp(cmd, "ble reconnect") == 0 || strcmp(cmd, "ble auto connect") == 0) {
        esp_err_t err = ble_central_reconnect_saved_or_scan();
        return err == ESP_OK ?
            json_ok(reply, reply_len, "ble reconnect", "\"ble\":\"connecting\"") :
            json_error(reply, reply_len, "ble reconnect", "reconnect start failed");
    }
    if (strcmp(cmd, "ble multiprobe") == 0 ||
        strcmp(cmd, "ble multi probe") == 0 ||
        strcmp(cmd, "ble fast") == 0 ||
        strcmp(cmd, "ble interval fast") == 0 ||
        strcmp(cmd, "ble event probe") == 0) {
        esp_err_t err = ble_central_start_multi_report_probe();
        return err == ESP_OK ?
            json_ok(reply, reply_len, "ble multiprobe", "\"ble_multi_probe\":\"started\",\"interval_request\":\"fast_7p5ms\",\"note\":\"move sticks hard; inspect sub_control_unique and gap buckets\"") :
            json_error(reply, reply_len, "ble multiprobe", "multi-report probe requires an active BLE connection");
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
        return err == ESP_OK ?
            json_ok(reply, reply_len, "ble auto", "\"ble_auto\":\"off\"") :
            json_error(reply, reply_len, "ble auto", "failed to save BLE autoconnect");
    }
    if (strcmp(cmd, "ble forget") == 0 || strcmp(cmd, "ble target clear") == 0) {
        ble_central_disconnect();
        esp_err_t err = device_config_save_ble_target("");
        return err == ESP_OK ?
            json_ok(reply, reply_len, "ble forget", "\"ble\":\"idle\",\"ble_target\":\"\"") :
            json_error(reply, reply_len, "ble forget", "failed to clear BLE target");
    }
    if (strcmp(cmd, "ble disconnect") == 0) {
        ble_central_disconnect();
        return json_ok(reply, reply_len, "ble disconnect", "\"ble\":\"idle\"");
    }
    if (strcmp(cmd, "input recalibrate") == 0 ||
        strcmp(cmd, "stick recalibrate") == 0 ||
        strcmp(cmd, "axis recalibrate") == 0) {
        switch2_gatt_reset_axis_calibration();
        switch2_state_clear_live();
        return json_ok(reply,
                       reply_len,
                       "input recalibrate",
                       "\"axis_calibration\":\"reset\",\"note\":\"keep sticks centered until BLE samples settle\"");
    }

    if (strncmp(cmd, "haptic", 6) == 0) {
        return handle_haptic_command(cmd, reply, reply_len);
    }
    if (strncmp(cmd, "rumble raw02 ", 13) == 0) {
        char payload_hex[HAPTIC_RAW02_PAYLOAD_BYTES * 2 + 1];
        esp_err_t err = haptic_audio_to_raw02_send_raw_hex(cmd + 13,
                                                           false,
                                                           payload_hex,
                                                           sizeof(payload_hex));
        char extra[220];
        snprintf(extra,
                 sizeof(extra),
                 "\"rumble\":\"raw02\",\"payload\":\"%s\",\"sent\":%s,\"error\":\"%s\"",
                 payload_hex,
                 err == ESP_OK ? "true" : "false",
                 err == ESP_OK ? "none" : esp_err_to_name(err));
        return err == ESP_OK ?
            json_ok(reply, reply_len, "rumble raw02", extra) :
            json_error(reply, reply_len, "rumble raw02", esp_err_to_name(err));
    }
    if (strcmp(cmd, "param default") == 0 || strcmp(cmd, "param defaults") == 0) {
        haptic_audio_to_raw02_defaults();
        return json_ok(reply, reply_len, "param defaults", "\"restored\":true");
    }
    if (strncmp(cmd, "param set haptic.max ", 21) == 0) {
        long value = 0;
        if (!parse_long_arg(cmd + 21, &value) || value < 0 || value > 255) {
            return json_error(reply, reply_len, "param set", "usage: param set haptic.max <0..255>");
        }
        haptic_audio_to_raw02_set_max_intensity((uint8_t)value);
        return json_ok(reply, reply_len, "param set", "\"saved\":false");
    }

    return json_error(reply, reply_len, "unknown", "unknown V5.9.17 command");
}




