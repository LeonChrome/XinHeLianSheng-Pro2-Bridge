#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <string>

#include "classic_reconnect_policy.hpp"

namespace {
int failures = 0;
int checks = 0;

void check(bool condition, const char *name) {
    ++checks;
    std::printf("[%s] %s\n", condition ? "PASS" : "FAIL", name);
    if (!condition) ++failures;
}
} // namespace

int main() {
    using classic_reconnect_policy::ClosedAclAction;
    check(classic_reconnect_policy::closed_acl_action(true) ==
              ClosedAclAction::WaitForHostHid,
          "newly paired host owns the first HID connection");
    check(classic_reconnect_policy::closed_acl_action(false) ==
              ClosedAclAction::StandardRetry,
          "established bonds keep normal reconnect behavior");

    std::ifstream source(CLASSIC_GAMEPAD_SOURCE, std::ios::binary);
    const std::string classic((std::istreambuf_iterator<char>(source)),
                              std::istreambuf_iterator<char>());
    check(classic.find("kIncomingHidOpenTimeoutUs") == std::string::npos &&
              classic.find("incoming_timeout peer=") == std::string::npos,
          "incoming host ACL is not terminated by a fixed HID timer");
    check(classic.find("host_owned_first_hid") != std::string::npos &&
              classic.find("post_pair_host_owned") != std::string::npos,
          "post-pair recovery remains passive for Windows HID setup");
    check(classic.find("incoming_hid_timeout=host_managed") !=
              std::string::npos,
          "runtime policy log exposes host-managed incoming HID setup");
    check(classic.find("kMaxImmediateReconnectAttempts = 2") !=
                  std::string::npos,
          "saved host gets two quick reconnect attempts before backoff");
    check(classic.find("LM_LINK_POLICY_ENABLE_SNIFF_MODE") ==
                  std::string::npos &&
              classic.find("gap_set_default_link_policy_settings(") !=
                  std::string::npos &&
              classic.find("LM_LINK_POLICY_ENABLE_ROLE_SWITCH") !=
                  std::string::npos &&
              classic.find("gap_set_allow_role_switch(true)") !=
                  std::string::npos &&
              classic.find("gap_sniff_mode_exit") != std::string::npos &&
              classic.find("sniff_mode=disabled_active_only") !=
                  std::string::npos &&
              classic.find("active_gamepad_policy") != std::string::npos,
          "Classic HID cannot re-enter sniff mode after opening");
    check(classic.find("gap_request_role(g_state.peer, HCI_ROLE_MASTER)") ==
                  std::string::npos &&
              classic.find("HCI_EVENT_ROLE_CHANGE") != std::string::npos &&
              classic.find("policy=host_role_switch_allowed") !=
                  std::string::npos,
          "Classic HID restores standard host role-switch policy");
    check(classic.find("HCI_EVENT_NUMBER_OF_COMPLETED_PACKETS") !=
                  std::string::npos &&
              classic.find("send_wait_ms=") != std::string::npos,
          "Classic HID ACL pacing diagnostics are present");
    check(classic.find("gap_qos_set") == std::string::npos &&
              classic.find("kHidSsrHostMaxLatencySlots = 24") !=
                  std::string::npos &&
              classic.find("kHidSsrHostMinTimeoutSlots = 24") !=
                  std::string::npos &&
              classic.find("HCI_EVENT_SNIFF_SUBRATING") !=
                  std::string::npos,
          "rejected QoS is removed and HID SSR latency is bounded");

    std::ifstream config(BTSTACK_CONFIG_SOURCE, std::ios::binary);
    const std::string btstack_config(
        (std::istreambuf_iterator<char>(config)),
        std::istreambuf_iterator<char>());
    check(btstack_config.find("#define HCI_ACL_PAYLOAD_SIZE 1021") !=
              std::string::npos,
          "Classic HID exposes multi-slot 3-DH5 ACL capacity");
    check(btstack_config.find(
              "#define ENABLE_HCI_CONTROLLER_TO_HOST_FLOW_CONTROL") !=
                  std::string::npos &&
              btstack_config.find(
                  "#define MAX_NR_CONTROLLER_ACL_BUFFERS 8") !=
                  std::string::npos &&
              btstack_config.find("#define HCI_HOST_ACL_PACKET_NUM 8") !=
                  std::string::npos &&
              classic.find(
                  "hci_flow_control=controller_to_host:8") !=
                  std::string::npos,
          "CYW43 shared HCI bus uses bounded controller-to-host flow control");
    check(classic.find("HCI_EVENT_MAX_SLOTS_CHANGED") !=
                  std::string::npos &&
              classic.find("HCI_EVENT_CONNECTION_PACKET_TYPE_CHANGED") !=
                  std::string::npos &&
              classic.find("create_connection_packet_mask=") !=
                  std::string::npos,
          "Classic HID records negotiated packet slots and packet types");

    std::printf("[RESULT] checks=%d failures=%d\n", checks, failures);
    return failures == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
