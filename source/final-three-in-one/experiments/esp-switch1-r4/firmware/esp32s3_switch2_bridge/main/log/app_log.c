#include <stdbool.h>
#include "esp_log.h"
#include "app_log.h"

static bool s_debug;

void app_log_init(void)
{
    s_debug = false;
    esp_log_level_set("*", ESP_LOG_INFO);
    esp_log_level_set("NimBLE", ESP_LOG_WARN);
}

void app_log_set_debug(bool enabled)
{
    s_debug = enabled;
    esp_log_level_set("*", enabled ? ESP_LOG_DEBUG : ESP_LOG_INFO);
    esp_log_level_set("NimBLE", enabled ? ESP_LOG_DEBUG : ESP_LOG_WARN);
}

bool app_log_debug_enabled(void)
{
    return s_debug;
}
