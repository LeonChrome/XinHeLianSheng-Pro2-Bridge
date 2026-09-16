#pragma once

#include <stdint.h>
#include "hid_report.h"
#include "internal_gamepad_state.h"
#include "switch2_state.h"

#define REPORT_MAPPER_NINTENDO_MOTION_SAMPLE_SIZE 12
#define REPORT_MAPPER_NINTENDO_MOTION_DEFAULT_OFFSET 0x31
#define REPORT_MAPPER_NINTENDO_MOTION_TIMESTAMP_OFFSET 0x2b

typedef enum {
    REPORT_MAPPER_MOTION_RAW = 0,
    REPORT_MAPPER_MOTION_SWAP_HALVES,
    REPORT_MAPPER_MOTION_REVERSE_SAMPLES,
    REPORT_MAPPER_MOTION_SWAP_REVERSE,
} report_mapper_motion_transform_t;

typedef enum {
    REPORT_MAPPER_MOTION_USB_TEST_OFF = 0,
    REPORT_MAPPER_MOTION_USB_TEST_GYRO_SECOND,
    REPORT_MAPPER_MOTION_USB_TEST_GYRO_FIRST,
    REPORT_MAPPER_MOTION_USB_TEST_ALL_AXES,
} report_mapper_motion_usb_test_t;

void report_mapper_state_to_generic_report(const switch2_state_t *state, bridge_hid_gamepad_report_t *report);
void report_mapper_internal_to_generic_report(const internal_gamepad_state_t *state,
                                              bridge_hid_gamepad_report_t *report);
void report_mapper_state_to_nintendo_report(const switch2_state_t *state, uint8_t report[NINTENDO_REPORT_SIZE]);
void report_mapper_internal_to_nintendo_report(const internal_gamepad_state_t *state,
                                               uint8_t report[NINTENDO_REPORT_SIZE]);
void report_mapper_internal_to_switch_legacy_report(const internal_gamepad_state_t *state,
                                                    uint8_t report[SWITCH_LEGACY_REPORT_SIZE]);
void report_mapper_state_to_switch1_pro_report(const switch2_state_t *state,
                                               bool imu_enabled,
                                               uint8_t report[SWITCH_LEGACY_REPORT_SIZE]);
void report_mapper_switch1_convert_motion_block(const uint8_t source[SWITCH2_MOTION_BLOCK_SIZE],
                                                uint8_t target[SWITCH2_MOTION_BLOCK_SIZE]);
void report_mapper_set_nintendo_motion_passthrough(bool enabled);
bool report_mapper_get_nintendo_motion_passthrough(void);
bool report_mapper_set_nintendo_motion_offset(uint8_t offset);
uint8_t report_mapper_get_nintendo_motion_offset(void);
void report_mapper_start_gyro_calibration(uint16_t samples);
uint16_t report_mapper_get_gyro_calibration_remaining(void);
bool report_mapper_get_gyro_bias(int32_t out_bias[3]);
bool report_mapper_set_gyro_scale(uint16_t scale);
uint16_t report_mapper_get_gyro_scale(void);
bool report_mapper_set_gyro_deadband(int16_t deadband);
int16_t report_mapper_get_gyro_deadband(void);
bool report_mapper_set_motion_transform(report_mapper_motion_transform_t transform);
report_mapper_motion_transform_t report_mapper_get_motion_transform(void);
const char *report_mapper_motion_transform_string(report_mapper_motion_transform_t transform);
bool report_mapper_set_motion_usb_test(report_mapper_motion_usb_test_t mode);
report_mapper_motion_usb_test_t report_mapper_get_motion_usb_test(void);
const char *report_mapper_motion_usb_test_string(report_mapper_motion_usb_test_t mode);
