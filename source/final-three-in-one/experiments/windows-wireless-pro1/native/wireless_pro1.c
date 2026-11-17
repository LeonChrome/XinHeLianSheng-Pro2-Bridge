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
#include "utf8_files.h"

#define HEADER 48
#define MAX_PAYLOAD 362
#define CONTROL_QUEUE 32
#define EDGE_QUEUE 16
#define ACK_QUEUE 256
#define SAMPLE_LIMIT 180000
typedef struct {uint8_t data[MAX_PAYLOAD];int len;uint64_t sequence;int64_t ticks;} frame_t;
typedef struct {int fresh;double sent;} ack_t;
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
static uint16_t cid;static hci_con_handle_t acl=HCI_CON_HANDLE_INVALID;
static bd_addr_t local,peer;static int have_peer;
static uint8_t session[16],last_report[MAX_PAYLOAD];static int last_report_len;
static uint64_t incoming_sequence,backend_sequence,source_received,source_superseded,source_stale,source_not_ready;
static uint64_t malformed,protocol_replies,host_commands,submitted,completions,ledger_overflow;
static uint64_t digital_transitions_queued,send_failures;
static uint64_t digital_transitions_expired,digital_resyncs,protocol_expired,send_rearms;
static double send_request_time,last_send_event,last_sniff_exit;
static double last_flow_snapshot;
static relay_recovery_t recovery;
static int peer_unplugged;
static unsigned connection_count;
static double connected_since,connected_seconds;
static unsigned sniff_exit_attempts;static int link_in_sniff;
static ack_t ledger[ACK_QUEUE];static unsigned ack_head,ack_tail,ack_count;
static double submitted_times[SAMPLE_LIMIT],completed_times[SAMPLE_LIMIT];static unsigned submitted_count,completed_count;
static int send_is_fresh,sending_len,seconds=180,wait_seconds=180,disconnect_reason=-1;
static double qpc_frequency,boot,ready,stop_time,last_heartbeat,last_progress;
static const char * result_path,*firmware_folder;static int firmware_verified,version_readback;
static SOCKET sock=INVALID_SOCKET;static WSAEVENT socket_event=WSA_INVALID_EVENT;static struct sockaddr_in managed;
static btstack_data_source_t ipc_source;static btstack_timer_source_t timer;
static btstack_packet_callback_registration_t event_registration;
static const hci_transport_t *base_transport;static hci_transport_t transport;
static void(*stack_receive)(uint8_t,uint8_t*,uint16_t);
static uint8_t hid_record[2048],did_record[256];
extern int relay_set_instance(const char *instance);
extern int relay_target_path_allowed(const char *path);
extern int relay_hid_send_checked(uint16_t cid,const uint8_t*message,uint16_t length,uint16_t*remote_mtu);
static void stop_backend(void);
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
static void clear_pending_input(void){
    have_latest=send_requested=0;edge_head=edge_tail=edge_count=0;
    control_head=control_tail=control_count=0;last_report_len=0;
}
static void close_link_accounting(void){
    if(connected_since){connected_seconds+=now()-connected_since;connected_since=0;}
    connected=0;cid=0;clear_pending_input();
}
static int begin_recovery(int reason){
    if(profile!=0||!have_peer||peer_unplugged||stopping||!ever_connected)return 0;
    if(!relay_recovery_begin(&recovery,now(),acl==HCI_CON_HANDLE_INVALID))return 0;
    close_link_accounting();emit_link(4,reason);
    printf("{\"event\":\"HID_RECOVERY_WAIT\",\"attempts_used\":%u,\"limit\":2,\"waiting_old_acl_release\":%s,\"same_peer_only\":true,\"physical_input_kept\":true}\n",recovery.attempts,acl==HCI_CON_HANDLE_INVALID?"false":"true");
    return 1;
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
    if(*kind<1||*kind>6||*len>MAX_PAYLOAD||size!=HEADER+*len||!*sequence)return 0;
    for(int i=40;i<48;i++)if(p[i])return 0;
    return 1;
}
static void request_send(void){
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
        if(kind==6&&len==0){completed=1;stop_backend();continue;}
        if(kind!=1&&kind!=2){malformed++;continue;}
        if(stopping)continue;
        if(!valid_input(p+HEADER,len)||(kind==1&&p[HEADER]==0x21)){malformed++;continue;}
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
    fprintf(out,"{\"profile\":\"%s\",\"transport\":\"bluetooth_classic_hid\",\"connections\":%u,\"reconnect_attempts\":%u,\"digital_expired\":%"PRIu64",\"digital_resyncs\":%"PRIu64",\"control_expired\":%"PRIu64",\"send_rearms\":%"PRIu64",\"sniff_exit_attempts\":%u,",profile_names[profile],connection_count,recovery.attempts,digital_transitions_expired,digital_resyncs,protocol_expired,send_rearms,sniff_exit_attempts);
    fprintf(out,"\"backend\":\"btstack_winusb_multimode\",\"completed\":%s,\"failed\":%s,\"ever_connected\":%s,\"disconnect_reason\":%d,\"connected_seconds\":%.3f,\"source_received\":%"PRIu64",\"source_superseded\":%"PRIu64",\"source_stale\":%"PRIu64",\"source_not_ready\":%"PRIu64",\"invalid_ipc\":%"PRIu64",\"host_output_commands\":%"PRIu64",\"protocol_replies\":%"PRIu64",\"fresh_submissions\":%"PRIu64",\"fresh_hci_completions\":%"PRIu64",\"ledger_overflow\":%"PRIu64",\"submitted\":",completed?"true":"false",failed?"true":"false",ever_connected?"true":"false",disconnect_reason,connected_seconds,source_received,source_superseded,source_stale,source_not_ready,malformed,host_commands,protocol_replies,submitted,completions,ledger_overflow);
    series(out,submitted_times,submitted_count);fputs(",\"hci_completed\":",out);series(out,completed_times,completed_count);
    fprintf(out,",\"digital_transitions_queued\":%"PRIu64",\"l2cap_send_failures\":%"PRIu64",\"realtek_firmware_runtime_verified\":%s,\"receiver_rate_verified\":false,\"driver_changes\":0,\"registry_changes\":0,\"windows_pairing_imported\":false,\"keys_logged\":false,\"tag_writes\":0}\n",digital_transitions_queued,send_failures,firmware_verified?"true":"false");fclose(out);
}
/* Only signaling metadata is logged. Never dump HID/tag payloads or arbitrary
 * HCI events, which can contain pairing keys. Fragmented signals are skipped. */
static void trace_acl_signals(const char *direction,const uint8_t *p,uint16_t size){
    if(size<12||!owns_acl(little_endian_read_16(p,0))||((p[1]>>4)&3)==1||little_endian_read_16(p,6)!=1)return;
    unsigned length=little_endian_read_16(p,4);
    if(length+8>size||little_endian_read_16(p,2)+4u!=size)return;
    for(unsigned pos=8;pos+4<=length+8;){
        unsigned code=p[pos],bytes=little_endian_read_16(p,pos+2);pos+=4;
        if(bytes>length+8-pos)return;
        if((code==6||code==7)&&bytes==4)
            printf("{\"event\":\"L2CAP_CHANNEL_SIGNAL\",\"direction\":\"%s\",\"code\":%u,\"destination_cid\":%u,\"source_cid\":%u}\n",direction,code,little_endian_read_16(p,pos),little_endian_read_16(p,pos+2));
        pos+=bytes;
    }
}
static void receive_packet(uint8_t type,uint8_t*p,uint16_t size){
    if(type==HCI_ACL_DATA_PACKET)trace_acl_signals("peer_to_pc",p,size);
    if(type==HCI_EVENT_PACKET&&size>=2){
        if(p[0]==HCI_EVENT_COMMAND_COMPLETE&&size>=6){
            uint16_t opcode=little_endian_read_16(p,3);
            if(ats&&opcode==HCI_OPCODE_HCI_READ_LOCAL_SUPPORTED_COMMANDS&&size>=70&&p[5]==0)p[26]&=(uint8_t)~(1u<<4);
            printf("{\"event\":\"HCI_COMMAND_COMPLETE\",\"opcode\":%u,\"status\":%u}\n",opcode,p[5]);
            if(opcode==HCI_OPCODE_HCI_READ_LOCAL_VERSION_INFORMATION&&size>=14&&p[5]==0){
                int rom=little_endian_read_16(p,12)==0x8852;
                if(realtek&&!rom)firmware_verified=1;
                printf("{\"event\":\"CONTROLLER_VERSION\",\"manufacturer\":%u,\"lmp_subversion\":%u,\"realtek_rom\":%s}\n",little_endian_read_16(p,10),little_endian_read_16(p,12),rom?"true":"false");
                if(realtek&&version_readback==2&&rom){failed=1;puts("{\"event\":\"REALTEK_STILL_ROM_ABORT\"}");stop_backend();}
            }
        }else if(p[0]==HCI_EVENT_COMMAND_STATUS&&size>=6){
            printf("{\"event\":\"HCI_COMMAND_STATUS\",\"opcode\":%u,\"status\":%u}\n",little_endian_read_16(p,4),p[2]);
        }else if(p[0]==HCI_EVENT_READ_REMOTE_VERSION_INFORMATION_COMPLETE&&size>=10){
            printf("{\"event\":\"REMOTE_VERSION_COMPLETE\",\"status\":%u,\"handle\":%u,\"manufacturer\":%u,\"lmp_subversion\":%u}\n",p[2],little_endian_read_16(p,3),little_endian_read_16(p,6),little_endian_read_16(p,8));
        }else if(p[0]==HCI_EVENT_CONNECTION_COMPLETE&&size>=13&&p[2]==0){
            bd_addr_t who;reverse_bd_addr(p+5,who);if(!have_peer||!bd_addr_cmp(who,peer))acl=little_endian_read_16(p,3);
        }
        else if(p[0]==HCI_EVENT_NUMBER_OF_COMPLETED_PACKETS&&size>=3){
            for(unsigned i=0;i<p[2]&&3+4*i+4<=size;i++)if(little_endian_read_16(p,3+4*i)==acl){
                unsigned count=little_endian_read_16(p,5+4*i);
                for(unsigned j=0;j<count&&ack_count;j++){
                    ack_t a=ledger[ack_head];ack_head=(ack_head+1)%ACK_QUEUE;ack_count--;
                    if(a.fresh){completions++;if(completed_count<SAMPLE_LIMIT)completed_times[completed_count++]=now();}
                }
            }
        }else if(p[0]==HCI_EVENT_DISCONNECTION_COMPLETE&&size>=6&&owns_acl(little_endian_read_16(p,3))){disconnect_reason=p[5];}
    }
    // Never log arbitrary raw HCI events: link-key events contain pairing keys.
    stack_receive(type,p,size);
}
static void register_receive(void(*callback)(uint8_t,uint8_t*,uint16_t)){stack_receive=callback;base_transport->register_packet_handler(receive_packet);}
static int send_packet(uint8_t type,uint8_t*p,int size){
    if(type==HCI_ACL_DATA_PACKET)trace_acl_signals("pc_to_peer",p,(uint16_t)size);
    int fresh=send_is_fresh&&type==HCI_ACL_DATA_PACKET&&size==sending_len+9&&p[8]==0xa1;
    int result=base_transport->send_packet(type,p,size);
    if(!result&&type==HCI_ACL_DATA_PACKET&&size>=4&&(little_endian_read_16(p,0)&0xfff)==acl){
        if(ack_count==ACK_QUEUE){ledger_overflow++;failed=1;stop_backend();}
        else{ledger[ack_tail]=(ack_t){fresh,now()};ack_tail=(ack_tail+1)%ACK_QUEUE;ack_count++;}
        if(fresh){submitted++;if(submitted_count<SAMPLE_LIMIT)submitted_times[submitted_count++]=now();}
    }
    return result;
}
static void host_output(uint16_t id,const uint8_t*body,int len){
    if(!connected||stopping||len<0||len+1>MAX_PAYLOAD)return;
    uint8_t output[MAX_PAYLOAD];output[0]=(uint8_t)id;if(len)memcpy(output+1,body,len);
    if(profile==0&&id!=1&&id!=0x10&&id!=0x11)return;
    if(profile==1&&id!=3)return;
    if(profile==2&&id!=0x31){printf("{\"event\":\"UNSUPPORTED_PS5_BT_OUTPUT\",\"report_id\":%u,\"bytes\":%d,\"audio_transcoding_available\":false}\n",id,len+1);return;}
    host_commands++;emit_ipc(3,output,len+1);
    printf("{\"event\":\"HOST_OUTPUT\",\"report_id\":%u,\"bytes\":%d,\"command\":%d,\"tag_data_logged\":false}\n",id,len+1,len>=10?body[9]:-1);
}
static void report_data(uint16_t channel,hid_report_type_t type,uint16_t id,int len,uint8_t*data){if(channel==cid&&type==HID_REPORT_TYPE_OUTPUT)host_output(id,data,len);}
static void set_report(uint16_t channel,hid_report_type_t type,int len,uint8_t*data){if(channel==cid&&type==HID_REPORT_TYPE_OUTPUT&&len>0)host_output(data[0],data+1,len-1);}
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
    if(channel!=cid)return 0;
    if(profile==2&&type==HID_REPORT_TYPE_FEATURE){uint8_t report[64];int size=ps5_feature(id,report);if(!size)return 0;*len=size-1;memcpy(data,report+1,(size_t)*len);printf("{\"event\":\"PS5_BT_FEATURE\",\"report_id\":%u,\"bytes\":%d,\"crc\":true}\n",id,size);return 1;}
    if(profile==2&&type==HID_REPORT_TYPE_INPUT&&id==1){
        memset(data,0,9);memset(data,128,4);data[4]=8;
        if(last_report_len==78&&last_report[0]==0x31){memcpy(data,last_report+2,4);memcpy(data+4,last_report+9,3);data[7]=last_report[6];data[8]=last_report[7];}
        *len=9;return 1;
    }
    if(profile==1&&type==HID_REPORT_TYPE_INPUT&&id==2){*len=1;data[0]=0;return 1;}
    if(profile==1&&type==HID_REPORT_TYPE_INPUT&&id==4&&xbox_battery>=0){*len=1;data[0]=(uint8_t)xbox_battery;return 1;}
    if(profile==1&&type==HID_REPORT_TYPE_INPUT&&id==1&&!last_report_len){memset(data,0,16);for(int i=0;i<8;i+=2)little_endian_store_16(data,i,32768);*len=16;return 1;}
    if(type!=HID_REPORT_TYPE_INPUT||!last_report_len||last_report[0]!=id)return 0;
    *len=last_report_len-1;memcpy(data,last_report+1,*len);return 1;
}
static void events(uint8_t type,uint16_t channel,uint8_t*p,uint16_t size){
    (void)channel;if(type!=HCI_EVENT_PACKET||size<2)return;
    switch(p[0]){
        case BTSTACK_EVENT_STATE:
            if(btstack_event_state_get_state(p)==HCI_STATE_WORKING){
                stack_working=1;
                gap_local_bd_addr(local);
                printf("{\"event\":\"STACK_WORKING\",\"address\":\"%s\",\"identity\":\"%s\",\"profile\":\"%s\"}\n",bd_addr_to_str(local),device_names[profile],profile_names[profile]);
                emit_link(0,0);
                if(realtek)version_readback=1;
            }else if(btstack_event_state_get_state(p)==HCI_STATE_OFF&&stopping)btstack_run_loop_trigger_exit();
            break;
        case BTSTACK_EVENT_POWERON_FAILED:
            failed=1;puts("{\"event\":\"RADIO_POWERON_FAILED\",\"driver_changed\":false}");emit_link(3,1);stop_backend();break;
        case HCI_EVENT_USER_CONFIRMATION_REQUEST:{
            bd_addr_t who;hci_event_user_confirmation_request_get_bd_addr(p,who);
            if(!have_peer||!bd_addr_cmp(who,peer))gap_ssp_confirmation_response(who);else gap_ssp_confirmation_negative(who);break;
        }
        case HCI_EVENT_PIN_CODE_REQUEST:{bd_addr_t who;hci_event_pin_code_request_get_bd_addr(p,who);if(!have_peer||!bd_addr_cmp(who,peer))gap_pin_code_response(who,"0000");break;}
        case HCI_EVENT_SIMPLE_PAIRING_COMPLETE:printf("{\"event\":\"PAIRING_COMPLETE\",\"status\":%u}\n",p[2]);break;
        case HCI_EVENT_ENCRYPTION_CHANGE:printf("{\"event\":\"ENCRYPTION\",\"status\":%u,\"enabled\":%u}\n",p[2],p[5]);break;
        case HCI_EVENT_MODE_CHANGE:
            if(size<8)break;
            printf("{\"event\":\"LINK_MODE\",\"status\":%u,\"mode\":%u,\"interval_slots\":%u}\n",p[2],p[5],little_endian_read_16(p,6));
            if(size>=8&&p[2]==0&&owns_acl(little_endian_read_16(p,3))){link_in_sniff=p[5]==2;send_requested=0;request_send();}break;
        case HCI_EVENT_CONNECTION_COMPLETE:
            if(size>=13&&p[2]&&recovery.active){
                bd_addr_t who;reverse_bd_addr(p+5,who);
                if(!bd_addr_cmp(who,peer)){failed=1;printf("{\"event\":\"HID_RECOVERY_ACL_FAILED\",\"status\":%u}\n",p[2]);emit_link(3,p[2]);stop_backend();}
            }break;
        case HCI_EVENT_DISCONNECTION_COMPLETE:
            printf("{\"event\":\"ACL_DISCONNECTED\",\"reason\":%u,\"cleanup\":%s}\n",p[5],stopping?"true":"false");
            if(!stopping&&ever_connected&&owns_acl(little_endian_read_16(p,3))){
                close_link_accounting();acl=HCI_CON_HANDLE_INVALID;
                ack_head=ack_tail=ack_count=0;link_in_sniff=0;
                if(p[5]==ERROR_CODE_REMOTE_USER_TERMINATED_CONNECTION&&begin_recovery(p[5]))relay_recovery_acl_down(&recovery,now());
                else{failed=1;emit_link(2,p[5]);stop_backend();}
            }break;
        case HCI_EVENT_HID_META:
            switch(hci_event_hid_meta_get_subevent_code(p)){
                case HID_SUBEVENT_SUSPEND:puts("{\"event\":\"PEER_HID_SUSPEND\"}");break;
                case HID_SUBEVENT_EXIT_SUSPEND:puts("{\"event\":\"PEER_HID_EXIT_SUSPEND\"}");break;
                case HID_SUBEVENT_VIRTUAL_CABLE_UNPLUG:peer_unplugged=1;puts("{\"event\":\"PEER_VIRTUAL_CABLE_UNPLUG\",\"auto_reconnect\":false}");break;
                case HID_SUBEVENT_CONNECTION_OPENED:{
                    uint8_t status=hid_subevent_connection_opened_get_status(p);
                    if(status){printf("{\"event\":\"HID_CONNECT_FAILED\",\"status\":%u}\n",status);if(recovery.active){failed=1;emit_link(3,status);stop_backend();}break;}
                    bd_addr_t who;hid_subevent_connection_opened_get_bd_addr(p,who);uint16_t opened=hid_subevent_connection_opened_get_hid_cid(p);
                    if(have_peer&&bd_addr_cmp(who,peer)){hid_device_disconnect(opened);break;}cid=opened;
                    memcpy(peer,who,6);have_peer=1;acl=hid_subevent_connection_opened_get_con_handle(p);
                    if(gap_security_level(acl)<LEVEL_2){failed=1;puts("{\"event\":\"ENCRYPTION_REQUIRED_ABORT\"}");stop_backend();break;}
                    if(recovery.active){clear_pending_input();emit_link(0,0);}
                    relay_recovery_opened(&recovery);connection_count++;connected_since=now();if(!ever_connected)ready=connected_since;
                    connected=ever_connected=1;peer_unplugged=0;last_send_event=0;gap_discoverable_control(0);
                    printf("{\"event\":\"CONTROLLER_HID_READY\",\"profile\":\"%s\",\"receiver_rate_verified\":false}\n",profile_names[profile]);emit_link(1,0);request_send();break;
                }
                case HID_SUBEVENT_CONNECTION_CLOSED:
                    if(hid_subevent_connection_closed_get_hid_cid(p)!=cid)break;
                    trace_flow("hid_channels_closed");close_link_accounting();puts("{\"event\":\"HID_CLOSED\"}");
                    if(ever_connected&&!stopping&&!begin_recovery(0)){failed=1;emit_link(2,disconnect_reason);stop_backend();}break;
                case HID_SUBEVENT_CAN_SEND_NOW:{
                    if(hid_subevent_can_send_now_get_hid_cid(p)!=cid)break;
                    send_requested=0;last_send_event=now();if(!connected||stopping||!relay_hid_channels_open(cid))break;
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
                    uint16_t remote_mtu=0;send_is_fresh=fresh;sending_len=frame.len;
                    int status=relay_hid_send_checked(cid,message,(uint16_t)(frame.len+1),&remote_mtu);send_is_fresh=0;
                    if(status){send_failures++;failed=1;printf("{\"event\":\"HID_SEND_REJECTED\",\"status\":%d,\"message_bytes\":%d,\"remote_mtu\":%u}\n",status,frame.len+1,remote_mtu);emit_link(3,status);stop_backend();break;}
                    request_send();break;
                }
            }break;
    }
}
static void stop_backend(void){if(stopping)return;stopping=1;stop_time=now();close_link_accounting();hci_power_control(HCI_POWER_OFF);}
static void watchdog(btstack_timer_source_t*t){
    double stamp=now();
    if(stopping&&stamp-stop_time>5){puts("{\"event\":\"POWER_OFF_TIMEOUT\"}");failed=1;btstack_run_loop_trigger_exit();return;}
    if(!stopping){
        int reconnect_action=relay_recovery_poll(&recovery,stamp);
        if(reconnect_action<0){failed=1;puts("{\"event\":\"HID_RECOVERY_TIMEOUT\",\"old_acl_reset\":false}");emit_link(3,0xff);stop_backend();}
        else if(reconnect_action>0){
            clear_pending_input();
            uint8_t status=hid_device_connect(peer,&cid);
            printf("{\"event\":\"HID_RECOVERY_CONNECT\",\"attempt\":%u,\"limit\":2,\"status\":%u,\"same_peer_only\":true,\"old_acl_released\":true}\n",recovery.attempts,status);
            if(status){failed=1;emit_link(3,status);stop_backend();}
        }
        if(connected&&(control_count||edge_count||have_latest)&&send_requested&&stamp-send_request_time>.25){
            if(stamp-last_flow_snapshot>=.25){last_flow_snapshot=stamp;trace_flow("send_wait_over_250ms");}
            send_requested=0;send_rearms++;if(send_rearms<=5)puts("{\"event\":\"SEND_NOW_REARM\",\"pending_real_input\":true,\"disconnect\":false}");request_send();
        }
        if(connected&&link_in_sniff&&sniff_exit_attempts<3&&stamp-last_sniff_exit>=5){
            last_sniff_exit=stamp;sniff_exit_attempts++;uint8_t status=gap_sniff_mode_exit(acl);
            printf("{\"event\":\"ACTIVE_LINK_REQUEST\",\"attempt\":%u,\"status\":%u,\"reason\":\"receiver_entered_sniff\"}\n",sniff_exit_attempts,status);
        }
        if(version_readback==1&&hci_can_send_command_packet_now()){version_readback=2;hci_send_cmd(&hci_read_local_version_information);}
        if(!stack_working&&stamp-boot>30){failed=1;puts("{\"event\":\"RADIO_INIT_TIMEOUT\"}");emit_link(3,2);stop_backend();}
        else if(stamp-last_heartbeat>5){failed=1;puts("{\"event\":\"MANAGED_HEARTBEAT_TIMEOUT\"}");stop_backend();}
        else if(!ever_connected&&stamp-boot>wait_seconds){failed=1;puts("{\"event\":\"PAIRING_WINDOW_TIMEOUT\"}");stop_backend();}
        else if(seconds>0&&connected&&stamp-ready>=seconds){completed=1;stop_backend();}
        if(connected&&stamp-last_progress>=5)trace_flow("periodic");
        if(stamp-last_progress>=5){last_progress=stamp;printf("{\"event\":\"WIRELESS_WINDOW\",\"connected\":%s,\"source\":%"PRIu64",\"submitted\":%"PRIu64",\"hci_completed\":%"PRIu64",\"superseded\":%"PRIu64",\"stale\":%"PRIu64",\"host_commands\":%"PRIu64"}\n",connected?"true":"false",source_received,submitted,completions,source_superseded,source_stale,host_commands);
            printf("{\"event\":\"TX_HEALTH\",\"edge_queue\":%u,\"control_queue\":%u,\"digital_expired\":%"PRIu64",\"digital_resyncs\":%"PRIu64",\"send_rearms\":%"PRIu64",\"last_send_event_age_ms\":%.1f,\"sniff\":%s}\n",edge_count,control_count,digital_transitions_expired,digital_resyncs,send_rearms,last_send_event?1000*(stamp-last_send_event):0,link_in_sniff?"true":"false");}
    }
    btstack_run_loop_set_timer(t,50);btstack_run_loop_add_timer(t);
}
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
    VERIFY(sizeof(ps5_descriptor)==279);VERIFY(sizeof(xbox_descriptor)==334);
    VERIFY(btstack_hid_get_report_size_for_id(0x31,HID_REPORT_TYPE_INPUT,ps5_descriptor,sizeof(ps5_descriptor))==77);
    VERIFY(btstack_hid_get_report_size_for_id(0x31,HID_REPORT_TYPE_OUTPUT,ps5_descriptor,sizeof(ps5_descriptor))==77);
    VERIFY(btstack_hid_get_report_size_for_id(5,HID_REPORT_TYPE_FEATURE,ps5_descriptor,sizeof(ps5_descriptor))==40);
    VERIFY(btstack_hid_get_report_size_for_id(1,HID_REPORT_TYPE_INPUT,xbox_descriptor,sizeof(xbox_descriptor))==16);
    VERIFY(btstack_hid_get_report_size_for_id(2,HID_REPORT_TYPE_INPUT,xbox_descriptor,sizeof(xbox_descriptor))==1);
    VERIFY(btstack_hid_get_report_size_for_id(3,HID_REPORT_TYPE_OUTPUT,xbox_descriptor,sizeof(xbox_descriptor))==8);
    uint8_t feature[64];VERIFY(ps5_feature(5,feature)==41);VERIFY(little_endian_read_32(feature,37)==ps_crc(feature,37,0xa3));
    VERIFY(ps5_feature(9,feature)==20);VERIFY(ps5_feature(0x20,feature)==64);VERIFY(ps5_feature(0xf0,feature)==0);
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
    VERIFY(relay_recovery_begin(&retry,10,0));
    VERIFY(relay_recovery_poll(&retry,11)==0); /* old ACL must finish closing */
    relay_recovery_acl_down(&retry,11);
    VERIFY(relay_recovery_poll(&retry,11.49)==0);
    VERIFY(relay_recovery_poll(&retry,11.5)==1&&retry.attempts==1);
    VERIFY(relay_recovery_poll(&retry,12)==0); /* never create two in flight */
    relay_recovery_opened(&retry);
    VERIFY(!retry.active&&retry.attempts==1); /* brief reconnect does not reset budget */
    VERIFY(relay_recovery_begin(&retry,14,1));
    VERIFY(relay_recovery_poll(&retry,14.5)==1&&retry.attempts==2);
    relay_recovery_opened(&retry);
    VERIFY(!relay_recovery_begin(&retry,16,1));
    retry=(relay_recovery_t){0};VERIFY(relay_recovery_begin(&retry,20,0));
    VERIFY(relay_recovery_poll(&retry,40)==-1&&retry.attempts==0);
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
        else if(!strcmp(arg,"--wait-seconds"))wait_seconds=atoi(value);
        else if(!strcmp(arg,"--firmware-folder"))firmware_folder=value;
        else if(!strcmp(arg,"--profile")){if(!strcmp(value,"pro1"))profile=0;else if(!strcmp(value,"xbox"))profile=1;else if(!strcmp(value,"ps5"))profile=2;else return 2;}
        else if(!strcmp(arg,"--peer")){if(!sscanf_bd_addr(value,peer))return 2;have_peer=1;}
        else return 2;
    }
    if(!instance||!relay_set_instance(instance)||!session_file||!result_path||listen_port<1024||listen_port>65535||managed_port<1024||managed_port>65535||listen_port==managed_port||(seconds!=0&&(seconds<15||seconds>600))||wait_seconds<15||wait_seconds>300)return 2;
    FILE*sf=fopen(session_file,"rb");if(!sf)return 3;size_t got=fread(session,1,16,sf);int extra=fgetc(sf);fclose(sf);if(got!=16||extra!=EOF)return 3;
    realtek=!_strnicmp(instance,"USB\\VID_0BDA",12);ats=!realtek;
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
    l2cap_init();sdp_init();
    hid_sdp_record_t params={8,0,1,1,1,true,false,24,24,3200,profile_descriptor(),profile_descriptor_size(),device_names[profile]};
    hid_create_sdp_record(hid_record,0x10002,&params);sdp_register_service(hid_record);
    device_id_create_sdp_record(did_record,0x10003,DEVICE_ID_VENDOR_ID_SOURCE_USB,profile==2?0x054c:profile==1?0x045e:0x057e,profile==2?0x0ce6:profile==1?0x02fd:0x2009,profile==1?0x0903:0x0300);sdp_register_service(did_record);
    hid_device_init(false,profile_descriptor_size(),profile_descriptor());hid_device_register_packet_handler(events);hid_device_register_report_request_callback(get_report);hid_device_register_set_report_callback(set_report);hid_device_register_report_data_callback(report_data);hid_device_accept_truncated_hid_reports(profile==0);
    event_registration.callback=events;hci_add_event_handler(&event_registration);
    btstack_run_loop_set_timer_handler(&timer,watchdog);btstack_run_loop_set_timer(&timer,50);btstack_run_loop_add_timer(&timer);
    printf("{\"event\":\"CONTROLLER_RADIO_START\",\"profile\":\"%s\",\"transport\":\"bluetooth_classic_hid\",\"driver_changes\":0,\"pairing_keys_logged\":false,\"receiver_rate_verified\":false}\n",profile_names[profile]);
    hci_power_control(HCI_POWER_ON);btstack_run_loop_execute();
    write_result();btstack_run_loop_remove_data_source(&ipc_source);closesocket(sock);WSACloseEvent(socket_event);WSACleanup();ReleaseMutex(mutex);CloseHandle(mutex);SecureZeroMemory(session,sizeof(session));
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
