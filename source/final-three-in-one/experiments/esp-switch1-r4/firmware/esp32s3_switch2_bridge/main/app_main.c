#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <unistd.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "nvs_flash.h"
#include "app_log.h"
#include "ble_central.h"
#include "ble_dual_probe.h"
#include "control_protocol.h"
#include "device_config.h"
#include "hid_report.h"
#include "internal_gamepad_state.h"
#include "report_mapper.h"
#include "report_rate_stats.h"
#include "switch2_state.h"
#include "usb_hid_device.h"
#include "usb_xinput_device.h"
#include "xbox_paddle_mapper.h"

static const char *TAG = "app";

#define BLE_LIVE_STALE_US 1000000LL
#define AUTO_A_TOGGLE_US 500000LL
#define HID_SOURCE_POLL_MS 1
#define HID_NEUTRAL_KEEPALIVE_US 50000LL
#define SWITCH1_REPORT_INTERVAL_US 15000LL
#define CONTROL_LINE_MAX 192

static int16_t read_i16_le(const uint8_t *src)
{
    return (int16_t)((uint16_t)src[0] | ((uint16_t)src[1] << 8));
}

static void write_i16_le(uint8_t *dst, int16_t value)
{
    dst[0] = (uint8_t)((uint16_t)value & 0xffu);
    dst[1] = (uint8_t)(((uint16_t)value >> 8) & 0xffu);
}

static void switch1_resample_motion(const switch2_state_t *previous,
                                    const switch2_state_t *current,
                                    switch2_state_t *output)
{
    *output = *current;
    if (!current->motion_valid) {
        return;
    }

    const uint8_t *current_sample =
        current->motion + SWITCH2_MOTION_BLOCK_SIZE - SWITCH2_MOTION_SAMPLE_SIZE;
    const uint8_t *previous_sample = previous && previous->motion_valid
        ? previous->motion + SWITCH2_MOTION_BLOCK_SIZE - SWITCH2_MOTION_SAMPLE_SIZE
        : current_sample;

    for (uint8_t axis = 0; axis < 6; axis++) {
        int16_t first = read_i16_le(previous_sample + axis * 2u);
        int16_t last = read_i16_le(current_sample + axis * 2u);
        int16_t middle = (int16_t)(((int32_t)first + last) / 2);
        write_i16_le(output->motion + axis * 2u, first);
        write_i16_le(output->motion + SWITCH2_MOTION_SAMPLE_SIZE + axis * 2u, middle);
        write_i16_le(output->motion + 2u * SWITCH2_MOTION_SAMPLE_SIZE + axis * 2u, last);
    }
}

static bool usb_current_mode_ready(void)
{
    if (device_config_get_mode() == XINPUT_EXPERIMENT_MODE) {
        return usb_xinput_device_ready();
    }
    if (device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE) {
        return usb_hid_device_dual_ready();
    }
    return usb_hid_device_ready();
}

static esp_err_t send_usb_state_report(const switch2_state_t *state)
{
    internal_gamepad_state_t internal;
    switch2_state_to_internal(state, &internal);

    if (device_config_get_mode() == SWITCH1_PRO_MODE) {
        uint8_t switch1_report[SWITCH_LEGACY_REPORT_SIZE];
        report_mapper_state_to_switch1_pro_report(
            state,
            usb_hid_device_switch1_imu_enabled(),
            switch1_report);
        return usb_hid_device_send_switch1_pro_report(switch1_report);
    }

    if (device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE) {
        uint8_t slot_a[SWITCH_LEGACY_REPORT_SIZE];
        uint8_t slot_b[SWITCH_LEGACY_REPORT_SIZE];
        report_mapper_internal_to_switch_legacy_report(&internal, slot_a);
        report_mapper_internal_to_switch_legacy_report(&internal, slot_b);
        return usb_hid_device_send_dual_switch_legacy_reports(slot_a, slot_b);
    }

    if (device_config_get_mode() == NINTENDO_EXPERIMENT_MODE) {
        uint8_t nintendo_report[NINTENDO_REPORT_SIZE];
        report_mapper_internal_to_nintendo_report(&internal, nintendo_report);
        return usb_hid_device_send_nintendo_report(nintendo_report);
    }

    if (device_config_get_mode() == XINPUT_EXPERIMENT_MODE) {
        return usb_xinput_device_send_report(&internal);
    }

    bridge_hid_gamepad_report_t report;
    report_mapper_internal_to_generic_report(&internal, &report);
    return usb_hid_device_send_generic_report(&report);
}

static void control_task(void *arg)
{
    (void)arg;
    char line[CONTROL_LINE_MAX];
    uint8_t rx[64];
    size_t line_len = 0;
    bool overflow = false;
    static char reply[16384];

    while (true) {
        int rx_len = read(STDIN_FILENO, rx, sizeof(rx));
        if (rx_len <= 0) {
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
        }

        for (int i = 0; i < rx_len; i++) {
            uint8_t ch = rx[i];
            if (ch == '\r' || ch == '\n') {
                if (overflow) {
                    APP_LOGW(TAG, "serial control line too long; discarded");
                    printf("{\"ok\":false,\"cmd\":\"serial\",\"error\":\"command line too long\"}\n");
                    overflow = false;
                    line_len = 0;
                    continue;
                }
                if (line_len > 0) {
                    line[line_len] = 0;
                    control_protocol_handle_line(line, reply, sizeof(reply));
                    line_len = 0;
                }
                continue;
            }

            if (overflow) {
                continue;
            }
            if (line_len + 1 >= sizeof(line)) {
                overflow = true;
                line_len = 0;
                continue;
            }

            line[line_len++] = (char)ch;
        }
    }
}

static uint32_t report_delay_ms(void)
{
    uint16_t rate_hz = device_config_get_report_rate_hz();
    uint32_t delay_ms = (1000u + rate_hz - 1u) / rate_hz;
    return delay_ms == 0 ? 1 : delay_ms;
}

static bool make_test_state(switch2_state_t *state, bool *pressed, int64_t now_us)
{
    static int64_t next_toggle_us;

    hid_test_mode_t test_mode = device_config_get_hid_test_mode();
    switch2_state_reset(state);

    if (test_mode == HID_TEST_AUTO_A && now_us >= next_toggle_us) {
        *pressed = !*pressed;
        next_toggle_us = now_us + AUTO_A_TOGGLE_US;
    }

    bool a_pressed = test_mode == HID_TEST_A_HELD ||
                     (test_mode == HID_TEST_AUTO_A && *pressed);
    if (a_pressed) {
        switch2_state_set_button(state, SWITCH2_BUTTON_A, true);
    }
    return a_pressed;
}

static void hid_report_task(void *arg)
{
    (void)arg;
    bool pressed = false;
    bool live_source_latched = false;
    bool neutral_latched = false;
    uint32_t last_sent_live_updates = 0;
    int64_t next_neutral_keepalive_us = 0;
    int64_t next_test_report_us = 0;
    int64_t next_log_us = 0;
    int64_t next_switch1_report_us = 0;
    switch2_state_t switch1_previous_report_state;
    bool switch1_previous_report_valid = false;

    while (true) {
        int64_t now_us = esp_timer_get_time();
        switch2_state_t state;
        const char *source = "test";
        uint32_t live_updates = 0;
        int64_t live_age_us = 0;
        hid_test_mode_t test_mode = device_config_get_hid_test_mode();

        usb_xinput_device_poll_out();

        if (!device_config_bridge_running()) {
            switch2_state_reset(&state);
            live_source_latched = false;

            bool should_send_neutral = !neutral_latched ||
                                       now_us >= next_neutral_keepalive_us;
            if (usb_current_mode_ready() && should_send_neutral) {
                esp_err_t err = send_usb_state_report(&state);
                if (err == ESP_OK) {
                    neutral_latched = true;
                    next_neutral_keepalive_us =
                        now_us + HID_NEUTRAL_KEEPALIVE_US;
                    if (now_us >= next_log_us) {
                        APP_LOGI(TAG,
                                 "USB stopped; neutral report sent mode=%s cadence=20hz_keepalive",
                                 device_mode_to_string(device_config_get_mode()));
                        next_log_us = now_us + 1000000LL;
                    }
                } else if (now_us >= next_log_us) {
                    APP_LOGW(TAG, "USB stopped; neutral report failed mode=%s err=%d",
                             device_mode_to_string(device_config_get_mode()),
                             (int)err);
                    next_log_us = now_us + 1000000LL;
                }
            } else if (now_us >= next_log_us) {
                APP_LOGI(TAG, "USB stopped; USB not ready state=%s mode=%s",
                         usb_hid_device_state_string(),
                         device_mode_to_string(device_config_get_mode()));
                next_log_us = now_us + 1000000LL;
            }
            vTaskDelay(pdMS_TO_TICKS(HID_SOURCE_POLL_MS));
            continue;
        }

        bool live_valid = switch2_state_get_live(&state, &live_updates, &live_age_us);
        bool using_live = live_valid && live_age_us <= BLE_LIVE_STALE_US;
        bool a_pressed = false;
        bool should_send = false;
        if (using_live) {
            source = "ble";
            if (device_config_get_mode() == SWITCH1_PRO_MODE) {
                should_send = !live_source_latched ||
                              (live_updates != last_sent_live_updates &&
                               now_us >= next_switch1_report_us);
            } else {
                should_send = !live_source_latched ||
                              live_updates != last_sent_live_updates;
            }
            neutral_latched = false;
        } else {
            a_pressed = make_test_state(&state, &pressed, now_us);
            live_source_latched = false;
            switch1_previous_report_valid = false;
            bool active_test = test_mode != HID_TEST_NEUTRAL;
            if (active_test) {
                should_send = now_us >= next_test_report_us;
                if (should_send) {
                    next_test_report_us =
                        now_us + (int64_t)report_delay_ms() * 1000LL;
                }
            } else {
                should_send = !neutral_latched ||
                              now_us >= next_neutral_keepalive_us;
            }
        }

        if (usb_current_mode_ready() && should_send) {
            switch2_state_t output_state;
            const switch2_state_t *state_to_send = &state;
            if (using_live && device_config_get_mode() == SWITCH1_PRO_MODE) {
                switch1_resample_motion(
                    switch1_previous_report_valid ? &switch1_previous_report_state : NULL,
                    &state,
                    &output_state);
                state_to_send = &output_state;
            }
            esp_err_t err = send_usb_state_report(state_to_send);
            if (err == ESP_OK) {
                if (using_live) {
                    last_sent_live_updates = live_updates;
                    live_source_latched = true;
                    if (device_config_get_mode() == SWITCH1_PRO_MODE) {
                        switch1_previous_report_state = state;
                        switch1_previous_report_valid = true;
                        next_switch1_report_us = now_us + SWITCH1_REPORT_INTERVAL_US;
                    }
                } else if (test_mode == HID_TEST_NEUTRAL) {
                    neutral_latched = true;
                    next_neutral_keepalive_us =
                        now_us + HID_NEUTRAL_KEEPALIVE_US;
                }
                if (now_us >= next_log_us) {
                    APP_LOGI(TAG, "report loop usb_mode=%s source=%s cadence=%s configured_test_rate_hz=%u live_updates=%lu live_age_ms=%lld test_mode=%s test_a=%s",
                             device_mode_to_string(device_config_get_mode()),
                             source,
                             using_live ?
                             (device_config_get_mode() == SWITCH1_PRO_MODE
                                 ? "switch1_15ms_resampled"
                                 : "ble_source_updates") :
                             (test_mode == HID_TEST_NEUTRAL ? "20hz_neutral_keepalive" : "fixed_test_rate"),
                             (unsigned)device_config_get_report_rate_hz(),
                             (unsigned long)live_updates,
                             using_live ? (long long)(live_age_us / 1000) : -1LL,
                             hid_test_mode_to_string(test_mode),
                             a_pressed ? "pressed" : "released");
                    next_log_us = now_us + 1000000LL;
                }
            } else if (!usb_current_mode_ready() && now_us >= next_log_us) {
                APP_LOGW(TAG, "report send failed err=%d", (int)err);
                next_log_us = now_us + 1000000LL;
            }
        } else if (!usb_current_mode_ready() && now_us >= next_log_us) {
            APP_LOGI(TAG, "USB not ready state=%s; firmware alive mode=%s",
                     usb_hid_device_state_string(),
                     device_mode_to_string(device_config_get_mode()));
            next_log_us = now_us + 1000000LL;
        }

        vTaskDelay(pdMS_TO_TICKS(HID_SOURCE_POLL_MS));
    }
}

void app_main(void)
{
    app_log_init();
    APP_LOGI(TAG, "ESP32-S3 Switch 2 bridge firmware 5.9.18-switch1-connect-rumble-test-r4 starting");
    APP_LOGI(TAG, "Input cadence: latest-state BLE notification paced; no fixed-rate historical replay");
    APP_LOGI(TAG, "Stable path: Steam Switch Pro/Pro2 layout, BLE input, raw-like gyro, rumble, and boot autoconnect are verified");
    APP_LOGI(TAG, "Experimental path: dual Pro2 BLE probe mode measures two-controller connection capacity before dual-HID work");
    if (device_config_get_mode() == SWITCH1_PRO_MODE) {
        APP_LOGI(TAG, "Switch 1 path: USB 057e:2009, one 8 ms HID interface, source-paced BLE input, three-sample IMU, and HD-rumble approximation");
        APP_LOGI(TAG, "Switch console requirement: enable Pro Controller Wired Communication; NFC is not available over this bridge");
    }

    esp_err_t nvs_err = nvs_flash_init();
    if (nvs_err == ESP_ERR_NVS_NO_FREE_PAGES || nvs_err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        nvs_err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(nvs_err);

    device_config_init();
    xbox_paddle_mapper_init();
    switch2_state_init();
    report_rate_stats_init();
    usb_xinput_device_init();

    ESP_ERROR_CHECK(usb_hid_device_init());
    control_protocol_init();
    ble_central_init();

    xTaskCreate(control_task, "control_task", 6144, NULL, 6, NULL);
    xTaskCreate(hid_report_task, "hid_report_task", 4096, NULL, 5, NULL);
}



