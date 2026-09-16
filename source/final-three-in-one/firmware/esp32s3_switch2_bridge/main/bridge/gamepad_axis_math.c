#include "gamepad_axis_math.h"

#include "internal_gamepad_state.h"

uint16_t gamepad_axis_normalize_12bit(uint16_t value,
                                      uint16_t center,
                                      uint16_t deadzone,
                                      uint16_t physical_full_scale)
{
    if (physical_full_scale <= deadzone) {
        return INTERNAL_GAMEPAD_AXIS_CENTER;
    }

    int32_t delta = (int32_t)value - (int32_t)center;
    bool negative = delta < 0;
    int32_t magnitude = negative ? -delta : delta;
    if (magnitude <= deadzone) {
        return INTERNAL_GAMEPAD_AXIS_CENTER;
    }

    int32_t usable = (int32_t)physical_full_scale - deadzone;
    int32_t target = negative ?
        INTERNAL_GAMEPAD_AXIS_CENTER :
        (INTERNAL_GAMEPAD_AXIS_MAX - INTERNAL_GAMEPAD_AXIS_CENTER);
    int32_t scaled =
        ((magnitude - deadzone) * target + usable / 2) / usable;
    if (scaled > target) {
        scaled = target;
    }

    int32_t output = negative ?
        (INTERNAL_GAMEPAD_AXIS_CENTER - scaled) :
        (INTERNAL_GAMEPAD_AXIS_CENTER + scaled);
    return internal_gamepad_state_clamp_axis(output);
}

uint8_t gamepad_axis_12bit_to_u8(uint16_t value, bool invert)
{
    value = internal_gamepad_state_snap_axis_center(value);
    if (value == INTERNAL_GAMEPAD_AXIS_CENTER) {
        return 0x80;
    }

    uint32_t mapped =
        ((uint32_t)value * 255u + INTERNAL_GAMEPAD_AXIS_MAX / 2u) /
        INTERNAL_GAMEPAD_AXIS_MAX;
    return invert ? (uint8_t)(0xffu - mapped) : (uint8_t)mapped;
}

int16_t gamepad_axis_12bit_to_i16(uint16_t value, bool invert)
{
    value = internal_gamepad_state_snap_axis_center(value);
    int32_t centered = (int32_t)value - INTERNAL_GAMEPAD_AXIS_CENTER;
    int32_t scaled = centered >= 0 ?
        (centered * 32767) /
            (INTERNAL_GAMEPAD_AXIS_MAX - INTERNAL_GAMEPAD_AXIS_CENTER) :
        (centered * 32768) / INTERNAL_GAMEPAD_AXIS_CENTER;
    if (invert) {
        scaled = -scaled;
    }
    if (scaled < -32768) {
        return -32768;
    }
    if (scaled > 32767) {
        return 32767;
    }
    return (int16_t)scaled;
}

void gamepad_axis_pack_12bit_pair(uint8_t *out,
                                  uint16_t x,
                                  uint16_t y)
{
    if (!out) {
        return;
    }

    x = internal_gamepad_state_clamp_axis(x);
    y = internal_gamepad_state_clamp_axis(y);
    out[0] = (uint8_t)(x & 0xffu);
    out[1] = (uint8_t)(((x >> 8) & 0x0fu) | ((y & 0x0fu) << 4));
    out[2] = (uint8_t)((y >> 4) & 0xffu);
}
