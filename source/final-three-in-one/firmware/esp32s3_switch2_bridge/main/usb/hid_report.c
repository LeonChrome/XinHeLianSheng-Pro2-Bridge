#include <string.h>
#include "hid_report.h"

void hid_report_make_neutral(bridge_hid_gamepad_report_t *report)
{
    memset(report, 0, sizeof(*report));
    report->hat = 0;
}

void hid_report_set_a(bridge_hid_gamepad_report_t *report, int pressed)
{
    if (pressed) {
        report->buttons |= 0x0001;
    } else {
        report->buttons &= ~0x00000001u;
    }
}

void hid_report_make_nintendo_neutral(uint8_t report[NINTENDO_REPORT_SIZE])
{
    memset(report, 0, NINTENDO_REPORT_SIZE);
    report[0] = NINTENDO_INPUT_REPORT_ID;
    report[2] = 0x20;
    report[11] = 0x00;
    report[12] = 0x08;
    report[13] = 0x80;
    report[14] = 0x00;
    report[15] = 0x08;
    report[16] = 0x80;
}

void hid_report_make_switch_legacy_neutral(uint8_t report[SWITCH_LEGACY_REPORT_SIZE])
{
    memset(report, 0, SWITCH_LEGACY_REPORT_SIZE);
    report[0] = SWITCH_LEGACY_REPORT_ID_FULL_STATE;
    report[2] = 0x91; // full battery, charging, USB attached
    report[6] = 0x00;
    report[7] = 0x08;
    report[8] = 0x80;
    report[9] = 0x00;
    report[10] = 0x08;
    report[11] = 0x80;
}
