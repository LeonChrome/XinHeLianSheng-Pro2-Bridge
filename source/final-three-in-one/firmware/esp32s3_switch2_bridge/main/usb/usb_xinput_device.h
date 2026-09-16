#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"
#include "internal_gamepad_state.h"

void usb_xinput_device_init(void);
void usb_xinput_device_on_mount(void);
void usb_xinput_device_on_unmount(void);
bool usb_xinput_device_ready(void);
esp_err_t usb_xinput_device_send_report(const internal_gamepad_state_t *state);
void usb_xinput_device_poll_out(void);
uint32_t usb_xinput_device_out_count(void);
uint16_t usb_xinput_device_last_out_len(void);
uint8_t usb_xinput_device_last_left_motor(void);
uint8_t usb_xinput_device_last_right_motor(void);
