#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"
#include "internal_gamepad_state.h"

typedef enum {
    XBOX_PADDLE_SIDE_LEFT = 0,
    XBOX_PADDLE_SIDE_RIGHT = 1,
    XBOX_PADDLE_SIDE_COUNT = 2,
} xbox_paddle_side_t;

typedef enum {
    XBOX_PADDLE_ACTION_HOLD = 0,
    XBOX_PADDLE_ACTION_TAP = 1,
    XBOX_PADDLE_ACTION_TURBO = 2,
} xbox_paddle_action_t;

typedef struct {
    bool enabled;
    xbox_paddle_action_t action;
    uint64_t target_mask;
    uint16_t tap_ms;
    uint16_t turbo_on_ms;
    uint16_t turbo_off_ms;
} xbox_paddle_binding_t;

void xbox_paddle_mapper_init(void);
void xbox_paddle_mapper_apply(const internal_gamepad_state_t *src,
                              internal_gamepad_state_t *dst);
void xbox_paddle_mapper_get(xbox_paddle_side_t side,
                            xbox_paddle_binding_t *out);
esp_err_t xbox_paddle_mapper_set(xbox_paddle_side_t side,
                                 const xbox_paddle_binding_t *binding);
esp_err_t xbox_paddle_mapper_reset(void);
bool xbox_paddle_mapper_parse_side(const char *text, xbox_paddle_side_t *out);
bool xbox_paddle_mapper_parse_action(const char *text,
                                     xbox_paddle_action_t *out);
bool xbox_paddle_mapper_parse_targets(const char *text, uint64_t *out_mask);
const char *xbox_paddle_mapper_side_string(xbox_paddle_side_t side);
const char *xbox_paddle_mapper_action_string(xbox_paddle_action_t action);
void xbox_paddle_mapper_format_targets(uint64_t mask, char *out, size_t out_len);
