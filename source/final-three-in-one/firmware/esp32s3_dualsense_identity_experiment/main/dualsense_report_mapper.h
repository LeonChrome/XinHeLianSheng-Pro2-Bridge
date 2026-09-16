#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "dualsense_report.h"
#include "switch2_state.h"

typedef struct {
    uint16_t raw_lx;
    uint16_t raw_ly;
    uint16_t raw_rx;
    uint16_t raw_ry;
    uint8_t lx;
    uint8_t ly;
    uint8_t rx;
    uint8_t ry;
    uint8_t l2;
    uint8_t r2;
    uint8_t hat;
    uint16_t buttons;
    bool motion_valid;
    int16_t gyro[3];
    int16_t accel[3];
} dualsense_input_debug_t;

void dualsense_report_mapper_init(void);
void dualsense_report_mapper_neutral(
    uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE]);
void dualsense_report_mapper_from_internal(
    const internal_gamepad_state_t *state,
    uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE],
    dualsense_input_debug_t *debug);
void dualsense_report_mapper_from_pro2(const switch2_state_t *state,
                                       uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE],
                                       dualsense_input_debug_t *debug);
