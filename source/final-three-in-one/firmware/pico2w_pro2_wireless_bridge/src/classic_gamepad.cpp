#include "classic_gamepad.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>

#include "bridge_state.hpp"
#include "btstack.h"
#include "classic/hid_device.h"
#include "classic_reconnect_policy.hpp"
#include "dualsense_protocol.hpp"
#include "imu_converter.hpp"
#include "mode_manager.hpp"
#include "pairing_feedback.hpp"
#include "pico/time.h"
#include "pro2_host.hpp"
#include "pro2_rumble.hpp"
#include "switch1_protocol.hpp"

namespace classic_gamepad {
namespace {

constexpr uint64_t kSwitchReportPeriodUs = 15000;
constexpr uint64_t kSwitchSimpleKeepaliveUs = 100000;
constexpr uint64_t kSwitchImuSamplePeriodUs = 5000;
constexpr size_t kSwitchReplyQueueSize = 4;
constexpr uint64_t kTelemetryPeriodUs = 15000000;
// A known host first gets a short passive window, followed by an active HID
// reconnect. The old 4s + 6s cadence made every mode change look broken even
// when the host was available. Keep bounded alternating windows so incoming
// and outgoing pages still cannot race on the same PSMs.
constexpr uint64_t kInitialReconnectDelayUs = 500000;
constexpr uint64_t kSwitchInitialReconnectDelayUs = 1500000;
constexpr uint64_t kReconnectRetryDelayUs = 750000;
// After the two fast reconnects, leave a long uninterrupted incoming window.
// A six-second background cadence spent roughly half of all wall time paging
// an unavailable saved host, during which Windows could see the controller
// but could not connect to it. Thirty seconds keeps automatic recovery while
// making pairing/reconnect from the host side deterministic.
constexpr uint64_t kBackgroundReconnectDelayUs = 30000000;
// The HCI page phase is owned by BTstack. Cancelling a not-yet-connected HID
// CID with hid_device_disconnect() moves its L2CAP channels out of BTstack's
// connection-failure cleanup path and eventually exhausts the fixed pool.
// Only abort after an ACL exists and the HID channels still have not opened.
constexpr uint64_t kHidOpenAfterAclTimeoutUs = 10000000;
// One fast active page is enough for the normal saved-host path. If the host
// is unavailable or deleted its pairing record, repeated 6-second pages keep
// the controller hidden from the pairing UI. Fall back to a full discoverable
// window after the first miss, then retry in the background.
constexpr uint8_t kMaxImmediateReconnectAttempts = 2;
// HIDSSR values are expressed in 0.625 ms baseband slots. Advertising 0xffff
// told a host that tens of seconds of remote latency were acceptable. Keep the
// link in active mode and declare a 15 ms upper bound for hosts that still
// inspect the optional HID 1.1 sniff-subrating attributes.
constexpr uint16_t kHidSsrHostMaxLatencySlots = 24;
constexpr uint16_t kHidSsrHostMinTimeoutSlots = 24;
#ifdef XHLS_CLASSIC_SHORT_REPORT_DIAGNOSTIC
constexpr bool kClassicShortReportDiagnostic = true;
#else
constexpr bool kClassicShortReportDiagnostic = false;
#endif
#ifndef XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ
#define XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ 0
#endif
constexpr uint32_t kClassicReportRateLimitHz =
    XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ;
constexpr uint64_t kClassicReportMinPeriodUs =
    kClassicReportRateLimitHz == 0
        ? 0
        : 1000000u / kClassicReportRateLimitHz;

struct State {
    bridge::Mode mode = bridge::Mode::DualSense;
    uint16_t hid_cid = 0;
    uint16_t connecting_hid_cid = 0;
    bool send_pending = false;
    bool send_call_active = false;
    bool peer_valid = false;
    bool peer_bonded = false;
    bool pairing_completed = false;
    // Windows owns the first HID service connection after creating a new
    // bond. Starting an outgoing page, or timing out the incoming ACL, races
    // Windows' post-pair SDP/HID setup and leaves a device paired but unusable.
    bool awaiting_first_hid_after_pairing = false;
    bool reconnect_pending = false;
    bool reconnect_in_progress = false;
    bool reconnect_abort_pending = false;
    bool forget_requested = false;
    bool stale_key_recovery_requested = false;
    bool pairing_reopen_requested = false;
    bool scan_enabled = false;
    uint32_t reconnect_attempts = 0;
    uint64_t reconnect_at_us = 0;
    uint64_t reconnect_started_us = 0;
    uint64_t reconnect_acl_started_us = 0;
    uint64_t incoming_started_us = 0;
    // Track the ACL created by either an incoming page or our active HID
    // reconnect. Without this handle an HID timeout can leave a half-open ACL
    // behind, and the next attempt races the previous one.
    hci_con_handle_t pending_acl_handle = HCI_CON_HANDLE_INVALID;
    hci_con_handle_t active_acl_handle = HCI_CON_HANDLE_INVALID;
    uint32_t last_generation = 0;
    uint64_t last_send_us = 0;
    uint64_t last_request_us = 0;
    uint64_t last_callback_us = 0;
    uint64_t next_switch_report_us = 0;
    uint64_t next_sony_report_us = 0;
    uint64_t last_telemetry_us = 0;
    uint64_t request_wait_total_us = 0;
    uint64_t request_wait_max_us = 0;
    uint64_t callback_gap_max_us = 0;
    uint32_t tx_count = 0;
    uint32_t request_count = 0;
    uint32_t callback_count = 0;
    uint32_t inline_callbacks = 0;
    uint32_t hci_completed_events = 0;
    uint32_t hci_completed_packets = 0;
    uint32_t acl_wait_ready_samples = 0;
    uint32_t acl_wait_blocked_samples = 0;
    int8_t latest_rssi_dbm = 127;
    bool rssi_valid = false;
    uint8_t link_mode = ACL_CONNECTION_MODE_ACTIVE;
    bool link_mode_observed = false;
    uint8_t link_max_slots = 0;
    bool link_max_slots_observed = false;
    uint16_t link_packet_types = 0;
    bool link_packet_types_observed = false;
    uint32_t skipped_source = 0;
    uint32_t output_count = 0;
    uint32_t rumble_count = 0;
    uint32_t output_set_callbacks = 0;
    uint32_t output_data_callbacks = 0;
    uint32_t output_ignored = 0;
    uint32_t output_invalid = 0;
    uint32_t output_no_motor_update = 0;
    uint32_t rumble_release_count = 0;
    uint32_t switch_reply_drops = 0;
    uint32_t output_trace_count = 0;
    bool compatible_rumble_active = false;
    uint8_t last_rumble_weak = 0;
    uint8_t last_rumble_strong = 0;
    std::array<uint8_t, 6> address{};
    bd_addr_t peer{};
    switch1::RuntimeState switch_runtime{};
    std::array<switch1::Report, kSwitchReplyQueueSize> switch_replies{};
    uint8_t switch_reply_head = 0;
    uint8_t switch_reply_count = 0;
    dualsense::InputReport last_dualsense_input{};
};

State g_state;
btstack_packet_callback_registration_t g_hci_callback;
btstack_packet_callback_registration_t g_l2cap_audit_callback;
std::array<uint8_t, 1100> g_hid_service{};
std::array<uint8_t, 220> g_pnp_service{};

bool is_sony() {
    return g_state.mode == bridge::Mode::DualSense ||
           g_state.mode == bridge::Mode::DualSenseEdge;
}

bool is_edge() {
    return g_state.mode == bridge::Mode::DualSenseEdge;
}

const char *link_mode_name(uint8_t mode) {
    switch (mode) {
    case ACL_CONNECTION_MODE_ACTIVE:
        return "active";
    case ACL_CONNECTION_MODE_HOLD:
        return "hold";
    case ACL_CONNECTION_MODE_SNIFF:
        return "sniff";
    default:
        return "unknown";
    }
}

uint64_t initial_reconnect_delay_us() {
    return is_sony() ? kInitialReconnectDelayUs
                     : kSwitchInitialReconnectDelayUs;
}

void stop_host_rumble(const char *reason) {
    const bool was_active = g_state.compatible_rumble_active;
    pro2_host::stop_rumble();
    g_state.compatible_rumble_active = false;
    g_state.last_rumble_weak = 0;
    g_state.last_rumble_strong = 0;
    if (was_active) {
        std::printf(
            "[BT_CLASSIC_RUMBLE] action=stop reason=%s\n", reason);
    }
}

void reset_switch_reply_queue() {
    g_state.switch_reply_head = 0;
    g_state.switch_reply_count = 0;
}

bool enqueue_switch_reply(const switch1::Report &reply) {
    if (g_state.switch_reply_count >= kSwitchReplyQueueSize) {
        ++g_state.switch_reply_drops;
        std::printf(
            "[SWITCH_HANDSHAKE] reply_queue_full subcommand=%02x "
            "drops=%lu\n",
            reply[15],
            static_cast<unsigned long>(g_state.switch_reply_drops));
        return false;
    }
    const size_t slot =
        (g_state.switch_reply_head + g_state.switch_reply_count) %
        kSwitchReplyQueueSize;
    g_state.switch_replies[slot] = reply;
    ++g_state.switch_reply_count;
    return true;
}

const char *link_phase_name() {
    if (g_state.hid_cid != 0) {
        return "connected";
    }
    if (g_state.reconnect_abort_pending || g_state.forget_requested ||
        g_state.stale_key_recovery_requested ||
        g_state.pairing_reopen_requested) {
        return "cleanup";
    }
    if (g_state.reconnect_in_progress) {
        return g_state.pending_acl_handle == HCI_CON_HANDLE_INVALID
                   ? "active_page"
                   : "active_hid_open";
    }
    if (g_state.incoming_started_us != 0) {
        return "incoming_hid_open";
    }
    if (g_state.reconnect_pending) {
        return "passive_window";
    }
    return g_state.scan_enabled ? "discoverable" : "idle";
}

const char *hci_status_name(uint8_t status) {
    switch (status) {
    case ERROR_CODE_SUCCESS:
        return "success";
    case ERROR_CODE_PAGE_TIMEOUT:
        return "page_timeout";
    case ERROR_CODE_AUTHENTICATION_FAILURE:
        return "authentication_failure";
    case ERROR_CODE_PIN_OR_KEY_MISSING:
        return "pin_or_key_missing";
    case ERROR_CODE_CONNECTION_TIMEOUT:
        return "connection_timeout";
    case ERROR_CODE_COMMAND_DISALLOWED:
        return "command_disallowed";
    case ERROR_CODE_REMOTE_USER_TERMINATED_CONNECTION:
        return "remote_user_terminated";
    case ERROR_CODE_CONNECTION_TERMINATED_BY_LOCAL_HOST:
        return "local_host_terminated";
    case BTSTACK_MEMORY_ALLOC_FAILED:
        return "btstack_memory_alloc_failed";
    default:
        return "other";
    }
}

void set_scan_enabled(bool enabled, const char *reason, bool force = false) {
    if (!force && g_state.scan_enabled == enabled) {
        return;
    }
    gap_discoverable_control(enabled ? 1 : 0);
    gap_connectable_control(enabled ? 1 : 0);
    g_state.scan_enabled = enabled;
    std::printf(
        "[BT_CLASSIC_SCAN] discoverable=%u connectable=%u reason=%s\n",
        enabled, enabled, reason);
}

int accept_classic_connection(bd_addr_t address,
                              hci_link_type_t link_type) {
    if (link_type != HCI_LINK_TYPE_ACL) {
        return 0;
    }
    if (g_state.hid_cid != 0) {
        std::printf(
            "[BT_CLASSIC_FILTER] reject peer=%s reason=host_already_active\n",
            bd_addr_to_str(address));
        return 0;
    }
    if (g_state.peer_valid &&
        bd_addr_cmp(address, g_state.peer) != 0) {
        std::printf(
            "[BT_CLASSIC_FILTER] reject peer=%s "
            "reason=single_host_bond\n",
            bd_addr_to_str(address));
        return 0;
    }
    return 1;
}

void schedule_hid_reconnect(uint64_t delay_us, const char *reason) {
    if (!g_state.peer_valid || g_state.hid_cid != 0 ||
        g_state.reconnect_in_progress) {
        return;
    }
    set_scan_enabled(true, "passive_reconnect_window");
    g_state.reconnect_pending = true;
    g_state.reconnect_at_us = time_us_64() + delay_us;
    std::printf(
        "[BT_CLASSIC_RECONNECT] passive_window peer=%s delay_ms=%lu "
        "then=active_fallback reason=%s\n",
        bd_addr_to_str(g_state.peer),
        static_cast<unsigned long>(delay_us / 1000), reason);
}

void schedule_next_hid_reconnect(const char *reason) {
    const bool immediate =
        g_state.reconnect_attempts < kMaxImmediateReconnectAttempts;
    schedule_hid_reconnect(
        immediate ? kReconnectRetryDelayUs
                  : kBackgroundReconnectDelayUs,
        reason);
}

bool load_last_bonded_peer() {
    g_state.peer_valid = false;
    g_state.peer_bonded = false;
    btstack_link_key_iterator_t iterator{};
    if (!gap_link_key_iterator_init(&iterator)) {
        std::printf(
            "[BT_CLASSIC_BOND] link key iterator unavailable\n");
        return false;
    }

    bd_addr_t address;
    link_key_t link_key;
    link_key_type_t type;
    bool found = false;
    uint32_t key_count = 0;
    while (gap_link_key_iterator_get_next(
        &iterator, address, link_key, &type)) {
        ++key_count;
        if (!found) {
            std::copy(address, address + 6, g_state.peer);
            g_state.peer_valid = true;
            g_state.peer_bonded = true;
            found = true;
        }
        std::printf(
            "[BT_CLASSIC_BOND] found peer=%s key_type=%u\n",
            bd_addr_to_str(address), static_cast<unsigned>(type));
    }
    gap_link_key_iterator_done(&iterator);
    std::printf(
        "[BT_CLASSIC_BOND] mode=%s key_count=%lu selected_peer=%s "
        "selection=first_in_current_mode_bank\n",
        bridge::mode_name(g_state.mode),
        static_cast<unsigned long>(key_count),
        found ? bd_addr_to_str(g_state.peer) : "none");
    return found;
}

void clear_link_attempt_state(bool reset_attempt_count) {
    g_state.reconnect_pending = false;
    g_state.reconnect_in_progress = false;
    if (reset_attempt_count) {
        g_state.reconnect_attempts = 0;
    }
    g_state.reconnect_at_us = 0;
    g_state.reconnect_started_us = 0;
    g_state.reconnect_acl_started_us = 0;
    g_state.reconnect_abort_pending = false;
    g_state.connecting_hid_cid = 0;
    g_state.incoming_started_us = 0;
    g_state.pending_acl_handle = HCI_CON_HANDLE_INVALID;
}

void complete_forget_bonds(const char *reason) {
    gap_delete_all_link_keys();
    clear_link_attempt_state(true);
    g_state.hid_cid = 0;
    g_state.active_acl_handle = HCI_CON_HANDLE_INVALID;
    g_state.peer_valid = false;
    g_state.peer_bonded = false;
    g_state.pairing_completed = false;
    g_state.awaiting_first_hid_after_pairing = false;
    g_state.forget_requested = false;
    g_state.stale_key_recovery_requested = false;
    g_state.pairing_reopen_requested = false;
    g_state.send_pending = false;
    reset_switch_reply_queue();
    stop_host_rumble("bonds_cleared");
    set_scan_enabled(true, reason, true);
    std::printf(
        "[BT_CLASSIC_BOND] all link keys cleared reason=%s\n", reason);
}

void complete_pairing_reopen(const char *reason) {
    clear_link_attempt_state(true);
    g_state.active_acl_handle = HCI_CON_HANDLE_INVALID;
    g_state.peer_valid = false;
    g_state.peer_bonded = false;
    g_state.pairing_completed = false;
    g_state.awaiting_first_hid_after_pairing = false;
    g_state.pairing_reopen_requested = false;
    set_scan_enabled(true, reason, true);
    std::printf(
        "[BT_CLASSIC_RECOVERY] pairing_reopened reason=%s\n", reason);
}

void complete_stale_link_key_recovery(const char *reason) {
    bd_addr_t stale_peer;
    std::copy(g_state.peer, g_state.peer + 6, stale_peer);
    clear_link_attempt_state(true);
    g_state.active_acl_handle = HCI_CON_HANDLE_INVALID;
    g_state.peer_valid = false;
    g_state.peer_bonded = false;
    g_state.pairing_completed = false;
    g_state.awaiting_first_hid_after_pairing = false;
    g_state.stale_key_recovery_requested = false;
    std::printf(
        "[BT_CLASSIC_BOND] stale link cleanup completed peer=%s "
        "reason=%s\n",
        bd_addr_to_str(stale_peer), reason);
    if (load_last_bonded_peer()) {
        schedule_hid_reconnect(
            initial_reconnect_delay_us(), "next_stored_link_key");
    } else {
        set_scan_enabled(true, "pairing_reopened", true);
        std::printf(
            "[BT_CLASSIC_BOND] no valid key remains; "
            "pairing reopened\n");
    }
}

void reopen_pairing_after_unbonded_failure(const char *reason,
                                           const bd_addr_t failed_peer,
                                           bool drop_partial_key) {
    bd_addr_t address;
    const bool address_valid = failed_peer != nullptr;
    if (address_valid) {
        std::copy(failed_peer, failed_peer + 6, address);
    } else if (g_state.peer_valid) {
        std::copy(g_state.peer, g_state.peer + 6, address);
    }
    const bool had_address = address_valid || g_state.peer_valid;

    if (drop_partial_key && had_address) {
        gap_drop_link_key_for_bd_addr(address);
    }
    g_state.peer_bonded = false;
    g_state.pairing_completed = false;
    g_state.awaiting_first_hid_after_pairing = false;
    g_state.reconnect_pending = false;
    g_state.pairing_reopen_requested = true;
    const hci_con_handle_t handle =
        g_state.pending_acl_handle != HCI_CON_HANDLE_INVALID
            ? g_state.pending_acl_handle
            : g_state.active_acl_handle;
    if (handle != HCI_CON_HANDLE_INVALID) {
        g_state.reconnect_abort_pending = true;
        set_scan_enabled(false, "pairing_reopen_waiting_for_acl", true);
        std::printf(
            "[BT_CLASSIC_RECOVERY] pairing_reopen deferred reason=%s "
            "failed_peer=%s handle=%04x partial_key_dropped=%u\n",
            reason, had_address ? bd_addr_to_str(address) : "none",
            handle, drop_partial_key && had_address);
        gap_disconnect(handle);
        return;
    }
    if (g_state.reconnect_in_progress) {
        set_scan_enabled(false, "pairing_reopen_waiting_for_page", true);
        std::printf(
            "[BT_CLASSIC_RECOVERY] pairing_reopen deferred reason=%s "
            "failed_peer=%s pending_cid=%04x partial_key_dropped=%u\n",
            reason, had_address ? bd_addr_to_str(address) : "none",
            g_state.connecting_hid_cid, drop_partial_key && had_address);
        return;
    }
    complete_pairing_reopen(reason);
    std::printf(
        "[BT_CLASSIC_RECOVERY] pairing_reopen_context "
        "failed_peer=%s partial_key_dropped=%u\n",
        had_address ? bd_addr_to_str(address) : "none",
        drop_partial_key && had_address);
}

void retry_bonded_peer_or_reopen(const char *reason) {
    if (g_state.forget_requested) {
        complete_forget_bonds("deferred_forget_completed");
        return;
    }
    if (g_state.stale_key_recovery_requested) {
        complete_stale_link_key_recovery(reason);
        return;
    }
    if (g_state.pairing_reopen_requested) {
        complete_pairing_reopen(reason);
        return;
    }
    if (classic_reconnect_policy::closed_acl_action(
            g_state.awaiting_first_hid_after_pairing) ==
        classic_reconnect_policy::ClosedAclAction::WaitForHostHid) {
        clear_link_attempt_state(false);
        set_scan_enabled(true, "post_pair_host_owned", true);
        std::printf(
            "[BT_CLASSIC_RECONNECT] host_owned_first_hid peer=%s "
            "reason=%s action=wait_for_incoming_hid\n",
            bd_addr_to_str(g_state.peer), reason);
        return;
    }
    // HID and ACL layers can report the same failed page independently. Once
    // one layer has scheduled the next passive window, do not cancel and
    // schedule it again when the companion event arrives.
    if (g_state.reconnect_pending && !g_state.reconnect_in_progress &&
        g_state.hid_cid == 0) {
        std::printf(
            "[BT_CLASSIC_RECONNECT] duplicate_failure_ignored reason=%s "
            "attempts=%lu\n",
            reason, static_cast<unsigned long>(g_state.reconnect_attempts));
        return;
    }
    const bool was_bonded = g_state.peer_bonded;
    clear_link_attempt_state(false);
    if (was_bonded && g_state.peer_valid) {
        set_scan_enabled(true, "bonded_retry_passive_window", true);
        schedule_next_hid_reconnect(reason);
        return;
    }
    reopen_pairing_after_unbonded_failure(reason, nullptr, false);
}

void recover_from_stale_link_key(const char *reason) {
    bd_addr_t stale_peer;
    std::copy(g_state.peer, g_state.peer + 6, stale_peer);
    gap_drop_link_key_for_bd_addr(stale_peer);
    g_state.peer_bonded = false;
    g_state.pairing_completed = false;
    g_state.awaiting_first_hid_after_pairing = false;
    g_state.reconnect_pending = false;
    g_state.stale_key_recovery_requested = true;
    set_scan_enabled(false, "stale_key_waiting_for_link_cleanup", true);
    std::printf(
        "[BT_CLASSIC_BOND] stale link key removed peer=%s "
        "reason=%s\n",
        bd_addr_to_str(stale_peer), reason);
    const hci_con_handle_t handle =
        g_state.pending_acl_handle != HCI_CON_HANDLE_INVALID
            ? g_state.pending_acl_handle
            : g_state.active_acl_handle;
    if (handle != HCI_CON_HANDLE_INVALID) {
        g_state.reconnect_abort_pending = true;
        std::printf(
            "[BT_CLASSIC_BOND] stale recovery deferred phase=acl_disconnect "
            "handle=%04x\n",
            handle);
        gap_disconnect(handle);
        return;
    }
    if (g_state.reconnect_in_progress) {
        std::printf(
            "[BT_CLASSIC_BOND] stale recovery deferred "
            "phase=btstack_page_result pending_cid=%04x\n",
            g_state.connecting_hid_cid);
        return;
    }
    complete_stale_link_key_recovery(reason);
}

void apply_rumble(const pro2_rumble::XboxRumbleCommand &rumble) {
    ++g_state.output_count;
    if (pro2_rumble::active(rumble)) {
        ++g_state.rumble_count;
    }
    pro2_host::set_rumble(rumble);
}

void handle_output(uint16_t report_id, const uint8_t *body,
                   size_t body_size, const char *source) {
    if (is_sony()) {
        if (report_id == dualsense::kOutputReportId) {
            const auto decoded =
                dualsense::decode_output_report(body, body_size);
            const auto &rumble = decoded.rumble;
            const bool motor_active_before =
                g_state.compatible_rumble_active;
            const auto transition = dualsense::resolve_motor_transition(
                decoded, motor_active_before);
            const bool changed =
                rumble.weak != g_state.last_rumble_weak ||
                rumble.strong != g_state.last_rumble_strong;
            if (g_state.output_trace_count < 8 ||
                transition ==
                    dualsense::MotorTransition::ReleaseCompatibleMotor ||
                (transition == dualsense::MotorTransition::ApplyUpdate &&
                 changed)) {
                const uint8_t flag0 =
                    body != nullptr && body_size > 2 ? body[2] : 0;
                const uint8_t flag1 =
                    body != nullptr && body_size > 3 ? body[3] : 0;
                const uint8_t flag2 =
                    body != nullptr && body_size > 40 ? body[40] : 0;
                std::printf(
                    "[BT_CLASSIC_OUTPUT] source=%s report_id=%02x "
                    "body_len=%u flag0=%02x flag1=%02x flag2=%02x "
                    "action=%s transition=%s active_before=%u "
                    "crc_valid=%u compatible_v2=%u "
                    "weak=%u strong=%u "
                    "prefix=%02x,%02x,%02x,%02x,%02x,%02x\n",
                    source, report_id, static_cast<unsigned>(body_size),
                    flag0, flag1, flag2,
                    dualsense::output_action_name(decoded.action),
                    dualsense::motor_transition_name(transition),
                    motor_active_before,
                    decoded.crc_valid, decoded.compatible_v2,
                    rumble.weak, rumble.strong,
                    body != nullptr && body_size > 0 ? body[0] : 0,
                    body != nullptr && body_size > 1 ? body[1] : 0,
                    body != nullptr && body_size > 2 ? body[2] : 0,
                    body != nullptr && body_size > 3 ? body[3] : 0,
                    body != nullptr && body_size > 4 ? body[4] : 0,
                    body != nullptr && body_size > 5 ? body[5] : 0);
                ++g_state.output_trace_count;
            }
            if (transition == dualsense::MotorTransition::ApplyUpdate) {
                g_state.last_rumble_weak = rumble.weak;
                g_state.last_rumble_strong = rumble.strong;
                g_state.compatible_rumble_active =
                    pro2_rumble::active(rumble);
                apply_rumble(rumble);
            } else if (
                transition ==
                dualsense::MotorTransition::ReleaseCompatibleMotor) {
                ++g_state.rumble_release_count;
                stop_host_rumble("compatible_mode_released");
            } else {
                ++g_state.output_ignored;
                if (decoded.action == dualsense::OutputAction::Invalid) {
                    ++g_state.output_invalid;
                } else {
                    ++g_state.output_no_motor_update;
                }
            }
        } else {
            ++g_state.output_ignored;
            ++g_state.output_invalid;
        }
        return;
    }

    if (report_id == 0x01 || report_id == 0x10 ||
        report_id == 0x11) {
        const auto rumble = switch1::decode_rumble(body, body_size);
        const bool active = pro2_rumble::active(rumble);
        if (g_state.output_trace_count < 16 || active) {
            std::printf(
                "[SWITCH_OUTPUT] source=%s report_id=%02x body_len=%u "
                "vibration_enabled=%u decoded_active=%u left=%u right=%u "
                "prefix=%02x,%02x,%02x,%02x,%02x,%02x,%02x,%02x,%02x\n",
                source, report_id, static_cast<unsigned>(body_size),
                g_state.switch_runtime.vibration_enabled, active,
                rumble.left_trigger, rumble.right_trigger,
                body != nullptr && body_size > 0 ? body[0] : 0,
                body != nullptr && body_size > 1 ? body[1] : 0,
                body != nullptr && body_size > 2 ? body[2] : 0,
                body != nullptr && body_size > 3 ? body[3] : 0,
                body != nullptr && body_size > 4 ? body[4] : 0,
                body != nullptr && body_size > 5 ? body[5] : 0,
                body != nullptr && body_size > 6 ? body[6] : 0,
                body != nullptr && body_size > 7 ? body[7] : 0,
                body != nullptr && body_size > 8 ? body[8] : 0);
            ++g_state.output_trace_count;
        }
        if (g_state.switch_runtime.vibration_enabled) {
            apply_rumble(rumble);
        }
        if (report_id == 0x01) {
            pro2::InputState input;
            uint32_t generation = 0;
            const bool have_source =
                bridge::read_latest_input(&input, &generation);
            const uint8_t input_mode_before =
                g_state.switch_runtime.input_mode;
            switch1::Report reply{};
            if (switch1::make_subcommand_reply(
                    body, body_size, g_state.address,
                    have_source ? &input : nullptr,
                    &g_state.switch_runtime, &reply)) {
                const bool queued = enqueue_switch_reply(reply);
                g_state.next_switch_report_us = time_us_64();
                std::printf(
                    "[SWITCH_HANDSHAKE] subcommand=%02x ack=%02x "
                    "input_mode=%02x->%02x imu=%u vibration=%u "
                    "reply=%s queue_depth=%u\n",
                    body_size > 9 ? body[9] : 0xff,
                    reply[14], input_mode_before,
                    g_state.switch_runtime.input_mode,
                    g_state.switch_runtime.imu_enabled,
                    g_state.switch_runtime.vibration_enabled,
                    queued ? "queued" : "dropped",
                    g_state.switch_reply_count);
            }
        }
    }
}

void report_data_callback(uint16_t, hid_report_type_t, uint16_t report_id,
                          int report_size, uint8_t *report) {
    ++g_state.output_data_callbacks;
    handle_output(report_id, report,
                  report_size > 0 ? static_cast<size_t>(report_size) : 0,
                  "interrupt_data");
}

void set_report_callback(uint16_t, hid_report_type_t, int report_size,
                         uint8_t *report) {
    if (report == nullptr || report_size <= 0) {
        return;
    }
    ++g_state.output_set_callbacks;
    handle_output(report[0], report + 1,
                  static_cast<size_t>(report_size - 1), "set_report");
}

int get_report_callback(uint16_t, hid_report_type_t report_type,
                        uint16_t report_id, int *out_report_size,
                        uint8_t *out_report) {
    if (out_report_size == nullptr || out_report == nullptr) {
        return 0;
    }
    if (is_sony() && report_type == HID_REPORT_TYPE_FEATURE) {
        const size_t size = dualsense::make_feature_report(
            static_cast<uint8_t>(report_id), g_state.address, is_edge(),
            out_report, 512);
        *out_report_size = static_cast<int>(size);
        return size > 0 ? 1 : 0;
    }
    if (is_sony() && report_type == HID_REPORT_TYPE_INPUT &&
        report_id == dualsense::kInputReportId) {
        std::copy(g_state.last_dualsense_input.begin() + 1,
                  g_state.last_dualsense_input.end(), out_report);
        *out_report_size =
            static_cast<int>(g_state.last_dualsense_input.size() - 1);
        return 1;
    }
    *out_report_size = 0;
    return 0;
}

void send_now() {
    const uint64_t now = time_us_64();
    ++g_state.callback_count;
    if (g_state.send_call_active) {
        ++g_state.inline_callbacks;
    }
    if (g_state.last_request_us != 0 && now >= g_state.last_request_us) {
        const uint64_t wait_us = now - g_state.last_request_us;
        g_state.request_wait_total_us += wait_us;
        if (wait_us > g_state.request_wait_max_us) {
            g_state.request_wait_max_us = wait_us;
        }
    }
    if (g_state.last_callback_us != 0 && now >= g_state.last_callback_us) {
        const uint64_t gap_us = now - g_state.last_callback_us;
        if (gap_us > g_state.callback_gap_max_us) {
            g_state.callback_gap_max_us = gap_us;
        }
    }
    g_state.last_callback_us = now;
    g_state.send_pending = false;
    if (g_state.hid_cid == 0) {
        return;
    }

    if (!is_sony() && g_state.switch_reply_count != 0) {
        const switch1::Report &reply =
            g_state.switch_replies[g_state.switch_reply_head];
        hid_device_send_interrupt_message(
            g_state.hid_cid, reply.data(),
            static_cast<uint16_t>(reply.size()));
        g_state.switch_reply_head = static_cast<uint8_t>(
            (g_state.switch_reply_head + 1) % kSwitchReplyQueueSize);
        --g_state.switch_reply_count;
        ++g_state.tx_count;
        g_state.last_send_us = now;
        return;
    }

    pro2::InputState source;
    uint32_t generation = 0;
    if (is_sony()) {
        const bool have_source =
            bridge::read_latest_input(&source, &generation);
        if (have_source) {
            g_state.last_dualsense_input =
                dualsense::make_input_report(
                    source, is_edge(),
                    static_cast<uint8_t>(source.sequence));
        } else {
            g_state.last_dualsense_input =
                dualsense::make_neutral_report(is_edge());
        }
        if (kClassicShortReportDiagnostic) {
            // The descriptor's Report 1 carries four sticks, three bytes of
            // hat/buttons, and two trigger axes. This diagnostic keeps the
            // same live source while reducing the over-air HID message from
            // 79 to 11 bytes, isolating payload-size scheduling from the
            // L2CAP/HID connection itself.
            std::array<uint8_t, 11> message{};
            message[0] = 0xa1;
            message[1] = 0x01;
            std::copy_n(g_state.last_dualsense_input.begin() + 2, 4,
                        message.begin() + 2);
            std::copy_n(g_state.last_dualsense_input.begin() + 9, 3,
                        message.begin() + 6);
            message[9] = g_state.last_dualsense_input[6];
            message[10] = g_state.last_dualsense_input[7];
            hid_device_send_interrupt_message(
                g_state.hid_cid, message.data(),
                static_cast<uint16_t>(message.size()));
        } else {
            std::array<uint8_t, dualsense::kInputReportSize + 1> message{};
            message[0] = 0xa1;
            std::copy(g_state.last_dualsense_input.begin(),
                      g_state.last_dualsense_input.end(),
                      message.begin() + 1);
            hid_device_send_interrupt_message(
                g_state.hid_cid, message.data(),
                static_cast<uint16_t>(message.size()));
        }
        if (have_source) {
            if (g_state.last_generation != 0 &&
                generation > g_state.last_generation + 1) {
                g_state.skipped_source +=
                    generation - g_state.last_generation - 1;
            }
            g_state.last_generation = generation;
        }
    } else {
        if (g_state.switch_runtime.input_mode == 0x3f) {
            const bool have_source =
                bridge::read_latest_input(&source, &generation);
            const switch1::SimpleReport message =
                switch1::make_simple_input_report(
                    have_source ? source : pro2::InputState{});
            hid_device_send_interrupt_message(
                g_state.hid_cid, message.data(),
                static_cast<uint16_t>(message.size()));
            if (have_source) {
                g_state.last_generation = generation;
            }
        } else {
            std::array<pro2::InputState, 3> samples{};
            const size_t count =
                bridge::read_resampled_inputs(
                    samples.data(), nullptr, samples.size(),
                    kSwitchImuSamplePeriodUs);
            const switch1::Report message =
                switch1::make_input_report(samples.data(), count,
                                           g_state.switch_runtime);
            hid_device_send_interrupt_message(
                g_state.hid_cid, message.data(),
                static_cast<uint16_t>(message.size()));
            if (count > 0) {
                g_state.last_generation = samples[count - 1].sequence;
            }
        }
    }
    ++g_state.tx_count;
    g_state.last_send_us = now;
    if (is_sony() && kClassicReportMinPeriodUs != 0) {
        g_state.next_sony_report_us = now + kClassicReportMinPeriodUs;
    }
}

void request_send() {
    if (g_state.hid_cid == 0 || g_state.send_pending) {
        return;
    }
    g_state.send_pending = true;
    g_state.send_call_active = true;
    ++g_state.request_count;
    g_state.last_request_us = time_us_64();
    hid_device_request_can_send_now_event(g_state.hid_cid);
    g_state.send_call_active = false;
}

void l2cap_audit_handler(uint8_t packet_type, uint16_t, uint8_t *packet,
                         uint16_t) {
    if (packet_type != HCI_EVENT_PACKET) {
        return;
    }
    switch (hci_event_packet_get_type(packet)) {
    case L2CAP_EVENT_INCOMING_CONNECTION: {
        bd_addr_t address;
        l2cap_event_incoming_connection_get_address(packet, address);
        std::printf(
            "[BT_CLASSIC_L2CAP] incoming peer=%s psm=%04x cid=%04x "
            "handle=%04x\n",
            bd_addr_to_str(address),
            l2cap_event_incoming_connection_get_psm(packet),
            l2cap_event_incoming_connection_get_local_cid(packet),
            l2cap_event_incoming_connection_get_handle(packet));
        break;
    }
    case L2CAP_EVENT_CHANNEL_OPENED: {
        bd_addr_t address;
        l2cap_event_channel_opened_get_address(packet, address);
        std::printf(
            "[BT_CLASSIC_L2CAP] opened status=%02x peer=%s psm=%04x "
            "cid=%04x mtu=%u\n",
            l2cap_event_channel_opened_get_status(packet),
            bd_addr_to_str(address),
            l2cap_event_channel_opened_get_psm(packet),
            l2cap_event_channel_opened_get_local_cid(packet),
            l2cap_event_channel_opened_get_remote_mtu(packet));
        break;
    }
    case L2CAP_EVENT_CHANNEL_CLOSED:
        std::printf("[BT_CLASSIC_L2CAP] closed cid=%04x\n",
                    l2cap_event_channel_closed_get_local_cid(packet));
        break;
    default:
        break;
    }
}

void packet_handler(uint8_t packet_type, uint16_t, uint8_t *packet,
                    uint16_t packet_size) {
    if (packet_type != HCI_EVENT_PACKET) {
        return;
    }
    switch (hci_event_packet_get_type(packet)) {
    case BTSTACK_EVENT_STATE:
        if (btstack_event_state_get_state(packet) == HCI_STATE_WORKING) {
            bd_addr_t address;
            gap_local_bd_addr(address);
            // Re-assert both scan modes after the controller is fully
            // initialized. This avoids a stale inquiry-only result in Windows
            // when the CYW43 transport rewrites its address during startup.
            set_scan_enabled(true, "controller_ready", true);
            std::printf(
                "[BT_CLASSIC_READY] mode=%s addr=%s identity=\"%s\"\n",
                bridge::mode_name(g_state.mode), bd_addr_to_str(address),
                bridge::mode_display_name(g_state.mode));
            std::printf(
                "[BT_CLASSIC_POLICY] incoming_first_ms=%lu "
                "incoming_hid_timeout=host_managed "
                "page_timeout=btstack_managed hid_after_acl_timeout_ms=%lu "
                "retry_ms=%lu background_ms=%lu "
                "fast_attempts=%u "
                "acl_payload_bytes=1021 packet_policy=controller_multislot "
                "hci_flow_control=controller_to_host:8 "
                "create_connection_packet_mask=%04x "
                "sniff_mode=disabled_active_only "
                "hid_ssr_slots={max_latency:%u,min_timeout:%u} "
                "qos=not_requested_controller_rejected_2c "
                "ssp=just_works ssp_auto_accept=on "
                "secure_connections_active=%u security_level=2 "
                "legacy_pin_fallback=0000 pairing_timeout=none\n",
                static_cast<unsigned long>(
                    initial_reconnect_delay_us() / 1000),
                static_cast<unsigned long>(
                    kHidOpenAfterAclTimeoutUs / 1000),
                static_cast<unsigned long>(kReconnectRetryDelayUs / 1000),
                static_cast<unsigned long>(kBackgroundReconnectDelayUs / 1000),
                kMaxImmediateReconnectAttempts,
                hci_usable_acl_packet_types(),
                kHidSsrHostMaxLatencySlots,
                kHidSsrHostMinTimeoutSlots,
                gap_secure_connections_active());
            if (mode_manager::pairing_recovery_required()) {
                gap_delete_all_link_keys();
                mode_manager::mark_pairing_recovery_applied();
                set_scan_enabled(true, "one_shot_pairing_recovery", true);
                std::printf(
                    "[BT_CLASSIC_BOND] one_shot_recovery=complete "
                    "mode=%s identity=\"%s\" stored_peer=none "
                    "discoverable=1 connectable=1\n",
                    bridge::mode_name(g_state.mode),
                    bridge::mode_display_name(g_state.mode));
                pairing_feedback::start("classic_pairing_recovery");
            } else if (load_last_bonded_peer()) {
                schedule_hid_reconnect(
                    initial_reconnect_delay_us(), "stored_link_key");
            } else {
                std::printf(
                    "[BT_CLASSIC_BOND] no stored peer; waiting for "
                    "first pairing\n");
                pairing_feedback::start("classic_no_stored_peer");
            }
        }
        break;
    case HCI_EVENT_NUMBER_OF_COMPLETED_PACKETS: {
        const uint8_t handle_count = packet[2];
        for (uint8_t i = 0; i < handle_count; ++i) {
            const int offset = 3 + static_cast<int>(i) * 4;
            const hci_con_handle_t handle =
                little_endian_read_16(packet, offset);
            if (handle != g_state.active_acl_handle &&
                handle != g_state.pending_acl_handle) {
                continue;
            }
            ++g_state.hci_completed_events;
            g_state.hci_completed_packets +=
                little_endian_read_16(packet, offset + 2);
        }
        break;
    }
    case GAP_EVENT_RSSI_MEASUREMENT:
        if (gap_event_rssi_measurement_get_con_handle(packet) ==
            g_state.active_acl_handle) {
            g_state.latest_rssi_dbm = static_cast<int8_t>(
                gap_event_rssi_measurement_get_rssi(packet));
            g_state.rssi_valid = true;
            std::printf(
                "[BT_CLASSIC_RADIO] handle=%04x rssi_dbm=%d\n",
                g_state.active_acl_handle, g_state.latest_rssi_dbm);
        }
        break;
    case HCI_EVENT_CONNECTION_REQUEST: {
        bd_addr_t address;
        hci_event_connection_request_get_bd_addr(packet, address);
        const bool peer_allowed =
            !g_state.peer_valid || bd_addr_cmp(address, g_state.peer) == 0;
        if (peer_allowed) {
            const bool known_bond =
                g_state.peer_bonded && g_state.peer_valid &&
                bd_addr_cmp(address, g_state.peer) == 0;
            std::copy(address, address + 6, g_state.peer);
            g_state.peer_valid = true;
            g_state.peer_bonded = known_bond;
            g_state.pairing_completed = known_bond;
            if (!known_bond) {
                g_state.awaiting_first_hid_after_pairing = false;
            }
            g_state.reconnect_pending = false;
        }
        std::printf(
            "[BT_CLASSIC_PAIR] connection_request peer=%s cod=%06lx "
            "link_type=%u allowed=%u policy=incoming_first\n",
            bd_addr_to_str(address),
            static_cast<unsigned long>(
                hci_event_connection_request_get_class_of_device(packet)),
            hci_event_connection_request_get_link_type(packet),
            peer_allowed);
        break;
    }
    case HCI_EVENT_CONNECTION_COMPLETE: {
        bd_addr_t address;
        hci_event_connection_complete_get_bd_addr(packet, address);
        const uint8_t status =
            hci_event_connection_complete_get_status(packet);
        const bool expected_peer =
            !g_state.peer_valid || bd_addr_cmp(address, g_state.peer) == 0;
        if (!expected_peer) {
            std::printf(
                "[BT_CLASSIC_FILTER] connection_complete ignored peer=%s "
                "status=%02x reason=unrelated_peer\n",
                bd_addr_to_str(address), status);
            break;
        }
        if (status == ERROR_CODE_SUCCESS && !g_state.reconnect_in_progress) {
            g_state.reconnect_pending = false;
            g_state.incoming_started_us = time_us_64();
            g_state.pending_acl_handle =
                hci_event_connection_complete_get_connection_handle(packet);
        } else if (status == ERROR_CODE_SUCCESS) {
            // Active HID reconnects also create an ACL before the HID control
            // and interrupt channels open. Keep its handle so a stalled
            // authentication can be cancelled cleanly before retrying.
            g_state.pending_acl_handle =
                hci_event_connection_complete_get_connection_handle(packet);
            g_state.reconnect_acl_started_us = time_us_64();
        } else if (status != ERROR_CODE_SUCCESS) {
            retry_bonded_peer_or_reopen("acl_open_failed");
        }
        const hci_con_handle_t handle =
            hci_event_connection_complete_get_connection_handle(packet);
        if (status == ERROR_CODE_SUCCESS && g_state.forget_requested) {
            g_state.reconnect_abort_pending = true;
            std::printf(
                "[BT_CLASSIC_BOND] page completed during forget; "
                "disconnecting handle=%04x\n",
                handle);
            gap_disconnect(handle);
        } else if (status == ERROR_CODE_SUCCESS) {
            // Ask for the remote feature pages immediately. Pairing logs can
            // then distinguish a host that really supports SSP from one that
            // has fallen back to the legacy PIN protocol.
            hci_remote_features_query(handle);
        }
        std::printf(
            "[BT_CLASSIC_PAIR] connection_complete status=%02x "
            "status_name=%s handle=%04x peer=%s encrypted=%u "
            "remote_features_query=%u\n",
            status, hci_status_name(status),
            handle,
            bd_addr_to_str(address),
            hci_event_connection_complete_get_encryption_enabled(packet),
            status == ERROR_CODE_SUCCESS);
        break;
    }
    case HCI_EVENT_PIN_CODE_REQUEST: {
        bd_addr_t address;
        hci_event_pin_code_request_get_bd_addr(packet, address);
        const bool stale_bond =
            g_state.peer_valid && g_state.peer_bonded &&
            bd_addr_cmp(address, g_state.peer) == 0;
        const hci_con_handle_t handle = g_state.pending_acl_handle;
        const bool remote_features_ready =
            handle != HCI_CON_HANDLE_INVALID &&
            hci_remote_features_available(handle);
        const bool ssp_supported_on_both_sides =
            handle != HCI_CON_HANDLE_INVALID &&
            gap_ssp_supported_on_both_sides(handle);
        if (stale_bond) {
            // A host that still owns the matching key does not restart legacy
            // PIN pairing. This means Windows removed or replaced its side of
            // the bond while the Pico retained its key. Repeating PIN 0000
            // only burns full timeout windows and leaves half-open ACLs.
            std::printf(
                "[BT_CLASSIC_PAIR] legacy_pin_request peer=%s "
                "response=reject reason=stale_local_bond handle=%04x "
                "remote_features_ready=%u ssp_both_sides=%u "
                "secure_connections_active=%u\n",
                bd_addr_to_str(address), handle,
                remote_features_ready, ssp_supported_on_both_sides,
                gap_secure_connections_active());
            gap_pin_code_negative(address);
            recover_from_stale_link_key(
                "bonded_peer_requested_legacy_pin");
            break;
        }
        std::printf(
            "[BT_CLASSIC_PAIR] legacy_pin_request peer=%s response=0000 "
            "reason=host_legacy_fallback handle=%04x "
            "remote_features_ready=%u ssp_both_sides=%u "
            "secure_connections_active=%u\n",
            bd_addr_to_str(address), handle,
            remote_features_ready, ssp_supported_on_both_sides,
            gap_secure_connections_active());
        gap_pin_code_response(address, "0000");
        break;
    }
    case HCI_EVENT_IO_CAPABILITY_REQUEST: {
        bd_addr_t address;
        hci_event_io_capability_request_get_bd_addr(packet, address);
        std::printf(
            "[BT_CLASSIC_PAIR] io_capability_request peer=%s "
            "local=no_input_no_output auth=general_bonding "
            "ssp_path=confirmed\n",
            bd_addr_to_str(address));
        break;
    }
    case HCI_EVENT_IO_CAPABILITY_RESPONSE: {
        bd_addr_t address;
        hci_event_io_capability_response_get_bd_addr(packet, address);
        std::printf(
            "[BT_CLASSIC_PAIR] io_capability_response peer=%s io=%u "
            "auth=%u\n",
            bd_addr_to_str(address),
            hci_event_io_capability_response_get_io_capability(packet),
            hci_event_io_capability_response_get_authentication_requirements(
                packet));
        break;
    }
    case HCI_EVENT_USER_CONFIRMATION_REQUEST: {
        bd_addr_t address;
        hci_event_user_confirmation_request_get_bd_addr(packet, address);
        std::printf(
            "[BT_CLASSIC_PAIR] user_confirmation peer=%s value=%06lu "
            "response=accept\n",
            bd_addr_to_str(address),
            static_cast<unsigned long>(
                hci_event_user_confirmation_request_get_numeric_value(
                    packet)));
        gap_ssp_confirmation_response(address);
        break;
    }
    case HCI_EVENT_SIMPLE_PAIRING_COMPLETE: {
        bd_addr_t address;
        hci_event_simple_pairing_complete_get_bd_addr(packet, address);
        const uint8_t status =
            hci_event_simple_pairing_complete_get_status(packet);
        if (status == ERROR_CODE_SUCCESS) {
            std::copy(address, address + 6, g_state.peer);
            g_state.peer_valid = true;
            g_state.peer_bonded = true;
            g_state.pairing_completed = true;
            g_state.awaiting_first_hid_after_pairing = true;
            // Pairing and encryption may consume several seconds. Preserve
            // the ACL so Windows can finish SDP discovery and open the HID
            // control/interrupt channels on its own schedule.
            g_state.incoming_started_us = time_us_64();
        } else if (g_state.hid_cid == 0) {
            reopen_pairing_after_unbonded_failure(
                "simple_pairing_failed", address, true);
        }
        std::printf(
            "[BT_CLASSIC_PAIR] simple_pairing_complete status=%02x peer=%s\n",
            status, bd_addr_to_str(address));
        break;
    }
    case HCI_EVENT_AUTHENTICATION_COMPLETE: {
        const uint8_t status =
            hci_event_authentication_complete_get_status(packet);
        std::printf(
            "[BT_CLASSIC_PAIR] authentication_complete status=%02x "
            "handle=%04x\n",
            status,
            hci_event_authentication_complete_get_connection_handle(packet));
        if (status != ERROR_CODE_SUCCESS && g_state.hid_cid == 0) {
            if (g_state.peer_valid &&
                (status == ERROR_CODE_PIN_OR_KEY_MISSING ||
                 status == ERROR_CODE_AUTHENTICATION_FAILURE)) {
                recover_from_stale_link_key(hci_status_name(status));
            } else {
                retry_bonded_peer_or_reopen("authentication_failed");
            }
        }
        break;
    }
    case HCI_EVENT_ENCRYPTION_CHANGE:
        std::printf(
            "[BT_CLASSIC_PAIR] encryption_change status=%02x handle=%04x "
            "enabled=%u\n",
            hci_event_encryption_change_get_status(packet),
            hci_event_encryption_change_get_connection_handle(packet),
            hci_event_encryption_change_get_encryption_enabled(packet));
        if (hci_event_encryption_change_get_status(packet) ==
                ERROR_CODE_SUCCESS &&
            g_state.hid_cid == 0 &&
            !g_state.reconnect_in_progress) {
            g_state.incoming_started_us = time_us_64();
        }
        break;
    case HCI_EVENT_ROLE_CHANGE: {
        bd_addr_t address;
        hci_event_role_change_get_bd_addr(packet, address);
        const uint8_t status = hci_event_role_change_get_status(packet);
        const hci_role_t role = static_cast<hci_role_t>(
            hci_event_role_change_get_role(packet));
        std::printf(
            "[BT_CLASSIC_ROLE] status=%02x status_name=%s peer=%s "
            "role=%s policy=host_role_switch_allowed\n",
            status, hci_status_name(status), bd_addr_to_str(address),
            role == HCI_ROLE_MASTER ? "master" : "slave");
        break;
    }
    case HCI_EVENT_MAX_SLOTS_CHANGED:
        if (hci_event_max_slots_changed_get_handle(packet) ==
            g_state.active_acl_handle) {
            g_state.link_max_slots =
                hci_event_max_slots_changed_get_lmp_max_slots(packet);
            g_state.link_max_slots_observed = true;
        }
        std::printf(
            "[BT_CLASSIC_PACKET] event=max_slots_changed handle=%04x "
            "max_slots=%u owned=%u\n",
            hci_event_max_slots_changed_get_handle(packet),
            hci_event_max_slots_changed_get_lmp_max_slots(packet),
            hci_event_max_slots_changed_get_handle(packet) ==
                g_state.active_acl_handle);
        break;
    case HCI_EVENT_CONNECTION_PACKET_TYPE_CHANGED:
        if (hci_event_connection_packet_type_changed_get_handle(packet) ==
            g_state.active_acl_handle) {
            g_state.link_packet_types =
                hci_event_connection_packet_type_changed_get_packet_types(
                    packet);
            g_state.link_packet_types_observed = true;
        }
        std::printf(
            "[BT_CLASSIC_PACKET] event=packet_type_changed status=%02x "
            "handle=%04x packet_types=%04x owned=%u\n",
            hci_event_connection_packet_type_changed_get_status(packet),
            hci_event_connection_packet_type_changed_get_handle(packet),
            hci_event_connection_packet_type_changed_get_packet_types(packet),
            hci_event_connection_packet_type_changed_get_handle(packet) ==
                g_state.active_acl_handle);
        break;
    case HCI_EVENT_MODE_CHANGE:
        g_state.link_mode = hci_event_mode_change_get_mode(packet);
        g_state.link_mode_observed = true;
        std::printf(
            "[BT_CLASSIC_LINK_MODE] status=%02x handle=%04x mode=%u "
            "mode_name=%s interval_slots=%u interval_ms=%.2f\n",
            hci_event_mode_change_get_status(packet),
            hci_event_mode_change_get_handle(packet),
            g_state.link_mode, link_mode_name(g_state.link_mode),
            hci_event_mode_change_get_interval(packet),
            hci_event_mode_change_get_interval(packet) * 0.625);
        if (g_state.link_mode == ACL_CONNECTION_MODE_SNIFF &&
            hci_event_mode_change_get_handle(packet) ==
                g_state.active_acl_handle) {
            const uint8_t status =
                gap_sniff_mode_exit(g_state.active_acl_handle);
            std::printf(
                "[BT_CLASSIC_LINK_MODE] action=exit_sniff status=%02x "
                "reason=active_gamepad_policy\n",
                status);
        }
        break;
    case HCI_EVENT_SNIFF_SUBRATING:
        std::printf(
            "[BT_CLASSIC_SSR] status=%02x handle=%04x "
            "max_tx_latency_slots=%u max_rx_latency_slots=%u "
            "min_remote_timeout_slots=%u min_local_timeout_slots=%u\n",
            hci_event_sniff_subrating_get_status(packet),
            hci_event_sniff_subrating_get_handle(packet),
            hci_event_sniff_subrating_get_max_tx_latency(packet),
            hci_event_sniff_subrating_get_max_rx_latency(packet),
            hci_event_sniff_subrating_get_min_remote_timeout(packet),
            hci_event_sniff_subrating_get_min_local_timeout(packet));
        break;
    case HCI_EVENT_DISCONNECTION_COMPLETE: {
        const hci_con_handle_t handle =
            hci_event_disconnection_complete_get_connection_handle(packet);
        const bool pending_match =
            handle == g_state.pending_acl_handle;
        const bool active_match = handle == g_state.active_acl_handle;
        std::printf(
            "[BT_CLASSIC_PAIR] acl_disconnected status=%02x handle=%04x "
            "reason=%02x reason_name=%s owned=%u pending=%u active=%u\n",
            hci_event_disconnection_complete_get_status(packet),
            handle,
            hci_event_disconnection_complete_get_reason(packet),
            hci_status_name(
                hci_event_disconnection_complete_get_reason(packet)),
            pending_match || active_match, pending_match, active_match);
        if (!pending_match && !active_match) {
            std::printf(
                "[BT_CLASSIC_FILTER] acl_disconnect ignored handle=%04x "
                "reason=unrelated_or_already_finalized\n",
                handle);
            break;
        }
        g_state.incoming_started_us = 0;
        if (pending_match) {
            g_state.pending_acl_handle = HCI_CON_HANDLE_INVALID;
        }
        if (active_match) {
            g_state.active_acl_handle = HCI_CON_HANDLE_INVALID;
            g_state.hid_cid = 0;
            g_state.send_pending = false;
            reset_switch_reply_queue();
            g_state.last_generation = 0;
            stop_host_rumble("acl_disconnected");
        }
        if (g_state.hid_cid == 0) {
            g_state.pairing_completed = false;
            retry_bonded_peer_or_reopen(
                g_state.peer_bonded ? "owned_acl_closed"
                                    : "acl_disconnected_unbonded");
        }
        break;
    }
    case HCI_EVENT_HID_META:
        switch (hci_event_hid_meta_get_subevent_code(packet)) {
        case HID_SUBEVENT_CONNECTION_OPENED: {
            const uint16_t event_cid =
                hid_subevent_connection_opened_get_hid_cid(packet);
            if (g_state.connecting_hid_cid != 0 &&
                event_cid != g_state.connecting_hid_cid) {
                std::printf(
                    "[BT_CLASSIC_FILTER] hid_open ignored event_cid=%04x "
                    "expected_cid=%04x\n",
                    event_cid, g_state.connecting_hid_cid);
                break;
            }
            const uint8_t open_status =
                hid_subevent_connection_opened_get_status(packet);
            if (open_status != ERROR_CODE_SUCCESS) {
                std::printf(
                    "[BT_CLASSIC_LINK] open_failed status=%02x peer=%s "
                    "attempt=%lu pending_acl=%04x\n",
                    open_status,
                    bd_addr_to_str(g_state.peer),
                    static_cast<unsigned long>(
                        g_state.reconnect_attempts),
                    g_state.pending_acl_handle);
                g_state.incoming_started_us = 0;
                if (g_state.pending_acl_handle !=
                    HCI_CON_HANDLE_INVALID) {
                    // HID/L2CAP failure does not guarantee that the baseband
                    // ACL has already gone away. Keep ownership of the
                    // handle and wait for DISCONNECTION_COMPLETE before a
                    // new page, otherwise the next attempt races a zombie
                    // ACL from the same peer.
                    g_state.reconnect_abort_pending = true;
                    set_scan_enabled(false, "hid_open_failure_cleanup");
                    gap_disconnect(g_state.pending_acl_handle);
                    std::printf(
                        "[BT_CLASSIC_RECONNECT] cleanup_requested "
                        "reason=hid_open_failed handle=%04x "
                        "next=wait_for_acl_close\n",
                        g_state.pending_acl_handle);
                } else {
                    g_state.connecting_hid_cid = 0;
                    g_state.reconnect_in_progress = false;
                    g_state.reconnect_started_us = 0;
                    retry_bonded_peer_or_reopen("hid_open_failed_no_acl");
                }
                break;
            }
            g_state.hid_cid =
                hid_subevent_connection_opened_get_hid_cid(packet);
            g_state.active_acl_handle =
                hid_subevent_connection_opened_get_con_handle(packet);
            hid_subevent_connection_opened_get_bd_addr(
                packet, g_state.peer);
            g_state.last_generation = 0;
            g_state.send_pending = false;
            g_state.connecting_hid_cid = 0;
            g_state.reconnect_in_progress = false;
            g_state.reconnect_started_us = 0;
            g_state.reconnect_acl_started_us = 0;
            g_state.reconnect_abort_pending = false;
            g_state.incoming_started_us = 0;
            g_state.pending_acl_handle = HCI_CON_HANDLE_INVALID;
            g_state.reconnect_pending = false;
            g_state.reconnect_attempts = 0;
            g_state.peer_valid = true;
            g_state.peer_bonded = true;
            g_state.pairing_completed = true;
            g_state.awaiting_first_hid_after_pairing = false;
            const hci_role_t link_role =
                gap_get_role(g_state.active_acl_handle);
            const uint8_t active_status =
                gap_sniff_mode_exit(g_state.active_acl_handle);
            g_state.link_mode = ACL_CONNECTION_MODE_ACTIVE;
            g_state.link_mode_observed = false;
            g_state.link_max_slots = 0;
            g_state.link_max_slots_observed = false;
            g_state.link_packet_types = 0;
            g_state.link_packet_types_observed = false;
            const int rssi_request =
                gap_read_rssi(g_state.active_acl_handle);
            if (!is_sony()) {
                g_state.switch_runtime = {};
                reset_switch_reply_queue();
            }
            g_state.next_switch_report_us = time_us_64();
            g_state.next_sony_report_us = time_us_64();
            set_scan_enabled(false, "hid_connected");
            std::printf(
                "[BT_CLASSIC_LINK] connected hid_cid=%04x peer=%s "
                "single_host_lock=on role=%s "
                "force_active_status=%02x "
                "link_policy=host_role_switch_allowed_active_only "
                "acl_payload_bytes=1021 "
                "rssi_request=%d\n",
                g_state.hid_cid, bd_addr_to_str(g_state.peer),
                link_role == HCI_ROLE_MASTER ? "master" : "slave",
                active_status, rssi_request);
            request_send();
            break;
        }
        case HID_SUBEVENT_CONNECTION_CLOSED: {
            const uint16_t event_cid =
                hid_subevent_connection_closed_get_hid_cid(packet);
            if (g_state.hid_cid == 0 || event_cid != g_state.hid_cid) {
                std::printf(
                    "[BT_CLASSIC_FILTER] hid_close ignored event_cid=%04x "
                    "active_cid=%04x reason=unrelated_or_already_finalized\n",
                    event_cid, g_state.hid_cid);
                break;
            }
            std::printf(
                "[BT_CLASSIC_LINK] disconnected peer=%s hid_cid=%04x\n",
                bd_addr_to_str(g_state.peer), event_cid);
            const hci_con_handle_t closed_acl_handle =
                g_state.active_acl_handle;
            g_state.hid_cid = 0;
            g_state.connecting_hid_cid = 0;
            g_state.reconnect_in_progress = false;
            g_state.reconnect_started_us = 0;
            g_state.send_pending = false;
            reset_switch_reply_queue();
            g_state.last_generation = 0;
            stop_host_rumble("hid_channel_closed");
            if (closed_acl_handle != HCI_CON_HANDLE_INVALID) {
                g_state.reconnect_abort_pending = true;
                set_scan_enabled(false, "hid_close_acl_cleanup");
                gap_disconnect(closed_acl_handle);
                std::printf(
                    "[BT_CLASSIC_RECONNECT] cleanup_requested "
                    "reason=hid_channel_closed handle=%04x "
                    "next=wait_for_acl_close\n",
                    closed_acl_handle);
            } else {
                retry_bonded_peer_or_reopen(
                    "hid_channel_closed_no_acl");
            }
            break;
        }
        case HID_SUBEVENT_CAN_SEND_NOW:
            send_now();
            break;
        default:
            break;
        }
        break;
    default:
        break;
    }
}

} // namespace

bool init(bridge::Mode mode) {
    if (mode != bridge::Mode::DualSense &&
        mode != bridge::Mode::DualSenseEdge &&
        mode != bridge::Mode::SwitchProClassic) {
        return false;
    }
    g_state = {};
    g_state.mode = mode;
    g_state.address = mode_manager::bluetooth_address(mode);
    g_state.last_telemetry_us = time_us_64();
    g_state.last_dualsense_input =
        dualsense::make_neutral_report(mode == bridge::Mode::DualSenseEdge);

    const bool sony = is_sony();
    const uint8_t *descriptor =
        sony ? dualsense::report_descriptor() : switch1::report_descriptor();
    const size_t descriptor_size =
        sony ? dualsense::report_descriptor_size()
             : switch1::report_descriptor_size();
    const char *local_name =
        mode == bridge::Mode::DualSense
            ? "Wireless Controller"
            : mode == bridge::Mode::DualSenseEdge
                  ? "DualSense Edge Wireless Controller"
                  : "Pro Controller";
    const uint16_t vendor = sony ? 0x054c : 0x057e;
    const uint16_t product =
        mode == bridge::Mode::DualSense
            ? 0x0ce6
            : mode == bridge::Mode::DualSenseEdge ? 0x0df2 : 0x2009;

    set_scan_enabled(true, "stack_init", true);
    gap_set_class_of_device(0x2508);
    gap_set_local_name(local_name);
    // Keep sniff disabled, but permit the normal HID role switch that lets the
    // host coordinate its Bluetooth piconet. R48 proved that forcing the Pico
    // into the master role did not improve ACL completion latency.
    gap_set_default_link_policy_settings(LM_LINK_POLICY_ENABLE_ROLE_SWITCH);
    gap_set_allow_role_switch(true);
    // A controller has no display or keyboard. Advertising DisplayYesNo makes
    // Windows wait for a numeric-comparison click that the device cannot show.
    // NoInputNoOutput selects the standard Just Works bonding path.
    gap_ssp_set_enable(true);
    // Let BTstack complete Just Works / no-input-no-output SSP itself. The
    // event handler still records and accepts confirmation requests, while
    // legacy-only hosts retain the explicit 0000 fallback below.
    gap_ssp_set_auto_accept(1);
    gap_secure_connections_enable(true);
    gap_ssp_set_io_capability(SSP_IO_CAPABILITY_NO_INPUT_NO_OUTPUT);
    gap_ssp_set_authentication_requirement(
        SSP_IO_AUTHREQ_MITM_PROTECTION_NOT_REQUIRED_GENERAL_BONDING);
    gap_set_security_level(LEVEL_2);
    gap_register_classic_connection_filter(
        accept_classic_connection);

    l2cap_init();
    sm_init();
    sdp_init();
    hid_sdp_record_t hid_params = {
        0x08,
        33,
        1,
        1,
        1,
        1,
        false,
        kHidSsrHostMaxLatencySlots,
        kHidSsrHostMinTimeoutSlots,
        3200,
        descriptor,
        static_cast<uint16_t>(descriptor_size),
        local_name,
    };
    g_hid_service.fill(0);
    hid_create_sdp_record(
        g_hid_service.data(), sdp_create_service_record_handle(),
        &hid_params);
    const uint16_t hid_sdp_size = de_get_len(g_hid_service.data());
    btstack_assert(hid_sdp_size <= g_hid_service.size());
    const uint8_t hid_sdp_status =
        sdp_register_service(g_hid_service.data());

    g_pnp_service.fill(0);
    device_id_create_sdp_record(
        g_pnp_service.data(), sdp_create_service_record_handle(),
        DEVICE_ID_VENDOR_ID_SOURCE_USB, vendor, product, 0x0100);
    const uint16_t pnp_sdp_size = de_get_len(g_pnp_service.data());
    btstack_assert(pnp_sdp_size <= g_pnp_service.size());
    const uint8_t pnp_sdp_status =
        sdp_register_service(g_pnp_service.data());
    std::printf(
        "[BT_CLASSIC_SDP] hid_bytes=%u pnp_bytes=%u descriptor_bytes=%u "
        "vendor=%04x product=%04x hid_register=%02x pnp_register=%02x "
        "short_report_diagnostic=%u\n",
        hid_sdp_size, pnp_sdp_size,
        static_cast<unsigned>(descriptor_size), vendor, product,
        hid_sdp_status, pnp_sdp_status,
        kClassicShortReportDiagnostic);

    hid_device_init(false, static_cast<uint16_t>(descriptor_size),
                    descriptor);
    hid_device_accept_truncated_hid_reports(!sony);
    hid_device_register_packet_handler(packet_handler);
    hid_device_register_report_request_callback(get_report_callback);
    hid_device_register_set_report_callback(set_report_callback);
    hid_device_register_report_data_callback(report_data_callback);

    g_hci_callback.callback = packet_handler;
    hci_add_event_handler(&g_hci_callback);
    g_l2cap_audit_callback.callback = l2cap_audit_handler;
    l2cap_add_event_handler(&g_l2cap_audit_callback);
    hci_power_control(HCI_POWER_ON);
    return true;
}

void forget_bonds() {
    g_state.forget_requested = true;
    g_state.reconnect_pending = false;
    set_scan_enabled(false, "forget_waiting_for_link_cleanup", true);
    if (g_state.hid_cid != 0) {
        std::printf(
            "[BT_CLASSIC_BOND] forget deferred phase=hid_disconnect "
            "hid_cid=%04x\n",
            g_state.hid_cid);
        if (g_state.active_acl_handle != HCI_CON_HANDLE_INVALID) {
            gap_disconnect(g_state.active_acl_handle);
        } else {
            hid_device_disconnect(g_state.hid_cid);
        }
        return;
    }
    if (g_state.pending_acl_handle != HCI_CON_HANDLE_INVALID) {
        g_state.reconnect_abort_pending = true;
        std::printf(
            "[BT_CLASSIC_BOND] forget deferred phase=acl_disconnect "
            "handle=%04x\n",
            g_state.pending_acl_handle);
        gap_disconnect(g_state.pending_acl_handle);
        return;
    }
    if (g_state.reconnect_in_progress) {
        // There is no ACL yet. BTstack must receive the page result so its
        // pending L2CAP control/interrupt channels can be freed correctly.
        std::printf(
            "[BT_CLASSIC_BOND] forget deferred phase=btstack_page_result "
            "pending_cid=%04x\n",
            g_state.connecting_hid_cid);
        return;
    }
    complete_forget_bonds("bonds_cleared");
}

bool connected() {
    return g_state.hid_cid != 0;
}

void print_status() {
    const uint64_t now = time_us_64();
    std::printf(
        "[BT_CLASSIC_STATUS] mode=%s phase=%s connected=%u discoverable=%u "
        "peer_valid=%u peer_bonded=%u peer=%s reconnect_pending=%u "
        "reconnect_active=%u reconnect_attempts=%lu next_retry_ms=%.1f "
        "hid_cid=%04x connecting_cid=%04x pending_acl=%04x "
        "active_acl=%04x first_hid_host_owned=%u "
        "deferred={forget:%u,stale:%u,reopen:%u}\n",
        bridge::mode_name(g_state.mode), link_phase_name(),
        g_state.hid_cid != 0,
        g_state.scan_enabled, g_state.peer_valid, g_state.peer_bonded,
        g_state.peer_valid ? bd_addr_to_str(g_state.peer) : "none",
        g_state.reconnect_pending, g_state.reconnect_in_progress,
        static_cast<unsigned long>(g_state.reconnect_attempts),
        g_state.reconnect_pending && g_state.reconnect_at_us > now
            ? (g_state.reconnect_at_us - now) / 1000.0
            : 0.0,
        g_state.hid_cid, g_state.connecting_hid_cid,
        g_state.pending_acl_handle, g_state.active_acl_handle,
        g_state.awaiting_first_hid_after_pairing,
        g_state.forget_requested, g_state.stale_key_recovery_requested,
        g_state.pairing_reopen_requested);
}

void task() {
    const uint64_t now = time_us_64();
    if (g_state.reconnect_pending && !g_state.reconnect_in_progress &&
        g_state.hid_cid == 0 && now >= g_state.reconnect_at_us) {
        g_state.reconnect_pending = false;
        g_state.reconnect_in_progress = true;
        g_state.reconnect_abort_pending = false;
        ++g_state.reconnect_attempts;
        g_state.reconnect_started_us = now;
        g_state.reconnect_acl_started_us = 0;
        // Never accept an incoming page while an outgoing HID open is in
        // flight. Alternating passive and active windows prevents Windows and
        // the controller from racing each other on the same PSMs.
        set_scan_enabled(false, "active_fallback_started");
        const uint8_t status = hid_device_connect(
            g_state.peer, &g_state.connecting_hid_cid);
        std::printf(
            "[BT_CLASSIC_RECONNECT] start peer=%s attempt=%lu phase=%s "
            "request_status=%02x status_name=%s pending_cid=%04x\n",
            bd_addr_to_str(g_state.peer),
            static_cast<unsigned long>(g_state.reconnect_attempts),
            g_state.reconnect_attempts <=
                    kMaxImmediateReconnectAttempts
                ? "immediate"
                : "background",
            status, hci_status_name(status), g_state.connecting_hid_cid);
        if (status != ERROR_CODE_SUCCESS) {
            g_state.connecting_hid_cid = 0;
            g_state.reconnect_in_progress = false;
            g_state.reconnect_started_us = 0;
            set_scan_enabled(true, "active_request_failed");
            schedule_next_hid_reconnect("connect_request_failed");
        }
    }

    const bool reconnect_acl_connected =
        g_state.pending_acl_handle != HCI_CON_HANDLE_INVALID &&
        g_state.reconnect_acl_started_us > 0;
    const uint64_t reconnect_acl_age_us =
        reconnect_acl_connected && now >= g_state.reconnect_acl_started_us
            ? now - g_state.reconnect_acl_started_us
            : 0;
    const auto reconnect_timeout_action =
        classic_reconnect_policy::timeout_action(
            g_state.reconnect_in_progress,
            g_state.reconnect_abort_pending, g_state.hid_cid != 0,
            reconnect_acl_connected, reconnect_acl_age_us,
            kHidOpenAfterAclTimeoutUs);
    if (reconnect_timeout_action ==
        classic_reconnect_policy::TimeoutAction::DisconnectAcl) {
        const hci_con_handle_t timed_out_handle =
            g_state.pending_acl_handle;
        g_state.reconnect_abort_pending = true;
        gap_disconnect(timed_out_handle);
        std::printf(
            "[BT_CLASSIC_RECONNECT] hid_after_acl_timeout peer=%s "
            "attempt=%lu pending_cid=%04x acl_handle=%04x "
            "next=wait_for_acl_close\n",
            bd_addr_to_str(g_state.peer),
            static_cast<unsigned long>(g_state.reconnect_attempts),
            g_state.connecting_hid_cid, timed_out_handle);
    }

    if (g_state.hid_cid != 0 && !g_state.send_pending) {
        if (g_state.switch_reply_count != 0) {
            request_send();
        } else if (is_sony()) {
            pro2::InputState source;
            uint32_t generation = 0;
            if (bridge::read_latest_input(&source, &generation) &&
                generation != g_state.last_generation &&
                (kClassicReportMinPeriodUs == 0 ||
                 now >= g_state.next_sony_report_us)) {
                request_send();
            }
        } else if (g_state.switch_runtime.input_mode == 0x3f) {
            pro2::InputState source;
            uint32_t generation = 0;
            const bool changed =
                bridge::read_latest_input(&source, &generation) &&
                generation != g_state.last_generation;
            if (changed || now >= g_state.next_switch_report_us) {
                request_send();
                g_state.next_switch_report_us =
                    now + kSwitchSimpleKeepaliveUs;
            }
        } else if (now >= g_state.next_switch_report_us) {
            request_send();
            do {
                g_state.next_switch_report_us += kSwitchReportPeriodUs;
            } while (g_state.next_switch_report_us <= now);
        }
    }

    if (g_state.hid_cid != 0 && g_state.send_pending &&
        g_state.active_acl_handle != HCI_CON_HANDLE_INVALID) {
        if (hci_can_send_acl_packet_now(g_state.active_acl_handle)) {
            ++g_state.acl_wait_ready_samples;
        } else {
            ++g_state.acl_wait_blocked_samples;
        }
    }

    if (now - g_state.last_telemetry_us >= kTelemetryPeriodUs) {
        const float elapsed =
            (now - g_state.last_telemetry_us) / 1000000.0f;
        float source_age_ms = -1.0f;
        pro2::InputState source;
        uint32_t generation = 0;
        if (bridge::read_latest_input(&source, &generation) &&
            now >= source.received_at_us) {
            source_age_ms = (now - source.received_at_us) / 1000.0f;
        }
        const imu::BiasStatus bias = imu::bias_status();
        const float request_wait_avg_ms =
            g_state.callback_count == 0
                ? 0.0f
                : static_cast<float>(g_state.request_wait_total_us) /
                      static_cast<float>(g_state.callback_count) / 1000.0f;
        const int acl_free =
            g_state.active_acl_handle == HCI_CON_HANDLE_INVALID
                ? -1
                : hci_number_free_acl_slots_for_handle(
                      g_state.active_acl_handle);
        std::printf(
            "[BT_CLASSIC_HEALTH] mode=%s phase=%s connected=%u tx_hz=%.1f "
            "source_age_ms=%.1f requests=%lu callbacks=%lu inline=%lu "
            "send_wait_ms={avg:%.2f,max:%.2f,callback_gap_max:%.2f} "
            "radio={role:%s,mode:%s,mode_observed:%u,rssi_dbm:%d,rssi_valid:%u} "
            "qos={requested:0,reason:controller_rejected_2c_in_r45_r46} "
            "acl={free:%d,completed_events:%lu,completed_packets:%lu,"
            "wait_ready:%lu,wait_blocked:%lu} "
            "skipped_source=%lu output_updates=%lu "
            "rumble_nonzero_updates=%lu output_callbacks={set:%lu,data:%lu} "
            "output_ignored=%lu output_invalid=%lu "
            "output_no_motor_update=%lu rumble_releases=%lu "
            "rumble_active=%u switch_reply_queue={depth:%u,drops:%lu} "
            "reconnect={pending:%u,active:%u,attempts:%lu} "
            "imu_bias=%s bias_raw=%.2f,%.2f,%.2f policy=%s "
            "report_rate_limit_hz=%lu\n",
            bridge::mode_name(g_state.mode), link_phase_name(),
            g_state.hid_cid != 0,
            g_state.tx_count / elapsed, source_age_ms,
            static_cast<unsigned long>(g_state.request_count),
            static_cast<unsigned long>(g_state.callback_count),
            static_cast<unsigned long>(g_state.inline_callbacks),
            request_wait_avg_ms,
            g_state.request_wait_max_us / 1000.0f,
            g_state.callback_gap_max_us / 1000.0f,
            g_state.active_acl_handle != HCI_CON_HANDLE_INVALID &&
                    gap_get_role(g_state.active_acl_handle) == HCI_ROLE_MASTER
                ? "master"
                : "slave",
            link_mode_name(g_state.link_mode),
            g_state.link_mode_observed,
            g_state.latest_rssi_dbm, g_state.rssi_valid,
            acl_free,
            static_cast<unsigned long>(g_state.hci_completed_events),
            static_cast<unsigned long>(g_state.hci_completed_packets),
            static_cast<unsigned long>(g_state.acl_wait_ready_samples),
            static_cast<unsigned long>(g_state.acl_wait_blocked_samples),
            static_cast<unsigned long>(g_state.skipped_source),
            static_cast<unsigned long>(g_state.output_count),
            static_cast<unsigned long>(g_state.rumble_count),
            static_cast<unsigned long>(g_state.output_set_callbacks),
            static_cast<unsigned long>(g_state.output_data_callbacks),
            static_cast<unsigned long>(g_state.output_ignored),
            static_cast<unsigned long>(g_state.output_invalid),
            static_cast<unsigned long>(g_state.output_no_motor_update),
            static_cast<unsigned long>(g_state.rumble_release_count),
            g_state.compatible_rumble_active,
            g_state.switch_reply_count,
            static_cast<unsigned long>(g_state.switch_reply_drops),
            g_state.reconnect_pending, g_state.reconnect_in_progress,
            static_cast<unsigned long>(g_state.reconnect_attempts),
            bias.calibrated ? "calibrated" : "collecting",
            bias.gyro_x, bias.gyro_y, bias.gyro_z,
            is_sony()
                ? "source_paced"
                : g_state.switch_runtime.input_mode == 0x3f
                      ? "simple_3f_change_plus_10hz_keepalive"
                      : "66.7hz_3_real_imu_samples",
            static_cast<unsigned long>(kClassicReportRateLimitHz));
        g_state.last_telemetry_us = now;
        g_state.tx_count = 0;
        g_state.request_count = 0;
        g_state.callback_count = 0;
        g_state.inline_callbacks = 0;
        g_state.request_wait_total_us = 0;
        g_state.request_wait_max_us = 0;
        g_state.callback_gap_max_us = 0;
        g_state.hci_completed_events = 0;
        g_state.hci_completed_packets = 0;
        g_state.acl_wait_ready_samples = 0;
        g_state.acl_wait_blocked_samples = 0;
        g_state.skipped_source = 0;
    }
}

bool self_test() {
    return dualsense::self_test() && switch1::self_test();
}

} // namespace classic_gamepad
