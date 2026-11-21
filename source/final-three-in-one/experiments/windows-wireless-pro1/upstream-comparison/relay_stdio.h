#ifndef UPSTREAM_RELAY_STDIO_H
#define UPSTREAM_RELAY_STDIO_H
#include <stdbool.h>
#include <stdint.h>
#include <windows.h>

enum relay_pipe_state { RELAY_PIPE_OPEN, RELAY_PIPE_CLOSED, RELAY_PIPE_INVALID };
bool relay_stdio_open(HANDLE pipe);
enum relay_pipe_state relay_stdio_tick(uint64_t now, bool ready);
void relay_stdio_observe_input(uint32_t mask);
bool relay_stdio_probe_home(bool press, uint64_t now);
bool relay_stdio_input_live(uint64_t now);
bool relay_stdio_fixture(void);
unsigned long long relay_stdio_frames(void);
unsigned long long relay_stdio_coalesced_edges(void);
#endif
