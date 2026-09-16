#pragma once

#include <cstdint>

namespace classic_reconnect_policy {

enum class TimeoutAction : uint8_t {
    None,
    WaitForStackPageResult,
    DisconnectAcl,
};

enum class ClosedAclAction : uint8_t {
    StandardRetry,
    WaitForHostHid,
};

inline ClosedAclAction closed_acl_action(
    bool awaiting_first_hid_after_pairing) {
    return awaiting_first_hid_after_pairing
               ? ClosedAclAction::WaitForHostHid
               : ClosedAclAction::StandardRetry;
}

inline TimeoutAction timeout_action(bool reconnect_active,
                                    bool abort_pending,
                                    bool hid_connected,
                                    bool acl_connected,
                                    uint64_t acl_age_us,
                                    uint64_t hid_open_timeout_us) {
    if (!reconnect_active || abort_pending || hid_connected) {
        return TimeoutAction::None;
    }
    if (!acl_connected) {
        return TimeoutAction::WaitForStackPageResult;
    }
    if (acl_age_us < hid_open_timeout_us) {
        return TimeoutAction::None;
    }
    return TimeoutAction::DisconnectAcl;
}

} // namespace classic_reconnect_policy
