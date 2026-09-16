#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define DUALSENSE_INPUT_REPORT_ID 0x01
#define DUALSENSE_OUTPUT_REPORT_ID 0x02
#define DUALSENSE_INPUT_PAYLOAD_SIZE 63
#define DUALSENSE_OUTPUT_PAYLOAD_SIZE 47

void dualsense_report_make_neutral(uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE]);
size_t dualsense_report_feature_size(uint8_t report_id);
bool dualsense_report_make_feature(uint8_t report_id, uint8_t *buffer, size_t len);
