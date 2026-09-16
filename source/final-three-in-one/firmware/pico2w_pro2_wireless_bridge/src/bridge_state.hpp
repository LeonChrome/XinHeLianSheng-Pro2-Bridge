#pragma once

#include <cstddef>
#include <cstdint>

#include "pro2_protocol.hpp"

namespace bridge {

void state_init();
void publish_input(const pro2::InputState &state);
void clear_input();
bool read_latest_input(pro2::InputState *state, uint32_t *generation);
size_t read_recent_inputs(pro2::InputState *states, uint32_t *generations,
                          size_t capacity);
size_t read_resampled_inputs(pro2::InputState *states,
                             uint32_t *generations, size_t capacity,
                             uint64_t sample_period_us);

} // namespace bridge
