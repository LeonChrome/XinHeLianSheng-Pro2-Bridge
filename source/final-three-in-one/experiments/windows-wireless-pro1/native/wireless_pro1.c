/* Pro2/Pro1 USB -> Windows -> Nintendo Pro Controller Classic HID.
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
static uint16_t cid;static hci_con_handle_t acl=HCI_CON_HANDLE_INVALID;
static bd_addr_t local,peer;static int have_peer;
static uint8_t session[16],last_report[MAX_PAYLOAD];static int last_report_len;
static uint64_t incoming_sequence,backend_sequence,source_received,source_superseded,source_stale,source_not_ready;
static uint64_t malformed,protocol_replies,host_commands,submitted,completions,ledger_overflow;
static uint64_t digital_transitions_queued,send_failures;
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
static int valid_input(const uint8_t*report,int len){
    return (len==49&&(report[0]==0x30||report[0]==0x21))||(len==362&&report[0]==0x31)||(len==12&&report[0]==0x3f);
}
static int same_digital_state(const frame_t*previous,const uint8_t*report,int len){
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
    if(!stopping&&connected&&!send_requested&&(control_count||edge_count||have_latest)){
        send_requested=1;hid_device_request_can_send_now_event(cid);
    }
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
        if(!valid_input(p+HEADER,len)||(kind==1&&p[HEADER]==0x21)){malformed++;continue;}
        if(kind==1){
            source_received++;
            double age=(ticks()-(int64_t)read64(p+32))/qpc_frequency;
            if(age<-.01||age>.1){source_stale++;continue;}
            if(!connected){source_not_ready++;continue;}
            if(have_latest){
                if(!same_digital_state(&latest,p+HEADER,len)){
                    if(edge_count==EDGE_QUEUE){failed=1;puts("{\"event\":\"DIGITAL_TRANSITION_OVERFLOW_ABORT\"}");stop_backend();continue;}
                    edges[edge_tail]=latest;edge_tail=(edge_tail+1)%EDGE_QUEUE;edge_count++;digital_transitions_queued++;
                }else source_superseded++;
            }
            memcpy(latest.data,p+HEADER,len);latest.len=len;latest.sequence=sequence;latest.ticks=(int64_t)read64(p+32);have_latest=1;
        }else{
            if(!connected){malformed++;continue;}
            if(control_count==CONTROL_QUEUE){failed=1;puts("{\"event\":\"PROTOCOL_REPLY_OVERFLOW_ABORT\"}");stop_backend();continue;}
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
    fprintf(out,"{\"backend\":\"btstack_winusb_pro1\",\"completed\":%s,\"failed\":%s,\"ever_connected\":%s,\"disconnect_reason\":%d,\"connected_seconds\":%.3f,\"source_received\":%"PRIu64",\"source_superseded\":%"PRIu64",\"source_stale\":%"PRIu64",\"source_not_ready\":%"PRIu64",\"invalid_ipc\":%"PRIu64",\"host_output_commands\":%"PRIu64",\"protocol_replies\":%"PRIu64",\"fresh_submissions\":%"PRIu64",\"fresh_hci_completions\":%"PRIu64",\"ledger_overflow\":%"PRIu64",\"submitted\":",completed?"true":"false",failed?"true":"false",ever_connected?"true":"false",disconnect_reason,ready?(stop_time?stop_time:now())-ready:0,source_received,source_superseded,source_stale,source_not_ready,malformed,host_commands,protocol_replies,submitted,completions,ledger_overflow);
    series(out,submitted_times,submitted_count);fputs(",\"hci_completed\":",out);series(out,completed_times,completed_count);
    fprintf(out,",\"digital_transitions_queued\":%"PRIu64",\"l2cap_send_failures\":%"PRIu64",\"realtek_firmware_runtime_verified\":%s,\"receiver_rate_verified\":false,\"driver_changes\":0,\"registry_changes\":0,\"windows_pairing_imported\":false,\"keys_logged\":false,\"tag_writes\":0}\n",digital_transitions_queued,send_failures,firmware_verified?"true":"false");fclose(out);
}
static void receive_packet(uint8_t type,uint8_t*p,uint16_t size){
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
    if(id!=1&&id!=0x10&&id!=0x11)return;
    host_commands++;emit_ipc(3,output,len+1);
    printf("{\"event\":\"HOST_OUTPUT\",\"report_id\":%u,\"bytes\":%d,\"command\":%d,\"tag_data_logged\":false}\n",id,len+1,len>=10?body[9]:-1);
}
static void report_data(uint16_t channel,hid_report_type_t type,uint16_t id,int len,uint8_t*data){if(channel==cid&&type==HID_REPORT_TYPE_OUTPUT)host_output(id,data,len);}
static void set_report(uint16_t channel,hid_report_type_t type,int len,uint8_t*data){if(channel==cid&&type==HID_REPORT_TYPE_OUTPUT&&len>0)host_output(data[0],data+1,len-1);}
static int get_report(uint16_t channel,hid_report_type_t type,uint16_t id,int*len,uint8_t*data){
    if(channel!=cid||type!=HID_REPORT_TYPE_INPUT||!last_report_len||last_report[0]!=id)return 0;
    *len=last_report_len-1;memcpy(data,last_report+1,*len);return 1;
}
static void events(uint8_t type,uint16_t channel,uint8_t*p,uint16_t size){
    (void)channel;if(type!=HCI_EVENT_PACKET||size<2)return;
    switch(p[0]){
        case BTSTACK_EVENT_STATE:
            if(btstack_event_state_get_state(p)==HCI_STATE_WORKING){
                stack_working=1;
                gap_local_bd_addr(local);
                printf("{\"event\":\"STACK_WORKING\",\"address\":\"%s\",\"identity\":\"Pro Controller\"}\n",bd_addr_to_str(local));
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
        case HCI_EVENT_MODE_CHANGE:printf("{\"event\":\"LINK_MODE\",\"status\":%u,\"mode\":%u,\"interval_slots\":%u}\n",p[2],p[5],little_endian_read_16(p,6));break;
        case HCI_EVENT_DISCONNECTION_COMPLETE:
            printf("{\"event\":\"ACL_DISCONNECTED\",\"reason\":%u,\"cleanup\":%s}\n",p[5],stopping?"true":"false");
            if(!stopping&&ever_connected&&owns_acl(little_endian_read_16(p,3))){failed=1;emit_link(2,p[5]);stop_backend();}break;
        case HCI_EVENT_HID_META:
            switch(hci_event_hid_meta_get_subevent_code(p)){
                case HID_SUBEVENT_CONNECTION_OPENED:{
                    uint8_t status=hid_subevent_connection_opened_get_status(p);
                    if(status){printf("{\"event\":\"HID_CONNECT_FAILED\",\"status\":%u}\n",status);break;}
                    bd_addr_t who;hid_subevent_connection_opened_get_bd_addr(p,who);uint16_t opened=hid_subevent_connection_opened_get_hid_cid(p);
                    if(have_peer&&bd_addr_cmp(who,peer)){hid_device_disconnect(opened);break;}cid=opened;
                    memcpy(peer,who,6);have_peer=1;acl=hid_subevent_connection_opened_get_con_handle(p);
                    if(gap_security_level(acl)<LEVEL_2){failed=1;puts("{\"event\":\"ENCRYPTION_REQUIRED_ABORT\"}");stop_backend();break;}
                    connected=ever_connected=1;ready=now();gap_discoverable_control(0);
                    puts("{\"event\":\"PRO1_HID_READY\",\"receiver_rate_verified\":false}");emit_link(1,0);request_send();break;
                }
                case HID_SUBEVENT_CONNECTION_CLOSED:
                    if(hid_subevent_connection_closed_get_hid_cid(p)!=cid)break;
                    connected=0;cid=0;send_requested=0;puts("{\"event\":\"HID_CLOSED\"}");if(ever_connected&&!stopping){failed=1;emit_link(2,disconnect_reason);stop_backend();}break;
                case HID_SUBEVENT_CAN_SEND_NOW:{
                    if(hid_subevent_can_send_now_get_hid_cid(p)!=cid)break;
                    send_requested=0;if(!connected||stopping)break;
                    frame_t frame;int fresh=0;
                    if(control_count){frame=controls[control_head];control_head=(control_head+1)%CONTROL_QUEUE;control_count--;}
                    else if(edge_count){frame=edges[edge_head];edge_head=(edge_head+1)%EDGE_QUEUE;edge_count--;fresh=1;}
                    else if(have_latest){frame=latest;have_latest=0;fresh=1;if((ticks()-frame.ticks)/qpc_frequency>.1){source_stale++;request_send();break;}}
                    else break;
                    if((ticks()-frame.ticks)/qpc_frequency>.1){
                        if(fresh){source_stale++;request_send();break;}
                        failed=1;puts("{\"event\":\"PROTOCOL_REPLY_STALE_ABORT\"}");stop_backend();break;
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
static void stop_backend(void){if(stopping)return;stopping=1;stop_time=now();hci_power_control(HCI_POWER_OFF);}
static void watchdog(btstack_timer_source_t*t){
    double stamp=now();
    if(stopping&&stamp-stop_time>5){puts("{\"event\":\"POWER_OFF_TIMEOUT\"}");failed=1;btstack_run_loop_trigger_exit();return;}
    if(!stopping){
        if(version_readback==1&&hci_can_send_command_packet_now()){version_readback=2;hci_send_cmd(&hci_read_local_version_information);}
        if(!stack_working&&stamp-boot>30){failed=1;puts("{\"event\":\"RADIO_INIT_TIMEOUT\"}");emit_link(3,2);stop_backend();}
        else if(stamp-last_heartbeat>5){failed=1;puts("{\"event\":\"MANAGED_HEARTBEAT_TIMEOUT\"}");stop_backend();}
        else if(!ever_connected&&stamp-boot>wait_seconds){failed=1;puts("{\"event\":\"PAIRING_WINDOW_TIMEOUT\"}");stop_backend();}
        else if(connected&&stamp-ready>=seconds){completed=1;stop_backend();}
        if(stamp-last_progress>=5){last_progress=stamp;printf("{\"event\":\"WIRELESS_WINDOW\",\"connected\":%s,\"source\":%"PRIu64",\"submitted\":%"PRIu64",\"hci_completed\":%"PRIu64",\"superseded\":%"PRIu64",\"stale\":%"PRIu64",\"host_commands\":%"PRIu64"}\n",connected?"true":"false",source_received,submitted,completions,source_superseded,source_stale,host_commands);}
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
    printf("{\"event\":\"NATIVE_SELF_TEST\",\"cases\":16,\"failures\":%d,\"radio_opened\":false}\n",failures);return failures?1:0;
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
        else if(!strcmp(arg,"--peer")){if(!sscanf_bd_addr(value,peer))return 2;have_peer=1;}
        else return 2;
    }
    if(!instance||!relay_set_instance(instance)||!session_file||!result_path||listen_port<1024||listen_port>65535||managed_port<1024||managed_port>65535||listen_port==managed_port||seconds<15||seconds>600||wait_seconds<15||wait_seconds>300)return 2;
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
    socket_event=WSACreateEvent();WSAEventSelect(sock,socket_event,FD_READ);
    btstack_memory_init();btstack_run_loop_init(btstack_run_loop_windows_get_instance());
    btstack_run_loop_set_data_source_handle(&ipc_source,socket_event);btstack_run_loop_set_data_source_handler(&ipc_source,ipc_ready);
    btstack_run_loop_enable_data_source_callbacks(&ipc_source,DATA_SOURCE_CALLBACK_READ);btstack_run_loop_add_data_source(&ipc_source);
    hci_transport_usb_add_device(realtek?0x0bda:0x10d7,realtek?0x0852:0xb012);
    base_transport=hci_transport_usb_instance();transport=*base_transport;transport.register_packet_handler=register_receive;transport.send_packet=send_packet;hci_init(&transport,NULL);
    if(realtek&&firmware_folder){btstack_chipset_realtek_set_product_id(0x0852);btstack_chipset_realtek_set_firmware_folder_path(firmware_folder);btstack_chipset_realtek_set_config_folder_path(firmware_folder);hci_set_chipset(btstack_chipset_realtek_instance());hci_enable_custom_pre_init();}
    hci_set_link_key_db(btstack_link_key_db_memory_instance());
    gap_set_local_name("Pro Controller");gap_set_class_of_device(0x2508);gap_set_bondable_mode(1);gap_ssp_set_io_capability(SSP_IO_CAPABILITY_NO_INPUT_NO_OUTPUT);gap_set_security_level(LEVEL_2);
    gap_set_default_link_policy_settings(LM_LINK_POLICY_ENABLE_ROLE_SWITCH);gap_set_allow_role_switch(true);gap_discoverable_control(1);gap_connectable_control(1);
    l2cap_init();sdp_init();
    hid_sdp_record_t params={8,0,1,1,1,true,false,24,24,3200,pro1_descriptor,sizeof(pro1_descriptor),"Pro Controller"};
    hid_create_sdp_record(hid_record,0x10002,&params);sdp_register_service(hid_record);
    device_id_create_sdp_record(did_record,0x10003,DEVICE_ID_VENDOR_ID_SOURCE_USB,0x057e,0x2009,0x0300);sdp_register_service(did_record);
    hid_device_init(false,sizeof(pro1_descriptor),pro1_descriptor);hid_device_register_packet_handler(events);hid_device_register_report_request_callback(get_report);hid_device_register_set_report_callback(set_report);hid_device_register_report_data_callback(report_data);hid_device_accept_truncated_hid_reports(true);
    event_registration.callback=events;hci_add_event_handler(&event_registration);
    btstack_run_loop_set_timer_handler(&timer,watchdog);btstack_run_loop_set_timer(&timer,50);btstack_run_loop_add_timer(&timer);
    puts("{\"event\":\"PRO1_RADIO_START\",\"input\":\"physical_usb_only\",\"driver_changes\":0,\"pairing_keys_logged\":false,\"receiver_rate_verified\":false}");
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
