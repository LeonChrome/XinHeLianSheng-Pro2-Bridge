#include "pro2_rumble_backend.h"

#include <string.h>

#include "ble_central.h"
#include "dualsense_rumble_intent.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/portmacro.h"
#include "freertos/task.h"
#include "normalized_rumble.h"
#include "pro2_input_backend.h"

#define DS5_OUTPUT_REPORT_ID 0x02
#define PRO2_RUMBLE_TICK_MS 12
#define PRO2_RUMBLE_HOLD_MS 250
#define PRO2_RAW02_HOLD_MS 120
#define PRO2_RAW02_REPEAT_MS 24
#define PRO2_RUMBLE_STOP_PACKETS 3
#define PRO2_RUMBLE_MAX_AMPLITUDE 640
#define RAW02_LEFT_FRAME_OFFSET 2
#define RAW02_RIGHT_FRAME_OFFSET 18
#define RAW02_FRAME_BYTES 5
#define RAW02_SOURCE_AMPLITUDE_MAX 65535

static const char *TAG = "v5.5_rumble";
static portMUX_TYPE s_lock = portMUX_INITIALIZER_UNLOCKED;
static bool s_task_started;
static pro2_rumble_arbiter_t s_arbiter;
static normalized_rumble_t s_rumble;
static uint8_t s_packet_id;
static uint8_t s_raw02_left[5];
static uint8_t s_raw02_right[5];
static int64_t s_raw02_last_write_us;
static int64_t s_ordinary_last_update_us;
static int64_t s_raw02_last_update_us;
static uint8_t s_last_right;
static uint8_t s_last_left;
static uint32_t s_updates;
static uint32_t s_writes;
static uint32_t s_errors;
static uint32_t s_active_updates;
static uint32_t s_non_rumble_updates;
static uint32_t s_ignored_nonzero_updates;
static uint32_t s_ordinary_writes;
static uint32_t s_ordinary_errors;
static uint32_t s_raw02_submissions;
static uint32_t s_raw02_writes;
static uint32_t s_raw02_errors;
static uint32_t s_stop_writes;
static uint32_t s_ordinary_updates_while_hd;
static bool s_last_compatibility_selected;
static bool s_last_compatibility_v1;
static bool s_last_compatibility_v2;
static bool s_last_audio_haptics_allowed = true;
static bool s_last_enabled;
static bool s_last_active;
static uint8_t s_last_valid_flag0;
static uint8_t s_last_valid_flag1;
static uint8_t s_last_valid_flag2;
static uint8_t s_last_preview_len;
static uint8_t s_last_preview[PRO2_RUMBLE_OUTPUT_PREVIEW_BYTES];
static int64_t s_raw02_next_log_us;
static TaskHandle_t s_rumble_task_handle;

const char *pro2_rumble_backend_source_string(pro2_rumble_source_t source)
{
    switch (source) {
    case PRO2_RUMBLE_SOURCE_HD:
        return "hd";
    case PRO2_RUMBLE_SOURCE_ORDINARY:
        return "ordinary";
    default:
        return "none";
    }
}

const char *pro2_rumble_backend_host_mode_string(
    pro2_rumble_host_mode_t mode)
{
    return pro2_rumble_arbiter_host_mode_string(mode);
}

static void write_motor_block(uint8_t *out, uint16_t offset, uint8_t packet_id,
                              const uint8_t vibration[5],
                              const uint8_t zero[5])
{
    out[offset] = (uint8_t)(0x50 | (packet_id & 0x0f));
    memcpy(out + offset + 1, vibration, 5);
    memcpy(out + offset + 6, zero, 5);
    memcpy(out + offset + 11, zero, 5);
}

static void build_packet(uint8_t packet_id, const normalized_rumble_t *rumble,
                         uint8_t out[33])
{
    uint8_t left[5];
    uint8_t right[5];
    uint8_t zero[5];
    normalized_rumble_build_zero_pro2(zero);
    normalized_rumble_build_pro2_pair(rumble, PRO2_RUMBLE_MAX_AMPLITUDE, left, right);
    memset(out, 0, 33);
    out[0] = 0x00;
    write_motor_block(out, 1, packet_id, left, zero);
    write_motor_block(out, 17, packet_id, right, zero);
}


static bool raw02_frame_has_effect(const uint8_t *data, uint16_t len, uint16_t offset)
{
    if (!data || len < offset + RAW02_FRAME_BYTES) {
        return false;
    }
    int b1 = data[offset + 1];
    int b2 = data[offset + 2];
    int b3 = data[offset + 3];
    int b4 = data[offset + 4];
    int high_amp = ((b1 & 0xfc) << 4) | ((b2 & 0x0f) << 12);
    int low_amp = (b3 & 0xc0) | (b4 << 8);
    return high_amp != 0 || low_amp != 0;
}

static bool raw02_is_switch2_rumble_report(const uint8_t *data, uint16_t len)
{
    return len >= 7 && data[0] == 0x02 && (data[1] & 0xf0) == 0x50;
}

static int raw02_clamp_int(int value, int min_value, int max_value)
{
    if (value < min_value) {
        return min_value;
    }
    if (value > max_value) {
        return max_value;
    }
    return value;
}

static int raw02_map_switch_amp_to_ble(int value)
{
    int clamped = raw02_clamp_int(value, 0, RAW02_SOURCE_AMPLITUDE_MAX);
    int64_t scaled = (int64_t)clamped * 1023LL;
    int64_t mapped = (scaled + RAW02_SOURCE_AMPLITUDE_MAX / 2) /
                     RAW02_SOURCE_AMPLITUDE_MAX;
    return raw02_clamp_int((int)mapped, 0, 1023);
}

static void raw02_build_ble_vibration_data(uint16_t lf_freq,
                                           bool lf_tone,
                                           uint16_t lf_amp,
                                           uint16_t hf_freq,
                                           bool hf_tone,
                                           uint16_t hf_amp,
                                           uint8_t out[5])
{
    uint64_t value = 0;
    value |= (uint64_t)(lf_freq & 0x01ff);
    value |= (uint64_t)(lf_tone ? 1 : 0) << 9;
    value |= (uint64_t)(lf_amp & 0x03ff) << 10;
    value |= (uint64_t)(hf_freq & 0x01ff) << 20;
    value |= (uint64_t)(hf_tone ? 1 : 0) << 29;
    value |= (uint64_t)(hf_amp & 0x03ff) << 30;

    for (size_t i = 0; i < 5; i++) {
        out[i] = (uint8_t)((value >> (8 * i)) & 0xff);
    }
}

static void raw02_build_zero_ble_vibration(uint8_t out[5])
{
    raw02_build_ble_vibration_data(0x0e1, false, 0, 0x1e1, false, 0, out);
}

static void raw02_encode_ble_vibration_from_switch_frame(const uint8_t *report,
                                                         uint16_t len,
                                                         uint16_t offset,
                                                         uint8_t out[5])
{
    if (len < offset + 5) {
        raw02_build_zero_ble_vibration(out);
        return;
    }

    int b0 = report[offset];
    int b1 = report[offset + 1];
    int b2 = report[offset + 2];
    int b3 = report[offset + 3];
    int b4 = report[offset + 4];

    int high_freq = b0 | ((b1 & 0x03) << 8);
    int high_amp = ((b1 & 0xfc) << 4) | ((b2 & 0x0f) << 12);
    int low_freq = ((b2 & 0xf0) >> 4) | ((b3 & 0x3f) << 4);
    int low_amp = (b3 & 0xc0) | (b4 << 8);

    raw02_build_ble_vibration_data((uint16_t)low_freq,
                                   false,
                                   (uint16_t)raw02_map_switch_amp_to_ble(low_amp),
                                   (uint16_t)high_freq,
                                   false,
                                   (uint16_t)raw02_map_switch_amp_to_ble(high_amp),
                                   out);
}

static void raw02_build_pro2_packet(uint8_t packet_id,
                                    const uint8_t left[5],
                                    const uint8_t right[5],
                                    uint8_t out[33])
{
    uint8_t zero[5];
    raw02_build_zero_ble_vibration(zero);
    memset(out, 0, 33);
    out[0] = 0x00;
    write_motor_block(out, 1, packet_id, left, zero);
    write_motor_block(out, 17, packet_id, right, zero);
}

esp_err_t pro2_rumble_backend_send_raw02_payload(const uint8_t *payload, uint16_t len)
{
    if (!payload || len != 64) {
        ESP_LOGW(TAG,
                 "[RUMBLE_RAW02] sent=false error=invalid_payload_len len=%u",
                 (unsigned)len);
        return ESP_ERR_INVALID_ARG;
    }
    if (!raw02_is_switch2_rumble_report(payload, len)) {
        ESP_LOGW(TAG,
                 "[RUMBLE_RAW02] sent=false error=invalid_report first=%02x/%02x",
                 payload[0],
                 payload[1]);
        return ESP_ERR_INVALID_ARG;
    }
    if (strcmp(pro2_input_backend_state(), "connected") != 0) {
        return ESP_ERR_INVALID_STATE;
    }

    uint8_t left[5];
    uint8_t right[5];
    bool active = raw02_frame_has_effect(payload, len, RAW02_LEFT_FRAME_OFFSET) ||
                  raw02_frame_has_effect(payload, len, RAW02_RIGHT_FRAME_OFFSET);
    if (active) {
        raw02_encode_ble_vibration_from_switch_frame(
            payload, len, RAW02_LEFT_FRAME_OFFSET, left);
        raw02_encode_ble_vibration_from_switch_frame(
            payload, len, RAW02_RIGHT_FRAME_OFFSET, right);
    } else {
        raw02_build_zero_ble_vibration(left);
        raw02_build_zero_ble_vibration(right);
    }

    int64_t now_us = esp_timer_get_time();
    portENTER_CRITICAL(&s_lock);
    s_raw02_submissions++;
    s_raw02_last_update_us = now_us;
    if (active) {
        memcpy(s_raw02_left, left, sizeof(s_raw02_left));
        memcpy(s_raw02_right, right, sizeof(s_raw02_right));
    }
    pro2_rumble_arbiter_update_hd(
        &s_arbiter, active, now_us, PRO2_RAW02_HOLD_MS);
    portEXIT_CRITICAL(&s_lock);

    if (now_us >= s_raw02_next_log_us) {
        s_raw02_next_log_us = now_us + 5000000LL;
        ESP_LOGI(TAG,
                 "[RUMBLE_RAW02] queued=true active=%s hold_ms=%u single_writer=true left=%02x%02x%02x%02x%02x right=%02x%02x%02x%02x%02x",
                 active ? "true" : "false",
                 active ? PRO2_RAW02_HOLD_MS : 0,
                 left[0], left[1], left[2], left[3], left[4],
                 right[0], right[1], right[2], right[3], right[4]);
    }
    return ESP_OK;
}
static void rumble_task(void *arg)
{
    (void)arg;
    int64_t next_log_us = 0;

    while (true) {
        if (strcmp(pro2_input_backend_state(), "connected") != 0) {
            vTaskDelay(pdMS_TO_TICKS(PRO2_RUMBLE_TICK_MS));
            continue;
        }

        normalized_rumble_t rumble;
        uint8_t raw02_left[5];
        uint8_t raw02_right[5];
        bool active;
        bool raw02_due;
        pro2_rumble_arbiter_decision_t decision;
        pro2_rumble_host_mode_t host_mode;
        bool hd_candidate_active;
        bool ordinary_candidate_active;
        int64_t now_us = esp_timer_get_time();

        portENTER_CRITICAL(&s_lock);
        memcpy(raw02_left, s_raw02_left, sizeof(raw02_left));
        memcpy(raw02_right, s_raw02_right, sizeof(raw02_right));
        rumble = s_rumble;
        decision = pro2_rumble_arbiter_tick(
            &s_arbiter, now_us, PRO2_RUMBLE_STOP_PACKETS);
        if (decision.source_changed &&
            decision.selected_source == PRO2_RUMBLE_SOURCE_HD) {
            s_raw02_last_write_us = 0;
        }

        raw02_due =
            decision.selected_source == PRO2_RUMBLE_SOURCE_HD &&
            (s_raw02_last_write_us == 0 ||
             now_us - s_raw02_last_write_us >=
                 (int64_t)PRO2_RAW02_REPEAT_MS * 1000LL);
        active =
            decision.selected_source == PRO2_RUMBLE_SOURCE_ORDINARY;
        host_mode = s_arbiter.host_mode;
        hd_candidate_active = s_arbiter.hd_active;
        ordinary_candidate_active = s_arbiter.ordinary_active;
        if (decision.send_stop) {
            normalized_rumble_reset(&rumble);
        }
        uint8_t packet_id = s_packet_id++ & 0x0f;
        portEXIT_CRITICAL(&s_lock);

        if (raw02_due || active || decision.send_stop) {
            uint8_t packet[33];
            if (raw02_due) {
                raw02_build_pro2_packet(packet_id, raw02_left, raw02_right, packet);
            } else {
                build_packet(packet_id, &rumble, packet);
            }
            esp_err_t err = ble_central_send_rumble(packet, sizeof(packet));

            portENTER_CRITICAL(&s_lock);
            if (err == ESP_OK) {
                s_writes++;
                if (!raw02_due) {
                    if (decision.send_stop) {
                        s_stop_writes++;
                    } else {
                        s_ordinary_writes++;
                    }
                } else {
                    s_raw02_last_write_us = now_us;
                    s_raw02_writes++;
                }
            } else if (err != ESP_ERR_INVALID_STATE) {
                s_errors++;
                if (!raw02_due) {
                    s_ordinary_errors++;
                } else {
                    s_raw02_errors++;
                }
            }
            uint32_t writes = s_writes;
            uint32_t errors = s_errors;
            portEXIT_CRITICAL(&s_lock);

            if (decision.source_changed) {
                ESP_LOGI(TAG,
                         "[DS5_RUMBLE_SOURCE] from=%s to=%s host_mode=%s hd_candidate_active=%s ordinary_active=%s",
                         pro2_rumble_backend_source_string(
                             decision.previous_source),
                         pro2_rumble_backend_source_string(
                             decision.selected_source),
                         pro2_rumble_backend_host_mode_string(
                             host_mode),
                         hd_candidate_active ? "true" : "false",
                         ordinary_candidate_active ? "true" : "false");
            }

            if (err == ESP_OK && (raw02_due || active) && now_us >= next_log_us) {
                uint8_t preview_left[5];
                uint8_t preview_right[5];
                next_log_us = now_us + 5000000LL;
                if (raw02_due) {
                    memcpy(preview_left, raw02_left, sizeof(preview_left));
                } else {
                    normalized_rumble_build_pro2_pair(&rumble,
                                                      PRO2_RUMBLE_MAX_AMPLITUDE,
                                                      preview_left,
                                                      preview_right);
                }
                ESP_LOGI(TAG,
                         "[DS5_RUMBLE] tick=true source=%s writes=%lu errors=%lu data=%02x%02x%02x%02x%02x",
                         raw02_due ? "raw02" : "ordinary",
                         (unsigned long)writes,
                         (unsigned long)errors,
                         preview_left[0],
                         preview_left[1],
                         preview_left[2],
                         preview_left[3],
                         preview_left[4]);
            } else if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
                ESP_LOGW(TAG,
                         "[DS5_RUMBLE] tick=false error=%s active=%s stop=%s",
                         esp_err_to_name(err),
                         active ? "true" : "false",
                         decision.send_stop ? "true" : "false");
            }
        }

        vTaskDelay(pdMS_TO_TICKS(PRO2_RUMBLE_TICK_MS));
    }
}

void pro2_rumble_backend_init(void)
{
    normalized_rumble_reset(&s_rumble);
    pro2_rumble_arbiter_init(&s_arbiter);
    if (!s_task_started) {
        BaseType_t created = xTaskCreate(rumble_task,
                                         "ds5_rumble",
                                         4096,
                                         NULL,
                                         4,
                                         &s_rumble_task_handle);
        ESP_ERROR_CHECK(created == pdPASS ? ESP_OK : ESP_FAIL);
        s_task_started = true;
    }
    ESP_LOGI(TAG,
             "[DS5_RUMBLE] initialized=true policy=dualsense_host_intent single_ble_writer=true default_host_mode=audio_haptics max_amp=%u ordinary_hold_ms=%u hd_hold_ms=%u",
             PRO2_RUMBLE_MAX_AMPLITUDE,
             PRO2_RUMBLE_HOLD_MS,
             PRO2_RAW02_HOLD_MS);
}

bool pro2_rumble_backend_handle_dualsense_output(
    uint8_t report_id,
    const uint8_t *buffer,
    uint16_t len)
{
    if (!buffer || len == 0 ||
        (report_id != 0 && report_id != DS5_OUTPUT_REPORT_ID)) {
        return false;
    }

    const uint8_t *payload = buffer;
    uint16_t payload_len = len;
    if (report_id == 0) {
        if (buffer[0] != DS5_OUTPUT_REPORT_ID) {
            return false;
        }
        payload = buffer + 1;
        payload_len--;
    }
    dualsense_rumble_intent_t intent;
    if (!dualsense_rumble_intent_parse(payload, payload_len, &intent)) {
        ESP_LOGW(TAG,
                 "[DS5_RUMBLE] handled=false error=short_payload len=%u",
                 (unsigned)payload_len);
        return false;
    }

    bool nonzero =
        intent.right_light != 0 || intent.left_heavy != 0;
    bool changed;
    uint32_t updates;
    int64_t now_us = esp_timer_get_time();
    uint8_t preview_len = payload_len < PRO2_RUMBLE_OUTPUT_PREVIEW_BYTES ?
        (uint8_t)payload_len : PRO2_RUMBLE_OUTPUT_PREVIEW_BYTES;
    uint8_t log_preview[8] = {0};
    uint8_t log_preview_len = payload_len < sizeof(log_preview) ? (uint8_t)payload_len : sizeof(log_preview);
    memcpy(log_preview, payload, log_preview_len);

    portENTER_CRITICAL(&s_lock);
    changed = intent.right_light != s_last_right ||
              intent.left_heavy != s_last_left ||
              intent.compatibility_selected !=
                  s_last_compatibility_selected ||
              intent.ordinary_active != s_arbiter.ordinary_active;
    s_last_right = intent.right_light;
    s_last_left = intent.left_heavy;
    s_last_compatibility_selected =
        intent.compatibility_selected;
    s_last_compatibility_v1 = intent.compatibility_v1;
    s_last_compatibility_v2 = intent.compatibility_v2;
    s_last_audio_haptics_allowed =
        intent.audio_haptics_allowed;
    s_last_enabled = intent.ordinary_valid;
    s_last_active = intent.ordinary_active;
    s_last_valid_flag0 = intent.valid_flag0;
    s_last_valid_flag1 = intent.valid_flag1;
    s_last_valid_flag2 = intent.valid_flag2;
    s_last_preview_len = preview_len;
    memset(s_last_preview, 0, sizeof(s_last_preview));
    memcpy(s_last_preview, payload, preview_len);
    s_updates++;
    updates = s_updates;
    pro2_rumble_arbiter_set_host_mode(
        &s_arbiter,
        intent.compatibility_selected
            ? PRO2_RUMBLE_HOST_COMPATIBILITY
            : PRO2_RUMBLE_HOST_AUDIO_HAPTICS);
    if (!intent.ordinary_valid) {
        s_non_rumble_updates++;
        if (nonzero) {
            s_ignored_nonzero_updates++;
        }
        s_ordinary_last_update_us = now_us;
        pro2_rumble_arbiter_update_ordinary(
            &s_arbiter, false, now_us, PRO2_RUMBLE_HOLD_MS);
        normalized_rumble_reset(&s_rumble);
    } else if (intent.ordinary_active) {
        s_active_updates++;
        if (s_arbiter.hd_active) {
            s_ordinary_updates_while_hd++;
        }
        normalized_rumble_from_dualsense_motors(intent.right_light,
                                                intent.left_heavy,
                                                PRO2_RUMBLE_HOLD_MS,
                                                &s_rumble);
        s_ordinary_last_update_us = now_us;
        pro2_rumble_arbiter_update_ordinary(
            &s_arbiter, true, now_us, s_rumble.duration_ms);
    } else {
        if (s_arbiter.hd_active) {
            s_ordinary_updates_while_hd++;
        }
        s_ordinary_last_update_us = now_us;
        pro2_rumble_arbiter_update_ordinary(
            &s_arbiter, false, now_us, PRO2_RUMBLE_HOLD_MS);
        normalized_rumble_reset(&s_rumble);
    }
    portEXIT_CRITICAL(&s_lock);

    if (changed || updates == 1) {
        ESP_LOGI(TAG,
                 "[DS5_RUMBLE] handled=true host_mode=%s compatibility=%s v1=%s v2=%s ordinary_valid=%s active=%s flags=%02x/%02x/%02x right_light=%u left_heavy=%u updates=%lu data=%02x%02x%02x%02x%02x%02x%02x%02x",
                 intent.compatibility_selected
                     ? "compatibility"
                     : "audio_haptics",
                 intent.compatibility_selected ? "true" : "false",
                 intent.compatibility_v1 ? "true" : "false",
                 intent.compatibility_v2 ? "true" : "false",
                 intent.ordinary_valid ? "true" : "false",
                 intent.ordinary_active ? "true" : "false",
                 intent.valid_flag0,
                 intent.valid_flag1,
                 intent.valid_flag2,
                 intent.right_light,
                 intent.left_heavy,
                 (unsigned long)updates,
                 log_preview[0], log_preview[1], log_preview[2], log_preview[3],
                 log_preview[4], log_preview[5], log_preview[6], log_preview[7]);
    }
    return true;
}

void pro2_rumble_backend_snapshot(pro2_rumble_backend_stats_t *out)
{
    if (!out) {
        return;
    }

    int64_t now_us = esp_timer_get_time();
    portENTER_CRITICAL(&s_lock);
    memset(out, 0, sizeof(*out));
    out->output_updates = s_updates;
    out->active_updates = s_active_updates;
    out->non_rumble_updates = s_non_rumble_updates;
    out->ignored_nonzero_updates = s_ignored_nonzero_updates;
    out->ordinary_ble_writes = s_ordinary_writes;
    out->ordinary_ble_errors = s_ordinary_errors;
    out->raw02_submissions = s_raw02_submissions;
    out->raw02_ble_writes = s_raw02_writes;
    out->raw02_ble_errors = s_raw02_errors;
    out->stop_ble_writes = s_stop_writes;
    out->source_transitions = s_arbiter.source_transitions;
    out->host_mode_transitions =
        s_arbiter.host_mode_transitions;
    out->audio_haptics_updates =
        s_arbiter.audio_haptics_updates;
    out->compatibility_updates =
        s_arbiter.compatibility_updates;
    out->hd_updates_blocked_by_compatibility =
        s_arbiter.hd_updates_blocked_by_compatibility;
    out->hd_preemptions = s_arbiter.hd_preemptions;
    out->ordinary_fallbacks = s_arbiter.ordinary_fallbacks;
    out->ordinary_updates_while_hd = s_ordinary_updates_while_hd;
    out->task_stack_high_watermark_bytes =
        s_rumble_task_handle ? uxTaskGetStackHighWaterMark(s_rumble_task_handle) : 0;
    out->ordinary_age_us =
        s_ordinary_last_update_us > 0 ? now_us - s_ordinary_last_update_us : -1;
    out->raw02_age_us =
        s_raw02_last_update_us > 0 ? now_us - s_raw02_last_update_us : -1;
    out->host_mode = s_arbiter.host_mode;
    out->selected_source = s_arbiter.selected_source;
    out->ordinary_source_active =
        s_arbiter.ordinary_active && now_us <= s_arbiter.ordinary_until_us;
    out->raw02_source_active =
        s_arbiter.hd_active && now_us <= s_arbiter.hd_until_us;
    out->compatibility_selected =
        s_last_compatibility_selected;
    out->compatibility_v1 = s_last_compatibility_v1;
    out->compatibility_v2 = s_last_compatibility_v2;
    out->audio_haptics_allowed =
        s_last_audio_haptics_allowed;
    out->enabled = s_last_enabled;
    out->active = s_last_active;
    out->valid_flag0 = s_last_valid_flag0;
    out->valid_flag1 = s_last_valid_flag1;
    out->valid_flag2 = s_last_valid_flag2;
    out->right_light = s_last_right;
    out->left_heavy = s_last_left;
    out->preview_len = s_last_preview_len;
    memcpy(out->preview, s_last_preview, sizeof(out->preview));
    portEXIT_CRITICAL(&s_lock);
}
