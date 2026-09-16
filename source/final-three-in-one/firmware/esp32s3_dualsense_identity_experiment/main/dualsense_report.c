#include "dualsense_report.h"

#include "esp_mac.h"

#include <string.h>

static const uint8_t s_calibration_feature[] = {
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x10, 0x27, 0xf0, 0xd8,
    0x10, 0x27, 0xf0, 0xd8,
    0x10, 0x27, 0xf0, 0xd8,
    0xf4, 0x01, 0xf4, 0x01,
    0x10, 0x27, 0xf0, 0xd8,
    0x10, 0x27, 0xf0, 0xd8,
    0x10, 0x27, 0xf0, 0xd8,
    0x0b, 0x00, 0x00, 0x00, 0x00, 0x00,
};

static const uint8_t s_firmware_feature[] = {
    0x4a, 0x75, 0x6e, 0x20, 0x31, 0x39, 0x20, 0x32,
    0x30, 0x32, 0x33, 0x31, 0x34, 0x3a, 0x34, 0x37,
    0x3a, 0x33, 0x34, 0x03, 0x00, 0x44, 0x00, 0x08,
    0x02, 0x00, 0x01, 0x36, 0x00, 0x00, 0x01, 0xc1,
    0xc8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x54, 0x01, 0x00, 0x00, 0x14,
    0x00, 0x00, 0x00, 0x0b, 0x00, 0x01, 0x00, 0x06,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
};

_Static_assert(sizeof(s_calibration_feature) == 40,
               "DualSense calibration feature size mismatch");
_Static_assert(sizeof(s_firmware_feature) == 63,
               "DualSense firmware feature size mismatch");

void dualsense_report_make_neutral(uint8_t report[DUALSENSE_INPUT_PAYLOAD_SIZE])
{
    memset(report, 0, DUALSENSE_INPUT_PAYLOAD_SIZE);

    // Four sticks centered, two analog triggers released.
    report[0] = 0x80;
    report[1] = 0x80;
    report[2] = 0x80;
    report[3] = 0x80;
    report[4] = 0x00;
    report[5] = 0x00;

    // Low nibble is the hat. Value 8 is the descriptor's null position.
    report[7] = 0x08;

    // A virtual DualSense lying flat reports gravity on negative Z.
    report[25] = 0x00;
    report[26] = 0xe0; // -8192, little endian.

    // Both touch contacts are not touching.
    report[32] = 0x80;
    report[36] = 0x80;

    // Fixed 80%/complete battery and wired USB data state for Phase 2.
    report[52] = 0x28;
    report[53] = 0x08;
}

size_t dualsense_report_feature_size(uint8_t report_id)
{
    switch (report_id) {
    case 0x05:
        return 40;
    case 0x08:
        return 47;
    case 0x09:
        return 19;
    case 0x0a:
        return 26;
    case 0x20:
        return 63;
    case 0x21:
        return 4;
    case 0x22:
    case 0x80:
    case 0x81:
    case 0x83:
    case 0x84:
    case 0xe0:
    case 0xf0:
    case 0xf1:
    case 0xf4:
        return 63;
    case 0x82:
        return 9;
    case 0x85:
        return 2;
    case 0xa0:
        return 1;
    case 0xf2:
        return 15;
    case 0xf5:
        return 3;
    default:
        return 0;
    }
}

static void copy_feature(uint8_t *buffer,
                         size_t len,
                         const uint8_t *feature,
                         size_t feature_len)
{
    size_t copy_len = len < feature_len ? len : feature_len;
    memcpy(buffer, feature, copy_len);
}

bool dualsense_report_make_feature(uint8_t report_id, uint8_t *buffer, size_t len)
{
    if (!buffer || len == 0) {
        return false;
    }

    memset(buffer, 0, len);

    switch (report_id) {
    case 0x05:
        copy_feature(buffer,
                     len,
                     s_calibration_feature,
                     sizeof(s_calibration_feature));
        return true;

    case 0x09: {
        uint8_t base_mac[6] = {0x74, 0xe7, 0xd6, 0x3a, 0x53, 0x35};
        if (esp_efuse_mac_get_default(base_mac) == ESP_OK) {
            for (size_t i = 0; i < sizeof(base_mac); ++i) {
                buffer[i] = base_mac[sizeof(base_mac) - 1 - i];
            }
        } else {
            memcpy(buffer, base_mac, sizeof(base_mac));
        }

        static const uint8_t pairing_tail[] = {
            0x08, 0x25, 0x00, 0x1e, 0x00, 0xee, 0x74,
            0xd0, 0xbc, 0x00, 0x00, 0x00, 0x00,
        };
        if (len > sizeof(base_mac)) {
            copy_feature(buffer + sizeof(base_mac),
                         len - sizeof(base_mac),
                         pairing_tail,
                         sizeof(pairing_tail));
        }
        return true;
    }

    case 0x20:
        copy_feature(buffer,
                     len,
                     s_firmware_feature,
                     sizeof(s_firmware_feature));
        return true;

    default:
        return false;
    }
}
