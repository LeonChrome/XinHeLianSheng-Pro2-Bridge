#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef struct {
    uint8_t weak;
    uint8_t strong;
    uint16_t duration_ms;
    uint8_t left_gain_percent;
    uint8_t right_gain_percent;
    bool stop;
} normalized_rumble_t;

void normalized_rumble_reset(normalized_rumble_t *rumble);
void normalized_rumble_set_balanced(normalized_rumble_t *rumble,
                                    uint8_t weak,
                                    uint8_t strong,
                                    uint16_t duration_ms);
void normalized_rumble_set_balance(normalized_rumble_t *rumble,
                                   uint8_t left_gain_percent,
                                   uint8_t right_gain_percent);
bool normalized_rumble_active(const normalized_rumble_t *rumble);
void normalized_rumble_from_dualsense_motors(uint8_t right_light,
                                             uint8_t left_heavy,
                                             uint16_t duration_ms,
                                             normalized_rumble_t *out);
void normalized_rumble_from_switch1_hd(const uint8_t left[4],
                                       const uint8_t right[4],
                                       uint16_t duration_ms,
                                       normalized_rumble_t *out);
void normalized_rumble_build_zero_pro2(uint8_t out[5]);
void normalized_rumble_build_pro2_pair(const normalized_rumble_t *rumble,
                                       uint16_t max_amplitude,
                                       uint8_t left[5],
                                       uint8_t right[5]);
