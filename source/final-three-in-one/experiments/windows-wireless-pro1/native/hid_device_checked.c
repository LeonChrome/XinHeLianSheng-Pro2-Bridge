/* Compile the pinned upstream HID implementation intact, adding one checked
 * interrupt-send entry point. Upstream's public void API discards l2cap_send
 * errors (including remote MTU rejection), which must be visible in this test.
 * Original BTstack license remains applicable to the included implementation.
 */
#include "hid_device.c"
int relay_hid_send_checked(uint16_t cid,const uint8_t*message,uint16_t length,uint16_t*remote_mtu){
    hid_device_t*device=hid_device_get_instance_for_hid_cid(cid);
    if(!device||!device->interrupt_cid)return ERROR_CODE_UNKNOWN_CONNECTION_IDENTIFIER;
    *remote_mtu=l2cap_get_remote_mtu_for_local_cid(device->interrupt_cid);
    int status=l2cap_send(device->interrupt_cid,(uint8_t*)message,length);
    if(device->user_request_can_send_now)l2cap_request_can_send_now_event(device->interrupt_cid);
    return status;
}
