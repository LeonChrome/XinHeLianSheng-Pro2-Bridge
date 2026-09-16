#pragma once

#include <stdbool.h>
#include "esp_err.h"
#include "hid_report.h"

esp_err_t usb_hid_device_init(void);
bool usb_hid_device_ready(void);
bool usb_hid_device_dual_ready(void);
const char *usb_hid_device_state_string(void);
uint32_t usb_hid_device_out_count(void);
uint8_t usb_hid_device_last_out_report_id(void);
uint8_t usb_hid_device_last_out_effective_report_id(void);
uint8_t usb_hid_device_last_out_type(void);
uint16_t usb_hid_device_last_out_len(void);
uint8_t usb_hid_device_last_out_first_byte(void);
uint32_t usb_hid_device_get_count(void);
uint8_t usb_hid_device_last_get_report_id(void);
uint8_t usb_hid_device_last_get_type(void);
uint16_t usb_hid_device_last_get_req_len(void);
uint16_t usb_hid_device_last_get_resp_len(void);
esp_err_t usb_hid_device_send_generic_report(const bridge_hid_gamepad_report_t *report);
esp_err_t usb_hid_device_send_generic_report_instance(uint8_t instance, const bridge_hid_gamepad_report_t *report);
esp_err_t usb_hid_device_send_dual_generic_report(const bridge_hid_gamepad_report_t *slot_a,
                                                  const bridge_hid_gamepad_report_t *slot_b);
esp_err_t usb_hid_device_send_nintendo_report(const uint8_t report[NINTENDO_REPORT_SIZE]);
esp_err_t usb_hid_device_send_dual_switch_legacy_reports(const uint8_t slot_a[SWITCH_LEGACY_REPORT_SIZE],
                                                         const uint8_t slot_b[SWITCH_LEGACY_REPORT_SIZE]);
esp_err_t usb_hid_device_send_switch1_pro_report(const uint8_t report[SWITCH_LEGACY_REPORT_SIZE]);
bool usb_hid_device_switch1_imu_enabled(void);
bool usb_hid_device_switch1_vibration_command_seen(void);
bool usb_hid_device_switch1_vibration_enabled(void);
uint32_t usb_hid_device_switch1_vibration_command_count(void);
uint32_t usb_hid_device_switch1_rumble_rx_count(void);
uint32_t usb_hid_device_switch1_rumble_active_count(void);
uint32_t usb_hid_device_switch1_rumble_neutral_count(void);
uint32_t usb_hid_device_switch1_rumble_forwarded_count(void);
uint32_t usb_hid_device_switch1_rumble_blocked_count(void);
