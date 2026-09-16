#pragma once

namespace xbox_ble {

bool init();
void task();
bool self_test();
void forget_bonds();
bool connected();
void print_status();

} // namespace xbox_ble
