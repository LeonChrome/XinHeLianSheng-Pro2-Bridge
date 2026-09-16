#pragma once

#include <stdbool.h>
#include <stdint.h>

#define GAMEPAD_AXIS_PRO2_FULL_SCALE_RANGE 1600u

uint16_t gamepad_axis_normalize_12bit(uint16_t value,
                                      uint16_t center,
                                      uint16_t deadzone,
                                      uint16_t physical_full_scale);
uint8_t gamepad_axis_12bit_to_u8(uint16_t value, bool invert);
int16_t gamepad_axis_12bit_to_i16(uint16_t value, bool invert);
void gamepad_axis_pack_12bit_pair(uint8_t *out,
                                  uint16_t x,
                                  uint16_t y);
