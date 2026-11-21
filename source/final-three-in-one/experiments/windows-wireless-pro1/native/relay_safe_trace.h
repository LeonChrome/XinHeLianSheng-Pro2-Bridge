#ifndef RELAY_SAFE_TRACE_H
#define RELAY_SAFE_TRACE_H

#include <stdint.h>

/* The transport calls these before passing packets on to BTstack. Address is
 * only for correlation and is never emitted. UINT16_MAX means no tracked ACL.
 * Caller must reset reassembly when the tracked connection changes. */
void relay_safe_trace_hci(const uint8_t *packet, uint16_t size, uint16_t tracked_handle,
                          const uint8_t peer_address[6]);
void relay_safe_trace_acl(int from_peer, const uint8_t *packet, uint16_t size,
                          uint16_t tracked_handle);
void relay_safe_trace_acl_reset(void);
int relay_safe_trace_self_test(int *cases);

#endif
