#pragma once

#include "bridge_mode.hpp"

namespace classic_gamepad {

bool init(bridge::Mode mode);
void task();
void forget_bonds();
bool connected();
void print_status();
bool self_test();

} // namespace classic_gamepad
