#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "internal_gamepad_state.h"

#define SWITCH2_MOTION_SAMPLE_SIZE 12
#define SWITCH2_MOTION_BLOCK_SIZE 36

typedef enum {
    SWITCH2_BUTTON_B = 0,
    SWITCH2_BUTTON_A,
    SWITCH2_BUTTON_Y,
    SWITCH2_BUTTON_X,
    SWITCH2_BUTTON_R,
    SWITCH2_BUTTON_ZR,
    SWITCH2_BUTTON_PLUS,
    SWITCH2_BUTTON_RSTICK,
    SWITCH2_BUTTON_DDOWN,
    SWITCH2_BUTTON_DRIGHT,
    SWITCH2_BUTTON_DLEFT,
    SWITCH2_BUTTON_DUP,
    SWITCH2_BUTTON_L,
    SWITCH2_BUTTON_ZL,
    SWITCH2_BUTTON_MINUS,
    SWITCH2_BUTTON_LSTICK,
    SWITCH2_BUTTON_HOME,
    SWITCH2_BUTTON_CAPTURE,
    SWITCH2_BUTTON_GR,
    SWITCH2_BUTTON_GL,
    SWITCH2_BUTTON_C,
    SWITCH2_BUTTON_COUNT
} switch2_button_t;

typedef struct {
    uint32_t buttons;
    uint16_t lx;
    uint16_t ly;
    uint16_t rx;
    uint16_t ry;
    bool motion_valid;
    uint8_t motion[SWITCH2_MOTION_BLOCK_SIZE];
} switch2_state_t;

typedef struct {
    uint32_t updates_total;
    uint32_t actual_millihz;
    uint32_t last_gap_us;
    uint32_t max_gap_us;
} switch2_live_stats_t;

void switch2_state_init(void);
void switch2_state_reset(switch2_state_t *state);
void switch2_state_set_button(switch2_state_t *state, switch2_button_t button, bool pressed);
bool switch2_state_get_button(const switch2_state_t *state, switch2_button_t button);
void switch2_state_store_live(const switch2_state_t *state);
bool switch2_state_get_live(switch2_state_t *out_state, uint32_t *out_updates, int64_t *out_age_us);
void switch2_state_get_live_stats(switch2_live_stats_t *out_stats);
bool switch2_state_live_active(int64_t max_age_us);
void switch2_state_clear_live(void);
void switch2_state_set_motion_raw(switch2_state_t *state, const uint8_t *data, uint8_t len);
void switch2_state_set_motion_sample(switch2_state_t *state, const uint8_t *data, uint8_t len);
void switch2_state_update_from_legacy_bytes(switch2_state_t *state, uint8_t b2, uint8_t b3, uint8_t b4);
void switch2_state_update_from_fd2_buttons(switch2_state_t *state, uint32_t buttons);
void switch2_state_to_internal(const switch2_state_t *src,
                               internal_gamepad_state_t *dst);
void switch2_state_from_internal(const internal_gamepad_state_t *src,
                                 switch2_state_t *dst);
