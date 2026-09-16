#include "wireless_backend.hpp"

#include <cstdio>

#include "btstack.h"
#include "classic_gamepad.hpp"
#include "mode_manager.hpp"
#include "xbox_ble.hpp"

namespace wireless_backend {
namespace {

bridge::Mode g_mode = bridge::kDefaultMode;
bd_addr_t g_selected_address{};
bool g_selected_address_valid = false;
} // namespace

bool init(bridge::Mode mode) {
    g_mode = mode;
    if (mode == bridge::Mode::Pro2Quiet) {
        return false;
    }
    const bool initialized = mode == bridge::Mode::XboxBle
                                 ? xbox_ble::init()
                                 : classic_gamepad::init(mode);
    std::printf(
        "[WIRELESS_POLICY] active_mode=%s backend=%s "
        "max_active_hosts=1 simultaneous_multi_mode=off initialized=%u\n",
        bridge::mode_name(mode),
        mode == bridge::Mode::XboxBle ? "ble_hids" : "classic_hid",
        initialized);
    return initialized;
}

void configure_address(bridge::Mode mode) {
    const auto address = mode_manager::bluetooth_address(mode);
    std::copy(address.begin(), address.end(), g_selected_address);
    g_selected_address_valid = true;
    hci_set_bd_addr(g_selected_address);
}

void task() {
    if (g_mode == bridge::Mode::XboxBle) {
        xbox_ble::task();
    } else {
        classic_gamepad::task();
    }
}

void forget_bonds() {
    if (mode_manager::current() == bridge::Mode::Pro2Quiet) {
        std::printf("[WIRELESS] forget ignored in pro2_quiet mode\n");
        return;
    }
    if (g_mode == bridge::Mode::XboxBle) {
        xbox_ble::forget_bonds();
    } else {
        classic_gamepad::forget_bonds();
    }
}

bool connected() {
    if (g_mode == bridge::Mode::Pro2Quiet) {
        return false;
    }
    return g_mode == bridge::Mode::XboxBle
               ? xbox_ble::connected()
               : classic_gamepad::connected();
}

void print_status() {
    if (g_mode == bridge::Mode::Pro2Quiet) {
        std::printf("[WIRELESS_STATUS] backend=off connected=0\n");
    } else if (g_mode == bridge::Mode::XboxBle) {
        xbox_ble::print_status();
    } else {
        classic_gamepad::print_status();
    }
}

bool self_test() {
    return xbox_ble::self_test() && classic_gamepad::self_test();
}

extern "C" void __real_hci_set_bd_addr(bd_addr_t address);
extern "C" uint8_t __real_l2cap_register_service(
    btstack_packet_handler_t packet_handler, uint16_t psm, uint16_t mtu,
    gap_security_level_t security_level);

extern "C" void __wrap_hci_set_bd_addr(bd_addr_t address) {
    // btstack_hci_transport_cyw43_open() writes the board MAC after the
    // application selected its mode address. Reapply the selected identity
    // so HCI initialization programs the intended mode-specific address.
    __real_hci_set_bd_addr(
        g_selected_address_valid ? g_selected_address : address);
}

extern "C" uint8_t __wrap_l2cap_register_service(
    btstack_packet_handler_t packet_handler, uint16_t psm, uint16_t mtu,
    gap_security_level_t security_level) {
    if (g_mode != bridge::Mode::XboxBle &&
        psm == BLUETOOTH_PSM_SDP) {
        const uint8_t status = __real_l2cap_register_service(
            packet_handler, psm, mtu, security_level);
        std::printf(
            "[BT_CLASSIC_SDP_LINK] register_psm=%04x mtu=%u "
            "security=%u status=%02x\n",
            psm, mtu, static_cast<unsigned>(security_level), status);
        return status;
    }
    return __real_l2cap_register_service(
        packet_handler, psm, mtu, security_level);
}

} // namespace wireless_backend
