#include "usb_xinput_device.h"

#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include "app_log.h"
#include "device_config.h"
#include "esp_timer.h"
#include "gamepad_axis_math.h"
#include "normalized_rumble.h"
#include "report_rate_stats.h"
#include "tusb.h"
#include "usb_descriptors.h"
#include "usb_switch2_vendor.h"
#include "xbox_paddle_mapper.h"

static const char *TAG = "usb_xinput";

#define XINPUT_RUMBLE_HOLD_MS 220

#ifdef XINPUT_ELITE_EXPERIMENT
#define XINPUT_OUT_MAX_LEN 64
#define GIP_PACKET_MAX_LEN 64
#define GIP_HELLO_INTERVAL_US 500000LL
#define GIP_ACTIVE_INPUT_INTERVAL_US 20000LL
#define GIP_INPUT_PAYLOAD_LEN 14
#define GIP_INPUT_PACKET_LEN (4 + GIP_INPUT_PAYLOAD_LEN)
#define GIP_METADATA_CHUNK_PAYLOAD_MAX 58

static const uint8_t s_gip_gamepad_metadata[] = {
    // Standard Gamepad compiled metadata from MS-GIPUSB section 3.2.5.1.2.2.
    0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xb6, 0x00,
    0x77, 0x00, 0x16, 0x00, 0x1b, 0x00, 0x1c, 0x00,
    0x23, 0x00, 0x29, 0x00, 0x46, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x00, 0x00, 0x00, 0x00, 0x06, 0x01, 0x02, 0x03,
    0x04, 0x06, 0x07, 0x05, 0x01, 0x04, 0x05, 0x06,
    0x0a, 0x01, 0x1a, 0x00, 0x57, 0x69, 0x6e, 0x64,
    0x6f, 0x77, 0x73, 0x2e, 0x58, 0x62, 0x6f, 0x78,
    0x2e, 0x49, 0x6e, 0x70, 0x75, 0x74, 0x2e, 0x47,
    0x61, 0x6d, 0x65, 0x70, 0x61, 0x64, 0x03, 0x56,
    0xff, 0x76, 0x97, 0xfd, 0x9b, 0x81, 0x45, 0xad,
    0x45, 0xb6, 0x45, 0xbb, 0xa5, 0x26, 0xd6, 0x2c,
    0x40, 0x2e, 0x08, 0xdf, 0x07, 0xe1, 0x45, 0xa5,
    0xab, 0xa3, 0x12, 0x7a, 0xf1, 0x97, 0xb5, 0xe7,
    0x1f, 0xf3, 0xb8, 0x86, 0x73, 0xe9, 0x40, 0xa9,
    0xf8, 0x2f, 0x21, 0x26, 0x3a, 0xcf, 0xb7, 0x02,
    0x17, 0x00, 0x20, 0x0e, 0x00, 0x01, 0x00, 0x10,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17,
    0x00, 0x09, 0x09, 0x00, 0x01, 0x00, 0x08, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
};

TU_VERIFY_STATIC(sizeof(s_gip_gamepad_metadata) == 182,
                 "unexpected GIP gamepad metadata size");
#else
#define XINPUT_INPUT_REPORT_LEN 20
#define XINPUT_OUT_MAX_LEN 32

typedef struct __attribute__((packed)) {
    uint8_t report_id;
    uint8_t report_size;
    uint8_t buttons_low;
    uint8_t buttons_high;
    uint8_t left_trigger;
    uint8_t right_trigger;
    int16_t left_x;
    int16_t left_y;
    int16_t right_x;
    int16_t right_y;
    uint8_t reserved[6];
} xinput_input_report_t;

typedef char xinput_report_size_must_be_20[
    sizeof(xinput_input_report_t) == XINPUT_INPUT_REPORT_LEN ? 1 : -1];
#endif

static uint32_t s_out_count;
static uint16_t s_last_out_len;
static uint8_t s_last_left_motor;
static uint8_t s_last_right_motor;

#ifdef XINPUT_ELITE_EXPERIMENT
typedef enum {
    GIP_STATE_ARRIVAL = 0,
    GIP_STATE_METADATA,
    GIP_STATE_IDLE,
    GIP_STATE_ACTIVE,
} gip_state_t;

typedef enum {
    GIP_METADATA_NONE = 0,
    GIP_METADATA_SEND_FIRST,
    GIP_METADATA_WAIT_FIRST_ACK,
    GIP_METADATA_SEND_REMAINDER,
    GIP_METADATA_WAIT_FINAL_ACK,
    GIP_METADATA_SEND_COMPLETE,
} gip_metadata_phase_t;

static uint8_t s_gip_seq;
static int64_t s_last_hello_us;
static int64_t s_last_input_us;
static bool s_gip_active_requested;
static bool s_gip_auth_challenge_seen;
static uint16_t s_gip_metadata_offset;
static uint8_t s_gip_metadata_seq;
static gip_state_t s_gip_state;
static gip_metadata_phase_t s_gip_metadata_phase;
#endif

static bool xinput_mode(void)
{
    return device_config_get_mode() == XINPUT_EXPERIMENT_MODE;
}

#ifdef XINPUT_ELITE_EXPERIMENT
static const char *gip_state_name(gip_state_t state)
{
    switch (state) {
    case GIP_STATE_ARRIVAL:
        return "Arrival";
    case GIP_STATE_METADATA:
        return "Metadata";
    case GIP_STATE_IDLE:
        return "Idle";
    case GIP_STATE_ACTIVE:
        return "Active";
    default:
        return "Unknown";
    }
}

static void gip_set_state(gip_state_t state, const char *reason)
{
    if (s_gip_state == state) {
        return;
    }

    APP_LOGI(TAG, "GIP state %s -> %s reason=%s",
             gip_state_name(s_gip_state),
             gip_state_name(state),
             reason ? reason : "unspecified");
    s_gip_state = state;
}

static void gip_reset_arrival(const char *reason)
{
    s_gip_seq = 0;
    s_last_hello_us = 0;
    s_last_input_us = 0;
    s_gip_active_requested = false;
    s_gip_auth_challenge_seen = false;
    s_gip_metadata_offset = 0;
    s_gip_metadata_seq = 0;
    s_gip_state = GIP_STATE_ARRIVAL;
    s_gip_metadata_phase = GIP_METADATA_NONE;
    APP_LOGI(TAG, "GIP state=Arrival reason=%s", reason ? reason : "reset");
}
#endif

void usb_xinput_device_init(void)
{
#ifdef XINPUT_ELITE_EXPERIMENT
    gip_reset_arrival("firmware init");
#endif
}

void usb_xinput_device_on_mount(void)
{
#ifdef XINPUT_ELITE_EXPERIMENT
    if (xinput_mode()) {
        gip_reset_arrival("SET_CONFIGURATION");
        APP_LOGI(TAG, "GIP endpoint open OUT=0x02 IN=0x82 packet=64 interval=4");
    }
#endif
}

void usb_xinput_device_on_unmount(void)
{
#ifdef XINPUT_ELITE_EXPERIMENT
    if (xinput_mode()) {
        gip_reset_arrival("USB unmount");
    }
#endif
}

bool usb_xinput_device_ready(void)
{
    return xinput_mode() && tud_vendor_mounted() && !tud_suspended();
}

static void set_button_bit(const internal_gamepad_state_t *state,
                           internal_gamepad_button_t button,
                           uint8_t *byte,
                           uint8_t mask)
{
    if (internal_gamepad_state_get_button(state, button)) {
        *byte |= mask;
    }
}

#ifdef XINPUT_ELITE_EXPERIMENT
static void put_u16_le(uint8_t *dst, uint16_t value)
{
    dst[0] = (uint8_t)(value & 0xffu);
    dst[1] = (uint8_t)(value >> 8);
}

static void put_i16_le(uint8_t *dst, int16_t value)
{
    put_u16_le(dst, (uint16_t)value);
}

static uint8_t gip_next_seq(void)
{
    s_gip_seq = (uint8_t)(s_gip_seq + 1u);
    if (s_gip_seq == 0) {
        s_gip_seq = 1;
    }
    return s_gip_seq;
}

static bool gip_write_bytes(const uint8_t *packet, size_t len)
{
    if (!packet || len == 0 || len > GIP_PACKET_MAX_LEN || !usb_xinput_device_ready()) {
        return false;
    }

    if (tud_vendor_write_available() < len) {
        tud_vendor_write_flush();
        if (tud_vendor_write_available() < len) {
            return false;
        }
    }

    uint32_t written = tud_vendor_write(packet, len);
    tud_vendor_write_flush();
    bool ok = written == len;
    if (ok && len >= 4) {
        uint16_t payload_len = 0;
        unsigned shift = 0;
        for (size_t i = 3; i < len && i < 6; ++i) {
            payload_len |= (uint16_t)(packet[i] & 0x7fu) << shift;
            if ((packet[i] & 0x80u) == 0) {
                break;
            }
            shift += 7;
        }
        APP_LOGI(TAG, "GIP IN type=0x%02x flags=0x%02x seq=%u len=%u wire=%u state=%s",
                 packet[0],
                 packet[1],
                 (unsigned)packet[2],
                 (unsigned)payload_len,
                 (unsigned)len,
                 gip_state_name(s_gip_state));
    }
    return ok;
}

static bool gip_write_message(uint8_t command, uint8_t flags, uint8_t seq,
                              const uint8_t *payload, uint8_t payload_len)
{
    if (payload_len > GIP_PACKET_MAX_LEN - 4) {
        return false;
    }

    uint8_t packet[GIP_PACKET_MAX_LEN];
    packet[0] = command;
    packet[1] = flags;
    packet[2] = seq;
    packet[3] = payload_len;
    if (payload_len > 0 && payload) {
        memcpy(packet + 4, payload, payload_len);
    }
    return gip_write_bytes(packet, (size_t)payload_len + 4u);
}

static size_t gip_encode_varint(uint8_t *dst, uint16_t value)
{
    size_t count = 0;
    do {
        uint8_t byte = (uint8_t)(value & 0x7fu);
        value >>= 7;
        if (value != 0) {
            byte |= 0x80u;
        }
        dst[count++] = byte;
    } while (value != 0);

    return count;
}

static bool gip_write_chunk(uint8_t command, uint8_t flags, uint8_t seq,
                            uint16_t chunk_offset, const uint8_t *payload,
                            uint8_t payload_len)
{
    uint8_t packet[GIP_PACKET_MAX_LEN];
    uint8_t chunk_value[3];
    size_t chunk_value_len = gip_encode_varint(chunk_value, chunk_offset);
    size_t header_len = 3;

    packet[header_len++] = payload_len;
    if ((header_len + chunk_value_len) % 2u != 0) {
        packet[header_len - 1] |= 0x80u;
        packet[header_len++] = 0x00;
    }

    if (header_len + chunk_value_len + payload_len > sizeof(packet)) {
        return false;
    }

    packet[0] = command;
    packet[1] = flags;
    packet[2] = seq;
    memcpy(packet + header_len, chunk_value, chunk_value_len);
    header_len += chunk_value_len;
    if (payload_len > 0 && payload) {
        memcpy(packet + header_len, payload, payload_len);
    }

    return gip_write_bytes(packet, header_len + payload_len);
}

static bool gip_send_hello_if_due(bool force)
{
    int64_t now = esp_timer_get_time();
    if (!force && s_last_hello_us != 0 &&
        now - s_last_hello_us < GIP_HELLO_INTERVAL_US) {
        return true;
    }

    uint8_t payload[28];
    memset(payload, 0, sizeof(payload));
    static const uint8_t address[6] = { 0xe2, 0x17, 0x05, 0x11, 0x0b, 0x00 };
    memcpy(payload + 0, address, sizeof(address));
    put_u16_le(payload + 8, USB_VID_XINPUT_EXPERIMENT);
    put_u16_le(payload + 10, USB_PID_XINPUT_ELITE_EXPERIMENT);
    put_u16_le(payload + 12, 1);
    put_u16_le(payload + 14, 0);
    put_u16_le(payload + 16, 0);
    put_u16_le(payload + 18, 0);
    put_u16_le(payload + 20, 1);
    put_u16_le(payload + 22, 0);
    put_u16_le(payload + 24, 0);
    put_u16_le(payload + 26, 0);

    bool ok = gip_write_message(0x02, 0x20, gip_next_seq(), payload, sizeof(payload));
    if (ok) {
        s_last_hello_us = now;
    }
    return ok;
}

static void gip_start_metadata_transfer(void)
{
    s_gip_active_requested = false;
    s_gip_metadata_offset = 0;
    s_gip_metadata_seq = gip_next_seq();
    s_gip_metadata_phase = GIP_METADATA_SEND_FIRST;
    gip_set_state(GIP_STATE_METADATA, "0x04 metadata request");
    APP_LOGI(TAG, "GIP metadata transfer requested len=%u",
             (unsigned)sizeof(s_gip_gamepad_metadata));
}

static bool gip_send_metadata_fragment(bool first)
{
    uint16_t total = (uint16_t)sizeof(s_gip_gamepad_metadata);
    uint16_t remaining = (uint16_t)(total - s_gip_metadata_offset);
    uint8_t payload_len = remaining > GIP_METADATA_CHUNK_PAYLOAD_MAX ?
        GIP_METADATA_CHUNK_PAYLOAD_MAX : (uint8_t)remaining;
    uint8_t flags = first ? 0xf0 : 0xa0;
    if (!first && remaining <= GIP_METADATA_CHUNK_PAYLOAD_MAX) {
        flags |= 0x10;
    }

    if (!gip_write_chunk(0x04, flags, s_gip_metadata_seq,
                         first ? total : s_gip_metadata_offset,
                         s_gip_gamepad_metadata + s_gip_metadata_offset,
                         payload_len)) {
        return false;
    }

    s_gip_metadata_offset = (uint16_t)(s_gip_metadata_offset + payload_len);
    if (first) {
        s_gip_metadata_phase = GIP_METADATA_WAIT_FIRST_ACK;
    } else if (s_gip_metadata_offset >= total) {
        s_gip_metadata_phase = GIP_METADATA_WAIT_FINAL_ACK;
    } else {
        s_gip_metadata_phase = GIP_METADATA_SEND_REMAINDER;
    }
    return true;
}

static bool gip_send_metadata_complete(void)
{
    if (!gip_write_chunk(0x04, 0xa0, s_gip_metadata_seq,
                         (uint16_t)sizeof(s_gip_gamepad_metadata), NULL, 0)) {
        return false;
    }

    s_gip_metadata_phase = GIP_METADATA_NONE;
    gip_set_state(s_gip_active_requested ? GIP_STATE_ACTIVE : GIP_STATE_IDLE,
                  s_gip_active_requested ?
                      "metadata complete; 0x05 already received" :
                      "metadata complete");
    APP_LOGI(TAG, "GIP metadata transfer complete len=%u",
             (unsigned)sizeof(s_gip_gamepad_metadata));
    return true;
}

static void gip_handle_metadata_ack(const uint8_t *data, uint16_t len)
{
    if (!data || len < 13 || data[0] != 0x01 || data[4] != 0x00 ||
        data[5] != 0x04) {
        return;
    }

    uint32_t offset = (uint32_t)data[7] |
        ((uint32_t)data[8] << 8) |
        ((uint32_t)data[9] << 16) |
        ((uint32_t)data[10] << 24);
    uint16_t remaining = (uint16_t)data[11] | ((uint16_t)data[12] << 8);
    APP_LOGI(TAG, "GIP metadata ACK offset=%lu remaining=%u phase=%u",
             (unsigned long)offset,
             (unsigned)remaining,
             (unsigned)s_gip_metadata_phase);

    if (s_gip_metadata_phase == GIP_METADATA_WAIT_FIRST_ACK &&
        offset >= s_gip_metadata_offset) {
        s_gip_metadata_phase = GIP_METADATA_SEND_REMAINDER;
    } else if (s_gip_metadata_phase == GIP_METADATA_WAIT_FINAL_ACK &&
               offset >= sizeof(s_gip_gamepad_metadata) && remaining == 0) {
        s_gip_metadata_phase = GIP_METADATA_SEND_COMPLETE;
    }
}

static bool gip_send_ack_for(const uint8_t *data, uint16_t len)
{
    if (!data || len < 4 || (data[1] & 0x10u) == 0) {
        return true;
    }

    uint8_t payload[9] = {
        0x00,
        data[0],
        (uint8_t)(data[1] & 0xf0u),
        data[3],
        0x00, 0x00, 0x00, 0x00, 0x00,
    };
    return gip_write_message(0x01, 0x20, gip_next_seq(), payload, sizeof(payload));
}

static uint16_t trigger_to_gip(uint16_t value, bool pressed)
{
    if (value == 0 && pressed) {
        return 1023;
    }

    uint32_t scaled = ((uint32_t)value * 1023u + (INTERNAL_GAMEPAD_TRIGGER_MAX / 2u)) /
        INTERNAL_GAMEPAD_TRIGGER_MAX;
    return scaled > 1023u ? 1023u : (uint16_t)scaled;
}

static void make_gip_input_packet(const internal_gamepad_state_t *state,
                                  uint8_t packet[GIP_INPUT_PACKET_LEN])
{
    memset(packet, 0, GIP_INPUT_PACKET_LEN);
    packet[0] = 0x20;
    packet[1] = 0x00;
    packet[2] = gip_next_seq();
    packet[3] = GIP_INPUT_PAYLOAD_LEN;

    if (!state) {
        return;
    }

    uint8_t *payload = packet + 4;
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_START, &payload[0], 0x04);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_BACK, &payload[0], 0x08);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_SOUTH, &payload[0], 0x10);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_EAST, &payload[0], 0x20);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_WEST, &payload[0], 0x40);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_NORTH, &payload[0], 0x80);

    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_DPAD_UP, &payload[1], 0x01);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_DPAD_DOWN, &payload[1], 0x02);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_DPAD_LEFT, &payload[1], 0x04);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_DPAD_RIGHT, &payload[1], 0x08);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_L1, &payload[1], 0x10);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_R1, &payload[1], 0x20);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_LSTICK, &payload[1], 0x40);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_RSTICK, &payload[1], 0x80);

    put_u16_le(payload + 2, trigger_to_gip(
                   state->l2,
                   internal_gamepad_state_get_button(state, INTERNAL_GAMEPAD_BUTTON_L2)));
    put_u16_le(payload + 4, trigger_to_gip(
                   state->r2,
                   internal_gamepad_state_get_button(state, INTERNAL_GAMEPAD_BUTTON_R2)));
    put_i16_le(payload + 6, gamepad_axis_12bit_to_i16(state->lx, false));
    put_i16_le(payload + 8, gamepad_axis_12bit_to_i16(state->ly, false));
    put_i16_le(payload + 10, gamepad_axis_12bit_to_i16(state->rx, false));
    put_i16_le(payload + 12, gamepad_axis_12bit_to_i16(state->ry, false));
}

static bool gip_service(void)
{
    switch (s_gip_metadata_phase) {
    case GIP_METADATA_SEND_FIRST:
        return gip_send_metadata_fragment(true);
    case GIP_METADATA_SEND_REMAINDER:
        return gip_send_metadata_fragment(false);
    case GIP_METADATA_SEND_COMPLETE:
        return gip_send_metadata_complete();
    default:
        break;
    }

    if (s_gip_state == GIP_STATE_ARRIVAL) {
        return gip_send_hello_if_due(false);
    }

    return true;
}
#else
static uint8_t trigger_to_xinput(uint16_t value, bool pressed)
{
    if (value == 0 && pressed) {
        return 255;
    }
    return (uint8_t)(((uint32_t)value * 255u + (INTERNAL_GAMEPAD_TRIGGER_MAX / 2u)) /
                     INTERNAL_GAMEPAD_TRIGGER_MAX);
}

static void make_report(const internal_gamepad_state_t *state,
                        xinput_input_report_t *report)
{
    memset(report, 0, sizeof(*report));
    report->report_id = 0x00;
    report->report_size = XINPUT_INPUT_REPORT_LEN;

    if (!state) {
        report->left_x = 0;
        report->left_y = 0;
        report->right_x = 0;
        report->right_y = 0;
        return;
    }

    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_DPAD_UP, &report->buttons_low, 0x01);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_DPAD_DOWN, &report->buttons_low, 0x02);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_DPAD_LEFT, &report->buttons_low, 0x04);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_DPAD_RIGHT, &report->buttons_low, 0x08);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_START, &report->buttons_low, 0x10);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_BACK, &report->buttons_low, 0x20);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_LSTICK, &report->buttons_low, 0x40);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_RSTICK, &report->buttons_low, 0x80);

    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_L1, &report->buttons_high, 0x01);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_R1, &report->buttons_high, 0x02);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_HOME, &report->buttons_high, 0x04);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_SOUTH, &report->buttons_high, 0x10);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_EAST, &report->buttons_high, 0x20);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_WEST, &report->buttons_high, 0x40);
    set_button_bit(state, INTERNAL_GAMEPAD_BUTTON_NORTH, &report->buttons_high, 0x80);

    report->left_trigger = trigger_to_xinput(
        state->l2,
        internal_gamepad_state_get_button(state, INTERNAL_GAMEPAD_BUTTON_L2));
    report->right_trigger = trigger_to_xinput(
        state->r2,
        internal_gamepad_state_get_button(state, INTERNAL_GAMEPAD_BUTTON_R2));
    report->left_x = gamepad_axis_12bit_to_i16(state->lx, false);
    report->left_y = gamepad_axis_12bit_to_i16(state->ly, false);
    report->right_x = gamepad_axis_12bit_to_i16(state->rx, false);
    report->right_y = gamepad_axis_12bit_to_i16(state->ry, false);
}

static bool parse_rumble_out(const uint8_t *data, uint16_t len,
                             uint8_t *left_heavy, uint8_t *right_light)
{
    if (!data || !left_heavy || !right_light || len < 4) {
        return false;
    }

    if (len >= 5 && data[1] == 0x08) {
        *left_heavy = data[3];
        *right_light = data[4];
        return true;
    }

    *left_heavy = data[2];
    *right_light = data[3];
    return true;
}

static bool xinput_write_report(const xinput_input_report_t *report)
{
    if (!report || !usb_xinput_device_ready()) {
        return false;
    }

    size_t len = sizeof(*report);
    if (tud_vendor_write_available() < len) {
        tud_vendor_write_flush();
        if (tud_vendor_write_available() < len) {
            return false;
        }
    }

    uint32_t written = tud_vendor_write(report, len);
    if (written != len) {
        return false;
    }
    tud_vendor_write_flush();
    return true;
}
#endif

esp_err_t usb_xinput_device_send_report(const internal_gamepad_state_t *state)
{
    if (!usb_xinput_device_ready()) {
        return ESP_ERR_INVALID_STATE;
    }

#ifdef XINPUT_ELITE_EXPERIMENT
    bool metadata_busy = s_gip_metadata_phase != GIP_METADATA_NONE;
    bool serviced = gip_service();
    if (metadata_busy || s_gip_state != GIP_STATE_ACTIVE) {
        report_rate_stats_record(serviced);
        return serviced ? ESP_OK : ESP_FAIL;
    }

    int64_t now = esp_timer_get_time();
    if (s_last_input_us != 0 &&
        now - s_last_input_us < GIP_ACTIVE_INPUT_INTERVAL_US) {
        report_rate_stats_record(true);
        return ESP_OK;
    }

    internal_gamepad_state_t mapped;
    xbox_paddle_mapper_apply(state, &mapped);
    uint8_t report[GIP_INPUT_PACKET_LEN];
    make_gip_input_packet(&mapped, report);
    bool ok = gip_write_bytes(report, sizeof(report));
    if (ok) {
        s_last_input_us = now;
    }
#else
    internal_gamepad_state_t mapped;
    xbox_paddle_mapper_apply(state, &mapped);
    xinput_input_report_t report;
    make_report(&mapped, &report);
    bool ok = xinput_write_report(&report);
#endif
    report_rate_stats_record(ok);
    return ok ? ESP_OK : ESP_FAIL;
}

#ifdef XINPUT_ELITE_EXPERIMENT
static void gip_hex_dump(const uint8_t *data, uint16_t len, char *out, size_t out_len)
{
    if (!out || out_len == 0) {
        return;
    }

    out[0] = 0;
    if (!data || len == 0) {
        return;
    }

    size_t used = 0;
    for (uint16_t i = 0; i < len && used + 4 < out_len; i++) {
        int written = snprintf(out + used, out_len - used, "%02x%s",
                               data[i],
                               i + 1 < len ? " " : "");
        if (written <= 0) {
            break;
        }
        used += (size_t)written;
    }
}
#endif

void usb_xinput_device_poll_out(void)
{
    if (!xinput_mode() || !tud_vendor_mounted()) {
        return;
    }

    while (tud_vendor_available() > 0) {
        uint8_t data[XINPUT_OUT_MAX_LEN];
        uint32_t read = tud_vendor_read(data, sizeof(data));
        if (read == 0) {
            return;
        }

        s_out_count++;
        s_last_out_len = (uint16_t)read;

#ifdef XINPUT_ELITE_EXPERIMENT
        char hex[(XINPUT_OUT_MAX_LEN * 3) + 1];
        gip_hex_dump(data, (uint16_t)read, hex, sizeof(hex));
        APP_LOGI(TAG, "GIP OUT wire=%u data=%s state=%s",
                 (unsigned)read,
                 hex,
                 gip_state_name(s_gip_state));

        if (read >= 4) {
            if (data[0] == 0x01) {
                gip_handle_metadata_ack(data, (uint16_t)read);
            } else if (data[0] == 0x04) {
                if (s_gip_metadata_phase == GIP_METADATA_NONE) {
                    gip_start_metadata_transfer();
                } else {
                    APP_LOGI(TAG, "GIP duplicate metadata request ignored phase=%u",
                             (unsigned)s_gip_metadata_phase);
                }
            } else if (data[0] == 0x05) {
                s_gip_active_requested = true;
                s_last_input_us = 0;
                if (s_gip_metadata_phase == GIP_METADATA_NONE) {
                    gip_set_state(GIP_STATE_ACTIVE, "0x05 Set Device State Start");
                } else {
                    APP_LOGI(TAG, "GIP Active requested by 0x05; waiting for metadata completion");
                }
            } else if (data[0] == 0x06 && !s_gip_auth_challenge_seen) {
                s_gip_auth_challenge_seen = true;
                APP_LOGW(TAG,
                         "GIP security challenge received; no licensed authentication backend is configured");
            }
            (void)gip_send_ack_for(data, (uint16_t)read);
        }
        continue;
#else
        uint8_t left_heavy = 0;
        uint8_t right_light = 0;
        if (!parse_rumble_out(data, (uint16_t)read, &left_heavy, &right_light)) {
            APP_LOGD(TAG, "XInput OUT ignored len=%u", (unsigned)read);
            continue;
        }

        s_last_left_motor = left_heavy;
        s_last_right_motor = right_light;
        if (left_heavy == 0 && right_light == 0) {
            usb_switch2_vendor_stop_hd_rumble();
            APP_LOGI(TAG, "XInput OUT rumble stop len=%u", (unsigned)read);
            continue;
        }

        normalized_rumble_t rumble;
        normalized_rumble_from_dualsense_motors(
            right_light,
            left_heavy,
            XINPUT_RUMBLE_HOLD_MS,
            &rumble);
        usb_switch2_vendor_start_normalized_rumble(&rumble, "xinput-out");
        APP_LOGI(TAG, "XInput OUT rumble len=%u left_heavy=%u right_light=%u",
                 (unsigned)read,
                 (unsigned)left_heavy,
                 (unsigned)right_light);
#endif
    }
}

uint32_t usb_xinput_device_out_count(void)
{
    return s_out_count;
}

uint16_t usb_xinput_device_last_out_len(void)
{
    return s_last_out_len;
}

uint8_t usb_xinput_device_last_left_motor(void)
{
    return s_last_left_motor;
}

uint8_t usb_xinput_device_last_right_motor(void)
{
    return s_last_right_motor;
}
