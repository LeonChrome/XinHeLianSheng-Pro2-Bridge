#ifndef PRO2_RELAY_FLOW_H
#define PRO2_RELAY_FLOW_H
#include <stdint.h>
typedef struct { int found,state,pending,can_send; uint16_t control_cid,interrupt_cid; } relay_hid_flow_t;
typedef struct {
    int acl_busy,command_busy,acl_done,command_done;
    uint32_t acl_error,command_error,acl_bytes,command_bytes;
    uint64_t acl_io_status,command_io_status;
} relay_usb_flow_t;
void relay_hid_flow_snapshot(uint16_t cid,relay_hid_flow_t *flow);
void relay_usb_flow_snapshot(relay_usb_flow_t *flow);
int relay_hid_lifecycle_self_test(int *cases);
int relay_hid_channels_open(uint16_t cid);
#endif
