#pragma once

#include "bridge_mode.hpp"

namespace wireless_backend {

bool init(bridge::Mode mode);
void configure_address(bridge::Mode mode);
void task();
void forget_bonds();
bool connected();
void print_status();
bool self_test();

} // namespace wireless_backend
