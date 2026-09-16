#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"

typedef enum {
    GENERIC_HID_MODE = 0,
    NINTENDO_EXPERIMENT_MODE = 1,
    XINPUT_EXPERIMENT_MODE = 2,
    DUAL_PRO2_EXPERIMENT_MODE = 3,
    SWITCH1_PRO_MODE = 4
} device_mode_t;

typedef enum {
    HID_TEST_AUTO_A = 0,
    HID_TEST_NEUTRAL = 1,
    HID_TEST_A_HELD = 2
} hid_test_mode_t;

void device_config_init(void);
device_mode_t device_config_get_mode(void);
void device_config_set_mode(device_mode_t mode);
esp_err_t device_config_save_mode(device_mode_t mode);
const char *device_mode_to_string(device_mode_t mode);
bool device_config_bridge_running(void);
void device_config_set_bridge_running(bool running);
hid_test_mode_t device_config_get_hid_test_mode(void);
void device_config_set_hid_test_mode(hid_test_mode_t mode);
const char *hid_test_mode_to_string(hid_test_mode_t mode);
uint16_t device_config_get_report_rate_hz(void);
void device_config_set_report_rate_hz(uint16_t rate_hz);
esp_err_t device_config_save_report_rate_hz(uint16_t rate_hz);
bool device_config_get_ble_autoconnect(void);
esp_err_t device_config_save_ble_autoconnect(bool enabled);
const char *device_config_get_ble_target(void);
esp_err_t device_config_save_ble_target(const char *target);
const char *device_config_get_version(void);
