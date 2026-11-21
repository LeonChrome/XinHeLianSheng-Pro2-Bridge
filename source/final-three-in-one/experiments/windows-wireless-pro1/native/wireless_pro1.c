/* Nintendo input -> Windows -> Pro1 / Xbox One S / DualSense Classic HID.
 * Optional BTstack backend for local personal experiments. Protocol/USB input
 * live in the existing .NET assistant. No driver/registry/system pairing edits.
 * HCI completion is explicitly NOT certified receiver input or game latency.
 */
#include <winsock2.h>
#include <ws2tcpip.h>
#include <Windows.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <inttypes.h>
#include <math.h>
#include "btstack.h"
#include "btstack_run_loop_windows.h"
#include "hci_transport_usb.h"
#include "classic/btstack_link_key_db_memory.h"
#include "classic/device_id_server.h"
#include "btstack_chipset_realtek.h"
#include "pro1_descriptor.h"
#include "wireless_descriptors.h"
#include "relay_flow.h"
#include "relay_recovery.h"
#include "relay_hid_schedule.h"
#include "utf8_files.h"
#include "relay_btkey_adapter.h"
#include "relay_safe_trace.h"
#include "relay_transition_capture.h"

#define HEADER 48
#define MAX_PAYLOAD 362
#define CONTROL_QUEUE 32
#define EDGE_QUEUE 16
#define ACK_QUEUE 256
#define SAMPLE_LIMIT 180000
typedef struct {uint8_t data[MAX_PAYLOAD];int len;uint64_t sequence;int64_t ticks;} frame_t;
typedef struct {int fresh,hid;double sent;} ack_t;
static frame_t latest,controls[CONTROL_QUEUE];
static frame_t edges[EDGE_QUEUE];
static unsigned edge_head,edge_tail,edge_count;
static unsigned control_head,control_tail,control_count;
static int have_latest,send_requested,connected,ever_connected,stopping,failed,completed,realtek,ats,stack_working;
static int profile; /* 0=Pro1, 1=Xbox One S Classic HID, 2=DualSense Classic HID */
static int xbox_battery=-1;
static const char *profile_names[]={"pro1","xbox","ps5"};
static const char *device_names[]={"Pro Controller","Xbox Wireless Controller","Wireless Controller"};
static const uint8_t *profile_descriptor(void){return profile==2?ps5_descriptor:profile==1?xbox_descriptor:pro1_descriptor;}
static unsigned profile_descriptor_size(void){return profile==2?sizeof(ps5_descriptor):profile==1?sizeof(xbox_descriptor):sizeof(pro1_descriptor);}
static hid_sdp_record_t profile_sdp_parameters(void){
    /* Pro1 advertises no host Sniff Subrating latency/timeout requirement.
     * Keep the other profiles and supervision timeout unchanged. */
    uint16_t ssr=profile==0?UINT16_MAX:24;
    return (hid_sdp_record_t){
        .hid_device_subclass=8,.hid_country_code=0,.hid_virtual_cable=1,
        .hid_remote_wake=1,.hid_reconnect_initiate=1,.hid_normally_connectable=true,
        .hid_boot_device=false,.hid_ssr_host_max_latency=ssr,
        .hid_ssr_host_min_timeout=ssr,.hid_supervision_timeout=3200,
        .hid_descriptor=profile_descriptor(),.hid_descriptor_size=(uint16_t)profile_descriptor_size(),
        .device_name=device_names[profile]
    };
}
static uint16_t cid;static hci_con_handle_t acl=HCI_CON_HANDLE_INVALID;
static bd_addr_t local,peer;static int have_peer;
static bd_addr_t trace_acl_peer;
static int trace_acl_peer_known;
static uint8_t session[16],last_report[MAX_PAYLOAD];static int last_report_len;
static uint64_t incoming_sequence,backend_sequence,source_received,source_superseded,source_stale,source_not_ready;
static uint64_t malformed,protocol_replies,host_commands,submitted,completions,ledger_overflow;
static uint64_t digital_transitions_queued,send_failures;
static uint64_t digital_transitions_expired,digital_resyncs,protocol_expired,send_rearms;
static double send_request_time,last_send_event,last_sniff_exit;
static double last_flow_snapshot;
static relay_recovery_t recovery;
static uint16_t pending_recovery_cid;
static double inbound_acl_until;
static int pro1_delivery,any_switch_init,last_delivery,timed_observation;
static double full_input_since;
static int require_switch_init;
static int peer_unplugged;
static unsigned connection_count,total_reconnect_attempts;
static double connected_since,connected_seconds;
static unsigned sniff_exit_attempts;static int link_in_sniff;
static relay_hid_fragments_t hid_fragments;
static unsigned hid_in_flight,hid_in_flight_max;
static uint64_t hid_mode_transitions;
static double requested_report_hz=60;
static uint64_t btkey_last_fresh_sequence;
static int btkey_latest_physical,btkey_source_released;
static char *btkey_tag_path;
static uint64_t btkey_tag_stamp;
static int btkey_tag_present;
static double btkey_tag_poll;
static unsigned wire_trace_count;
static double pro1_active_transition_at;
static unsigned pro1_active_packets;
static unsigned menu_buttons_logged;
static int first_wire_input;
static uint64_t sending_sequence;
static int64_t sending_source_ticks;
static int wire_timer_known;static uint8_t wire_last_timer;static double wire_last_stamp;
/* Switch controls its own Sniff/TSI schedule. Sending from this raw WinUSB
 * transport must not imply Exit Sniff (there is no Linux socket force_active).
 * Keep the existing bounded active-link policy for Xbox and PS5 only. */
static int should_exit_peer_sniff(double stamp){
    return profile!=0&&connected&&link_in_sniff&&sniff_exit_attempts<3&&stamp-last_sniff_exit>=5;
}
static ack_t ledger[ACK_QUEUE];static unsigned ack_head,ack_tail,ack_count;
static double submitted_times[SAMPLE_LIMIT],completed_times[SAMPLE_LIMIT];static unsigned submitted_count,completed_count;
static int send_is_fresh,send_is_hid,seconds=180,wait_seconds=180,disconnect_reason=-1;
enum { EXIT_PRO1_KEY_SIZE_UNSUPPORTED = 23 };
static hci_con_handle_t pending_key_size_handle = HCI_CON_HANDLE_INVALID;
static int pro1_key_size_unsupported;
static double key_size_error_time;
static double qpc_frequency,boot,ready,stop_time,last_heartbeat,last_progress;
static const char * result_path,*firmware_folder;static int firmware_verified,version_readback;
static SOCKET sock=INVALID_SOCKET;static WSAEVENT socket_event=WSA_INVALID_EVENT;static struct sockaddr_in managed;
static btstack_data_source_t ipc_source;static btstack_timer_source_t timer;
static btstack_packet_callback_registration_t event_registration;
static const hci_transport_t *base_transport;static hci_transport_t transport;
static void(*stack_receive)(uint8_t,uint8_t*,uint16_t);
static uint8_t hid_record[2048],did_record[256];
enum { PRO1_EIR_BYTES=240 };
static uint8_t pro1_eir[PRO1_EIR_BYTES];
static void build_pro1_eir(uint8_t data[PRO1_EIR_BYTES]){
    static const char name[]="Pro Controller";
    memset(data,0,PRO1_EIR_BYTES);
    unsigned pos=0;
    data[pos++]=sizeof(name);data[pos++]=0x09;
    memcpy(data+pos,name,sizeof(name)-1);pos+=sizeof(name)-1;
    data[pos++]=5;data[pos++]=0x03;
    little_endian_store_16(data,pos,0x1124);pos+=2;
    little_endian_store_16(data,pos,0x1200);pos+=2;
    data[pos++]=9;data[pos++]=0x10;
    little_endian_store_16(data,pos,0x0002);pos+=2; /* USB vendor ID source */
    little_endian_store_16(data,pos,0x057e);pos+=2;
    little_endian_store_16(data,pos,0x2009);pos+=2;
    little_endian_store_16(data,pos,0x0100);
}
enum { RELAY_PAIRING_PENDING=5, RELAY_PAIRING_ENDED=6, RELAY_PAIRING_CONFIRM=8 };
enum { PAIRING_SUBMITTED=1, PAIRING_TIMEOUT=2, PAIRING_PEER_CANCELLED=3, PAIRING_HCI_REJECTED=4 };
#define SWITCH_CONFIRM_SECONDS 18
typedef struct {
    bd_addr_t address;
    hci_con_handle_t handle;
    uint8_t id;
    double deadline;
} switch_pairing_request_t;
static switch_pairing_request_t switch_pairing;
static uint8_t switch_pairing_last_id, switch_pairing_accepted_id;
static int defer_switch_pairing;
extern int relay_set_instance(const char *instance);
extern int relay_target_path_allowed(const char *path);
extern int relay_hid_send_checked(uint16_t cid,const uint8_t*message,uint16_t length,uint16_t*remote_mtu);
static void stop_backend(void);
static void request_send(void);
static double now(void){LARGE_INTEGER t;QueryPerformanceCounter(&t);return t.QuadPart/qpc_frequency;}
static int64_t ticks(void){LARGE_INTEGER t;QueryPerformanceCounter(&t);return t.QuadPart;}
static uint64_t read64(const uint8_t*p){uint64_t v;memcpy(&v,p,8);return v;}
static void write64(uint8_t*p,uint64_t v){memcpy(p,&v,8);}
static int owns_acl(uint16_t handle){return acl!=HCI_CON_HANDLE_INVALID&&(handle&0xfff)==acl;}
static void emit_ipc(int kind,const uint8_t*payload,int len){
    uint8_t message[HEADER+MAX_PAYLOAD]={0};memcpy(message,"PRW1",4);message[4]=1;message[5]=(uint8_t)kind;
    little_endian_store_16(message,6,(uint16_t)len);memcpy(message+8,session,16);write64(message+24,++backend_sequence);write64(message+32,(uint64_t)ticks());
    if(len)memcpy(message+HEADER,payload,len);
    if(sendto(sock,(const char*)message,HEADER+len,0,(struct sockaddr*)&managed,sizeof(managed))!=HEADER+len){failed=1;puts("{\"event\":\"MANAGED_IPC_SEND_FAILED\"}");stop_backend();}
}
static void emit_link(int state,int reason){uint8_t payload[8];memcpy(payload,local,6);payload[6]=(uint8_t)state;payload[7]=(uint8_t)reason;emit_ipc(4,payload,8);}
static int begin_switch_pairing(const bd_addr_t address,hci_con_handle_t handle,double stamp){
    if(switch_pairing.id||switch_pairing_accepted_id||switch_pairing_last_id==UINT8_MAX||handle==HCI_CON_HANDLE_INVALID)return 0;
    memcpy(switch_pairing.address,address,sizeof(switch_pairing.address));
    switch_pairing.handle=handle;
    switch_pairing.id=++switch_pairing_last_id;
    switch_pairing.deadline=stamp+SWITCH_CONFIRM_SECONDS;
    return 1;
}
static void end_switch_pairing(int result){
    uint8_t id=switch_pairing.id;
    if(!id)return;
    switch_pairing.id=0;
    printf("{\"event\":\"SWITCH_PAIRING_ENDED\",\"request_id\":%u,\"result\":%d}\n",id,result);
    emit_link(RELAY_PAIRING_ENDED,result);
}
static int switch_pairing_current(uint8_t id,hci_con_handle_t handle,double stamp){
    return id&&id==switch_pairing.id&&handle==switch_pairing.handle&&stamp<switch_pairing.deadline;
}
static void confirm_switch_pairing(uint8_t id){
    if(stopping||profile!=0||!defer_switch_pairing||!switch_pairing_current(id,acl,now()))return;
    hci_connection_t *connection=hci_connection_for_bd_addr_and_type(switch_pairing.address,BD_ADDR_TYPE_ACL);
    if(!connection||connection->con_handle!=switch_pairing.handle){
        end_switch_pairing(PAIRING_PEER_CANCELLED);
        return;
    }
    hci_con_handle_t handle=connection->con_handle;
    int status=gap_ssp_confirmation_response(switch_pairing.address);
    if(!status)switch_pairing_accepted_id=id;
    end_switch_pairing(status ? PAIRING_HCI_REJECTED : PAIRING_SUBMITTED);
    printf("{\"event\":\"SWITCH_PAIRING_CONFIRMATION\",\"request_id\":%u,\"hci_status\":%d}\n",id,status);
    if(status)gap_disconnect(handle);
}
static void clear_pending_input(void){
    have_latest=send_requested=0;edge_head=edge_tail=edge_count=0;
    control_head=control_tail=control_count=0;last_report_len=0;
}
static void release_pro1_controls(void){
    uint8_t neutral[49]={0x30};
    neutral[7]=neutral[10]=0x80;
    for(int i=0;i<3;i++)little_endian_store_16(neutral,13+i*12+4,4096);
    relay_btkey_observe_snapshot(neutral,sizeof(neutral));
    btkey_source_released=1;btkey_latest_physical=0;have_latest=0;
}
static void close_link_accounting(void){
    if(connected && profile==0)last_delivery=pro1_delivery;
    if(connected_since){connected_seconds+=now()-connected_since;connected_since=0;}
    if(profile==0&&connected&&cid){
        /* ACL loss can precede the HID close callback. Clear the upstream
         * channel before our CID is discarded and the next session opens. */
        uint8_t closed[]={HCI_EVENT_HID_META,3,HID_SUBEVENT_CONNECTION_CLOSED,0,0};
        little_endian_store_16(closed,3,cid);
        relay_btkey_packet_handler(HCI_EVENT_PACKET,0,closed,sizeof(closed));
    }
    connected=0;cid=0;pro1_delivery=RELAY_PRO1_SIMPLE;full_input_since=0;clear_pending_input();
    wire_timer_known=0;pro1_active_transition_at=0;pro1_active_packets=0;
}
static int begin_recovery(int reason){
    if(profile!=0||!have_peer||peer_unplugged||stopping||pro1_key_size_unsupported||!ever_connected)return 0;
    double stamp=now();
    int full=pro1_delivery==RELAY_PRO1_CONFIRMED;
    int handoff=full && pro1_active_transition_at>0 && stamp-pro1_active_transition_at<=2;
    relay_recovery_lost(&recovery,stamp,handoff,full);
    close_link_accounting();emit_link(4,reason);
    printf("{\"event\":\"HID_RECOVERY_WAIT\",\"attempts_used\":%u,\"limit\":2,\"waiting_old_acl_release\":%s,\"same_peer_only\":true,\"physical_input_kept\":true}\n",recovery.attempts,acl==HCI_CON_HANDLE_INVALID?"false":"true");
    return 1;
}
static void recovery_failed(uint8_t status){
    if(recovery.phase!=RELAY_RECOVERY_DIALING)return;
    pending_recovery_cid=0;
    int transient=status==ERROR_CODE_PAGE_TIMEOUT || status==ERROR_CODE_CONNECTION_TIMEOUT ||
        status==ERROR_CODE_CONNECTION_REJECTED_DUE_TO_LIMITED_RESOURCES ||
        status==ERROR_CODE_CONNECTION_ACCEPT_TIMEOUT_EXCEEDED ||
        status==ERROR_CODE_LMP_RESPONSE_TIMEOUT_LL_RESPONSE_TIMEOUT ||
        status==L2CAP_CONNECTION_RESPONSE_RESULT_REFUSED_RESOURCES ||
        status==L2CAP_CONNECTION_RESPONSE_RESULT_RTX_TIMEOUT;
    if(transient){
        relay_recovery_dial_failed(&recovery,now());
        printf("{\"event\":\"HID_RECOVERY_BACKOFF\",\"status\":%u,\"attempts\":%u,\"retry_in_ms\":%.0f}\n",
               status,recovery.attempts,1000*fmax(0,recovery.due-now()));
        return;
    }
    failed=1;emit_link(3,status);stop_backend();
}
static int valid_input(const uint8_t*report,int len){
    if(profile==2)return len==78&&report[0]==0x31;
    if(profile==1)return (len==17&&report[0]==1)||(len==2&&(report[0]==2||report[0]==4));
    return (len==49&&(report[0]==0x30||report[0]==0x21))||(len==362&&report[0]==0x31)||(len==12&&report[0]==0x3f);
}
static int same_digital_state(const frame_t*previous,const uint8_t*report,int len){
    if(profile==2)return previous->len==len&&len==78&&!memcmp(previous->data+9,report+9,4)&&((previous->data[34]^report[34])&0x80)==0;
    if(profile==1)return previous->len==len&&len==17&&!memcmp(previous->data+13,report+13,4);
    if(previous->len!=len||previous->data[0]!=report[0])return 0;
    int offset=report[0]==0x3f?1:3;return !memcmp(previous->data+offset,report+offset,3);
}
static int decode_ipc(const uint8_t*p,int size,uint64_t*sequence,int*kind,int*len){
    if(size<HEADER||memcmp(p,"PRW1",4)||p[4]!=1||memcmp(p+8,session,16))return 0;
    *kind=p[5];*len=little_endian_read_16(p,6);*sequence=read64(p+24);
    if((*kind<1||(*kind>6&&*kind!=RELAY_PAIRING_CONFIRM))||*len>MAX_PAYLOAD||size!=HEADER+*len||!*sequence)return 0;
    if(*kind==RELAY_PAIRING_CONFIRM&&(*len!=1||!p[HEADER]))return 0;
    for(int i=40;i<48;i++)if(p[i])return 0;
    return 1;
}
static void request_send(void){
    if(profile==0)return; /* btkeyLib owns the Pro1 send timer and ACK lifecycle. */
    if(!stopping&&connected&&!send_requested&&(control_count||edge_count||have_latest)&&relay_hid_channels_open(cid)){
        send_requested=1;send_request_time=now();hid_device_request_can_send_now_event(cid);
    }
}
static void prune_edges(int64_t stamp){
    while(edge_count&&(stamp-edges[edge_head].ticks)/qpc_frequency>.1){edge_head=(edge_head+1)%EDGE_QUEUE;edge_count--;digital_transitions_expired++;}
}
static void trace_flow(const char*reason){
    relay_hid_flow_t hid;relay_usb_flow_t usb;
    relay_hid_flow_snapshot(cid,&hid);relay_usb_flow_snapshot(&usb);
    int free_acl=acl!=HCI_CON_HANDLE_INVALID?hci_number_free_acl_slots_for_handle(acl):0;
    printf("{\"event\":\"FLOW_STATE\",\"build\":\"20261117-hid2\",\"reason\":\"%s\",\"hci_reserved\":%s,\"free_acl\":%d,\"hci_acl_ready\":%s,\"usb_acl_busy\":%d,\"usb_acl_done\":%d,\"usb_acl_error\":%u,\"usb_acl_bytes\":%u,\"usb_acl_status\":%"PRIu64",\"usb_cmd_busy\":%d,\"usb_cmd_done\":%d,\"usb_cmd_error\":%u,\"usb_cmd_status\":%"PRIu64",\"hid_found\":%d,\"hid_state\":%d,\"hid_waiting\":%d,\"hid_control\":%u,\"hid_interrupt\":%u,\"l2cap_ready\":%d,\"app_waiting\":%d,\"latest_pending\":%d,\"ack_queue\":%u}\n",
        reason,hci_is_packet_buffer_reserved()?"true":"false",free_acl,acl!=HCI_CON_HANDLE_INVALID&&hci_can_send_acl_packet_now(acl)?"true":"false",
        usb.acl_busy,usb.acl_done,usb.acl_error,usb.acl_bytes,usb.acl_io_status,usb.command_busy,usb.command_done,usb.command_error,usb.command_io_status,
        hid.found,hid.state,hid.pending,hid.control_cid,hid.interrupt_cid,hid.can_send,send_requested,have_latest,ack_count);
}
static void preserve_transition(const frame_t*frame,int64_t stamp){
    prune_edges(stamp);
    if((stamp-frame->ticks)/qpc_frequency>.1){digital_transitions_expired++;return;}
    if(edge_count==EDGE_QUEUE){
        digital_transitions_expired+=edge_count;edge_head=edge_tail=edge_count=0;digital_resyncs++;
        if(digital_resyncs<=5)printf("{\"event\":\"DIGITAL_BACKPRESSURE_RESYNC\",\"count\":%"PRIu64",\"action\":\"keep_latest_complete_state\",\"disconnect\":false}\n",digital_resyncs);
        return;
    }
    edges[edge_tail]=*frame;edge_tail=(edge_tail+1)%EDGE_QUEUE;edge_count++;digital_transitions_queued++;
}
static void ipc_ready(btstack_data_source_t*ds,btstack_data_source_callback_type_t callback){
    (void)ds;(void)callback;WSANETWORKEVENTS events;WSAEnumNetworkEvents(sock,socket_event,&events);
    for(int i=0;i<512;i++){
        uint8_t p[HEADER+MAX_PAYLOAD+1];struct sockaddr_in sender;int slen=sizeof(sender);
        int size=recvfrom(sock,(char*)p,sizeof(p),0,(struct sockaddr*)&sender,&slen);if(size<0)break;
        uint64_t sequence;int kind,len;
        if(sender.sin_addr.s_addr!=htonl(INADDR_LOOPBACK)||sender.sin_port!=managed.sin_port||!decode_ipc(p,size,&sequence,&kind,&len)||sequence<=incoming_sequence){malformed++;continue;}
        incoming_sequence=sequence;last_heartbeat=now();
        if(kind==5&&len==0)continue;
        if(kind==6&&len==0){
            if(!stopping){completed=!require_switch_init;stop_backend();}
            continue;
        }
        if(kind==RELAY_PAIRING_CONFIRM){confirm_switch_pairing(p[HEADER]);continue;}
        if(kind!=1&&kind!=2){malformed++;continue;}
        if(stopping)continue;
        if(!valid_input(p+HEADER,len)||(kind==1&&p[HEADER]==0x21)){malformed++;continue;}
        if(profile==0){
            /* The Windows btkey engine owns ACKs/NFC; managed supplies only
             * canonical latest state. A neutral safety release is not a new
             * physical report and must never inflate the fresh counter. */
            if(p[HEADER]!=0x30||len!=49)continue;
            if(kind==1)source_received++;else protocol_replies++;
            double age=(ticks()-(int64_t)read64(p+32))/qpc_frequency;
            if(age<-.01||age>.1){source_stale++;continue;}
            if(!connected)source_not_ready++;
            if(have_latest)source_superseded++;
            memcpy(latest.data,p+HEADER,49);latest.len=49;latest.sequence=sequence;latest.ticks=(int64_t)read64(p+32);have_latest=1;
            btkey_latest_physical=kind==1;btkey_source_released=0;
            relay_btkey_observe_snapshot(latest.data,latest.len);
            continue;
        }
        if(profile==1&&len==2&&p[HEADER]==4)xbox_battery=p[HEADER+1];
        if(kind==1){
            source_received++;
            double age=(ticks()-(int64_t)read64(p+32))/qpc_frequency;
            if(age<-.01||age>.1){source_stale++;continue;}
            if(!connected){source_not_ready++;continue;}
            if(have_latest){
                if(!same_digital_state(&latest,p+HEADER,len)){
                    preserve_transition(&latest,ticks());
                }else source_superseded++;
            }
            memcpy(latest.data,p+HEADER,len);latest.len=len;latest.sequence=sequence;latest.ticks=(int64_t)read64(p+32);have_latest=1;
        }else{
            if(!connected){malformed++;continue;}
            if(control_count==CONTROL_QUEUE){control_head=(control_head+1)%CONTROL_QUEUE;control_count--;protocol_expired++;puts("{\"event\":\"CONTROL_BACKPRESSURE\",\"action\":\"discard_oldest_reply\",\"disconnect\":false}");}
            frame_t*f=&controls[control_tail];memcpy(f->data,p+HEADER,len);f->len=len;f->sequence=sequence;f->ticks=(int64_t)read64(p+32);
            control_tail=(control_tail+1)%CONTROL_QUEUE;control_count++;protocol_replies++;
        }
    }
    request_send();
}
static int compare_double(const void*a,const void*b){double x=*(const double*)a,y=*(const double*)b;return(x>y)-(x<y);}
static void series(FILE*out,double*times,unsigned count){
    fprintf(out,"{\"count\":%u,\"hz\":%.4f",count,count>1&&times[count-1]>times[0]?(count-1)/(times[count-1]-times[0]):0);
    if(count>1){double*gaps=malloc((count-1)*sizeof(double));if(gaps){for(unsigned i=1;i<count;i++)gaps[i-1]=1000*(times[i]-times[i-1]);unsigned n=count-1;qsort(gaps,n,sizeof(double),compare_double);
        fprintf(out,",\"p50_ms\":%.4f,\"p95_ms\":%.4f,\"p99_ms\":%.4f,\"max_ms\":%.4f",gaps[(n-1)/2],gaps[(unsigned)((n-1)*.95)],gaps[(unsigned)((n-1)*.99)],gaps[n-1]);free(gaps);}}
    fputs("}",out);
}
static void write_result(void){
    FILE*out=fopen(result_path,"w");if(!out){failed=1;return;}
    fprintf(out,"{\"profile\":\"%s\",\"transport\":\"bluetooth_classic_hid\",\"connections\":%u,\"reconnect_attempts\":%u,\"digital_expired\":%"PRIu64",\"digital_resyncs\":%"PRIu64",\"control_expired\":%"PRIu64",\"send_rearms\":%"PRIu64",\"sniff_exit_attempts\":%u,",profile_names[profile],connection_count,total_reconnect_attempts,digital_transitions_expired,digital_resyncs,protocol_expired,send_rearms,sniff_exit_attempts);
    if(profile==0)fprintf(out,"\"pro1_engine\":\"btkeyLib_4b49cb9\",\"requested_report_hz\":%.3f,\"normal_report_cap_hz\":%.3f,",requested_report_hz,fmin(requested_report_hz,60));
    fprintf(out,"\"mode_transitions\":%"PRIu64",\"hid_in_flight_max\":%u,",hid_mode_transitions,hid_in_flight_max);
    fprintf(out,"\"backend\":\"btstack_winusb_multimode\",\"completed\":%s,\"failed\":%s,\"ever_connected\":%s,\"disconnect_reason\":%d,\"connected_seconds\":%.3f,\"source_received\":%"PRIu64",\"source_superseded\":%"PRIu64",\"source_stale\":%"PRIu64",\"source_not_ready\":%"PRIu64",\"invalid_ipc\":%"PRIu64",\"host_output_commands\":%"PRIu64",\"protocol_replies\":%"PRIu64",\"fresh_submissions\":%"PRIu64",\"fresh_hci_completions\":%"PRIu64",\"ledger_overflow\":%"PRIu64",\"submitted\":",completed?"true":"false",failed?"true":"false",ever_connected?"true":"false",disconnect_reason,connected_seconds,source_received,source_superseded,source_stale,source_not_ready,malformed,host_commands,protocol_replies,submitted,completions,ledger_overflow);
    series(out,submitted_times,submitted_count);fputs(",\"hci_completed\":",out);series(out,completed_times,completed_count);
    if(profile==0){
        relay_btkey_stats_t stats;relay_btkey_get_stats(&stats);
        if(pro1_key_size_unsupported)fputs(",\"terminal_reason\":\"read_encryption_key_size_unsupported\"",out);
        fprintf(out,",\"switch_init_seen\":%s,\"last_hid_delivery\":%d,\"timed_observation\":%s,\"home_input_verified\":false,\"startup_simple_repeats\":%"PRIu64,
            any_switch_init?"true":"false",last_delivery,timed_observation?"true":"false",stats.startup_repeats);
        fprintf(out,",\"btkey\":{\"snapshots\":%"PRIu64",\"reports_sent\":%"PRIu64",\"ack_reports\":%"PRIu64",\"nfc_reports\":%"PRIu64",\"send_failures\":%"PRIu64",\"command_queue_peak\":%u,\"command_queue_overflows\":%"PRIu64"}",
            stats.snapshots,stats.send_successes,stats.subcommand_replies,stats.nfc_reports,stats.send_failures,stats.command_queue_peak,stats.command_overflows);
    }
    fprintf(out,",\"digital_transitions_queued\":%"PRIu64",\"l2cap_send_failures\":%"PRIu64",\"realtek_firmware_runtime_verified\":%s,\"receiver_rate_verified\":false,\"driver_changes\":0,\"registry_changes\":0,\"windows_pairing_imported\":false,\"keys_logged\":false,\"tag_writes\":0}\n",digital_transitions_queued,send_failures,firmware_verified?"true":"false");fclose(out);
}
/* Command Status contains no handle. Correlate its opcode with the actual
 * outbound request for this ACL before attributing a terminal radio error. */
static int key_size_command_failed(const uint8_t *packet,uint16_t size){
    if(size<6 || packet[1]+2!=size)return 0;
    int status_event=packet[0]==HCI_EVENT_COMMAND_STATUS;
    if(status_event && size!=6)return 0;
    if(!status_event && packet[0]!=HCI_EVENT_COMMAND_COMPLETE)return 0;
    uint16_t opcode=little_endian_read_16(packet,status_event?4:3);
    if(opcode!=HCI_OPCODE_HCI_READ_ENCRYPTION_KEY_SIZE)return 0;
    uint8_t status=packet[status_event?2:5];
    int matched=profile==0 && !stopping && pending_key_size_handle!=HCI_CON_HANDLE_INVALID && pending_key_size_handle==acl;
    if(!status_event || status!=ERROR_CODE_SUCCESS)pending_key_size_handle=HCI_CON_HANDLE_INVALID;
    return matched && status==ERROR_CODE_UNKNOWN_HCI_COMMAND;
}
/* Log only decoded HCI and L2CAP signaling metadata. The signaling tracer
 * handles fragments without dumping packet bodies. */
static void receive_packet(uint8_t type,uint8_t*p,uint16_t size){
    int completed_credit=0;
    if(profile==0)relay_capture_packet(type,1,p,size,now());
    if(type==HCI_ACL_DATA_PACKET&&profile==0)relay_safe_trace_acl(1,p,size,acl);
    int observing_acl_close=0;
    if(type==HCI_EVENT_PACKET&&size>=2){
        if(!pro1_key_size_unsupported && key_size_command_failed(p,size)){
            pro1_key_size_unsupported=1;
            failed=1;
            key_size_error_time=now();
            puts("{\"event\":\"PRO1_KEY_SIZE_UNSUPPORTED\",\"opcode\":5128,\"status\":1}");
        }
        if(p[0]==HCI_EVENT_COMMAND_COMPLETE&&size>=6){
            uint16_t opcode=little_endian_read_16(p,3);
            if(ats&&profile!=0&&opcode==HCI_OPCODE_HCI_READ_LOCAL_SUPPORTED_COMMANDS&&size>=70&&p[5]==0){
                unsigned advertised=(p[26]>>4)&1;
                p[26]&=(uint8_t)~(1u<<4);
                printf("{\"event\":\"ACTIONS_ENCRYPTION_KEY_SIZE_CAPABILITY\",\"advertised_by_radio\":%u,"
                       "\"exposed_to_btstack\":false,\"size_assumed_by_legacy_stack\":16,\"size_measured_by_override\":false}\n",advertised);
            }
            if(opcode&&((opcode>>10)!=0x3f))
                printf("{\"event\":\"HCI_COMMAND_COMPLETE\",\"opcode\":%u,\"status\":%u}\n",opcode,p[5]);
            if(opcode==HCI_OPCODE_HCI_READ_LOCAL_VERSION_INFORMATION&&size>=14&&p[5]==0){
                int rom=little_endian_read_16(p,12)==0x8852;
                if(realtek&&!rom)firmware_verified=1;
                printf("{\"event\":\"CONTROLLER_VERSION\",\"manufacturer\":%u,\"lmp_subversion\":%u,\"realtek_rom\":%s}\n",little_endian_read_16(p,10),little_endian_read_16(p,12),rom?"true":"false");
                if(realtek&&version_readback==2&&rom){failed=1;puts("{\"event\":\"REALTEK_STILL_ROM_ABORT\"}");stop_backend();}
            }
        }else if(p[0]==HCI_EVENT_COMMAND_STATUS&&size>=6){
            printf("{\"event\":\"HCI_COMMAND_STATUS\",\"opcode\":%u,\"status\":%u,\"cleanup\":%s}\n",little_endian_read_16(p,4),p[2],stopping?"true":"false");
        }else if(p[0]==HCI_EVENT_READ_REMOTE_VERSION_INFORMATION_COMPLETE&&size>=10){
            printf("{\"event\":\"REMOTE_VERSION_COMPLETE\",\"status\":%u,\"handle\":%u,\"manufacturer\":%u,\"lmp_subversion\":%u}\n",p[2],little_endian_read_16(p,3),little_endian_read_16(p,6),little_endian_read_16(p,8));
        }else if(p[0]==HCI_EVENT_CONNECTION_COMPLETE&&size>=13&&p[2]==0&&p[11]==1){
            bd_addr_t who;reverse_bd_addr(p+5,who);
            int pairing_peer_matches=(!switch_pairing.id&&!switch_pairing_accepted_id)||!bd_addr_cmp(who,switch_pairing.address);
            if(pairing_peer_matches&&(!have_peer||!bd_addr_cmp(who,peer))){
                acl=little_endian_read_16(p,3);
                pending_key_size_handle=HCI_CON_HANDLE_INVALID;
                memcpy(trace_acl_peer,who,sizeof(trace_acl_peer));
                trace_acl_peer_known=1;
                relay_safe_trace_acl_reset();
                if(profile==0)relay_capture_new_link(acl);
            }
        }
        else if(p[0]==HCI_EVENT_NUMBER_OF_COMPLETED_PACKETS&&size>=3){
            for(unsigned i=0;i<p[2]&&3+4*i+4<=size;i++)if(little_endian_read_16(p,3+4*i)==acl){
                unsigned count=little_endian_read_16(p,5+4*i);
                for(unsigned j=0;j<count&&ack_count;j++){
                    ack_t a=ledger[ack_head];ack_head=(ack_head+1)%ACK_QUEUE;ack_count--;
                    if(a.hid&&hid_in_flight)hid_in_flight--;
                    completed_credit=1;
                    if(a.fresh){completions++;if(completed_count<SAMPLE_LIMIT)completed_times[completed_count++]=now();}
                }
            }
        }else if(p[0]==HCI_EVENT_DISCONNECTION_COMPLETE&&size>=6&&p[2]==0&&owns_acl(little_endian_read_16(p,3))){
            observing_acl_close=1;
            pending_key_size_handle=HCI_CON_HANDLE_INVALID;
            if(!stopping)disconnect_reason=p[5];
        }
        if(profile==0)relay_safe_trace_hci(p,size,acl,trace_acl_peer_known?trace_acl_peer:NULL);
    }
    // Never log arbitrary raw HCI events: link-key events contain pairing keys.
    stack_receive(type,p,size);
    if(observing_acl_close){relay_safe_trace_acl_reset();trace_acl_peer_known=0;}
    /* The upstream stack must process the completion first, so its ACL credit
     * state is current when our one-report window is rearmed. */
    if(completed_credit)request_send();
}
static void register_receive(void(*callback)(uint8_t,uint8_t*,uint16_t)){stack_receive=callback;base_transport->register_packet_handler(receive_packet);}
static unsigned pro1_public_report_bytes(const uint8_t *report,unsigned length){
    unsigned safe=length<13?length:13;
    if(!length)return safe;
    if(report[0]==0x30&&length==49)return 49;
    if(report[0]==0x21&&length==49){
        uint8_t sub=report[14];
        if(sub==2||sub==3||sub==4||sub==8||sub==0x21||sub==0x30||sub==0x40||sub==0x48)return 49;
        if(sub==0x10&&report[13]==0x90){
            uint32_t address=little_endian_read_32(report,15);
            unsigned count=report[19];
            if(count&&count<=29&&address>=0x6000&&address<0x6100&&count<=0x6100-address)return 49;
        }
    }
    return safe;
}
static void trace_pro1_wire(int fresh){
    if(profile!=0||!last_report_len)return;
    if(!first_wire_input && (last_report[0]==0x30 || last_report[0]==0x31 || last_report[0]==0x3f)){
        first_wire_input=1;
        printf("{\"event\":\"PRO1_FIRST_WIRE_INPUT\",\"connection\":%u,\"report_id\":%u,\"elapsed_ms\":%.3f}\n",
            connection_count,last_report[0],1000*(now()-connected_since));
    }
    if(pro1_active_transition_at&&pro1_active_packets<16){
        double elapsed_ms=1000*(now()-pro1_active_transition_at);
        if(elapsed_ms<=1100){
            unsigned subcommand=last_report[0]==0x21&&last_report_len>14 ? last_report[14] : 0;
            printf("{\"event\":\"PRO1_ACTIVE_TX\",\"elapsed_ms\":%.3f,\"report_id\":%u,\"subcommand\":%u,"
                   "\"fresh\":%s,\"outstanding_acl\":%u}\n",elapsed_ms,last_report[0],subcommand,fresh?"true":"false",ack_count);
            pro1_active_packets++;
        }
    }
    if(wire_trace_count>=160)return;
    unsigned length=(unsigned)last_report_len;uint8_t id=last_report[0];
    int has_timer=id==0x21||id==0x30||id==0x31;
    int delta=has_timer&&wire_timer_known?(uint8_t)(last_report[1]-wire_last_timer):-1;
    double elapsed=has_timer&&wire_timer_known?1000*(now()-wire_last_stamp):-1;
    /* Only normal input and whitelisted public initialization replies may be
     * dumped. Never dump 0x31 NFC payloads, pairing replies or SPI key areas. */
    unsigned safe=pro1_public_report_bytes(last_report,length);
    char hex[99];for(unsigned i=0;i<safe;i++)snprintf(hex+2*i,3,"%02X",last_report[i]);hex[2*safe]=0;
    printf("{\"event\":\"PRO1_WIRE_REPORT\",\"index\":%u,\"ipc_sequence\":%"PRIu64",\"report_id\":%u,\"bytes\":%u,\"fresh\":%s,\"source_age_ms\":%.3f,\"timer_mod_delta\":%d,\"since_prior_timed_report_ms\":%.3f,\"public_prefix\":\"%s\",\"logged_bytes\":%u,\"tag_and_keys_logged\":false}\n",++wire_trace_count,sending_sequence,id,length,fresh?"true":"false",1000*(ticks()-sending_source_ticks)/qpc_frequency,delta,elapsed,hex,safe);
    if(has_timer){wire_timer_known=1;wire_last_timer=last_report[1];wire_last_stamp=now();}
}
static int send_packet(uint8_t type,uint8_t*p,int size){
    if(type==HCI_ACL_DATA_PACKET&&profile==0&&size>0)relay_safe_trace_acl(0,p,(uint16_t)size,acl);
    int flags=0;
    if(type==HCI_ACL_DATA_PACKET&&size>=4&&owns_acl(little_endian_read_16(p,0))){
        int first=((p[1]>>4)&3)!=1;
        int app=first&&send_is_hid&&size>=9&&p[8]==0xa1;
        flags=relay_hid_fragment(&hid_fragments,first,app,send_is_fresh,app?(unsigned)little_endian_read_16(p,4)+4:0,(unsigned)size-4);
    }
    int fresh=(flags&RELAY_HID_FRESH_END)!=0,hid=(flags&RELAY_HID_FRAGMENT_END)!=0;
    int result=base_transport->send_packet(type,p,size);
    if(!result && profile==0 && type==HCI_COMMAND_DATA_PACKET && size==5 && p[2]==2 &&
       little_endian_read_16(p,0)==HCI_OPCODE_HCI_READ_ENCRYPTION_KEY_SIZE){
        pending_key_size_handle=little_endian_read_16(p,3);
    }
    if(profile==0&&!result&&size>0)relay_capture_packet(type,0,p,(unsigned)size,now());
    if(!result&&type==HCI_ACL_DATA_PACKET&&size>=4&&(little_endian_read_16(p,0)&0xfff)==acl){
        if(ack_count==ACK_QUEUE){ledger_overflow++;failed=1;stop_backend();}
        else{
            ledger[ack_tail]=(ack_t){fresh,hid,now()};ack_tail=(ack_tail+1)%ACK_QUEUE;ack_count++;
            if(flags&RELAY_HID_FRAGMENT_START){
                trace_pro1_wire((flags&RELAY_HID_FRESH_START)!=0);

            }
        }
        if(flags&RELAY_HID_FRESH_START){submitted++;if(submitted_count<SAMPLE_LIMIT)submitted_times[submitted_count++]=now();}
    }
    return result;
}
static int btkey_send(uint16_t channel,const uint8_t *message,uint16_t bytes,int startup_repeat,void *context){
    (void)context;
    if(!connected||stopping||channel!=cid||bytes<2||bytes>MAX_PAYLOAD+1||message[0]!=0xa1)return ERROR_CODE_COMMAND_DISALLOWED;
    double age=latest.len==49?(ticks()-latest.ticks)/qpc_frequency:1e9;
    int input=message[1]==0x30||message[1]==0x31||message[1]==0x3f;
    int fresh=input&&!startup_repeat&&have_latest&&btkey_latest_physical&&latest.sequence!=btkey_last_fresh_sequence&&age>=0&&age<=.1;
    memcpy(last_report,message+1,bytes-1);last_report_len=bytes-1;
    sending_sequence=latest.len==49?latest.sequence:0;sending_source_ticks=latest.len==49?latest.ticks:ticks();
    send_is_fresh=fresh;send_is_hid=1;
    hid_in_flight++;if(hid_in_flight>hid_in_flight_max)hid_in_flight_max=hid_in_flight;
    uint16_t mtu=0;int status=relay_hid_send_checked(channel,message,bytes,&mtu);
    send_is_fresh=send_is_hid=0;
    if(status){
        if(hid_in_flight)hid_in_flight--;
        send_failures++;
        printf("{\"event\":\"BTKEY_SEND_RETRY\",\"status\":%d,\"message_bytes\":%u,\"remote_mtu\":%u,\"disconnect\":false}\n",status,bytes,mtu);
    }else if(fresh){
        btkey_last_fresh_sequence=latest.sequence;have_latest=0;
        if((message[1]==0x30 || message[1]==0x31) && bytes>=7){
            unsigned keys=((message[4]&0x40)&&(message[6]&0x40)?1u:0u) |
                (message[4]&8?2u:0u) | (message[4]&4?4u:0u) | (message[5]&0x10?8u:0u);
            /* Log the first observed L+R / A / B / HOME per session only. */
            if(keys & ~menu_buttons_logged){
                menu_buttons_logged|=keys;
                printf("{\"event\":\"PRO1_MENU_BUTTONS_SENT\",\"connection\":%u,\"lr\":%u,\"a\":%u,\"b\":%u,\"home\":%u}\n",
                       connection_count,keys&1,(keys>>1)&1,(keys>>2)&1,(keys>>3)&1);
            }
        }
    }
    return status;
}
static void btkey_request(uint16_t channel,void *context){
    (void)context;
    if(connected&&!stopping&&channel==cid&&!send_requested&&relay_hid_channels_open(cid)){
        send_requested=1;send_request_time=now();hid_device_request_can_send_now_event(cid);
    }
}
static void btkey_poll_tag(void){
    if(!btkey_tag_path)return;
    wchar_t wide[32768];if(!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,btkey_tag_path,-1,wide,32768))return;
    WIN32_FILE_ATTRIBUTE_DATA data;
    if(!GetFileAttributesExW(wide,GetFileExInfoStandard,&data)){
        DWORD error=GetLastError();
        if((error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND)&&btkey_tag_present){relay_btkey_clear_tag();btkey_tag_present=0;btkey_tag_stamp=0;puts("{\"event\":\"BTKEY_TAG_CLEARED\",\"tag_data_logged\":false}");}
        return;
    }
    uint64_t stamp=((uint64_t)data.ftLastWriteTime.dwHighDateTime<<32)|data.ftLastWriteTime.dwLowDateTime;
    if(stamp==btkey_tag_stamp)return;
    if(data.nFileSizeHigh||data.nFileSizeLow!=540){
        if(btkey_tag_present){relay_btkey_clear_tag();btkey_tag_present=0;}
        btkey_tag_stamp=stamp;return;
    }
    HANDLE file=CreateFileW(wide,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);if(file==INVALID_HANDLE_VALUE)return;
    uint8_t tag[541];DWORD count=0;BOOL read=ReadFile(file,tag,sizeof(tag),&count,NULL);CloseHandle(file);
    if(read&&count==540&&relay_btkey_set_tag540(tag,540)){
        btkey_tag_stamp=stamp;btkey_tag_present=1;puts("{\"event\":\"BTKEY_TAG_INSTALLED\",\"bytes\":540,\"tag_data_logged\":false}");
    }else if(read){
        relay_btkey_clear_tag();btkey_tag_present=0;btkey_tag_stamp=stamp;
        puts("{\"event\":\"BTKEY_TAG_REJECTED\",\"tag_data_logged\":false}");
    }
    SecureZeroMemory(tag,sizeof(tag));
}
static void host_output(uint16_t id,const uint8_t*body,int len){
    if(!connected||stopping||len<0||len+1>MAX_PAYLOAD)return;
    if(profile==0&&id!=1&&id!=0x10&&id!=0x11){
        printf("{\"event\":\"UNEXPECTED_PRO1_OUTPUT\",\"report_id\":%u,\"bytes\":%d,\"tag_and_keys_logged\":false}\n",id,len+1);
        return;
    }
    uint8_t output[MAX_PAYLOAD];output[0]=(uint8_t)id;if(len)memcpy(output+1,body,len);
    if(profile==1&&id!=3)return;
    if(profile==2&&id!=0x31){printf("{\"event\":\"UNSUPPORTED_PS5_BT_OUTPUT\",\"report_id\":%u,\"bytes\":%d,\"audio_transcoding_available\":false}\n",id,len+1);return;}
    if(profile==0)relay_btkey_handle_report(cid,HID_REPORT_TYPE_OUTPUT,id,len,(uint8_t*)body);
    host_commands++;emit_ipc(3,output,len+1);
    int argument=profile==0&&id==1&&len>=11&&(body[9]==3||body[9]==0x30||body[9]==0x40||body[9]==0x48)?body[10]:-1;
    printf("{\"event\":\"HOST_OUTPUT\",\"report_id\":%u,\"bytes\":%d,\"command\":%d,\"argument\":%d,\"tag_data_logged\":false}\n",id,len+1,len>=10?body[9]:-1,argument);
}
static void report_data(uint16_t channel,hid_report_type_t type,uint16_t id,int len,uint8_t*data){if(channel==cid&&type==HID_REPORT_TYPE_OUTPUT)host_output(id,data,len);}
static void set_report(uint16_t channel,hid_report_type_t type,int len,uint8_t*data){
    if(channel==cid&&type==HID_REPORT_TYPE_OUTPUT&&len>0)host_output(data[0],data+1,len-1);
}
static uint32_t ps_crc(const uint8_t*bytes,int length,uint8_t seed){
    uint32_t c=0xffffffffu;for(int i=-1;i<length;i++){c^=i<0?seed:bytes[i];for(int j=0;j<8;j++)c=(c>>1)^((c&1)?0xedb88320u:0);}return ~c;
}
static int ps5_feature(uint16_t id,uint8_t*out){
    int size=id==5?41:id==9?20:id==0x20?64:0;if(!size)return 0;
    memset(out,0,(size_t)size);out[0]=(uint8_t)id;
    if(id==5){int16_t values[]={0,0,0,8192,-8192,8192,-8192,8192,-8192,500,500,8192,-8192,8192,-8192,8192,-8192};for(int i=0;i<17;i++)little_endian_store_16(out,1+2*i,(uint16_t)values[i]);out[35]=0x0b;}
    if(id==9){reverse_bd_addr(local,out+1);out[7]=8;}
    if(id==0x20){memcpy(out+1,"Nov 16 2026",11);little_endian_store_32(out,24,0x01000000);little_endian_store_32(out,44,0x00000350);}
    little_endian_store_32(out,size-4,ps_crc(out,size-4,0xa3));return size;
}
static int get_report(uint16_t channel,hid_report_type_t type,uint16_t id,int*len,uint8_t*data){
    /* The pinned HID callback uses 1=body available, 0=not ready and an
     * alternate value=unsupported. The on-wire success status is separate. */
    if(!cid||channel!=cid)return 0;
    if(profile==0&&type==HID_REPORT_TYPE_INPUT&&id==0x3f){
        relay_btkey_build_simple_input(data);*len=11;return 1;
    }
    if(profile==2&&type==HID_REPORT_TYPE_FEATURE){uint8_t report[64];int size=ps5_feature(id,report);if(!size)return -1;*len=size-1;memcpy(data,report+1,(size_t)*len);printf("{\"event\":\"PS5_BT_FEATURE\",\"report_id\":%u,\"bytes\":%d,\"crc\":true}\n",id,size);return 1;}
    if(profile==2&&type==HID_REPORT_TYPE_INPUT&&id==1){
        memset(data,0,9);memset(data,128,4);data[4]=8;
        if(last_report_len==78&&last_report[0]==0x31){memcpy(data,last_report+2,4);memcpy(data+4,last_report+9,3);data[7]=last_report[6];data[8]=last_report[7];}
        *len=9;return 1;
    }
    if(profile==1&&type==HID_REPORT_TYPE_INPUT&&id==2){*len=1;data[0]=0;return 1;}
    if(profile==1&&type==HID_REPORT_TYPE_INPUT&&id==4&&xbox_battery>=0){*len=1;data[0]=(uint8_t)xbox_battery;return 1;}
    if(profile==1&&type==HID_REPORT_TYPE_INPUT&&id==1&&!last_report_len){memset(data,0,16);for(int i=0;i<8;i+=2)little_endian_store_16(data,i,32768);*len=16;return 1;}
    if(type!=HID_REPORT_TYPE_INPUT)return -1;
    int available_id=profile==0?(id==0x21||id==0x30||id==0x31):
        profile==1?(id==1||id==2||id==4):id==0x31;
    if(!available_id)return -1;
    if(!last_report_len||last_report[0]!=id)return 0;
    *len=last_report_len-1;memcpy(data,last_report+1,*len);return 1;
}
static void events(uint8_t type,uint16_t channel,uint8_t*p,uint16_t size){
    (void)channel;if(type!=HCI_EVENT_PACKET||size<2)return;
    switch(p[0]){
        case BTSTACK_EVENT_STATE:
            if(btstack_event_state_get_state(p)==HCI_STATE_WORKING){
                stack_working=1;
                gap_local_bd_addr(local);
                if(profile==0)relay_btkey_set_local_address(local);
                printf("{\"event\":\"STACK_WORKING\",\"address\":\"%s\",\"identity\":\"%s\",\"profile\":\"%s\"}\n",bd_addr_to_str(local),device_names[profile],profile_names[profile]);
                emit_link(0,0);
                if(realtek)version_readback=1;
            }else if(btstack_event_state_get_state(p)==HCI_STATE_OFF&&stopping)btstack_run_loop_trigger_exit();
            break;
        case BTSTACK_EVENT_POWERON_FAILED:
            failed=1;puts("{\"event\":\"RADIO_POWERON_FAILED\",\"driver_changed\":false}");emit_link(3,1);stop_backend();break;
        case HCI_EVENT_USER_CONFIRMATION_REQUEST:{
            if(size<12)break;
            bd_addr_t who;hci_event_user_confirmation_request_get_bd_addr(p,who);
            if(stopping||(have_peer&&bd_addr_cmp(who,peer))){gap_ssp_confirmation_negative(who);break;}
            if(profile!=0||!defer_switch_pairing||ever_connected){gap_ssp_confirmation_response(who);break;}
            hci_connection_t *connection=hci_connection_for_bd_addr_and_type(who,BD_ADDR_TYPE_ACL);
            if(!connection||!owns_acl(connection->con_handle)){gap_ssp_confirmation_negative(who);break;}
            if((switch_pairing.id||switch_pairing_accepted_id)&&switch_pairing.handle==connection->con_handle&&!bd_addr_cmp(who,switch_pairing.address))break;
            if(!begin_switch_pairing(who,connection->con_handle,now())){gap_ssp_confirmation_negative(who);break;}
            printf("{\"event\":\"SWITCH_PAIRING_PENDING\",\"request_id\":%u,\"timeout_seconds\":%u}\n",
                switch_pairing.id,SWITCH_CONFIRM_SECONDS);
            emit_link(RELAY_PAIRING_PENDING,switch_pairing.id);
            break;
        }
        case HCI_EVENT_PIN_CODE_REQUEST:{
            if(size<8)break;
            bd_addr_t who;hci_event_pin_code_request_get_bd_addr(p,who);
            if(profile==0&&defer_switch_pairing&&!ever_connected)gap_pin_code_negative(who);
            else if(!have_peer||!bd_addr_cmp(who,peer))gap_pin_code_response(who,"0000");
            break;
        }
        case HCI_EVENT_SIMPLE_PAIRING_COMPLETE:
            if(size>=9){
                printf("{\"event\":\"PAIRING_COMPLETE\",\"status\":%u}\n",p[2]);
                if(switch_pairing.id||switch_pairing_accepted_id){
                    bd_addr_t who;hci_event_simple_pairing_complete_get_bd_addr(p,who);
                    if(!bd_addr_cmp(who,switch_pairing.address)){
                        if(switch_pairing.id)end_switch_pairing(PAIRING_PEER_CANCELLED);
                        else if(p[2]){
                            switch_pairing_accepted_id=0;
                            emit_link(RELAY_PAIRING_ENDED,PAIRING_HCI_REJECTED);
                        }
                    }
                }
            }
            break;
        case HCI_EVENT_ENCRYPTION_CHANGE:printf("{\"event\":\"ENCRYPTION\",\"status\":%u,\"enabled\":%u}\n",p[2],p[5]);break;
        case HCI_EVENT_MODE_CHANGE:
            if(size<8)break;
            printf("{\"event\":\"LINK_MODE\",\"status\":%u,\"mode\":%u,\"interval_slots\":%u}\n",p[2],p[5],little_endian_read_16(p,6));
            if(size>=8&&p[2]==0&&owns_acl(little_endian_read_16(p,3))){
                if(profile==0&&link_in_sniff&&p[5]==0){pro1_active_transition_at=now();pro1_active_packets=0;}
                link_in_sniff=p[5]==2;
                if(profile==0){
                    /* The mature Windows engine controls the report stream;
                     * a host mode change is diagnostic metadata, not another
                     * quiet/credit policy layered over that state machine. */
                    hid_mode_transitions++;
                    relay_btkey_link_mode(p[5]);
                }
                if(profile==0&&link_in_sniff)printf("{\"event\":\"PEER_SNIFF_RETAINED\",\"profile\":\"pro1\",\"interval_slots\":%u,\"interval_ms\":%.3f,\"active_exit_enabled\":false,\"receiver_rate_verified\":false}\n",little_endian_read_16(p,6),little_endian_read_16(p,6)*.625);
                if(profile!=0){send_requested=0;request_send();}
            }break;
        case HCI_EVENT_CONNECTION_REQUEST:
            if(size>=12 && p[11]==1 && profile==0 && have_peer){
                bd_addr_t who;reverse_bd_addr(p+2,who);
                if(!bd_addr_cmp(who,peer))inbound_acl_until=now()+4;
            }
            break;
        case HCI_EVENT_CONNECTION_COMPLETE:
            if(size>=13 && p[11]==1 && profile==0 && have_peer){
                bd_addr_t who;reverse_bd_addr(p+5,who);
                if(!bd_addr_cmp(who,peer))inbound_acl_until=0;
            }
            break;
        case HCI_EVENT_DISCONNECTION_COMPLETE:
            if(size<6 || p[2]!=0)break;
            printf("{\"event\":\"ACL_DISCONNECTED\",\"reason\":%u,\"cleanup\":%s}\n",p[5],stopping?"true":"false");
            if(switch_pairing.id&&little_endian_read_16(p,3)==switch_pairing.handle)end_switch_pairing(PAIRING_PEER_CANCELLED);
            if(switch_pairing_accepted_id&&little_endian_read_16(p,3)==switch_pairing.handle){
                switch_pairing_accepted_id=0;
                emit_link(RELAY_PAIRING_ENDED,PAIRING_PEER_CANCELLED);
            }
            if(owns_acl(little_endian_read_16(p,3))){
                int can_recover=p[5]==ERROR_CODE_REMOTE_USER_TERMINATED_CONNECTION || p[5]==ERROR_CODE_CONNECTION_TIMEOUT;
                int releasing=!stopping && !peer_unplugged && can_recover &&
                    (recovery.phase==RELAY_RECOVERY_WAIT_ACL || begin_recovery(p[5]));
                relay_hid_forget_acl(acl);
                close_link_accounting();acl=HCI_CON_HANDLE_INVALID;pending_recovery_cid=0;
                ack_head=ack_tail=ack_count=0;hid_in_flight=0;hid_fragments=(relay_hid_fragments_t){0};link_in_sniff=0;
                if(releasing){
                    double stamp=now();
                    relay_recovery_released(&recovery,stamp);
                    gap_discoverable_control(1);
                    printf("{\"event\":\"PRO1_LISTEN_FOR_HOST\",\"remaining_ms\":%.0f,\"dial_due_ms\":%.0f,\"attempts\":%u,\"menu_handoff\":%s}\n",
                        1000*fmax(0,recovery.deadline-stamp),1000*fmax(0,recovery.due-stamp),
                        recovery.attempts,recovery.handoff?"true":"false");
                }else if(!stopping&&ever_connected){failed=1;emit_link(2,p[5]);stop_backend();}
            }break;
        case HCI_EVENT_HID_META:
            switch(hci_event_hid_meta_get_subevent_code(p)){
                case HID_SUBEVENT_SUSPEND:puts("{\"event\":\"PEER_HID_SUSPEND\"}");break;
                case HID_SUBEVENT_EXIT_SUSPEND:puts("{\"event\":\"PEER_HID_EXIT_SUSPEND\"}");break;
                case HID_SUBEVENT_VIRTUAL_CABLE_UNPLUG:peer_unplugged=1;puts("{\"event\":\"PEER_VIRTUAL_CABLE_UNPLUG\",\"auto_reconnect\":false}");break;
                case HID_SUBEVENT_CONNECTION_OPENED:{
                    if(size<14)break;
                    uint8_t status=hid_subevent_connection_opened_get_status(p);
                    bd_addr_t who;hid_subevent_connection_opened_get_bd_addr(p,who);
                    if(status){
                        printf("{\"event\":\"HID_CONNECT_FAILED\",\"status\":%u}\n",status);
                        if(pending_recovery_cid &&
                           hid_subevent_connection_opened_get_hid_cid(p)==pending_recovery_cid && !bd_addr_cmp(who,peer)){
                            if(status==L2CAP_CONNECTION_BASEBAND_DISCONNECT && acl!=HCI_CON_HANDLE_INVALID)
                                puts("{\"event\":\"HID_WAIT_FOR_ACL_DISCONNECT_REASON\"}");
                            else recovery_failed(status);
                        }
                        break;
                    }
                    uint16_t opened=hid_subevent_connection_opened_get_hid_cid(p);
                    if(have_peer&&bd_addr_cmp(who,peer)){hid_device_disconnect(opened);break;}
                    if(profile==0&&defer_switch_pairing&&!ever_connected){
                        if(!switch_pairing_accepted_id||bd_addr_cmp(who,switch_pairing.address)||
                           hid_subevent_connection_opened_get_con_handle(p)!=switch_pairing.handle){
                            puts("{\"event\":\"SWITCH_HID_WITHOUT_DEFERRED_CONFIRMATION\"}");
                            hid_device_disconnect(opened);break;
                        }
                        switch_pairing_accepted_id=0;
                    }
                    cid=opened;
                    memcpy(peer,who,6);have_peer=1;acl=hid_subevent_connection_opened_get_con_handle(p);
                    if(gap_security_level(acl)<LEVEL_2){failed=1;puts("{\"event\":\"ENCRYPTION_REQUIRED_ABORT\"}");stop_backend();break;}
                    if(recovery.phase!=RELAY_RECOVERY_IDLE){clear_pending_input();emit_link(0,0);}
                    pending_recovery_cid=0;
                    connection_count++;connected_since=now();if(!ever_connected)ready=connected_since;
                    relay_recovery_hid_opened(&recovery,connected_since);
                    connected=ever_connected=1;peer_unplugged=0;menu_buttons_logged=0;first_wire_input=0;last_send_event=0;gap_discoverable_control(0);
                    printf("{\"event\":\"CONTROLLER_HID_READY\",\"profile\":\"%s\",\"receiver_rate_verified\":false}\n",profile_names[profile]);emit_link(1,0);
                    if(profile==0){
                        double age=latest.len==49?(ticks()-latest.ticks)/qpc_frequency:1e9;
                        if(age<-.01 || age>.1)release_pro1_controls();
                        relay_btkey_packet_handler(type,channel,p,size);
                    }else request_send();
                    break;
                }
                case HID_SUBEVENT_CONNECTION_CLOSED:
                    if(hid_subevent_connection_closed_get_hid_cid(p)!=cid)break;
                    if(profile==0&&pro1_active_transition_at){
                        printf("{\"event\":\"PRO1_ACTIVE_TO_HID_CLOSE\",\"elapsed_ms\":%.3f,"
                               "\"reports_logged_in_transition\":%u}\n",1000*(now()-pro1_active_transition_at),pro1_active_packets);
                    }
                    trace_flow("hid_channels_closed");puts("{\"event\":\"HID_CLOSED\"}");
                    if(!begin_recovery(0)){
                        close_link_accounting();
                        if(ever_connected&&!stopping){failed=1;emit_link(2,disconnect_reason);stop_backend();}
                    }
                    break;
                case HID_SUBEVENT_CAN_SEND_NOW:{
                    if(hid_subevent_can_send_now_get_hid_cid(p)!=cid)break;
                    send_requested=0;last_send_event=now();if(!connected||stopping||!relay_hid_channels_open(cid))break;
                    if(profile==0){
                        relay_btkey_packet_handler(type,channel,p,size);
                        int state=relay_btkey_delivery_state();
                        if(pro1_delivery!=state){
                            pro1_delivery=state;
                            if(state==RELAY_PRO1_CONFIRMED){
                                any_switch_init=1;
                                full_input_since=now();
                            }else{
                                full_input_since=0;
                            }
                            relay_recovery_report(&recovery,now(),state==RELAY_PRO1_CONFIRMED);
                            emit_link(7,state);
                            printf("{\"event\":\"PRO1_SESSION_INPUT\",\"connection\":%u,\"state\":%d,\"receiver_observed\":false}\n",connection_count,state);
                        }
                        break;
                    }
                    prune_edges(ticks());
                    frame_t frame;int fresh=0;
                    if(control_count){frame=controls[control_head];control_head=(control_head+1)%CONTROL_QUEUE;control_count--;}
                    else if(edge_count){frame=edges[edge_head];edge_head=(edge_head+1)%EDGE_QUEUE;edge_count--;fresh=1;}
                    else if(have_latest){frame=latest;have_latest=0;fresh=1;if((ticks()-frame.ticks)/qpc_frequency>.1){source_stale++;request_send();break;}}
                    else break;
                    if((ticks()-frame.ticks)/qpc_frequency>.1){
                        if(fresh){source_stale++;request_send();break;}
                        if((ticks()-frame.ticks)/qpc_frequency>2){protocol_expired++;puts("{\"event\":\"PROTOCOL_REPLY_EXPIRED\",\"disconnect\":false}");request_send();break;}
                    }
                    uint8_t message[MAX_PAYLOAD+1];message[0]=0xa1;memcpy(message+1,frame.data,frame.len);
                    memcpy(last_report,frame.data,frame.len);last_report_len=frame.len;
                    sending_sequence=frame.sequence;sending_source_ticks=frame.ticks;
                    uint16_t remote_mtu=0;send_is_fresh=fresh;send_is_hid=1;
                    hid_in_flight++;if(hid_in_flight>hid_in_flight_max)hid_in_flight_max=hid_in_flight;
                    int status=relay_hid_send_checked(cid,message,(uint16_t)(frame.len+1),&remote_mtu);send_is_fresh=send_is_hid=0;
                    if(status){send_failures++;failed=1;printf("{\"event\":\"HID_SEND_REJECTED\",\"status\":%d,\"message_bytes\":%d,\"remote_mtu\":%u}\n",status,frame.len+1,remote_mtu);emit_link(3,status);stop_backend();break;}
                    request_send();break;
                }
            }break;
    }
}
static void stop_backend(void){if(stopping)return;stopping=1;stop_time=now();switch_pairing.id=0;switch_pairing_accepted_id=0;if(profile==0)relay_btkey_shutdown();close_link_accounting();hci_power_control(HCI_POWER_OFF);}
static void watchdog(btstack_timer_source_t*t){
    double stamp=now();
    if(profile==0)relay_capture_poll(stamp);
    if(stopping&&stamp-stop_time>5){puts("{\"event\":\"POWER_OFF_TIMEOUT\"}");failed=1;btstack_run_loop_trigger_exit();return;}
    if(!stopping && pro1_key_size_unsupported){
        /* Give upstream time to finish the disconnect it initiated. */
        if(acl==HCI_CON_HANDLE_INVALID || stamp-key_size_error_time>=1)stop_backend();
    }else if(!stopping){
        if(switch_pairing.id&&stamp>=switch_pairing.deadline){
            hci_con_handle_t expired=switch_pairing.handle;
            bd_addr_t address;memcpy(address,switch_pairing.address,sizeof(address));
            end_switch_pairing(PAIRING_TIMEOUT);
            if(gap_ssp_confirmation_negative(address))gap_disconnect(expired);
        }
        if(profile==0){
            if(stamp-btkey_tag_poll>=.25){btkey_tag_poll=stamp;btkey_poll_tag();}
            if(connected&&latest.len==49&&!btkey_source_released&&(ticks()-latest.ticks)/qpc_frequency>.5){
                release_pro1_controls();
                puts("{\"event\":\"BTKEY_SOURCE_TIMEOUT_RELEASE\",\"physical_frame\":false,\"disconnect\":false}");
            }
        }
        int peer_link_busy=acl!=HCI_CON_HANDLE_INVALID || stamp<inbound_acl_until;
        enum relay_recovery_action reconnect_action=relay_recovery_poll(&recovery,stamp,peer_link_busy);
        if(reconnect_action==RELAY_RECOVERY_EXPIRED){
            failed=1;printf("{\"event\":\"HID_RECOVERY_TIMEOUT\",\"phase\":%d,\"attempts\":%u,\"old_acl_reset\":false}\n",recovery.phase,recovery.attempts);
            emit_link(3,0xff);stop_backend();
        }else if(reconnect_action==RELAY_RECOVERY_DIAL){
            clear_pending_input();
            total_reconnect_attempts++;
            uint8_t status=hid_device_connect(peer,&pending_recovery_cid);
            printf("{\"event\":\"HID_RECOVERY_CONNECT\",\"attempt\":%u,\"limit\":2,\"status\":%u,\"same_peer_only\":true,\"old_acl_released\":true}\n",recovery.attempts,status);
            if(status)recovery_failed(status);
        }else if(reconnect_action==RELAY_RECOVERY_STABLE){
            puts("{\"event\":\"HID_RECOVERY_STABLE\",\"stable_ms\":2000,\"home_input_verified\":false}");
        }
        if(profile!=0&&connected&&(control_count||edge_count||have_latest)&&send_requested&&stamp-send_request_time>.25){
            if(stamp-last_flow_snapshot>=.25){last_flow_snapshot=stamp;trace_flow("send_wait_over_250ms");}
            send_requested=0;send_rearms++;if(send_rearms<=5)puts("{\"event\":\"SEND_NOW_REARM\",\"pending_real_input\":true,\"disconnect\":false}");request_send();
        }
        if(should_exit_peer_sniff(stamp)){
            last_sniff_exit=stamp;sniff_exit_attempts++;uint8_t status=gap_sniff_mode_exit(acl);
            printf("{\"event\":\"ACTIVE_LINK_REQUEST\",\"attempt\":%u,\"status\":%u,\"reason\":\"receiver_entered_sniff\"}\n",sniff_exit_attempts,status);
        }
        if(version_readback==1&&hci_can_send_command_packet_now()){version_readback=2;hci_send_cmd(&hci_read_local_version_information);}
        if(!stack_working&&stamp-boot>30){failed=1;puts("{\"event\":\"RADIO_INIT_TIMEOUT\"}");emit_link(3,2);stop_backend();}
        else if(stamp-last_heartbeat>5){failed=1;puts("{\"event\":\"MANAGED_HEARTBEAT_TIMEOUT\"}");stop_backend();}
        else if(!ever_connected&&stamp-boot>wait_seconds){failed=1;puts("{\"event\":\"PAIRING_WINDOW_TIMEOUT\"}");stop_backend();}
        else if(seconds>0 && ever_connected){
            double qualified_since=require_switch_init?full_input_since:connected_since;
            int result=relay_observation(stamp,ready,qualified_since,(unsigned)seconds);
            if(result){
                timed_observation=result>0;
                completed=timed_observation && (!require_switch_init || pro1_delivery==RELAY_PRO1_CONFIRMED);
                if(!completed)failed=1;
                printf("{\"event\":\"HID_OBSERVATION_END\",\"duration_reached\":%s,\"require_switch_init\":%s,\"current_delivery\":%d,\"home_input_verified\":false}\n",
                       timed_observation?"true":"false",require_switch_init?"true":"false",pro1_delivery);
                stop_backend();
            }
        }
        if(connected&&stamp-last_progress>=5)trace_flow("periodic");
        if(stamp-last_progress>=5){last_progress=stamp;printf("{\"event\":\"WIRELESS_WINDOW\",\"connected\":%s,\"source\":%"PRIu64",\"submitted\":%"PRIu64",\"hci_completed\":%"PRIu64",\"superseded\":%"PRIu64",\"stale\":%"PRIu64",\"host_commands\":%"PRIu64"}\n",connected?"true":"false",source_received,submitted,completions,source_superseded,source_stale,host_commands);
            printf("{\"event\":\"TX_HEALTH\",\"edge_queue\":%u,\"control_queue\":%u,\"digital_expired\":%"PRIu64",\"digital_resyncs\":%"PRIu64",\"send_rearms\":%"PRIu64",\"last_send_event_age_ms\":%.1f,\"sniff\":%s}\n",edge_count,control_count,digital_transitions_expired,digital_resyncs,send_rearms,last_send_event?1000*(stamp-last_send_event):0,link_in_sniff?"true":"false");}
    }
    btstack_run_loop_set_timer(t,50);btstack_run_loop_add_timer(t);
}
static int test_transport_send(uint8_t type,uint8_t *packet,int size){(void)type;(void)packet;(void)size;return 0;}
static void test_stack_receive(uint8_t type,uint8_t *packet,uint16_t size){(void)type;(void)packet;(void)size;}
static int self_test(void){
    int failures=0;
    failures+=btstack_hid_get_report_size_for_id(0x30,HID_REPORT_TYPE_INPUT,pro1_descriptor,sizeof(pro1_descriptor))!=48;
    failures+=btstack_hid_get_report_size_for_id(0x31,HID_REPORT_TYPE_INPUT,pro1_descriptor,sizeof(pro1_descriptor))!=361;
    failures+=btstack_hid_get_report_size_for_id(0x11,HID_REPORT_TYPE_OUTPUT,pro1_descriptor,sizeof(pro1_descriptor))!=48;
    failures+=!relay_set_instance("USB\\VID_10D7&PID_B012\\ACTIONS1234");
    failures+=!relay_target_path_allowed("\\\\?\\usb#vid_10d7&pid_b012#actions1234#{guid}");
    failures+=relay_target_path_allowed("\\\\?\\usb#vid_10d7&pid_b012#OTHER#{guid}");
    failures+=relay_set_instance("USB\\VID_057E&PID_2069\\REAL_PRO2");
    uint8_t p[HEADER]={0};memcpy(p,"PRW1",4);p[4]=1;p[5]=5;write64(p+24,1);uint64_t sequence;int kind,len;
    failures+=!decode_ipc(p,HEADER,&sequence,&kind,&len);p[8]=1;failures+=decode_ipc(p,HEADER,&sequence,&kind,&len);p[8]=0;p[40]=1;failures+=decode_ipc(p,HEADER,&sequence,&kind,&len);
    acl=0x42;failures+=!owns_acl(0x42);failures+=owns_acl(0x43);acl=HCI_CON_HANDLE_INVALID;failures+=owns_acl(0xffff);
    frame_t sample={0};sample.len=49;sample.data[0]=0x30;uint8_t unchanged[49]={0x30};failures+=!same_digital_state(&sample,unchanged,49);unchanged[3]=1;failures+=same_digital_state(&sample,unchanged,49);unchanged[3]=0;unchanged[6]=0x80;failures+=!same_digital_state(&sample,unchanged,49);
    int cases=16;
#define VERIFY(test) do { cases++; failures+=!(test); } while(0)
    build_pro1_eir(pro1_eir);
    VERIFY(pro1_eir[0]==15&&pro1_eir[1]==0x09&&!memcmp(pro1_eir+2,"Pro Controller",14));
    VERIFY(pro1_eir[16]==5&&pro1_eir[17]==3&&little_endian_read_16(pro1_eir,18)==0x1124&&little_endian_read_16(pro1_eir,20)==0x1200);
    VERIFY(pro1_eir[22]==9&&pro1_eir[23]==0x10&&little_endian_read_16(pro1_eir,24)==2&&
           little_endian_read_16(pro1_eir,26)==0x057e&&little_endian_read_16(pro1_eir,28)==0x2009&&little_endian_read_16(pro1_eir,30)==0x0100);
    VERIFY(pro1_eir[32]==0&&pro1_eir[sizeof(pro1_eir)-1]==0);
    failures+=relay_safe_trace_self_test(&cases);
    bd_addr_t first={1,2,3,4,5,6}, later={2,3,4,5,6,7};
    VERIFY(begin_switch_pairing(first,0x42,10.0));
    uint8_t first_id=switch_pairing.id;
    VERIFY(!begin_switch_pairing(later,0x43,11.0)); /* does not overwrite a live request */
    VERIFY(switch_pairing_current(first_id,0x42,27.99));
    VERIFY(!switch_pairing_current(first_id,0x42,28.0));
    VERIFY(!switch_pairing_current(first_id,0x43,11.0));
    VERIFY(!switch_pairing_current(0,0x42,11.0));
    switch_pairing.id=0;
    VERIFY(begin_switch_pairing(later,0x42,29.0)); /* controller reuses ACL handle */
    VERIFY(!switch_pairing_current(first_id,0x42,29.1));
    VERIFY(switch_pairing_current(switch_pairing.id,0x42,29.1));
    uint8_t confirmation[HEADER+1]={0};memcpy(confirmation,"PRW1",4);
    confirmation[4]=1;confirmation[5]=RELAY_PAIRING_CONFIRM;write64(confirmation+24,1);
    VERIFY(!decode_ipc(confirmation,HEADER,&sequence,&kind,&len));
    little_endian_store_16(confirmation,6,1);
    VERIFY(!decode_ipc(confirmation,HEADER+1,&sequence,&kind,&len)); /* zero request ID */
    confirmation[HEADER]=switch_pairing.id;
    VERIFY(decode_ipc(confirmation,HEADER+1,&sequence,&kind,&len)&&kind==RELAY_PAIRING_CONFIRM&&len==1);
    confirmation[8]=1;
    VERIFY(!decode_ipc(confirmation,HEADER+1,&sequence,&kind,&len)); /* bad session token */
    switch_pairing.id=0;switch_pairing_last_id=0;
    for(int selected=0;selected<3;selected++){
        profile=selected;hid_sdp_record_t parameters=profile_sdp_parameters();
        uint8_t record[2048];hid_create_sdp_record(record,0x10002,&parameters);
        uint16_t expected=selected==0?UINT16_MAX:24,value=0;
        uint8_t *attribute=sdp_get_attribute_value_for_attribute_id(record,BLUETOOTH_ATTRIBUTE_HIDSSR_HOST_MAX_LATENCY);
        VERIFY(attribute&&de_element_get_uint16(attribute,&value)&&value==expected);
        attribute=sdp_get_attribute_value_for_attribute_id(record,BLUETOOTH_ATTRIBUTE_HIDSSR_HOST_MIN_TIMEOUT);
        VERIFY(attribute&&de_element_get_uint16(attribute,&value)&&value==expected);
        attribute=sdp_get_attribute_value_for_attribute_id(record,BLUETOOTH_ATTRIBUTE_HID_SUPERVISION_TIMEOUT);
        VERIFY(attribute&&de_element_get_uint16(attribute,&value)&&value==3200);
    }
    profile=0;
    VERIFY(sizeof(ps5_descriptor)==279);VERIFY(sizeof(xbox_descriptor)==334);
    VERIFY(btstack_hid_get_report_size_for_id(0x31,HID_REPORT_TYPE_INPUT,ps5_descriptor,sizeof(ps5_descriptor))==77);
    VERIFY(btstack_hid_get_report_size_for_id(0x31,HID_REPORT_TYPE_OUTPUT,ps5_descriptor,sizeof(ps5_descriptor))==77);
    VERIFY(btstack_hid_get_report_size_for_id(5,HID_REPORT_TYPE_FEATURE,ps5_descriptor,sizeof(ps5_descriptor))==40);
    VERIFY(btstack_hid_get_report_size_for_id(1,HID_REPORT_TYPE_INPUT,xbox_descriptor,sizeof(xbox_descriptor))==16);
    VERIFY(btstack_hid_get_report_size_for_id(2,HID_REPORT_TYPE_INPUT,xbox_descriptor,sizeof(xbox_descriptor))==1);
    VERIFY(btstack_hid_get_report_size_for_id(3,HID_REPORT_TYPE_OUTPUT,xbox_descriptor,sizeof(xbox_descriptor))==8);
    uint8_t feature[64];VERIFY(ps5_feature(5,feature)==41);VERIFY(little_endian_read_32(feature,37)==ps_crc(feature,37,0xa3));
    VERIFY(ps5_feature(9,feature)==20);VERIFY(ps5_feature(0x20,feature)==64);VERIFY(ps5_feature(0xf0,feature)==0);
    int report_len=0;
    cid=1;
    VERIFY(get_report(cid,HID_REPORT_TYPE_INPUT,0x3f,&report_len,feature)==1&&report_len==11);
    VERIFY(get_report(cid,HID_REPORT_TYPE_INPUT,0x30,&report_len,feature)==0);
    VERIFY(get_report(cid,HID_REPORT_TYPE_FEATURE,0x30,&report_len,feature)==-1);
    profile=1;
    VERIFY(get_report(cid,HID_REPORT_TYPE_INPUT,1,&report_len,feature)==1&&report_len==16&&little_endian_read_16(feature,0)==32768);
    VERIFY(get_report(cid,HID_REPORT_TYPE_FEATURE,5,&report_len,feature)==-1);
    profile=2;
    VERIFY(get_report(cid,HID_REPORT_TYPE_FEATURE,5,&report_len,feature)==1&&report_len==40);
    VERIFY(get_report(cid,HID_REPORT_TYPE_FEATURE,0xf0,&report_len,feature)==-1);
    cid=0;
    profile=2;uint8_t ps[78]={0x31};VERIFY(valid_input(ps,78));VERIFY(!valid_input(ps,77));
    sample.len=78;memcpy(sample.data,ps,78);VERIFY(same_digital_state(&sample,ps,78));ps[17]=1;VERIFY(same_digital_state(&sample,ps,78));ps[9]=1;VERIFY(!same_digital_state(&sample,ps,78));
    profile=1;uint8_t xb[17]={1};uint8_t guide[2]={2};VERIFY(valid_input(xb,17));VERIFY(valid_input(guide,2));VERIFY(!valid_input(ps,78));
    sample.len=17;memcpy(sample.data,xb,17);VERIFY(same_digital_state(&sample,xb,17));xb[14]=1;VERIFY(!same_digital_state(&sample,xb,17));profile=0;
    qpc_frequency=1000;edge_head=edge_tail=edge_count=0;frame_t pressure={0};pressure.len=49;pressure.data[0]=0x30;
    for(int i=0;i<1000;i++){pressure.ticks=i*8;preserve_transition(&pressure,i*8);}
    VERIFY(!failed&&!stopping);VERIFY(edge_count<EDGE_QUEUE);VERIFY(digital_transitions_expired>0);
    edge_head=edge_tail=edge_count=0;
    for(int i=0;i<40;i++){pressure.ticks=i;preserve_transition(&pressure,i);}
    VERIFY(!failed&&!stopping);VERIFY(digital_resyncs>0);VERIFY(edge_count<EDGE_QUEUE);
    prune_edges(1000);VERIFY(edge_count==0);
    relay_usb_flow_t usb_flow;relay_hid_flow_t hid_flow;
    relay_usb_flow_snapshot(&usb_flow);relay_hid_flow_snapshot(0xffff,&hid_flow);
    VERIFY(usb_flow.acl_busy==0&&usb_flow.command_busy==0);VERIFY(hid_flow.found==0&&hid_flow.can_send==0);
    failures+=relay_hid_lifecycle_self_test(&cases);
    relay_recovery_t retry={0};
    relay_recovery_lost(&retry,10,1,1);
    VERIFY(relay_recovery_poll(&retry,12,1)==RELAY_RECOVERY_NONE);
    relay_recovery_released(&retry,13);
    VERIFY(retry.deadline==51 && relay_recovery_poll(&retry,14.49,0)==RELAY_RECOVERY_NONE);
    VERIFY(relay_recovery_poll(&retry,14.5,1)==RELAY_RECOVERY_NONE);
    VERIFY(relay_recovery_poll(&retry,14.5,0)==RELAY_RECOVERY_DIAL && retry.attempts==1);
    VERIFY(relay_recovery_poll(&retry,14.6,0)==RELAY_RECOVERY_NONE);
    relay_recovery_hid_opened(&retry,15);
    VERIFY(retry.phase==RELAY_RECOVERY_WAIT_REPORT && relay_recovery_poll(&retry,17,1)==RELAY_RECOVERY_NONE);
    relay_recovery_lost(&retry,17,0,0);
    relay_recovery_released(&retry,18);
    VERIFY(retry.due==39 && retry.deadline==51 && retry.requires_full);
    VERIFY(relay_recovery_poll(&retry,38.99,0)==RELAY_RECOVERY_NONE);
    VERIFY(relay_recovery_poll(&retry,39,0)==RELAY_RECOVERY_DIAL && retry.attempts==2);
    relay_recovery_dial_failed(&retry,40);
    relay_recovery_dial_failed(&retry,41);
    VERIFY(retry.due==41.5 && relay_recovery_poll(&retry,49,0)==RELAY_RECOVERY_NONE);
    VERIFY(relay_recovery_poll(&retry,51,0)==RELAY_RECOVERY_EXPIRED);
    relay_recovery_hid_opened(&retry,45);
    relay_recovery_report(&retry,46,1);
    relay_recovery_report(&retry,47,0);
    VERIFY(relay_recovery_poll(&retry,48,1)==RELAY_RECOVERY_NONE);
    relay_recovery_report(&retry,48,1);
    VERIFY(relay_recovery_poll(&retry,50,1)==RELAY_RECOVERY_STABLE && retry.attempts==0);
    relay_recovery_lost(&retry,52,0,0);
    relay_recovery_released(&retry,55);
    relay_recovery_hid_opened(&retry,56);
    VERIFY(relay_recovery_poll(&retry,58,1)==RELAY_RECOVERY_STABLE && retry.attempts==0); /* simple HID host */
    VERIFY(relay_observation(115,5,114.9,110)==0);
    VERIFY(relay_observation(150,5,114.9,60)==0 && relay_observation(175,5,114.9,60)==1);
    VERIFY(relay_observation(156,5,0,60)==-1 && !relay_observation(999,5,0,0));
    connected=1;link_in_sniff=1;sniff_exit_attempts=0;last_sniff_exit=0;
    profile=0;VERIFY(!should_exit_peer_sniff(10));VERIFY(!should_exit_peer_sniff(1000));
    profile=1;VERIFY(should_exit_peer_sniff(10));
    profile=2;VERIFY(should_exit_peer_sniff(10));
    last_sniff_exit=8;VERIFY(!should_exit_peer_sniff(10));
    last_sniff_exit=0;sniff_exit_attempts=3;VERIFY(!should_exit_peer_sniff(10));
    sniff_exit_attempts=0;link_in_sniff=0;VERIFY(!should_exit_peer_sniff(10));
    profile=0;connected=link_in_sniff=0;
    relay_hid_fragments_t fragments={0};
    int fragment_flags=relay_hid_fragment(&fragments,1,1,1,54,54);
    VERIFY(fragment_flags==(RELAY_HID_FRAGMENT_START|RELAY_HID_FRAGMENT_END|RELAY_HID_FRESH_START|RELAY_HID_FRESH_END));
    VERIFY(!fragments.remaining);
    fragment_flags=relay_hid_fragment(&fragments,1,1,1,367,100); /* NFC across ACL fragments */
    VERIFY(fragment_flags==(RELAY_HID_FRAGMENT_START|RELAY_HID_FRESH_START)&&fragments.remaining==267);
    VERIFY(relay_hid_fragment(&fragments,0,0,0,0,100)==0&&fragments.remaining==167);
    VERIFY(relay_hid_fragment(&fragments,0,0,0,0,100)==0&&fragments.remaining==67);
    VERIFY(relay_hid_fragment(&fragments,0,0,0,0,67)==(RELAY_HID_FRAGMENT_END|RELAY_HID_FRESH_END));
    VERIFY(!fragments.remaining&&!relay_hid_fragment(&fragments,0,0,0,0,16));
    fragment_flags=relay_hid_fragment(&fragments,1,1,0,54,54); /* command ACK is not fresh input */
    VERIFY(fragment_flags==(RELAY_HID_FRAGMENT_START|RELAY_HID_FRAGMENT_END));
    VERIFY(!relay_hid_fragment(&fragments,1,0,0,16,16)); /* signaling has no HID credit */
    uint8_t trace_report[362]={0x31};VERIFY(pro1_public_report_bytes(trace_report,362)==13);
    trace_report[0]=0x30;VERIFY(pro1_public_report_bytes(trace_report,49)==49);
    trace_report[0]=0x21;trace_report[14]=1;VERIFY(pro1_public_report_bytes(trace_report,49)==13); /* no pairing body */
    trace_report[13]=0x90;trace_report[14]=0x10;little_endian_store_32(trace_report,15,0x2000);trace_report[19]=16;
    VERIFY(pro1_public_report_bytes(trace_report,49)==13); /* no SPI key storage */
    little_endian_store_32(trace_report,15,0xffffffff);VERIFY(pro1_public_report_bytes(trace_report,49)==13);
    little_endian_store_32(trace_report,15,0x6000);VERIFY(pro1_public_report_bytes(trace_report,49)==49);
    trace_report[19]=30;VERIFY(pro1_public_report_bytes(trace_report,49)==13);
    trace_report[19]=16;
    trace_report[13]=0x80;VERIFY(pro1_public_report_bytes(trace_report,49)==13);trace_report[13]=0x90;
    little_endian_store_32(trace_report,15,0x60ff);VERIFY(pro1_public_report_bytes(trace_report,49)==13); /* end must stay in public area */
    /* Run the real transport ledger and completion callback, not just the
     * policy predicates. A long NFC report must retain its credit until the
     * LAST asynchronous ACL fragment completes. No USB/HCI device is opened. */
    hci_transport_t fake_transport={0};fake_transport.send_packet=test_transport_send;
    base_transport=&fake_transport;stack_receive=test_stack_receive;acl=0x42;
    {
        int saved_profile=profile,saved_ats=ats;
        uint8_t supported[70]={HCI_EVENT_COMMAND_COMPLETE,68,1,0x02,0x10,0};
        uint8_t request[]={0x08,0x14,2,0x42,0};
        uint8_t unsupported[]={HCI_EVENT_COMMAND_STATUS,4,ERROR_CODE_UNKNOWN_HCI_COMMAND,1,0x08,0x14};
        uint8_t unsupported_complete[]={HCI_EVENT_COMMAND_COMPLETE,4,1,0x08,0x14,ERROR_CODE_UNKNOWN_HCI_COMMAND};
        profile=0;ats=1;supported[26]=0x10;
        receive_packet(HCI_EVENT_PACKET,supported,sizeof(supported));
        VERIFY(supported[26]==0x10); /* Pro1 must see the actual controller response. */
        VERIFY(!key_size_command_failed(unsupported,sizeof(unsupported)));
        VERIFY(send_packet(HCI_COMMAND_DATA_PACKET,request,sizeof(request))==0);
        VERIFY(!key_size_command_failed(unsupported,sizeof(unsupported)-1));
        VERIFY(key_size_command_failed(unsupported,sizeof(unsupported)));
        VERIFY(!key_size_command_failed(unsupported,sizeof(unsupported)));
        pending_key_size_handle=acl+1;
        VERIFY(!key_size_command_failed(unsupported_complete,sizeof(unsupported_complete)));
        pending_key_size_handle=acl;
        VERIFY(key_size_command_failed(unsupported_complete,sizeof(unsupported_complete)));
        profile=1;pending_key_size_handle=acl;
        VERIFY(!key_size_command_failed(unsupported,sizeof(unsupported)));
        profile=saved_profile;ats=saved_ats;
    }
    connected=0;ack_head=ack_tail=ack_count=0;hid_in_flight=1;hid_fragments=(relay_hid_fragments_t){0};
    submitted=completions=0;send_is_hid=send_is_fresh=1;
    uint8_t acl_data[104]={0x42,0x20,100,0,0x6b,1,0x42,0,0xa1,0x31};
    uint8_t acl_done[7]={HCI_EVENT_NUMBER_OF_COMPLETED_PACKETS,5,1,0x42,0,1,0};
    VERIFY(send_packet(HCI_ACL_DATA_PACKET,acl_data,104)==0&&submitted==1&&ack_count==1);
    send_is_hid=send_is_fresh=0;
    receive_packet(HCI_EVENT_PACKET,acl_done,sizeof(acl_done));
    VERIFY(hid_in_flight==1&&completions==0&&ack_count==0);
    acl_data[1]=0x10;
    for(int i=0;i<2;i++){
        VERIFY(send_packet(HCI_ACL_DATA_PACKET,acl_data,104)==0&&ack_count==1);
        receive_packet(HCI_EVENT_PACKET,acl_done,sizeof(acl_done));
        VERIFY(hid_in_flight==1&&completions==0&&ack_count==0);
    }
    little_endian_store_16(acl_data,2,67);
    VERIFY(send_packet(HCI_ACL_DATA_PACKET,acl_data,71)==0);
    receive_packet(HCI_EVENT_PACKET,acl_done,sizeof(acl_done));
    VERIFY(hid_in_flight==0&&completions==1&&submitted==1&&ack_count==0);
    acl_data[1]=0x20;little_endian_store_16(acl_data,2,54);little_endian_store_16(acl_data,4,50);acl_data[9]=0x21;
    hid_in_flight=1;send_is_hid=1;
    VERIFY(send_packet(HCI_ACL_DATA_PACKET,acl_data,58)==0);
    send_is_hid=0;receive_packet(HCI_EVENT_PACKET,acl_done,sizeof(acl_done));
    VERIFY(!hid_in_flight&&completions==1&&submitted==1); /* ACK never inflates input Hz */
    disconnect_reason=0x13;stopping=1;
    uint8_t local_closed[]={HCI_EVENT_DISCONNECTION_COMPLETE,4,0,0x42,0,0x16};
    receive_packet(HCI_EVENT_PACKET,local_closed,sizeof(local_closed));
    VERIFY(disconnect_reason==0x13); /* cleanup must not erase peer close cause */
    stopping=0;acl=HCI_CON_HANDLE_INVALID;base_transport=NULL;stack_receive=NULL;
#undef VERIFY
    printf("{\"event\":\"NATIVE_SELF_TEST\",\"cases\":%d,\"failures\":%d,\"radio_opened\":false}\n",cases,failures);return failures?1:0;
}
static int fixture_test(const char*path){
    FILE*file=fopen(path,"rb");if(!file)return 3;
    size_t got=fread(session,1,16,file);uint8_t p[HEADER+MAX_PAYLOAD+1];int size=(int)fread(p,1,sizeof(p),file);int extra=fgetc(file);fclose(file);
    uint64_t sequence=0;int kind=0,len=0;
    int valid=got==16&&extra==EOF&&decode_ipc(p,size,&sequence,&kind,&len);
    if(valid&&(kind==1||kind==2))valid=valid_input(p+HEADER,len)&&(kind!=1||p[HEADER]!=0x21);
    if(valid&&(kind==5||kind==6))valid=len==0;
    printf("{\"event\":\"IPC_FIXTURE\",\"valid\":%s,\"kind\":%d,\"payload_bytes\":%d,\"sequence\":%"PRIu64",\"radio_opened\":false}\n",valid?"true":"false",kind,len,sequence);
    SecureZeroMemory(session,sizeof(session));return valid?0:1;
}
static int relay_main(int argc,char**argv){
    if(argc==2&&!strcmp(argv[1],"--self-test"))return self_test();
    if(argc==2&&!strcmp(argv[1],"--btkey-self-test"))return relay_btkey_self_test();
    if(argc==3&&!strcmp(argv[1],"--verify-transition-capture"))return relay_capture_fixture(argv[2]);
    if(argc==3&&!strcmp(argv[1],"--verify-ipc-fixture"))return fixture_test(argv[2]);
    if(argc==5&&!strcmp(argv[1],"--verify-ipc-fixture")&&!strcmp(argv[3],"--profile")){
        if(!strcmp(argv[4],"ps5"))profile=2;else if(!strcmp(argv[4],"xbox"))profile=1;else if(strcmp(argv[4],"pro1"))return 2;
        return fixture_test(argv[2]);
    }
    const char*instance=NULL,*session_file=NULL;int listen_port=0,managed_port=0;
    for(int i=1;i<argc;i++){
        if(i+1>=argc)return 2;const char*arg=argv[i++],*value=argv[i];
        if(!strcmp(arg,"--instance"))instance=value;
        else if(!strcmp(arg,"--session-file"))session_file=value;
        else if(!strcmp(arg,"--output"))result_path=value;
        else if(!strcmp(arg,"--port"))listen_port=atoi(value);
        else if(!strcmp(arg,"--managed-port"))managed_port=atoi(value);
        else if(!strcmp(arg,"--seconds"))seconds=atoi(value);
        else if(!strcmp(arg,"--require-switch-init")){if(strcmp(value,"1"))return 2;require_switch_init=1;}
        else if(!strcmp(arg,"--wait-seconds"))wait_seconds=atoi(value);
        else if(!strcmp(arg,"--report-hz")){char *end;requested_report_hz=strtod(value,&end);if(*end||!isfinite(requested_report_hz)||requested_report_hz<1||requested_report_hz>250)return 2;}
        else if(!strcmp(arg,"--defer-switch-pairing")){if(strcmp(value,"1"))return 2;defer_switch_pairing=1;}
        else if(!strcmp(arg,"--firmware-folder"))firmware_folder=value;
        else if(!strcmp(arg,"--profile")){if(!strcmp(value,"pro1"))profile=0;else if(!strcmp(value,"xbox"))profile=1;else if(!strcmp(value,"ps5"))profile=2;else return 2;}
        else if(!strcmp(arg,"--peer")){if(!sscanf_bd_addr(value,peer))return 2;have_peer=1;}
        else return 2;
    }
    if((defer_switch_pairing||require_switch_init)&&profile!=0)return 2;
    if(!instance||!relay_set_instance(instance)||!session_file||!result_path||listen_port<1024||listen_port>65535||managed_port<1024||managed_port>65535||listen_port==managed_port||(seconds!=0&&(seconds<15||seconds>600))||wait_seconds<15||wait_seconds>300)return 2;
    FILE*sf=fopen(session_file,"rb");if(!sf)return 3;size_t got=fread(session,1,16,sf);int extra=fgetc(sf);fclose(sf);if(got!=16||extra!=EOF)return 3;
    if(profile==0){
        const char *slash=strrchr(session_file,'\\'),*forward=strrchr(session_file,'/');
        if(forward&&(!slash||forward>slash))slash=forward;
        size_t directory=slash?(size_t)(slash-session_file+1):0;
        btkey_tag_path=malloc(directory+sizeof("relay-ntag215.bin"));if(!btkey_tag_path)return 3;
        memcpy(btkey_tag_path,session_file,directory);memcpy(btkey_tag_path+directory,"relay-ntag215.bin",sizeof("relay-ntag215.bin"));
    }
    realtek=!_strnicmp(instance,"USB\\VID_0BDA",12);ats=!_strnicmp(instance,"USB\\VID_10D7&PID_B012\\",22);
    LARGE_INTEGER freq;QueryPerformanceFrequency(&freq);qpc_frequency=(double)freq.QuadPart;boot=last_heartbeat=last_progress=now();
    setvbuf(stdout,NULL,_IONBF,0);
    HANDLE mutex=CreateMutexA(NULL,FALSE,realtek?"Local\\Pro2RelayRadio0BDA0852":"Local\\Pro2RelayRadio10D7B012");
    if(!mutex||WaitForSingleObject(mutex,0)!=WAIT_OBJECT_0){puts("{\"event\":\"RADIO_BUSY\"}");return 4;}
    WSADATA wsa;if(WSAStartup(MAKEWORD(2,2),&wsa))return 5;
    sock=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);int exclusive=1;setsockopt(sock,SOL_SOCKET,SO_EXCLUSIVEADDRUSE,(const char*)&exclusive,sizeof(exclusive));
    struct sockaddr_in bind_address={0};bind_address.sin_family=AF_INET;bind_address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);bind_address.sin_port=htons((u_short)listen_port);
    if(bind(sock,(struct sockaddr*)&bind_address,sizeof(bind_address))){puts("{\"event\":\"LOCAL_PORT_BUSY\"}");closesocket(sock);WSACleanup();ReleaseMutex(mutex);CloseHandle(mutex);return 5;}
    managed=bind_address;managed.sin_port=htons((u_short)managed_port);
    // Sent only after bind() succeeded. The owner waits for this authenticated
    // datagram before transmitting its first heartbeat.
    emit_ipc(7,NULL,0);
    socket_event=WSACreateEvent();WSAEventSelect(sock,socket_event,FD_READ);
    btstack_memory_init();btstack_run_loop_init(btstack_run_loop_windows_get_instance());
    btstack_run_loop_set_data_source_handle(&ipc_source,socket_event);btstack_run_loop_set_data_source_handler(&ipc_source,ipc_ready);
    btstack_run_loop_enable_data_source_callbacks(&ipc_source,DATA_SOURCE_CALLBACK_READ);btstack_run_loop_add_data_source(&ipc_source);
    hci_transport_usb_add_device(realtek?0x0bda:0x10d7,realtek?0x0852:0xb012);
    base_transport=hci_transport_usb_instance();transport=*base_transport;transport.register_packet_handler=register_receive;transport.send_packet=send_packet;hci_init(&transport,NULL);
    if(realtek&&firmware_folder){btstack_chipset_realtek_set_product_id(0x0852);btstack_chipset_realtek_set_firmware_folder_path(firmware_folder);btstack_chipset_realtek_set_config_folder_path(firmware_folder);hci_set_chipset(btstack_chipset_realtek_instance());hci_enable_custom_pre_init();}
    hci_set_link_key_db(btstack_link_key_db_memory_instance());
    gap_set_local_name(device_names[profile]);gap_set_class_of_device(0x2508);gap_set_bondable_mode(1);gap_ssp_set_io_capability(SSP_IO_CAPABILITY_NO_INPUT_NO_OUTPUT);gap_set_security_level(LEVEL_2);
    uint16_t policy=LM_LINK_POLICY_ENABLE_ROLE_SWITCH|(profile==0?LM_LINK_POLICY_ENABLE_SNIFF_MODE:0);
    gap_set_default_link_policy_settings(policy);gap_set_allow_role_switch(true);gap_discoverable_control(1);gap_connectable_control(1);
    printf("{\"event\":\"LINK_POLICY\",\"default_mask\":%u,\"sniff_allowed\":%s,\"registry_changes\":0}\n",policy,profile==0?"true":"false");
    printf("{\"event\":\"SNIFF_POLICY\",\"profile\":\"%s\",\"host_controls_sniff\":%s,\"active_exit_enabled\":%s,\"registry_changes\":0}\n",profile_names[profile],profile==0?"true":"false",profile==0?"false":"true");
    printf("{\"event\":\"HID_SEND_POLICY\",\"profile\":\"%s\",\"engine\":\"%s\",\"mode_change_quiet_ms\":0,\"in_flight_report_limit\":0,\"receiver_rate_verified\":false}\n",profile_names[profile],profile==0?"btkeyLib_4b49cb9":"existing_multimode");
    if(profile==0){
        relay_btkey_callbacks_t callbacks={btkey_send,btkey_request,NULL};
        if(!relay_btkey_init(&callbacks,requested_report_hz)){puts("{\"event\":\"BTKEY_INIT_FAILED\"}");return 3;}
        build_pro1_eir(pro1_eir);
        gap_set_extended_inquiry_response(pro1_eir);
        puts("{\"event\":\"PRO1_EIR\",\"name\":\"Pro Controller\",\"complete_hid_pnp_uuids\":true,\"source\":2,\"vid\":1406,\"pid\":8201,\"version\":256}");
        printf("{\"event\":\"BTKEY_ENGINE\",\"upstream_commit\":\"4b49cb955d08dc80a82673083fc0463cbc0c2540\",\"requested_hz\":%.3f,\"normal_cap_hz\":%.3f,\"ack_owner\":\"native_engine\",\"input_owner\":\"physical_source\",\"receiver_rate_verified\":false}\n",requested_report_hz,fmin(requested_report_hz,60));
    }else{
        l2cap_init();sdp_init();
        hid_sdp_record_t params=profile_sdp_parameters();
        hid_create_sdp_record(hid_record,0x10002,&params);sdp_register_service(hid_record);
        device_id_create_sdp_record(did_record,0x10003,DEVICE_ID_VENDOR_ID_SOURCE_USB,profile==2?0x054c:0x045e,profile==2?0x0ce6:0x02fd,profile==1?0x0903:0x0300);sdp_register_service(did_record);
        hid_device_init(false,profile_descriptor_size(),profile_descriptor());
    }
    hid_device_register_packet_handler(events);hid_device_register_report_request_callback(get_report);hid_device_register_set_report_callback(set_report);hid_device_register_report_data_callback(report_data);hid_device_accept_truncated_hid_reports(profile==0);
    event_registration.callback=events;hci_add_event_handler(&event_registration);
    btstack_run_loop_set_timer_handler(&timer,watchdog);btstack_run_loop_set_timer(&timer,50);btstack_run_loop_add_timer(&timer);
    printf("{\"event\":\"CONTROLLER_RADIO_START\",\"profile\":\"%s\",\"transport\":\"bluetooth_classic_hid\",\"driver_changes\":0,\"pairing_keys_logged\":false,\"receiver_rate_verified\":false}\n",profile_names[profile]);
    if(profile==0)relay_capture_init(result_path,now());
    hci_power_control(HCI_POWER_ON);btstack_run_loop_execute();
    if(profile==0)relay_capture_finish();
    write_result();btstack_run_loop_remove_data_source(&ipc_source);closesocket(sock);WSACloseEvent(socket_event);WSACleanup();ReleaseMutex(mutex);CloseHandle(mutex);SecureZeroMemory(session,sizeof(session));free(btkey_tag_path);btkey_tag_path=NULL;
    if(pro1_key_size_unsupported)return EXIT_PRO1_KEY_SIZE_UNSUPPORTED;
    return failed||!completed?1:0;
}
int wmain(int argc,wchar_t**wide){
    char**argv=calloc((size_t)argc,sizeof(char*));if(!argv)return 2;
    for(int i=0;i<argc;i++){
        int length=WideCharToMultiByte(CP_UTF8,0,wide[i],-1,NULL,0,NULL,NULL);
        argv[i]=malloc((size_t)length);if(!argv[i])return 2;
        WideCharToMultiByte(CP_UTF8,0,wide[i],-1,argv[i],length,NULL,NULL);
    }
    int result=relay_main(argc,argv);for(int i=0;i<argc;i++)free(argv[i]);free(argv);return result;
}
