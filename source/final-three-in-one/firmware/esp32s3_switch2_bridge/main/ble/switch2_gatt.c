#include <stdbool.h>
#include <stdio.h>
#include <string.h>
#include "app_log.h"
#include "gamepad_axis_math.h"
#include "switch2_gatt.h"

static const char *TAG = "switch2_gatt";

#define CENTER_12BIT 2048
#define AXIS_DEADZONE INTERNAL_GAMEPAD_AXIS_CENTER_DEADBAND
#define AXIS_CALIBRATION_SAMPLES 20
#define AXIS_CENTER_LEARN_MAX_DELTA 256
#define FD2_FULL_REPORT_MIN_LEN 60
#define FD2_FULL_MOTION_OFFSET 48
#define MOTION_NOTIFY_MAX_OFFSET 51

typedef struct {
    bool calibrated;
    uint32_t sample_count;
    uint32_t sum_lx;
    uint32_t sum_ly;
    uint32_t sum_rx;
    uint32_t sum_ry;
    uint16_t center_lx;
    uint16_t center_ly;
    uint16_t center_rx;
    uint16_t center_ry;
} axis_calibration_t;

static axis_calibration_t s_fd2_axis = {
    .center_lx = CENTER_12BIT,
    .center_ly = CENTER_12BIT,
    .center_rx = CENTER_12BIT,
    .center_ry = CENTER_12BIT,
};
static axis_calibration_t s_legacy_axis = {
    .center_lx = CENTER_12BIT,
    .center_ly = CENTER_12BIT,
    .center_rx = CENTER_12BIT,
    .center_ry = CENTER_12BIT,
};
static uint8_t s_motion_source_offset = FD2_FULL_MOTION_OFFSET;
static bool s_motion_full_only = true;

static void axis_calibration_reset(axis_calibration_t *cal)
{
    if (!cal) {
        return;
    }
    memset(cal, 0, sizeof(*cal));
    cal->center_lx = CENTER_12BIT;
    cal->center_ly = CENTER_12BIT;
    cal->center_rx = CENTER_12BIT;
    cal->center_ry = CENTER_12BIT;
}

void switch2_gatt_reset_axis_calibration(void)
{
    axis_calibration_reset(&s_fd2_axis);
    axis_calibration_reset(&s_legacy_axis);
    APP_LOGI(TAG, "axis calibration reset");
}

bool switch2_gatt_set_motion_source_offset(uint8_t offset)
{
    if (offset > MOTION_NOTIFY_MAX_OFFSET) {
        return false;
    }
    s_motion_source_offset = offset;
    return true;
}

uint8_t switch2_gatt_get_motion_source_offset(void)
{
    return s_motion_source_offset;
}

void switch2_gatt_set_motion_full_only(bool enabled)
{
    s_motion_full_only = enabled;
}

bool switch2_gatt_get_motion_full_only(void)
{
    return s_motion_full_only;
}

static uint32_t read_le32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint16_t clamp12(int32_t value)
{
    if (value < 0) {
        return 0;
    }
    if (value > 4095) {
        return 4095;
    }
    return (uint16_t)value;
}

static uint16_t unpack12_x(const uint8_t *data, int offset)
{
    return clamp12((int32_t)data[offset] | (((int32_t)data[offset + 1] & 0x0f) << 8));
}

static uint16_t unpack12_y(const uint8_t *data, int offset)
{
    return clamp12((((int32_t)data[offset + 1] >> 4) & 0x0f) | ((int32_t)data[offset + 2] << 4));
}

static bool axis_near_factory_center(uint16_t value)
{
    int32_t delta = (int32_t)value - CENTER_12BIT;
    if (delta < 0) {
        delta = -delta;
    }
    return delta <= AXIS_CENTER_LEARN_MAX_DELTA;
}

static bool axes_look_centered(uint16_t lx,
                               uint16_t ly,
                               uint16_t rx,
                               uint16_t ry)
{
    return axis_near_factory_center(lx) &&
           axis_near_factory_center(ly) &&
           axis_near_factory_center(rx) &&
           axis_near_factory_center(ry);
}

static void apply_axes(axis_calibration_t *cal,
                       const char *source,
                       switch2_state_t *state,
                       uint16_t lx,
                       uint16_t ly,
                       uint16_t rx,
                       uint16_t ry)
{
    if (!cal->calibrated &&
        state->buttons == 0 &&
        axes_look_centered(lx, ly, rx, ry)) {
        cal->sum_lx += lx;
        cal->sum_ly += ly;
        cal->sum_rx += rx;
        cal->sum_ry += ry;
        cal->sample_count++;
        if (cal->sample_count >= AXIS_CALIBRATION_SAMPLES) {
            cal->center_lx = (uint16_t)(cal->sum_lx / cal->sample_count);
            cal->center_ly = (uint16_t)(cal->sum_ly / cal->sample_count);
            cal->center_rx = (uint16_t)(cal->sum_rx / cal->sample_count);
            cal->center_ry = (uint16_t)(cal->sum_ry / cal->sample_count);
            cal->calibrated = true;
            APP_LOGI(TAG, "auto center %s lx=%u ly=%u rx=%u ry=%u",
                     source,
                     (unsigned)cal->center_lx,
                     (unsigned)cal->center_ly,
                     (unsigned)cal->center_rx,
                     (unsigned)cal->center_ry);
        }
    } else if (!cal->calibrated) {
        cal->sample_count = 0;
        cal->sum_lx = 0;
        cal->sum_ly = 0;
        cal->sum_rx = 0;
        cal->sum_ry = 0;
    }

    if (!cal->calibrated) {
        state->lx = CENTER_12BIT;
        state->ly = CENTER_12BIT;
        state->rx = CENTER_12BIT;
        state->ry = CENTER_12BIT;
        return;
    }

    state->lx = gamepad_axis_normalize_12bit(
        lx, cal->center_lx, AXIS_DEADZONE,
        GAMEPAD_AXIS_PRO2_FULL_SCALE_RANGE);
    state->ly = gamepad_axis_normalize_12bit(
        ly, cal->center_ly, AXIS_DEADZONE,
        GAMEPAD_AXIS_PRO2_FULL_SCALE_RANGE);
    state->rx = gamepad_axis_normalize_12bit(
        rx, cal->center_rx, AXIS_DEADZONE,
        GAMEPAD_AXIS_PRO2_FULL_SCALE_RANGE);
    state->ry = gamepad_axis_normalize_12bit(
        ry, cal->center_ry, AXIS_DEADZONE,
        GAMEPAD_AXIS_PRO2_FULL_SCALE_RANGE);
}

static void apply_motion_if_available(switch2_state_t *state, const uint8_t *data, uint16_t len)
{
    if ((uint16_t)s_motion_source_offset + SWITCH2_MOTION_SAMPLE_SIZE > len) {
        return;
    }

    /*
     * Pro2 7FD2 full BLE reports carry one raw IMU sample at bytes 48..59:
     * accel XYZ followed by gyro XYZ. The USB 0x05 report uses the same
     * 12-byte layout shifted by one byte because report[0] is the report ID.
     */
    if (s_motion_full_only && len < FD2_FULL_REPORT_MIN_LEN) {
        return;
    }

    switch2_state_set_motion_sample(state, data + s_motion_source_offset, SWITCH2_MOTION_SAMPLE_SIZE);
}

esp_err_t switch2_gatt_handle_notify(const char *uuid, const uint8_t *data, uint16_t len, switch2_state_t *out_state)
{
    if (!uuid || !data || !out_state) {
        return ESP_ERR_INVALID_ARG;
    }

    APP_LOGD(TAG, "notify uuid=%s len=%u", uuid, (unsigned)len);

    if (strcmp(uuid, SWITCH2_NOTIFY_FD2_UUID) == 0 && len >= 8) {
        switch2_state_update_from_fd2_buttons(out_state, read_le32(&data[4]));
        if (len >= 16) {
            apply_axes(&s_fd2_axis,
                       "fd2",
                       out_state,
                       unpack12_x(data, 10),
                       unpack12_y(data, 10),
                       unpack12_x(data, 13),
                       unpack12_y(data, 13));
        }
        apply_motion_if_available(out_state, data, len);
        return ESP_OK;
    }

    if (strcmp(uuid, SWITCH2_NOTIFY_LEGACY_UUID) == 0 && len >= 5) {
        switch2_state_update_from_legacy_bytes(out_state, data[2], data[3], data[4]);
        if (len >= 11) {
            apply_axes(&s_legacy_axis,
                       "legacy",
                       out_state,
                       unpack12_x(data, 5),
                       unpack12_y(data, 5),
                       unpack12_x(data, 8),
                       unpack12_y(data, 8));
        }
        return ESP_OK;
    }

    return ESP_ERR_NOT_FOUND;
}

esp_err_t switch2_gatt_send_rumble_stub(const uint8_t *data, uint16_t len)
{
    (void)data;
    (void)len;
    APP_LOGI(TAG, "legacy rumble stub is unused; Pro2 rumble is handled by the cc48 HD stream path");
    return ESP_ERR_NOT_SUPPORTED;
}
