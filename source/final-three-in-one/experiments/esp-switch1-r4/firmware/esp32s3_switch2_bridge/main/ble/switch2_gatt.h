#pragma once

#include <stdint.h>
#include "esp_err.h"
#include "switch2_state.h"

#define SWITCH2_NOTIFY_FD2_UUID "ab7de9be-89fe-49ad-828f-118f09df7fd2"
#define SWITCH2_NOTIFY_LEGACY_UUID "7492866c-ec3e-4619-8258-32755ffcc0f8"
#define SWITCH2_NOTIFY_506D_UUID "506d9f7d-4278-4e95-a549-326ba77657e0"
#define SWITCH2_NOTIFY_D3BD_UUID "d3bd69d2-841c-4241-ab15-f86f406d2a80"
#define SWITCH2_NOTIFY_FDE_UUID "ab7de9be-89fe-49ad-828f-118f09df7fde"
#define SWITCH2_ACK_UUID "c765a961-d9d8-4d36-a20a-5315b111836a"
#define SWITCH2_CMD_UUID "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005"
#define SWITCH2_RUMBLE_CC48_UUID "cc483f51-9258-427d-a939-630c31f72b05"

esp_err_t switch2_gatt_handle_notify(const char *uuid, const uint8_t *data, uint16_t len, switch2_state_t *out_state);
esp_err_t switch2_gatt_send_rumble_stub(const uint8_t *data, uint16_t len);
bool switch2_gatt_set_motion_source_offset(uint8_t offset);
uint8_t switch2_gatt_get_motion_source_offset(void);
void switch2_gatt_set_motion_full_only(bool enabled);
bool switch2_gatt_get_motion_full_only(void);
void switch2_gatt_reset_axis_calibration(void);
