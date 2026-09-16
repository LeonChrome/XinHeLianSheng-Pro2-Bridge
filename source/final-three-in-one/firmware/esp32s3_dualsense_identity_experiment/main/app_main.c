#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#include "dualsense_report.h"
#include "dualsense_report_mapper.h"
#include "dualsense_runtime_stats.h"
#include "device/dcd.h"
#include "device/usbd_pvt.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nvs_flash.h"
#include "pro2_input_backend.h"
#include "pro2_rumble_backend.h"
#include "tinyusb.h"
#include "tusb.h"
#include "usb_dualsense_descriptor.h"
#include "v55_control_protocol.h"

#if DS5_ENABLE_UAC1_AUDIO || DS5_ENABLE_UAC2_AUDIO
#include "dualsense_haptic_audio.h"
#endif

#ifndef DS5_PROFILE_NAME
#define DS5_PROFILE_NAME "unknown"
#endif
#ifndef DS5_USB_PID
#define DS5_USB_PID 0x0ce6
#endif
#ifndef DS5_USB_PRODUCT
#define DS5_USB_PRODUCT "DualSense Wireless Controller"
#endif

#ifndef DS5_ENABLE_USB_AUDIO
#define DS5_ENABLE_USB_AUDIO 0
#endif
#ifndef DS5_ENABLE_UAC1_AUDIO
#define DS5_ENABLE_UAC1_AUDIO 0
#endif
#ifndef DS5_ENABLE_UAC1_CONTROL_ONLY
#define DS5_ENABLE_UAC1_CONTROL_ONLY 0
#endif
#ifndef DS5_ENABLE_UAC1_STREAMING_ALT0
#define DS5_ENABLE_UAC1_STREAMING_ALT0 0
#endif
#ifndef DS5_ENABLE_UAC2_AUDIO
#define DS5_ENABLE_UAC2_AUDIO 0
#endif
#ifndef DS5_AUDIO_CHANNELS
#define DS5_AUDIO_CHANNELS 0
#endif

static const char *TAG = "v5.5_ds5";
static volatile bool s_mounted;
static volatile bool s_suspended;
static volatile bool s_usb_configuration_ready;
static uint32_t s_report_count;
static uint32_t s_report_completed_count;
static uint32_t s_report_failed_count;
static uint32_t s_report_submit_failed_count;
static uint32_t s_report_xfer_failed_count;
static uint32_t s_report_submit_failure_streak;
static uint32_t s_report_not_ready_count;
static uint32_t s_hid_endpoint_kick_count;
static volatile uint32_t s_hid_endpoint_kicks_since_report;
static uint32_t s_usb_recovery_count;
static uint32_t s_usb_recovery_inhibited_count;
static uint32_t s_usb_recovery_inhibit_reason;
static uint32_t s_output_count;
static uint32_t s_feature_get_count;
static uint32_t s_uac_out_xfer_success;
static uint32_t s_uac_out_xfer_errors;
static uint32_t s_uac_out_rearm_failures;
static uint32_t s_uac_set_interface_count;
static uint32_t s_uac_mic_alt1_attempts;
static uint32_t s_uac_mic_alt1_rejects;
static uint32_t s_uac_last_xfer_bytes;
static int32_t s_uac_last_xfer_result;
static uint32_t s_mount_count;
static uint32_t s_umount_count;
static volatile uint32_t s_bus_reset_count;
static uint32_t s_configuration_reset_count;
static uint32_t s_suspend_count;
static uint32_t s_resume_count;
static uint32_t s_report_last_gap_us;
static uint32_t s_report_max_gap_us;
static int64_t s_last_report_us;
static int64_t s_first_report_submit_failure_us;
static int64_t s_last_report_submit_failure_us;
static int64_t s_last_configuration_reset_us;
static int64_t s_last_hid_endpoint_kick_us;
static int64_t s_last_usb_recovery_us;
static int64_t s_last_usb_recovery_inhibited_us;
static int64_t s_uac_last_xfer_us;
static int64_t s_last_output_us;
static int64_t s_last_usb_event_us;
static bool s_usb_recovering;
static volatile bool s_hid_endpoint_kick_pending;
static volatile bool s_usb_bus_reset_pending;
static volatile bool s_usb_bus_reset_was_ready;
static TaskHandle_t s_control_task_handle;
static TaskHandle_t s_input_task_handle;

#define PRO2_INPUT_STALE_US 200000LL
#define PRO2_INPUT_WARMUP_UPDATES 4
#define DS5_SOURCE_POLL_MS 1
#define DS5_NEUTRAL_KEEPALIVE_US 50000LL
#define DS5_HID_ENDPOINT_KICK_STALL_US 1000000LL
#define DS5_HID_ENDPOINT_KICK_COOLDOWN_US 1000000LL
#define DS5_HID_ENDPOINT_KICK_SUBMIT_FAILURE_US 25000LL
#define DS5_HID_ENDPOINT_KICK_SUBMIT_FAILURE_MIN_COUNT 8
#define DS5_HID_ENDPOINT_KICKS_BEFORE_REENUMERATION 2
#define DS5_HID_EMERGENCY_RECOVERY_US 8000000LL
#define DS5_USB_REENUMERATION_GRACE_US 15000000LL
#define DS5_USB_RECOVERY_COOLDOWN_US 10000000LL
#define DS5_USB_RECONNECT_DELAY_MS 250
#define DS5_USB_RECOVERY_INHIBIT_LOG_US 1000000LL
#define V55_CONTROL_LINE_MAX 192

static tusb_desc_endpoint_t const s_hid_in_endpoint_descriptor = {
    .bLength = sizeof(tusb_desc_endpoint_t),
    .bDescriptorType = TUSB_DESC_ENDPOINT,
    .bEndpointAddress = DUALSENSE_USB_HID_EP_IN,
    .bmAttributes = {
        .xfer = TUSB_XFER_INTERRUPT,
    },
    .wMaxPacketSize = 64,
    .bInterval = 4,
};

enum {
    DS5_USB_RECOVERY_INHIBIT_NONE = 0,
    DS5_USB_RECOVERY_INHIBIT_REENUMERATION = 1,
    DS5_USB_RECOVERY_INHIBIT_AUDIO_STREAMING = 2,
};

void tud_mount_cb(void)
{
    int64_t now_us = esp_timer_get_time();
    if (s_usb_bus_reset_pending) {
        bool was_ready = s_usb_bus_reset_was_ready;
        s_usb_bus_reset_pending = false;
        s_usb_bus_reset_was_ready = false;
        s_configuration_reset_count++;
        s_last_configuration_reset_us = now_us;
        ESP_LOGW(TAG,
                 "[DS5_USB] configuration_reset=true source=event_hook_mount previous_ready=%s resets=%lu",
                 was_ready ? "true" : "false",
                 (unsigned long)s_configuration_reset_count);
    }
    s_mounted = true;
    s_suspended = false;
    s_usb_configuration_ready = true;
    s_report_submit_failure_streak = 0;
    s_first_report_submit_failure_us = 0;
    s_hid_endpoint_kicks_since_report = 0;
    s_last_report_us = now_us;
    s_mount_count++;
    s_last_usb_event_us = now_us;
    ESP_LOGI(TAG,
             "[DS5_USB] mounted=true configuration_ready=true mounts=%lu resets=%lu",
             (unsigned long)s_mount_count,
             (unsigned long)s_configuration_reset_count);
}

void tud_event_hook_cb(uint8_t rhport, uint32_t eventid, bool in_isr)
{
    (void)rhport;
    (void)in_isr;
    if (eventid != DCD_EVENT_BUS_RESET) {
        return;
    }

    s_usb_bus_reset_was_ready = s_usb_configuration_ready;
    s_usb_configuration_ready = false;
    s_mounted = false;
    s_suspended = false;
    s_usb_bus_reset_pending = true;
    s_bus_reset_count++;
}

void tud_umount_cb(void)
{
    s_mounted = false;
    s_suspended = false;
    s_usb_configuration_ready = false;
    s_report_submit_failure_streak = 0;
    s_first_report_submit_failure_us = 0;
    s_umount_count++;
    s_last_usb_event_us = esp_timer_get_time();
    ESP_LOGI(TAG, "[DS5_USB] mounted=false");
}

void dualsense_runtime_usb_configuration_reset(void)
{
    bool bus_reset_pending = s_usb_bus_reset_pending;
    bool was_ready = bus_reset_pending
                         ? s_usb_bus_reset_was_ready
                         : s_usb_configuration_ready;
    s_usb_bus_reset_pending = false;
    s_usb_bus_reset_was_ready = false;
    s_usb_configuration_ready = false;
    s_mounted = false;
    s_suspended = false;
    s_hid_endpoint_kick_pending = false;
    s_hid_endpoint_kicks_since_report = 0;
    s_report_submit_failure_streak = 0;
    s_first_report_submit_failure_us = 0;
    s_configuration_reset_count++;
    s_last_configuration_reset_us = esp_timer_get_time();
    s_last_usb_event_us = s_last_configuration_reset_us;
    ESP_LOGW(TAG,
             "[DS5_USB] configuration_reset=true source=%s previous_ready=%s bus_resets=%lu resets=%lu",
             bus_reset_pending ? "bus_reset_event_hook" : "configuration_change",
             was_ready ? "true" : "false",
             (unsigned long)s_bus_reset_count,
             (unsigned long)s_configuration_reset_count);
}

void tud_suspend_cb(bool remote_wakeup_en)
{
    (void)remote_wakeup_en;
    s_suspended = true;
    s_suspend_count++;
    s_last_usb_event_us = esp_timer_get_time();
    ESP_LOGI(TAG, "[DS5_USB] suspended=true");
}

void tud_resume_cb(void)
{
    s_suspended = false;
    s_resume_count++;
    s_last_usb_event_us = esp_timer_get_time();
    ESP_LOGI(TAG, "[DS5_USB] suspended=false");
}

void dualsense_runtime_stats_snapshot(dualsense_runtime_stats_t *out)
{
    if (!out) {
        return;
    }

    *out = (dualsense_runtime_stats_t) {
        .mounted = s_mounted,
        .suspended = s_suspended,
        .configuration_ready = s_usb_configuration_ready,
        .mount_count = s_mount_count,
        .umount_count = s_umount_count,
        .bus_reset_count = s_bus_reset_count,
        .configuration_reset_count = s_configuration_reset_count,
        .suspend_count = s_suspend_count,
        .resume_count = s_resume_count,
        .report_sent = s_report_count,
        .report_completed = s_report_completed_count,
        .report_failed = s_report_failed_count,
        .report_submit_failed = s_report_submit_failed_count,
        .report_xfer_failed = s_report_xfer_failed_count,
        .report_submit_failure_streak = s_report_submit_failure_streak,
        .report_not_ready = s_report_not_ready_count,
        .hid_endpoint_kick_count = s_hid_endpoint_kick_count,
        .usb_recovery_count = s_usb_recovery_count,
        .usb_recovery_inhibited_count = s_usb_recovery_inhibited_count,
        .usb_recovery_inhibit_reason = s_usb_recovery_inhibit_reason,
        .output_count = s_output_count,
        .feature_get_count = s_feature_get_count,
        .uac_out_xfer_success = s_uac_out_xfer_success,
        .uac_out_xfer_errors = s_uac_out_xfer_errors,
        .uac_out_rearm_failures = s_uac_out_rearm_failures,
        .uac_set_interface_count = s_uac_set_interface_count,
        .uac_mic_alt1_attempts = s_uac_mic_alt1_attempts,
        .uac_mic_alt1_rejects = s_uac_mic_alt1_rejects,
        .uac_last_xfer_bytes = s_uac_last_xfer_bytes,
        .uac_last_xfer_result = s_uac_last_xfer_result,
        .control_task_stack_high_watermark_bytes =
            s_control_task_handle ?
                uxTaskGetStackHighWaterMark(s_control_task_handle) : 0,
        .input_task_stack_high_watermark_bytes =
            s_input_task_handle ?
                uxTaskGetStackHighWaterMark(s_input_task_handle) : 0,
        .report_last_gap_us = s_report_last_gap_us,
        .report_max_gap_us = s_report_max_gap_us,
        .last_report_us = s_last_report_us,
        .first_report_submit_failure_us = s_first_report_submit_failure_us,
        .last_report_submit_failure_us = s_last_report_submit_failure_us,
        .last_configuration_reset_us = s_last_configuration_reset_us,
        .last_hid_endpoint_kick_us = s_last_hid_endpoint_kick_us,
        .last_usb_recovery_us = s_last_usb_recovery_us,
        .last_usb_recovery_inhibited_us =
            s_last_usb_recovery_inhibited_us,
        .uac_last_xfer_us = s_uac_last_xfer_us,
        .last_output_us = s_last_output_us,
        .last_usb_event_us = s_last_usb_event_us,
    };
}

void dualsense_runtime_uac1_note_out_xfer(int32_t result,
                                         uint32_t bytes,
                                         bool rearm_ok)
{
    s_uac_last_xfer_result = result;
    s_uac_last_xfer_bytes = bytes;
    s_uac_last_xfer_us = esp_timer_get_time();
    if (result == XFER_RESULT_SUCCESS) {
        s_uac_out_xfer_success++;
    } else {
        s_uac_out_xfer_errors++;
    }
    if (!rearm_ok) {
        s_uac_out_rearm_failures++;
    }
}

void dualsense_runtime_uac1_note_set_interface(bool microphone,
                                              uint8_t alt,
                                              bool accepted)
{
    s_uac_set_interface_count++;
    if (microphone && alt == 1) {
        s_uac_mic_alt1_attempts++;
        if (!accepted) {
            s_uac_mic_alt1_rejects++;
        }
    }
}

void tud_hid_report_complete_cb(uint8_t instance,
                                uint8_t const *report,
                                uint16_t len)
{
    (void)report;
    (void)len;
    if (instance != 0) {
        return;
    }

    int64_t now_us = esp_timer_get_time();
    if (s_last_report_us > 0) {
        int64_t gap_us = now_us - s_last_report_us;
        if (gap_us > 0 && gap_us <= UINT32_MAX) {
            s_report_last_gap_us = (uint32_t)gap_us;
            if (s_report_last_gap_us > s_report_max_gap_us) {
                s_report_max_gap_us = s_report_last_gap_us;
            }
        }
    }
    s_last_report_us = now_us;
    s_report_completed_count++;
    s_hid_endpoint_kicks_since_report = 0;
    s_report_submit_failure_streak = 0;
    s_first_report_submit_failure_us = 0;
}

void tud_hid_report_failed_cb(uint8_t instance,
                              hid_report_type_t report_type,
                              uint8_t const *report,
                              uint16_t xferred_bytes)
{
    (void)instance;
    (void)report;
    s_report_failed_count++;
    s_report_xfer_failed_count++;
    ESP_LOGW(TAG,
             "[DS5_HID_XFER] completed=false type=%u bytes=%u xfer_failures=%lu total_failures=%lu",
             (unsigned)report_type,
             (unsigned)xferred_bytes,
             (unsigned long)s_report_xfer_failed_count,
             (unsigned long)s_report_failed_count);
}

uint16_t tud_hid_get_report_cb(uint8_t instance,
                               uint8_t report_id,
                               hid_report_type_t report_type,
                               uint8_t *buffer,
                               uint16_t reqlen)
{
    (void)instance;
    if (!buffer || reqlen == 0) {
        return 0;
    }

    if (report_type == HID_REPORT_TYPE_INPUT &&
        (report_id == 0 || report_id == DUALSENSE_INPUT_REPORT_ID)) {
        uint8_t neutral[DUALSENSE_INPUT_PAYLOAD_SIZE];
        dualsense_report_make_neutral(neutral);
        uint16_t length = reqlen < sizeof(neutral) ? reqlen : sizeof(neutral);
        memcpy(buffer, neutral, length);
        return length;
    }

    if (report_type == HID_REPORT_TYPE_FEATURE) {
        size_t feature_size = dualsense_report_feature_size(report_id);
        if (feature_size == 0) {
            ESP_LOGW(TAG, "[DS5_FEATURE] report_id=0x%02x supported=false", report_id);
            return 0;
        }
        uint16_t length = reqlen < feature_size ? reqlen : (uint16_t)feature_size;
        bool populated = dualsense_report_make_feature(report_id, buffer, length);
        s_feature_get_count++;
        if (s_feature_get_count <= 8 || (s_feature_get_count % 256) == 0) {
            ESP_LOGI(TAG,
                     "[DS5_FEATURE] report_id=0x%02x len=%u populated=%s count=%lu",
                     report_id,
                     (unsigned)length,
                     populated ? "true" : "false",
                     (unsigned long)s_feature_get_count);
        }
        return length;
    }

    memset(buffer, 0, reqlen);
    return reqlen;
}

void tud_hid_set_report_cb(uint8_t instance,
                           uint8_t report_id,
                           hid_report_type_t report_type,
                           uint8_t const *buffer,
                           uint16_t bufsize)
{
    (void)instance;
    uint8_t effective_report_id = report_id;
    if (effective_report_id == 0 && buffer && bufsize > 0) {
        effective_report_id = buffer[0];
    }

    s_output_count++;
    s_last_output_us = esp_timer_get_time();
    bool rumble_handled = pro2_rumble_backend_handle_dualsense_output(
        report_id,
        buffer,
        bufsize);
    if (s_output_count <= 8 || (s_output_count % 250) == 0) {
        ESP_LOGI(TAG,
                 "[DS5_OUTPUT] report_id=0x%02x effective_report_id=0x%02x type=%u len=%u count=%lu rumble_handled=%s",
                 report_id,
                 effective_report_id,
                 (unsigned)report_type,
                 (unsigned)bufsize,
                 (unsigned long)s_output_count,
                 rumble_handled ? "true" : "false");
    }
}

static void control_task(void *arg)
{
    (void)arg;
    char line[V55_CONTROL_LINE_MAX];
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
                    ESP_LOGW(TAG, "[V55_CONTROL] line too long; discarded");
                    printf("{\"ok\":false,\"cmd\":\"serial\",\"error\":\"command line too long\"}\n");
                    overflow = false;
                    line_len = 0;
                    continue;
                }
                if (line_len > 0) {
                    line[line_len] = 0;
                    v55_control_protocol_handle_line(line, reply, sizeof(reply));
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

static void note_usb_recovery_inhibited(uint32_t reason, int64_t now_us)
{
    if (s_usb_recovery_inhibit_reason == reason &&
        s_last_usb_recovery_inhibited_us > 0 &&
        now_us - s_last_usb_recovery_inhibited_us <
            DS5_USB_RECOVERY_INHIBIT_LOG_US) {
        return;
    }
    s_usb_recovery_inhibit_reason = reason;
    s_usb_recovery_inhibited_count++;
    s_last_usb_recovery_inhibited_us = now_us;
    ESP_LOGW(TAG,
             "[DS5_USB_RECOVERY] inhibited=true reason=%s count=%lu",
             reason == DS5_USB_RECOVERY_INHIBIT_AUDIO_STREAMING
                 ? "audio_streaming"
                 : "reenumeration_grace",
             (unsigned long)s_usb_recovery_inhibited_count);
}

static void hid_endpoint_kick_deferred(void *arg)
{
    (void)arg;
    if (!s_hid_endpoint_kick_pending) {
        return;
    }

    bool ready = s_mounted && s_usb_configuration_ready &&
                 !s_suspended && !s_usb_recovering && tud_ready();
    bool activated = false;
    int64_t now_us = esp_timer_get_time();
    if (ready) {
        s_hid_endpoint_kick_count++;
        s_hid_endpoint_kicks_since_report++;
        s_last_hid_endpoint_kick_us = now_us;
        activated = usbd_edpt_iso_activate(
            0, &s_hid_in_endpoint_descriptor);
    }

    s_hid_endpoint_kick_pending = false;
    ESP_LOGW(TAG,
             "[DS5_HID_ENDPOINT] kick=true activated=%s ready=%s kicks=%lu since_report=%lu report_age_ms=%lld",
             activated ? "true" : "false",
             ready ? "true" : "false",
             (unsigned long)s_hid_endpoint_kick_count,
             (unsigned long)s_hid_endpoint_kicks_since_report,
             (long long)(s_last_report_us > 0
                             ? (now_us - s_last_report_us) / 1000
                             : -1));
}

static bool request_hid_endpoint_kick(int64_t now_us)
{
    if (s_hid_endpoint_kick_pending || s_usb_recovering) {
        return false;
    }
    if (s_last_hid_endpoint_kick_us > 0 &&
        now_us - s_last_hid_endpoint_kick_us <
            DS5_HID_ENDPOINT_KICK_COOLDOWN_US) {
        return false;
    }

    s_hid_endpoint_kick_pending = true;
    usbd_defer_func(hid_endpoint_kick_deferred, NULL, false);
    return true;
}

static bool usb_hid_recovery_allowed(int64_t now_us, bool emergency)
{
#if DS5_ENABLE_UAC1_AUDIO || DS5_ENABLE_UAC2_AUDIO
    dualsense_haptic_audio_features_t audio;
    if (dualsense_haptic_audio_snapshot(&audio) && audio.streaming) {
        if (emergency) {
            s_usb_recovery_inhibit_reason =
                DS5_USB_RECOVERY_INHIBIT_NONE;
            return true;
        }
        note_usb_recovery_inhibited(
            DS5_USB_RECOVERY_INHIBIT_AUDIO_STREAMING, now_us);
        return false;
    }
#endif

    int64_t last_transition_us = s_last_configuration_reset_us;
    if (s_last_usb_event_us > last_transition_us) {
        last_transition_us = s_last_usb_event_us;
    }
    if (last_transition_us > 0 &&
        now_us - last_transition_us < DS5_USB_REENUMERATION_GRACE_US) {
        if (emergency) {
            s_usb_recovery_inhibit_reason =
                DS5_USB_RECOVERY_INHIBIT_NONE;
            return true;
        }
        note_usb_recovery_inhibited(
            DS5_USB_RECOVERY_INHIBIT_REENUMERATION, now_us);
        return false;
    }
    s_usb_recovery_inhibit_reason = DS5_USB_RECOVERY_INHIBIT_NONE;
    return true;
}

static void recover_stalled_usb_hid(int64_t now_us)
{
    if (s_usb_recovering) {
        return;
    }
    int64_t report_age_us = s_last_report_us > 0
                                ? now_us - s_last_report_us
                                : INT64_MAX;
    bool emergency =
        report_age_us >= DS5_HID_EMERGENCY_RECOVERY_US &&
        s_hid_endpoint_kicks_since_report >=
            DS5_HID_ENDPOINT_KICKS_BEFORE_REENUMERATION;
    if (!usb_hid_recovery_allowed(now_us, emergency)) {
        return;
    }
    if (s_last_usb_recovery_us > 0 &&
        now_us - s_last_usb_recovery_us < DS5_USB_RECOVERY_COOLDOWN_US) {
        return;
    }

    s_usb_recovering = true;
    s_usb_recovery_count++;
    s_last_usb_recovery_us = now_us;
    ESP_LOGW(TAG,
             "[DS5_USB_RECOVERY] reason=hid_in_stalled emergency=%s report_age_ms=%lld endpoint_kicks=%lu recoveries=%lu",
             emergency ? "true" : "false",
             (long long)(report_age_us == INT64_MAX
                             ? -1
                             : report_age_us / 1000),
             (unsigned long)s_hid_endpoint_kicks_since_report,
             (unsigned long)s_usb_recovery_count);

#if DS5_ENABLE_UAC1_AUDIO || DS5_ENABLE_UAC2_AUDIO
    dualsense_haptic_audio_set_streaming(false, 0);
#endif
    s_mounted = false;
    s_suspended = false;
    s_usb_configuration_ready = false;
    s_hid_endpoint_kick_pending = false;
    s_hid_endpoint_kicks_since_report = 0;
    s_report_submit_failure_streak = 0;
    s_first_report_submit_failure_us = 0;
    (void)tud_disconnect();
    vTaskDelay(pdMS_TO_TICKS(DS5_USB_RECONNECT_DELAY_MS));
    s_last_report_us = 0;
    (void)tud_connect();
    s_usb_recovering = false;
}

static void note_hid_submit_failure(int64_t now_us)
{
    s_report_failed_count++;
    s_report_submit_failed_count++;
    s_report_submit_failure_streak++;
    s_last_report_submit_failure_us = now_us;
    if (s_first_report_submit_failure_us == 0) {
        s_first_report_submit_failure_us = now_us;
    }

    if (s_report_submit_failure_streak <= 4 ||
        (s_report_submit_failure_streak % 64) == 0) {
        ESP_LOGW(TAG,
                 "[DS5_HID_SUBMIT] accepted=false streak=%lu submit_failures=%lu age_ms=%lld mounted=%s configuration_ready=%s tud_mounted=%s tud_ready=%s",
                 (unsigned long)s_report_submit_failure_streak,
                 (unsigned long)s_report_submit_failed_count,
                 (long long)((now_us - s_first_report_submit_failure_us) / 1000),
                 s_mounted ? "true" : "false",
                 s_usb_configuration_ready ? "true" : "false",
                 tud_mounted() ? "true" : "false",
                 tud_ready() ? "true" : "false");
    }
}

static void neutral_report_task(void *arg)
{
    (void)arg;
    uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE];
    int64_t next_input_log_us = 0;
    bool last_connected = false;
    uint32_t stable_update_count = 0;
    uint32_t last_seen_updates = 0;
    uint32_t last_sent_updates = 0;
    bool source_report_latched = false;
    bool neutral_report_latched = false;
    int64_t next_neutral_keepalive_us = 0;
    dualsense_input_debug_t debug = {0};
    TickType_t next_wake = xTaskGetTickCount();
    dualsense_report_mapper_init();

    while (true) {
        int64_t now_us = esp_timer_get_time();
        switch2_state_t state;
        uint32_t updates = 0;
        int64_t age_us = INT64_MAX;
        bool live = pro2_input_backend_get_live(&state, &updates, &age_us);
        bool connected = strcmp(pro2_input_backend_state(), "connected") == 0;
        bool live_recent = live && connected && age_us <= PRO2_INPUT_STALE_US;
        if (!live_recent) {
            stable_update_count = 0;
            last_seen_updates = updates;
        } else if (updates != last_seen_updates) {
            last_seen_updates = updates;
            if (stable_update_count < PRO2_INPUT_WARMUP_UPDATES) {
                stable_update_count++;
            }
        }
        bool using_pro2 = live_recent && stable_update_count >= PRO2_INPUT_WARMUP_UPDATES;
        bool should_send = false;
        if (using_pro2) {
            should_send = !source_report_latched || updates != last_sent_updates;
            neutral_report_latched = false;
        } else {
            source_report_latched = false;
            should_send = !neutral_report_latched ||
                          now_us >= next_neutral_keepalive_us;
        }
        if (connected != last_connected) {
            ESP_LOGI(TAG,
                     "[PRO2_INPUT] connected=%s state=%s",
                     connected ? "true" : "false",
                     pro2_input_backend_state());
            last_connected = connected;
        }

        if (s_mounted && s_usb_configuration_ready &&
            !s_suspended && !s_usb_recovering &&
            !s_hid_endpoint_kick_pending &&
            should_send && tud_hid_n_ready(0)) {
            if (using_pro2) {
                dualsense_report_mapper_from_pro2(&state, report, &debug);
            } else {
                dualsense_report_mapper_neutral(report);
                memset(&debug, 0, sizeof(debug));
            }
            bool sent = tud_hid_n_report(0,
                                         DUALSENSE_INPUT_REPORT_ID,
                                         report,
                                         sizeof(report));
            if (sent) {
                s_report_count++;
                s_report_submit_failure_streak = 0;
                s_first_report_submit_failure_us = 0;
                if (using_pro2) {
                    last_sent_updates = updates;
                    source_report_latched = true;
                } else {
                    neutral_report_latched = true;
                    next_neutral_keepalive_us =
                        now_us + DS5_NEUTRAL_KEEPALIVE_US;
                }
                if (s_report_count == 1 || (s_report_count % 2500) == 0) {
                    ESP_LOGI(TAG,
                             "[DS5_REPORT] source=%s cadence=%s sent=true report_id=0x%02x len=%u count=%lu updates=%lu",
                             using_pro2 ? "pro2" : "neutral",
                             using_pro2 ? "ble_source_updates" : "20hz_neutral_keepalive",
                             DUALSENSE_INPUT_REPORT_ID,
                             (unsigned)sizeof(report),
                             (unsigned long)s_report_count,
                             (unsigned long)updates);
                }
            } else {
                note_hid_submit_failure(now_us);
                if (s_report_submit_failure_streak >=
                        DS5_HID_ENDPOINT_KICK_SUBMIT_FAILURE_MIN_COUNT &&
                    now_us - s_first_report_submit_failure_us >=
                        DS5_HID_ENDPOINT_KICK_SUBMIT_FAILURE_US) {
                    (void)request_hid_endpoint_kick(now_us);
                }
                if (s_last_report_us > 0 &&
                    now_us - s_last_report_us >=
                        DS5_HID_EMERGENCY_RECOVERY_US) {
                    recover_stalled_usb_hid(now_us);
                    next_wake = xTaskGetTickCount();
                }
            }
        } else if (should_send &&
                   s_mounted && s_usb_configuration_ready &&
                   !s_suspended && !s_usb_recovering &&
                   !s_hid_endpoint_kick_pending) {
            s_report_not_ready_count++;
            if (s_report_count > 0 &&
                s_last_report_us > 0 &&
                now_us - s_last_report_us >
                    DS5_HID_ENDPOINT_KICK_STALL_US) {
                (void)request_hid_endpoint_kick(now_us);
            }
            if (s_report_count > 0 &&
                s_last_report_us > 0 &&
                now_us - s_last_report_us >=
                    DS5_HID_EMERGENCY_RECOVERY_US) {
                recover_stalled_usb_hid(now_us);
                next_wake = xTaskGetTickCount();
            }
        }

        if (now_us >= next_input_log_us) {
            if (using_pro2) {
                ESP_LOGI(TAG,
                         "[DS5_INPUT_MAP] buttons=0x%04x hat=%u raw12=(%u,%u,%u,%u) ds5=(%u,%u,%u,%u) l2=%u r2=%u updates=%lu age_ms=%lld",
                         debug.buttons,
                         debug.hat,
                         (unsigned)debug.raw_lx,
                         (unsigned)debug.raw_ly,
                         (unsigned)debug.raw_rx,
                         (unsigned)debug.raw_ry,
                         debug.lx,
                         debug.ly,
                         debug.rx,
                         debug.ry,
                         debug.l2,
                         debug.r2,
                         (unsigned long)updates,
                         (long long)(age_us / 1000));
                ESP_LOGI(TAG,
                         "[DS5_INPUT_MAP] gyro=%d,%d,%d accel=%d,%d,%d motion_valid=%s",
                         debug.gyro[0],
                         debug.gyro[1],
                         debug.gyro[2],
                         debug.accel[0],
                         debug.accel[1],
                         debug.accel[2],
                         debug.motion_valid ? "true" : "false");
            } else {
                ESP_LOGI(TAG,
                         "[DS5_INPUT] source=neutral reason=%s ble_state=%s updates=%lu age_ms=%lld warmup=%lu/%u",
                         connected ? (live_recent ? "warming_pro2_input" : "stale_pro2_input") : "no_pro2",
                         pro2_input_backend_state(),
                         (unsigned long)updates,
                         (long long)(age_us == INT64_MAX ? -1 : age_us / 1000),
                         (unsigned long)stable_update_count,
                         (unsigned)PRO2_INPUT_WARMUP_UPDATES);
            }
            next_input_log_us = now_us + 5000000LL;
        }

        xTaskDelayUntil(&next_wake, pdMS_TO_TICKS(DS5_SOURCE_POLL_MS));
    }
}

void app_main(void)
{
    ESP_LOGI(TAG, "[DS5_IDENTITY] enabled=true mode=dualsense_experimental profile=%s",
             DS5_PROFILE_NAME);
    ESP_LOGI(TAG,
             "[DS5_INPUT_CADENCE] policy=ble_source_updates latest_state=true historical_replay=false neutral_keepalive_hz=20");
    ESP_LOGI(TAG,
             "[DS5_IDENTITY] vid=0x054c pid=0x%04x product=%s",
             DS5_USB_PID,
             DS5_USB_PRODUCT);
    ESP_LOGI(TAG,
             "[DS5_IDENTITY] audio=%s ble_input=true rumble_compat=true raw02_forwarding=true",
             DS5_ENABLE_UAC1_CONTROL_ONLY ? "uac1_control_only" :
             (DS5_ENABLE_UAC1_STREAMING_ALT0 ? "uac1_streaming_alt0_only" :
             (DS5_ENABLE_UAC1_AUDIO ?
                  (DS5_AUDIO_CHANNELS == 4 ? "uac1_4ch_ds5like" : "uac1_2ch_fallback") :
             (DS5_ENABLE_UAC2_AUDIO ? "uac2_experimental" : "false"))));
    ESP_LOGI(TAG,
             "[DS5_DESCRIPTOR_STAGE] uac1_control_only=%s uac1_streaming_alt0_only=%s",
             DS5_ENABLE_UAC1_CONTROL_ONLY ? "true" : "false",
             DS5_ENABLE_UAC1_STREAMING_ALT0 ? "true" : "false");
    ESP_LOGI(TAG,
             "[DS5_AUDIO_PROFILE] enabled=%s uac1=%s uac2=%s channels=%u sample_rate=48000 bits=16",
             DS5_ENABLE_USB_AUDIO ? "true" : "false",
             DS5_ENABLE_UAC1_AUDIO ? "true" : "false",
             DS5_ENABLE_UAC2_AUDIO ? "true" : "false",
             (unsigned)DS5_AUDIO_CHANNELS);

    esp_err_t nvs_err = nvs_flash_init();
    if (nvs_err == ESP_ERR_NVS_NO_FREE_PAGES ||
        nvs_err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        nvs_err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(nvs_err);

    const tinyusb_config_t tusb_config = {
        .device_descriptor = dualsense_usb_device_descriptor(),
        .string_descriptor = dualsense_usb_string_descriptors(),
        .string_descriptor_count = dualsense_usb_string_descriptor_count(),
        .external_phy = false,
        .configuration_descriptor = dualsense_usb_configuration_descriptor(),
        .self_powered = false,
        .vbus_monitor_io = 0,
    };

#if DS5_ENABLE_UAC1_AUDIO || DS5_ENABLE_UAC2_AUDIO
    dualsense_haptic_audio_init();
#endif
    ESP_ERROR_CHECK(tinyusb_driver_install(&tusb_config));
    pro2_input_backend_init();
    pro2_rumble_backend_init();
    v55_control_protocol_init();
    ESP_ERROR_CHECK(xTaskCreate(control_task,
                                "v55_control",
                                6144,
                                NULL,
                                4,
                                &s_control_task_handle) == pdPASS ?
                                    ESP_OK : ESP_FAIL);
    ESP_ERROR_CHECK(xTaskCreate(neutral_report_task,
                                "ds5_input",
                                4096,
                                NULL,
                                6,
                                &s_input_task_handle) == pdPASS ?
                                    ESP_OK : ESP_FAIL);
}


