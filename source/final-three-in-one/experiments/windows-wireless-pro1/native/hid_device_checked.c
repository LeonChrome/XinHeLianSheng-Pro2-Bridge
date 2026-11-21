/* Compile the pinned upstream HID implementation intact. The wrappers
 * observe the negotiated channels and HID Control metadata and preserve
 * l2cap_send errors which upstream's public void API discards.
 * Original BTstack license remains applicable to the included implementation.
 */
#include "btstack.h"
#include <stdio.h>
static uint8_t relay_l2cap_register_service(btstack_packet_handler_t handler,uint16_t psm,uint16_t mtu,gap_security_level_t level);
static uint8_t relay_l2cap_create_channel(btstack_packet_handler_t handler,bd_addr_t address,uint16_t psm,uint16_t mtu,uint16_t *out_cid);
static uint8_t relay_l2cap_send_prepared(uint16_t channel,uint16_t len);
static uint8_t relay_l2cap_send(uint16_t channel,const uint8_t *data,uint16_t len);
/* The pinned upstream clears device->cid before emitting CLOSED. Preserve the
 * identity at the public callback boundary without altering upstream source. */
#define hid_device_register_packet_handler relay_upstream_register_packet_handler
#define hid_device_register_report_request_callback relay_upstream_register_report_request_callback
#define hid_device_register_set_report_callback relay_upstream_register_set_report_callback
#define hid_device_register_report_data_callback relay_upstream_register_report_data_callback
#define l2cap_register_service relay_l2cap_register_service
#define l2cap_create_channel relay_l2cap_create_channel
#define l2cap_send_prepared relay_l2cap_send_prepared
#define l2cap_send relay_l2cap_send
#undef BTSTACK_FILE__
#include "hid_device.c"
#undef hid_device_register_packet_handler
#undef hid_device_register_report_request_callback
#undef hid_device_register_set_report_callback
#undef hid_device_register_report_data_callback
#undef l2cap_register_service
#undef l2cap_create_channel
#undef l2cap_send_prepared
#undef l2cap_send
#include "relay_flow.h"
static btstack_packet_handler_t relay_client;
static uint16_t relay_open_cid;
static struct {
    uint16_t handle, control, interrupt;
    uint8_t pending_open[15];
    uint16_t pending_size;
} relay_hid_link={.handle=HCI_CON_HANDLE_INVALID};
static int (*relay_get_report)(uint16_t,hid_report_type_t,uint16_t,int*,uint8_t*);
static void (*relay_set_report)(uint16_t,hid_report_type_t,int,uint8_t*);
static void (*relay_report_data)(uint16_t,hid_report_type_t,uint16_t,int,uint8_t*);
static unsigned relay_report_deliveries;

void relay_hid_forget_acl(uint16_t handle){
    if(relay_hid_link.handle==handle){
        relay_hid_link.handle=HCI_CON_HANDLE_INVALID;
        relay_hid_link.control=relay_hid_link.interrupt=relay_hid_link.pending_size=relay_open_cid=0;
    }
    if(handle!=HCI_CON_HANDLE_INVALID && hid_device_singleton.con_handle==handle){
        hid_device_singleton.control_cid=hid_device_singleton.interrupt_cid=0;
        hid_device_singleton.connected=hid_device_singleton.incoming=0;
        hid_device_singleton.con_handle=HCI_CON_HANDLE_INVALID;
        hid_device_singleton.cid=0;
    }
}
static int relay_hid_has_channels(const hid_device_t *device){
    return device && device->connected && relay_hid_link.handle==device->con_handle &&
        relay_hid_link.control && relay_hid_link.control==device->control_cid &&
        relay_hid_link.interrupt && relay_hid_link.interrupt==device->interrupt_cid;
}
static void relay_hid_publish_open(void){
    const hid_device_t *device=&hid_device_singleton;
    if(!relay_hid_link.pending_size || !relay_hid_has_channels(device))return;
    uint8_t event[sizeof(relay_hid_link.pending_open)];
    uint16_t size=relay_hid_link.pending_size;
    memcpy(event,relay_hid_link.pending_open,size);
    relay_hid_link.pending_size=0;
    relay_open_cid=device->cid;
    if(relay_client)relay_client(HCI_EVENT_PACKET,relay_open_cid,event,size);
}
static void relay_hid_publish_closed(uint16_t reported){
    if(!relay_open_cid || (reported && reported!=relay_open_cid))return;
    uint16_t closed=relay_open_cid;
    relay_open_cid=0;
    relay_hid_link.pending_size=0;
    uint8_t event[]={HCI_EVENT_HID_META,3,HID_SUBEVENT_CONNECTION_CLOSED,0,0};
    little_endian_store_16(event,3,closed);
    printf("{\"event\":\"HID_SESSION_CLOSED\",\"reported_cid\":%u,\"closed_cid\":%u,\"identity_recovered\":%s}\n",
        reported,closed,reported?"false":"true");
    if(relay_client)relay_client(HCI_EVENT_PACKET,closed,event,sizeof(event));
}
static void relay_hid_observe_l2cap_open(uint8_t status,uint16_t handle,uint16_t psm,uint16_t cid){
    const hid_device_t *device=&hid_device_singleton;
    if(psm!=PSM_HID_CONTROL && psm!=PSM_HID_INTERRUPT)return;
    if(device->con_handle!=HCI_CON_HANDLE_INVALID && device->con_handle!=handle)return;
    if(cid!=(psm==PSM_HID_CONTROL?device->control_cid:device->interrupt_cid))return;
    if(relay_hid_link.handle!=handle){
        relay_hid_forget_acl(relay_hid_link.handle);
        relay_hid_link.handle=handle;
    }
    if(psm==PSM_HID_CONTROL)relay_hid_link.control=status?0:cid;
    else relay_hid_link.interrupt=status?0:cid;
}

/* Only HIDP header fields may enter the trace. Report bodies may contain
 * pairing, SPI, NFC or feedback data and are never dumped by these wrappers. */
typedef struct {
    int transaction,parameter,report_type,report_id,protocol_arg,host_buffer;
} relay_control_metadata_t;

static relay_control_metadata_t relay_control_request_metadata(const uint8_t *bytes,uint16_t len,int has_report_id){
    relay_control_metadata_t meta={-1,-1,-1,-1,-1,-1};
    if(!len)return meta;
    meta.transaction=bytes[0]>>4;
    meta.parameter=bytes[0]&15;
    switch(meta.transaction){
        case HID_MESSAGE_TYPE_GET_REPORT:
        case HID_MESSAGE_TYPE_SET_REPORT:
        case HID_MESSAGE_TYPE_DATA:
            meta.report_type=meta.parameter&3;
            if(has_report_id&&len>=2)meta.report_id=bytes[1];
            if(meta.transaction==HID_MESSAGE_TYPE_GET_REPORT&&(meta.parameter&8)){
                unsigned offset=has_report_id?2u:1u;
                if(len>=offset+2)meta.host_buffer=little_endian_read_16(bytes,offset);
            }
            break;
        case HID_MESSAGE_TYPE_SET_PROTOCOL:
            meta.protocol_arg=meta.parameter&1;
            break;
        default:break;
    }
    return meta;
}
static int relay_control_has_id(const hid_device_t *device){
    return device->protocol_mode==HID_PROTOCOL_MODE_BOOT||
        btstack_hid_report_id_declared(hid_device_descriptor,hid_device_descriptor_len);
}
static void relay_trace_hid_rx(uint16_t channel,const uint8_t *packet,uint16_t len){
    const hid_device_t *device=&hid_device_singleton;
    int is_control=channel&&channel==device->control_cid;
    if(!is_control&&(!channel||channel!=device->interrupt_cid||!len||
       packet[0]>>4==HID_MESSAGE_TYPE_DATA))return;
    relay_control_metadata_t meta=relay_control_request_metadata(packet,len,relay_control_has_id(device));
    printf("{\"event\":\"HID_CONTROL_RX\",\"handle\":%u,\"hid_cid\":%u,\"l2cap_cid\":%u,"
        "\"transaction\":%d,\"parameter\":%d,\"report_type\":%d,\"report_id\":%d,"
        "\"bytes\":%u,\"protocol_mode\":%u,\"protocol_arg\":%d,\"host_buffer_bytes\":%d,"
        "\"control_channel\":%s,\"payload_logged\":false}\n",
        device->con_handle,device->cid,channel,meta.transaction,meta.parameter,meta.report_type,meta.report_id,len,
        (unsigned)device->protocol_mode,meta.protocol_arg,meta.host_buffer,is_control?"true":"false");
}
static void relay_trace_filtered_output(uint16_t channel,const uint8_t *packet,uint16_t len){
    hid_device_t *device=&hid_device_singleton;
    if(!channel||channel!=device->interrupt_cid||!len||packet[0]>>4!=HID_MESSAGE_TYPE_DATA||
       (packet[0]&3)!=HID_REPORT_TYPE_OUTPUT)return;
    int has_id=btstack_hid_report_id_declared(hid_device_descriptor,hid_device_descriptor_len)!=0;
    int report_id=has_id&&len>=2?packet[1]:0;
    const char *reason=NULL;
    if(len<2)reason="short_packet";
    else if(hid_report_id_status(device->cid,report_id)==HID_REPORT_ID_INVALID)reason="invalid_report_id";
    else if(!hid_report_size_valid(device->cid,report_id,HID_REPORT_TYPE_OUTPUT,len-1-has_id))reason="invalid_report_size";
    if(reason)printf("{\"event\":\"HID_INTERRUPT_OUTPUT_FILTERED\",\"handle\":%u,\"hid_cid\":%u,"
        "\"l2cap_cid\":%u,\"report_id\":%d,\"bytes\":%u,\"reason\":\"%s\",\"payload_logged\":false}\n",
        device->con_handle,device->cid,channel,has_id&&len<2?-1:report_id,len,reason);
}
static void relay_hid_l2cap_packet_handler(uint8_t type,uint16_t channel,uint8_t *packet,uint16_t size){
    if(type==L2CAP_DATA_PACKET){
        relay_trace_hid_rx(channel,packet,size);
        unsigned before=relay_report_deliveries;
        packet_handler(type,channel,packet,size);
        /* Normal feedback reaches the callback and has already been logged
         * by the application. Only re-check descriptor details for rejects. */
        if(relay_report_deliveries==before)relay_trace_filtered_output(channel,packet,size);
        return;
    }else if(type==HCI_EVENT_PACKET&&size>=25&&packet[0]==L2CAP_EVENT_CHANNEL_OPENED){
        uint8_t status=l2cap_event_channel_opened_get_status(packet);
        uint16_t handle=l2cap_event_channel_opened_get_handle(packet);
        uint16_t local_cid=l2cap_event_channel_opened_get_local_cid(packet);
        uint16_t hid_cid=hid_device_singleton.cid;
        relay_hid_observe_l2cap_open(status,handle,
            l2cap_event_channel_opened_get_psm(packet),local_cid);
        printf("{\"event\":\"HID_L2CAP_CHANNEL_OPENED\",\"status\":%u,\"handle\":%u,\"psm\":%u,"
            "\"local_cid\":%u,\"remote_cid\":%u,\"local_mtu\":%u,\"remote_mtu\":%u,\"mode\":%u,"
            "\"flush_timeout\":%u,\"incoming\":%s}\n",
            l2cap_event_channel_opened_get_status(packet),l2cap_event_channel_opened_get_handle(packet),
            l2cap_event_channel_opened_get_psm(packet),l2cap_event_channel_opened_get_local_cid(packet),
            l2cap_event_channel_opened_get_remote_cid(packet),l2cap_event_channel_opened_get_local_mtu(packet),
            l2cap_event_channel_opened_get_remote_mtu(packet),l2cap_event_channel_opened_get_mode(packet),
            l2cap_event_channel_opened_get_flush_timeout(packet),
            l2cap_event_channel_opened_get_incoming(packet)?"true":"false");
        packet_handler(type,channel,packet,size);
        relay_hid_publish_open();
        hid_device_t *device=&hid_device_singleton;
        if(status && hid_cid && device->cid==hid_cid && local_cid &&
           (device->con_handle==HCI_CON_HANDLE_INVALID || device->con_handle==handle) &&
           (local_cid==device->control_cid || local_cid==device->interrupt_cid)){
            /* A failed L2CAP OPENED frees that channel without a CLOSED event.
             * Retire the dead CID so a sibling close can finish the session. */
            if(local_cid==device->control_cid)device->control_cid=0;
            if(local_cid==device->interrupt_cid)device->interrupt_cid=0;
            relay_hid_link.pending_size=0;
            if(device->control_cid || device->interrupt_cid)hid_device_disconnect(hid_cid);
            else{
                relay_hid_forget_acl(handle);
                device->cid=0;
            }
        }
        return;
    }else if(type==HCI_EVENT_PACKET&&size>=4&&packet[0]==L2CAP_EVENT_CHANNEL_CLOSED){
        uint16_t cid=l2cap_event_channel_closed_get_local_cid(packet);
        if(cid==relay_hid_link.control || cid==relay_hid_link.interrupt){
            if(cid==relay_hid_link.control)relay_hid_link.control=0;
            else relay_hid_link.interrupt=0;
            relay_hid_link.pending_size=0;
            /* Either channel going away makes this HID session unusable.
             * The upstream callback on the second close is deduplicated. */
            packet_handler(type,channel,packet,size);
            relay_hid_publish_closed(0);
            return;
        }
    }
    packet_handler(type,channel,packet,size);
}
static uint8_t relay_l2cap_register_service(btstack_packet_handler_t handler,uint16_t psm,uint16_t mtu,gap_security_level_t level){
    return l2cap_register_service(handler==packet_handler?relay_hid_l2cap_packet_handler:handler,psm,mtu,level);
}
static uint8_t relay_l2cap_create_channel(btstack_packet_handler_t handler,bd_addr_t address,uint16_t psm,uint16_t mtu,uint16_t *out_cid){
    return l2cap_create_channel(handler==packet_handler?relay_hid_l2cap_packet_handler:handler,address,psm,mtu,out_cid);
}
static int relay_hid_response_to(hid_device_state_t state){
    switch(state){
        case HID_DEVICE_W2_GET_REPORT:return HID_MESSAGE_TYPE_GET_REPORT;
        case HID_DEVICE_W2_SET_REPORT:return HID_MESSAGE_TYPE_SET_REPORT;
        case HID_DEVICE_W2_GET_PROTOCOL:return HID_MESSAGE_TYPE_GET_PROTOCOL;
        case HID_DEVICE_W2_SET_PROTOCOL:return HID_MESSAGE_TYPE_SET_PROTOCOL;
        default:return -1;
    }
}
static uint8_t relay_send_hid_control(uint16_t channel,const uint8_t *data,uint16_t len,int prepared){
    const hid_device_t *device=&hid_device_singleton;
    if(!channel||channel!=device->control_cid||!len){
        return prepared?l2cap_send_prepared(channel,len):l2cap_send(channel,data,len);
    }
    int response_to=prepared?relay_hid_response_to(device->state):-1;
    unsigned transaction=data[0]>>4;
    unsigned parameter=data[0]&15;
    int handshake_result=transaction==HID_MESSAGE_TYPE_HANDSHAKE?(int)parameter:-1;
    int has_report=response_to==HID_MESSAGE_TYPE_GET_REPORT;
    int report_type=has_report?(int)device->report_type:-1;
    int report_id=has_report?(int)device->report_id:-1;
    unsigned handle=device->con_handle, hid_cid=device->cid, protocol_mode=device->protocol_mode;
    uint8_t status=prepared?l2cap_send_prepared(channel,len):l2cap_send(channel,data,len);
    printf("{\"event\":\"HID_CONTROL_TX\",\"handle\":%u,\"hid_cid\":%u,\"l2cap_cid\":%u,"
        "\"response_to\":%d,\"transaction\":%u,\"parameter\":%u,\"report_type\":%d,"
        "\"report_id\":%d,\"bytes\":%u,\"protocol_mode\":%u,\"handshake_result\":%d,"
        "\"l2cap_status\":%u,\"payload_logged\":false}\n",
        handle,hid_cid,channel,response_to,transaction,parameter,report_type,report_id,len,protocol_mode,handshake_result,status);
    return status;
}
static uint8_t relay_l2cap_send_prepared(uint16_t channel,uint16_t len){
    return relay_send_hid_control(channel,l2cap_get_outgoing_buffer(),len,1);
}
static uint8_t relay_l2cap_send(uint16_t channel,const uint8_t *data,uint16_t len){
    return relay_send_hid_control(channel,data,len,0);
}

static int relay_get_report_traced(uint16_t cid,hid_report_type_t type,uint16_t id,int *len,uint8_t *data){
    int result=relay_get_report(cid,type,id,len,data);
    printf("{\"event\":\"HID_GET_REPORT_CALLBACK\",\"hid_cid\":%u,\"report_type\":%u,\"report_id\":%u,"
        "\"provided_body_bytes\":%d,\"descriptor_body_bytes\":%u,\"callback_result\":%d,\"payload_logged\":false}\n",
        cid,(unsigned)type,id,*len,hid_device_singleton.expected_report_size,result);
    return result;
}
static void relay_set_report_traced(uint16_t cid,hid_report_type_t type,int len,uint8_t *data){
    int report_id=len>0&&relay_control_has_id(&hid_device_singleton)?data[0]:-1;
    printf("{\"event\":\"HID_SET_REPORT_CALLBACK\",\"hid_cid\":%u,\"report_type\":%u,\"report_id\":%d,"
        "\"bytes_including_report_id_if_present\":%d,\"descriptor_validated\":true,\"payload_logged\":false}\n",
        cid,(unsigned)type,report_id,len);
    relay_set_report(cid,type,len,data);
}
static void relay_report_data_traced(uint16_t cid,hid_report_type_t type,uint16_t id,int len,uint8_t *data){
    relay_report_deliveries++;
    relay_report_data(cid,type,id,len,data);
}
void hid_device_register_report_request_callback(int (*callback)(uint16_t,hid_report_type_t,uint16_t,int*,uint8_t*)){
    relay_get_report=callback;
    relay_upstream_register_report_request_callback(callback?relay_get_report_traced:NULL);
}
void hid_device_register_set_report_callback(void (*callback)(uint16_t,hid_report_type_t,int,uint8_t*)){
    relay_set_report=callback;
    relay_upstream_register_set_report_callback(callback?relay_set_report_traced:NULL);
}
void hid_device_register_report_data_callback(void (*callback)(uint16_t,hid_report_type_t,uint16_t,int,uint8_t*)){
    relay_report_data=callback;
    relay_upstream_register_report_data_callback(callback?relay_report_data_traced:NULL);
}
static void relay_hid_event(uint8_t type,uint16_t channel,uint8_t *packet,uint16_t size){
    if(type==HCI_EVENT_PACKET&&size>=5&&packet[0]==HCI_EVENT_HID_META){
        uint8_t sub=hci_event_hid_meta_get_subevent_code(packet);
        if(sub==HID_SUBEVENT_CONNECTION_OPENED&&size>=6&&hid_subevent_connection_opened_get_status(packet)==0){
            if(size!=sizeof(relay_hid_link.pending_open) || relay_open_cid ||
               hid_subevent_connection_opened_get_con_handle(packet)!=relay_hid_link.handle)return;
            memcpy(relay_hid_link.pending_open,packet,size);
            relay_hid_link.pending_size=size;
            relay_hid_publish_open();
            return;
        }
        if(sub==HID_SUBEVENT_CONNECTION_CLOSED){
            uint16_t reported=hid_subevent_connection_closed_get_hid_cid(packet);
            relay_hid_publish_closed(reported);
            if(!relay_open_cid)relay_hid_forget_acl(relay_hid_link.handle);
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
    return relay_open_cid==cid&&relay_hid_has_channels(device);
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
    if(!device||!relay_hid_channels_open(cid))return ERROR_CODE_UNKNOWN_CONNECTION_IDENTIFIER;
    *remote_mtu=l2cap_get_remote_mtu_for_local_cid(device->interrupt_cid);
    int status=l2cap_send(device->interrupt_cid,(uint8_t*)message,length);
    if(device->user_request_can_send_now)l2cap_request_can_send_now_event(device->interrupt_cid);
    return status;
}

/* Exercise the actual pinned upstream close handler, including both channel
 * close orders. No controller, USB handle or HCI transport is initialized. */
static unsigned test_opens,test_closes;
static uint16_t test_closed_cid,test_closed_channel;
static void test_event(uint8_t type,uint16_t channel,uint8_t *packet,uint16_t size){
    if(type==HCI_EVENT_PACKET&&size>=15&&packet[0]==HCI_EVENT_HID_META&&
       packet[2]==HID_SUBEVENT_CONNECTION_OPENED&&!hid_subevent_connection_opened_get_status(packet))test_opens++;
    if(type==HCI_EVENT_PACKET&&size>=5&&packet[0]==HCI_EVENT_HID_META&&packet[2]==HID_SUBEVENT_CONNECTION_CLOSED){
        test_closes++;test_closed_cid=hid_subevent_connection_closed_get_hid_cid(packet);test_closed_channel=channel;
    }
}
int relay_hid_lifecycle_self_test(int *cases){
    int failures=0;bd_addr_t address={1,2,3,4,5,6};
    const uint8_t get[]={0x4b,0x05,0x40,0x00};
    relay_control_metadata_t meta=relay_control_request_metadata(get,sizeof(get),1);
    (*cases)++;failures+=meta.transaction!=HID_MESSAGE_TYPE_GET_REPORT||meta.report_type!=HID_REPORT_TYPE_FEATURE||
        meta.report_id!=0x05||meta.host_buffer!=64;
    meta=relay_control_request_metadata(get,sizeof(get)-2,1);
    (*cases)++;failures+=meta.report_id!=0x05||meta.host_buffer!=-1;
    const uint8_t proto[]={0x71};
    meta=relay_control_request_metadata(proto,sizeof(proto),1);
    (*cases)++;failures+=meta.transaction!=HID_MESSAGE_TYPE_SET_PROTOCOL||meta.protocol_arg!=HID_PROTOCOL_MODE_REPORT||
        meta.report_id!=-1||meta.report_type!=-1;
    meta=relay_control_request_metadata(NULL,0,1);
    (*cases)++;failures+=meta.transaction!=-1||meta.report_id!=-1;
    hid_device_register_packet_handler(test_event);
    for(int order=0;order<2;order++){
        test_opens=test_closes=0;test_closed_cid=0;test_closed_channel=0;
        hid_device_setup_instance(&hid_device_singleton,address);
        hid_device_singleton.control_cid=0x41;hid_device_singleton.interrupt_cid=0x42;
        hid_device_singleton.con_handle=0x80;hid_device_singleton.incoming=1;
        uint16_t expected=hid_device_singleton.cid;
        uint8_t opened[26]={L2CAP_EVENT_CHANNEL_OPENED,24};
        little_endian_store_16(opened,9,0x80);
        little_endian_store_16(opened,11,order?PSM_HID_INTERRUPT:PSM_HID_CONTROL);
        little_endian_store_16(opened,13,order?0x42:0x41);
        opened[23]=1;
        relay_hid_l2cap_packet_handler(HCI_EVENT_PACKET,little_endian_read_16(opened,13),opened,sizeof(opened));
        (*cases)++;failures+=relay_hid_channels_open(expected)||test_opens||!relay_hid_link.pending_size;
        uint16_t psm=order?PSM_HID_CONTROL:PSM_HID_INTERRUPT;
        uint16_t other_cid=order?0x41:0x42;
        relay_hid_observe_l2cap_open(1,0x80,psm,other_cid);
        relay_hid_publish_open();
        relay_hid_observe_l2cap_open(0,0x81,psm,other_cid);
        relay_hid_publish_open();
        (*cases)++;failures+=relay_hid_channels_open(expected)||test_opens;
        little_endian_store_16(opened,11,psm);
        little_endian_store_16(opened,13,other_cid);
        relay_hid_l2cap_packet_handler(HCI_EVENT_PACKET,other_cid,opened,sizeof(opened));
        relay_hid_publish_open();
        (*cases)++;failures+=test_opens!=1||relay_hid_link.pending_size;
        (*cases)++;failures+=!relay_hid_channels_open(expected);
        uint8_t closed[]={L2CAP_EVENT_CHANNEL_CLOSED,2,0,0};
        little_endian_store_16(closed,2,order?0x41:0x42);
        relay_hid_l2cap_packet_handler(HCI_EVENT_PACKET,little_endian_read_16(closed,2),closed,sizeof(closed));
        (*cases)++;failures+=test_closes!=1||test_closed_cid!=expected||relay_hid_channels_open(expected);
        little_endian_store_16(closed,2,order?0x42:0x41);
        relay_hid_l2cap_packet_handler(HCI_EVENT_PACKET,little_endian_read_16(closed,2),closed,sizeof(closed));
        (*cases)++;failures+=test_closes!=1||test_closed_cid!=expected||test_closed_channel!=expected;
        (*cases)++;failures+=hid_device_singleton.cid!=0||relay_open_cid!=0;
    }
    for(int opened_control=0;opened_control<2;opened_control++){
        test_opens=test_closes=0;
        hid_device_setup_instance(&hid_device_singleton,address);
        hid_device_singleton.control_cid=0x41;hid_device_singleton.interrupt_cid=0x42;
        hid_device_singleton.con_handle=0x80;hid_device_singleton.incoming=1;
        uint8_t opened[26]={L2CAP_EVENT_CHANNEL_OPENED,24};
        little_endian_store_16(opened,9,0x80);
        opened[23]=1;
        if(opened_control){
            little_endian_store_16(opened,11,PSM_HID_CONTROL);
            little_endian_store_16(opened,13,0x41);
            relay_hid_l2cap_packet_handler(HCI_EVENT_PACKET,0x41,opened,sizeof(opened));
        }
        opened[2]=L2CAP_CONNECTION_RESPONSE_RESULT_RTX_TIMEOUT;
        little_endian_store_16(opened,11,PSM_HID_INTERRUPT);
        little_endian_store_16(opened,13,0x42);
        relay_hid_l2cap_packet_handler(HCI_EVENT_PACKET,0x42,opened,sizeof(opened));
        (*cases)++;failures+=hid_device_singleton.interrupt_cid||relay_hid_link.pending_size||test_opens;
        uint8_t closed[]={L2CAP_EVENT_CHANNEL_CLOSED,2,0x41,0};
        relay_hid_l2cap_packet_handler(HCI_EVENT_PACKET,0x41,closed,sizeof(closed));
        (*cases)++;failures+=hid_device_singleton.cid||test_closes;
    }
    relay_hid_link.handle=0x90;relay_hid_link.control=0x41;relay_hid_link.pending_size=15;
    relay_hid_forget_acl(0x91);
    (*cases)++;failures+=!relay_hid_link.pending_size;
    relay_hid_forget_acl(0x90);
    (*cases)++;failures+=relay_hid_link.pending_size||relay_hid_link.control;
    relay_client=NULL;hid_device_deinit();return failures;
}
