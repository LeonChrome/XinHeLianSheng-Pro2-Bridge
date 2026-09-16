#include "pro2_input_backend.h"

#include <string.h>

#include "app_log.h"
#include "ble_central.h"
#include "device_config.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "v5.5_pro2";
static bool s_seen_connected;

#define PRO2_NOTIFY_START_GRACE_US 8000000LL
#define PRO2_NOTIFY_STALE_RECOVERY_US 1000000LL
#define PRO2_STALE_RECOVERY_COOLDOWN_US 5000000LL

static void reconnect_watchdog_task(void *arg)
{
    (void)arg;
    vTaskDelay(pdMS_TO_TICKS(500));

    uint32_t attempt = 0;
    int64_t last_stale_recovery_us = 0;
    while (true) {
        const char *state = ble_central_state_string();
        if (strcmp(state, "connected") == 0) {
            s_seen_connected = true;

            ble_central_conn_metrics_t metrics;
            ble_central_get_conn_metrics(&metrics);
            int64_t now_us = esp_timer_get_time();
            int64_t notify_age_us =
                metrics.last_parsed_notify_us > 0
                    ? now_us - metrics.last_parsed_notify_us
                    : INT64_MAX;
            int64_t connect_age_us =
                metrics.last_connect_us > 0
                    ? now_us - metrics.last_connect_us
                    : 0;
            bool notify_started_for_connection =
                metrics.last_connect_us > 0 &&
                metrics.last_parsed_notify_us >= metrics.last_connect_us;
            bool notify_stale =
                notify_started_for_connection &&
                notify_age_us > PRO2_NOTIFY_STALE_RECOVERY_US;
            bool notify_never_started =
                !notify_started_for_connection &&
                connect_age_us > PRO2_NOTIFY_START_GRACE_US;
            bool cooldown_done =
                last_stale_recovery_us == 0 ||
                now_us - last_stale_recovery_us >
                    PRO2_STALE_RECOVERY_COOLDOWN_US;

            if ((notify_stale || notify_never_started) && cooldown_done) {
                last_stale_recovery_us = now_us;
                esp_err_t err = ble_central_recover_stale_link();
                ESP_LOGW(TAG,
                         "[PRO2_INPUT] stale_link_recovery reason=%s notify_age_ms=%lld connect_age_ms=%lld started=%s err=%s",
                         notify_stale ? "notify_stale" : "notify_never_started",
                         (long long)(notify_age_us == INT64_MAX
                                         ? -1
                                         : notify_age_us / 1000),
                         (long long)(connect_age_us / 1000),
                         err == ESP_OK ? "true" : "false",
                         esp_err_to_name(err));
            }
        }
        if (strcmp(state, "idle") == 0) {
            attempt++;
            if (s_seen_connected) {
                ble_central_start_auto_reconnect();
                ESP_LOGI(TAG,
                         "[PRO2_INPUT] wake_reconnect_wait attempt=%lu",
                         (unsigned long)attempt);
            } else {
                esp_err_t err = ble_central_reconnect_saved_or_scan();
                if (err != ESP_OK) {
                    ble_central_start_auto_reconnect();
                }
                ESP_LOGI(TAG,
                         "[PRO2_INPUT] reconnect_attempt=%lu started=%s err=%s",
                         (unsigned long)attempt,
                         err == ESP_OK ? "true" : "false",
                         esp_err_to_name(err));
            }
        }

        vTaskDelay(pdMS_TO_TICKS(1500));
    }
}

void pro2_input_backend_init(void)
{
    app_log_init();
    device_config_init();
    switch2_state_init();
    ble_central_init();
    if (device_config_get_ble_autoconnect()) {
        esp_err_t err = ble_central_reconnect_saved_or_scan();
        if (err != ESP_OK) {
            ble_central_start_auto_reconnect();
        }
        ESP_LOGI(TAG,
                 "[PRO2_INPUT] initial_reconnect started=%s err=%s",
                 err == ESP_OK ? "true" : "false",
                 esp_err_to_name(err));
    }

    BaseType_t created = xTaskCreate(reconnect_watchdog_task,
                                     "pro2_reconnect",
                                     4096,
                                     NULL,
                                     4,
                                     NULL);
    ESP_ERROR_CHECK(created == pdPASS ? ESP_OK : ESP_FAIL);
}

bool pro2_input_backend_get_live(switch2_state_t *state,
                                 uint32_t *updates,
                                 int64_t *age_us)
{
    return switch2_state_get_live(state, updates, age_us);
}

const char *pro2_input_backend_state(void)
{
    return ble_central_state_string();
}
