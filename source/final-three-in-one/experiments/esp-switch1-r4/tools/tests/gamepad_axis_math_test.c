#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include "gamepad_axis_math.h"
#include "internal_gamepad_state.h"

static void fail(const char *name, int32_t expected, int32_t actual)
{
    fprintf(stderr, "%s: expected %ld, got %ld\n",
            name, (long)expected, (long)actual);
    exit(1);
}

static void expect_i32(const char *name, int32_t expected, int32_t actual)
{
    if (expected != actual) {
        fail(name, expected, actual);
    }
}

static uint16_t unpack_x(const uint8_t packed[3])
{
    return (uint16_t)packed[0] |
           (uint16_t)((packed[1] & 0x0fu) << 8);
}

static uint16_t unpack_y(const uint8_t packed[3])
{
    return (uint16_t)((packed[1] >> 4) & 0x0fu) |
           (uint16_t)(packed[2] << 4);
}

static void test_common_pro2_normalization(void)
{
    const uint16_t center = INTERNAL_GAMEPAD_AXIS_CENTER;
    const uint16_t deadzone = INTERNAL_GAMEPAD_AXIS_CENTER_DEADBAND;
    const uint16_t full_scale = GAMEPAD_AXIS_PRO2_FULL_SCALE_RANGE;

    expect_i32("center", center,
               gamepad_axis_normalize_12bit(
                   center, center, deadzone, full_scale));
    expect_i32("negative deadzone edge", center,
               gamepad_axis_normalize_12bit(
                   center - deadzone, center, deadzone, full_scale));
    expect_i32("positive deadzone edge", center,
               gamepad_axis_normalize_12bit(
                   center + deadzone, center, deadzone, full_scale));
    expect_i32("negative full throw", 0,
               gamepad_axis_normalize_12bit(
                   center - full_scale, center, deadzone, full_scale));
    expect_i32("positive full throw", INTERNAL_GAMEPAD_AXIS_MAX,
               gamepad_axis_normalize_12bit(
                   center + full_scale, center, deadzone, full_scale));
    expect_i32("negative overtravel", 0,
               gamepad_axis_normalize_12bit(
                   0, center, deadzone, full_scale));
    expect_i32("positive overtravel", INTERNAL_GAMEPAD_AXIS_MAX,
               gamepad_axis_normalize_12bit(
                   INTERNAL_GAMEPAD_AXIS_MAX,
                   center, deadzone, full_scale));

    uint16_t shifted_center = 2020;
    expect_i32("shifted center negative full throw", 0,
               gamepad_axis_normalize_12bit(
                   shifted_center - full_scale,
                   shifted_center, deadzone, full_scale));
    expect_i32("shifted center positive full throw",
               INTERNAL_GAMEPAD_AXIS_MAX,
               gamepad_axis_normalize_12bit(
                   shifted_center + full_scale,
                   shifted_center, deadzone, full_scale));

    uint16_t below_negative = gamepad_axis_normalize_12bit(
        center - full_scale + 1, center, deadzone, full_scale);
    uint16_t below_positive = gamepad_axis_normalize_12bit(
        center + full_scale - 1, center, deadzone, full_scale);
    if (below_negative == 0 || below_positive == INTERNAL_GAMEPAD_AXIS_MAX) {
        fprintf(stderr, "full-scale saturation starts before configured throw\n");
        exit(1);
    }
}

static void test_pro2_report_range(void)
{
    uint8_t packed[3] = {0};

    gamepad_axis_pack_12bit_pair(
        packed, 0, INTERNAL_GAMEPAD_AXIS_MAX);
    expect_i32("Pro2 packed X minimum", 0, unpack_x(packed));
    expect_i32("Pro2 packed Y maximum",
               INTERNAL_GAMEPAD_AXIS_MAX, unpack_y(packed));

    gamepad_axis_pack_12bit_pair(
        packed, INTERNAL_GAMEPAD_AXIS_MAX, 0);
    expect_i32("Pro2 packed X maximum",
               INTERNAL_GAMEPAD_AXIS_MAX, unpack_x(packed));
    expect_i32("Pro2 packed Y minimum", 0, unpack_y(packed));
}

static void test_dualsense_report_range(void)
{
    expect_i32("DualSense minimum", 0,
               gamepad_axis_12bit_to_u8(0, false));
    expect_i32("DualSense maximum", 255,
               gamepad_axis_12bit_to_u8(
                   INTERNAL_GAMEPAD_AXIS_MAX, false));
    expect_i32("DualSense center", 128,
               gamepad_axis_12bit_to_u8(
                   INTERNAL_GAMEPAD_AXIS_CENTER, false));
    expect_i32("DualSense inverted minimum", 255,
               gamepad_axis_12bit_to_u8(0, true));
    expect_i32("DualSense inverted maximum", 0,
               gamepad_axis_12bit_to_u8(
                   INTERNAL_GAMEPAD_AXIS_MAX, true));
    expect_i32("DualSense inverted center", 128,
               gamepad_axis_12bit_to_u8(
                   INTERNAL_GAMEPAD_AXIS_CENTER, true));
}

static void test_xinput_report_range(void)
{
    expect_i32("XInput minimum", -32768,
               gamepad_axis_12bit_to_i16(0, false));
    expect_i32("XInput maximum", 32767,
               gamepad_axis_12bit_to_i16(
                   INTERNAL_GAMEPAD_AXIS_MAX, false));
    expect_i32("XInput center", 0,
               gamepad_axis_12bit_to_i16(
                   INTERNAL_GAMEPAD_AXIS_CENTER, false));
}

int main(void)
{
    test_common_pro2_normalization();
    test_pro2_report_range();
    test_dualsense_report_range();
    test_xinput_report_range();
    puts("gamepad axis math tests passed");
    return 0;
}
