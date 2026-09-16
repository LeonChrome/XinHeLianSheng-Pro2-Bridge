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

static void decode_switch1_side(const uint8_t encoded[4],
                                uint8_t *weak,
                                uint8_t *strong)
{
    if (!encoded || !weak || !strong) {
        return;
    }

    // Switch HD rumble interleaves frequency and amplitude. The high-band
    // amplitude is the even part of byte 1. The low-band amplitude walks a
    // 0x0040/0x8040 table encoded by byte 2 bit 7 and byte 3.
    uint16_t high_index = (uint16_t)((encoded[1] & 0xfeu) >> 1);
    uint16_t low_index = 0;
    if (encoded[3] >= 0x40u) {
        low_index = (uint16_t)(encoded[3] - 0x40u) * 2u;
        if ((encoded[2] & 0x80u) != 0) {
            low_index++;
        }
    }
    if (high_index > 100u) high_index = 100u;
    if (low_index > 100u) low_index = 100u;
    *weak = (uint8_t)((high_index * 255u + 50u) / 100u);
    *strong = (uint8_t)((low_index * 255u + 50u) / 100u);
}

void normalized_rumble_from_switch1_hd(const uint8_t left[4],
                                       const uint8_t right[4],
                                       uint16_t duration_ms,
                                       normalized_rumble_t *out)
{
    normalized_rumble_reset(out);
    if (!out || !left || !right) {
        return;
    }

    uint8_t left_weak = 0, left_strong = 0;
    uint8_t right_weak = 0, right_strong = 0;
    decode_switch1_side(left, &left_weak, &left_strong);
    decode_switch1_side(right, &right_weak, &right_strong);

    uint8_t weak = left_weak > right_weak ? left_weak : right_weak;
    uint8_t strong = left_strong > right_strong ? left_strong : right_strong;
    normalized_rumble_set_balanced(out, weak, strong, duration_ms);
    if (!normalized_rumble_active(out)) {
        return;
    }

    uint16_t left_energy = (uint16_t)left_weak + left_strong;
    uint16_t right_energy = (uint16_t)right_weak + right_strong;
    uint16_t peak = left_energy > right_energy ? left_energy : right_energy;
    uint8_t left_gain = peak == 0 ? 0 : (uint8_t)((left_energy * 100u + peak / 2u) / peak);
    uint8_t right_gain = peak == 0 ? 0 : (uint8_t)((right_energy * 100u + peak / 2u) / peak);
    normalized_rumble_set_balance(out, left_gain, right_gain);
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
