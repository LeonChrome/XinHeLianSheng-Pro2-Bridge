#pragma once

#include <array>

#include "bridge_mode.hpp"

namespace mode_manager {

void init();
void task();
bridge::Mode current();
bridge::Mode next(bridge::Mode mode);
bool parse(const char *text, bridge::Mode *mode);
void set_and_reboot(bridge::Mode mode);
bool pairing_recovery_required();
void mark_pairing_recovery_applied();
std::array<uint8_t, 6> bluetooth_address(bridge::Mode mode);
bool self_test();

} // namespace mode_manager
