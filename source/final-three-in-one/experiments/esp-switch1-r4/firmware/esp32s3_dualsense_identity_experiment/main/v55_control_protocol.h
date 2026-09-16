#pragma once

#include "esp_err.h"

void v55_control_protocol_init(void);
esp_err_t v55_control_protocol_handle_line(const char *line, char *reply, int reply_len);
