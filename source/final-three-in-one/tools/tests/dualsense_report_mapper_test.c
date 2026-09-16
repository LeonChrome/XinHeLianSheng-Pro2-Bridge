#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "dualsense_report_mapper.h"

static int s_failures;

void dualsense_report_make_neutral(uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE])
{
    memset(report, 0, DUALSENSE_INPUT_PAYLOAD_SIZE);
    report[0] = 0x80;
    report[1] = 0x80;
    report[2] = 0x80;
    report[3] = 0x80;
    report[7] = 0x08;
    report[25] = 0x00;
    report[26] = 0xe0;
}

void switch2_state_to_internal(const switch2_state_t *src,
                               internal_gamepad_state_t *dst)
{
    (void)src;
    internal_gamepad_state_reset(dst);
}

static int16_t read_i16_le(const uint8_t *src)
{
    return (int16_t)((uint16_t)src[0] | ((uint16_t)src[1] << 8));
}

static void expect_i16(const char *name, int16_t expected, int16_t actual)
{
    if (expected == actual) {
        return;
    }

    fprintf(stderr,
            "FAIL %s: expected %d, got %d\n",
            name,
            (int)expected,
            (int)actual);
    s_failures++;
}

static void test_ps5_motion_mapping(void)
{
    internal_gamepad_state_t state;
    internal_gamepad_state_reset(&state);
    state.gyro_valid = true;
    state.accel_valid = true;
    state.gyro[0] = 100;
    state.gyro[1] = 200;
    state.gyro[2] = -300;
    state.accel[0] = 10;
    state.accel[1] = 4096;
    state.accel[2] = 20;

    uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE];
    dualsense_input_debug_t debug;
    dualsense_report_mapper_init();
    dualsense_report_mapper_from_internal(&state, report, &debug);

    expect_i16("DualSense gyro X = source X",
               115,
               read_i16_le(report + 15));
    expect_i16("DualSense gyro Y = source Z",
               -345,
               read_i16_le(report + 17));
    expect_i16("DualSense gyro Z = -source Y",
               -230,
               read_i16_le(report + 19));
    expect_i16("DualSense accel X = source X",
               20,
               read_i16_le(report + 21));
    expect_i16("DualSense accel Y = source Z",
               40,
               read_i16_le(report + 23));
    expect_i16("DualSense accel Z = -source Y",
               -8192,
               read_i16_le(report + 25));

    expect_i16("Debug gyro X reports mapped value", 115, debug.gyro[0]);
    expect_i16("Debug gyro Y reports mapped value", -345, debug.gyro[1]);
    expect_i16("Debug gyro Z reports mapped value", -230, debug.gyro[2]);
    expect_i16("Debug accel X reports mapped value", 20, debug.accel[0]);
    expect_i16("Debug accel Y reports mapped value", 40, debug.accel[1]);
    expect_i16("Debug accel Z reports mapped value", -8192, debug.accel[2]);
}

static void test_neutral_gravity(void)
{
    uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE];
    dualsense_report_mapper_init();
    dualsense_report_mapper_neutral(report);
    expect_i16("DualSense neutral accel Z is -1g",
               -8192,
               read_i16_le(report + 25));
}

static void test_stationary_gyro_bias_is_removed_once(void)
{
    internal_gamepad_state_t state;
    internal_gamepad_state_reset(&state);
    state.accel_valid = true;
    state.gyro_valid = true;
    state.accel[2] = 4096;
    state.gyro[0] = 12;
    state.gyro[1] = -4;
    state.gyro[2] = -20;

    uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE];
    dualsense_report_mapper_init();
    for (int i = 0; i < 250; i++) {
        dualsense_report_mapper_from_internal(&state, report, NULL);
    }
    dualsense_report_mapper_from_internal(&state, report, NULL);
    expect_i16("DualSense calibrated gyro X is zero", 0, read_i16_le(report + 15));
    expect_i16("DualSense calibrated gyro Y is zero", 0, read_i16_le(report + 17));
    expect_i16("DualSense calibrated gyro Z is zero", 0, read_i16_le(report + 19));
}

static void test_i16_min_negation_saturates(void)
{
    internal_gamepad_state_t state;
    internal_gamepad_state_reset(&state);
    state.gyro_valid = true;
    state.gyro[0] = INT16_MIN;

    uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE];
    dualsense_report_mapper_init();
    dualsense_report_mapper_from_internal(&state, report, NULL);

    expect_i16("DualSense gyro X preserves INT16_MIN when X is not inverted",
               INT16_MIN,
               read_i16_le(report + 15));
}

int main(void)
{
    test_ps5_motion_mapping();
    test_i16_min_negation_saturates();
    test_neutral_gravity();
    test_stationary_gyro_bias_is_removed_once();

    if (s_failures != 0) {
        return 1;
    }

    puts("dualsense report mapper tests passed");
    return 0;
}
