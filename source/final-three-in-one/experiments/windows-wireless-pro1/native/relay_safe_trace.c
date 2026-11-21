#include "relay_safe_trace.h"

#include <inttypes.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

/*
 * Observational transport trace. Only fixed protocol fields are emitted.
 * Never hex-dump packets, signaling option blobs, or Echo payloads. Addresses
 * are only compared; opaque/secret-bearing event payloads are never parsed.
 */
typedef struct {
    char text[768];
    size_t used;
    int valid;
} trace_line_t;

static void trace_print(const char *line, void *context) {
    (void)context;
    puts(line);
}

static void (*trace_sink)(const char *, void *) = trace_print;
static void *trace_sink_context;

static void trace_start(trace_line_t *line, const char *event) {
    int length = snprintf(line->text, sizeof(line->text), "{\"event\":\"%s\"", event);
    line->valid = length >= 0 && (size_t)length + 1 < sizeof(line->text);
    line->used = line->valid ? (size_t)length : 0;
}

static void trace_number(trace_line_t *line, const char *name, uint32_t number) {
    if (!line->valid) return;
    size_t room = sizeof(line->text) - line->used;
    int length = snprintf(line->text + line->used, room, ",\"%s\":%" PRIu32, name, number);
    line->valid = length >= 0 && (size_t)length + 1 < room;
    if (line->valid) line->used += (size_t)length;
}

/* Only literals controlled by this file may enter a string-valued field. */
static void trace_literal(trace_line_t *line, const char *name, const char *value) {
    if (!line->valid) return;
    size_t room = sizeof(line->text) - line->used;
    int length = snprintf(line->text + line->used, room, ",\"%s\":\"%s\"", name, value);
    line->valid = length >= 0 && (size_t)length + 1 < room;
    if (line->valid) line->used += (size_t)length;
}

static void trace_emit(trace_line_t *line) {
    if (!line->valid) return;
    line->text[line->used++] = '}';
    line->text[line->used] = '\0';
    trace_sink(line->text, trace_sink_context);
}

static uint16_t read16(const uint8_t *p) {
    return (uint16_t)(p[0] | ((uint16_t)p[1] << 8));
}

static uint32_t read32(const uint8_t *p) {
    return (uint32_t)read16(p) | ((uint32_t)read16(p + 2) << 16);
}

static int peer_matches(const uint8_t *wire_address, const uint8_t *peer_address) {
    if (!peer_address) return 0;
    for (size_t i = 0; i < 6; ++i) {
        if (wire_address[i] != peer_address[5 - i]) return 0;
    }
    return 1;
}

enum {
    HCI_CONN_COMPLETE = 0x03,
    HCI_CONN_REQUEST = 0x04,
    HCI_DISCONNECT = 0x05,
    HCI_AUTH_COMPLETE = 0x06,
    HCI_REMOTE_NAME = 0x07,
    HCI_ENCRYPTION = 0x08,
    HCI_KEY_CHANGE = 0x09,
    HCI_REMOTE_FEATURES = 0x0b,
    HCI_REMOTE_VERSION = 0x0c,
    HCI_QOS_SETUP = 0x0d,
    HCI_COMMAND_COMPLETE = 0x0e,
    HCI_COMMAND_STATUS = 0x0f,
    HCI_HARDWARE_ERROR = 0x10,
    HCI_FLUSH = 0x11,
    HCI_ROLE = 0x12,
    HCI_MODE = 0x14,
    HCI_BUFFER_OVERFLOW = 0x1a,
    HCI_MAX_SLOTS = 0x1b,
    HCI_CLOCK_OFFSET = 0x1c,
    HCI_PACKET_TYPE = 0x1d,
    HCI_QOS_VIOLATION = 0x1e,
    HCI_PAGE_SCAN_REPETITION = 0x20,
    HCI_FLOW_SPEC = 0x21,
    HCI_REMOTE_EXT_FEATURES = 0x23,
    HCI_SNIFF_SUBRATING = 0x2e,
    HCI_KEY_REFRESH = 0x30,
    HCI_IO_CAP_REQUEST = 0x31,
    HCI_IO_CAP_RESPONSE = 0x32,
    HCI_PAIRING_COMPLETE = 0x36,
    HCI_SUPERVISION_TIMEOUT = 0x38,
    HCI_ENHANCED_FLUSH = 0x39,
    HCI_ENCRYPTION_V2 = 0x59
};

/* Parameter offsets (HCI event code and length take the first two bytes).
 * Source: pinned BTstack btstack_defines.h / generated btstack_event.h, plus
 * Core HCI QoS Setup Complete and Read Remote Extended Features Complete. */
typedef struct {
    uint8_t event, bytes, status_at, handle_at, address_at;
    const char *name;
} hci_layout_t;

static const hci_layout_t hci_layouts[] = {
    { HCI_CONN_COMPLETE,        13, 2, 3, 5, "connection_complete" },
    { HCI_CONN_REQUEST,         12, 0, 0, 2, "connection_request" },
    { HCI_DISCONNECT,            6, 2, 3, 0, "disconnection_complete" },
    { HCI_AUTH_COMPLETE,         5, 2, 3, 0, "authentication_complete" },
    { HCI_REMOTE_NAME,           9, 2, 0, 3, "remote_name_complete" },
    { HCI_ENCRYPTION,            6, 2, 3, 0, "encryption_change" },
    { HCI_KEY_CHANGE,            5, 2, 3, 0, "change_link_key_complete" },
    { HCI_REMOTE_FEATURES,      13, 2, 3, 0, "remote_supported_features" },
    { HCI_REMOTE_VERSION,       10, 2, 3, 0, "remote_version" },
    { HCI_QOS_SETUP,            23, 2, 3, 0, "qos_setup_complete" },
    { HCI_HARDWARE_ERROR,        3, 0, 0, 0, "hardware_error" },
    { HCI_FLUSH,                 4, 0, 2, 0, "flush_occurred" },
    { HCI_ROLE,                 10, 2, 0, 3, "role_change" },
    { HCI_MODE,                  8, 2, 3, 0, "mode_change" },
    { HCI_BUFFER_OVERFLOW,       3, 0, 0, 0, "data_buffer_overflow" },
    { HCI_MAX_SLOTS,             5, 0, 2, 0, "max_slots_change" },
    { HCI_CLOCK_OFFSET,          7, 2, 3, 0, "read_clock_offset_complete" },
    { HCI_PACKET_TYPE,           7, 2, 3, 0, "connection_packet_type_changed" },
    { HCI_QOS_VIOLATION,         4, 0, 2, 0, "qos_violation" },
    /* Core HCI 7.7.31 uses BD_ADDR here. Pinned BTstack's generated H1
     * accessor should not be used to parse this event. */
    { HCI_PAGE_SCAN_REPETITION, 9, 0, 0, 2, "page_scan_repetition_mode_change" },
    { HCI_FLOW_SPEC,            24, 2, 3, 0, "flow_specification_complete" },
    { HCI_REMOTE_EXT_FEATURES,  15, 2, 3, 0, "remote_extended_features_complete" },
    { HCI_SNIFF_SUBRATING,      13, 2, 3, 0, "sniff_subrating" },
    { HCI_KEY_REFRESH,           5, 2, 3, 0, "encryption_key_refresh_complete" },
    { HCI_IO_CAP_REQUEST,        8, 0, 0, 2, "io_capability_request" },
    { HCI_IO_CAP_RESPONSE,      11, 0, 0, 2, "io_capability_response" },
    { HCI_PAIRING_COMPLETE,     9, 2, 0, 3, "simple_pairing_complete" },
    { HCI_SUPERVISION_TIMEOUT,   6, 0, 2, 0, "link_supervision_timeout_changed" },
    { HCI_ENHANCED_FLUSH,        4, 0, 2, 0, "enhanced_flush_complete" },
    { HCI_ENCRYPTION_V2,         7, 2, 3, 0, "encryption_change_v2" },
};

static int is_excluded_hci_event(uint8_t event) {
    switch (event) {
        case 0x13: /* Completed ACL credits are accounted elsewhere. */
        case 0x15: /* Return Link Keys. */
        case 0x16: /* PIN request. */
        case 0x17: /* Link Key Request. */
        case 0x18: /* Link Key Notification. */
        case 0x19: /* Loopback embeds the whole command. */
        case 0x33: /* Numeric comparison value. */
        case 0x34: /* Passkey request. */
        case 0x35: /* OOB data request. */
        case 0x3b: /* Passkey notification. */
        case 0x3c: /* Keypress notification. */
        case 0x3e: /* LE Meta includes key-related subevents; Classic only. */
        case 0x48: /* Completed Data Blocks. */
        case 0x6e: /* BTstack's local transport-sent notification. */
        case 0xff: /* Opaque vendor event may carry private data. */
            return 1;
        default:
            return 0;
    }
}

static void trace_hci_command_result(const uint8_t *p, uint16_t size) {
    /* Event parameters include Num_HCI_Command_Packets and opcode. Standard
     * completions have a Status byte first, but the NOP (or a vendor-specific
     * completion) needn't have such a byte. Don't assume a vendor payload. */
    if (size < 6) return;
    int complete = p[0] == HCI_COMMAND_COMPLETE;
    uint16_t opcode = read16(p + (complete ? 3 : 4));
    if (complete && (!opcode || (opcode >> 10) == 0x3f)) return;
    uint8_t status = p[complete ? 5 : 2];
    if (!status) return; /* The relay already emits its usual success log. */
    trace_line_t line;
    trace_start(&line, "HCI_COMMAND_ERROR");
    trace_number(&line, "hci_event", p[0]);
    trace_number(&line, "opcode", opcode);
    trace_number(&line, "status", status);
    trace_emit(&line);
}

static void trace_hci_parameters(trace_line_t *line, const uint8_t *p) {
    switch (p[0]) {
        case HCI_CONN_COMPLETE:
            trace_number(line, "link_type", p[11]);
            trace_number(line, "encryption_enabled", p[12]);
            break;
        case HCI_CONN_REQUEST:
            trace_number(line, "link_type", p[11]);
            break;
        case HCI_DISCONNECT:
            trace_number(line, "reason", p[5]);
            break;
        case HCI_ENCRYPTION:
        case HCI_ENCRYPTION_V2:
            trace_number(line, "encryption_enabled", p[5]);
            if (p[0] == HCI_ENCRYPTION_V2) trace_number(line, "key_size_bytes", p[6]);
            break;
        case HCI_REMOTE_FEATURES:
        case HCI_REMOTE_EXT_FEATURES: {
            unsigned offset = p[0] == HCI_REMOTE_FEATURES ? 5u : 7u;
            if (offset == 7u) {
                trace_number(line, "feature_page", p[5]);
                trace_number(line, "max_feature_page", p[6]);
            }
            trace_number(line, "feature_mask_low", read32(p + offset));
            trace_number(line, "feature_mask_high", read32(p + offset + 4));
            break;
        }
        case HCI_REMOTE_VERSION:
            trace_number(line, "lmp_version", p[5]);
            trace_number(line, "manufacturer", read16(p + 6));
            trace_number(line, "lmp_subversion", read16(p + 8));
            break;
        case HCI_QOS_SETUP:
            trace_number(line, "service_type", p[6]);
            trace_number(line, "token_rate", read32(p + 7));
            trace_number(line, "peak_bandwidth", read32(p + 11));
            trace_number(line, "latency_us", read32(p + 15));
            trace_number(line, "delay_variation_us", read32(p + 19));
            break;
        case HCI_HARDWARE_ERROR:
            trace_number(line, "hardware_error_code", p[2]);
            break;
        case HCI_ROLE:
            trace_number(line, "role", p[9]);
            break;
        case HCI_MODE:
            trace_number(line, "mode", p[5]);
            trace_number(line, "interval_slots", read16(p + 6));
            break;
        case HCI_BUFFER_OVERFLOW:
            trace_number(line, "link_type", p[2]);
            break;
        case HCI_MAX_SLOTS:
            trace_number(line, "max_slots", p[4]);
            break;
        case HCI_CLOCK_OFFSET:
            trace_number(line, "clock_offset_slots", read16(p + 5));
            break;
        case HCI_PACKET_TYPE:
            trace_number(line, "packet_types", read16(p + 5));
            break;
        case HCI_PAGE_SCAN_REPETITION:
            trace_number(line, "page_scan_repetition_mode", p[8]);
            break;
        case HCI_FLOW_SPEC:
            trace_number(line, "flow_direction", p[6]);
            trace_number(line, "service_type", p[7]);
            trace_number(line, "token_rate", read32(p + 8));
            trace_number(line, "token_bucket_size", read32(p + 12));
            trace_number(line, "peak_bandwidth", read32(p + 16));
            trace_number(line, "access_latency_us", read32(p + 20));
            break;
        case HCI_SNIFF_SUBRATING:
            trace_number(line, "max_tx_latency_slots", read16(p + 5));
            trace_number(line, "max_rx_latency_slots", read16(p + 7));
            trace_number(line, "min_remote_timeout_slots", read16(p + 9));
            trace_number(line, "min_local_timeout_slots", read16(p + 11));
            break;
        case HCI_IO_CAP_RESPONSE:
            trace_number(line, "io_capability", p[8]);
            trace_number(line, "oob_data_present", p[9]);
            trace_number(line, "authentication_requirements", p[10]);
            break;
        case HCI_SUPERVISION_TIMEOUT:
            trace_number(line, "timeout_slots", read16(p + 4));
            break;
        default:
            break;
    }
}

void relay_safe_trace_hci(const uint8_t *packet, uint16_t size, uint16_t tracked_handle,
                          const uint8_t peer_address[6]) {
    if (!packet || size < 2 || is_excluded_hci_event(packet[0])) return;
    uint8_t event = packet[0];
    if ((uint16_t)packet[1] + 2u != size) goto malformed;
    if (event == HCI_COMMAND_COMPLETE || event == HCI_COMMAND_STATUS) {
        trace_hci_command_result(packet, size);
        return;
    }

    const hci_layout_t *layout = NULL;
    for (size_t i = 0; i < sizeof(hci_layouts) / sizeof(hci_layouts[0]); ++i) {
        if (hci_layouts[i].event == event) {
            layout = &hci_layouts[i];
            break;
        }
    }

    trace_line_t line;
    trace_start(&line, "HCI_EVENT_META");
    trace_number(&line, "hci_event", event);
    trace_number(&line, "bytes", size);
    if (!layout) {
        /* No payload is read at all for unknown events. This only counts the
         * event ID and size; unlike an ACL event, scope is unattributed. */
        if (tracked_handle == UINT16_MAX) return;
        trace_literal(&line, "scope", "controller_unattributed");
        trace_emit(&line);
        return;
    }
    if (size < layout->bytes) goto malformed;

    unsigned status = layout->status_at ? packet[layout->status_at] : 0;
    int matches_address = layout->address_at && peer_matches(packet + layout->address_at, peer_address);
    if (layout->address_at && peer_address && !matches_address) return;

    if (layout->handle_at && (event != HCI_CONN_COMPLETE || !status)) {
        uint16_t handle = read16(packet + layout->handle_at) & 0x0fff;
        if (handle != tracked_handle) return;
        trace_number(&line, "handle", handle);
        trace_literal(&line, "scope", "tracked_acl");
    } else {
        trace_literal(&line, "scope", matches_address ? "tracked_peer" : "controller_unattributed");
        if (matches_address && tracked_handle != UINT16_MAX) trace_number(&line, "handle", tracked_handle);
    }
    trace_literal(&line, "name", layout->name);
    if (layout->status_at) trace_number(&line, "status", status);
    /* Failure values following Status are often unspecified. */
    if (!status) trace_hci_parameters(&line, packet);
    trace_emit(&line);
    return;

malformed: {
    trace_line_t error;
    trace_start(&error, "HCI_EVENT_MALFORMED");
    trace_number(&error, "hci_event", event);
    trace_number(&error, "bytes", size);
    trace_emit(&error);
}
}

enum { SIGNAL_CAPACITY = 2048, CLASSIC_SIGNALING_CID = 0x0001 };

typedef struct {
    uint8_t buffer[SIGNAL_CAPACITY];
    size_t expected, received;
    uint16_t handle;
} signaling_reassembly_t;

/* One owned ACL with independent host TX and RX fragment streams. */
static signaling_reassembly_t signaling_rx, signaling_tx;

static void trace_signal_start(trace_line_t *line, const char *event, int from_peer, uint16_t handle) {
    trace_start(line, event);
    trace_literal(line, "direction", from_peer ? "peer_to_pc" : "pc_to_peer");
    trace_number(line, "handle", handle);
}

static void trace_signal_boundary_error(int from_peer, uint16_t handle, const char *reason,
                                        size_t expected, size_t received) {
    trace_line_t line;
    trace_signal_start(&line, "L2CAP_SIGNAL_INCOMPLETE", from_peer, handle);
    trace_literal(&line, "reason", reason);
    trace_number(&line, "expected_bytes", (uint32_t)expected);
    trace_number(&line, "received_bytes", (uint32_t)received);
    trace_emit(&line);
}

void relay_safe_trace_acl_reset(void) {
    if (signaling_rx.expected) {
        trace_signal_boundary_error(1, signaling_rx.handle, "connection_reset",
                                    signaling_rx.expected, signaling_rx.received);
    }
    if (signaling_tx.expected) {
        trace_signal_boundary_error(0, signaling_tx.handle, "connection_reset",
                                    signaling_tx.expected, signaling_tx.received);
    }
    memset(&signaling_rx, 0, sizeof(signaling_rx));
    memset(&signaling_tx, 0, sizeof(signaling_tx));
}

static void trace_config_options(int from_peer, uint16_t handle, unsigned code, unsigned sig_id,
                                 const uint8_t *p, size_t bytes) {
    for (size_t offset = 0; offset < bytes;) {
        if (bytes - offset < 2) {
            trace_signal_boundary_error(from_peer, handle, "config_option_header", 2, bytes - offset);
            return;
        }
        unsigned type = p[offset] & 0x7f;
        unsigned hint = p[offset] >> 7;
        unsigned count = p[offset + 1];
        offset += 2;
        if (count > bytes - offset) {
            trace_signal_boundary_error(from_peer, handle, "config_option_data", count, bytes - offset);
            return;
        }
        trace_line_t line;
        trace_signal_start(&line, "L2CAP_CONFIG_OPTION", from_peer, handle);
        trace_number(&line, "code", code);
        trace_number(&line, "identifier", sig_id);
        trace_number(&line, "option_type", type);
        trace_number(&line, "hint", hint);
        trace_number(&line, "option_bytes", count);
        const uint8_t *param = p + offset;
        if (type == 1 && count == 2) trace_number(&line, "mtu", read16(param));
        if (type == 2 && count == 2) trace_number(&line, "flush_timeout", read16(param));
        if (type == 3 && count == 22) {
            trace_number(&line, "service_type", param[1]);
            trace_number(&line, "token_rate", read32(param + 2));
            trace_number(&line, "token_bucket_size", read32(param + 6));
            trace_number(&line, "peak_bandwidth", read32(param + 10));
            trace_number(&line, "latency_us", read32(param + 14));
            trace_number(&line, "delay_variation_us", read32(param + 18));
        }
        if (type == 4 && count == 9) {
            trace_number(&line, "mode", param[0]);
            trace_number(&line, "tx_window", param[1]);
            trace_number(&line, "max_transmit", param[2]);
            trace_number(&line, "retransmission_timeout_ms", read16(param + 3));
            trace_number(&line, "monitor_timeout_ms", read16(param + 5));
            trace_number(&line, "mps", read16(param + 7));
        }
        if (type == 5 && count == 1) trace_number(&line, "fcs", param[0]);
        if (type == 6 && count == 16) {
            trace_number(&line, "flow_identifier", param[0]);
            trace_number(&line, "service_type", param[1]);
            trace_number(&line, "max_sdu_size", read16(param + 2));
            trace_number(&line, "sdu_interarrival_us", read32(param + 4));
            trace_number(&line, "access_latency_us", read32(param + 8));
            trace_number(&line, "flush_timeout_us", read32(param + 12));
        }
        if (type == 7 && count == 2) trace_number(&line, "extended_window", read16(param));
        trace_emit(&line);
        offset += count;
    }
}

static void trace_signal(int from_peer, uint16_t handle, unsigned code, unsigned sig_id,
                         const uint8_t *p, size_t bytes) {
    static const uint8_t min_bytes[] = {0,2,4,8,4,6,4,4,0,0,2,4};
    if (code >= 1 && code < sizeof(min_bytes) && bytes < min_bytes[code]) {
        trace_signal_boundary_error(from_peer, handle, "short_command_parameters", min_bytes[code], bytes);
        return;
    }
    trace_line_t line;
    trace_signal_start(&line, "L2CAP_CHANNEL_SIGNAL", from_peer, handle);
    trace_number(&line, "code", code);
    trace_number(&line, "identifier", sig_id);
    trace_number(&line, "bytes", (uint32_t)bytes);
    switch (code) {
        case 1: {
            unsigned reason = read16(p);
            trace_number(&line, "reason", reason);
            if (reason == 1 && bytes >= 4) trace_number(&line, "signaling_mtu", read16(p + 2));
            if (reason == 2 && bytes >= 6) {
                trace_number(&line, "local_cid", read16(p + 2));
                trace_number(&line, "remote_cid", read16(p + 4));
            }
            break;
        }
        case 2:
            trace_number(&line, "psm", read16(p));
            trace_number(&line, "source_cid", read16(p + 2));
            break;
        case 3:
            trace_number(&line, "destination_cid", read16(p));
            trace_number(&line, "source_cid", read16(p + 2));
            trace_number(&line, "result", read16(p + 4));
            trace_number(&line, "status", read16(p + 6));
            break;
        case 4:
        case 5:
            trace_number(&line, code == 4 ? "destination_cid" : "source_cid", read16(p));
            trace_number(&line, "flags", read16(p + 2));
            if (code == 5) trace_number(&line, "result", read16(p + 4));
            trace_number(&line, "options_bytes", (uint32_t)(bytes - (code == 4 ? 4u : 6u)));
            break;
        case 6:
        case 7:
            trace_number(&line, "destination_cid", read16(p));
            trace_number(&line, "source_cid", read16(p + 2));
            break;
        case 8:
        case 9:
            /* Echo payload can have arbitrary application data. */
            break;
        case 10:
        case 11: {
            unsigned info = read16(p);
            trace_number(&line, "info_type", info);
            if (code == 11) {
                unsigned result = read16(p + 2);
                trace_number(&line, "result", result);
                if (!result && info == 1 && bytes == 6) trace_number(&line, "connectionless_mtu", read16(p + 4));
                if (!result && info == 2 && bytes == 8) trace_number(&line, "extended_feature_mask", read32(p + 4));
                if (!result && info == 3 && bytes == 12) {
                    trace_number(&line, "fixed_channel_mask_low", read32(p + 4));
                    trace_number(&line, "fixed_channel_mask_high", read32(p + 8));
                }
            }
            break;
        }
        default:
            /* Unknown signaling code, preserve the header metadata only. */
            break;
    }
    trace_emit(&line);
    if (code == 4 || code == 5) {
        size_t header = code == 4 ? 4u : 6u;
        trace_config_options(from_peer, handle, code, sig_id, p + header, bytes - header);
    }
}

static void trace_signal_pdu(int from_peer, uint16_t handle, const uint8_t *p, size_t bytes) {
    for (size_t offset = 0; offset < bytes;) {
        if (bytes - offset < 4) {
            trace_signal_boundary_error(from_peer, handle, "command_header", 4, bytes - offset);
            return;
        }
        unsigned code = p[offset];
        unsigned sig_id = p[offset + 1];
        unsigned count = read16(p + offset + 2);
        offset += 4;
        if (count > bytes - offset) {
            trace_signal_boundary_error(from_peer, handle, "command_parameters", count, bytes - offset);
            return;
        }
        trace_signal(from_peer, handle, code, sig_id, p + offset, count);
        offset += count;
    }
}

void relay_safe_trace_acl(int from_peer, const uint8_t *packet, uint16_t size,
                          uint16_t tracked_handle) {
    if (!packet || size < 4 || tracked_handle == UINT16_MAX) return;
    unsigned header = read16(packet);
    uint16_t handle = (uint16_t)(header & 0x0fff);
    if (handle != tracked_handle || header >> 14) return; /* Unicast only. */
    unsigned boundary = (header >> 12) & 3;
    size_t acl_bytes = read16(packet + 2);
    signaling_reassembly_t *state = from_peer ? &signaling_rx : &signaling_tx;
    if (state->expected && state->handle != handle) state->expected = 0;

    if (boundary == 1) {
        if (!state->expected) return; /* May be non-signaling continuation. */
        size_t room = state->expected - state->received;
        if (!acl_bytes || acl_bytes != size - 4u || acl_bytes > room) {
            trace_signal_boundary_error(from_peer, handle, "bad_acl_continuation", room, size - 4u);
            state->expected = 0;
            return;
        }
        memcpy(state->buffer + state->received, packet + 4, acl_bytes);
        state->received += acl_bytes;
        if (state->received == state->expected) {
            trace_signal_pdu(from_peer, handle, state->buffer, state->expected);
            state->expected = 0;
        }
        return;
    }

    if (state->expected) {
        trace_signal_boundary_error(from_peer, handle, "replaced_first_fragment", state->expected, state->received);
        state->expected = 0;
    }
    if (size < 8 || read16(packet + 6) != CLASSIC_SIGNALING_CID) return;
    size_t l2cap_bytes = read16(packet + 4);
    size_t received = size - 8u;
    if (acl_bytes != size - 4u || received > l2cap_bytes || boundary == 3) {
        trace_signal_boundary_error(from_peer, handle, "bad_first_fragment", l2cap_bytes, received);
        return;
    }
    if (received == l2cap_bytes) {
        trace_signal_pdu(from_peer, handle, packet + 8, received);
        return;
    }
    if (l2cap_bytes > sizeof(state->buffer)) {
        trace_signal_boundary_error(from_peer, handle, "trace_reassembly_capacity", l2cap_bytes, received);
        return;
    }
    state->handle = handle;
    state->expected = l2cap_bytes;
    state->received = received;
    memcpy(state->buffer, packet + 8, received);
}

typedef struct {
    char text[4096];
    size_t used;
    unsigned lines;
} trace_test_output_t;

static void trace_test_capture(const char *line, void *context) {
    trace_test_output_t *output = context;
    size_t bytes = strlen(line);
    size_t capacity = sizeof(output->text) - output->used;
    ++output->lines;
    if (bytes + 2 > capacity) return;
    memcpy(output->text + output->used, line, bytes);
    output->used += bytes;
    output->text[output->used++] = '\n';
    output->text[output->used] = '\0';
}

static int trace_check(int *cases, int condition, const char *description) {
    ++*cases;
    if (condition) return 0;
    fprintf(stderr, "relay_safe_trace self-test: %s\n", description);
    return 1;
}

int relay_safe_trace_self_test(int *cases) {
    enum { test_handle = 0x42 };
    const uint8_t peer[] = {1, 2, 3, 4, 5, 6};
    const uint8_t other[] = {1, 2, 3, 4, 5, 7};
    const uint8_t role[] = {0x12, 8, 0, 6, 5, 4, 3, 2, 1, 1};
    const uint8_t page[] = {0x20, 7, 6, 5, 4, 3, 2, 1, 2};
    const uint8_t sniff[] = {0x2e, 11, 0, test_handle, 0, 1, 0, 2, 0, 3, 0, 4, 0};
    const uint8_t max_slots[] = {0x1b, 3, test_handle, 0, 5};
    const uint8_t malformed[] = {0x23, 3, 0, test_handle, 0};
    const uint8_t failed_status[] = {0x0f, 4, 12, 1, 0x11, 0x08};
    const uint8_t failed_complete[] = {0x0e, 5, 1, 0x4a, 0x0c, 12, 0xa5};
    const uint8_t vendor_complete[] = {0x0e, 5, 1, 0x20, 0xfc, 0xa5, 0xa5};
    const uint8_t fragmented_config[] = {test_handle, 0x20, 7, 0, 16, 0, 1, 0, 4, 7, 8};
    const uint8_t config_tail[] = {test_handle, 0x10, 13, 0, 0, 0x40, 0, 0, 0, 1, 2, 0xa0, 2, 8, 8, 0, 0};
    const uint8_t disconnect[] = {test_handle, 0x20, 12, 0, 8, 0, 1, 0, 6, 1, 4, 0, 0x41, 0, 0x42, 0};
    const uint8_t malformed_option[] = {test_handle, 0x20, 14, 0, 10, 0, 1, 0, 4, 2, 6, 0, 0x40, 0, 0, 0, 1, 2};
    const uint8_t echo[] = {
        test_handle, 0x20, 16, 0, 12, 0, 1, 0, 8, 9, 8, 0, 'K', 'E', 'Y', 'T', 'E', 'S', 'T', '!'
    };
    trace_test_output_t output = {0};
    void (*previous_sink)(const char *, void *) = trace_sink;
    void *previous_context = trace_sink_context;
    trace_sink = trace_test_capture;
    trace_sink_context = &output;
    relay_safe_trace_acl_reset();

    int failures = 0;
    relay_safe_trace_hci(role, sizeof(role), test_handle, other);
    relay_safe_trace_hci(max_slots, sizeof(max_slots), test_handle + 1, peer);
    failures += trace_check(cases, output.lines == 0, "foreign address and handle do not enter scoped logs");

    relay_safe_trace_hci(role, sizeof(role), test_handle, peer);
    relay_safe_trace_hci(page, sizeof(page), test_handle, peer);
    relay_safe_trace_hci(sniff, sizeof(sniff), test_handle, peer);
    failures += trace_check(cases, output.lines == 3 &&
                            strstr(output.text, "\"role\":1") &&
                            strstr(output.text, "\"page_scan_repetition_mode\":2") &&
                            strstr(output.text, "\"min_local_timeout_slots\":4") &&
                            strstr(output.text, "\"handle\":66") &&
                            !strstr(output.text, "address"), "address only correlates events, and safe fields retain offsets");

    relay_safe_trace_hci(malformed, sizeof(malformed), test_handle, peer);
    failures += trace_check(cases, output.lines == 4 &&
                            strstr(output.text, "\"event\":\"HCI_EVENT_MALFORMED\"") &&
                            !strstr(output.text, "feature_mask"), "truncated HCI event is length metadata only");

    memset(&output, 0, sizeof(output));
    const uint8_t private_event_ids[] = {0x13, 0x15, 0x17, 0x18, 0x19, 0x33, 0x34, 0x35, 0x3b, 0x3c, 0x3e, 0xff};
    uint8_t private_event[25];
    memset(private_event, 0xa5, sizeof(private_event));
    private_event[1] = sizeof(private_event) - 2;
    for (size_t i = 0; i < sizeof(private_event_ids); ++i) {
        private_event[0] = private_event_ids[i];
        relay_safe_trace_hci(private_event, sizeof(private_event), test_handle, peer);
        relay_safe_trace_hci(private_event, sizeof(private_event) - 1, test_handle, peer);
    }
    relay_safe_trace_hci(vendor_complete, sizeof(vendor_complete), test_handle, peer);
    failures += trace_check(cases, output.lines == 0, "opaque events and vendor return parameters are excluded");

    relay_safe_trace_hci(failed_status, sizeof(failed_status), test_handle, peer);
    relay_safe_trace_hci(failed_complete, sizeof(failed_complete), test_handle, peer);
    failures += trace_check(cases, output.lines == 2 &&
                            strstr(output.text, "\"opcode\":2065,\"status\":12") &&
                            strstr(output.text, "\"opcode\":3146,\"status\":12") &&
                            !strstr(output.text, "165"), "command failures emit opcode and status only");

    memset(&output, 0, sizeof(output));
    relay_safe_trace_acl(1, fragmented_config, sizeof(fragmented_config), test_handle);
    relay_safe_trace_acl(0, config_tail, sizeof(config_tail), test_handle);
    failures += trace_check(cases, output.lines == 0, "fragment buffers are independent per direction");
    relay_safe_trace_acl(0, disconnect, sizeof(disconnect), test_handle);
    relay_safe_trace_acl(1, config_tail, sizeof(config_tail), test_handle);
    failures += trace_check(cases, output.lines == 4 &&
                            strstr(output.text, "\"direction\":\"pc_to_peer\",\"handle\":66,\"code\":6") &&
                            strstr(output.text, "\"direction\":\"peer_to_pc\",\"handle\":66,\"code\":4") &&
                            strstr(output.text, "\"mtu\":672") &&
                            strstr(output.text, "\"code\":8,\"identifier\":8,\"bytes\":0"),
                            "both directions, reassembled config and second command are visible");

    memset(&output, 0, sizeof(output));
    relay_safe_trace_acl(1, echo, sizeof(echo), test_handle);
    relay_safe_trace_acl(0, malformed_option, sizeof(malformed_option), test_handle);
    relay_safe_trace_acl(0, fragmented_config, sizeof(fragmented_config), test_handle);
    relay_safe_trace_acl_reset();
    failures += trace_check(cases, output.lines == 4 &&
                            strstr(output.text, "\"code\":8,\"identifier\":9,\"bytes\":8") &&
                            strstr(output.text, "\"reason\":\"config_option_data\"") &&
                            strstr(output.text, "\"reason\":\"connection_reset\"") &&
                            !strstr(output.text, "\"mtu\"") && !strstr(output.text, "KEYTEST") &&
                            !strstr(output.text, "4B45595445535421"), "malformed options, unfinished fragments and Echo expose no payload");

    trace_sink = previous_sink;
    trace_sink_context = previous_context;
    return failures;
}
