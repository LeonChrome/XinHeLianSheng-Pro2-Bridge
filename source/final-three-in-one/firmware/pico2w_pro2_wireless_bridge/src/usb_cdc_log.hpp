#pragma once

namespace usb_cdc_log {

void init();
void task();
void set_runtime_ready(bool ready);

} // namespace usb_cdc_log
