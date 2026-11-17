/* Compile the pinned upstream HID implementation intact, adding one checked
 * interrupt-send entry point. Upstream's public void API discards l2cap_send
 * errors (including remote MTU rejection), which must be visible in this test.
 * Original BTstack license remains applicable to the included implementation.
 */
#include "btstack.h"
#include <stdio.h>
/* The pinned upstream clears device->cid before emitting CLOSED. Preserve the
 * identity at the public callback boundary without altering upstream source. */
#define hid_device_register_packet_handler relay_upstream_register_packet_handler
#undef BTSTACK_FILE__
#include "hid_device.c"
#undef hid_device_register_packet_handler
#include "relay_flow.h"
static btstack_packet_handler_t relay_client;
static uint16_t relay_open_cid;
static void relay_hid_event(uint8_t type,uint16_t channel,uint8_t *packet,uint16_t size){
    if(type==HCI_EVENT_PACKET&&size>=5&&packet[0]==HCI_EVENT_HID_META){
        uint8_t sub=hci_event_hid_meta_get_subevent_code(packet);
        if(sub==HID_SUBEVENT_CONNECTION_OPENED&&size>=6&&hid_subevent_connection_opened_get_status(packet)==0)
            relay_open_cid=hid_subevent_connection_opened_get_hid_cid(packet);
        if(sub==HID_SUBEVENT_CONNECTION_CLOSED){
            uint16_t reported=hid_subevent_connection_closed_get_hid_cid(packet);
            uint16_t closed=reported?reported:relay_open_cid;
            uint8_t event[5];memcpy(event,packet,5);little_endian_store_16(event,3,closed);
            relay_open_cid=0;
            printf("{\"event\":\"HID_SESSION_CLOSED\",\"reported_cid\":%u,\"closed_cid\":%u,\"identity_recovered\":%s}\n",reported,closed,reported==0&&closed?"true":"false");
            if(relay_client)relay_client(type,closed,event,sizeof(event));
            return;
        }
    }
    if(relay_client)relay_client(type,channel,packet,size);
}
void hid_device_register_packet_handler(btstack_packet_handler_t callback){
    relay_client=callback;relay_upstream_register_packet_handler(relay_hid_event);
}
int relay_hid_channels_open(uint16_t cid){
    hid_device_t *device=hid_device_get_instance_for_hid_cid(cid);
    return device&&device->connected&&device->control_cid&&device->interrupt_cid;
}
void relay_hid_flow_snapshot(uint16_t cid,relay_hid_flow_t *flow){
    memset(flow,0,sizeof(*flow));
    hid_device_t*device=hid_device_get_instance_for_hid_cid(cid);if(!device)return;
    flow->found=1;flow->state=device->state;flow->pending=device->user_request_can_send_now;
    flow->control_cid=device->control_cid;flow->interrupt_cid=device->interrupt_cid;
    flow->can_send=device->interrupt_cid?l2cap_can_send_packet_now(device->interrupt_cid):0;
}
int relay_hid_send_checked(uint16_t cid,const uint8_t*message,uint16_t length,uint16_t*remote_mtu){
    hid_device_t*device=hid_device_get_instance_for_hid_cid(cid);
    if(!device||!device->interrupt_cid)return ERROR_CODE_UNKNOWN_CONNECTION_IDENTIFIER;
    *remote_mtu=l2cap_get_remote_mtu_for_local_cid(device->interrupt_cid);
    int status=l2cap_send(device->interrupt_cid,(uint8_t*)message,length);
    if(device->user_request_can_send_now)l2cap_request_can_send_now_event(device->interrupt_cid);
    return status;
}

/* Exercise the actual pinned upstream close handler, including both channel
 * close orders. No controller, USB handle or HCI transport is initialized. */
static unsigned test_closes;static uint16_t test_closed_cid,test_closed_channel;
static void test_event(uint8_t type,uint16_t channel,uint8_t *packet,uint16_t size){
    if(type==HCI_EVENT_PACKET&&size>=5&&packet[0]==HCI_EVENT_HID_META&&packet[2]==HID_SUBEVENT_CONNECTION_CLOSED){
        test_closes++;test_closed_cid=hid_subevent_connection_closed_get_hid_cid(packet);test_closed_channel=channel;
    }
}
int relay_hid_lifecycle_self_test(int *cases){
    int failures=0;bd_addr_t address={1,2,3,4,5,6};
    hid_device_register_packet_handler(test_event);
    for(int order=0;order<2;order++){
        test_closes=0;test_closed_cid=0;test_closed_channel=0;
        hid_device_setup_instance(&hid_device_singleton,address);
        hid_device_singleton.control_cid=0x41;hid_device_singleton.interrupt_cid=0x42;hid_device_singleton.connected=1;
        uint16_t expected=hid_device_singleton.cid;
        hid_device_emit_connected_event(&hid_device_singleton,0);
        (*cases)++;failures+=!relay_hid_channels_open(expected);
        uint8_t closed[]={L2CAP_EVENT_CHANNEL_CLOSED,2,0,0};
        little_endian_store_16(closed,2,order?0x41:0x42);
        packet_handler(HCI_EVENT_PACKET,little_endian_read_16(closed,2),closed,sizeof(closed));
        (*cases)++;failures+=test_closes!=0||relay_hid_channels_open(expected);
        little_endian_store_16(closed,2,order?0x42:0x41);
        packet_handler(HCI_EVENT_PACKET,little_endian_read_16(closed,2),closed,sizeof(closed));
        (*cases)++;failures+=test_closes!=1||test_closed_cid!=expected||test_closed_channel!=expected;
        (*cases)++;failures+=hid_device_singleton.cid!=0||relay_open_cid!=0;
    }
    relay_client=NULL;relay_open_cid=0;hid_device_deinit();return failures;
}
