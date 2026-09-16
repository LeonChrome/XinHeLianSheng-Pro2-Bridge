#pragma once

#include <stdbool.h>
#include <stdint.h>

#define INTERNAL_GAMEPAD_AXIS_CENTER 2048
#define INTERNAL_GAMEPAD_AXIS_MAX 4095
#define INTERNAL_GAMEPAD_AXIS_CENTER_DEADBAND 64
#define INTERNAL_GAMEPAD_TRIGGER_MAX 4095
#define INTERNAL_GAMEPAD_BATTERY_UNKNOWN 255

typedef enum {
    INTERNAL_GAMEPAD_BUTTON_SOUTH = 0,
    INTERNAL_GAMEPAD_BUTTON_EAST,
    INTERNAL_GAMEPAD_BUTTON_WEST,
    INTERNAL_GAMEPAD_BUTTON_NORTH,
    INTERNAL_GAMEPAD_BUTTON_L1,
    INTERNAL_GAMEPAD_BUTTON_R1,
    INTERNAL_GAMEPAD_BUTTON_L2,
    INTERNAL_GAMEPAD_BUTTON_R2,
    INTERNAL_GAMEPAD_BUTTON_BACK,
    INTERNAL_GAMEPAD_BUTTON_START,
    INTERNAL_GAMEPAD_BUTTON_LSTICK,
    INTERNAL_GAMEPAD_BUTTON_RSTICK,
    INTERNAL_GAMEPAD_BUTTON_DPAD_DOWN,
    INTERNAL_GAMEPAD_BUTTON_DPAD_RIGHT,
    INTERNAL_GAMEPAD_BUTTON_DPAD_LEFT,
    INTERNAL_GAMEPAD_BUTTON_DPAD_UP,
    INTERNAL_GAMEPAD_BUTTON_HOME,
    INTERNAL_GAMEPAD_BUTTON_CAPTURE,
    INTERNAL_GAMEPAD_BUTTON_PADDLE_RIGHT,
    INTERNAL_GAMEPAD_BUTTON_PADDLE_LEFT,
    INTERNAL_GAMEPAD_BUTTON_AUX,
    INTERNAL_GAMEPAD_BUTTON_COUNT
} internal_gamepad_button_t;

typedef enum {
    INTERNAL_GAMEPAD_CONNECTION_DISCONNECTED = 0,
    INTERNAL_GAMEPAD_CONNECTION_CONNECTING,
    INTERNAL_GAMEPAD_CONNECTION_CONNECTED,
    INTERNAL_GAMEPAD_CONNECTION_DEGRADED,
} internal_gamepad_connection_t;

typedef struct {
    uint64_t buttons;
    uint16_t lx;
    uint16_t ly;
    uint16_t rx;
    uint16_t ry;
    uint16_t l2;
    uint16_t r2;
    bool accel_valid;
    bool gyro_valid;
    int16_t accel[3];
    int16_t gyro[3];
    uint8_t battery_percent;
    bool battery_charging;
    internal_gamepad_connection_t connection;
    uint32_t input_updates_total;
    uint32_t input_rate_millihz;
} internal_gamepad_state_t;

void internal_gamepad_state_reset(internal_gamepad_state_t *state);
void internal_gamepad_state_set_button(internal_gamepad_state_t *state,
                                       internal_gamepad_button_t button,
                                       bool pressed);
bool internal_gamepad_state_get_button(const internal_gamepad_state_t *state,
                                       internal_gamepad_button_t button);
uint16_t internal_gamepad_state_clamp_axis(int32_t value);
uint16_t internal_gamepad_state_snap_axis_center(uint16_t value);
void internal_gamepad_state_apply_center_snap(internal_gamepad_state_t *state);
uint16_t internal_gamepad_state_clamp_trigger(int32_t value);
const char *internal_gamepad_connection_string(
    internal_gamepad_connection_t connection);
