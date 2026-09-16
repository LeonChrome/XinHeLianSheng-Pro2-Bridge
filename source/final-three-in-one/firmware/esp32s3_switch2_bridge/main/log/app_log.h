#pragma once

#include <stdbool.h>
#include "esp_log.h"

#define APP_LOGI(tag, fmt, ...) ESP_LOGI(tag, "[LOG] " fmt, ##__VA_ARGS__)
#define APP_LOGW(tag, fmt, ...) ESP_LOGW(tag, "[LOG] " fmt, ##__VA_ARGS__)
#define APP_LOGE(tag, fmt, ...) ESP_LOGE(tag, "[LOG] " fmt, ##__VA_ARGS__)
#define APP_LOGD(tag, fmt, ...) ESP_LOGD(tag, "[LOG] " fmt, ##__VA_ARGS__)

void app_log_init(void);
void app_log_set_debug(bool enabled);
bool app_log_debug_enabled(void);
