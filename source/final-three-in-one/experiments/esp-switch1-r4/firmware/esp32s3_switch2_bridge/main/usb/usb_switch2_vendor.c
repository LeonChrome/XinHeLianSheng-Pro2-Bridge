#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/portmacro.h"
#include "freertos/task.h"
#include "app_log.h"
#include "ble_central.h"
#include "control_protocol.h"
#include "device_config.h"
#include "esp_err.h"
#include "esp_timer.h"
#include "normalized_rumble.h"
#include "tusb.h"
#include "usb_descriptors.h"
#include "usb_switch2_vendor.h"

static const char *TAG = "usb_vendor";

#define SWITCH2_VENDOR_ITF 0
#define SWITCH2_MS_OS_20_DESC_LEN 0xB2
#define SWITCH2_BOS_TOTAL_LEN (TUD_BOS_DESC_LEN + TUD_BOS_MICROSOFT_OS_DESC_LEN)
#define SWITCH2_BULK_REPLY_MAX 128
#define SWITCH2_CONTROL_REPLY_MAX 3072
#define SWITCH2_PENDING_REPLY_MAX 3072
#define SWITCH2_CONTROL_MAGIC "Y7CTL1"
#define SWITCH2_CONTROL_REPLY_MAGIC "Y7RSP1"
#define SWITCH2_CONTROL_MAGIC_LEN 6
#define SWITCH2_CONTROL_REPLY_HEADER_LEN 8
#define SWITCH2_MS_OS_10_STRING_INDEX 0xee
#define SWITCH2_MS_OS_10_COMPAT_ID_LEN 0x28
#define SWITCH2_MS_OS_10_PROPERTY_LEN 0x8e
#ifdef XINPUT_ELITE_EXPERIMENT
#define XGIP_MS_VENDOR_CODE 0x90
#define XGIP_MS_OS_10_COMPAT_ID_LEN 0x28
#define XGIP_FIRST_INTERFACE 0x00
#define XGIP_FUNCTION_INTERFACE_COUNT 0x01
#endif
#define SWITCH2_FLASH_REPLY_FULL_SPEED_LEN 0x50
#define SWITCH2_HID_GUARD_TIMEOUT_US (10LL * 60LL * 1000LL * 1000LL)
#define SWITCH2_HD_STREAM_TICK_DEFAULT_MS 20
#define SWITCH2_HD_STREAM_HOLD_DEFAULT_MS 180
#define SWITCH2_HD_SELF_TEST_HOLD_US 450000LL
#define SWITCH2_HD_SCALE_DEFAULT_PERCENT 100
#define SWITCH2_HD_STOP_PACKETS_DEFAULT 3

static uint8_t s_pending_reply[SWITCH2_PENDING_REPLY_MAX];
static uint8_t s_control_reply[SWITCH2_CONTROL_REPLY_MAX];
static size_t s_pending_len;
static size_t s_pending_offset;
static uint8_t s_pending_itf;
static uint32_t s_bulk_out_count;
static uint32_t s_bulk_in_count;
static uint32_t s_bulk_in_done_count;
static uint32_t s_bulk_last_sent_bytes;
static uint16_t s_last_rx_len;
static uint16_t s_last_tx_len;
static uint32_t s_last_address;
static uint8_t s_last_cmd;
static uint8_t s_last_arg;
static bool s_hid_guard_active;
static bool s_hid_guard_done;
static bool s_hid_guard_release_after_tx;
static int64_t s_hid_guard_started_us;
static portMUX_TYPE s_hd_lock = portMUX_INITIALIZER_UNLOCKED;
static bool s_hd_task_started;
static bool s_hd_stream_active;
static int64_t s_hd_stream_until_us;
static uint8_t s_hd_left_vibration[5];
static uint8_t s_hd_right_vibration[5];
static uint8_t s_hd_packet_id;
static uint8_t s_hd_stop_packets_pending;
static uint32_t s_hd_stream_updates;
static uint32_t s_hd_stream_writes;
static uint32_t s_hd_stream_stops;
static uint32_t s_hd_stream_errors;
static uint32_t s_hd_preset_ignored;
static int64_t s_hd_next_update_log_us;
static int64_t s_hd_next_stop_log_us;
static uint16_t s_hd_scale_percent = SWITCH2_HD_SCALE_DEFAULT_PERCENT;
static uint16_t s_hd_hold_ms = SWITCH2_HD_STREAM_HOLD_DEFAULT_MS;
static uint16_t s_hd_tick_ms = SWITCH2_HD_STREAM_TICK_DEFAULT_MS;
static uint8_t s_hd_stop_packet_count = SWITCH2_HD_STOP_PACKETS_DEFAULT;

static const uint8_t s_ms_os_10_string_descriptor[] = {
    0x12, TUSB_DESC_STRING,
    'M', 0x00, 'S', 0x00, 'F', 0x00, 'T', 0x00,
    '1', 0x00, '0', 0x00, '0', 0x00,
    USB_SWITCH2_MS_VENDOR_CODE, 0x00,
};

#ifdef XINPUT_ELITE_EXPERIMENT
static const uint8_t s_xgip_ms_os_10_string_descriptor[] = {
    0x12, TUSB_DESC_STRING,
    'M', 0x00, 'S', 0x00, 'F', 0x00, 'T', 0x00,
    '1', 0x00, '0', 0x00, '0', 0x00,
    XGIP_MS_VENDOR_CODE, 0x00,
};

static const uint8_t s_xgip_ms_os_10_compat_id_descriptor[] = {
    U32_TO_U8S_LE(XGIP_MS_OS_10_COMPAT_ID_LEN),
    U16_TO_U8S_LE(0x0100),
    U16_TO_U8S_LE(0x0004),
    0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

    XGIP_FIRST_INTERFACE, XGIP_FUNCTION_INTERFACE_COUNT,
    'X', 'G', 'I', 'P', '1', '0', 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
};
#endif

static const uint8_t s_ms_os_10_compat_id_descriptor[] = {
    U32_TO_U8S_LE(SWITCH2_MS_OS_10_COMPAT_ID_LEN),
    U16_TO_U8S_LE(0x0100),
    U16_TO_U8S_LE(0x0004),
    0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

    USB_SWITCH2_VENDOR_INTERFACE, 0x01,
    'W', 'I', 'N', 'U', 'S', 'B', 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
};

static const uint8_t s_ms_os_10_property_descriptor[] = {
    U32_TO_U8S_LE(SWITCH2_MS_OS_10_PROPERTY_LEN),
    U16_TO_U8S_LE(0x0100),
    U16_TO_U8S_LE(0x0005),
    U16_TO_U8S_LE(0x0001),

    U32_TO_U8S_LE(SWITCH2_MS_OS_10_PROPERTY_LEN - 0x0a),
    U32_TO_U8S_LE(0x00000001),
    U16_TO_U8S_LE(0x0028),
    'D', 0x00, 'e', 0x00, 'v', 0x00, 'i', 0x00, 'c', 0x00, 'e', 0x00,
    'I', 0x00, 'n', 0x00, 't', 0x00, 'e', 0x00, 'r', 0x00, 'f', 0x00,
    'a', 0x00, 'c', 0x00, 'e', 0x00, 'G', 0x00, 'U', 0x00, 'I', 0x00,
    'D', 0x00, 0x00, 0x00,
    U32_TO_U8S_LE(0x004e),
    '{', 0x00, '6', 0x00, 'F', 0x00, '1', 0x00, '3', 0x00, '7', 0x00,
    '2', 0x00, '5', 0x00, 'E', 0x00, '-', 0x00, 'E', 0x00, 'F', 0x00,
    '0', 0x00, 'E', 0x00, '-', 0x00, '4', 0x00, 'F', 0x00, 'D', 0x00,
    '3', 0x00, '-', 0x00, 'A', 0x00, 'E', 0x00, '5', 0x00, 'F', 0x00,
    '-', 0x00, 'B', 0x00, '2', 0x00, 'D', 0x00, 'E', 0x00, '9', 0x00,
    '8', 0x00, '9', 0x00, 'E', 0x00, 'C', 0x00, '8', 0x00, '2', 0x00,
    '5', 0x00, '}', 0x00, 0x00, 0x00,
};

TU_VERIFY_STATIC(sizeof(s_ms_os_10_string_descriptor) == 18,
                 "incorrect MS OS 1.0 string descriptor size");
#ifdef XINPUT_ELITE_EXPERIMENT
TU_VERIFY_STATIC(sizeof(s_xgip_ms_os_10_string_descriptor) == 18,
                 "incorrect XGIP MS OS 1.0 string descriptor size");
TU_VERIFY_STATIC(sizeof(s_xgip_ms_os_10_compat_id_descriptor) == XGIP_MS_OS_10_COMPAT_ID_LEN,
                 "incorrect XGIP MS OS 1.0 compat ID descriptor size");
#endif
TU_VERIFY_STATIC(sizeof(s_ms_os_10_compat_id_descriptor) == SWITCH2_MS_OS_10_COMPAT_ID_LEN,
                 "incorrect MS OS 1.0 compat ID descriptor size");
TU_VERIFY_STATIC(sizeof(s_ms_os_10_property_descriptor) == SWITCH2_MS_OS_10_PROPERTY_LEN,
                 "incorrect MS OS 1.0 property descriptor size");

static const uint8_t s_bos_descriptor[] = {
    TUD_BOS_DESCRIPTOR(SWITCH2_BOS_TOTAL_LEN, 1),
    TUD_BOS_MS_OS_20_DESCRIPTOR(SWITCH2_MS_OS_20_DESC_LEN, USB_SWITCH2_MS_VENDOR_CODE),
};

static const uint8_t s_ms_os_20_descriptor[] = {
    U16_TO_U8S_LE(0x000A), U16_TO_U8S_LE(MS_OS_20_SET_HEADER_DESCRIPTOR),
    U32_TO_U8S_LE(0x06030000), U16_TO_U8S_LE(SWITCH2_MS_OS_20_DESC_LEN),

    U16_TO_U8S_LE(0x0008), U16_TO_U8S_LE(MS_OS_20_SUBSET_HEADER_CONFIGURATION),
    0, 0, U16_TO_U8S_LE(SWITCH2_MS_OS_20_DESC_LEN - 0x0A),

    U16_TO_U8S_LE(0x0008), U16_TO_U8S_LE(MS_OS_20_SUBSET_HEADER_FUNCTION),
    USB_SWITCH2_VENDOR_INTERFACE, 0,
    U16_TO_U8S_LE(SWITCH2_MS_OS_20_DESC_LEN - 0x0A - 0x08),

    U16_TO_U8S_LE(0x0014), U16_TO_U8S_LE(MS_OS_20_FEATURE_COMPATBLE_ID),
    'W', 'I', 'N', 'U', 'S', 'B', 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

    U16_TO_U8S_LE(SWITCH2_MS_OS_20_DESC_LEN - 0x0A - 0x08 - 0x08 - 0x14),
    U16_TO_U8S_LE(MS_OS_20_FEATURE_REG_PROPERTY),
    U16_TO_U8S_LE(0x0007), U16_TO_U8S_LE(0x002A),
    'D', 0x00, 'e', 0x00, 'v', 0x00, 'i', 0x00, 'c', 0x00, 'e', 0x00,
    'I', 0x00, 'n', 0x00, 't', 0x00, 'e', 0x00, 'r', 0x00, 'f', 0x00,
    'a', 0x00, 'c', 0x00, 'e', 0x00, 'G', 0x00, 'U', 0x00, 'I', 0x00,
    'D', 0x00, 's', 0x00, 0x00, 0x00,
    U16_TO_U8S_LE(0x0050),
    '{', 0x00, '6', 0x00, 'F', 0x00, '1', 0x00, '3', 0x00, '7', 0x00,
    '2', 0x00, '5', 0x00, 'E', 0x00, '-', 0x00, 'E', 0x00, 'F', 0x00,
    '0', 0x00, 'E', 0x00, '-', 0x00, '4', 0x00, 'F', 0x00, 'D', 0x00,
    '3', 0x00, '-', 0x00, 'A', 0x00, 'E', 0x00, '5', 0x00, 'F', 0x00,
    '-', 0x00, 'B', 0x00, '2', 0x00, 'D', 0x00, 'E', 0x00, '9', 0x00,
    '8', 0x00, '9', 0x00, 'E', 0x00, 'C', 0x00, '8', 0x00, '2', 0x00,
    '5', 0x00, '}', 0x00, 0x00, 0x00, 0x00, 0x00,
};

TU_VERIFY_STATIC(sizeof(s_ms_os_20_descriptor) == SWITCH2_MS_OS_20_DESC_LEN,
                 "incorrect MS OS 2.0 descriptor size");

static bool nintendo_mode(void)
{
    return device_config_get_mode() == NINTENDO_EXPERIMENT_MODE;
}

#ifdef XINPUT_ELITE_EXPERIMENT
static bool xgip_mode(void)
{
    return device_config_get_mode() == XINPUT_EXPERIMENT_MODE;
}
#endif

static void hex_preview(const uint8_t *data, uint16_t len, char *out, size_t out_len)
{
    if (!out || out_len == 0) {
        return;
    }
    out[0] = 0;
    if (!data || len == 0) {
        return;
    }

    size_t used = 0;
    uint16_t n = len > 32 ? 32 : len;
    for (uint16_t i = 0; i < n && used + 4 < out_len; i++) {
        int written = snprintf(out + used, out_len - used, "%02x%s",
                               data[i], i + 1 < n ? " " : "");
        if (written <= 0) {
            break;
        }
        used += (size_t)written;
    }
    if (len > n && used + 5 < out_len) {
        snprintf(out + used, out_len - used, " ...");
    }
}

static void hid_guard_begin(void)
{
    if (!s_hid_guard_active) {
        APP_LOGI(TAG, "Steam init guard active: pausing HID input reports");
    }
    s_hid_guard_active = true;
    s_hid_guard_done = false;
    s_hid_guard_release_after_tx = false;
    s_hid_guard_started_us = esp_timer_get_time();
}

static void hid_guard_release(const char *reason)
{
    if (s_hid_guard_active || s_hid_guard_release_after_tx) {
        APP_LOGI(TAG, "Steam init guard released: %s", reason ? reason : "done");
    }
    s_hid_guard_active = false;
    s_hid_guard_done = true;
    s_hid_guard_release_after_tx = false;
}

static bool hid_guard_timed_out(void)
{
    return s_hid_guard_active &&
        (esp_timer_get_time() - s_hid_guard_started_us) > SWITCH2_HID_GUARD_TIMEOUT_US;
}

static uint32_t command_address(const uint8_t *cmd, uint16_t cmd_len)
{
    if (!cmd || cmd_len < 16) {
        return 0;
    }
    return (uint32_t)cmd[12] |
        ((uint32_t)cmd[13] << 8) |
        ((uint32_t)cmd[14] << 16) |
        ((uint32_t)cmd[15] << 24);
}

static void write_bytes(uint8_t *out, size_t out_len, size_t offset,
                        const uint8_t *data, size_t data_len)
{
    if (!out || !data || offset >= out_len) {
        return;
    }
    size_t n = data_len;
    if (n > out_len - offset) {
        n = out_len - offset;
    }
    memcpy(out + offset, data, n);
}

static void pack12_pair(uint8_t *out, size_t offset, uint16_t x, uint16_t y)
{
    out[offset] = (uint8_t)(x & 0xff);
    out[offset + 1] = (uint8_t)(((x >> 8) & 0x0f) | ((y & 0x0f) << 4));
    out[offset + 2] = (uint8_t)((y >> 4) & 0xff);
}

static void pack_stick_calibration(uint8_t out[9])
{
    pack12_pair(out, 0, 2048, 2048);
    pack12_pair(out, 3, 2048, 2048);
    pack12_pair(out, 6, 2048, 2048);
}

static size_t flash_read_length(uint32_t address)
{
    if (address == 0x13040) {
        return 0x10;
    }
    if (address == 0x13100) {
        return 0x18;
    }
    if (address == 0x13060) {
        return 0x20;
    }
    return 0x40;
}

static size_t build_ack(const uint8_t *cmd, uint16_t cmd_len,
                        uint8_t *reply, size_t reply_len)
{
    if (!cmd || !reply || reply_len == 0) {
        return 0;
    }
    memset(reply, 0, reply_len);
    reply[0] = cmd[0];
    if (reply_len > 1) {
        reply[1] = 0x01;
    }
    if (reply_len > 2 && cmd_len > 2) {
        reply[2] = cmd[2];
    }
    if (reply_len > 3 && cmd_len > 3) {
        reply[3] = cmd[3];
    }
    if (reply_len > 4 && cmd_len > 4) {
        reply[4] = cmd[4];
    }
    if (reply_len > 5) {
        reply[5] = 0xf8;
    }
    return reply_len;
}

static size_t build_flash_read_reply(const uint8_t *cmd, uint16_t cmd_len,
                                     uint8_t *reply, size_t reply_max)
{
    if (cmd_len < 16 || !reply) {
        return 0;
    }

    uint32_t address = (uint32_t)cmd[12] |
                       ((uint32_t)cmd[13] << 8) |
                       ((uint32_t)cmd[14] << 16) |
                       ((uint32_t)cmd[15] << 24);
    size_t data_len = flash_read_length(address);
    size_t full_reply_len = 0x10 + data_len;
    if (full_reply_len > reply_max) {
        return 0;
    }

    memset(reply, 0, full_reply_len);
    uint8_t *data = reply + 0x10;

    if (address == 0x13000) {
        static const uint8_t serial[] = {'H', 'A', '2', 'F', '8', '3', 'J', 'F'};
        write_bytes(data, data_len, 2, serial, sizeof(serial));
    }

    if (address == 0x13080 || address == 0x130C0) {
        memset(data, 0xff, data_len);
        uint8_t calib[9];
        pack_stick_calibration(calib);
        write_bytes(data, data_len, 0x28, calib, sizeof(calib));
    }

    if (address == 0x1fc040 || address == 0x1fc080 || address == 0x13060) {
        memset(data, 0xff, data_len);
    }

    if (address == 0x13040) {
        static const uint8_t block[] = {
            0x16, 0xf4, 0xd3, 0x41, 0x48, 0xce, 0x85, 0xba,
            0xf1, 0x05, 0x71, 0xba, 0x1f, 0x27, 0xcb, 0x3b,
        };
        write_bytes(data, data_len, 0, block, sizeof(block));
    }

    if (address == 0x13100) {
        static const uint8_t block[] = {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x2d, 0x10, 0xa7, 0x3d,
            0xe7, 0x49, 0x35, 0x3c, 0xa4, 0x2d, 0x20, 0x41,
        };
        write_bytes(data, data_len, 0, block, sizeof(block));
    }

    reply[0] = 0x02;
    reply[1] = 0x01;
    reply[2] = cmd[2];
    reply[3] = cmd[3];
    reply[5] = 0xf8;
    reply[8] = (uint8_t)data_len;
    memcpy(reply + 12, cmd + 12, 4);
    if (full_reply_len > SWITCH2_FLASH_REPLY_FULL_SPEED_LEN) {
        full_reply_len = SWITCH2_FLASH_REPLY_FULL_SPEED_LEN;
    }
    return full_reply_len;
}

static size_t build_reply(const uint8_t *cmd, uint16_t cmd_len,
                          uint8_t *reply, size_t reply_max)
{
    if (!cmd || cmd_len == 0 || !reply || reply_max == 0) {
        return 0;
    }

    uint8_t c0 = cmd[0];
    uint8_t arg1_hi = cmd_len > 3 ? cmd[3] : 0;

    if (cmd_len >= 16 && c0 == 0x02) {
        return build_flash_read_reply(cmd, cmd_len, reply, reply_max);
    }

    if (c0 == 0x0c && arg1_hi == 0x02) {
        return 0;
    }

    if (c0 == 0x10) {
        return 0;
    }

    if (c0 == 0x03 && arg1_hi == 0x0d) {
        size_t n = build_ack(cmd, cmd_len, reply, 12);
        reply[8] = 0x01;
        return n;
    }

    if (c0 == 0x15 && arg1_hi == 0x01) {
        static const uint8_t mac_le[] = {0x2d, 0xfc, 0x27, 0xce, 0xc6, 0x38};
        size_t n = build_ack(cmd, cmd_len, reply, 17);
        reply[8] = 0x01;
        reply[9] = 0x04;
        reply[10] = 0x01;
        write_bytes(reply, n, 11, mac_le, sizeof(mac_le));
        return n;
    }

    if (c0 == 0x15 && arg1_hi == 0x02) {
        size_t n = build_ack(cmd, cmd_len, reply, 25);
        reply[8] = 0x01;
        return n;
    }

    if (c0 == 0x15 && arg1_hi == 0x03) {
        size_t n = build_ack(cmd, cmd_len, reply, 9);
        reply[8] = 0x01;
        return n;
    }

    if (c0 == 0x11) {
        static const uint8_t payload[] = {
            0x20, 0x03, 0x00, 0x00, 0x0a, 0xe8, 0x1c, 0x3b,
            0x79, 0x7d, 0x8b, 0x3a, 0x0a, 0xe8, 0x9c, 0x42,
            0x58, 0xa0, 0x0b, 0x42, 0x0a, 0xe8, 0x9c, 0x41,
            0x58, 0xa0, 0x0b, 0x41,
        };
        size_t n = build_ack(cmd, cmd_len, reply, 37);
        reply[8] = 0x01;
        write_bytes(reply, n, 9, payload, sizeof(payload));
        return n;
    }

    if (c0 == 0x01 && arg1_hi == 0x0c) {
        static const uint8_t payload[] = {0x61, 0x12, 0x50, 0x10};
        size_t n = build_ack(cmd, cmd_len, reply, 12);
        write_bytes(reply, n, 8, payload, sizeof(payload));
        return n;
    }

    if (c0 == 0x03 && arg1_hi == 0x01) {
        size_t n = build_ack(cmd, cmd_len, reply, 16);
        reply[10] = 0x40;
        reply[11] = 0xf0;
        reply[14] = 0x60;
        return n;
    }

    return build_ack(cmd, cmd_len, reply, 8);
}

static bool has_non_zero_payload(const uint8_t *data, uint16_t len, uint16_t offset)
{
    for (uint16_t i = offset; i < len; i++) {
        if (data[i] != 0) {
            return true;
        }
    }
    return false;
}

static bool has_neutral_rumble_frame(const uint8_t *data, uint16_t len, uint16_t offset)
{
    return len >= offset + 5 &&
           data[offset] == 0x87 &&
           data[offset + 1] == 0x01 &&
           data[offset + 2] == 0x20 &&
           data[offset + 3] == 0x11 &&
           data[offset + 4] == 0x00;
}

static bool is_neutral_switch_rumble(const uint8_t *data, uint16_t len)
{
    return has_neutral_rumble_frame(data, len, 2) &&
           has_neutral_rumble_frame(data, len, 0x12);
}

static bool is_switch2_hid_rumble_report(const uint8_t *data, uint16_t len)
{
    return len >= 7 &&
           data[0] == 0x02 &&
           (data[1] & 0xf0) == 0x50;
}

static int clamp_int(int value, int min, int max)
{
    if (value < min) {
        return min;
    }
    if (value > max) {
        return max;
    }
    return value;
}

static uint16_t hd_scale_percent(void)
{
    uint16_t value;
    portENTER_CRITICAL(&s_hd_lock);
    value = s_hd_scale_percent;
    portEXIT_CRITICAL(&s_hd_lock);
    return value;
}

static int64_t hd_hold_us(void)
{
    uint16_t value;
    portENTER_CRITICAL(&s_hd_lock);
    value = s_hd_hold_ms;
    portEXIT_CRITICAL(&s_hd_lock);
    return (int64_t)value * 1000LL;
}

static uint16_t hd_tick_ms(void)
{
    uint16_t value;
    portENTER_CRITICAL(&s_hd_lock);
    value = s_hd_tick_ms;
    portEXIT_CRITICAL(&s_hd_lock);
    return value;
}

static int map_switch_amp_to_ble(int value)
{
    int64_t scaled = (int64_t)value * 1023LL * hd_scale_percent();
    int64_t mapped = (scaled + 1450000LL) / 2900000LL;
    return clamp_int((int)mapped, 0, 1023);
}

static void build_ble_vibration_data(uint16_t lf_freq, bool lf_tone, uint16_t lf_amp,
                                     uint16_t hf_freq, bool hf_tone, uint16_t hf_amp,
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

static void build_zero_ble_vibration(uint8_t out[5])
{
    build_ble_vibration_data(0x0e1, false, 0, 0x1e1, false, 0, out);
}

static void encode_ble_vibration_from_switch_frame(const uint8_t *report, uint16_t len,
                                                   uint16_t offset, uint8_t out[5])
{
    if (len < offset + 5) {
        build_zero_ble_vibration(out);
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

    build_ble_vibration_data((uint16_t)low_freq,
                             false,
                             (uint16_t)map_switch_amp_to_ble(low_amp),
                             (uint16_t)high_freq,
                             false,
                             (uint16_t)map_switch_amp_to_ble(high_amp),
                             out);
}

static void write_motor_block(uint8_t *out, uint16_t offset, uint8_t packet_id,
                              const uint8_t first[5], const uint8_t zero[5])
{
    out[offset] = (uint8_t)(0x50 | (packet_id & 0x0f));
    memcpy(out + offset + 1, first, 5);
    memcpy(out + offset + 6, zero, 5);
    memcpy(out + offset + 11, zero, 5);
}

static void build_pro2_hd_packet(uint8_t packet_id, const uint8_t left[5], const uint8_t right[5],
                                 uint8_t out[33])
{
    uint8_t zero[5];
    build_zero_ble_vibration(zero);

    memset(out, 0, 33);
    out[0] = 0x00;
    write_motor_block(out, 1, packet_id, left, zero);
    write_motor_block(out, 17, packet_id, right, zero);
}

static uint8_t next_hd_packet_id(void)
{
    uint8_t id;
    portENTER_CRITICAL(&s_hd_lock);
    id = s_hd_packet_id++ & 0x0f;
    portEXIT_CRITICAL(&s_hd_lock);
    return id;
}

static void hd_stream_update(const uint8_t left[5], const uint8_t right[5], int64_t hold_us,
                             const char *reason)
{
    int64_t now_us = esp_timer_get_time();
    int64_t until_us = now_us + hold_us;
    uint32_t updates;

    portENTER_CRITICAL(&s_hd_lock);
    memcpy(s_hd_left_vibration, left, sizeof(s_hd_left_vibration));
    memcpy(s_hd_right_vibration, right, sizeof(s_hd_right_vibration));
    s_hd_stream_until_us = until_us;
    s_hd_stream_active = true;
    s_hd_stream_updates++;
    updates = s_hd_stream_updates;
    portEXIT_CRITICAL(&s_hd_lock);

    bool self_test = reason && strcmp(reason, "self-test") == 0;
    if (self_test || app_log_debug_enabled() || now_us >= s_hd_next_update_log_us) {
        s_hd_next_update_log_us = now_us + 500000LL;
        APP_LOGI(TAG, "HD rumble stream update reason=%s updates=%lu hold_ms=%lld left=%02x%02x%02x%02x%02x right=%02x%02x%02x%02x%02x",
                 reason ? reason : "unknown",
                 (unsigned long)updates,
                 (long long)(hold_us / 1000),
                 left[0], left[1], left[2], left[3], left[4],
                 right[0], right[1], right[2], right[3], right[4]);
    }
}

void usb_switch2_vendor_stop_hd_rumble(void)
{
    uint8_t zero[5];
    build_zero_ble_vibration(zero);
    int64_t now_us = esp_timer_get_time();
    uint32_t stops;

    portENTER_CRITICAL(&s_hd_lock);
    memcpy(s_hd_left_vibration, zero, sizeof(s_hd_left_vibration));
    memcpy(s_hd_right_vibration, zero, sizeof(s_hd_right_vibration));
    s_hd_stream_until_us = 0;
    s_hd_stream_active = false;
    s_hd_stop_packets_pending = s_hd_stop_packet_count;
    s_hd_stream_stops++;
    stops = s_hd_stream_stops;
    portEXIT_CRITICAL(&s_hd_lock);

    if (app_log_debug_enabled() || now_us >= s_hd_next_stop_log_us) {
        s_hd_next_stop_log_us = now_us + 500000LL;
        APP_LOGI(TAG, "HD rumble stream stop requested stops=%lu", (unsigned long)stops);
    }
}

void usb_switch2_vendor_start_hd_rumble_self_test(void)
{
    usb_switch2_vendor_start_hd_rumble_self_test_ms((uint16_t)(SWITCH2_HD_SELF_TEST_HOLD_US / 1000LL));
}

void usb_switch2_vendor_start_hd_rumble_self_test_ms(uint16_t hold_ms)
{
    uint8_t left[5];
    uint8_t right[5];
    uint16_t safe_hold_ms = (uint16_t)clamp_int(hold_ms, 100, 10000);
    build_ble_vibration_data(0x0e1, false, 320, 0x1e1, false, 460, left);
    build_ble_vibration_data(0x0e1, false, 320, 0x1e1, false, 460, right);
    hd_stream_update(left, right, (int64_t)safe_hold_ms * 1000LL, "self-test");
}

void usb_switch2_vendor_start_normalized_rumble(const normalized_rumble_t *rumble,
                                                const char *reason)
{
    if (!rumble || !normalized_rumble_active(rumble)) {
        usb_switch2_vendor_stop_hd_rumble();
        return;
    }

    uint8_t left[5];
    uint8_t right[5];
    uint16_t max_amplitude =
        (uint16_t)clamp_int((int)(((uint32_t)512u * s_hd_scale_percent + 50u) / 100u),
                            1,
                            1023);
    uint16_t hold_ms = rumble->duration_ms == 0 ? s_hd_hold_ms : rumble->duration_ms;
    hold_ms = (uint16_t)clamp_int(hold_ms, 50, 1000);

    normalized_rumble_build_pro2_pair(rumble, max_amplitude, left, right);
    hd_stream_update(left, right, (int64_t)hold_ms * 1000LL, reason ? reason : "normalized");
}

bool usb_switch2_vendor_hd_rumble_active(void)
{
    bool active;
    int64_t until_us;
    int64_t now_us = esp_timer_get_time();
    portENTER_CRITICAL(&s_hd_lock);
    active = s_hd_stream_active;
    until_us = s_hd_stream_until_us;
    portEXIT_CRITICAL(&s_hd_lock);
    return active && now_us <= until_us;
}

uint32_t usb_switch2_vendor_hd_rumble_update_count(void)
{
    uint32_t value;
    portENTER_CRITICAL(&s_hd_lock);
    value = s_hd_stream_updates;
    portEXIT_CRITICAL(&s_hd_lock);
    return value;
}

uint32_t usb_switch2_vendor_hd_rumble_write_count(void)
{
    uint32_t value;
    portENTER_CRITICAL(&s_hd_lock);
    value = s_hd_stream_writes;
    portEXIT_CRITICAL(&s_hd_lock);
    return value;
}

uint32_t usb_switch2_vendor_hd_rumble_stop_count(void)
{
    uint32_t value;
    portENTER_CRITICAL(&s_hd_lock);
    value = s_hd_stream_stops;
    portEXIT_CRITICAL(&s_hd_lock);
    return value;
}

uint32_t usb_switch2_vendor_hd_rumble_error_count(void)
{
    uint32_t value;
    portENTER_CRITICAL(&s_hd_lock);
    value = s_hd_stream_errors;
    portEXIT_CRITICAL(&s_hd_lock);
    return value;
}

uint32_t usb_switch2_vendor_hd_rumble_preset_ignored_count(void)
{
    uint32_t value;
    portENTER_CRITICAL(&s_hd_lock);
    value = s_hd_preset_ignored;
    portEXIT_CRITICAL(&s_hd_lock);
    return value;
}

void usb_switch2_vendor_get_hd_rumble_tuning(uint16_t *scale_percent, uint16_t *hold_ms,
                                             uint16_t *tick_ms, uint8_t *stop_packets)
{
    portENTER_CRITICAL(&s_hd_lock);
    if (scale_percent) {
        *scale_percent = s_hd_scale_percent;
    }
    if (hold_ms) {
        *hold_ms = s_hd_hold_ms;
    }
    if (tick_ms) {
        *tick_ms = s_hd_tick_ms;
    }
    if (stop_packets) {
        *stop_packets = s_hd_stop_packet_count;
    }
    portEXIT_CRITICAL(&s_hd_lock);
}

void usb_switch2_vendor_set_hd_rumble_tuning(uint16_t scale_percent, uint16_t hold_ms,
                                             uint16_t tick_ms, uint8_t stop_packets)
{
    portENTER_CRITICAL(&s_hd_lock);
    s_hd_scale_percent = (uint16_t)clamp_int(scale_percent, 10, 250);
    s_hd_hold_ms = (uint16_t)clamp_int(hold_ms, 50, 1000);
    s_hd_tick_ms = (uint16_t)clamp_int(tick_ms, 5, 50);
    s_hd_stop_packet_count = (uint8_t)clamp_int(stop_packets, 1, 8);
    portEXIT_CRITICAL(&s_hd_lock);
}

static void hd_rumble_task(void *arg)
{
    (void)arg;
    int64_t next_log_us = 0;

    while (true) {
        uint8_t left[5];
        uint8_t right[5];
        bool active;
        bool send_stop = false;
        int64_t now_us = esp_timer_get_time();
        int64_t until_us;

        portENTER_CRITICAL(&s_hd_lock);
        active = s_hd_stream_active && now_us <= s_hd_stream_until_us;
        until_us = s_hd_stream_until_us;
        memcpy(left, s_hd_left_vibration, sizeof(left));
        memcpy(right, s_hd_right_vibration, sizeof(right));
        if (s_hd_stream_active && !active) {
            s_hd_stream_active = false;
            s_hd_stop_packets_pending = s_hd_stop_packet_count;
            s_hd_stream_stops++;
        }
        if (!active && s_hd_stop_packets_pending > 0) {
            s_hd_stop_packets_pending--;
            send_stop = true;
            build_zero_ble_vibration(left);
            build_zero_ble_vibration(right);
        }
        portEXIT_CRITICAL(&s_hd_lock);

        if (active || send_stop) {
            uint8_t packet[33];
            build_pro2_hd_packet(next_hd_packet_id(), left, right, packet);
            esp_err_t err = ble_central_send_rumble(packet, sizeof(packet));
            portENTER_CRITICAL(&s_hd_lock);
            if (err == ESP_OK) {
                s_hd_stream_writes++;
            } else {
                s_hd_stream_errors++;
            }
            portEXIT_CRITICAL(&s_hd_lock);

            if (err != ESP_OK) {
                APP_LOGD(TAG, "HD rumble stream write skipped active=%s stop=%s err=%d",
                         active ? "yes" : "no", send_stop ? "yes" : "no", (int)err);
            } else if (active && now_us >= next_log_us) {
                next_log_us = now_us + 500000LL;
                APP_LOGI(TAG, "HD rumble stream tick until_ms=%lld left=%02x%02x%02x%02x%02x right=%02x%02x%02x%02x%02x",
                         (long long)((until_us - now_us) / 1000),
                         left[0], left[1], left[2], left[3], left[4],
                         right[0], right[1], right[2], right[3], right[4]);
            }
        }

        vTaskDelay(pdMS_TO_TICKS(hd_tick_ms()));
    }
}

void usb_switch2_vendor_bridge_hid_output_to_ble(const uint8_t *data, uint16_t len)
{
    if (!data || len < 2) {
        return;
    }

    if (is_switch2_hid_rumble_report(data, len)) {
        bool active = has_non_zero_payload(data, len, 2) &&
                      !is_neutral_switch_rumble(data, len);
        uint8_t left[5];
        uint8_t right[5];
        if (active) {
            encode_ble_vibration_from_switch_frame(data, len, 2, left);
            encode_ble_vibration_from_switch_frame(data, len, 0x12, right);
            hd_stream_update(left, right, hd_hold_us(), "hid-out");
        } else {
            usb_switch2_vendor_stop_hd_rumble();
        }
        return;
    }

    uint8_t c0 = data[0];
    uint8_t sub = len > 3 ? data[3] : 0xff;
    if (c0 == 0x0a && sub == 0x02 && len >= 16) {
        uint32_t ignored;
        portENTER_CRITICAL(&s_hd_lock);
        ignored = ++s_hd_preset_ignored;
        portEXIT_CRITICAL(&s_hd_lock);
        APP_LOGI(TAG,
                 "bulk vibrate preset ignored len=%u count=%lu; raw HID 0x02 stream stays authoritative",
                 (unsigned)len,
                 (unsigned long)ignored);
        return;
    }
}

esp_err_t usb_switch2_vendor_send_raw02_payload(const uint8_t *payload, uint16_t len)
{
    if (!payload || len != 64) {
        APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=invalid_payload_len len=%u",
                 (unsigned)len);
        return ESP_ERR_INVALID_ARG;
    }
    if (payload[0] != 0x02) {
        APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=invalid_report_id report_id=0x%02x",
                 payload[0]);
        return ESP_ERR_INVALID_ARG;
    }
    if (!is_switch2_hid_rumble_report(payload, len)) {
        APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=invalid_rumble_frame first=0x%02x",
                 payload[1]);
        return ESP_ERR_INVALID_ARG;
    }

    bool active = has_non_zero_payload(payload, len, 2) &&
                  !is_neutral_switch_rumble(payload, len);
    if (!active) {
        usb_switch2_vendor_stop_hd_rumble();
        APP_LOGI(TAG, "[RUMBLE_RAW02] sent=true len=%u neutral=true",
                 (unsigned)len);
        return ESP_OK;
    }

    uint8_t left[5];
    uint8_t right[5];
    uint8_t packet[33];
    encode_ble_vibration_from_switch_frame(payload, len, 2, left);
    encode_ble_vibration_from_switch_frame(payload, len, 0x12, right);
    build_pro2_hd_packet(next_hd_packet_id(), left, right, packet);

    esp_err_t err = ble_central_send_rumble(packet, sizeof(packet));
    portENTER_CRITICAL(&s_hd_lock);
    if (err == ESP_OK) {
        s_hd_stream_writes++;
    } else {
        s_hd_stream_errors++;
    }
    portEXIT_CRITICAL(&s_hd_lock);
    if (err != ESP_OK) {
        APP_LOGW(TAG, "[RUMBLE_RAW02] sent=false error=%s", esp_err_to_name(err));
        return err;
    }

    hd_stream_update(left, right, hd_hold_us(), "raw02");
    APP_LOGI(TAG, "[RUMBLE_RAW02] sent=true len=%u", (unsigned)len);
    return ESP_OK;
}

static void bridge_bulk_output_to_ble(const uint8_t *data, uint16_t len)
{
    usb_switch2_vendor_bridge_hid_output_to_ble(data, len);
}

static void flush_pending_reply(void)
{
    if (s_pending_len == 0 || s_pending_offset >= s_pending_len) {
        s_pending_len = 0;
        s_pending_offset = 0;
        return;
    }

    while (s_pending_offset < s_pending_len) {
        uint32_t available = tud_vendor_n_write_available(s_pending_itf);
        if (available == 0) {
            break;
        }

        size_t remaining = s_pending_len - s_pending_offset;
        uint32_t chunk = remaining > available ? available : (uint32_t)remaining;
        uint32_t written = tud_vendor_n_write(s_pending_itf,
                                              s_pending_reply + s_pending_offset,
                                              chunk);
        if (written == 0) {
            break;
        }
        s_pending_offset += written;
    }

    tud_vendor_n_write_flush(s_pending_itf);

    if (s_pending_offset >= s_pending_len) {
        s_pending_len = 0;
        s_pending_offset = 0;
    }
}

static void queue_reply(uint8_t itf, const uint8_t *reply, size_t reply_len)
{
    if (!reply || reply_len == 0) {
        return;
    }

    if (reply_len > sizeof(s_pending_reply)) {
        APP_LOGW(TAG, "bulk IN reply too large len=%u", (unsigned)reply_len);
        return;
    }

    if (s_pending_len != 0 && s_pending_offset < s_pending_len) {
        APP_LOGW(TAG, "bulk IN pending reply overwritten remaining=%u",
                 (unsigned)(s_pending_len - s_pending_offset));
    }

    memcpy(s_pending_reply, reply, reply_len);
    s_pending_len = reply_len;
    s_pending_offset = 0;
    s_pending_itf = itf;
    flush_pending_reply();
}

static bool is_manager_control_packet(const uint8_t *data, uint16_t len)
{
    return data && len > SWITCH2_CONTROL_MAGIC_LEN &&
           memcmp(data, SWITCH2_CONTROL_MAGIC, SWITCH2_CONTROL_MAGIC_LEN) == 0;
}

static size_t build_manager_control_reply(const uint8_t *cmd, uint16_t cmd_len,
                                          uint8_t *out, size_t out_len)
{
    if (!cmd || !out || out_len <= SWITCH2_CONTROL_REPLY_HEADER_LEN ||
        cmd_len <= SWITCH2_CONTROL_MAGIC_LEN) {
        return 0;
    }

    char command[192];
    size_t command_len = cmd_len - SWITCH2_CONTROL_MAGIC_LEN;
    while (command_len > 0 &&
           (cmd[SWITCH2_CONTROL_MAGIC_LEN + command_len - 1] == 0 ||
            cmd[SWITCH2_CONTROL_MAGIC_LEN + command_len - 1] == '\r' ||
            cmd[SWITCH2_CONTROL_MAGIC_LEN + command_len - 1] == '\n')) {
        command_len--;
    }
    if (command_len >= sizeof(command)) {
        command_len = sizeof(command) - 1;
    }
    memcpy(command, cmd + SWITCH2_CONTROL_MAGIC_LEN, command_len);
    command[command_len] = 0;

    static char json[SWITCH2_CONTROL_REPLY_MAX - SWITCH2_CONTROL_REPLY_HEADER_LEN];
    control_protocol_handle_line(command, json, sizeof(json));

    size_t json_len = strlen(json);
    size_t max_json_len = out_len - SWITCH2_CONTROL_REPLY_HEADER_LEN;
    if (json_len > max_json_len) {
        json_len = max_json_len;
    }

    memcpy(out, SWITCH2_CONTROL_REPLY_MAGIC, SWITCH2_CONTROL_MAGIC_LEN);
    out[6] = (uint8_t)(json_len & 0xff);
    out[7] = (uint8_t)((json_len >> 8) & 0xff);
    memcpy(out + SWITCH2_CONTROL_REPLY_HEADER_LEN, json, json_len);
    return SWITCH2_CONTROL_REPLY_HEADER_LEN + json_len;
}

void usb_switch2_vendor_init(void)
{
    build_zero_ble_vibration(s_hd_left_vibration);
    build_zero_ble_vibration(s_hd_right_vibration);
    if (!s_hd_task_started) {
        BaseType_t ok = xTaskCreate(hd_rumble_task, "hd_rumble_task", 4096, NULL, 4, NULL);
        if (ok == pdPASS) {
            s_hd_task_started = true;
        } else {
            APP_LOGW(TAG, "failed to start HD rumble task");
        }
    }
    APP_LOGI(TAG, "Switch2 vendor bulk responder enabled");
}

bool usb_switch2_vendor_mounted(void)
{
    return nintendo_mode() && tud_vendor_n_mounted(SWITCH2_VENDOR_ITF);
}

uint32_t usb_switch2_vendor_rx_count(void)
{
    return s_bulk_out_count;
}

uint32_t usb_switch2_vendor_tx_count(void)
{
    return s_bulk_in_count;
}

uint32_t usb_switch2_vendor_tx_done_count(void)
{
    return s_bulk_in_done_count;
}

uint32_t usb_switch2_vendor_last_sent_bytes(void)
{
    return s_bulk_last_sent_bytes;
}

bool usb_switch2_vendor_hid_guard_active(void)
{
    if (hid_guard_timed_out()) {
        hid_guard_release("timeout");
    }
    return s_hid_guard_active;
}

const char *usb_switch2_vendor_hid_guard_state(void)
{
    if (usb_switch2_vendor_hid_guard_active()) {
        return "active";
    }
    return s_hid_guard_done ? "done" : "idle";
}

void usb_switch2_vendor_reset_hid_guard(void)
{
    s_hid_guard_active = false;
    s_hid_guard_done = false;
    s_hid_guard_release_after_tx = false;
    s_hid_guard_started_us = 0;
}

void usb_switch2_vendor_arm_hid_guard(void)
{
    hid_guard_begin();
}

void usb_switch2_vendor_release_hid_guard(void)
{
    hid_guard_release("manual");
}

uint16_t usb_switch2_vendor_last_rx_len(void)
{
    return s_last_rx_len;
}

uint32_t usb_switch2_vendor_last_address(void)
{
    return s_last_address;
}

uint16_t usb_switch2_vendor_last_tx_len(void)
{
    return s_last_tx_len;
}

uint8_t usb_switch2_vendor_last_cmd(void)
{
    return s_last_cmd;
}

uint8_t usb_switch2_vendor_last_arg(void)
{
    return s_last_arg;
}

uint16_t usb_switch2_vendor_pending_len(void)
{
    return (uint16_t)s_pending_len;
}

uint16_t usb_switch2_vendor_pending_offset(void)
{
    return (uint16_t)s_pending_offset;
}

uint8_t const *tud_descriptor_bos_cb(void)
{
    return nintendo_mode() ? s_bos_descriptor : NULL;
}

uint16_t const *tinyusb_extra_string_descriptor_cb(uint8_t index, uint16_t langid)
{
    (void)langid;
#ifdef XINPUT_ELITE_EXPERIMENT
    if (xgip_mode() && index == SWITCH2_MS_OS_10_STRING_INDEX) {
        APP_LOGI(TAG, "USB GET_DESCRIPTOR string index=0xee len=0x12 signature=MSFT100 vendor_code=0x90");
        return (uint16_t const *)(uintptr_t)s_xgip_ms_os_10_string_descriptor;
    }
#endif
    if (nintendo_mode() && index == SWITCH2_MS_OS_10_STRING_INDEX) {
        APP_LOGI(TAG, "MS OS 1.0 string descriptor requested");
        return (uint16_t const *)(uintptr_t)s_ms_os_10_string_descriptor;
    }
    return NULL;
}

bool tud_vendor_control_xfer_cb(uint8_t rhport, uint8_t stage,
                                tusb_control_request_t const *request)
{
    if (stage != CONTROL_STAGE_SETUP) {
        return true;
    }

    if (device_config_get_mode() == XINPUT_EXPERIMENT_MODE) {
#ifdef XINPUT_ELITE_EXPERIMENT
        if (!request) {
            return false;
        }

        APP_LOGI(TAG, "USB vendor request bm=0x%02x req=0x%02x value=0x%04x index=0x%04x length=%u",
                 request->bmRequestType,
                 request->bRequest,
                 request->wValue,
                 request->wIndex,
                 (unsigned)request->wLength);

        if (request->bmRequestType == 0xc0 &&
            request->bRequest == XGIP_MS_VENDOR_CODE &&
            request->wValue == 0x0000 &&
            request->wIndex == 0x0004) {
            APP_LOGI(TAG, "USB vendor request 0x90 returning XGIP10 compat ID first_itf=%u interfaces=%u len=%u",
                     (unsigned)XGIP_FIRST_INTERFACE,
                     (unsigned)XGIP_FUNCTION_INTERFACE_COUNT,
                     (unsigned)sizeof(s_xgip_ms_os_10_compat_id_descriptor));
            return tud_control_xfer(rhport,
                                    request,
                                    (void *)(uintptr_t)s_xgip_ms_os_10_compat_id_descriptor,
                                    sizeof(s_xgip_ms_os_10_compat_id_descriptor));
        }
        return false;
#else
        (void)rhport;
        (void)request;
        return true;
#endif
    }

    if (!nintendo_mode() || !request) {
        return false;
    }

    if (request->bmRequestType_bit.type == TUSB_REQ_TYPE_VENDOR &&
        request->bRequest == USB_SWITCH2_MS_VENDOR_CODE) {
        if (request->wIndex == 0x0004) {
            APP_LOGI(TAG, "MS OS 1.0 compat ID requested len=%u",
                     (unsigned)sizeof(s_ms_os_10_compat_id_descriptor));
            return tud_control_xfer(rhport,
                                    request,
                                    (void *)(uintptr_t)s_ms_os_10_compat_id_descriptor,
                                    sizeof(s_ms_os_10_compat_id_descriptor));
        }

        if (request->wIndex == 0x0005) {
            APP_LOGI(TAG, "MS OS 1.0 property requested len=%u",
                     (unsigned)sizeof(s_ms_os_10_property_descriptor));
            return tud_control_xfer(rhport,
                                    request,
                                    (void *)(uintptr_t)s_ms_os_10_property_descriptor,
                                    sizeof(s_ms_os_10_property_descriptor));
        }

        if (request->wIndex == 0x0007) {
            uint16_t total_len;
            memcpy(&total_len, s_ms_os_20_descriptor + 8, sizeof(total_len));
            APP_LOGI(TAG, "MS OS 2.0 descriptor requested len=%u", (unsigned)total_len);
            return tud_control_xfer(rhport,
                                    request,
                                    (void *)(uintptr_t)s_ms_os_20_descriptor,
                                    total_len);
        }
    }

    return false;
}

void tud_vendor_rx_cb(uint8_t itf, uint8_t const *buffer, uint16_t bufsize)
{
    if (!nintendo_mode() || itf != SWITCH2_VENDOR_ITF || !buffer || bufsize == 0) {
        return;
    }

    uint8_t cmd[SWITCH2_BULK_REPLY_MAX];
    uint16_t cmd_len = bufsize > sizeof(cmd) ? sizeof(cmd) : bufsize;
    memcpy(cmd, buffer, cmd_len);
    tud_vendor_n_read_flush(itf);

    char preview[112];
    s_bulk_out_count++;
    s_last_rx_len = cmd_len;
    s_last_cmd = cmd[0];
    s_last_arg = cmd_len > 3 ? cmd[3] : 0;
    s_last_address = command_address(cmd, cmd_len);
    hex_preview(cmd, cmd_len, preview, sizeof(preview));
    APP_LOGI(TAG, "bulk OUT len=%u data=%s", (unsigned)cmd_len, preview);

    if (is_manager_control_packet(cmd, cmd_len)) {
        size_t reply_len = build_manager_control_reply(cmd, cmd_len, s_control_reply, sizeof(s_control_reply));
        s_last_tx_len = (uint16_t)reply_len;
        if (reply_len == 0) {
            return;
        }
        s_bulk_in_count++;
        queue_reply(itf, s_control_reply, reply_len);
        APP_LOGI(TAG, "manager bulk control reply queued len=%u", (unsigned)reply_len);
        return;
    }

    if (s_hid_guard_active) {
        hid_guard_begin();
    }

    bridge_bulk_output_to_ble(cmd, cmd_len);

    uint8_t reply[SWITCH2_BULK_REPLY_MAX];
    size_t reply_len = build_reply(cmd, cmd_len, reply, sizeof(reply));
    s_last_tx_len = (uint16_t)reply_len;
    if (reply_len == 0) {
        return;
    }

    if (cmd[0] == 0x03 && s_last_arg == 0x0d) {
        s_hid_guard_release_after_tx = true;
    }

    s_bulk_in_count++;
    queue_reply(itf, reply, reply_len);

    hex_preview(reply, (uint16_t)reply_len, preview, sizeof(preview));
    APP_LOGI(TAG, "bulk IN queued len=%u data=%s", (unsigned)reply_len, preview);
}

void tud_vendor_tx_cb(uint8_t itf, uint32_t sent_bytes)
{
    s_bulk_in_done_count++;
    s_bulk_last_sent_bytes = sent_bytes;
    if (s_hid_guard_release_after_tx) {
        hid_guard_release("start output ack sent");
    }
    if (itf == s_pending_itf) {
        flush_pending_reply();
    }
}
