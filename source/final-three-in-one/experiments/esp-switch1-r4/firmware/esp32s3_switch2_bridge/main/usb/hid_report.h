#pragma once

#include <stdint.h>

#define GENERIC_HID_REPORT_ID 0x01
#define NINTENDO_INPUT_REPORT_ID 0x05
#define NINTENDO_OUTPUT_REPORT_ID 0x02
#define SWITCH_LEGACY_REPORT_ID_SUBCOMMAND_REPLY 0x21
#define SWITCH_LEGACY_REPORT_ID_FULL_STATE 0x30
#define SWITCH_LEGACY_REPORT_ID_FULL_STATE_MCU 0x31
#define SWITCH_LEGACY_REPORT_ID_SIMPLE_STATE 0x3f
#define SWITCH_LEGACY_REPORT_ID_COMMAND_ACK 0x81
#define SWITCH_LEGACY_REPORT_ID_RUMBLE_SUBCOMMAND 0x01
#define SWITCH_LEGACY_REPORT_ID_RUMBLE 0x10
#define SWITCH_LEGACY_REPORT_ID_PROPRIETARY 0x80
#define MANAGER_FEATURE_REPORT_ID 0x7f
#define NINTENDO_REPORT_SIZE 64
#define SWITCH_LEGACY_REPORT_SIZE 64
#define MANAGER_FEATURE_REPORT_SIZE 64

typedef struct __attribute__((packed)) {
    int8_t x;
    int8_t y;
    int8_t z;
    int8_t rz;
    int8_t rx;
    int8_t ry;
    uint8_t hat;
    uint32_t buttons;
} bridge_hid_gamepad_report_t;

void hid_report_make_neutral(bridge_hid_gamepad_report_t *report);
void hid_report_set_a(bridge_hid_gamepad_report_t *report, int pressed);
void hid_report_make_nintendo_neutral(uint8_t report[NINTENDO_REPORT_SIZE]);
void hid_report_make_switch_legacy_neutral(uint8_t report[SWITCH_LEGACY_REPORT_SIZE]);
