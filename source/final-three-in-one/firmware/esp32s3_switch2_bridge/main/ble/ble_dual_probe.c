#include <ctype.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "app_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "host/ble_gap.h"
#include "host/ble_gatt.h"
#include "host/ble_hs.h"
#include "host/ble_hs_adv.h"
#include "host/util/util.h"
#include "nimble/ble.h"
#include "os/os_mbuf.h"
#include "switch2_gatt.h"
#include "ble_dual_probe.h"

static const char *TAG = "ble_dual";

#define DUAL_SCAN_DURATION_MS 12000
#define DUAL_CONNECT_TIMEOUT_MS 30000
#define DUAL_MAX_SERVICES 32
#define DUAL_MAX_CHARS 96
#define DUAL_NOTIFY_BUF_MAX 128
#define DUAL_INIT_COMMAND_COUNT 15
#define NINTENDO_COMPANY_ID 0x0553
#define DUAL_FAST_CONN_ITVL_MIN 6
#define DUAL_FAST_CONN_ITVL_MAX 6
#define DUAL_FAST_CONN_LATENCY 0
#define DUAL_FAST_CONN_SUPERVISION_TIMEOUT 400
#define DUAL_FAST_SCAN_ITVL 16
#define DUAL_FAST_SCAN_WINDOW 16
#define DUAL_SIM_DEFAULT_RATE_HZ 133
#define DUAL_SIM_MIN_RATE_HZ 20
#define DUAL_SIM_MAX_RATE_HZ 250

typedef struct {
    uint16_t start_handle;
    uint16_t end_handle;
    char uuid[BLE_UUID_STR_LEN];
} dual_service_t;

typedef struct {
    uint16_t def_handle;
    uint16_t val_handle;
    uint16_t end_handle;
    uint16_t cccd_handle;
    uint8_t properties;
    int service_index;
    char uuid[BLE_UUID_STR_LEN];
    bool ack_target;
    bool notify_target;
    bool post_init_notify_target;
    bool command_target;
    bool subscribed;
} dual_char_t;

typedef enum {
    DUAL_PAD_EMPTY = 0,
    DUAL_PAD_TARGET,
    DUAL_PAD_CONNECTING,
    DUAL_PAD_DISCOVERING,
    DUAL_PAD_INIT,
    DUAL_PAD_READY
} dual_pad_state_t;

typedef enum {
    DUAL_SUBSCRIBE_ACK = 0,
    DUAL_SUBSCRIBE_INPUT
} dual_subscribe_phase_t;

typedef struct {
    const char *name;
    const uint8_t *data;
    uint16_t len;
} dual_init_command_t;

typedef struct {
    uint32_t actual_millihz;
    uint32_t last_gap_us;
    uint32_t max_gap_us;
    int64_t window_start_us;
    int64_t last_event_us;
    uint32_t window_count;
    uint32_t window_max_gap_us;
} dual_rate_t;

typedef struct {
    int index;
    dual_pad_state_t state;
    bool target_valid;
    ble_addr_t target_addr;
    char target_addr_text[32];
    char target_name[32];
    uint16_t conn_handle;
    bool connected;
    bool ready;
    bool simulated;
    uint16_t interval_units;
    uint16_t latency;
    uint16_t supervision_timeout;
    int last_connect_status;
    int last_disconnect_reason;
    int last_update_status;
    uint32_t connect_start_count;
    uint32_t connect_success_count;
    uint32_t connect_failure_count;
    uint32_t disconnect_count;
    uint32_t notify_count;
    uint32_t unique_count;
    uint32_t repeat_count;
    dual_rate_t notify_rate;
    dual_rate_t unique_rate;
    bool last_raw_valid;
    uint16_t last_raw_len;
    uint8_t last_raw[DUAL_NOTIFY_BUF_MAX];
    dual_service_t services[DUAL_MAX_SERVICES];
    dual_char_t chars[DUAL_MAX_CHARS];
    size_t service_count;
    size_t char_count;
    size_t disc_service_index;
    int desc_chr_index;
    int subscribe_index;
    dual_subscribe_phase_t subscribe_phase;
    bool subscribe_task_pending;
    uint16_t cmd_val_handle;
    bool cmd_write_no_rsp;
    uint16_t input_val_handle;
    bool init_started;
    bool init_done;
    size_t init_index;
} dual_pad_t;

static bool s_host_ready;
static bool s_running;
static bool s_scanning;
static uint8_t s_own_addr_type;
static uint32_t s_scan_seen_count;
static uint32_t s_candidate_seen_count;
static uint32_t s_target_count;
static uint32_t s_total_notify_count;
static dual_rate_t s_total_notify_rate;
static dual_pad_t s_pads[BLE_DUAL_PROBE_PAD_COUNT];
static TaskHandle_t s_connect_task;
static esp_timer_handle_t s_sim_timer;
static uint32_t s_sim_seq;
static ble_dual_probe_sim_mode_t s_sim_mode = BLE_DUAL_PROBE_SIM_OFF;
static uint16_t s_sim_rate_hz = DUAL_SIM_DEFAULT_RATE_HZ;

static const struct ble_gap_conn_params s_fast_connect_params = {
    .scan_itvl = DUAL_FAST_SCAN_ITVL,
    .scan_window = DUAL_FAST_SCAN_WINDOW,
    .itvl_min = DUAL_FAST_CONN_ITVL_MIN,
    .itvl_max = DUAL_FAST_CONN_ITVL_MAX,
    .latency = DUAL_FAST_CONN_LATENCY,
    .supervision_timeout = DUAL_FAST_CONN_SUPERVISION_TIMEOUT,
    .min_ce_len = 0,
    .max_ce_len = 0,
};

static const struct ble_gap_upd_params s_fast_update_params = {
    .itvl_min = DUAL_FAST_CONN_ITVL_MIN,
    .itvl_max = DUAL_FAST_CONN_ITVL_MAX,
    .latency = DUAL_FAST_CONN_LATENCY,
    .supervision_timeout = DUAL_FAST_CONN_SUPERVISION_TIMEOUT,
    .min_ce_len = 0,
    .max_ce_len = 0,
};

static const uint8_t s_init_cmd_0[] = {0x03, 0x91, 0x01, 0x0d, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff};
static const uint8_t s_init_cmd_1[] = {0x07, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_2[] = {0x16, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_3[] = {0x15, 0x91, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_4[] = {0x0c, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_5[] = {0x11, 0x91, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_6[] = {0x0a, 0x91, 0x01, 0x08, 0x00, 0x14, 0x00, 0x00, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_7[] = {0x0c, 0x91, 0x01, 0x04, 0x00, 0x04, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_8[] = {0x03, 0x91, 0x01, 0x0a, 0x00, 0x04, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_9[] = {0x10, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_10[] = {0x01, 0x91, 0x01, 0x0c, 0x00, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_11[] = {0x01, 0x91, 0x01, 0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_12[] = {0x09, 0x91, 0x01, 0x07, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
static const uint8_t s_init_cmd_13[] = {0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00, 0x09, 0x7e, 0x00, 0x00, 0xa8, 0x30, 0x01, 0x00};
static const uint8_t s_init_cmd_14[] = {0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00, 0x09, 0x7e, 0x00, 0x00, 0xe8, 0x30, 0x01, 0x00};

static const dual_init_command_t s_init_commands[DUAL_INIT_COMMAND_COUNT] = {
    {"INIT", s_init_cmd_0, sizeof(s_init_cmd_0)},
    {"CMD_07", s_init_cmd_1, sizeof(s_init_cmd_1)},
    {"CMD_16", s_init_cmd_2, sizeof(s_init_cmd_2)},
    {"CMD_15_03", s_init_cmd_3, sizeof(s_init_cmd_3)},
    {"FEATSEL_SET_MASK", s_init_cmd_4, sizeof(s_init_cmd_4)},
    {"CMD_11", s_init_cmd_5, sizeof(s_init_cmd_5)},
    {"VIBRATE_CFG", s_init_cmd_6, sizeof(s_init_cmd_6)},
    {"FEATSEL_ENABLE", s_init_cmd_7, sizeof(s_init_cmd_7)},
    {"SELECT_REPORT", s_init_cmd_8, sizeof(s_init_cmd_8)},
    {"FW_INFO_GET", s_init_cmd_9, sizeof(s_init_cmd_9)},
    {"CMD_01_0C", s_init_cmd_10, sizeof(s_init_cmd_10)},
    {"RUMBLE_ENABLE", s_init_cmd_11, sizeof(s_init_cmd_11)},
    {"SET_PLAYER_LED", s_init_cmd_12, sizeof(s_init_cmd_12)},
    {"CALIB_LEFT", s_init_cmd_13, sizeof(s_init_cmd_13)},
    {"CALIB_RIGHT", s_init_cmd_14, sizeof(s_init_cmd_14)},
};

static int dual_gap_event(struct ble_gap_event *event, void *arg);
static void dual_schedule_connect_next(uint32_t delay_ms);
static void reset_slot(dual_pad_t *pad, int index);

static void format_addr(const ble_addr_t *addr, char *out, size_t out_len)
{
    snprintf(out, out_len, "%02x:%02x:%02x:%02x:%02x:%02x/%u",
             addr->val[5], addr->val[4], addr->val[3],
             addr->val[2], addr->val[1], addr->val[0],
             addr->type);
}

static bool same_addr_value(const ble_addr_t *a, const ble_addr_t *b)
{
    return a && b && memcmp(a->val, b->val, sizeof(a->val)) == 0;
}

static bool contains_ci(const char *haystack, const char *needle)
{
    if (!haystack || !needle || !needle[0]) {
        return false;
    }

    size_t needle_len = strlen(needle);
    for (const char *p = haystack; *p; p++) {
        size_t i = 0;
        while (i < needle_len && p[i] &&
               tolower((unsigned char)p[i]) == tolower((unsigned char)needle[i])) {
            i++;
        }
        if (i == needle_len) {
            return true;
        }
    }
    return false;
}

static void uuid_to_lower_string(const ble_uuid_t *uuid, char *out, size_t out_len)
{
    if (!uuid || !out || out_len == 0) {
        return;
    }

    ble_uuid_to_str(uuid, out);
    out[out_len - 1] = 0;
    for (char *p = out; *p; p++) {
        *p = (char)tolower((unsigned char)*p);
    }
}

static void copy_adv_name(const struct ble_hs_adv_fields *fields, char *out, size_t out_len)
{
    if (!fields->name || fields->name_len == 0 || out_len == 0) {
        if (out_len > 0) {
            out[0] = 0;
        }
        return;
    }

    size_t n = fields->name_len;
    if (n >= out_len) {
        n = out_len - 1;
    }
    memcpy(out, fields->name, n);
    out[n] = 0;
}

static bool name_looks_like_switch_controller(const char *name)
{
    return contains_ci(name, "switch") ||
           contains_ci(name, "nintendo") ||
           contains_ci(name, "pro controller") ||
           contains_ci(name, "pro2");
}

static bool adv_event_connectable(uint8_t event_type)
{
    return event_type == BLE_HCI_ADV_RPT_EVTYPE_ADV_IND ||
           event_type == BLE_HCI_ADV_RPT_EVTYPE_DIR_IND;
}

static bool adv_has_company_id(const struct ble_hs_adv_fields *fields, uint16_t company_id)
{
    return fields->mfg_data &&
           fields->mfg_data_len >= 2 &&
           ((uint16_t)fields->mfg_data[0] | ((uint16_t)fields->mfg_data[1] << 8)) == company_id;
}

static bool is_ack_uuid(const char *uuid)
{
    return uuid && strcmp(uuid, SWITCH2_ACK_UUID) == 0;
}

static bool is_input_uuid(const char *uuid)
{
    return uuid && strcmp(uuid, SWITCH2_NOTIFY_FD2_UUID) == 0;
}

static bool is_command_uuid(const char *uuid)
{
    return uuid && strcmp(uuid, SWITCH2_CMD_UUID) == 0;
}

static void update_rate(dual_rate_t *rate, int64_t now_us)
{
    if (!rate) {
        return;
    }
    if (rate->window_start_us == 0) {
        rate->window_start_us = now_us;
    }
    if (rate->last_event_us > 0 && now_us > rate->last_event_us) {
        uint32_t gap_us = (uint32_t)(now_us - rate->last_event_us);
        rate->last_gap_us = gap_us;
        if (gap_us > rate->window_max_gap_us) {
            rate->window_max_gap_us = gap_us;
        }
    }
    rate->last_event_us = now_us;
    rate->window_count++;

    int64_t elapsed_us = now_us - rate->window_start_us;
    if (elapsed_us >= 1000000LL) {
        rate->actual_millihz = (uint32_t)(((uint64_t)rate->window_count * 1000000000ULL +
                                           (uint64_t)(elapsed_us / 2)) /
                                          (uint64_t)elapsed_us);
        rate->max_gap_us = rate->window_max_gap_us;
        rate->window_count = 0;
        rate->window_max_gap_us = 0;
        rate->window_start_us = now_us;
    }
}

static void record_pad_payload(dual_pad_t *pad,
                               const uint8_t *data,
                               uint16_t len,
                               int64_t now_us,
                               bool log_sample)
{
    if (!pad || !data || len == 0) {
        return;
    }

    uint16_t copy_len = len > DUAL_NOTIFY_BUF_MAX ? DUAL_NOTIFY_BUF_MAX : len;
    pad->notify_count++;
    s_total_notify_count++;
    update_rate(&pad->notify_rate, now_us);
    update_rate(&s_total_notify_rate, now_us);

    bool repeat = pad->last_raw_valid &&
                  pad->last_raw_len == copy_len &&
                  memcmp(pad->last_raw, data, copy_len) == 0;
    if (repeat) {
        pad->repeat_count++;
    } else {
        pad->unique_count++;
        update_rate(&pad->unique_rate, now_us);
    }

    memcpy(pad->last_raw, data, copy_len);
    pad->last_raw_len = copy_len;
    pad->last_raw_valid = true;

    if (log_sample && (pad->notify_count & 0x7f) == 1) {
        APP_LOGI(TAG,
                 "dual notify pad=%d source=%s count=%lu len=%u notify_hz=%lu unique_hz=%lu repeat=%lu",
                 pad->index,
                 pad->simulated ? "sim" : "ble",
                 (unsigned long)pad->notify_count,
                 (unsigned)copy_len,
                 (unsigned long)((pad->notify_rate.actual_millihz + 500u) / 1000u),
                 (unsigned long)((pad->unique_rate.actual_millihz + 500u) / 1000u),
                 (unsigned long)pad->repeat_count);
    }
}

static void ensure_simulated_pad(int index, const char *name)
{
    if (index < 0 || index >= BLE_DUAL_PROBE_PAD_COUNT) {
        return;
    }

    dual_pad_t *pad = &s_pads[index];
    if (pad->connected && !pad->simulated) {
        return;
    }

    pad->target_valid = true;
    pad->connected = true;
    pad->ready = true;
    pad->simulated = true;
    pad->state = DUAL_PAD_READY;
    pad->conn_handle = (uint16_t)(0xff00u + (uint16_t)index);
    pad->input_val_handle = 0xfffd;
    pad->interval_units = DUAL_FAST_CONN_ITVL_MIN;
    pad->latency = 0;
    pad->supervision_timeout = DUAL_FAST_CONN_SUPERVISION_TIMEOUT;
    snprintf(pad->target_addr_text,
             sizeof(pad->target_addr_text),
             "simulated:%d",
             index);
    snprintf(pad->target_name,
             sizeof(pad->target_name),
             "%s",
             name && name[0] ? name : "Simulated Pro2");
    if (s_target_count < BLE_DUAL_PROBE_PAD_COUNT) {
        uint32_t real_or_sim_targets = 0;
        for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
            if (s_pads[i].target_valid) {
                real_or_sim_targets++;
            }
        }
        s_target_count = real_or_sim_targets;
    }
}

static void clear_simulated_pads(void)
{
    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        dual_pad_t *pad = &s_pads[i];
        if (pad->simulated) {
            reset_slot(pad, i);
        }
    }
}

static void mirror_payload_to_missing_pad(const dual_pad_t *source,
                                          const uint8_t *data,
                                          uint16_t len,
                                          int64_t now_us)
{
    if (s_sim_mode != BLE_DUAL_PROBE_SIM_MIRROR || !source || source->simulated) {
        return;
    }

    int target_index = source->index == 0 ? 1 : 0;
    dual_pad_t *target = &s_pads[target_index];
    if (target->connected && !target->simulated) {
        return;
    }

    ensure_simulated_pad(target_index, "Mirror of real Pro2");
    record_pad_payload(target, data, len, now_us, true);
}

static uint32_t sanitize_sim_rate(uint16_t rate_hz)
{
    if (rate_hz == 0) {
        return DUAL_SIM_DEFAULT_RATE_HZ;
    }
    if (rate_hz < DUAL_SIM_MIN_RATE_HZ) {
        return DUAL_SIM_MIN_RATE_HZ;
    }
    if (rate_hz > DUAL_SIM_MAX_RATE_HZ) {
        return DUAL_SIM_MAX_RATE_HZ;
    }
    return rate_hz;
}

static void stop_sim_task(void)
{
    if (s_sim_timer) {
        (void)esp_timer_stop(s_sim_timer);
        (void)esp_timer_delete(s_sim_timer);
        s_sim_timer = NULL;
    }
}

static void dual_synthetic_timer_cb(void *arg)
{
    (void)arg;
    if (s_sim_mode != BLE_DUAL_PROBE_SIM_SYNTHETIC) {
        return;
    }

    uint32_t seq = s_sim_seq++;
    int64_t now_us = esp_timer_get_time();
    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        ensure_simulated_pad(i, i == 0 ? "Synthetic Pro2 A" : "Synthetic Pro2 B");
        uint8_t payload[16] = {
            0x7f, 0xd2, (uint8_t)i, (uint8_t)(seq & 0xff),
            (uint8_t)((seq >> 8) & 0xff),
            (uint8_t)((seq >> 16) & 0xff),
            (uint8_t)((seq >> 24) & 0xff),
            (uint8_t)(0xa0u + (uint8_t)i),
            (uint8_t)(now_us & 0xff),
            (uint8_t)((now_us >> 8) & 0xff),
            (uint8_t)((now_us >> 16) & 0xff),
            (uint8_t)((now_us >> 24) & 0xff),
            0x33, 0x55, 0x77, 0x99
        };
        record_pad_payload(&s_pads[i], payload, sizeof(payload), now_us, true);
    }
}

static esp_err_t start_synthetic_task(void)
{
    if (s_sim_timer) {
        return ESP_OK;
    }

    uint32_t rate_hz = sanitize_sim_rate(s_sim_rate_hz);
    uint64_t period_us = 1000000ULL / rate_hz;
    if (period_us == 0) {
        period_us = 1;
    }
    s_sim_seq = 0;
    const esp_timer_create_args_t args = {
        .callback = dual_synthetic_timer_cb,
        .arg = NULL,
        .dispatch_method = ESP_TIMER_TASK,
        .name = "dual_sim"
    };
    esp_err_t err = esp_timer_create(&args, &s_sim_timer);
    if (err != ESP_OK) {
        s_sim_timer = NULL;
        APP_LOGE(TAG, "dual synthetic simulator timer create failed err=%d", err);
        return ESP_FAIL;
    }
    err = esp_timer_start_periodic(s_sim_timer, period_us);
    if (err != ESP_OK) {
        (void)esp_timer_delete(s_sim_timer);
        s_sim_timer = NULL;
        APP_LOGE(TAG, "dual synthetic simulator timer start failed err=%d", err);
        return ESP_FAIL;
    }
    APP_LOGI(TAG,
             "dual synthetic simulator started rate_hz=%lu period_us=%llu",
             (unsigned long)rate_hz,
             (unsigned long long)period_us);
    return ESP_OK;
}

static void clear_slot_gatt(dual_pad_t *pad)
{
    memset(pad->services, 0, sizeof(pad->services));
    memset(pad->chars, 0, sizeof(pad->chars));
    pad->service_count = 0;
    pad->char_count = 0;
    pad->disc_service_index = 0;
    pad->desc_chr_index = -1;
    pad->subscribe_index = -1;
    pad->subscribe_phase = DUAL_SUBSCRIBE_ACK;
    pad->subscribe_task_pending = false;
    pad->cmd_val_handle = 0;
    pad->cmd_write_no_rsp = false;
    pad->input_val_handle = 0;
    pad->init_started = false;
    pad->init_done = false;
    pad->init_index = 0;
}

static void reset_slot(dual_pad_t *pad, int index)
{
    memset(pad, 0, sizeof(*pad));
    pad->index = index;
    pad->state = DUAL_PAD_EMPTY;
    pad->last_connect_status = -1;
    pad->last_disconnect_reason = -1;
    pad->last_update_status = -1;
    clear_slot_gatt(pad);
}

static dual_pad_t *find_pad_by_conn(uint16_t conn_handle)
{
    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        if (s_pads[i].connected && s_pads[i].conn_handle == conn_handle) {
            return &s_pads[i];
        }
    }
    return NULL;
}

static dual_char_t *find_char_by_value_handle(dual_pad_t *pad, uint16_t value_handle)
{
    if (!pad) {
        return NULL;
    }
    for (size_t i = 0; i < pad->char_count; i++) {
        if (pad->chars[i].val_handle == value_handle) {
            return &pad->chars[i];
        }
    }
    return NULL;
}

static void reset_probe_state(void)
{
    s_scanning = false;
    s_scan_seen_count = 0;
    s_candidate_seen_count = 0;
    s_target_count = 0;
    s_total_notify_count = 0;
    memset(&s_total_notify_rate, 0, sizeof(s_total_notify_rate));
    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        reset_slot(&s_pads[i], i);
    }
}

static bool target_already_known(const ble_addr_t *addr)
{
    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        if (s_pads[i].target_valid && same_addr_value(addr, &s_pads[i].target_addr)) {
            return true;
        }
    }
    return false;
}

static void remember_candidate(const struct ble_gap_disc_desc *disc, const char *name)
{
    if (s_target_count >= BLE_DUAL_PROBE_PAD_COUNT || target_already_known(&disc->addr)) {
        return;
    }

    dual_pad_t *pad = &s_pads[s_target_count];
    pad->target_valid = true;
    pad->target_addr = disc->addr;
    pad->state = DUAL_PAD_TARGET;
    format_addr(&disc->addr, pad->target_addr_text, sizeof(pad->target_addr_text));
    snprintf(pad->target_name, sizeof(pad->target_name), "%s", name && name[0] ? name : "<unnamed>");
    s_target_count++;

    APP_LOGI(TAG,
             "dual Pro2 target pad=%d addr=%s name=\"%s\" rssi=%d",
             pad->index,
             pad->target_addr_text,
             pad->target_name,
             disc->rssi);

    if (s_target_count >= BLE_DUAL_PROBE_PAD_COUNT && ble_gap_disc_active()) {
        int rc = ble_gap_disc_cancel();
        if (rc != 0) {
            APP_LOGW(TAG, "dual scan cancel after target fill rc=%d", rc);
        }
    }
}

static void handle_scan_report(const struct ble_gap_disc_desc *disc)
{
    s_scan_seen_count++;

    struct ble_hs_adv_fields fields;
    int rc = ble_hs_adv_parse_fields(&fields, disc->data, disc->length_data);
    if (rc != 0) {
        APP_LOGW(TAG, "dual scan parse failed seen=%lu rc=%d",
                 (unsigned long)s_scan_seen_count,
                 rc);
        return;
    }

    char name[32];
    copy_adv_name(&fields, name, sizeof(name));
    bool nintendo_mfg = adv_has_company_id(&fields, NINTENDO_COMPANY_ID);
    bool candidate = adv_event_connectable(disc->event_type) &&
                     (name_looks_like_switch_controller(name) || nintendo_mfg);
    if (!candidate) {
        return;
    }

    s_candidate_seen_count++;
    remember_candidate(disc, name);
}

static void update_conn_desc(dual_pad_t *pad, const char *reason)
{
    struct ble_gap_conn_desc desc;
    int rc = ble_gap_conn_find(pad->conn_handle, &desc);
    if (rc != 0) {
        APP_LOGW(TAG, "dual conn desc failed pad=%d reason=%s rc=%d",
                 pad->index,
                 reason ? reason : "<none>",
                 rc);
        return;
    }

    pad->interval_units = desc.conn_itvl;
    pad->latency = desc.conn_latency;
    pad->supervision_timeout = desc.supervision_timeout;
    APP_LOGI(TAG,
             "dual conn params pad=%d reason=%s interval_units=%u interval_us=%lu latency=%u supervision=%u",
             pad->index,
             reason ? reason : "<none>",
             (unsigned)pad->interval_units,
             (unsigned long)pad->interval_units * 1250UL,
             (unsigned)pad->latency,
             (unsigned)pad->supervision_timeout);
}

static void request_fast_params(dual_pad_t *pad, const char *reason)
{
    int rc = ble_gap_update_params(pad->conn_handle, &s_fast_update_params);
    APP_LOGI(TAG,
             "dual conn fast params pad=%d reason=%s rc=%d",
             pad->index,
             reason ? reason : "<none>",
             rc);
}

static bool subscribe_target_for_phase(dual_pad_t *pad, const dual_char_t *chr)
{
    if (pad->subscribe_phase == DUAL_SUBSCRIBE_ACK) {
        return chr->ack_target;
    }
    return chr->post_init_notify_target;
}

static void dual_subscribe_next(dual_pad_t *pad);

static void dual_subscribe_next_task(void *arg)
{
    dual_pad_t *pad = (dual_pad_t *)arg;
    vTaskDelay(pdMS_TO_TICKS(1));
    pad->subscribe_task_pending = false;
    dual_subscribe_next(pad);
    vTaskDelete(NULL);
}

static void schedule_subscribe_next(dual_pad_t *pad)
{
    if (!pad || pad->subscribe_task_pending) {
        return;
    }
    pad->subscribe_task_pending = true;
    BaseType_t ok = xTaskCreate(dual_subscribe_next_task,
                                "dual_sub_next",
                                4096,
                                pad,
                                5,
                                NULL);
    if (ok != pdPASS) {
        pad->subscribe_task_pending = false;
        APP_LOGE(TAG, "dual subscribe scheduler failed pad=%d", pad->index);
    }
}

static void dual_start_post_init_subscriptions(dual_pad_t *pad)
{
    pad->subscribe_phase = DUAL_SUBSCRIBE_INPUT;
    pad->subscribe_index = -1;
    APP_LOGI(TAG, "dual post-init subscribe start pad=%d", pad->index);
    schedule_subscribe_next(pad);
}

static void dual_send_current_init_command(dual_pad_t *pad)
{
    if (!pad->connected || pad->cmd_val_handle == 0) {
        APP_LOGW(TAG,
                 "dual init stopped pad=%d connected=%s cmd=0x%04x",
                 pad->index,
                 pad->connected ? "yes" : "no",
                 pad->cmd_val_handle);
        return;
    }

    if (pad->init_index >= DUAL_INIT_COMMAND_COUNT) {
        pad->init_done = true;
        APP_LOGI(TAG, "dual init complete pad=%d; enabling input notifications", pad->index);
        dual_start_post_init_subscriptions(pad);
        return;
    }

    const dual_init_command_t *cmd = &s_init_commands[pad->init_index];
    int rc = pad->cmd_write_no_rsp ?
        ble_gattc_write_no_rsp_flat(pad->conn_handle, pad->cmd_val_handle, cmd->data, cmd->len) :
        ble_gattc_write_flat(pad->conn_handle, pad->cmd_val_handle, cmd->data, cmd->len, NULL, NULL);
    if (rc != 0) {
        APP_LOGW(TAG,
                 "dual init send failed pad=%d index=%u name=%s rc=%d",
                 pad->index,
                 (unsigned)pad->init_index,
                 cmd->name,
                 rc);
        return;
    }

    APP_LOGI(TAG,
             "dual init send pad=%d index=%u/%u name=%s len=%u",
             pad->index,
             (unsigned)pad->init_index,
             (unsigned)DUAL_INIT_COMMAND_COUNT,
             cmd->name,
             (unsigned)cmd->len);
}

static void dual_advance_init_from_ack(dual_pad_t *pad, const uint8_t *data, uint16_t len)
{
    if (!pad->init_started || pad->init_done) {
        return;
    }

    APP_LOGI(TAG,
             "dual init ACK pad=%d index=%u len=%u first=0x%02x",
             pad->index,
             (unsigned)pad->init_index,
             (unsigned)len,
             len > 0 ? data[0] : 0);
    pad->init_index++;
    dual_send_current_init_command(pad);
}

static int dual_subscribe_write_cb(uint16_t conn_handle,
                                   const struct ble_gatt_error *error,
                                   struct ble_gatt_attr *attr,
                                   void *arg)
{
    (void)conn_handle;
    (void)attr;
    dual_pad_t *pad = (dual_pad_t *)arg;
    if (!pad) {
        return 0;
    }

    if (pad->subscribe_index >= 0 && pad->subscribe_index < (int)pad->char_count) {
        dual_char_t *chr = &pad->chars[pad->subscribe_index];
        if (error->status == 0) {
            chr->subscribed = true;
            APP_LOGI(TAG, "dual subscribe ok pad=%d uuid=%s", pad->index, chr->uuid);
        } else {
            APP_LOGW(TAG,
                     "dual subscribe failed pad=%d uuid=%s status=%d",
                     pad->index,
                     chr->uuid,
                     error->status);
        }
    }

    schedule_subscribe_next(pad);
    return 0;
}

static void dual_subscribe_next(dual_pad_t *pad)
{
    uint8_t enable_notify[2] = {0x01, 0x00};

    for (int i = pad->subscribe_index + 1; i < (int)pad->char_count; i++) {
        dual_char_t *chr = &pad->chars[i];
        if (!subscribe_target_for_phase(pad, chr)) {
            continue;
        }

        uint16_t cccd_handle = chr->cccd_handle ? chr->cccd_handle : chr->val_handle + 1;
        pad->subscribe_index = i;
        int rc = ble_gattc_write_flat(pad->conn_handle,
                                      cccd_handle,
                                      enable_notify,
                                      sizeof(enable_notify),
                                      dual_subscribe_write_cb,
                                      pad);
        if (rc != 0) {
            APP_LOGW(TAG,
                     "dual subscribe start failed pad=%d uuid=%s cccd=0x%04x rc=%d",
                     pad->index,
                     chr->uuid,
                     cccd_handle,
                     rc);
            continue;
        }

        APP_LOGI(TAG,
                 "dual subscribe start pad=%d uuid=%s value=0x%04x cccd=0x%04x",
                 pad->index,
                 chr->uuid,
                 chr->val_handle,
                 cccd_handle);
        return;
    }

    if (pad->subscribe_phase == DUAL_SUBSCRIBE_ACK) {
        APP_LOGI(TAG, "dual ACK subscribe complete pad=%d cmd=0x%04x", pad->index, pad->cmd_val_handle);
        if (pad->cmd_val_handle == 0) {
            APP_LOGW(TAG, "dual init skipped pad=%d; command characteristic missing", pad->index);
            dual_start_post_init_subscriptions(pad);
            return;
        }
        pad->state = DUAL_PAD_INIT;
        pad->init_started = true;
        pad->init_done = false;
        pad->init_index = 0;
        dual_send_current_init_command(pad);
        return;
    }

    pad->state = DUAL_PAD_READY;
    pad->ready = true;
    APP_LOGI(TAG,
             "dual Pro2 pad ready pad=%d input=0x%04x target=%s",
             pad->index,
             pad->input_val_handle,
             pad->target_addr_text);
    dual_schedule_connect_next(50);
}

static void dual_start_next_descriptor_discovery(dual_pad_t *pad);

static int dual_dsc_cb(uint16_t conn_handle,
                       const struct ble_gatt_error *error,
                       uint16_t chr_val_handle,
                       const struct ble_gatt_dsc *dsc,
                       void *arg)
{
    (void)conn_handle;
    (void)chr_val_handle;
    dual_pad_t *pad = (dual_pad_t *)arg;
    if (!pad || pad->desc_chr_index < 0 || pad->desc_chr_index >= (int)pad->char_count) {
        return 0;
    }

    dual_char_t *chr = &pad->chars[pad->desc_chr_index];
    if (error->status == 0) {
        if (dsc->uuid.u.type == BLE_UUID_TYPE_16 &&
            ble_uuid_u16(&dsc->uuid.u) == BLE_GATT_DSC_CLT_CFG_UUID16) {
            chr->cccd_handle = dsc->handle;
        }
        return 0;
    }

    if (error->status == BLE_HS_EDONE) {
        dual_start_next_descriptor_discovery(pad);
        return 0;
    }

    APP_LOGW(TAG,
             "dual descriptor discovery failed pad=%d chr=%s status=%d",
             pad->index,
             chr->uuid,
             error->status);
    dual_start_next_descriptor_discovery(pad);
    return 0;
}

static void dual_start_next_descriptor_discovery(dual_pad_t *pad)
{
    for (int i = pad->desc_chr_index + 1; i < (int)pad->char_count; i++) {
        dual_char_t *chr = &pad->chars[i];
        if (!chr->notify_target || chr->end_handle <= chr->val_handle) {
            continue;
        }

        pad->desc_chr_index = i;
        int rc = ble_gattc_disc_all_dscs(pad->conn_handle,
                                         chr->val_handle,
                                         chr->end_handle,
                                         dual_dsc_cb,
                                         pad);
        if (rc != 0) {
            APP_LOGW(TAG,
                     "dual descriptor discovery start failed pad=%d chr=%s rc=%d",
                     pad->index,
                     chr->uuid,
                     rc);
            continue;
        }

        return;
    }

    pad->subscribe_index = -1;
    schedule_subscribe_next(pad);
}

static void dual_finalize_character_end_handles(dual_pad_t *pad)
{
    for (size_t i = 0; i < pad->char_count; i++) {
        uint16_t end_handle = pad->services[pad->chars[i].service_index].end_handle;
        for (size_t j = 0; j < pad->char_count; j++) {
            if (i == j || pad->chars[j].service_index != pad->chars[i].service_index) {
                continue;
            }
            if (pad->chars[j].def_handle > pad->chars[i].def_handle &&
                pad->chars[j].def_handle - 1 < end_handle) {
                end_handle = pad->chars[j].def_handle - 1;
            }
        }
        pad->chars[i].end_handle = end_handle;
    }
}

static void dual_start_next_characteristic_discovery(dual_pad_t *pad);

static int dual_chr_cb(uint16_t conn_handle,
                       const struct ble_gatt_error *error,
                       const struct ble_gatt_chr *chr,
                       void *arg)
{
    (void)conn_handle;
    dual_pad_t *pad = (dual_pad_t *)arg;
    if (!pad) {
        return 0;
    }

    if (error->status == 0) {
        if (pad->char_count >= DUAL_MAX_CHARS) {
            APP_LOGW(TAG, "dual char cache full pad=%d value=0x%04x", pad->index, chr->val_handle);
            return 0;
        }

        dual_char_t *out = &pad->chars[pad->char_count++];
        memset(out, 0, sizeof(*out));
        out->def_handle = chr->def_handle;
        out->val_handle = chr->val_handle;
        out->properties = chr->properties;
        out->service_index = (int)pad->disc_service_index;
        uuid_to_lower_string(&chr->uuid.u, out->uuid, sizeof(out->uuid));
        out->ack_target = is_ack_uuid(out->uuid);
        out->post_init_notify_target = is_input_uuid(out->uuid);
        out->notify_target = out->ack_target || out->post_init_notify_target;
        out->command_target = is_command_uuid(out->uuid);
        if (out->post_init_notify_target) {
            pad->input_val_handle = out->val_handle;
        }
        if (out->command_target) {
            pad->cmd_val_handle = out->val_handle;
            pad->cmd_write_no_rsp = (out->properties & BLE_GATT_CHR_F_WRITE_NO_RSP) != 0;
        }

        if (out->notify_target || out->command_target) {
            APP_LOGI(TAG,
                     "dual char pad=%d value=0x%04x uuid=%s target=%s",
                     pad->index,
                     out->val_handle,
                     out->uuid,
                     out->ack_target ? "ack" :
                     (out->post_init_notify_target ? "input" :
                     (out->command_target ? "cmd" : "no")));
        }
        return 0;
    }

    if (error->status == BLE_HS_EDONE) {
        pad->disc_service_index++;
        dual_start_next_characteristic_discovery(pad);
        return 0;
    }

    APP_LOGW(TAG,
             "dual characteristic discovery failed pad=%d status=%d",
             pad->index,
             error->status);
    pad->disc_service_index++;
    dual_start_next_characteristic_discovery(pad);
    return 0;
}

static void dual_start_next_characteristic_discovery(dual_pad_t *pad)
{
    while (pad->disc_service_index < pad->service_count) {
        dual_service_t *svc = &pad->services[pad->disc_service_index];
        if (svc->end_handle <= svc->start_handle) {
            pad->disc_service_index++;
            continue;
        }

        int rc = ble_gattc_disc_all_chrs(pad->conn_handle,
                                         svc->start_handle,
                                         svc->end_handle,
                                         dual_chr_cb,
                                         pad);
        if (rc != 0) {
            APP_LOGW(TAG,
                     "dual characteristic discovery start failed pad=%d svc=%s rc=%d",
                     pad->index,
                     svc->uuid,
                     rc);
            pad->disc_service_index++;
            continue;
        }
        return;
    }

    dual_finalize_character_end_handles(pad);
    APP_LOGI(TAG,
             "dual characteristic discovery complete pad=%d chars=%u input=0x%04x cmd=0x%04x",
             pad->index,
             (unsigned)pad->char_count,
             pad->input_val_handle,
             pad->cmd_val_handle);
    pad->desc_chr_index = -1;
    dual_start_next_descriptor_discovery(pad);
}

static int dual_svc_cb(uint16_t conn_handle,
                       const struct ble_gatt_error *error,
                       const struct ble_gatt_svc *service,
                       void *arg)
{
    (void)conn_handle;
    dual_pad_t *pad = (dual_pad_t *)arg;
    if (!pad) {
        return 0;
    }

    if (error->status == 0) {
        if (pad->service_count >= DUAL_MAX_SERVICES) {
            APP_LOGW(TAG, "dual service cache full pad=%d start=0x%04x", pad->index, service->start_handle);
            return 0;
        }

        dual_service_t *out = &pad->services[pad->service_count++];
        out->start_handle = service->start_handle;
        out->end_handle = service->end_handle;
        uuid_to_lower_string(&service->uuid.u, out->uuid, sizeof(out->uuid));
        return 0;
    }

    if (error->status == BLE_HS_EDONE) {
        APP_LOGI(TAG,
                 "dual service discovery complete pad=%d services=%u",
                 pad->index,
                 (unsigned)pad->service_count);
        pad->disc_service_index = 0;
        dual_start_next_characteristic_discovery(pad);
        return 0;
    }

    APP_LOGW(TAG,
             "dual service discovery failed pad=%d status=%d",
             pad->index,
             error->status);
    return 0;
}

static void dual_start_service_discovery(dual_pad_t *pad)
{
    int rc = ble_gattc_disc_all_svcs(pad->conn_handle, dual_svc_cb, pad);
    if (rc != 0) {
        APP_LOGE(TAG, "dual service discovery start failed pad=%d rc=%d", pad->index, rc);
    } else {
        APP_LOGI(TAG, "dual service discovery start pad=%d", pad->index);
    }
}

static int dual_mtu_cb(uint16_t conn_handle,
                       const struct ble_gatt_error *error,
                       uint16_t mtu,
                       void *arg)
{
    (void)conn_handle;
    dual_pad_t *pad = (dual_pad_t *)arg;
    if (!pad) {
        return 0;
    }

    if (error->status == 0) {
        APP_LOGI(TAG, "dual MTU exchange ok pad=%d mtu=%u", pad->index, mtu);
    } else {
        APP_LOGW(TAG, "dual MTU exchange failed pad=%d status=%d", pad->index, error->status);
    }
    dual_start_service_discovery(pad);
    return 0;
}

static void dual_start_gatt(dual_pad_t *pad)
{
    clear_slot_gatt(pad);
    pad->state = DUAL_PAD_DISCOVERING;
    int rc = ble_gattc_exchange_mtu(pad->conn_handle, dual_mtu_cb, pad);
    if (rc != 0) {
        APP_LOGW(TAG, "dual MTU exchange start failed pad=%d rc=%d", pad->index, rc);
        dual_start_service_discovery(pad);
    } else {
        APP_LOGI(TAG, "dual MTU exchange start pad=%d", pad->index);
    }
}

static void handle_notify_rx(const struct ble_gap_event *event, void *arg)
{
    dual_pad_t *pad = arg ? (dual_pad_t *)arg : find_pad_by_conn(event->notify_rx.conn_handle);
    if (!pad) {
        APP_LOGW(TAG, "dual notify for unknown conn=%u", event->notify_rx.conn_handle);
        return;
    }

    uint16_t len = OS_MBUF_PKTLEN(event->notify_rx.om);
    uint16_t copy_len = len > DUAL_NOTIFY_BUF_MAX ? DUAL_NOTIFY_BUF_MAX : len;
    uint8_t data[DUAL_NOTIFY_BUF_MAX];
    int rc = os_mbuf_copydata(event->notify_rx.om, 0, copy_len, data);
    if (rc != 0) {
        APP_LOGW(TAG, "dual notify copy failed pad=%d len=%u rc=%d", pad->index, len, rc);
        return;
    }

    dual_char_t *chr = find_char_by_value_handle(pad, event->notify_rx.attr_handle);
    const char *uuid = chr ? chr->uuid : "<unknown>";
    if (chr && chr->ack_target) {
        dual_advance_init_from_ack(pad, data, copy_len);
        return;
    }
    if (!chr || !is_input_uuid(uuid)) {
        return;
    }

    int64_t now_us = esp_timer_get_time();
    record_pad_payload(pad, data, copy_len, now_us, true);
    mirror_payload_to_missing_pad(pad, data, copy_len, now_us);
}

static bool any_pad_connecting(void)
{
    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        if (s_pads[i].state == DUAL_PAD_CONNECTING) {
            return true;
        }
    }
    return false;
}

static void dual_connect_next(void)
{
    if (!s_running || !s_host_ready || s_scanning || any_pad_connecting()) {
        return;
    }
    if (ble_gap_disc_active() || ble_gap_conn_active()) {
        dual_schedule_connect_next(100);
        return;
    }

    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        dual_pad_t *pad = &s_pads[i];
        if (!pad->target_valid || pad->connected || pad->ready) {
            continue;
        }

        pad->state = DUAL_PAD_CONNECTING;
        pad->connect_start_count++;
        APP_LOGI(TAG,
                 "dual connect start pad=%d target=%s name=\"%s\"",
                 pad->index,
                 pad->target_addr_text,
                 pad->target_name);
        int rc = ble_gap_connect(s_own_addr_type,
                                 &pad->target_addr,
                                 DUAL_CONNECT_TIMEOUT_MS,
                                 &s_fast_connect_params,
                                 dual_gap_event,
                                 pad);
        if (rc != 0) {
            pad->state = DUAL_PAD_TARGET;
            pad->connect_failure_count++;
            pad->last_connect_status = rc;
            APP_LOGW(TAG, "dual connect start failed pad=%d rc=%d", pad->index, rc);
            continue;
        }
        return;
    }
}

static void dual_connect_task(void *arg)
{
    uint32_t delay_ms = (uint32_t)(uintptr_t)arg;
    if (delay_ms > 0) {
        vTaskDelay(pdMS_TO_TICKS(delay_ms));
    }
    s_connect_task = NULL;
    dual_connect_next();
    vTaskDelete(NULL);
}

static void dual_schedule_connect_next(uint32_t delay_ms)
{
    if (s_connect_task || !s_running) {
        return;
    }
    BaseType_t ok = xTaskCreate(dual_connect_task,
                                "dual_connect",
                                4096,
                                (void *)(uintptr_t)delay_ms,
                                4,
                                &s_connect_task);
    if (ok != pdPASS) {
        s_connect_task = NULL;
        APP_LOGE(TAG, "dual connect task create failed");
    }
}

static int dual_gap_event(struct ble_gap_event *event, void *arg)
{
    dual_pad_t *pad = arg ? (dual_pad_t *)arg : NULL;

    switch (event->type) {
    case BLE_GAP_EVENT_DISC:
        handle_scan_report(&event->disc);
        return 0;

    case BLE_GAP_EVENT_DISC_COMPLETE:
        s_scanning = false;
        APP_LOGI(TAG,
                 "dual scan complete reason=%d seen=%lu candidates=%lu targets=%lu",
                 event->disc_complete.reason,
                 (unsigned long)s_scan_seen_count,
                 (unsigned long)s_candidate_seen_count,
                 (unsigned long)s_target_count);
        dual_schedule_connect_next(100);
        return 0;

    case BLE_GAP_EVENT_CONNECT:
        if (!pad) {
            return 0;
        }
        pad->last_connect_status = event->connect.status;
        if (event->connect.status == 0) {
            pad->connected = true;
            pad->conn_handle = event->connect.conn_handle;
            pad->connect_success_count++;
            pad->state = DUAL_PAD_DISCOVERING;
            APP_LOGI(TAG,
                     "dual connected pad=%d handle=%u target=%s",
                     pad->index,
                     event->connect.conn_handle,
                     pad->target_addr_text);
            update_conn_desc(pad, "connect");
            request_fast_params(pad, "connect");
            dual_start_gatt(pad);
        } else {
            pad->connected = false;
            pad->ready = false;
            pad->conn_handle = 0;
            pad->state = DUAL_PAD_TARGET;
            pad->connect_failure_count++;
            APP_LOGW(TAG,
                     "dual connect failed pad=%d status=%d target=%s",
                     pad->index,
                     event->connect.status,
                     pad->target_addr_text);
            dual_schedule_connect_next(500);
        }
        return 0;

    case BLE_GAP_EVENT_CONN_UPDATE:
        pad = pad ? pad : find_pad_by_conn(event->conn_update.conn_handle);
        if (pad) {
            pad->last_update_status = event->conn_update.status;
            update_conn_desc(pad, "update");
        }
        return 0;

    case BLE_GAP_EVENT_CONN_UPDATE_REQ:
        if (event->conn_update_req.self_params) {
            *event->conn_update_req.self_params = s_fast_update_params;
        }
        return 0;

    case BLE_GAP_EVENT_DISCONNECT:
        pad = pad ? pad : find_pad_by_conn(event->disconnect.conn.conn_handle);
        if (pad) {
            APP_LOGW(TAG,
                     "dual disconnected pad=%d reason=%d target=%s",
                     pad->index,
                     event->disconnect.reason,
                     pad->target_addr_text);
            pad->last_disconnect_reason = event->disconnect.reason;
            pad->disconnect_count++;
            pad->connected = false;
            pad->ready = false;
            pad->conn_handle = 0;
            pad->state = pad->target_valid ? DUAL_PAD_TARGET : DUAL_PAD_EMPTY;
            clear_slot_gatt(pad);
        }
        if (s_running) {
            dual_schedule_connect_next(800);
        }
        return 0;

    case BLE_GAP_EVENT_NOTIFY_RX:
        handle_notify_rx(event, arg);
        return 0;

    default:
        return 0;
    }
}

esp_err_t ble_dual_probe_start(void)
{
    if (!s_host_ready) {
        APP_LOGW(TAG, "dual probe start before host ready");
        return ESP_ERR_INVALID_STATE;
    }

    ble_dual_probe_stop();
    reset_probe_state();
    s_running = true;

    if (s_sim_mode == BLE_DUAL_PROBE_SIM_SYNTHETIC) {
        s_scanning = false;
        APP_LOGI(TAG,
                 "dual Pro2 probe synthetic mode started rate_hz=%u",
                 (unsigned)s_sim_rate_hz);
        return start_synthetic_task();
    }

    s_scanning = true;

    struct ble_gap_disc_params disc_params = {0};
    disc_params.filter_duplicates = 1;
    disc_params.passive = 0;
    disc_params.itvl = DUAL_FAST_SCAN_ITVL;
    disc_params.window = DUAL_FAST_SCAN_WINDOW;
    disc_params.filter_policy = 0;
    disc_params.limited = 0;

    APP_LOGI(TAG, "dual Pro2 probe scan started duration_ms=%u", (unsigned)DUAL_SCAN_DURATION_MS);
    int rc = ble_gap_disc(s_own_addr_type, DUAL_SCAN_DURATION_MS, &disc_params, dual_gap_event, NULL);
    if (rc != 0) {
        s_running = false;
        s_scanning = false;
        APP_LOGE(TAG, "dual scan start failed rc=%d", rc);
        return ESP_FAIL;
    }
    return ESP_OK;
}

void ble_dual_probe_stop(void)
{
    stop_sim_task();
    if (ble_gap_disc_active()) {
        (void)ble_gap_disc_cancel();
    }
    if (s_connect_task) {
        vTaskDelete(s_connect_task);
        s_connect_task = NULL;
    }
    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        if (s_pads[i].connected) {
            (void)ble_gap_terminate(s_pads[i].conn_handle, BLE_ERR_REM_USER_CONN_TERM);
        }
    }
    s_running = false;
    s_scanning = false;
}

const char *ble_dual_probe_sim_mode_string(ble_dual_probe_sim_mode_t mode)
{
    switch (mode) {
    case BLE_DUAL_PROBE_SIM_OFF:
        return "off";
    case BLE_DUAL_PROBE_SIM_MIRROR:
        return "mirror";
    case BLE_DUAL_PROBE_SIM_SYNTHETIC:
        return "synthetic";
    default:
        return "unknown";
    }
}

esp_err_t ble_dual_probe_set_simulation(ble_dual_probe_sim_mode_t mode, uint16_t rate_hz)
{
    if (mode != BLE_DUAL_PROBE_SIM_OFF &&
        mode != BLE_DUAL_PROBE_SIM_MIRROR &&
        mode != BLE_DUAL_PROBE_SIM_SYNTHETIC) {
        return ESP_ERR_INVALID_ARG;
    }

    bool was_running = s_running;
    if (was_running) {
        ble_dual_probe_stop();
        reset_probe_state();
    } else {
        stop_sim_task();
    }

    s_sim_mode = mode;
    s_sim_rate_hz = (uint16_t)sanitize_sim_rate(rate_hz);
    if (mode == BLE_DUAL_PROBE_SIM_OFF) {
        clear_simulated_pads();
    }

    APP_LOGI(TAG,
             "dual simulation mode=%s rate_hz=%u",
             ble_dual_probe_sim_mode_string(s_sim_mode),
             (unsigned)s_sim_rate_hz);

    if (was_running) {
        return ble_dual_probe_start();
    }
    if (s_host_ready && mode != BLE_DUAL_PROBE_SIM_OFF) {
        return ble_dual_probe_start();
    }
    return ESP_OK;
}

void ble_dual_probe_host_ready(uint8_t own_addr_type)
{
    s_host_ready = true;
    s_own_addr_type = own_addr_type;
    APP_LOGI(TAG, "dual probe host ready own_addr_type=%u", (unsigned)s_own_addr_type);
    (void)ble_dual_probe_start();
}

const char *ble_dual_probe_state_string(void)
{
    if (!s_host_ready) {
        return "host_wait";
    }
    if (s_scanning) {
        return "scanning";
    }
    if (!s_running) {
        return "stopped";
    }
    bool all_ready = s_target_count == BLE_DUAL_PROBE_PAD_COUNT;
    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        all_ready = all_ready && s_pads[i].ready;
    }
    if (all_ready) {
        return "ready";
    }
    if (any_pad_connecting()) {
        return "connecting";
    }
    return "probing";
}

void ble_dual_probe_get_metrics(ble_dual_probe_metrics_t *out_metrics)
{
    if (!out_metrics) {
        return;
    }
    memset(out_metrics, 0, sizeof(*out_metrics));
    out_metrics->running = s_running;
    out_metrics->host_ready = s_host_ready;
    out_metrics->scanning = s_scanning;
    out_metrics->sim_mode = s_sim_mode;
    out_metrics->sim_rate_hz = s_sim_rate_hz;
    out_metrics->scan_seen_count = s_scan_seen_count;
    out_metrics->candidate_seen_count = s_candidate_seen_count;
    out_metrics->target_count = s_target_count;
    out_metrics->total_notify_count = s_total_notify_count;
    out_metrics->total_notify_actual_millihz = s_total_notify_rate.actual_millihz;
    out_metrics->total_notify_last_gap_us = s_total_notify_rate.last_gap_us;
    out_metrics->total_notify_max_gap_us = s_total_notify_rate.max_gap_us;

    for (int i = 0; i < BLE_DUAL_PROBE_PAD_COUNT; i++) {
        const dual_pad_t *pad = &s_pads[i];
        out_metrics->pad_target_valid[i] = pad->target_valid;
        out_metrics->pad_connected[i] = pad->connected;
        out_metrics->pad_ready[i] = pad->ready;
        out_metrics->pad_simulated[i] = pad->simulated;
        out_metrics->pad_conn_handle[i] = pad->conn_handle;
        out_metrics->pad_input_handle[i] = pad->input_val_handle;
        out_metrics->pad_interval_units[i] = pad->interval_units;
        out_metrics->pad_latency[i] = pad->latency;
        out_metrics->pad_supervision_timeout[i] = pad->supervision_timeout;
        out_metrics->pad_last_connect_status[i] = pad->last_connect_status;
        out_metrics->pad_last_disconnect_reason[i] = pad->last_disconnect_reason;
        out_metrics->pad_last_update_status[i] = pad->last_update_status;
        out_metrics->pad_connect_start_count[i] = pad->connect_start_count;
        out_metrics->pad_connect_success_count[i] = pad->connect_success_count;
        out_metrics->pad_connect_failure_count[i] = pad->connect_failure_count;
        out_metrics->pad_disconnect_count[i] = pad->disconnect_count;
        out_metrics->pad_notify_count[i] = pad->notify_count;
        out_metrics->pad_notify_actual_millihz[i] = pad->notify_rate.actual_millihz;
        out_metrics->pad_notify_last_gap_us[i] = pad->notify_rate.last_gap_us;
        out_metrics->pad_notify_max_gap_us[i] = pad->notify_rate.max_gap_us;
        out_metrics->pad_unique_count[i] = pad->unique_count;
        out_metrics->pad_repeat_count[i] = pad->repeat_count;
        out_metrics->pad_unique_actual_millihz[i] = pad->unique_rate.actual_millihz;
        out_metrics->pad_unique_last_gap_us[i] = pad->unique_rate.last_gap_us;
        out_metrics->pad_unique_max_gap_us[i] = pad->unique_rate.max_gap_us;
        snprintf(out_metrics->pad_addr[i], sizeof(out_metrics->pad_addr[i]), "%s", pad->target_addr_text);
        snprintf(out_metrics->pad_name[i], sizeof(out_metrics->pad_name[i]), "%s", pad->target_name);
    }
}

void ble_dual_probe_format_status_json(char *out, size_t out_len)
{
    if (!out || out_len == 0) {
        return;
    }

    ble_dual_probe_metrics_t m;
    ble_dual_probe_get_metrics(&m);
    snprintf(out,
             out_len,
             "\"dual_pro2\":\"%s\","
             "\"dual_running\":%s,"
             "\"dual_scanning\":%s,"
             "\"dual_sim_mode\":\"%s\","
             "\"dual_sim_rate_hz\":%u,"
             "\"dual_scan_seen\":%lu,"
             "\"dual_candidates\":%lu,"
             "\"dual_targets\":%lu,"
             "\"dual_total_notify\":%lu,"
             "\"dual_total_notify_hz\":%lu,"
             "\"dual_total_notify_mhz\":%lu,"
             "\"dual_total_gap_us\":%lu,"
             "\"dual_total_max_gap_us\":%lu,"
             "\"dual_pad0\":\"%s\","
             "\"dual_pad0_simulated\":%s,"
             "\"dual_pad0_addr\":\"%s\","
             "\"dual_pad0_name\":\"%s\","
             "\"dual_pad0_conn\":%u,"
             "\"dual_pad0_input\":%u,"
             "\"dual_pad0_interval_us\":%lu,"
             "\"dual_pad0_notify\":%lu,"
             "\"dual_pad0_notify_hz\":%lu,"
             "\"dual_pad0_notify_mhz\":%lu,"
             "\"dual_pad0_unique\":%lu,"
             "\"dual_pad0_unique_hz\":%lu,"
             "\"dual_pad0_unique_mhz\":%lu,"
             "\"dual_pad0_repeat\":%lu,"
             "\"dual_pad0_disconnects\":%lu,"
             "\"dual_pad0_connect_failures\":%lu,"
             "\"dual_pad0_last_disconnect\":%d,"
             "\"dual_pad1\":\"%s\","
             "\"dual_pad1_simulated\":%s,"
             "\"dual_pad1_addr\":\"%s\","
             "\"dual_pad1_name\":\"%s\","
             "\"dual_pad1_conn\":%u,"
             "\"dual_pad1_input\":%u,"
             "\"dual_pad1_interval_us\":%lu,"
             "\"dual_pad1_notify\":%lu,"
             "\"dual_pad1_notify_hz\":%lu,"
             "\"dual_pad1_notify_mhz\":%lu,"
             "\"dual_pad1_unique\":%lu,"
             "\"dual_pad1_unique_hz\":%lu,"
             "\"dual_pad1_unique_mhz\":%lu,"
             "\"dual_pad1_repeat\":%lu,"
             "\"dual_pad1_disconnects\":%lu,"
             "\"dual_pad1_connect_failures\":%lu,"
             "\"dual_pad1_last_disconnect\":%d",
             ble_dual_probe_state_string(),
             m.running ? "true" : "false",
             m.scanning ? "true" : "false",
             ble_dual_probe_sim_mode_string(m.sim_mode),
             (unsigned)m.sim_rate_hz,
             (unsigned long)m.scan_seen_count,
             (unsigned long)m.candidate_seen_count,
             (unsigned long)m.target_count,
             (unsigned long)m.total_notify_count,
             (unsigned long)((m.total_notify_actual_millihz + 500u) / 1000u),
             (unsigned long)m.total_notify_actual_millihz,
             (unsigned long)m.total_notify_last_gap_us,
             (unsigned long)m.total_notify_max_gap_us,
             m.pad_ready[0] ? "ready" : (m.pad_connected[0] ? "connected" : (m.pad_target_valid[0] ? "target" : "empty")),
             m.pad_simulated[0] ? "true" : "false",
             m.pad_addr[0],
             m.pad_name[0],
             (unsigned)m.pad_conn_handle[0],
             (unsigned)m.pad_input_handle[0],
             (unsigned long)m.pad_interval_units[0] * 1250UL,
             (unsigned long)m.pad_notify_count[0],
             (unsigned long)((m.pad_notify_actual_millihz[0] + 500u) / 1000u),
             (unsigned long)m.pad_notify_actual_millihz[0],
             (unsigned long)m.pad_unique_count[0],
             (unsigned long)((m.pad_unique_actual_millihz[0] + 500u) / 1000u),
             (unsigned long)m.pad_unique_actual_millihz[0],
             (unsigned long)m.pad_repeat_count[0],
             (unsigned long)m.pad_disconnect_count[0],
             (unsigned long)m.pad_connect_failure_count[0],
             m.pad_last_disconnect_reason[0],
             m.pad_ready[1] ? "ready" : (m.pad_connected[1] ? "connected" : (m.pad_target_valid[1] ? "target" : "empty")),
             m.pad_simulated[1] ? "true" : "false",
             m.pad_addr[1],
             m.pad_name[1],
             (unsigned)m.pad_conn_handle[1],
             (unsigned)m.pad_input_handle[1],
             (unsigned long)m.pad_interval_units[1] * 1250UL,
             (unsigned long)m.pad_notify_count[1],
             (unsigned long)((m.pad_notify_actual_millihz[1] + 500u) / 1000u),
             (unsigned long)m.pad_notify_actual_millihz[1],
             (unsigned long)m.pad_unique_count[1],
             (unsigned long)((m.pad_unique_actual_millihz[1] + 500u) / 1000u),
             (unsigned long)m.pad_unique_actual_millihz[1],
             (unsigned long)m.pad_repeat_count[1],
             (unsigned long)m.pad_disconnect_count[1],
             (unsigned long)m.pad_connect_failure_count[1],
             m.pad_last_disconnect_reason[1]);
}
