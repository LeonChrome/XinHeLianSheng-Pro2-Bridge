#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"
#include "normalized_rumble.h"

void usb_switch2_vendor_init(void);
bool usb_switch2_vendor_mounted(void);
bool usb_switch2_vendor_hid_guard_active(void);
const char *usb_switch2_vendor_hid_guard_state(void);
void usb_switch2_vendor_reset_hid_guard(void);
void usb_switch2_vendor_arm_hid_guard(void);
void usb_switch2_vendor_release_hid_guard(void);
void usb_switch2_vendor_bridge_hid_output_to_ble(const uint8_t *data, uint16_t len);
esp_err_t usb_switch2_vendor_send_raw02_payload(const uint8_t *payload, uint16_t len);
void usb_switch2_vendor_start_hd_rumble_self_test(void);
void usb_switch2_vendor_start_hd_rumble_self_test_ms(uint16_t hold_ms);
void usb_switch2_vendor_start_normalized_rumble(const normalized_rumble_t *rumble,
                                                const char *reason);
void usb_switch2_vendor_stop_hd_rumble(void);
bool usb_switch2_vendor_hd_rumble_active(void);
uint32_t usb_switch2_vendor_hd_rumble_update_count(void);
uint32_t usb_switch2_vendor_hd_rumble_write_count(void);
uint32_t usb_switch2_vendor_hd_rumble_stop_count(void);
uint32_t usb_switch2_vendor_hd_rumble_error_count(void);
uint32_t usb_switch2_vendor_hd_rumble_preset_ignored_count(void);
void usb_switch2_vendor_get_hd_rumble_tuning(uint16_t *scale_percent, uint16_t *hold_ms,
                                             uint16_t *tick_ms, uint8_t *stop_packets);
void usb_switch2_vendor_set_hd_rumble_tuning(uint16_t scale_percent, uint16_t hold_ms,
                                             uint16_t tick_ms, uint8_t stop_packets);
uint32_t usb_switch2_vendor_rx_count(void);
uint32_t usb_switch2_vendor_tx_count(void);
uint32_t usb_switch2_vendor_tx_done_count(void);
uint32_t usb_switch2_vendor_last_sent_bytes(void);
uint16_t usb_switch2_vendor_last_rx_len(void);
uint32_t usb_switch2_vendor_last_address(void);
uint16_t usb_switch2_vendor_last_tx_len(void);
uint8_t usb_switch2_vendor_last_cmd(void);
uint8_t usb_switch2_vendor_last_arg(void);
uint16_t usb_switch2_vendor_pending_len(void);
uint16_t usb_switch2_vendor_pending_offset(void);
