#ifndef RELAY_TRANSITION_CAPTURE_H
#define RELAY_TRANSITION_CAPTURE_H
#include <stdint.h>

/* Driven on the BTstack run-loop thread. The writer receives only sanitized records. */
void relay_capture_init(const char *utf8_result_file, double monotonic_now);
void relay_capture_new_link(uint16_t handle);
void relay_capture_packet(uint8_t type, int incoming, const uint8_t *packet, unsigned size, double stamp);
void relay_capture_poll(double stamp);
void relay_capture_finish(void);
int relay_capture_fixture(const char *utf8_result_file);
#endif
