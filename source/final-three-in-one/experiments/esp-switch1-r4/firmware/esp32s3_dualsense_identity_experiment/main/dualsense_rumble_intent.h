#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef struct {
    bool compatibility_selected;
    bool compatibility_v1;
    bool compatibility_v2;
    bool audio_haptics_allowed;
    bool ordinary_valid;
    bool ordinary_active;
    uint8_t valid_flag0;
    uint8_t valid_flag1;
    uint8_t valid_flag2;
    uint8_t right_light;
    uint8_t left_heavy;
} dualsense_rumble_intent_t;

bool dualsense_rumble_intent_parse(const uint8_t *payload,
                                   uint16_t payload_len,
                                   dualsense_rumble_intent_t *out);
