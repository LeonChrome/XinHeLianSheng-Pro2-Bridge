#include <string.h>
#include "esp_err.h"
#include "esp_log.h"
#include "tinyusb.h"
#include "tusb.h"
#include "app_log.h"
#include "ble_central.h"
#include "control_protocol.h"
#include "device_config.h"
#include "report_rate_stats.h"
#include "usb_descriptors.h"
#include "usb_hid_device.h"
#include "usb_switch2_vendor.h"
#include "usb_xinput_device.h"

static const char *TAG = "usb";
static bool s_mounted;
static bool s_suspended;
static uint32_t s_hid_out_count;
static uint8_t s_hid_out_last_report_id;
static uint8_t s_hid_out_last_effective_report_id;
static uint8_t s_hid_out_last_type;
static uint16_t s_hid_out_last_len;
static uint8_t s_hid_out_last_first_byte;
static uint32_t s_hid_get_count;
static uint8_t s_hid_get_last_report_id;
static uint8_t s_hid_get_last_type;
static uint16_t s_hid_get_last_req_len;
static uint16_t s_hid_get_last_resp_len;
static char s_feature_reply[3072];
static uint16_t s_feature_reply_len;
static uint16_t s_feature_reply_offset;

#define HID_INSTANCE_GENERIC 0
#define HID_INSTANCE_NINTENDO 0
#define HID_INSTANCE_DUAL_A 0
#define HID_INSTANCE_DUAL_B 1
#define MANAGER_FEATURE_SET_MAGIC "Y7HID1"
#define MANAGER_FEATURE_REPLY_MAGIC "Y7HRS1"
#define MANAGER_FEATURE_MAGIC_LEN 6
#define MANAGER_FEATURE_REPLY_HEADER_LEN 11
#define SWITCH_LEGACY_SUBCOMMAND_ACK 0x80
#define SWITCH_LEGACY_DEVICE_TYPE_PRO 0x03
#define SWITCH_LEGACY_SUBCMD_REQUEST_DEVICE_INFO 0x02
#define SWITCH_LEGACY_SUBCMD_SET_INPUT_REPORT_MODE 0x03
#define SWITCH_LEGACY_SUBCMD_SPI_FLASH_READ 0x10
#define SWITCH_LEGACY_SUBCMD_SET_PLAYER_LIGHTS 0x30
#define SWITCH_LEGACY_SUBCMD_GET_PLAYER_LIGHTS 0x31
#define SWITCH_LEGACY_SUBCMD_SET_HOME_LIGHT 0x38
#define SWITCH_LEGACY_SUBCMD_ENABLE_IMU 0x40
#define SWITCH_LEGACY_SUBCMD_SET_IMU_SENSITIVITY 0x41
#define SWITCH_LEGACY_SUBCMD_ENABLE_VIBRATION 0x48
#define SWITCH_LEGACY_PROP_STATUS 0x01
#define SWITCH_LEGACY_PROP_HANDSHAKE 0x02
#define SWITCH_LEGACY_PROP_HIGH_SPEED 0x03
#define SWITCH_LEGACY_PROP_FORCE_USB 0x04
#define SWITCH_LEGACY_PROP_CLEAR_USB 0x05
#define SWITCH_LEGACY_PROP_RESET_MCU 0x06
#define SWITCH_LEGACY_SPI_STICK_FACTORY 0x603d
#define SWITCH_LEGACY_SPI_STICK_USER 0x8010
#define SWITCH_LEGACY_SPI_IMU_FACTORY 0x6020
#define SWITCH_LEGACY_SPI_IMU_USER 0x8026

static const uint8_t s_switch_legacy_mac[2][6] = {
    { 0x98, 0xb6, 0xe9, 0x00, 0x00, 0xa1 },
    { 0x98, 0xb6, 0xe9, 0x00, 0x00, 0xb2 },
};
static uint8_t s_switch_legacy_reply_seq[2];
static uint8_t s_switch_legacy_force_usb_count[2];
static bool s_switch_legacy_input_enabled[2];
static bool s_switch_legacy_imu_enabled[2];
static bool s_switch_legacy_vibration_command_seen[2];
static bool s_switch_legacy_vibration_enabled[2];
static uint8_t s_switch_legacy_report_mode[2];
static uint8_t s_switch_legacy_player_lights[2];
static uint32_t s_switch1_vibration_command_count;
static uint32_t s_switch1_rumble_rx_count;
static uint32_t s_switch1_rumble_active_count;
static uint32_t s_switch1_rumble_neutral_count;
static uint32_t s_switch1_rumble_forwarded_count;
static uint32_t s_switch1_rumble_blocked_count;

static bool switch1_pro_mode_active(void)
{
    return device_config_get_mode() == SWITCH1_PRO_MODE;
}

static bool usb_hid_device_instance_ready(uint8_t instance)
{
    return s_mounted && !s_suspended && tud_hid_n_ready(instance);
}

static uint8_t switch_legacy_slot_from_instance(uint8_t instance)
{
    return instance == HID_INSTANCE_DUAL_B ? 1 : 0;
}

static void switch_legacy_reset_session(void)
{
    memset(s_switch_legacy_reply_seq, 0, sizeof(s_switch_legacy_reply_seq));
    memset(s_switch_legacy_force_usb_count, 0, sizeof(s_switch_legacy_force_usb_count));
    memset(s_switch_legacy_input_enabled, 0, sizeof(s_switch_legacy_input_enabled));
    memset(s_switch_legacy_imu_enabled, 0, sizeof(s_switch_legacy_imu_enabled));
    memset(s_switch_legacy_vibration_command_seen, 0, sizeof(s_switch_legacy_vibration_command_seen));
    memset(s_switch_legacy_vibration_enabled, 0, sizeof(s_switch_legacy_vibration_enabled));
    memset(s_switch_legacy_report_mode, 0, sizeof(s_switch_legacy_report_mode));
    memset(s_switch_legacy_player_lights, 0, sizeof(s_switch_legacy_player_lights));
    s_switch1_vibration_command_count = 0;
    s_switch1_rumble_rx_count = 0;
    s_switch1_rumble_active_count = 0;
    s_switch1_rumble_neutral_count = 0;
    s_switch1_rumble_forwarded_count = 0;
    s_switch1_rumble_blocked_count = 0;
}

static void switch_legacy_pack_12bit_pair(uint8_t *dst, uint16_t first, uint16_t second)
{
    dst[0] = (uint8_t)(first & 0xff);
    dst[1] = (uint8_t)(((first >> 8) & 0x0f) | ((second & 0x0f) << 4));
    dst[2] = (uint8_t)((second >> 4) & 0xff);
}

static void switch_legacy_write_le16(uint8_t *dst, int16_t value)
{
    dst[0] = (uint8_t)((uint16_t)value & 0xff);
    dst[1] = (uint8_t)(((uint16_t)value >> 8) & 0xff);
}

static void switch_legacy_write_le32(uint8_t *dst, uint32_t value)
{
    dst[0] = (uint8_t)(value & 0xff);
    dst[1] = (uint8_t)((value >> 8) & 0xff);
    dst[2] = (uint8_t)((value >> 16) & 0xff);
    dst[3] = (uint8_t)((value >> 24) & 0xff);
}

static uint32_t switch_legacy_read_le32(const uint8_t *src)
{
    return (uint32_t)src[0] |
           ((uint32_t)src[1] << 8) |
           ((uint32_t)src[2] << 16) |
           ((uint32_t)src[3] << 24);
}

static void switch_legacy_fill_factory_stick(uint8_t *dst)
{
    uint8_t left[9];
    uint8_t right[9];
    switch_legacy_pack_12bit_pair(left + 0, 1000, 1000);
    switch_legacy_pack_12bit_pair(left + 3, 2048, 2048);
    switch_legacy_pack_12bit_pair(left + 6, 1000, 1000);
    switch_legacy_pack_12bit_pair(right + 0, 2048, 2048);
    switch_legacy_pack_12bit_pair(right + 3, 1000, 1000);
    switch_legacy_pack_12bit_pair(right + 6, 1000, 1000);
    memcpy(dst, left, sizeof(left));
    memcpy(dst + sizeof(left), right, sizeof(right));
}

static void switch_legacy_fill_factory_imu(uint8_t *dst)
{
    memset(dst, 0, 24);
    switch_legacy_write_le16(dst + 6, 16384);
    switch_legacy_write_le16(dst + 8, 16384);
    switch_legacy_write_le16(dst + 10, 16384);
    switch_legacy_write_le16(dst + 18, 13371);
    switch_legacy_write_le16(dst + 20, 13371);
    switch_legacy_write_le16(dst + 22, 13371);
}

static void switch_legacy_fill_spi_read(uint32_t address, uint8_t requested_len, uint8_t *dst, size_t capacity)
{
    if (!dst || capacity < 5) {
        return;
    }
    memset(dst, 0, capacity);
    switch_legacy_write_le32(dst, address);
    dst[4] = requested_len;

    uint8_t temp[32] = {0};
    size_t temp_len = 0;
    if (address == SWITCH_LEGACY_SPI_STICK_FACTORY) {
        switch_legacy_fill_factory_stick(temp);
        temp_len = 18;
    } else if (address == SWITCH_LEGACY_SPI_IMU_FACTORY) {
        switch_legacy_fill_factory_imu(temp);
        temp_len = 24;
    } else if (address == SWITCH_LEGACY_SPI_STICK_USER ||
               address == SWITCH_LEGACY_SPI_IMU_USER) {
        temp_len = requested_len;
    } else {
        temp_len = requested_len;
    }

    size_t copy_len = requested_len;
    if (copy_len > temp_len) {
        copy_len = temp_len;
    }
    if (copy_len > capacity - 5) {
        copy_len = capacity - 5;
    }
    memcpy(dst + 5, temp, copy_len);
}

static bool switch_legacy_send_report_now(uint8_t instance, const uint8_t report[SWITCH_LEGACY_REPORT_SIZE])
{
    if (!usb_hid_device_instance_ready(instance)) {
        return false;
    }
    return tud_hid_n_report(instance, report[0], report + 1, SWITCH_LEGACY_REPORT_SIZE - 1);
}

static bool switch_legacy_send_input_report(uint8_t instance, const uint8_t report[SWITCH_LEGACY_REPORT_SIZE])
{
    uint8_t slot = switch_legacy_slot_from_instance(instance);
    if (!s_switch_legacy_input_enabled[slot]) {
        return true;
    }
    return switch_legacy_send_report_now(instance, report);
}

static void switch_legacy_make_subcommand_reply(uint8_t instance,
                                                uint8_t subcommand,
                                                const uint8_t *subcommand_data,
                                                uint16_t subcommand_len,
                                                uint8_t report[SWITCH_LEGACY_REPORT_SIZE])
{
    uint8_t slot = switch_legacy_slot_from_instance(instance);
    hid_report_make_switch_legacy_neutral(report);
    report[0] = SWITCH_LEGACY_REPORT_ID_SUBCOMMAND_REPLY;
    report[1] = s_switch_legacy_reply_seq[slot]++;
    report[13] = SWITCH_LEGACY_SUBCOMMAND_ACK;
    report[14] = subcommand;

    if (subcommand == SWITCH_LEGACY_SUBCMD_REQUEST_DEVICE_INFO) {
        report[13] = 0x82;
        report[15] = 0x04;
        report[16] = 0x33;
        report[17] = SWITCH_LEGACY_DEVICE_TYPE_PRO;
        report[18] = 0x02;
        memcpy(report + 19, s_switch_legacy_mac[slot], 6);
        report[26] = 0x01;
    } else if (subcommand == SWITCH_LEGACY_SUBCMD_SPI_FLASH_READ &&
               subcommand_data && subcommand_len >= 5) {
        report[13] = 0x90;
        uint32_t address = switch_legacy_read_le32(subcommand_data);
        uint8_t requested_len = subcommand_data[4];
        switch_legacy_fill_spi_read(address, requested_len, report + 15, SWITCH_LEGACY_REPORT_SIZE - 15);
    } else if (switch1_pro_mode_active() &&
               subcommand == SWITCH_LEGACY_SUBCMD_SET_INPUT_REPORT_MODE &&
               subcommand_data && subcommand_len >= 1) {
        s_switch_legacy_report_mode[slot] = subcommand_data[0];
        if (subcommand_data[0] == SWITCH_LEGACY_REPORT_ID_FULL_STATE ||
            subcommand_data[0] == SWITCH_LEGACY_REPORT_ID_FULL_STATE_MCU) {
            s_switch_legacy_input_enabled[slot] = true;
        }
    } else if (switch1_pro_mode_active() &&
               subcommand == SWITCH_LEGACY_SUBCMD_SET_PLAYER_LIGHTS &&
               subcommand_data && subcommand_len >= 1) {
        s_switch_legacy_player_lights[slot] = subcommand_data[0];
    } else if (switch1_pro_mode_active() &&
               subcommand == SWITCH_LEGACY_SUBCMD_GET_PLAYER_LIGHTS) {
        report[13] = 0xb0;
        report[15] = s_switch_legacy_player_lights[slot];
    } else if (switch1_pro_mode_active() &&
               subcommand == SWITCH_LEGACY_SUBCMD_ENABLE_IMU &&
               subcommand_data && subcommand_len >= 1) {
        s_switch_legacy_imu_enabled[slot] = subcommand_data[0] != 0;
    } else if (switch1_pro_mode_active() &&
               subcommand == SWITCH_LEGACY_SUBCMD_ENABLE_VIBRATION &&
               subcommand_data && subcommand_len >= 1) {
        s_switch_legacy_vibration_command_seen[slot] = true;
        s_switch_legacy_vibration_enabled[slot] = subcommand_data[0] != 0;
        s_switch1_vibration_command_count++;
        if (!s_switch_legacy_vibration_enabled[slot]) {
            usb_switch2_vendor_stop_hd_rumble();
        }
        APP_LOGI(TAG,
                 "Switch 1 vibration command enabled=%s commands=%lu",
                 s_switch_legacy_vibration_enabled[slot] ? "yes" : "no",
                 (unsigned long)s_switch1_vibration_command_count);
    } else if (!switch1_pro_mode_active() && subcommand_data && subcommand_len > 0) {
        uint16_t copy_len = subcommand_len;
        if (copy_len > SWITCH_LEGACY_REPORT_SIZE - 15) {
            copy_len = SWITCH_LEGACY_REPORT_SIZE - 15;
        }
        memcpy(report + 15, subcommand_data, copy_len);
    }
}

static void switch1_handle_rumble(uint8_t slot,
                                  const uint8_t left[4],
                                  const uint8_t right[4],
                                  const char *reason)
{
    normalized_rumble_t rumble;
    normalized_rumble_from_switch1_hd(left, right, 70, &rumble);
    s_switch1_rumble_rx_count++;

    if (!normalized_rumble_active(&rumble)) {
        s_switch1_rumble_neutral_count++;
        if (usb_switch2_vendor_hd_rumble_active()) {
            usb_switch2_vendor_stop_hd_rumble();
        }
        return;
    }

    s_switch1_rumble_active_count++;
    bool explicitly_disabled = s_switch_legacy_vibration_command_seen[slot] &&
                               !s_switch_legacy_vibration_enabled[slot];
    if (explicitly_disabled) {
        s_switch1_rumble_blocked_count++;
    } else {
        s_switch1_rumble_forwarded_count++;
        usb_switch2_vendor_start_normalized_rumble(&rumble, reason);
    }

    if (s_switch1_rumble_active_count == 1 ||
        explicitly_disabled ||
        (s_switch1_rumble_active_count % 128u) == 0) {
        APP_LOGI(TAG,
                 "[SWITCH1_RUMBLE] active=%lu forwarded=%lu blocked=%lu vibration_state=%s weak=%u strong=%u left_gain=%u right_gain=%u raw=%02x%02x%02x%02x/%02x%02x%02x%02x",
                 (unsigned long)s_switch1_rumble_active_count,
                 (unsigned long)s_switch1_rumble_forwarded_count,
                 (unsigned long)s_switch1_rumble_blocked_count,
                 !s_switch_legacy_vibration_command_seen[slot] ? "unknown_compatible" :
                 s_switch_legacy_vibration_enabled[slot] ? "enabled" : "disabled",
                 (unsigned)rumble.weak,
                 (unsigned)rumble.strong,
                 (unsigned)rumble.left_gain_percent,
                 (unsigned)rumble.right_gain_percent,
                 left[0], left[1], left[2], left[3],
                 right[0], right[1], right[2], right[3]);
    }
}

static void switch_legacy_send_subcommand_reply(uint8_t instance,
                                                uint8_t subcommand,
                                                const uint8_t *subcommand_data,
                                                uint16_t subcommand_len)
{
    uint8_t report[SWITCH_LEGACY_REPORT_SIZE];
    switch_legacy_make_subcommand_reply(instance, subcommand, subcommand_data, subcommand_len, report);
    bool ok = switch_legacy_send_report_now(instance, report);
    APP_LOGI(TAG, "Switch legacy subcommand reply instance=%u cmd=0x%02x ok=%s",
             (unsigned)instance,
             subcommand,
             ok ? "yes" : "no");
}

static void switch_legacy_send_proprietary_reply(uint8_t instance, uint8_t command)
{
    uint8_t slot = switch_legacy_slot_from_instance(instance);
    uint8_t payload[SWITCH_LEGACY_REPORT_SIZE - 1] = {0};
    payload[0] = command;

    if (command == SWITCH_LEGACY_PROP_STATUS) {
        payload[2] = SWITCH_LEGACY_DEVICE_TYPE_PRO;
        for (uint8_t i = 0; i < 6; i++) {
            payload[3 + i] = s_switch_legacy_mac[slot][5 - i];
        }
    }

    bool ok = usb_hid_device_instance_ready(instance) &&
              tud_hid_n_report(instance,
                               SWITCH_LEGACY_REPORT_ID_COMMAND_ACK,
                               payload,
                               sizeof(payload));
    APP_LOGI(TAG, "Switch legacy proprietary reply instance=%u cmd=0x%02x ok=%s",
             (unsigned)instance,
             command,
             ok ? "yes" : "no");
}

static void switch_legacy_handle_output(uint8_t instance, const uint8_t full_report[SWITCH_LEGACY_REPORT_SIZE],
                                        uint16_t full_len)
{
    if (instance != HID_INSTANCE_DUAL_A && instance != HID_INSTANCE_DUAL_B) {
        return;
    }
    if (!full_report || full_len == 0) {
        return;
    }

    uint8_t report_id = full_report[0];
    if (report_id == SWITCH_LEGACY_REPORT_ID_PROPRIETARY && full_len >= 2) {
        uint8_t command = full_report[1];
        uint8_t slot = switch_legacy_slot_from_instance(instance);
        if (command == SWITCH_LEGACY_PROP_STATUS) {
            s_switch_legacy_force_usb_count[slot] = 0;
            s_switch_legacy_input_enabled[slot] = false;
        }
        if (switch1_pro_mode_active() &&
            (command == SWITCH_LEGACY_PROP_FORCE_USB ||
             command == SWITCH_LEGACY_PROP_CLEAR_USB)) {
            if (command == SWITCH_LEGACY_PROP_FORCE_USB) {
                s_switch_legacy_force_usb_count[slot] = 1;
                s_switch_legacy_input_enabled[slot] = true;
            }
            switch_legacy_send_proprietary_reply(instance, command);
            APP_LOGI(TAG, "Switch 1 Pro wired command=0x%02x input_enabled=%s",
                     command,
                     s_switch_legacy_input_enabled[slot] ? "yes" : "no");
            return;
        }
        if (command == SWITCH_LEGACY_PROP_FORCE_USB ||
            command == SWITCH_LEGACY_PROP_CLEAR_USB) {
            if (command == SWITCH_LEGACY_PROP_FORCE_USB) {
                if (s_switch_legacy_force_usb_count[slot] < 255) {
                    s_switch_legacy_force_usb_count[slot]++;
                }
                if (s_switch_legacy_force_usb_count[slot] >= 2 && !s_switch_legacy_input_enabled[slot]) {
                    s_switch_legacy_input_enabled[slot] = true;
                    APP_LOGI(TAG, "Switch legacy input enabled instance=%u slot=%u force_usb_count=%u",
                             (unsigned)instance,
                             (unsigned)slot,
                             (unsigned)s_switch_legacy_force_usb_count[slot]);
                }
            }
            APP_LOGI(TAG, "Switch legacy proprietary no-reply instance=%u cmd=0x%02x",
                     (unsigned)instance,
                     command);
            return;
        }
        switch_legacy_send_proprietary_reply(instance, command);
        return;
    }

    if (report_id == SWITCH_LEGACY_REPORT_ID_RUMBLE_SUBCOMMAND && full_len >= 11) {
        uint8_t slot = switch_legacy_slot_from_instance(instance);
        if (switch1_pro_mode_active()) {
            switch1_handle_rumble(slot,
                                  full_report + 2,
                                  full_report + 6,
                                  "switch1-subcommand-rumble");
        }
        uint8_t subcommand = full_report[10];
        const uint8_t *subcommand_data = full_report + 11;
        uint16_t subcommand_len = full_len > 11 ? (uint16_t)(full_len - 11) : 0;
        switch_legacy_send_subcommand_reply(instance, subcommand, subcommand_data, subcommand_len);
        return;
    }

    if (report_id == SWITCH_LEGACY_REPORT_ID_RUMBLE) {
        uint8_t slot = switch_legacy_slot_from_instance(instance);
        if (switch1_pro_mode_active() && full_len >= 10) {
            switch1_handle_rumble(slot,
                                  full_report + 2,
                                  full_report + 6,
                                  "switch1-rumble");
        }
        APP_LOGD(TAG, "Switch legacy rumble instance=%u len=%u", (unsigned)instance, (unsigned)full_len);
    }
}

esp_err_t usb_hid_device_init(void)
{
    APP_LOGI(TAG, "initializing TinyUSB HID mode=%s", device_mode_to_string(device_config_get_mode()));
    APP_LOGI(TAG, "USB identity VID=0x%04x PID=0x%04x product=%s",
             usb_descriptors_current_vid(),
             usb_descriptors_current_pid(),
             usb_descriptors_current_product());
#ifdef XINPUT_ELITE_EXPERIMENT
    if (device_config_get_mode() == XINPUT_EXPERIMENT_MODE) {
        APP_LOGI(TAG, "GIP bring-up descriptor bcdUSB=0x0200 bcdDevice=0x0512 serial=ELITE2-GIP-0512");
        APP_LOGI(TAG, "GIP data interface=0 class/subclass/protocol=ff/47/d0 OUT=0x02 IN=0x82 mps=64 interval=4");
        APP_LOGI(TAG, "GIP MS OS 1.0 string=0xee vendor_code=0x90 compatible_id=XGIP10");
    }
#endif
    APP_LOGI(TAG, "USB path: Nintendo/Steam HID and vendor bulk init are verified on ESP32-S3");
    usb_switch2_vendor_init();

    const tinyusb_config_t tusb_cfg = {
        .device_descriptor = usb_descriptors_current_device(),
        .string_descriptor = usb_descriptors_current_strings(),
        .string_descriptor_count = usb_descriptors_current_string_count(),
        .external_phy = false,
        .configuration_descriptor = usb_descriptors_current_configuration(),
    };
    return tinyusb_driver_install(&tusb_cfg);
}

bool usb_hid_device_ready(void)
{
    return usb_hid_device_instance_ready(HID_INSTANCE_GENERIC);
}

bool usb_hid_device_dual_ready(void)
{
    return usb_hid_device_instance_ready(HID_INSTANCE_DUAL_A) &&
           usb_hid_device_instance_ready(HID_INSTANCE_DUAL_B);
}

const char *usb_hid_device_state_string(void)
{
    if (!s_mounted) {
        return "not_mounted";
    }
    return s_suspended ? "suspended" : "mounted";
}

uint32_t usb_hid_device_out_count(void)
{
    return s_hid_out_count;
}

uint8_t usb_hid_device_last_out_report_id(void)
{
    return s_hid_out_last_report_id;
}

uint8_t usb_hid_device_last_out_effective_report_id(void)
{
    return s_hid_out_last_effective_report_id;
}

uint8_t usb_hid_device_last_out_type(void)
{
    return s_hid_out_last_type;
}

uint16_t usb_hid_device_last_out_len(void)
{
    return s_hid_out_last_len;
}

uint8_t usb_hid_device_last_out_first_byte(void)
{
    return s_hid_out_last_first_byte;
}

uint32_t usb_hid_device_get_count(void)
{
    return s_hid_get_count;
}

uint8_t usb_hid_device_last_get_report_id(void)
{
    return s_hid_get_last_report_id;
}

uint8_t usb_hid_device_last_get_type(void)
{
    return s_hid_get_last_type;
}

uint16_t usb_hid_device_last_get_req_len(void)
{
    return s_hid_get_last_req_len;
}

uint16_t usb_hid_device_last_get_resp_len(void)
{
    return s_hid_get_last_resp_len;
}

esp_err_t usb_hid_device_send_generic_report(const bridge_hid_gamepad_report_t *report)
{
    return usb_hid_device_send_generic_report_instance(HID_INSTANCE_GENERIC, report);
}

esp_err_t usb_hid_device_send_generic_report_instance(uint8_t instance, const bridge_hid_gamepad_report_t *report)
{
    if (!report || !usb_hid_device_instance_ready(instance)) {
        return ESP_ERR_INVALID_STATE;
    }
    uint8_t report_id = device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE ? 0 : GENERIC_HID_REPORT_ID;
    bool ok = tud_hid_n_report(instance, report_id, report, sizeof(*report));
    report_rate_stats_record(ok);
    return ok ? ESP_OK : ESP_FAIL;
}

esp_err_t usb_hid_device_send_dual_generic_report(const bridge_hid_gamepad_report_t *slot_a,
                                                  const bridge_hid_gamepad_report_t *slot_b)
{
    if (!slot_a || !slot_b || !usb_hid_device_dual_ready()) {
        return ESP_ERR_INVALID_STATE;
    }
    esp_err_t err_a = usb_hid_device_send_generic_report_instance(HID_INSTANCE_DUAL_A, slot_a);
    esp_err_t err_b = usb_hid_device_send_generic_report_instance(HID_INSTANCE_DUAL_B, slot_b);
    return (err_a == ESP_OK && err_b == ESP_OK) ? ESP_OK : ESP_FAIL;
}

esp_err_t usb_hid_device_send_nintendo_report(const uint8_t report[NINTENDO_REPORT_SIZE])
{
    if (usb_switch2_vendor_hid_guard_active()) {
        return ESP_OK;
    }
    if (!usb_hid_device_instance_ready(HID_INSTANCE_NINTENDO)) {
        return ESP_ERR_INVALID_STATE;
    }
    bool ok = tud_hid_n_report(HID_INSTANCE_NINTENDO, NINTENDO_INPUT_REPORT_ID, report + 1, NINTENDO_REPORT_SIZE - 1);
    report_rate_stats_record(ok);
    return ok ? ESP_OK : ESP_FAIL;
}

esp_err_t usb_hid_device_send_dual_switch_legacy_reports(const uint8_t slot_a[SWITCH_LEGACY_REPORT_SIZE],
                                                         const uint8_t slot_b[SWITCH_LEGACY_REPORT_SIZE])
{
    if (!slot_a || !slot_b || !usb_hid_device_dual_ready()) {
        return ESP_ERR_INVALID_STATE;
    }

    bool ok_a = switch_legacy_send_input_report(HID_INSTANCE_DUAL_A, slot_a);
    bool ok_b = switch_legacy_send_input_report(HID_INSTANCE_DUAL_B, slot_b);
    report_rate_stats_record(ok_a && ok_b);
    return (ok_a && ok_b) ? ESP_OK : ESP_FAIL;
}

esp_err_t usb_hid_device_send_switch1_pro_report(const uint8_t report[SWITCH_LEGACY_REPORT_SIZE])
{
    if (!report || !switch1_pro_mode_active()) {
        return ESP_ERR_INVALID_ARG;
    }
    bool ok = switch_legacy_send_input_report(HID_INSTANCE_NINTENDO, report);
    report_rate_stats_record(ok);
    return ok ? ESP_OK : ESP_FAIL;
}

bool usb_hid_device_switch1_imu_enabled(void)
{
    return switch1_pro_mode_active() && s_switch_legacy_imu_enabled[0];
}

bool usb_hid_device_switch1_vibration_command_seen(void)
{
    return switch1_pro_mode_active() && s_switch_legacy_vibration_command_seen[0];
}

bool usb_hid_device_switch1_vibration_enabled(void)
{
    return switch1_pro_mode_active() && s_switch_legacy_vibration_enabled[0];
}

uint32_t usb_hid_device_switch1_vibration_command_count(void)
{
    return s_switch1_vibration_command_count;
}

uint32_t usb_hid_device_switch1_rumble_rx_count(void)
{
    return s_switch1_rumble_rx_count;
}

uint32_t usb_hid_device_switch1_rumble_active_count(void)
{
    return s_switch1_rumble_active_count;
}

uint32_t usb_hid_device_switch1_rumble_neutral_count(void)
{
    return s_switch1_rumble_neutral_count;
}

uint32_t usb_hid_device_switch1_rumble_forwarded_count(void)
{
    return s_switch1_rumble_forwarded_count;
}

uint32_t usb_hid_device_switch1_rumble_blocked_count(void)
{
    return s_switch1_rumble_blocked_count;
}

static bool manager_feature_set_command(uint8_t const *payload, uint16_t payload_size)
{
    return payload && payload_size > MANAGER_FEATURE_MAGIC_LEN &&
           memcmp(payload, MANAGER_FEATURE_SET_MAGIC, MANAGER_FEATURE_MAGIC_LEN) == 0;
}

static void manager_feature_handle_set(uint8_t const *payload, uint16_t payload_size)
{
    char command[128];
    size_t command_len = payload_size > MANAGER_FEATURE_MAGIC_LEN ?
        payload_size - MANAGER_FEATURE_MAGIC_LEN : 0;

    while (command_len > 0 &&
           (payload[MANAGER_FEATURE_MAGIC_LEN + command_len - 1] == 0 ||
            payload[MANAGER_FEATURE_MAGIC_LEN + command_len - 1] == '\r' ||
            payload[MANAGER_FEATURE_MAGIC_LEN + command_len - 1] == '\n')) {
        command_len--;
    }
    if (command_len >= sizeof(command)) {
        command_len = sizeof(command) - 1;
    }

    memcpy(command, payload + MANAGER_FEATURE_MAGIC_LEN, command_len);
    command[command_len] = 0;

    control_protocol_handle_line(command, s_feature_reply, sizeof(s_feature_reply));
    s_feature_reply_len = (uint16_t)strlen(s_feature_reply);
    s_feature_reply_offset = 0;
    APP_LOGI(TAG, "manager HID feature command handled cmd=%s reply_len=%u",
             command,
             (unsigned)s_feature_reply_len);
}

static uint16_t manager_feature_get_chunk(uint8_t *buffer, uint16_t reqlen)
{
    if (!buffer || reqlen == 0) {
        return 0;
    }

    memset(buffer, 0, reqlen);
    if (reqlen < MANAGER_FEATURE_REPLY_HEADER_LEN) {
        return reqlen;
    }

    uint16_t offset = s_feature_reply_offset;
    uint16_t remaining = offset < s_feature_reply_len ? (uint16_t)(s_feature_reply_len - offset) : 0;
    uint16_t chunk = (uint16_t)(reqlen - MANAGER_FEATURE_REPLY_HEADER_LEN);
    if (chunk > remaining) {
        chunk = remaining;
    }

    memcpy(buffer, MANAGER_FEATURE_REPLY_MAGIC, MANAGER_FEATURE_MAGIC_LEN);
    buffer[6] = (uint8_t)(s_feature_reply_len & 0xff);
    buffer[7] = (uint8_t)((s_feature_reply_len >> 8) & 0xff);
    buffer[8] = (uint8_t)(offset & 0xff);
    buffer[9] = (uint8_t)((offset >> 8) & 0xff);
    buffer[10] = (uint8_t)chunk;
    if (chunk > 0) {
        memcpy(buffer + MANAGER_FEATURE_REPLY_HEADER_LEN, s_feature_reply + offset, chunk);
        s_feature_reply_offset = (uint16_t)(offset + chunk);
    }

    return reqlen;
}

void tud_mount_cb(void)
{
    s_mounted = true;
    s_suspended = false;
    switch_legacy_reset_session();
    usb_switch2_vendor_reset_hid_guard();
    usb_xinput_device_on_mount();
    APP_LOGI(TAG, "USB SET_CONFIGURATION complete; mounted");
}

void tud_umount_cb(void)
{
    s_mounted = false;
    s_suspended = false;
    switch_legacy_reset_session();
    usb_switch2_vendor_reset_hid_guard();
    usb_xinput_device_on_unmount();
    APP_LOGI(TAG, "USB unmounted");
}

void tud_suspend_cb(bool remote_wakeup_en)
{
    (void)remote_wakeup_en;
    s_suspended = true;
    APP_LOGI(TAG, "USB suspended");
}

void tud_resume_cb(void)
{
    s_suspended = false;
    APP_LOGI(TAG, "USB resumed");
}

uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id, hid_report_type_t report_type,
                               uint8_t *buffer, uint16_t reqlen)
{
    (void)instance;

    s_hid_get_count++;
    s_hid_get_last_report_id = report_id;
    s_hid_get_last_type = (uint8_t)report_type;
    s_hid_get_last_req_len = reqlen;

    if (!buffer || reqlen == 0) {
        s_hid_get_last_resp_len = 0;
        return 0;
    }

    uint16_t resp_len = 0;
    if (report_type == HID_REPORT_TYPE_FEATURE &&
        report_id == MANAGER_FEATURE_REPORT_ID) {
        resp_len = manager_feature_get_chunk(buffer, reqlen);
    } else if (device_config_get_mode() == NINTENDO_EXPERIMENT_MODE &&
               report_type == HID_REPORT_TYPE_INPUT &&
        (report_id == 0 || report_id == NINTENDO_INPUT_REPORT_ID)) {
        uint8_t report[NINTENDO_REPORT_SIZE];
        hid_report_make_nintendo_neutral(report);

        if (report_id == NINTENDO_INPUT_REPORT_ID) {
            resp_len = (uint16_t)(NINTENDO_REPORT_SIZE - 1);
            if (resp_len > reqlen) {
                resp_len = reqlen;
            }
            memcpy(buffer, report + 1, resp_len);
        } else {
            resp_len = NINTENDO_REPORT_SIZE;
            if (resp_len > reqlen) {
                resp_len = reqlen;
            }
            memcpy(buffer, report, resp_len);
        }
    } else if ((device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE ||
                device_config_get_mode() == SWITCH1_PRO_MODE) &&
               report_type == HID_REPORT_TYPE_INPUT &&
               (report_id == 0 ||
                report_id == SWITCH_LEGACY_REPORT_ID_FULL_STATE ||
                report_id == SWITCH_LEGACY_REPORT_ID_FULL_STATE_MCU ||
                report_id == SWITCH_LEGACY_REPORT_ID_SIMPLE_STATE ||
                report_id == SWITCH_LEGACY_REPORT_ID_SUBCOMMAND_REPLY ||
                report_id == SWITCH_LEGACY_REPORT_ID_COMMAND_ACK)) {
        uint8_t report[SWITCH_LEGACY_REPORT_SIZE];
        hid_report_make_switch_legacy_neutral(report);
        if (report_id == SWITCH_LEGACY_REPORT_ID_FULL_STATE_MCU ||
            report_id == SWITCH_LEGACY_REPORT_ID_SIMPLE_STATE ||
            report_id == SWITCH_LEGACY_REPORT_ID_SUBCOMMAND_REPLY ||
            report_id == SWITCH_LEGACY_REPORT_ID_COMMAND_ACK) {
            report[0] = report_id;
        }

        if (report_id == 0) {
            resp_len = SWITCH_LEGACY_REPORT_SIZE;
            if (resp_len > reqlen) {
                resp_len = reqlen;
            }
            memcpy(buffer, report, resp_len);
        } else {
            resp_len = (uint16_t)(SWITCH_LEGACY_REPORT_SIZE - 1);
            if (resp_len > reqlen) {
                resp_len = reqlen;
            }
            memcpy(buffer, report + 1, resp_len);
        }
    } else if (device_config_get_mode() == GENERIC_HID_MODE &&
               report_type == HID_REPORT_TYPE_INPUT &&
               (report_id == 0 || report_id == GENERIC_HID_REPORT_ID)) {
        bridge_hid_gamepad_report_t report;
        hid_report_make_neutral(&report);
        resp_len = (uint16_t)sizeof(report);
        if (resp_len > reqlen) {
            resp_len = reqlen;
        }
        memcpy(buffer, &report, resp_len);
    } else {
        resp_len = reqlen;
        memset(buffer, 0, resp_len);
    }

    s_hid_get_last_resp_len = resp_len;
    APP_LOGI(TAG, "HID GET instance=%u report_id=0x%02x type=%d req=%u resp=%u",
             (unsigned)instance,
             report_id,
             report_type,
             (unsigned)reqlen,
             (unsigned)resp_len);
    return resp_len;
}

void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id, hid_report_type_t report_type,
                           uint8_t const *buffer, uint16_t bufsize)
{
    uint8_t effective_report_id = report_id;
    uint8_t const *payload = buffer;
    uint16_t payload_size = bufsize;

    if (effective_report_id == 0 && buffer && bufsize > 0) {
        effective_report_id = buffer[0];
        payload = buffer + 1;
        payload_size = (uint16_t)(bufsize - 1);
    }

    s_hid_out_count++;
    s_hid_out_last_report_id = report_id;
    s_hid_out_last_effective_report_id = effective_report_id;
    s_hid_out_last_type = (uint8_t)report_type;
    s_hid_out_last_len = bufsize;
    s_hid_out_last_first_byte = buffer && bufsize > 0 ? buffer[0] : 0;

    bool quiet_output = (effective_report_id == NINTENDO_OUTPUT_REPORT_ID ||
                         effective_report_id == SWITCH_LEGACY_REPORT_ID_RUMBLE) &&
                        !app_log_debug_enabled();
    if (quiet_output) {
        APP_LOGD(TAG, "HID OUT instance=%u report_id=0x%02x effective=0x%02x type=%d size=%u",
                 (unsigned)instance,
                 report_id,
                 effective_report_id,
                 report_type,
                 (unsigned)bufsize);
    } else {
        APP_LOGI(TAG, "HID OUT instance=%u report_id=0x%02x effective=0x%02x type=%d size=%u",
                 (unsigned)instance,
                 report_id,
                 effective_report_id,
                 report_type,
                 (unsigned)bufsize);
    }
    if ((device_config_get_mode() == NINTENDO_EXPERIMENT_MODE ||
         device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE) &&
        report_type == HID_REPORT_TYPE_FEATURE &&
        effective_report_id == MANAGER_FEATURE_REPORT_ID &&
        manager_feature_set_command(payload, payload_size)) {
        manager_feature_handle_set(payload, payload_size);
        return;
    }

    if (device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE ||
        device_config_get_mode() == SWITCH1_PRO_MODE) {
        uint8_t full_report[SWITCH_LEGACY_REPORT_SIZE];
        full_report[0] = effective_report_id;
        uint16_t copy_len = payload_size > (SWITCH_LEGACY_REPORT_SIZE - 1) ?
            (SWITCH_LEGACY_REPORT_SIZE - 1) : payload_size;
        memcpy(full_report + 1, payload, copy_len);
        if (copy_len < SWITCH_LEGACY_REPORT_SIZE - 1) {
            memset(full_report + 1 + copy_len, 0, (SWITCH_LEGACY_REPORT_SIZE - 1) - copy_len);
        }
        switch_legacy_handle_output(instance, full_report, (uint16_t)(copy_len + 1));
        return;
    }

    if (device_config_get_mode() == NINTENDO_EXPERIMENT_MODE &&
        effective_report_id == NINTENDO_OUTPUT_REPORT_ID) {
        uint8_t full_report[NINTENDO_REPORT_SIZE];
        full_report[0] = effective_report_id;
        uint16_t copy_len = payload_size > (NINTENDO_REPORT_SIZE - 1) ? (NINTENDO_REPORT_SIZE - 1) : payload_size;
        memcpy(full_report + 1, payload, copy_len);
        if (copy_len < NINTENDO_REPORT_SIZE - 1) {
            memset(full_report + 1 + copy_len, 0, (NINTENDO_REPORT_SIZE - 1) - copy_len);
        }
        usb_switch2_vendor_bridge_hid_output_to_ble(full_report, (uint16_t)(copy_len + 1));
    }
}
