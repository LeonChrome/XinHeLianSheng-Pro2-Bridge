#include "xbox_ble.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>

#include "ble/gatt-service/battery_service_server.h"
#include "ble/gatt-service/device_information_service_server.h"
#include "ble/gatt-service/hids_device.h"
#include "bridge_state.hpp"
#include "btstack.h"
#include "pairing_feedback.hpp"
#include "pico/time.h"
#include "pro2_host.hpp"
#include "pro2_protocol.hpp"
#include "pro2_rumble.hpp"
#include "xbox_ble.h"

#ifndef XHLS_FIRMWARE_VERSION
#define XHLS_FIRMWARE_VERSION "unknown"
#endif

namespace xbox_ble {
namespace {

constexpr uint8_t kInputReportId = 1;
constexpr uint8_t kLedReportId = 2;
constexpr uint8_t kRumbleReportId = 3;
constexpr uint8_t kBatteryReportId = 4;
constexpr uint64_t kTelemetryPeriodUs = 15000000;

// This mirrors the report contract already validated by the ESP32 prototype:
// 4x16-bit sticks, 2x10-bit triggers in 16-bit slots, hat, 15 buttons, capture.
constexpr uint8_t kHidReportDescriptor[] = {
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x05,       // Usage (Game Pad)
    0xa1, 0x01,       // Collection (Application)
    0x85, 0x01,       //   Report ID 1

    0x09, 0x01,       //   Usage (Pointer)
    0xa1, 0x00,       //   Collection (Physical)
    0x09, 0x30,       //     Usage X
    0x09, 0x31,       //     Usage Y
    0x16, 0x00, 0x00, //     Logical Minimum 0
    0x26, 0xff, 0xff, //     Logical Maximum 65535
    0x75, 0x10,       //     Report Size 16
    0x95, 0x02,       //     Report Count 2
    0x81, 0x02,       //     Input (Data, Variable, Absolute)
    0xc0,

    0x09, 0x01,
    0xa1, 0x00,
    0x09, 0x32,       //     Usage Z
    0x09, 0x35,       //     Usage Rz
    0x16, 0x00, 0x00,
    0x26, 0xff, 0xff,
    0x75, 0x10,
    0x95, 0x02,
    0x81, 0x02,
    0xc0,

    0x05, 0x02,       //   Usage Page (Simulation)
    0x09, 0xc5,       //   Usage (Brake)
    0x15, 0x00,
    0x26, 0xff, 0x03, //   Logical Maximum 1023
    0x75, 0x0a,
    0x95, 0x01,
    0x81, 0x02,
    0x15, 0x00,
    0x25, 0x00,
    0x75, 0x06,
    0x95, 0x01,
    0x81, 0x03,

    0x09, 0xc4,       //   Usage (Accelerator)
    0x15, 0x00,
    0x26, 0xff, 0x03,
    0x75, 0x0a,
    0x95, 0x01,
    0x81, 0x02,
    0x15, 0x00,
    0x25, 0x00,
    0x75, 0x06,
    0x95, 0x01,
    0x81, 0x03,

    0x05, 0x01,
    0x09, 0x39,       //   Usage (Hat Switch)
    0x15, 0x01,
    0x25, 0x08,
    0x35, 0x00,
    0x46, 0x3b, 0x01,
    0x65, 0x14,
    0x75, 0x04,
    0x95, 0x01,
    0x81, 0x42,       //   Input (Data, Variable, Absolute, Null State)
    0x15, 0x00,
    0x25, 0x00,
    0x35, 0x00,
    0x45, 0x00,
    0x65, 0x00,
    0x75, 0x04,
    0x95, 0x01,
    0x81, 0x03,

    0x05, 0x09,       //   Usage Page (Button)
    0x19, 0x01,
    0x29, 0x0f,
    0x15, 0x00,
    0x25, 0x01,
    0x75, 0x01,
    0x95, 0x0f,
    0x81, 0x02,
    0x75, 0x01,
    0x95, 0x01,
    0x81, 0x03,

    0x05, 0x0c,       //   Usage Page (Consumer)
    0x09, 0xb2,       //   Usage (Record)
    0x15, 0x00,
    0x25, 0x01,
    0x75, 0x01,
    0x95, 0x01,
    0x81, 0x02,
    0x75, 0x07,
    0x95, 0x01,
    0x81, 0x03,

    0x85, 0x03,       //   Report ID 3, rumble output
    0x05, 0x0f,       //   Usage Page (Physical Input Device)
    0x09, 0x21,       //   Usage (Set Effect Report)
    0xa1, 0x02,       //   Collection (Logical)
    0x09, 0x97,       //     Usage (DC Enable Actuators)
    0x15, 0x00,
    0x25, 0x01,
    0x75, 0x04,
    0x95, 0x01,
    0x91, 0x02,
    0x15, 0x00,
    0x25, 0x00,
    0x75, 0x04,
    0x95, 0x01,
    0x91, 0x03,
    0x09, 0x70,       //     Usage (Magnitude)
    0x15, 0x00,
    0x25, 0x64,
    0x75, 0x08,
    0x95, 0x04,
    0x91, 0x02,
    0x09, 0x50,       //     Usage (Duration)
    0x15, 0x00,
    0x26, 0xff, 0x00,
    0x75, 0x08,
    0x95, 0x01,
    0x91, 0x02,
    0x09, 0xa7,       //     Usage (Start Delay)
    0x15, 0x00,
    0x26, 0xff, 0x00,
    0x75, 0x08,
    0x95, 0x01,
    0x91, 0x02,
    0x09, 0x7c,       //     Usage (Loop Count)
    0x15, 0x00,
    0x26, 0xff, 0x00,
    0x75, 0x08,
    0x95, 0x01,
    0x91, 0x02,
    0xc0,

    0x85, 0x04,       //   Report ID 4, battery
    0x05, 0x06,       //   Usage Page (Generic Device Controls)
    0x09, 0x20,       //   Usage (Battery Strength)
    0x15, 0x00,
    0x26, 0xff, 0x00,
    0x75, 0x08,
    0x95, 0x01,
    0x81, 0x02,

    0x85, 0x02,       //   Report ID 2, player LEDs
    0x05, 0x08,       //   Usage Page (LED)
    0x19, 0x01,
    0x29, 0x04,
    0x15, 0x00,
    0x25, 0x01,
    0x75, 0x01,
    0x95, 0x04,
    0x91, 0x02,
    0x75, 0x04,
    0x95, 0x01,
    0x91, 0x03,

    0xc0,
};

constexpr uint8_t kAdvertisingData[] = {
    0x02, BLUETOOTH_DATA_TYPE_FLAGS, 0x06,
    0x03, BLUETOOTH_DATA_TYPE_COMPLETE_LIST_OF_16_BIT_SERVICE_CLASS_UUIDS,
    static_cast<uint8_t>(ORG_BLUETOOTH_SERVICE_HUMAN_INTERFACE_DEVICE),
    static_cast<uint8_t>(ORG_BLUETOOTH_SERVICE_HUMAN_INTERFACE_DEVICE >> 8),
    0x03, BLUETOOTH_DATA_TYPE_APPEARANCE, 0xc4, 0x03,
    0x0e, BLUETOOTH_DATA_TYPE_SHORTENED_LOCAL_NAME,
    'X', 'b', 'o', 'x', ' ', 'W', 'i', 'r', 'e', 'l', 'e', 's', 's',
};

constexpr uint8_t kScanResponseData[] = {
    0x19, BLUETOOTH_DATA_TYPE_COMPLETE_LOCAL_NAME,
    'X', 'b', 'o', 'x', ' ', 'W', 'i', 'r', 'e', 'l', 'e', 's', 's', ' ',
    'C', 'o', 'n', 't', 'r', 'o', 'l', 'l', 'e', 'r',
};

struct State {
    hci_con_handle_t connection = HCI_CON_HANDLE_INVALID;
    bool input_subscribed = false;
    bool send_request_pending = false;
    bool send_request_call_active = false;
    bool neutral_sent = false;
    bool forget_bonds_after_disconnect = false;
    uint32_t last_sent_generation = 0;
    uint64_t last_telemetry_us = 0;
    uint64_t last_send_us = 0;
    uint64_t send_request_started_us = 0;
    uint32_t notify_count = 0;
    uint32_t can_send_requests = 0;
    uint32_t can_send_callbacks = 0;
    uint32_t synchronous_callbacks = 0;
    uint32_t skipped_source_states = 0;
    uint32_t send_errors = 0;
    uint32_t rumble_writes = 0;
    uint32_t rumble_active_writes = 0;
    bool have_last_rumble = false;
    pro2_rumble::XboxRumbleCommand last_rumble{};
};

State g_state;
btstack_packet_callback_registration_t g_hci_callback;
btstack_packet_callback_registration_t g_sm_callback;
std::array<hids_device_report_t, 4> g_report_storage{};
std::array<uint8_t, 16> g_last_input_report{};
uint8_t g_battery_report = 0x07; // Full, rechargeable.

void write_u16_le(uint8_t *destination, uint16_t value) {
    destination[0] = static_cast<uint8_t>(value);
    destination[1] = static_cast<uint8_t>(value >> 8);
}

uint16_t map_stick(uint16_t value, bool invert) {
    float normalized = pro2::normalize_stick(value);
    if (invert) {
        normalized = -normalized;
    }
    const float mapped = (normalized + 1.0f) * 32767.5f;
    return static_cast<uint16_t>(
        std::clamp<long>(std::lround(mapped), 0, 65535));
}

uint8_t make_hat(uint32_t buttons) {
    const bool up = (buttons & pro2::ButtonUp) != 0;
    const bool down = (buttons & pro2::ButtonDown) != 0;
    const bool left = (buttons & pro2::ButtonLeft) != 0;
    const bool right = (buttons & pro2::ButtonRight) != 0;
    if (up) {
        if (left) return 8;
        if (right) return 2;
        return 1;
    }
    if (down) {
        if (left) return 6;
        if (right) return 4;
        return 5;
    }
    if (left) return 7;
    if (right) return 3;
    return 0x0f;
}

void set_button(uint16_t &buttons, unsigned index, bool pressed) {
    if (pressed && index >= 1 && index <= 15) {
        buttons |= static_cast<uint16_t>(1u << (index - 1));
    }
}

std::array<uint8_t, 16> make_input_report(const pro2::InputState &input) {
    std::array<uint8_t, 16> report{};
    write_u16_le(report.data() + 0, map_stick(input.left_x, false));
    write_u16_le(report.data() + 2, map_stick(input.left_y, true));
    write_u16_le(report.data() + 4, map_stick(input.right_x, false));
    write_u16_le(report.data() + 6, map_stick(input.right_y, true));
    write_u16_le(report.data() + 8,
                 (input.buttons & pro2::ButtonZL) != 0 ? 1023 : 0);
    write_u16_le(report.data() + 10,
                 (input.buttons & pro2::ButtonZR) != 0 ? 1023 : 0);
    report[12] = make_hat(input.buttons);

    uint16_t buttons = 0;
    set_button(buttons, 1, (input.buttons & pro2::ButtonA) != 0);
    set_button(buttons, 2, (input.buttons & pro2::ButtonB) != 0);
    set_button(buttons, 4, (input.buttons & pro2::ButtonX) != 0);
    set_button(buttons, 5, (input.buttons & pro2::ButtonY) != 0);
    set_button(buttons, 7, (input.buttons & pro2::ButtonL) != 0);
    set_button(buttons, 8, (input.buttons & pro2::ButtonR) != 0);
    set_button(buttons, 11, (input.buttons & pro2::ButtonMinus) != 0);
    set_button(buttons, 12, (input.buttons & pro2::ButtonPlus) != 0);
    set_button(buttons, 13, (input.buttons & pro2::ButtonHome) != 0);
    set_button(buttons, 14, (input.buttons & pro2::ButtonLeftStick) != 0);
    set_button(buttons, 15, (input.buttons & pro2::ButtonRightStick) != 0);
    write_u16_le(report.data() + 13, buttons);
    report[15] = (input.buttons & pro2::ButtonCapture) != 0 ? 1 : 0;
    return report;
}

std::array<uint8_t, 16> make_neutral_report() {
    pro2::InputState neutral;
    return make_input_report(neutral);
}

pro2_rumble::XboxRumbleCommand decode_rumble_report(
    const uint8_t *data, size_t length) {
    pro2_rumble::XboxRumbleCommand command;
    if (data == nullptr || length < 8) {
        return command;
    }
    command.enabled_mask = data[0] & 0x0f;
    command.left_trigger = data[1];
    command.right_trigger = data[2];
    command.strong = data[3];
    command.weak = data[4];
    command.duration = data[5];
    command.start_delay = data[6];
    command.loop_count = data[7];
    return command;
}

int clear_all_bonds() {
    int deleted = 0;
    bd_addr_t address;
    const int max_entries = le_device_db_max_count();
    for (int index = 0; index < max_entries; ++index) {
        int address_type = static_cast<int>(BD_ADDR_TYPE_UNKNOWN);
        le_device_db_info(index, &address_type, address, nullptr);
        if (address_type == static_cast<int>(BD_ADDR_TYPE_UNKNOWN)) {
            continue;
        }
        gap_delete_bonding(static_cast<bd_addr_type_t>(address_type), address);
        ++deleted;
    }
    return deleted;
}

int bond_count() {
    int count = 0;
    bd_addr_t address;
    const int max_entries = le_device_db_max_count();
    for (int index = 0; index < max_entries; ++index) {
        int address_type = static_cast<int>(BD_ADDR_TYPE_UNKNOWN);
        le_device_db_info(index, &address_type, address, nullptr);
        if (address_type != static_cast<int>(BD_ADDR_TYPE_UNKNOWN)) {
            ++count;
        }
    }
    return count;
}

void get_report(hci_con_handle_t connection, hid_report_type_t report_type,
                uint16_t report_id, uint16_t max_report_size, uint8_t *out_report) {
    (void)connection;
    if (out_report == nullptr) {
        return;
    }
    if (report_type == HID_REPORT_TYPE_INPUT && report_id == kInputReportId) {
        std::memcpy(out_report, g_last_input_report.data(),
                    std::min<size_t>(max_report_size, g_last_input_report.size()));
    } else if (report_type == HID_REPORT_TYPE_INPUT && report_id == kBatteryReportId &&
               max_report_size >= 1) {
        out_report[0] = g_battery_report;
    } else {
        std::memset(out_report, 0, max_report_size);
    }
}

void send_latest_input() {
    ++g_state.can_send_callbacks;
    if (g_state.send_request_call_active) {
        ++g_state.synchronous_callbacks;
    }
    g_state.send_request_pending = false;
    g_state.send_request_started_us = 0;
    if (g_state.connection == HCI_CON_HANDLE_INVALID || !g_state.input_subscribed) {
        return;
    }

    pro2::InputState source;
    uint32_t generation = 0;
    const bool have_source = bridge::read_latest_input(&source, &generation);
    if (have_source) {
        g_last_input_report = make_input_report(source);
    } else {
        g_last_input_report = make_neutral_report();
    }

    const uint8_t status = hids_device_send_input_report_for_id(
        g_state.connection, kInputReportId, g_last_input_report.data(),
        static_cast<uint16_t>(g_last_input_report.size()));
    if (status == ERROR_CODE_SUCCESS) {
        if (have_source) {
            if (g_state.last_sent_generation != 0 &&
                generation > g_state.last_sent_generation + 1) {
                g_state.skipped_source_states +=
                    generation - g_state.last_sent_generation - 1;
            }
            g_state.last_sent_generation = generation;
            g_state.neutral_sent = false;
        } else {
            g_state.neutral_sent = true;
        }
        ++g_state.notify_count;
        g_state.last_send_us = time_us_64();
    } else {
        ++g_state.send_errors;
        std::printf("[BLE_INPUT] notify failed status=%02x\n", status);
    }
}

void request_input_send() {
    if (g_state.connection == HCI_CON_HANDLE_INVALID ||
        !g_state.input_subscribed || g_state.send_request_pending) {
        return;
    }

    // BTstack may invoke HIDS_SUBEVENT_CAN_SEND_NOW synchronously from inside
    // this call. Arm the state first so that an inline callback can clear it.
    g_state.send_request_pending = true;
    g_state.send_request_started_us = time_us_64();
    g_state.send_request_call_active = true;
    ++g_state.can_send_requests;
    const uint8_t status =
        hids_device_request_can_send_now_event(g_state.connection);
    g_state.send_request_call_active = false;
    if (status != ERROR_CODE_SUCCESS) {
        g_state.send_request_pending = false;
        g_state.send_request_started_us = 0;
        ++g_state.send_errors;
        std::printf("[BLE_INPUT] can-send request failed status=%02x\n", status);
    }
}

void handle_hids_event(const uint8_t *packet) {
    switch (hci_event_hids_meta_get_subevent_code(packet)) {
    case HIDS_SUBEVENT_INPUT_REPORT_ENABLE: {
        const uint8_t report_id =
            hids_subevent_input_report_enable_get_report_id(packet);
        const bool enabled =
            hids_subevent_input_report_enable_get_enable(packet) != 0;
        if (report_id == kInputReportId) {
            g_state.connection =
                hids_subevent_input_report_enable_get_con_handle(packet);
            g_state.input_subscribed = enabled;
            g_state.send_request_pending = false;
            g_state.send_request_call_active = false;
            g_state.send_request_started_us = 0;
            g_state.neutral_sent = false;
            std::printf("[BLE_HID] input subscribed=%u report_id=%u\n",
                        enabled, report_id);
            if (!enabled) {
                pro2_host::stop_rumble();
            }
        }
        break;
    }
    case HIDS_SUBEVENT_CAN_SEND_NOW:
        send_latest_input();
        break;
    case HIDS_SUBEVENT_SET_REPORT: {
        const uint8_t report_id = hids_subevent_set_report_get_report_id(packet);
        const uint8_t length = hids_subevent_set_report_get_report_length(packet);
        const uint8_t *data = hids_subevent_set_report_get_report_data(packet);
        if (report_id == kRumbleReportId && length >= 8) {
            ++g_state.rumble_writes;
            const pro2_rumble::XboxRumbleCommand command =
                decode_rumble_report(data, length);
            const bool is_active = pro2_rumble::active(command);
            if (is_active) {
                ++g_state.rumble_active_writes;
            }
            const bool changed =
                !g_state.have_last_rumble ||
                std::memcmp(&command, &g_state.last_rumble,
                            sizeof(command)) != 0;
            g_state.have_last_rumble = true;
            g_state.last_rumble = command;
            pro2_host::set_rumble(command);
            if (g_state.rumble_writes <= 5 ||
                (changed && (g_state.rumble_writes % 25) == 0) ||
                (g_state.rumble_writes % 250) == 0) {
                std::printf(
                    "[BLE_OUTPUT] rumble mask=%02x lt=%u rt=%u strong=%u "
                    "weak=%u duration=%u delay=%u loops=%u active=%u "
                    "return_to_pro2=queued\n",
                    command.enabled_mask, command.left_trigger,
                    command.right_trigger, command.strong, command.weak,
                    command.duration, command.start_delay,
                    command.loop_count, is_active);
            }
        } else if (report_id == kLedReportId && length >= 1) {
            std::printf("[BLE_OUTPUT] player_leds=%02x\n", data[0] & 0x0f);
        }
        break;
    }
    default:
        break;
    }
}

void packet_handler(uint8_t packet_type, uint16_t channel, uint8_t *packet,
                    uint16_t size) {
    (void)channel;
    (void)size;
    if (packet_type != HCI_EVENT_PACKET) {
        return;
    }

    switch (hci_event_packet_get_type(packet)) {
    case BTSTACK_EVENT_STATE:
        if (btstack_event_state_get_state(packet) == HCI_STATE_WORKING) {
            bd_addr_t local_address;
            gap_local_bd_addr(local_address);
            const int stored_bonds = bond_count();
            std::printf("[BLE_READY] addr=%s name=\"Xbox Wireless Controller\" "
                        "mode=source_paced conn_interval_request=7.5ms "
                        "stored_bonds=%d advertising=on "
                        "pairing_timeout=none\n",
                        bd_addr_to_str(local_address), stored_bonds);
            if (stored_bonds == 0) {
                pairing_feedback::start("xbox_ble_no_stored_peer");
            }
        }
        break;
    case HCI_EVENT_LE_META:
        switch (hci_event_le_meta_get_subevent_code(packet)) {
        case HCI_SUBEVENT_LE_CONNECTION_COMPLETE:
            if (hci_subevent_le_connection_complete_get_status(packet) ==
                ERROR_CODE_SUCCESS) {
                g_state.connection =
                    hci_subevent_le_connection_complete_get_connection_handle(packet);
                g_state.input_subscribed = false;
                g_state.send_request_pending = false;
                g_state.send_request_call_active = false;
                g_state.send_request_started_us = 0;
                g_state.neutral_sent = false;
                gap_request_connection_parameter_update(
                    g_state.connection, 6, 6, 0, 200);
                std::printf("[BLE_LINK] connected handle=%04x "
                            "requested_interval=7.5ms latency=0 timeout=2s "
                            "single_host_lock=on\n",
                            g_state.connection);
            }
            break;
        case HCI_SUBEVENT_LE_CONNECTION_UPDATE_COMPLETE:
            std::printf(
                "[BLE_LINK] parameters status=%02x interval=%.2fms latency=%u "
                "timeout=%ums\n",
                hci_subevent_le_connection_update_complete_get_status(packet),
                hci_subevent_le_connection_update_complete_get_conn_interval(packet) *
                    1.25f,
                hci_subevent_le_connection_update_complete_get_conn_latency(packet),
                hci_subevent_le_connection_update_complete_get_supervision_timeout(packet) *
                    10);
            break;
        default:
            break;
        }
        break;
    case HCI_EVENT_DISCONNECTION_COMPLETE:
        std::printf("[BLE_LINK] disconnected reason=%02x\n",
                    hci_event_disconnection_complete_get_reason(packet));
        g_state.connection = HCI_CON_HANDLE_INVALID;
        g_state.input_subscribed = false;
        g_state.send_request_pending = false;
        g_state.send_request_call_active = false;
        g_state.send_request_started_us = 0;
        g_state.neutral_sent = false;
        g_state.last_sent_generation = 0;
        g_state.have_last_rumble = false;
        pro2_host::stop_rumble();
        if (g_state.forget_bonds_after_disconnect) {
            g_state.forget_bonds_after_disconnect = false;
            const int deleted = clear_all_bonds();
            std::printf("[BLE_BOND] cleared=%d\n", deleted);
        }
        // LE advertising stops when a connection is established. Always
        // restart it after disconnect so a powered-off phone or PC cannot
        // leave Xbox mode permanently invisible until Pico is rebooted.
        gap_advertisements_enable(1);
        std::printf(
            "[BLE_PAIRING] advertising=on reason=host_disconnected "
            "pairing_timeout=none\n");
        break;
    case SM_EVENT_JUST_WORKS_REQUEST:
        sm_just_works_confirm(sm_event_just_works_request_get_handle(packet));
        break;
    case SM_EVENT_PAIRING_COMPLETE:
        std::printf("[BLE_SECURITY] pairing status=%02x reason=%02x\n",
                    sm_event_pairing_complete_get_status(packet),
                    sm_event_pairing_complete_get_reason(packet));
        break;
    case HCI_EVENT_HIDS_META:
        handle_hids_event(packet);
        break;
    default:
        break;
    }
}

} // namespace

bool self_test() {
    const auto neutral = make_neutral_report();
    if (neutral.size() != 16 || neutral[0] != 0x00 || neutral[1] != 0x80 ||
        neutral[2] != 0x00 || neutral[3] != 0x80 || neutral[4] != 0x00 ||
        neutral[5] != 0x80 || neutral[6] != 0x00 || neutral[7] != 0x80 ||
        neutral[12] != 0x0f) {
        return false;
    }

    pro2::InputState test;
    test.left_x = pro2::kStickMaximum;
    test.left_y = 0;
    test.right_x = 0;
    test.right_y = pro2::kStickMaximum;
    test.buttons = pro2::ButtonA | pro2::ButtonUp | pro2::ButtonRight |
                   pro2::ButtonZL | pro2::ButtonCapture;
    const auto report = make_input_report(test);
    const uint8_t rumble_bytes[] = {
        0x0f, 0x11, 0x22, 0x33, 0x44, 0xff, 0x00, 0x01};
    const auto rumble =
        decode_rumble_report(rumble_bytes, sizeof(rumble_bytes));
    return report[0] == 0xff && report[1] == 0xff &&
           report[2] == 0xff && report[3] == 0xff &&
           report[4] == 0x00 && report[5] == 0x00 &&
           report[6] == 0x00 && report[7] == 0x00 &&
           report[8] == 0xff && report[9] == 0x03 &&
           report[12] == 2 && (report[13] & 0x01) != 0 &&
           report[15] == 1 && rumble.enabled_mask == 0x0f &&
           rumble.left_trigger == 0x11 && rumble.right_trigger == 0x22 &&
           rumble.strong == 0x33 && rumble.weak == 0x44 &&
           rumble.duration == 0xff && rumble.start_delay == 0x00 &&
           rumble.loop_count == 0x01;
}

bool init() {
    g_state = {};
    g_state.connection = HCI_CON_HANDLE_INVALID;
    g_state.last_telemetry_us = time_us_64();
    g_last_input_report = make_neutral_report();

    l2cap_init();
    sm_init();
    sm_set_io_capabilities(IO_CAPABILITY_NO_INPUT_NO_OUTPUT);
    sm_set_authentication_requirements(
        SM_AUTHREQ_SECURE_CONNECTION | SM_AUTHREQ_BONDING);

    att_server_init(profile_data, nullptr, nullptr);
    battery_service_server_init(100);
    device_information_service_server_init();
    device_information_service_server_set_manufacturer_name("Microsoft");
    device_information_service_server_set_model_number("1708");
    device_information_service_server_set_serial_number("XHLS-PICO2W-REV-B");
    device_information_service_server_set_hardware_revision("Pico 2 W");
    device_information_service_server_set_firmware_revision(
        XHLS_FIRMWARE_VERSION);
    device_information_service_server_set_software_revision(
        XHLS_FIRMWARE_VERSION);
    device_information_service_server_set_pnp_id(0x02, 0x045e, 0x0b13, 0x0100);

    hids_device_init_with_storage(
        0, kHidReportDescriptor,
        static_cast<uint16_t>(sizeof(kHidReportDescriptor)),
        static_cast<uint16_t>(g_report_storage.size()), g_report_storage.data());
    hids_device_register_packet_handler(packet_handler);
    hids_device_register_get_report_callback(get_report);

    uint16_t adv_interval_min = 0x30;
    uint16_t adv_interval_max = 0x30;
    bd_addr_t null_address{};
    gap_advertisements_set_params(adv_interval_min, adv_interval_max, 0, 0,
                                  null_address, 0x07, 0x00);
    gap_advertisements_set_data(
        static_cast<uint8_t>(sizeof(kAdvertisingData)),
        const_cast<uint8_t *>(kAdvertisingData));
    gap_scan_response_set_data(
        static_cast<uint8_t>(sizeof(kScanResponseData)),
        const_cast<uint8_t *>(kScanResponseData));
    gap_advertisements_enable(1);

    g_hci_callback.callback = packet_handler;
    hci_add_event_handler(&g_hci_callback);
    g_sm_callback.callback = packet_handler;
    sm_add_event_handler(&g_sm_callback);

    hci_power_control(HCI_POWER_ON);
    return true;
}

void forget_bonds() {
    gap_advertisements_enable(0);
    if (g_state.connection != HCI_CON_HANDLE_INVALID) {
        g_state.forget_bonds_after_disconnect = true;
        std::printf("[BLE_BOND] disconnecting active host before clearing bonds\n");
        gap_disconnect(g_state.connection);
        return;
    }

    const int deleted = clear_all_bonds();
    gap_advertisements_enable(1);
    std::printf("[BLE_BOND] cleared=%d advertising=on\n", deleted);
}

bool connected() {
    return g_state.connection != HCI_CON_HANDLE_INVALID;
}

void print_status() {
    std::printf(
        "[BLE_STATUS] connected=%u subscribed=%u stored_bonds=%d "
        "advertising_policy=continuous_after_disconnect\n",
        g_state.connection != HCI_CON_HANDLE_INVALID,
        g_state.input_subscribed, bond_count());
}

void task() {
    const uint64_t now = time_us_64();
    if (g_state.connection != HCI_CON_HANDLE_INVALID &&
        g_state.input_subscribed && !g_state.send_request_pending) {
        pro2::InputState source;
        uint32_t generation = 0;
        const bool have_source = bridge::read_latest_input(&source, &generation);
        const bool should_send =
            (have_source && generation != g_state.last_sent_generation) ||
            (!have_source && !g_state.neutral_sent);
        if (should_send) {
            request_input_send();
        }
    }

    if (now - g_state.last_telemetry_us >= kTelemetryPeriodUs) {
        const float elapsed =
            static_cast<float>(now - g_state.last_telemetry_us) / 1000000.0f;
        const float notify_hz = g_state.notify_count / elapsed;
        float source_age_ms = -1.0f;
        pro2::InputState source;
        uint32_t generation = 0;
        if (bridge::read_latest_input(&source, &generation) &&
            now >= source.received_at_us) {
            source_age_ms =
                static_cast<float>(now - source.received_at_us) / 1000.0f;
        }
        const float pending_age_ms =
            g_state.send_request_pending &&
                    g_state.send_request_started_us > 0 &&
                    now >= g_state.send_request_started_us
                ? static_cast<float>(now - g_state.send_request_started_us) /
                      1000.0f
                : 0.0f;
        std::printf(
            "[BLE_HEALTH] connected=%u subscribed=%u notify_hz=%.1f "
            "source_age_ms=%.1f pending=%u pending_age_ms=%.1f "
            "requests=%lu callbacks=%lu inline_callbacks=%lu "
            "skipped_source=%lu send_errors=%lu rumble_writes=%lu "
            "rumble_active_writes=%lu "
            "policy=latest_state_att_backpressure\n",
            g_state.connection != HCI_CON_HANDLE_INVALID,
            g_state.input_subscribed, notify_hz, source_age_ms,
            g_state.send_request_pending, pending_age_ms,
            static_cast<unsigned long>(g_state.can_send_requests),
            static_cast<unsigned long>(g_state.can_send_callbacks),
            static_cast<unsigned long>(g_state.synchronous_callbacks),
            static_cast<unsigned long>(g_state.skipped_source_states),
            static_cast<unsigned long>(g_state.send_errors),
            static_cast<unsigned long>(g_state.rumble_writes),
            static_cast<unsigned long>(g_state.rumble_active_writes));
        g_state.last_telemetry_us = now;
        g_state.notify_count = 0;
        g_state.can_send_requests = 0;
        g_state.can_send_callbacks = 0;
        g_state.synchronous_callbacks = 0;
        g_state.skipped_source_states = 0;
    }
}

} // namespace xbox_ble
