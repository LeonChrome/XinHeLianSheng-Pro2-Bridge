#include "normalized_rumble.h"

#include <string.h>

static uint8_t clamp_percent(int32_t value)
{
    if (value < 0) {
        return 0;
    }
    if (value > 100) {
        return 100;
    }
    return (uint8_t)value;
}

static uint8_t scale_u8_percent(uint8_t value, uint8_t percent)
{
    return (uint8_t)(((uint32_t)value * percent + 50u) / 100u);
}

static uint16_t scale_amplitude(uint8_t value, uint16_t max_amplitude)
{
    if (max_amplitude == 0) {
        return 0;
    }
    return (uint16_t)(((uint32_t)value * max_amplitude + 127u) / 255u);
}

static uint16_t mix_frequency(uint16_t low, uint16_t high, uint8_t value)
{
    return (uint16_t)(low + (((uint32_t)(high - low) * value + 127u) / 255u));
}

static void build_side_payload(uint8_t weak,
                               uint8_t strong,
                               uint16_t max_amplitude,
                               uint8_t out[5])
{
    uint16_t low_amp = scale_amplitude(strong, max_amplitude);
    uint16_t high_amp = scale_amplitude(weak, max_amplitude);
    uint16_t low_freq = 0x0e1;
    uint16_t high_freq = 0x1e1;
    uint64_t value = 0;

    if (low_amp != 0) {
        low_freq = mix_frequency(0x0b8, 0x122, strong);
    }
    if (high_amp != 0) {
        high_freq = mix_frequency(0x160, 0x1f0, weak);
    }

    value |= (uint64_t)low_freq;
    value |= (uint64_t)(low_amp & 0x03ff) << 10;
    value |= (uint64_t)high_freq << 20;
    value |= (uint64_t)(high_amp & 0x03ff) << 30;

    for (size_t i = 0; i < 5; i++) {
        out[i] = (uint8_t)((value >> (8 * i)) & 0xff);
    }
}

void normalized_rumble_reset(normalized_rumble_t *rumble)
{
    if (!rumble) {
        return;
    }

    memset(rumble, 0, sizeof(*rumble));
    rumble->left_gain_percent = 100;
    rumble->right_gain_percent = 100;
    rumble->stop = true;
}

void normalized_rumble_set_balanced(normalized_rumble_t *rumble,
                                    uint8_t weak,
                                    uint8_t strong,
                                    uint16_t duration_ms)
{
    if (!rumble) {
        return;
    }

    rumble->weak = weak;
    rumble->strong = strong;
    rumble->duration_ms = duration_ms;
    rumble->stop = weak == 0 && strong == 0;
}

void normalized_rumble_set_balance(normalized_rumble_t *rumble,
                                   uint8_t left_gain_percent,
                                   uint8_t right_gain_percent)
{
    if (!rumble) {
        return;
    }

    rumble->left_gain_percent = clamp_percent(left_gain_percent);
    rumble->right_gain_percent = clamp_percent(right_gain_percent);
}

bool normalized_rumble_active(const normalized_rumble_t *rumble)
{
    return rumble && !rumble->stop && (rumble->weak != 0 || rumble->strong != 0);
}

void normalized_rumble_from_dualsense_motors(uint8_t right_light,
                                             uint8_t left_heavy,
                                             uint16_t duration_ms,
                                             normalized_rumble_t *out)
{
    normalized_rumble_reset(out);
    if (!out) {
        return;
    }

    normalized_rumble_set_balanced(out, right_light, left_heavy, duration_ms);
}

void normalized_rumble_build_zero_pro2(uint8_t out[5])
{
    build_side_payload(0, 0, 0, out);
}

void normalized_rumble_build_pro2_pair(const normalized_rumble_t *rumble,
                                       uint16_t max_amplitude,
                                       uint8_t left[5],
                                       uint8_t right[5])
{
    if (!rumble || !normalized_rumble_active(rumble)) {
        normalized_rumble_build_zero_pro2(left);
        normalized_rumble_build_zero_pro2(right);
        return;
    }

    build_side_payload(scale_u8_percent(rumble->weak, rumble->left_gain_percent),
                       scale_u8_percent(rumble->strong, rumble->left_gain_percent),
                       max_amplitude,
                       left);
    build_side_payload(scale_u8_percent(rumble->weak, rumble->right_gain_percent),
                       scale_u8_percent(rumble->strong, rumble->right_gain_percent),
                       max_amplitude,
                       right);
}
