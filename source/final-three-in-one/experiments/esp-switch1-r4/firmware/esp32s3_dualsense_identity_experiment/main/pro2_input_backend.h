#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "switch2_state.h"

void pro2_input_backend_init(void);
bool pro2_input_backend_get_live(switch2_state_t *state,
                                 uint32_t *updates,
                                 int64_t *age_us);
const char *pro2_input_backend_state(void);
