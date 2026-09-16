#include "dualsense_rumble_intent.h"

#include <string.h>

#define DS5_OUTPUT_MIN_PAYLOAD 4
#define DS5_VALID_FLAG0_COMPATIBLE_VIBRATION 0x01
#define DS5_VALID_FLAG0_HAPTICS_SELECT 0x02
#define DS5_VALID_FLAG2_OFFSET 38
#define DS5_VALID_FLAG2_COMPATIBLE_VIBRATION2 0x04

bool dualsense_rumble_intent_parse(const uint8_t *payload,
                                   uint16_t payload_len,
                                   dualsense_rumble_intent_t *out)
{
    if (!payload || !out || payload_len < DS5_OUTPUT_MIN_PAYLOAD) {
        return false;
    }

    memset(out, 0, sizeof(*out));
    out->valid_flag0 = payload[0];
    out->valid_flag1 = payload[1];
    out->valid_flag2 =
        payload_len > DS5_VALID_FLAG2_OFFSET
            ? payload[DS5_VALID_FLAG2_OFFSET]
            : 0;
    out->right_light = payload[2];
    out->left_heavy = payload[3];

    out->compatibility_v1 =
        (out->valid_flag0 &
         DS5_VALID_FLAG0_COMPATIBLE_VIBRATION) != 0;
    out->compatibility_v2 =
        (out->valid_flag2 &
         DS5_VALID_FLAG2_COMPATIBLE_VIBRATION2) != 0;
    out->compatibility_selected =
        (out->valid_flag0 & DS5_VALID_FLAG0_HAPTICS_SELECT) != 0 ||
        out->compatibility_v1 ||
        out->compatibility_v2;
    out->audio_haptics_allowed = !out->compatibility_selected;
    out->ordinary_valid =
        out->compatibility_v1 || out->compatibility_v2;
    out->ordinary_active =
        out->ordinary_valid &&
        (out->right_light != 0 || out->left_heavy != 0);
    return true;
}
