#pragma once

namespace pairing_feedback {

// Queue two gentle low-frequency pulses. If Pro2 input is not ready yet,
// feedback waits briefly instead of writing to an uninitialized USB endpoint.
void start(const char *reason);
void task();

} // namespace pairing_feedback
