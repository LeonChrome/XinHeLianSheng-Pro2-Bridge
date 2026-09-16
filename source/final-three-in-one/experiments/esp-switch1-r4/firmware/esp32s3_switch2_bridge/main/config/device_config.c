#include "device_config.h"
#include <stdio.h>
#include "nvs.h"
#include "app_log.h"

static const char *TAG = "config";
static const char *NVS_NAMESPACE = "bridge";
#ifndef DEVICE_PROFILE_LOCKED_MODE
static const char *NVS_KEY_MODE = "mode";
#endif
static const char *NVS_KEY_REPORT_RATE = "rate_hz";
static const char *NVS_KEY_BLE_AUTO = "ble_auto";
static const char *NVS_KEY_BLE_TARGET = "ble_target";

#define DEFAULT_REPORT_RATE_HZ 133
#define MIN_REPORT_RATE_HZ 20
#define MAX_REPORT_RATE_HZ 1000
#define BLE_TARGET_MAX_LEN 40
#ifndef DEVICE_DEFAULT_MODE
#define DEVICE_DEFAULT_MODE NINTENDO_EXPERIMENT_MODE
#endif

static device_mode_t s_mode = DEVICE_DEFAULT_MODE;
static bool s_bridge_running = true;
static hid_test_mode_t s_hid_test_mode = HID_TEST_NEUTRAL;
static uint16_t s_report_rate_hz = DEFAULT_REPORT_RATE_HZ;
static bool s_ble_autoconnect = true;
static char s_ble_target[BLE_TARGET_MAX_LEN];

static uint16_t sanitize_report_rate_hz(uint16_t rate_hz)
{
    if (rate_hz < MIN_REPORT_RATE_HZ) {
        return MIN_REPORT_RATE_HZ;
    }
    if (rate_hz > MAX_REPORT_RATE_HZ) {
        return MAX_REPORT_RATE_HZ;
    }
    return rate_hz;
}

void device_config_init(void)
{
    s_mode = DEVICE_DEFAULT_MODE;
    s_bridge_running = true;
    s_hid_test_mode = HID_TEST_NEUTRAL;
    s_report_rate_hz = DEFAULT_REPORT_RATE_HZ;
    s_ble_autoconnect = true;
    s_ble_target[0] = 0;

    nvs_handle_t handle;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READONLY, &handle);
    if (err == ESP_OK) {
#ifdef DEVICE_PROFILE_LOCKED_MODE
        APP_LOGI(TAG, "release profile locks device mode to %s",
                 device_mode_to_string(s_mode));
#else
        uint8_t stored_mode = (uint8_t)DEVICE_DEFAULT_MODE;
        err = nvs_get_u8(handle, NVS_KEY_MODE, &stored_mode);

        if (err == ESP_OK && stored_mode <= SWITCH1_PRO_MODE) {
            s_mode = (device_mode_t)stored_mode;
        } else if (err == ESP_ERR_NVS_NOT_FOUND) {
            APP_LOGI(TAG, "no persisted mode; defaulting to %s", device_mode_to_string(s_mode));
        } else if (err != ESP_OK) {
            APP_LOGW(TAG, "failed to read persisted mode err=%d; defaulting to %s",
                     (int)err,
                     device_mode_to_string(s_mode));
        } else {
            APP_LOGW(TAG, "invalid persisted mode=%u; defaulting to %s",
                     (unsigned)stored_mode,
                     device_mode_to_string(s_mode));
        }
#endif

        uint16_t stored_rate = DEFAULT_REPORT_RATE_HZ;
        err = nvs_get_u16(handle, NVS_KEY_REPORT_RATE, &stored_rate);
        if (err == ESP_OK) {
            s_report_rate_hz = sanitize_report_rate_hz(stored_rate);
        } else if (err == ESP_ERR_NVS_NOT_FOUND) {
            APP_LOGI(TAG, "no persisted report rate; defaulting to %u Hz", (unsigned)s_report_rate_hz);
        } else {
            APP_LOGW(TAG, "failed to read report rate err=%d; defaulting to %u Hz",
                     (int)err,
                     (unsigned)s_report_rate_hz);
        }

        uint8_t stored_auto = 1;
        err = nvs_get_u8(handle, NVS_KEY_BLE_AUTO, &stored_auto);
        if (err == ESP_OK) {
            s_ble_autoconnect = stored_auto != 0;
        } else if (err == ESP_ERR_NVS_NOT_FOUND) {
            APP_LOGI(TAG, "no persisted BLE autoconnect; defaulting to enabled");
        } else {
            APP_LOGW(TAG, "failed to read BLE autoconnect err=%d; defaulting to enabled", (int)err);
        }

        size_t target_len = sizeof(s_ble_target);
        err = nvs_get_str(handle, NVS_KEY_BLE_TARGET, s_ble_target, &target_len);
        if (err == ESP_ERR_NVS_NOT_FOUND) {
            s_ble_target[0] = 0;
            APP_LOGI(TAG, "no persisted BLE target; autoconnect will scan");
        } else if (err != ESP_OK) {
            s_ble_target[0] = 0;
            APP_LOGW(TAG, "failed to read BLE target err=%d; autoconnect will scan", (int)err);
        } else {
            s_ble_target[sizeof(s_ble_target) - 1] = 0;
        }
        nvs_close(handle);
    } else if (err == ESP_ERR_NVS_NOT_FOUND) {
        APP_LOGI(TAG, "config namespace not found; defaulting to %s", device_mode_to_string(s_mode));
    } else {
        APP_LOGW(TAG, "failed to open config namespace err=%d; defaulting to %s",
                 (int)err,
                 device_mode_to_string(s_mode));
    }

    APP_LOGI(TAG, "device mode loaded: %s report_rate_hz=%u ble_auto=%s ble_target=%s",
             device_mode_to_string(s_mode),
             (unsigned)s_report_rate_hz,
             s_ble_autoconnect ? "on" : "off",
             s_ble_target[0] ? s_ble_target : "<scan>");
}

device_mode_t device_config_get_mode(void)
{
    return s_mode;
}

void device_config_set_mode(device_mode_t mode)
{
    s_mode = mode;
}

esp_err_t device_config_save_mode(device_mode_t mode)
{
    if (mode != GENERIC_HID_MODE &&
        mode != NINTENDO_EXPERIMENT_MODE &&
        mode != XINPUT_EXPERIMENT_MODE &&
        mode != DUAL_PRO2_EXPERIMENT_MODE &&
        mode != SWITCH1_PRO_MODE) {
        return ESP_ERR_INVALID_ARG;
    }

#ifdef DEVICE_PROFILE_LOCKED_MODE
    if (mode != DEVICE_DEFAULT_MODE) {
        APP_LOGW(TAG, "release profile rejected mode change from %s to %s",
                 device_mode_to_string(DEVICE_DEFAULT_MODE),
                 device_mode_to_string(mode));
        return ESP_ERR_NOT_SUPPORTED;
    }
    s_mode = DEVICE_DEFAULT_MODE;
    return ESP_OK;
#else
    nvs_handle_t handle;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle);
    if (err != ESP_OK) {
        APP_LOGW(TAG, "failed to open config namespace for write err=%d", (int)err);
        return err;
    }

    err = nvs_set_u8(handle, NVS_KEY_MODE, (uint8_t)mode);
    if (err == ESP_OK) {
        err = nvs_commit(handle);
    }
    nvs_close(handle);

    if (err == ESP_OK) {
        s_mode = mode;
        APP_LOGI(TAG, "device mode saved: %s", device_mode_to_string(mode));
    } else {
        APP_LOGW(TAG, "failed to save mode err=%d", (int)err);
    }
    return err;
#endif
}

const char *device_mode_to_string(device_mode_t mode)
{
    switch (mode) {
    case GENERIC_HID_MODE:
        return "generic";
    case NINTENDO_EXPERIMENT_MODE:
        return "nintendo";
    case XINPUT_EXPERIMENT_MODE:
        return "xinput";
    case DUAL_PRO2_EXPERIMENT_MODE:
        return "dual_pro2";
    case SWITCH1_PRO_MODE:
        return "switch1_pro";
    default:
        return "unknown";
    }
}

bool device_config_bridge_running(void)
{
    return s_bridge_running;
}

void device_config_set_bridge_running(bool running)
{
    s_bridge_running = running;
}

hid_test_mode_t device_config_get_hid_test_mode(void)
{
    return s_hid_test_mode;
}

void device_config_set_hid_test_mode(hid_test_mode_t mode)
{
    s_hid_test_mode = mode;
}

const char *hid_test_mode_to_string(hid_test_mode_t mode)
{
    switch (mode) {
    case HID_TEST_AUTO_A:
        return "auto_a";
    case HID_TEST_NEUTRAL:
        return "neutral";
    case HID_TEST_A_HELD:
        return "a_held";
    default:
        return "unknown";
    }
}

uint16_t device_config_get_report_rate_hz(void)
{
    return s_report_rate_hz;
}

void device_config_set_report_rate_hz(uint16_t rate_hz)
{
    s_report_rate_hz = sanitize_report_rate_hz(rate_hz);
}

esp_err_t device_config_save_report_rate_hz(uint16_t rate_hz)
{
    uint16_t sanitized = sanitize_report_rate_hz(rate_hz);

    nvs_handle_t handle;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle);
    if (err != ESP_OK) {
        APP_LOGW(TAG, "failed to open config namespace for report-rate write err=%d", (int)err);
        return err;
    }

    err = nvs_set_u16(handle, NVS_KEY_REPORT_RATE, sanitized);
    if (err == ESP_OK) {
        err = nvs_commit(handle);
    }
    nvs_close(handle);

    if (err == ESP_OK) {
        s_report_rate_hz = sanitized;
        APP_LOGI(TAG, "report rate saved: %u Hz", (unsigned)s_report_rate_hz);
    } else {
        APP_LOGW(TAG, "failed to save report rate err=%d", (int)err);
    }
    return err;
}

bool device_config_get_ble_autoconnect(void)
{
    return s_ble_autoconnect;
}

esp_err_t device_config_save_ble_autoconnect(bool enabled)
{
    nvs_handle_t handle;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle);
    if (err != ESP_OK) {
        APP_LOGW(TAG, "failed to open config namespace for BLE auto write err=%d", (int)err);
        return err;
    }

    err = nvs_set_u8(handle, NVS_KEY_BLE_AUTO, enabled ? 1 : 0);
    if (err == ESP_OK) {
        err = nvs_commit(handle);
    }
    nvs_close(handle);

    if (err == ESP_OK) {
        s_ble_autoconnect = enabled;
        APP_LOGI(TAG, "BLE autoconnect saved: %s", enabled ? "on" : "off");
    } else {
        APP_LOGW(TAG, "failed to save BLE autoconnect err=%d", (int)err);
    }
    return err;
}

const char *device_config_get_ble_target(void)
{
    return s_ble_target;
}

esp_err_t device_config_save_ble_target(const char *target)
{
    char sanitized[BLE_TARGET_MAX_LEN];
    snprintf(sanitized, sizeof(sanitized), "%s", target ? target : "");

    nvs_handle_t handle;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle);
    if (err != ESP_OK) {
        APP_LOGW(TAG, "failed to open config namespace for BLE target write err=%d", (int)err);
        return err;
    }

    if (sanitized[0]) {
        err = nvs_set_str(handle, NVS_KEY_BLE_TARGET, sanitized);
    } else {
        err = nvs_erase_key(handle, NVS_KEY_BLE_TARGET);
        if (err == ESP_ERR_NVS_NOT_FOUND) {
            err = ESP_OK;
        }
    }
    if (err == ESP_OK) {
        err = nvs_commit(handle);
    }
    nvs_close(handle);

    if (err == ESP_OK) {
        snprintf(s_ble_target, sizeof(s_ble_target), "%s", sanitized);
        APP_LOGI(TAG, "BLE target saved: %s", s_ble_target[0] ? s_ble_target : "<scan>");
    } else {
        APP_LOGW(TAG, "failed to save BLE target err=%d", (int)err);
    }
    return err;
}

const char *device_config_get_version(void)
{
    return "5.9.18-switch1-connect-rumble-test-r4";
}



