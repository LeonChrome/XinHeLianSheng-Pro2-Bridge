#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/portmacro.h"
#include "esp_timer.h"
#include "switch2_state.h"

#define CENTER_12BIT 2048

static portMUX_TYPE s_live_lock = portMUX_INITIALIZER_UNLOCKED;
static switch2_state_t s_live_state;
static bool s_live_valid;
static uint32_t s_live_updates;
static int64_t s_live_updated_us;
static uint32_t s_live_actual_millihz;
static uint32_t s_live_last_gap_us;
static uint32_t s_live_max_gap_us;
static uint32_t s_live_window_updates;
static uint32_t s_live_window_max_gap_us;
static int64_t s_live_last_update_us;
static int64_t s_live_window_start_us;

void switch2_state_init(void)
{
    switch2_state_clear_live();
}

void switch2_state_reset(switch2_state_t *state)
{
    memset(state, 0, sizeof(*state));
    state->lx = CENTER_12BIT;
    state->ly = CENTER_12BIT;
    state->rx = CENTER_12BIT;
    state->ry = CENTER_12BIT;
}

static void reset_controls_preserving_motion(switch2_state_t *state)
{
    bool motion_valid = state->motion_valid;
    uint8_t motion[SWITCH2_MOTION_BLOCK_SIZE];

    if (motion_valid) {
        memcpy(motion, state->motion, sizeof(motion));
    }

    switch2_state_reset(state);

    if (motion_valid) {
        memcpy(state->motion, motion, sizeof(motion));
        state->motion_valid = true;
    }
}

static int16_t read_i16_le(const uint8_t *src)
{
    return (int16_t)((uint16_t)src[0] | ((uint16_t)src[1] << 8));
}

static void write_i16_le(uint8_t *dst, int16_t value)
{
    dst[0] = (uint8_t)((uint16_t)value & 0xff);
    dst[1] = (uint8_t)(((uint16_t)value >> 8) & 0xff);
}

void switch2_state_set_button(switch2_state_t *state, switch2_button_t button, bool pressed)
{
    if (button >= SWITCH2_BUTTON_COUNT) {
        return;
    }
    uint32_t mask = 1u << button;
    if (pressed) {
        state->buttons |= mask;
    } else {
        state->buttons &= ~mask;
    }
}

void switch2_state_set_motion_raw(switch2_state_t *state, const uint8_t *data, uint8_t len)
{
    if (!state || !data || len < SWITCH2_MOTION_BLOCK_SIZE) {
        return;
    }
    memcpy(state->motion, data, SWITCH2_MOTION_BLOCK_SIZE);
    state->motion_valid = true;
}

void switch2_state_set_motion_sample(switch2_state_t *state, const uint8_t *data, uint8_t len)
{
    if (!state || !data || len < SWITCH2_MOTION_SAMPLE_SIZE) {
        return;
    }

    for (uint8_t offset = 0; offset < SWITCH2_MOTION_BLOCK_SIZE; offset += SWITCH2_MOTION_SAMPLE_SIZE) {
        memcpy(state->motion + offset, data, SWITCH2_MOTION_SAMPLE_SIZE);
    }
    state->motion_valid = true;
}

bool switch2_state_get_button(const switch2_state_t *state, switch2_button_t button)
{
    if (button >= SWITCH2_BUTTON_COUNT) {
        return false;
    }
    return (state->buttons & (1u << button)) != 0;
}

void switch2_state_store_live(const switch2_state_t *state)
{
    if (!state) {
        return;
    }

    portENTER_CRITICAL(&s_live_lock);
    int64_t now_us = esp_timer_get_time();
    if (s_live_window_start_us == 0) {
        s_live_window_start_us = now_us;
    }
    if (s_live_last_update_us > 0 && now_us > s_live_last_update_us) {
        uint32_t gap_us = (uint32_t)(now_us - s_live_last_update_us);
        s_live_last_gap_us = gap_us;
        if (gap_us > s_live_window_max_gap_us) {
            s_live_window_max_gap_us = gap_us;
        }
    }
    s_live_last_update_us = now_us;

    s_live_state = *state;
    s_live_valid = true;
    s_live_updates++;
    s_live_window_updates++;
    s_live_updated_us = now_us;

    int64_t elapsed_us = now_us - s_live_window_start_us;
    if (elapsed_us >= 1000000LL) {
        s_live_actual_millihz = (uint32_t)(((uint64_t)s_live_window_updates * 1000000000ULL +
                                            (uint64_t)(elapsed_us / 2)) /
                                           (uint64_t)elapsed_us);
        s_live_max_gap_us = s_live_window_max_gap_us;
        s_live_window_updates = 0;
        s_live_window_max_gap_us = 0;
        s_live_window_start_us = now_us;
    }
    portEXIT_CRITICAL(&s_live_lock);
}

bool switch2_state_get_live(switch2_state_t *out_state, uint32_t *out_updates, int64_t *out_age_us)
{
    bool valid;
    switch2_state_t snapshot;
    uint32_t updates;
    int64_t updated_us;

    portENTER_CRITICAL(&s_live_lock);
    valid = s_live_valid;
    snapshot = s_live_state;
    updates = s_live_updates;
    updated_us = s_live_updated_us;
    portEXIT_CRITICAL(&s_live_lock);

    if (out_state) {
        if (valid) {
            *out_state = snapshot;
        } else {
            switch2_state_reset(out_state);
        }
    }
    if (out_updates) {
        *out_updates = updates;
    }
    if (out_age_us) {
        *out_age_us = valid ? esp_timer_get_time() - updated_us : INT64_MAX;
    }
    return valid;
}

void switch2_state_get_live_stats(switch2_live_stats_t *out_stats)
{
    if (!out_stats) {
        return;
    }

    portENTER_CRITICAL(&s_live_lock);
    out_stats->updates_total = s_live_updates;
    out_stats->actual_millihz = s_live_actual_millihz;
    out_stats->last_gap_us = s_live_last_gap_us;
    out_stats->max_gap_us = s_live_max_gap_us;
    portEXIT_CRITICAL(&s_live_lock);
}

bool switch2_state_live_active(int64_t max_age_us)
{
    int64_t age_us = 0;
    return switch2_state_get_live(NULL, NULL, &age_us) && age_us <= max_age_us;
}

void switch2_state_clear_live(void)
{
    switch2_state_t neutral;
    switch2_state_reset(&neutral);

    portENTER_CRITICAL(&s_live_lock);
    s_live_state = neutral;
    s_live_valid = false;
    s_live_updates = 0;
    s_live_updated_us = 0;
    s_live_actual_millihz = 0;
    s_live_last_gap_us = 0;
    s_live_max_gap_us = 0;
    s_live_window_updates = 0;
    s_live_window_max_gap_us = 0;
    s_live_last_update_us = 0;
    s_live_window_start_us = 0;
    portEXIT_CRITICAL(&s_live_lock);
}

void switch2_state_update_from_legacy_bytes(switch2_state_t *state, uint8_t b2, uint8_t b3, uint8_t b4)
{
    reset_controls_preserving_motion(state);
    switch2_state_set_button(state, SWITCH2_BUTTON_B, (b2 & 0x01) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_A, (b2 & 0x02) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_Y, (b2 & 0x04) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_X, (b2 & 0x08) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_R, (b2 & 0x10) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_ZR, (b2 & 0x20) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_PLUS, (b2 & 0x40) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_RSTICK, (b2 & 0x80) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_DDOWN, (b3 & 0x01) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_DRIGHT, (b3 & 0x02) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_DLEFT, (b3 & 0x04) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_DUP, (b3 & 0x08) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_L, (b3 & 0x10) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_ZL, (b3 & 0x20) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_MINUS, (b3 & 0x40) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_LSTICK, (b3 & 0x80) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_HOME, (b4 & 0x01) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_CAPTURE, (b4 & 0x02) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_GR, (b4 & 0x04) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_GL, (b4 & 0x08) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_C, (b4 & 0x10) != 0);
}

void switch2_state_update_from_fd2_buttons(switch2_state_t *state, uint32_t buttons)
{
    reset_controls_preserving_motion(state);
    switch2_state_set_button(state, SWITCH2_BUTTON_Y, (buttons & 0x00000001) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_X, (buttons & 0x00000002) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_B, (buttons & 0x00000004) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_A, (buttons & 0x00000008) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_R, (buttons & 0x00000040) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_ZR, (buttons & 0x00000080) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_MINUS, (buttons & 0x00000100) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_PLUS, (buttons & 0x00000200) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_RSTICK, (buttons & 0x00000400) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_LSTICK, (buttons & 0x00000800) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_HOME, (buttons & 0x00001000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_CAPTURE, (buttons & 0x00002000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_C, (buttons & 0x00004000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_DDOWN, (buttons & 0x00010000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_DUP, (buttons & 0x00020000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_DRIGHT, (buttons & 0x00040000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_DLEFT, (buttons & 0x00080000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_L, (buttons & 0x00400000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_ZL, (buttons & 0x00800000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_GR, (buttons & 0x01000000) != 0);
    switch2_state_set_button(state, SWITCH2_BUTTON_GL, (buttons & 0x02000000) != 0);
}

void switch2_state_to_internal(const switch2_state_t *src,
                               internal_gamepad_state_t *dst)
{
    internal_gamepad_state_reset(dst);
    if (!src || !dst) {
        return;
    }

    dst->connection = INTERNAL_GAMEPAD_CONNECTION_CONNECTED;
    dst->lx = internal_gamepad_state_clamp_axis(src->lx);
    dst->ly = internal_gamepad_state_clamp_axis(src->ly);
    dst->rx = internal_gamepad_state_clamp_axis(src->rx);
    dst->ry = internal_gamepad_state_clamp_axis(src->ry);
    internal_gamepad_state_apply_center_snap(dst);
    dst->l2 = switch2_state_get_button(src, SWITCH2_BUTTON_ZL) ? INTERNAL_GAMEPAD_TRIGGER_MAX : 0;
    dst->r2 = switch2_state_get_button(src, SWITCH2_BUTTON_ZR) ? INTERNAL_GAMEPAD_TRIGGER_MAX : 0;

    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_SOUTH,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_B));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_EAST,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_A));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_WEST,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_Y));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_NORTH,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_X));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_L1,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_L));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_R1,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_R));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_L2,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_ZL));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_R2,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_ZR));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_BACK,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_MINUS));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_START,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_PLUS));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_LSTICK,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_LSTICK));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_RSTICK,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_RSTICK));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_DPAD_DOWN,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_DDOWN));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_DPAD_RIGHT,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_DRIGHT));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_DPAD_LEFT,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_DLEFT));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_DPAD_UP,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_DUP));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_HOME,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_HOME));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_CAPTURE,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_CAPTURE));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_PADDLE_RIGHT,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_GR));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_PADDLE_LEFT,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_GL));
    internal_gamepad_state_set_button(dst, INTERNAL_GAMEPAD_BUTTON_AUX,
                                      switch2_state_get_button(src, SWITCH2_BUTTON_C));

    if (src->motion_valid) {
        const uint8_t *latest = src->motion + (SWITCH2_MOTION_BLOCK_SIZE - SWITCH2_MOTION_SAMPLE_SIZE);
        dst->accel_valid = true;
        dst->gyro_valid = true;
        dst->accel[0] = read_i16_le(latest + 0);
        dst->accel[1] = read_i16_le(latest + 2);
        dst->accel[2] = read_i16_le(latest + 4);
        dst->gyro[0] = read_i16_le(latest + 6);
        dst->gyro[1] = read_i16_le(latest + 8);
        dst->gyro[2] = read_i16_le(latest + 10);
    }
}

void switch2_state_from_internal(const internal_gamepad_state_t *src,
                                 switch2_state_t *dst)
{
    switch2_state_reset(dst);
    if (!src || !dst) {
        return;
    }

    dst->lx = internal_gamepad_state_snap_axis_center(src->lx);
    dst->ly = internal_gamepad_state_snap_axis_center(src->ly);
    dst->rx = internal_gamepad_state_snap_axis_center(src->rx);
    dst->ry = internal_gamepad_state_snap_axis_center(src->ry);

    switch2_state_set_button(dst, SWITCH2_BUTTON_B,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_SOUTH));
    switch2_state_set_button(dst, SWITCH2_BUTTON_A,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_EAST));
    switch2_state_set_button(dst, SWITCH2_BUTTON_Y,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_WEST));
    switch2_state_set_button(dst, SWITCH2_BUTTON_X,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_NORTH));
    switch2_state_set_button(dst, SWITCH2_BUTTON_L,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_L1));
    switch2_state_set_button(dst, SWITCH2_BUTTON_R,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_R1));
    switch2_state_set_button(dst, SWITCH2_BUTTON_ZL,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_L2) || src->l2 > 0);
    switch2_state_set_button(dst, SWITCH2_BUTTON_ZR,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_R2) || src->r2 > 0);
    switch2_state_set_button(dst, SWITCH2_BUTTON_MINUS,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_BACK));
    switch2_state_set_button(dst, SWITCH2_BUTTON_PLUS,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_START));
    switch2_state_set_button(dst, SWITCH2_BUTTON_LSTICK,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_LSTICK));
    switch2_state_set_button(dst, SWITCH2_BUTTON_RSTICK,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_RSTICK));
    switch2_state_set_button(dst, SWITCH2_BUTTON_DDOWN,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_DPAD_DOWN));
    switch2_state_set_button(dst, SWITCH2_BUTTON_DRIGHT,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_DPAD_RIGHT));
    switch2_state_set_button(dst, SWITCH2_BUTTON_DLEFT,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_DPAD_LEFT));
    switch2_state_set_button(dst, SWITCH2_BUTTON_DUP,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_DPAD_UP));
    switch2_state_set_button(dst, SWITCH2_BUTTON_HOME,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_HOME));
    switch2_state_set_button(dst, SWITCH2_BUTTON_CAPTURE,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_CAPTURE));
    switch2_state_set_button(dst, SWITCH2_BUTTON_GR,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_PADDLE_RIGHT));
    switch2_state_set_button(dst, SWITCH2_BUTTON_GL,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_PADDLE_LEFT));
    switch2_state_set_button(dst, SWITCH2_BUTTON_C,
                             internal_gamepad_state_get_button(src, INTERNAL_GAMEPAD_BUTTON_AUX));

    if (src->accel_valid || src->gyro_valid) {
        uint8_t sample[SWITCH2_MOTION_SAMPLE_SIZE] = {0};
        write_i16_le(sample + 0, src->accel_valid ? src->accel[0] : 0);
        write_i16_le(sample + 2, src->accel_valid ? src->accel[1] : 0);
        write_i16_le(sample + 4, src->accel_valid ? src->accel[2] : 0);
        write_i16_le(sample + 6, src->gyro_valid ? src->gyro[0] : 0);
        write_i16_le(sample + 8, src->gyro_valid ? src->gyro[1] : 0);
        write_i16_le(sample + 10, src->gyro_valid ? src->gyro[2] : 0);
        switch2_state_set_motion_sample(dst, sample, sizeof(sample));
    }
}
