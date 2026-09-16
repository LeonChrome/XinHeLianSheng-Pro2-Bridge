#include "internal_gamepad_state.h"

#include <string.h>

void internal_gamepad_state_reset(internal_gamepad_state_t *state)
{
    if (!state) {
        return;
    }

    memset(state, 0, sizeof(*state));
    state->lx = INTERNAL_GAMEPAD_AXIS_CENTER;
    state->ly = INTERNAL_GAMEPAD_AXIS_CENTER;
    state->rx = INTERNAL_GAMEPAD_AXIS_CENTER;
    state->ry = INTERNAL_GAMEPAD_AXIS_CENTER;
    state->battery_percent = INTERNAL_GAMEPAD_BATTERY_UNKNOWN;
    state->connection = INTERNAL_GAMEPAD_CONNECTION_DISCONNECTED;
}

void internal_gamepad_state_set_button(internal_gamepad_state_t *state,
                                       internal_gamepad_button_t button,
                                       bool pressed)
{
    if (!state || button >= INTERNAL_GAMEPAD_BUTTON_COUNT) {
        return;
    }

    uint64_t mask = 1ULL << (uint8_t)button;
    if (pressed) {
        state->buttons |= mask;
    } else {
        state->buttons &= ~mask;
    }
}

bool internal_gamepad_state_get_button(const internal_gamepad_state_t *state,
                                       internal_gamepad_button_t button)
{
    if (!state || button >= INTERNAL_GAMEPAD_BUTTON_COUNT) {
        return false;
    }
    return (state->buttons & (1ULL << (uint8_t)button)) != 0;
}

uint16_t internal_gamepad_state_clamp_axis(int32_t value)
{
    if (value < 0) {
        return 0;
    }
    if (value > INTERNAL_GAMEPAD_AXIS_MAX) {
        return INTERNAL_GAMEPAD_AXIS_MAX;
    }
    return (uint16_t)value;
}

uint16_t internal_gamepad_state_snap_axis_center(uint16_t value)
{
    uint16_t clamped = internal_gamepad_state_clamp_axis(value);
    int32_t delta = (int32_t)clamped - INTERNAL_GAMEPAD_AXIS_CENTER;
    if (delta < 0) {
        delta = -delta;
    }
    if (delta <= INTERNAL_GAMEPAD_AXIS_CENTER_DEADBAND) {
        return INTERNAL_GAMEPAD_AXIS_CENTER;
    }
    return clamped;
}

void internal_gamepad_state_apply_center_snap(internal_gamepad_state_t *state)
{
    if (!state) {
        return;
    }

    state->lx = internal_gamepad_state_snap_axis_center(state->lx);
    state->ly = internal_gamepad_state_snap_axis_center(state->ly);
    state->rx = internal_gamepad_state_snap_axis_center(state->rx);
    state->ry = internal_gamepad_state_snap_axis_center(state->ry);
}

uint16_t internal_gamepad_state_clamp_trigger(int32_t value)
{
    if (value < 0) {
        return 0;
    }
    if (value > INTERNAL_GAMEPAD_TRIGGER_MAX) {
        return INTERNAL_GAMEPAD_TRIGGER_MAX;
    }
    return (uint16_t)value;
}

const char *internal_gamepad_connection_string(
    internal_gamepad_connection_t connection)
{
    switch (connection) {
    case INTERNAL_GAMEPAD_CONNECTION_DISCONNECTED:
        return "disconnected";
    case INTERNAL_GAMEPAD_CONNECTION_CONNECTING:
        return "connecting";
    case INTERNAL_GAMEPAD_CONNECTION_CONNECTED:
        return "connected";
    case INTERNAL_GAMEPAD_CONNECTION_DEGRADED:
        return "degraded";
    default:
        return "unknown";
    }
}
