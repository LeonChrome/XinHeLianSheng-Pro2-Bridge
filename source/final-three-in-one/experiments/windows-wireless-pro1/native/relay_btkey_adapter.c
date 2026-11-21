/* Windows adaptation of the pinned btkeyLib engine. See third_party/btkeylib/ADAPTATION.md. */
#include <Windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include "btstack.h"
#include "relay_btkey_adapter.h"

static relay_btkey_callbacks_t adapter_callbacks;
static relay_btkey_stats_t adapter_stats;
static btstack_timer_source_t adapter_timer;
static int adapter_timer_armed, adapter_can_pending, adapter_inside_can, adapter_live;
static int adapter_testing;
static uint32_t adapter_test_time;
static uint8_t adapter_snapshot[49];
static int adapter_have_snapshot, adapter_tag_valid;
static uint32_t adapter_spi_address;
static unsigned adapter_spi_length;
static int adapter_spi_requested, adapter_nack_pending;
static uint8_t adapter_nack_subcommand;
static uint16_t adapter_cid;
static uint32_t adapter_last_send_ms, adapter_timer_due;
static uint8_t adapter_last_simple[11];
static int adapter_sent_simple;
/* The first Switch command arrived 1.25–1.56 s after HID opened on this setup.
 * Repeat the initial state briefly while waiting for a command; gameplay simple
 * reports remain change-driven. A repeat is never a fresh physical submission. */
enum { STARTUP_SIMPLE_PERIOD_MS=500, STARTUP_SIMPLE_REPEAT_LIMIT=6 };
static unsigned startup_repeats_left;
/* Same maximum body as the outer transport (362 bytes including report ID).
 * The upstream state machine holds one reply. Keep later commands intact
 * until that reply has actually been submitted on the interrupt channel. */
enum { HOST_COMMAND_CAPACITY=32, HOST_COMMAND_BODY_MAX=361 };
typedef struct { uint16_t id,length; uint8_t body[HOST_COMMAND_BODY_MAX]; } host_command_t;
static host_command_t host_commands[HOST_COMMAND_CAPACITY];
static unsigned command_head, command_count;
static int command_loaded;
static void reset_commands(void) {
    SecureZeroMemory(host_commands,sizeof(host_commands));
    command_head=command_count=0;
    command_loaded=0;
}
static int adapter_sent_this_dispatch, adapter_dispatch_status;
/* Switch can actively close HID on leaving Change Grip/Order. Match the
 * reduced-rate transition used by pcble2gamepad's real-console Classic path. */
enum btkey_phase { BTKEY_WAITING_HOST, BTKEY_PAIRING_MENU, BTKEY_LEAVING_MENU, BTKEY_RUNNING };
static enum btkey_phase adapter_phase;
static uint32_t adapter_transition_at;
static int adapter_link_in_sniff;
static uint32_t adapter_last_timer_tick, adapter_timer_remainder;
static uint8_t adapter_wire_timer;
enum {
    PRO1_INPUT_SIMPLE=0x3f, PRO1_INPUT_FULL=0x30, PRO1_INPUT_NFC=0x31,
    PRO1_SUBCOMMAND_REPLY=0x21, PRO1_SUBCOMMAND_BYTES=50,
    PRO1_SPI_DATA_OFFSET=21, PRO1_SPI_MAX_READ=PRO1_SUBCOMMAND_BYTES-PRO1_SPI_DATA_OFFSET
};
static uint8_t adapter_input_mode, adapter_requested_mode;
static int full_selected_this_link,delivery_state;

int relay_btkey_delivery_state(void) { return delivery_state; }

static uint32_t adapter_now(void) {
    return adapter_testing ? adapter_test_time : btstack_run_loop_get_time_ms();
}
static void adapter_request(uint16_t cid);
static int adapter_send(uint16_t cid,const uint8_t *message,uint16_t length);
static void adapter_ignore_registration(const void *value) { (void)value; }
static void adapter_ignore_sleep(DWORD ms) { (void)ms; }
static int adapter_ignore_power(int mode) { (void)mode; return 0; }
static void adapter_keep_peer_confirmation(int enabled) { (void)enabled; }
static void adapter_hid_init(bool boot_protocol,uint16_t length,const uint8_t *descriptor) {
    (void)boot_protocol;
    /* Match the HID SDP Boot Device attribute and use only report protocol. */
    hid_device_init(false,length,descriptor);
}

/* Keep upstream byte-for-byte. Root owns packet dispatch and radio power.
 * Only its platform bindings are intercepted here. The legacy blocking Sleep
 * becomes a timer in adapter_request; no busy loop runs on BTstack's thread. */
#define hid_device_send_interrupt_message adapter_send
#define hid_device_request_can_send_now_event adapter_request
#define hid_device_init adapter_hid_init
#define hid_device_register_packet_handler adapter_ignore_registration
#define hid_device_register_report_data_callback adapter_ignore_registration
#define hci_add_event_handler adapter_ignore_registration
#define hci_register_sco_packet_handler adapter_ignore_registration
#define sm_init() ((void)0)
#define hci_power_control adapter_ignore_power
#define gap_ssp_set_auto_accept adapter_keep_peer_confirmation
#define Sleep adapter_ignore_sleep
#define btstack_main relay_btkey_upstream_main
#define gamepad_paired relay_btkey_upstream_paired
#define send_button relay_btkey_upstream_button
#define send_stick_r relay_btkey_upstream_stick_r
#define send_stick_l relay_btkey_upstream_stick_l
#define send_gyro relay_btkey_upstream_gyro
#define send_accel relay_btkey_upstream_accel
#define send_padcolor relay_btkey_upstream_color
#define send_amiibo relay_btkey_upstream_amiibo_file
#define get_rumble relay_btkey_upstream_rumble
#define rumble_register relay_btkey_upstream_rumble_register
#undef BTSTACK_FILE__
#include "third_party/btkeylib/btkeyLib.c"
#undef hid_device_send_interrupt_message
#undef hid_device_request_can_send_now_event
#undef hid_device_init
#undef hid_device_register_packet_handler
#undef hid_device_register_report_data_callback
#undef hci_add_event_handler
#undef hci_register_sco_packet_handler
#undef sm_init
#undef hci_power_control
#undef gap_ssp_set_auto_accept
#undef Sleep
#undef btstack_main
#undef gamepad_paired
#undef send_button
#undef send_stick_r
#undef send_stick_l
#undef send_gyro
#undef send_accel
#undef send_padcolor
#undef send_amiibo
#undef get_rumble
#undef rumble_register

static void adapter_cancel_timer(void) {
    if (adapter_timer_armed && !adapter_testing) btstack_run_loop_remove_timer(&adapter_timer);
    adapter_timer_armed=0;
}
static uint32_t adapter_report_period(uint32_t stamp) {
    if (adapter_phase==BTKEY_LEAVING_MENU && !adapter_link_in_sniff &&
        stamp-adapter_transition_at>=1000u) {
        adapter_phase=BTKEY_RUNNING;
        if (!adapter_testing) {
            printf("{\"event\":\"PRO1_CADENCE\",\"phase\":\"normal\",\"normal_cap_hz\":%.3f}\n",
                adapter_stats.effective_hz);
        }
    }
    double cap=adapter_stats.effective_hz;
    if (adapter_phase==BTKEY_WAITING_HOST) cap=fmin(cap,10);
    else if (adapter_phase!=BTKEY_RUNNING) cap=fmin(cap,15);
    return (uint32_t)ceil(1000.0/cap);
}
static void adapter_begin_transition(const char *reason) {
    if (!handshake_done || adapter_phase==BTKEY_RUNNING || adapter_phase==BTKEY_WAITING_HOST) return;
    adapter_phase=BTKEY_LEAVING_MENU;
    adapter_transition_at=adapter_now();
    if (!adapter_testing) {
        printf("{\"event\":\"PRO1_CADENCE\",\"phase\":\"leaving_pairing\",\"reason\":\"%s\","
               "\"hold_ms\":1000,\"cap_hz\":15,\"sniff\":%s}\n",reason,
               adapter_link_in_sniff?"true":"false");
    }
}
void relay_btkey_link_mode(uint8_t mode) {
    if (!adapter_live || !adapter_cid) return;
    int exited_sniff=adapter_link_in_sniff && mode==0;
    adapter_link_in_sniff=mode==2;
    /* The console can also leave via another controller. Its Active event
     * restarts the protection window even if our own A/B/HOME was sent first. */
    if (exited_sniff) adapter_begin_transition("sniff_to_active");
}
static void adapter_timer_handler(btstack_timer_source_t *timer) {
    (void)timer;
    adapter_timer_armed=0;
    if (!adapter_live || !adapter_cid || adapter_can_pending) return;
    adapter_can_pending=1;
    adapter_stats.can_send_requests++;
    if (adapter_callbacks.request_can_send)
        adapter_callbacks.request_can_send(adapter_cid,adapter_callbacks.context);
}
static void adapter_arm_next(void) {
    if (!adapter_live || !adapter_cid || adapter_can_pending) return;
    uint32_t stamp=adapter_now();
    uint32_t period=adapter_report_period(stamp);
    uint32_t elapsed=stamp-adapter_last_send_ms;
    uint32_t delay=elapsed>=period ? 1 : period-elapsed;
    if (command_count && !command_loaded) delay=1;
    if (!command_count && pairing_state==STATE_INPUT_REPORT && !adapter_nack_pending &&
        adapter_input_mode==PRO1_INPUT_SIMPLE && adapter_sent_simple) {
        uint8_t body[sizeof(adapter_last_simple)];
        relay_btkey_build_simple_input(body);
        if (!memcmp(body,adapter_last_simple,sizeof(body))) {
            if (!startup_repeats_left) { adapter_cancel_timer();return; }
            delay=elapsed>=STARTUP_SIMPLE_PERIOD_MS?1:STARTUP_SIMPLE_PERIOD_MS-elapsed;
        }
    }
    if (adapter_timer_armed && (adapter_timer_due==stamp+delay || (int32_t)(stamp-adapter_timer_due)>=0)) return;
    adapter_cancel_timer();
    adapter_timer_due=stamp+delay;
    adapter_timer_armed=1;
    if (adapter_testing) return;
    btstack_run_loop_set_timer_handler(&adapter_timer,adapter_timer_handler);
    btstack_run_loop_set_timer(&adapter_timer,delay);
    btstack_run_loop_add_timer(&adapter_timer);
}
static void adapter_request(uint16_t request_cid) {
    if (!adapter_live || !request_cid || request_cid!=adapter_cid) return;
    if (adapter_inside_can) return;
    if (adapter_can_pending) return;
    if (!command_count && pairing_state==STATE_INPUT_REPORT && !adapter_nack_pending && adapter_timer_armed) return;
    /* A pending host command takes priority over the regular report timer. */
    adapter_cancel_timer();
    adapter_can_pending=1;
    adapter_stats.can_send_requests++;
    if (adapter_callbacks.request_can_send)
        adapter_callbacks.request_can_send(request_cid,adapter_callbacks.context);
}
static void adapter_pack_stick(uint8_t *out,unsigned x,unsigned y) {
    out[0]=(uint8_t)x;
    out[1]=(uint8_t)(((x>>8)&15)|((y&15)<<4));
    out[2]=(uint8_t)(y>>4);
}
static uint8_t adapter_spi_byte(uint32_t address,int *patched) {
    static const uint8_t parameters80[]={0x5e,0x01,0x00,0x00,0xf1,0x0f,0x19,0xd0,0x4c,0xae,0x40,0xe1,0,0,0,0,0,0,0xff,0xff,0xff,0xff,0xff,0xff};
    static const uint8_t parameters98[]={0x19,0xd0,0x4c,0xae,0x40,0xe1,0,0,0,0,0,0,0xff,0xff,0xff,0xff,0xff,0xff};
    if (address>=0x6020 && address<0x6038) {
        *patched=1;
        unsigned offset=address-0x6020, group=offset/6;
        uint16_t value=group==1 ? 16384 : group==3 ? 13371 : 0;
        return (uint8_t)(value >> ((offset&1)*8));
    }
    if (address>=0x603d && address<0x604f) {
        *patched=1;
        unsigned offset=address-0x603d, right=offset>=9;
        offset%=9;
        unsigned center_triplet=right?0:1, value=offset/3==center_triplet?2048:2047;
        uint8_t pair[3];adapter_pack_stick(pair,value,value);
        return pair[offset%3];
    }
    if (address>=0x6050 && address<0x605c) {
        *patched=1;
        return reply1050[SPI_COLOR_OFFSET+address-0x6050];
    }
    if (address>=0x6080 && address<0x6080+sizeof(parameters80)) {
        *patched=1;
        return parameters80[address-0x6080];
    }
    if (address>=0x6098 && address<0x6098+sizeof(parameters98)) {
        *patched=1;
        return parameters98[address-0x6098];
    }
    return 0xff;
}
static void adapter_apply_snapshot(uint8_t *message,uint16_t length) {
    if (length<14) return;
    /* The receiver sees a Bluetooth controller. Power from the source PC's
     * USB cable does not mean this controller is USB-powered by the Switch. */
    message[3]=adapter_have_snapshot ? adapter_snapshot[2]&0xe0 : 0x80;
    if (!adapter_have_snapshot) return;
    memcpy(message+4,adapter_snapshot+3,9);
    if ((message[1]==0x30 || message[1]==0x31) && length>=50)
        memcpy(message+14,adapter_snapshot+13,36);
    adapter_stats.overwritten_snapshot_reports++;
}
static uint16_t adapter_simple_axis(unsigned value,int invert) {
    unsigned full=value<=2048 ? value*16u : 32768u+(value-2048u)*32767u/2047u;
    if (!invert) return (uint16_t)full;
    if (!full) return UINT16_MAX;
    if (full==UINT16_MAX) return 0;
    return (uint16_t)(65536u-full);
}
void relay_btkey_build_simple_input(uint8_t body[11]) {
    memset(body,0,11);
    body[2]=8;
    for (int axis=0;axis<4;axis++) little_endian_store_16(body,3+2*axis,32768);
    if (!adapter_have_snapshot) return;

    /* Nintendo full and simple modes use different bit orders. The hat
     * combines D-pad directions and cancels opposite presses. */
    unsigned right=adapter_snapshot[3], shared=adapter_snapshot[4], left=adapter_snapshot[5];
    body[0]=(uint8_t)(((right&3)<<2)|((right&12)>>2)|
        ((left&0x40)>>2)|((right&0x40)>>1)|((left&0x80)>>1)|(right&0x80));
    body[1]=(uint8_t)((shared&0x33)|((shared&4)<<1)|((shared&8)>>1));
    static const uint8_t hat[3][3]={{5,4,3},{6,8,2},{7,0,1}};
    int vertical=1+((left&2)!=0)-((left&1)!=0);
    int horizontal=1+((left&4)!=0)-((left&8)!=0);
    body[2]=hat[vertical][horizontal];
    for (int stick=0;stick<2;stick++) {
        const uint8_t *packed=adapter_snapshot+6+3*stick;
        unsigned x=packed[0]|((packed[1]&15u)<<8);
        unsigned y=(packed[1]>>4)|((unsigned)packed[2]<<4);
        little_endian_store_16(body,3+4*stick,adapter_simple_axis(x,0));
        little_endian_store_16(body,5+4*stick,adapter_simple_axis(y,1));
    }
}
static int adapter_is_latched_reply(pairing_state_t state) {
    return (state>=STATE_SUBCOMMAND_REPLY_FIRST && state<=STATE_SUBCOMMAND_REPLY_LAST) ||
        state==STATE_PAIRING_INFO_1 || state==STATE_PAIRING_INFO_2 || state==STATE_PAIRING_INFO_3;
}
static int adapter_send(uint16_t send_cid,const uint8_t *message,uint16_t length) {
    if (!adapter_live || send_cid!=adapter_cid || length>363 || length<2 || message[0]!=0xa1)
        return ERROR_CODE_COMMAND_DISALLOWED;
    /* btkeyLib zero-pads its canned 0x21 replies to 64. The registered HID
     * descriptor declares 48 body bytes after the report ID, plus A1 on air. */
    if (message[1]==PRO1_SUBCOMMAND_REPLY) {
        if (length<PRO1_SUBCOMMAND_BYTES) return ERROR_CODE_INVALID_HCI_COMMAND_PARAMETERS;
        length=PRO1_SUBCOMMAND_BYTES;
    }
    uint8_t output[363];memcpy(output,message,length);
    int startup_repeat=0;
    if (output[1]==PRO1_INPUT_FULL && adapter_input_mode==PRO1_INPUT_SIMPLE) {
        /* A fresh pairing starts in simple mode until the host selects full input. */
        output[1]=PRO1_INPUT_SIMPLE;
        relay_btkey_build_simple_input(output+2);
        length=13;
        if (adapter_sent_simple && !memcmp(output+2,adapter_last_simple,sizeof(adapter_last_simple))) {
            if (!startup_repeats_left || adapter_now()-adapter_last_send_ms<STARTUP_SIMPLE_PERIOD_MS)
                return ERROR_CODE_SUCCESS;
            startup_repeat=1;
        }
    } else adapter_apply_snapshot(output,length);
    if (length>=14 && (output[1]==PRO1_SUBCOMMAND_REPLY || output[1]==0x30 || output[1]==0x31)) {
        uint32_t stamp=adapter_now();
        uint64_t elapsed=(uint32_t)(stamp-adapter_last_timer_tick)+(uint64_t)adapter_timer_remainder;
        adapter_wire_timer+=(uint8_t)(elapsed/5u);
        adapter_timer_remainder=(uint32_t)(elapsed%5u);
        adapter_last_timer_tick=stamp;
        output[2]=adapter_wire_timer;
    }
    int spi_reply=output[1]==PRO1_SUBCOMMAND_REPLY && output[14]==0x90 && output[15]==0x10 && adapter_spi_requested;
    if (spi_reply) {
        little_endian_store_32(output,16,adapter_spi_address);
        output[20]=(uint8_t)adapter_spi_length;
        memset(output+PRO1_SPI_DATA_OFFSET,0,PRO1_SPI_MAX_READ);
        int patched=0;
        for (unsigned i=0;i<adapter_spi_length;i++)
            output[PRO1_SPI_DATA_OFFSET+i]=adapter_spi_byte(adapter_spi_address+i,&patched);
        if (!adapter_testing) {
            printf("{\"event\":\"PRO1_SPI_READ\",\"address\":%lu,\"length\":%u,\"patched\":%s}\n",
                (unsigned long)adapter_spi_address,adapter_spi_length,patched?"true":"false");
        }
    }
    /* A missing/cleared card must never expose canned UID or tag data. */
    if (output[1]==0x31 && !adapter_tag_valid && length>=363) {
        const uint8_t *nfc=output+50;
        if ((nfc[0]==0x3a) || (nfc[0]==0x2a && (nfc[7]==9 || nfc[7]==4))) {
            memcpy(output+50,reply110204,NFC_PAYLOAD_SIZE);
            output[362]=crc8ccitt(output+50,NFC_PAYLOAD_SIZE);
            adapter_stats.no_tag_guards++;
        }
    }
    adapter_stats.send_attempts++;
    int status=adapter_callbacks.send ? adapter_callbacks.send(send_cid,output,length,startup_repeat,adapter_callbacks.context) : ERROR_CODE_COMMAND_DISALLOWED;
    adapter_stats.last_send_status=status;
    adapter_dispatch_status=status;
    adapter_sent_this_dispatch=1;
    adapter_last_send_ms=adapter_now();
    if (status) adapter_stats.send_failures++;
    else {
        adapter_stats.send_successes++;
        if (output[1]==PRO1_INPUT_SIMPLE) {
            memcpy(adapter_last_simple,output+2,sizeof(adapter_last_simple));
            adapter_sent_simple=1;
            if (startup_repeat) { startup_repeats_left--;adapter_stats.startup_repeats++; }
        }
        if (output[1]==PRO1_SUBCOMMAND_REPLY && (output[14]&0x80) && output[15]==0x03 && adapter_requested_mode) {
            if (adapter_requested_mode==PRO1_INPUT_SIMPLE && adapter_input_mode!=PRO1_INPUT_SIMPLE) adapter_sent_simple=0;
            adapter_input_mode=adapter_requested_mode;
            adapter_requested_mode=0;
            full_selected_this_link=adapter_input_mode!=PRO1_INPUT_SIMPLE;
            delivery_state=RELAY_PRO1_SIMPLE;
            if (adapter_input_mode!=PRO1_INPUT_NFC) {
                pairing_state=STATE_INPUT_REPORT;
                packet_amiibo_flag=false;
            }
            if (!adapter_testing) {
                printf("{\"event\":\"PRO1_REPORT_MODE\",\"id\":%u,\"source\":\"host_set_mode_ack_sent\"}\n",
                    adapter_input_mode);
            }
        }
        if (full_selected_this_link && (output[1]==PRO1_INPUT_FULL || output[1]==PRO1_INPUT_NFC))
            delivery_state=RELAY_PRO1_CONFIRMED;
        if (output[1]==PRO1_SUBCOMMAND_REPLY) {
            adapter_stats.subcommand_replies++;
            if (adapter_is_latched_reply(pairing_state)) pairing_state=STATE_INPUT_REPORT;
            if (spi_reply) adapter_spi_requested=0;
        }
        else if (output[1]==0x31) adapter_stats.nfc_reports++;
        else if (output[1]==0x30) adapter_stats.fresh_reports++;
        if (adapter_phase==BTKEY_PAIRING_MENU && output[1]==0x30 && length>=14 &&
            ((output[4]&0x0c) || (output[5]&0x10))) {
            adapter_begin_transition("sent_a_b_home");
        }
    }
    return status;
}
static void adapter_guard_no_tag(void) {
    if (adapter_tag_valid) return;
    switch (pairing_state) {
        case STATE_NFC_TAG_DETECTED:case STATE_NFC_TAG_DATA_1:case STATE_NFC_TAG_DATA_2:
        case STATE_NFC_TAG_DATA_3:case STATE_NFC_TAG_READ:case STATE_NFC_TAG_PENDING:
            pairing_state=STATE_NFC_TAG_POLLING;packet_amiibo_flag=false;
            adapter_stats.no_tag_guards++;break;
        default:break;
    }
}
void relay_btkey_clear_tag(void) {
    adapter_tag_valid=0;packet_amiibo_flag=false;
    memset(amiibo_block1,0,sizeof(amiibo_block1));
    memset(amiibo_block2,0,sizeof(amiibo_block2));
    memset(amiibo_block3,0,sizeof(amiibo_block3));
    memset(reply110201+NFC_TAG_DETECTED_UID_OFFSET,0,AMIIBO_UID_SIZE);
    crc_block1=crc_block2=crc_block3=crc_tag_detected=0;
    adapter_guard_no_tag();
}
int relay_btkey_set_tag540(const uint8_t *tag,uint16_t length) {
    if (!tag || length!=540 || tag[0]!=4 || tag[3]!=(uint8_t)(0x88^tag[0]^tag[1]^tag[2])
        || tag[8]!=(uint8_t)(tag[4]^tag[5]^tag[6]^tag[7]) || tag[12]!=0xe1 || tag[14]!=0x3e) {
        adapter_stats.invalid_tags++;return 0;
    }
    uint8_t uid[7];amiibo_extract_uid(tag,uid);
    memcpy(reply110201+NFC_TAG_DETECTED_UID_OFFSET,uid,7);
    amiibo_build_blocks(tag,uid);
    /* The 540-byte snapshot has no originality signature. Do not reuse
     * the unrelated sample card's 32-byte signature from upstream. */
    memset(amiibo_block1+sizeof(nfc_packet1)+AMIIBO_UID_SIZE+4,0,32);
    crc_block1=crc8ccitt(amiibo_block1,sizeof(amiibo_block1));
    crc_block2=crc8ccitt(amiibo_block2,sizeof(amiibo_block2));
    crc_block3=crc8ccitt(amiibo_block3,sizeof(amiibo_block3));
    crc_tag_detected=crc8ccitt(reply110201,sizeof(reply110201));
    adapter_tag_valid=1;return 1;
}
void relay_btkey_set_local_address(const uint8_t address[6]) {
    if (!address) return;
    memcpy(reply01_1+REPLY01_BD_ADDR_OFFSET,address,6);
    memcpy(reply02+REPLY02_BD_ADDR_OFFSET,address,6);
}
int relay_btkey_observe_snapshot(const uint8_t *report,uint16_t length) {
    if (!report || length!=49 || report[0]!=0x30) { adapter_stats.invalid_snapshots++;return 0; }
    memcpy(adapter_snapshot,report,49);adapter_have_snapshot=1;adapter_stats.snapshots++;
    if (adapter_input_mode==PRO1_INPUT_SIMPLE && pairing_state==STATE_INPUT_REPORT &&
        !command_count && !adapter_nack_pending && !adapter_inside_can) adapter_arm_next();
    return 1;
}
int relay_btkey_init(const relay_btkey_callbacks_t *callbacks,double requested_hz) {
    if (!callbacks || !callbacks->send || !callbacks->request_can_send || !isfinite(requested_hz) || requested_hz<=0) return 0;
    adapter_cancel_timer();adapter_callbacks=*callbacks;
    reset_commands();
    memset(&adapter_stats,0,sizeof(adapter_stats));
    adapter_stats.requested_hz=requested_hz;
    adapter_stats.effective_hz=requested_hz>60 ? 60 : requested_hz;
    adapter_can_pending=adapter_inside_can=adapter_live=adapter_have_snapshot=adapter_cid=0;
    adapter_spi_requested=adapter_nack_pending=adapter_sent_simple=0;
    adapter_last_send_ms=0;adapter_sent_this_dispatch=0;adapter_dispatch_status=0;
    adapter_phase=BTKEY_WAITING_HOST;adapter_link_in_sniff=0;
    hid_cid=0;pairing_state=STATE_INPUT_REPORT;handshake_done=false;report_counter=input_report_timer=0;
    button_flg=0;stick_l_flg=stick_r_flg=0x800800;
    accel_x_flg=accel_y_flg=accel_z_flg=gyro1_flg=gyro2_flg=gyro3_flg=0;
    relay_btkey_clear_tag();adapter_live=1;
    if (!adapter_testing) {
        static const char *args[]={"relay-btkeylib",NULL};
        relay_btkey_upstream_main(1,args);
        printf("{\"event\":\"BTKEYLIB_ENGINE\",\"source_commit\":\"%s\",\"requested_hz\":%.3f,\"configured_cap_hz\":%.3f,\"cadence_scope\":\"native_engine_upper_bound_not_receiver_measurement\",\"power_owned_by_outer_backend\":true,\"nfc_tag_loaded\":false}\n",RELAY_BTKEY_SOURCE_COMMIT,requested_hz,adapter_stats.effective_hz);
    }
    return 1;
}
static void adapter_nack(uint8_t command) {
    adapter_nack_subcommand=command;
    adapter_nack_pending=1;
    adapter_request(adapter_cid);
}
void relay_btkey_handle_report(uint16_t report_cid,hid_report_type_t type,uint16_t report_id,int length,uint8_t *body) {
    if (!adapter_live || !adapter_cid || report_cid!=adapter_cid || type!=HID_REPORT_TYPE_OUTPUT || !body || length<=0) return;
    if (adapter_phase==BTKEY_WAITING_HOST) {
        adapter_phase=BTKEY_PAIRING_MENU;
        if (!adapter_testing) puts("{\"event\":\"PRO1_CADENCE\",\"phase\":\"pairing_menu\",\"cap_hz\":15}");
    }
    // Report 0x10 carries rumble only. Pad feedback is sent via the outer
    // backend; requesting CAN_SEND here could overrun the normal cadence.
    if (report_id==0x10) {
        if (!command_loaded) report_counter=body[OUT_REPORT_TIMER];
        return;
    }
    if ((report_id!=OUT_REPORT_ID_SUBCOMMAND && report_id!=0x11) ||
        length<=OUT_REPORT_SUBCMD || length>HOST_COMMAND_BODY_MAX) return;
    startup_repeats_left=0;
    if (command_count==HOST_COMMAND_CAPACITY) {
        adapter_stats.command_overflows++;
        if (!adapter_testing && (adapter_stats.command_overflows==1 || adapter_stats.command_overflows%64==0))
            printf("{\"event\":\"PRO1_COMMAND_QUEUE_FULL\",\"capacity\":%u,\"dropped_total\":%llu}\n",
                (unsigned)HOST_COMMAND_CAPACITY,(unsigned long long)adapter_stats.command_overflows);
        return;
    }
    host_command_t *entry=&host_commands[(command_head+command_count)%HOST_COMMAND_CAPACITY];
    entry->id=report_id;entry->length=(uint16_t)length;memcpy(entry->body,body,(size_t)length);
    command_count++;
    if (command_count>adapter_stats.command_queue_peak) adapter_stats.command_queue_peak=command_count;
    adapter_request(report_cid);
}
static void apply_head_command(void) {
    host_command_t *entry=&host_commands[command_head];
    uint8_t *body=entry->body;
    uint16_t report_cid=adapter_cid, report_id=entry->id;
    int length=entry->length;
    command_loaded=1;
    if (report_id==OUT_REPORT_ID_SUBCOMMAND && length>OUT_REPORT_SUBCMD) {
        uint8_t command=body[OUT_REPORT_SUBCMD];
        uint8_t arg=report_byte(body,length,OUT_REPORT_ARG(0));
        adapter_spi_requested=adapter_nack_pending=adapter_requested_mode=0;
        if (adapter_is_latched_reply(pairing_state)) pairing_state=STATE_INPUT_REPORT;
        report_counter=body[OUT_REPORT_TIMER];
        switch (command) {
            case 0x01:
                if (arg<1 || arg>3) { adapter_nack(command);return; }
                break;
            case 0x02:
                /* The 0x01 Device Info command has no argument. Preserve 0x11
                 * NFC handling even when a host pads its Device Info request. */
                pairing_state=STATE_REPLY_02;
                adapter_request(report_cid);
                return;
            case 0x03:
                if (arg!=PRO1_INPUT_SIMPLE && arg!=PRO1_INPUT_FULL && arg!=PRO1_INPUT_NFC) {
                    adapter_nack(command);return;
                }
                adapter_requested_mode=arg;
                break;
            case 0x10:
                if (length<15) { adapter_nack(command);return; }
                adapter_spi_address=little_endian_read_32(body,10);
                adapter_spi_length=body[14];
                if (adapter_spi_length>PRO1_SPI_MAX_READ ||
                    (adapter_spi_length && UINT32_MAX-adapter_spi_address<adapter_spi_length-1)) {
                    if (!adapter_testing) {
                        printf("{\"event\":\"PRO1_SPI_READ\",\"address\":%lu,\"length\":%u,\"patched\":false,\"valid\":false}\n",
                            (unsigned long)adapter_spi_address,adapter_spi_length);
                    }
                    adapter_nack(command);return;
                }
                /* The reply template is patched for every address and count,
                 * including erased regions outside the public calibration. */
                adapter_spi_requested=1;
                pairing_state=STATE_REPLY_1010;
                adapter_request(report_cid);
                return;
            case 0x21:
                if (arg!=0x21) { adapter_nack(command);return; }
                break;
            case 0x22:
                if (length!=48 || arg!=1) { adapter_nack(command);return; }
                break;
            case 0x04:case 0x08:case 0x30:case 0x40:case 0x48:
                break;
            default:
                adapter_nack(command);return;
        }
    }
    hid_report_data_callback(report_cid,HID_REPORT_TYPE_OUTPUT,report_id,length,body);
    adapter_guard_no_tag();
}
void relay_btkey_packet_handler(uint8_t type,uint16_t channel,uint8_t *packet,uint16_t size) {
    if (!adapter_live || type!=HCI_EVENT_PACKET || !packet || size<3 || packet[0]!=HCI_EVENT_HID_META) return;
    uint8_t sub=hci_event_hid_meta_get_subevent_code(packet);
    if (sub==HID_SUBEVENT_CONNECTION_OPENED) {
        if(size<14 || hid_subevent_connection_opened_get_status(packet))return;
        adapter_cancel_timer();adapter_can_pending=0;reset_commands();
        adapter_cid=hid_subevent_connection_opened_get_hid_cid(packet);
        adapter_stats.connected=1;full_selected_this_link=0;delivery_state=RELAY_PRO1_SIMPLE;
        adapter_input_mode=PRO1_INPUT_SIMPLE;adapter_phase=BTKEY_WAITING_HOST;
        startup_repeats_left=STARTUP_SIMPLE_REPEAT_LIMIT;
        adapter_link_in_sniff=adapter_requested_mode=0;
        adapter_spi_requested=adapter_nack_pending=adapter_sent_simple=0;
        adapter_last_timer_tick=adapter_now();adapter_timer_remainder=0;adapter_wire_timer=0;
        pairing_state=STATE_INPUT_REPORT;packet_amiibo_flag=false;
        if(!adapter_testing)puts("{\"event\":\"PRO1_REPORT_MODE\",\"id\":63,\"source\":\"new_hid_session\"}");
        nintendo_packet_handler(type,channel,packet,size);
        return;
    }
    if (sub==HID_SUBEVENT_CONNECTION_CLOSED) {
        if (size<5 || !adapter_cid || hid_subevent_connection_closed_get_hid_cid(packet)!=adapter_cid) return;
        adapter_cancel_timer();adapter_can_pending=0;adapter_stats.connected=0;startup_repeats_left=0;
        full_selected_this_link=delivery_state=0;
        reset_commands();
        nintendo_packet_handler(type,channel,packet,size);adapter_cid=0;return;
    }
    if (sub!=HID_SUBEVENT_CAN_SEND_NOW || size<5 || hid_subevent_can_send_now_get_hid_cid(packet)!=adapter_cid) return;
    adapter_cancel_timer();adapter_can_pending=0;adapter_inside_can=1;
    if (command_count && !command_loaded) apply_head_command();
    adapter_guard_no_tag();
    pairing_state_t saved_state=pairing_state;
    uint8_t saved_input_timer=input_report_timer,saved_counter=report_counter;
    bool saved_handshake=handshake_done;
    adapter_sent_this_dispatch=0;adapter_dispatch_status=0;
    if (adapter_nack_pending) {
        uint8_t reply[PRO1_SUBCOMMAND_BYTES];
        memcpy(reply,reply00,sizeof(reply));
        reply[1]=PRO1_SUBCOMMAND_REPLY;
        reply[15]=adapter_nack_subcommand;
        if (!adapter_send(adapter_cid,reply,sizeof(reply))) adapter_nack_pending=0;
    } else nintendo_packet_handler(type,channel,packet,size);
    adapter_inside_can=0;
    if (adapter_sent_this_dispatch && adapter_dispatch_status) {
        pairing_state=saved_state;input_report_timer=saved_input_timer;
        report_counter=saved_counter;handshake_done=saved_handshake;
    } else if (command_loaded && (adapter_sent_this_dispatch || host_commands[command_head].id==0x11)) {
        SecureZeroMemory(&host_commands[command_head],sizeof(host_commands[command_head]));
        command_head=(command_head+1)%HOST_COMMAND_CAPACITY;command_count--;
        command_loaded=0;
    }
    adapter_arm_next();
}
void relay_btkey_get_stats(relay_btkey_stats_t *out) {
    if (!out) return;
    adapter_stats.handshake_done=handshake_done;adapter_stats.tag_loaded=adapter_tag_valid;
    *out=adapter_stats;
}
void relay_btkey_shutdown(void) {
    reset_commands();
    adapter_live=0;adapter_cancel_timer();adapter_can_pending=0;adapter_cid=hid_cid=0;
    adapter_stats.connected=0;relay_btkey_clear_tag();
}

/* The executable's --btkey-self-test dispatch invokes this without opening HCI.
 * Tests call the actual included upstream state machine and patched send path. */
typedef struct { uint8_t packet[363];uint16_t bytes;unsigned requests;int status; } adapter_test_capture_t;
static int adapter_test_send(uint16_t test_cid,const uint8_t *message,uint16_t bytes,int startup_repeat,void *context) {
    (void)test_cid;adapter_test_capture_t *c=context;memcpy(c->packet,message,bytes);c->bytes=bytes;return c->status;
}
static void adapter_test_request(uint16_t test_cid,void *context) {
    (void)test_cid;((adapter_test_capture_t *)context)->requests++;
}
static void adapter_test_pump(void) {
    uint8_t event[]={HCI_EVENT_HID_META,3,HID_SUBEVENT_CAN_SEND_NOW,1,0};
    adapter_test_time+=20;relay_btkey_packet_handler(HCI_EVENT_PACKET,0,event,sizeof(event));
}
static void adapter_test_output(uint8_t command,const uint8_t *args,unsigned length) {
    uint8_t output[48]={0};output[0]=0x42;output[9]=command;
    if (length) memcpy(output+10,args,length);
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,1,(int)(10+length),output);
    adapter_test_pump();
}
static void adapter_hex(const uint8_t *bytes,unsigned length) {for(unsigned i=0;i<length;i++)printf("%02X",bytes[i]);}
int relay_btkey_self_test(void) {
    int failed=0,cases=0;
    adapter_testing=1;adapter_test_time=1000;
    adapter_test_capture_t capture={0};
    relay_btkey_callbacks_t callbacks={adapter_test_send,adapter_test_request,&capture};
    int initialized=relay_btkey_init(&callbacks,133);
    uint8_t opened[15]={HCI_EVENT_HID_META,13,HID_SUBEVENT_CONNECTION_OPENED,1,0};
    relay_btkey_packet_handler(HCI_EVENT_PACKET,0,opened,sizeof(opened));
    printf("{\"event\":\"BTKEYLIB_SELF_TEST\",\"schema\":1,\"source_commit\":\"%s\",\"engine_owns_protocol\":true,\"hardware_opened\":false,\"requested_hz\":133,\"effective_hz\":60,\"cases_detail\":[",RELAY_BTKEY_SOURCE_COMMIT);
#define CHECK(name,condition) do { int pass=(condition)!=0;printf("%s{\"name\":\"%s\",\"pass\":%s}",cases?",":"",name,pass?"true":"false");cases++;if(!pass)failed++;} while(0)
    CHECK("requested_rate_capped_to_upstream_60",initialized && adapter_stats.requested_hz==133 && adapter_stats.effective_hz==60);
    CHECK("input_sizes_match_the_registered_hid_descriptor",
        btstack_hid_get_report_size_for_id(0x21,HID_REPORT_TYPE_INPUT,reportMap,sizeof(reportMap))==PRO1_SUBCOMMAND_BYTES-2 &&
        btstack_hid_get_report_size_for_id(0x3f,HID_REPORT_TYPE_INPUT,reportMap,sizeof(reportMap))==sizeof(adapter_last_simple));
    CHECK("before_switch_output_changed_simple_reports_are_limited_to_10_hz",adapter_report_period(adapter_test_time)==100);
    adapter_test_pump();
    CHECK("fresh_hid_session_sends_a1_3f_and_neutral_controls_before_any_host_output",
        capture.bytes==13 && capture.packet[0]==0xa1 && capture.packet[1]==0x3f &&
        capture.packet[2]==0 && capture.packet[3]==0 && capture.packet[4]==8 &&
        little_endian_read_16(capture.packet,5)==32768 && little_endian_read_16(capture.packet,11)==32768);
    uint64_t first_send=adapter_stats.send_successes;
    CHECK("startup_arms_a_half_second_retry",adapter_timer_armed && adapter_timer_due==adapter_last_send_ms+STARTUP_SIMPLE_PERIOD_MS);
    adapter_test_time+=100;
    adapter_test_pump();
    CHECK("early_send_event_does_not_repeat_unchanged_simple_report",
        adapter_stats.send_successes==first_send);
    for (unsigned i=0;i<STARTUP_SIMPLE_REPEAT_LIMIT;i++) {
        adapter_test_time=adapter_last_send_ms+STARTUP_SIMPLE_PERIOD_MS;adapter_test_pump();
    }
    CHECK("startup_retries_are_bounded_and_then_idle",
        adapter_stats.startup_repeats==STARTUP_SIMPLE_REPEAT_LIMIT && !startup_repeats_left && !adapter_timer_armed);
    uint8_t simple_source[49]={0x30};simple_source[3]=0xc6;simple_source[4]=0x2c;simple_source[5]=0xc6;
    simple_source[2]=0x9f;
    adapter_pack_stick(simple_source+6,0,4095);adapter_pack_stick(simple_source+9,4095,0);
    relay_btkey_observe_snapshot(simple_source,sizeof(simple_source));
    CHECK("changed_simple_report_rearms_after_idle",adapter_timer_armed);
    adapter_test_time=adapter_timer_due;adapter_test_pump();
    CHECK("simple_mode_maps_face_buttons_hat_and_inverted_y",
        capture.bytes==13 && capture.packet[2]==0xf9 && capture.packet[3]==0x2c && capture.packet[4]==1 &&
        little_endian_read_16(capture.packet,5)==0 && little_endian_read_16(capture.packet,7)==0 &&
        little_endian_read_16(capture.packet,9)==UINT16_MAX && little_endian_read_16(capture.packet,11)==UINT16_MAX);
    uint8_t address[]={2,3,4,5,6,7};relay_btkey_set_local_address(address);
    uint8_t padding=2;adapter_test_output(2,&padding,1);
    CHECK("device_info_50byte_a1_reply_with_real_mac_and_pro_controller_type",
        capture.bytes==50 && capture.packet[0]==0xa1 && capture.packet[1]==0x21 && capture.packet[3]==0x80 &&
        capture.packet[14]==0x82 && capture.packet[15]==0x02 && capture.packet[16]==3 && capture.packet[17]==0x48 &&
        capture.packet[18]==3 && !memcmp(capture.packet+20,address,6) && capture.packet[27]==2);
    CHECK("ack_uses_the_same_5ms_clock_as_input",capture.packet[2]==(uint8_t)((adapter_test_time-1000)/5));
    CHECK("after_first_host_output_pairing_reports_are_limited_to_15_hz",adapter_report_period(adapter_test_time)==67);
    uint64_t ack_count=adapter_stats.subcommand_replies;
    adapter_test_time+=100;adapter_test_pump();
    CHECK("device_info_ack_once_then_silent_until_input_changes",pairing_state==STATE_INPUT_REPORT &&
        adapter_stats.subcommand_replies==ack_count && !adapter_timer_armed);
    adapter_test_output(0x7f,NULL,0);
    CHECK("unknown_command_gets_one_nack_with_its_own_id",
        capture.bytes==50 && capture.packet[1]==0x21 && capture.packet[14]==0 && capture.packet[15]==0x7f);
    ack_count=adapter_stats.subcommand_replies;
    adapter_test_time+=100;adapter_test_pump();
    CHECK("unknown_command_nack_does_not_override_following_input",
        adapter_stats.subcommand_replies==ack_count && !adapter_timer_armed);
    simple_source[2]=0x5f;relay_btkey_observe_snapshot(simple_source,sizeof(simple_source));
    adapter_test_output(2,NULL,0);
    CHECK("wireless_power_keeps_the_three_source_battery_bits",capture.packet[3]==0x40);
    adapter_have_snapshot=0;adapter_test_output(2,NULL,0);
    CHECK("missing_source_uses_existing_full_battery_default_without_connection_flags",capture.packet[3]==0x80);
    uint8_t full=0x30;
    capture.status=7;
    adapter_test_output(0x03,&full,1);
    CHECK("failed_mode_ack_does_not_switch_to_full_reports",
        adapter_input_mode==PRO1_INPUT_SIMPLE && adapter_requested_mode==PRO1_INPUT_FULL);
    capture.status=0;adapter_test_pump();
    CHECK("full_mode_starts_only_after_successful_03_reply",
        adapter_input_mode==PRO1_INPUT_FULL && !adapter_requested_mode && capture.packet[1]==0x21 && capture.packet[15]==0x03);
    adapter_test_pump();
    CHECK("full_input_is_next_without_waiting_for_player_light_handshake",
        !handshake_done && capture.bytes==50 && capture.packet[1]==0x30);
    const uint8_t commands[][2]={{0x04,0x83},{0x40,0x80},{0x48,0x80},{0x21,0xa0}};
    for (unsigned i=0;i<sizeof(commands)/sizeof(commands[0]);i++) {
        uint8_t arg=commands[i][0]==0x21 ? 0x21 : 1;
        adapter_test_output(commands[i][0],&arg,1);
        CHECK("expected_typed_reply_and_no_latched_ack",capture.bytes==50 && capture.packet[1]==0x21 &&
            capture.packet[14]==commands[i][1] && capture.packet[15]==commands[i][0] && pairing_state==STATE_INPUT_REPORT);
    }
    uint8_t one=1;adapter_test_output(0x30,&one,1);
    CHECK("player_light_ack_completes_upstream_handshake",handshake_done && pairing_state==STATE_INPUT_REPORT);
    relay_btkey_link_mode(2); /* The measured Switch settled into 30 ms Sniff. */
    uint8_t snapshot[49]={0x30};snapshot[2]=0x81;snapshot[3]=8;snapshot[4]=0x10;snapshot[5]=2;
    adapter_pack_stick(snapshot+6,2048,4095);adapter_pack_stick(snapshot+9,0,2048);
    int16_t motion[]={4096,8192,-4096,13371,-6685,0};
    for(int group=0;group<3;group++)for(int axis=0;axis<6;axis++)little_endian_store_16(snapshot,13+12*group+axis*2,(uint16_t)motion[axis]);
    relay_btkey_observe_snapshot(snapshot,sizeof(snapshot));adapter_test_pump();
    uint8_t previous[49];memcpy(previous,capture.packet+1,49);uint8_t old_timer=capture.packet[2];
    uint32_t button_at=adapter_transition_at;
    CHECK("exit_button_holds_pairing_rate_while_sniff_is_active",adapter_phase==BTKEY_LEAVING_MENU &&
        adapter_report_period(button_at+2000u)==67);
    snapshot[3]=4;relay_btkey_observe_snapshot(snapshot,sizeof(snapshot));adapter_test_pump();
    uint8_t latest[49];memcpy(latest,capture.packet+1,49);
    CHECK("latest_snapshot_selected_at_actual_can_send",capture.bytes==50 && capture.packet[4]==4 && !memcmp(capture.packet+14,snapshot+13,36));
    CHECK("input_timer_uses_elapsed_time",capture.packet[2]==(uint8_t)(old_timer+4));
    CHECK("usb_source_power_is_not_advertised_on_the_bluetooth_link",capture.packet[3]==0x80);
    relay_btkey_link_mode(0);
    uint32_t active_at=adapter_transition_at;
    CHECK("switch_active_event_extends_protection_past_the_observed_122ms_close",
        active_at>button_at && adapter_report_period(active_at+122u)==67);
    CHECK("normal_rate_returns_after_the_full_active_transition",
        adapter_report_period(active_at+999u)==67 && adapter_report_period(active_at+1000u)==17);
    uint8_t spi[]={0x3d,0x60,0,0,18};adapter_test_output(0x10,spi,5);
    uint8_t spi603d[18];memcpy(spi603d,capture.packet+21,18);
    CHECK("spi_stick_calibration_matches_normalized_input",spi603d[0]==0xff && spi603d[1]==0xf7 && spi603d[2]==0x7f && spi603d[3]==0 && spi603d[4]==8 && spi603d[5]==0x80 && spi603d[9]==0 && spi603d[10]==8 && spi603d[11]==0x80);
    spi[0]=0x20;spi[4]=24;adapter_test_output(0x10,spi,5);
    uint8_t spi6020[24];memcpy(spi6020,capture.packet+21,24);
    CHECK("spi_imu_calibration_matches_4096g_and_13371_936dps",little_endian_read_16(spi6020,0)==0 && little_endian_read_16(spi6020,6)==16384 && little_endian_read_16(spi6020,12)==0 && little_endian_read_16(spi6020,18)==13371);
    spi[0]=0x28;spi[1]=0x80;spi[4]=24;adapter_test_output(0x10,spi,5);
    CHECK("unknown_user_spi_read_has_correct_address",little_endian_read_32(capture.packet,16)==0x8028 && capture.packet[21]==0xff);
    spi[0]=0x3d;spi[1]=0x60;spi[4]=PRO1_SPI_MAX_READ;
    uint8_t spi_output[48]={0};spi_output[9]=0x10;memcpy(spi_output+10,spi,sizeof(spi));
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,1,sizeof(spi_output),spi_output);
    uint8_t rumble_during_spi[9]={0};
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,0x10,sizeof(rumble_during_spi),rumble_during_spi);
    adapter_test_pump();
    CHECK("max_spi_read_retains_patch_when_concurrent_rumble_arrives",
        capture.bytes==50 && capture.packet[1]==0x21 && capture.packet[14]==0x90 &&
        little_endian_read_32(capture.packet,16)==0x603d && capture.packet[20]==29 && capture.packet[49]==reply1050[SPI_COLOR_OFFSET+9]);
    spi[4]=PRO1_SPI_MAX_READ+1;adapter_test_output(0x10,spi,5);
    CHECK("oversized_spi_read_gets_nack_instead_of_truncated_ack",
        capture.bytes==50 && capture.packet[14]==0 && capture.packet[15]==0x10 && !adapter_spi_requested);
    pairing_state=STATE_NFC_TAG_DATA_1;adapter_test_pump();
    CHECK("no_tag_cannot_send_canned_uid_or_tag_data",capture.bytes==363 && capture.packet[1]==0x31 && capture.packet[50]==0x2a && capture.packet[57]==0 && capture.packet[66]==0 && !adapter_tag_valid && pairing_state==STATE_NFC_TAG_POLLING);
    uint8_t tag[540]={4,1,2,0,3,4,5,6};tag[3]=(uint8_t)(0x88^tag[0]^tag[1]^tag[2]);tag[8]=tag[4]^tag[5]^tag[6]^tag[7];tag[12]=0xe1;tag[13]=0x10;tag[14]=0x3e;
    CHECK("synthetic_ntag215_structure_is_accepted",relay_btkey_set_tag540(tag,sizeof(tag)));
    uint8_t no_signature[32]={0};
    CHECK("unsigned_tag_does_not_use_a_sample_signature",!memcmp(amiibo_block1+sizeof(nfc_packet1)+AMIIBO_UID_SIZE+4,no_signature,sizeof(no_signature)));
    relay_btkey_clear_tag();CHECK("clear_tag_erases_uid_and_blocks",!adapter_tag_valid && amiibo_block1[15]==0 && reply110201[NFC_TAG_DETECTED_UID_OFFSET]==0);
    tag[3]^=1;CHECK("invalid_tag_rejected",!relay_btkey_set_tag540(tag,sizeof(tag)));
    uint8_t closed[]={HCI_EVENT_HID_META,3,HID_SUBEVENT_CONNECTION_CLOSED,1,0};
    relay_btkey_packet_handler(HCI_EVENT_PACKET,0,closed,sizeof(closed));
    CHECK("close_clears_cid_but_keeps_handshake_for_same_peer",!adapter_cid && !hid_cid && !adapter_stats.connected && handshake_done && !adapter_timer_armed);
    relay_btkey_packet_handler(HCI_EVENT_PACKET,0,opened,sizeof(opened));
    adapter_test_pump();
    CHECK("hid_reconnect_starts_in_simple_mode_again",adapter_input_mode==PRO1_INPUT_SIMPLE &&
        capture.bytes==13 && capture.packet[1]==0x3f);
    CHECK("previous_handshake_does_not_qualify_new_link",relay_btkey_delivery_state()==RELAY_PRO1_SIMPLE);
    uint8_t full_mode=0x30;
    adapter_test_output(0x03,&full_mode,1);
    CHECK("mode_ack_alone_does_not_qualify_link",relay_btkey_delivery_state()==RELAY_PRO1_SIMPLE);
    adapter_test_pump();
    CHECK("current_host_request_and_submitted_input_qualify_link",relay_btkey_delivery_state()==RELAY_PRO1_CONFIRMED &&
        capture.packet[1]==0x30);
    uint8_t simple_mode=0x3f;
    adapter_test_output(0x03,&simple_mode,1);
    CHECK("host_can_return_to_simple_input",adapter_input_mode==PRO1_INPUT_SIMPLE &&
        relay_btkey_delivery_state()==RELAY_PRO1_SIMPLE);
    relay_btkey_packet_handler(HCI_EVENT_PACKET,0,closed,sizeof(closed));
    relay_btkey_packet_handler(HCI_EVENT_PACKET,0,opened,sizeof(opened));
    adapter_test_output(0x48,&one,1);
    CHECK("reconnect_information_reply_preserves_handshake",handshake_done && pairing_state==STATE_INPUT_REPORT);
    relay_btkey_link_mode(2);
    relay_btkey_link_mode(0);
    CHECK("host_can_leave_pairing_using_a_different_controller",
        adapter_phase==BTKEY_LEAVING_MENU && adapter_report_period(adapter_test_time+122u)==67);
    unsigned rumble_requests=capture.requests;
    uint8_t rumble[9]={0};
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,0x10,sizeof(rumble),rumble);
    CHECK("rumble_does_not_trigger_extra_input_frames",capture.requests==rumble_requests);
    snapshot[3]^=4;relay_btkey_observe_snapshot(snapshot,sizeof(snapshot));
    capture.status=7;uint8_t saved=input_report_timer;uint64_t failures_before=adapter_stats.send_failures;
    adapter_test_time+=100;adapter_test_pump();
    CHECK("send_failure_is_visible_and_restores_engine_state",adapter_stats.last_send_status==7 &&
        adapter_stats.send_failures==failures_before+1 && input_report_timer==saved);
    capture.status=0;
    uint8_t mode_request[48]={0}, light_request[48]={0};
    mode_request[9]=0x03;mode_request[10]=0x30;
    light_request[9]=0x30;light_request[10]=1;
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,1,sizeof(mode_request),mode_request);
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,1,sizeof(light_request),light_request);
    adapter_test_pump();
    CHECK("burst_sends_mode_ack_before_the_next_host_command",
        capture.packet[1]==0x21 && capture.packet[15]==3 && adapter_input_mode==PRO1_INPUT_FULL);
    adapter_test_pump();
    CHECK("burst_sends_player_light_ack_second",capture.packet[1]==0x21 && capture.packet[15]==0x30);
    spi_output[10]=0x3d;spi_output[14]=4;
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,1,sizeof(spi_output),spi_output);
    spi_output[10]=0x20;
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,1,sizeof(spi_output),spi_output);
    capture.status=7;adapter_test_pump();
    CHECK("failed_head_spi_reply_keeps_both_commands",command_count==2 && command_loaded);
    capture.status=0;adapter_test_pump();
    CHECK("retried_spi_uses_its_original_address",command_count==1 && little_endian_read_32(capture.packet,16)==0x603d);
    adapter_test_pump();
    CHECK("next_spi_uses_its_own_address",command_count==0 && little_endian_read_32(capture.packet,16)==0x6020);
    uint8_t mcu[48]={0};mcu[9]=0x02;mcu[10]=2;
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,1,sizeof(mode_request),mode_request);
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,0x11,sizeof(mcu),mcu);
    relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,0x10,sizeof(rumble),rumble);
    adapter_test_pump();
    CHECK("nfc_and_rumble_cannot_replace_pending_mode_ack",
        capture.packet[1]==0x21 && capture.packet[15]==3 && command_count==1);
    adapter_test_pump();
    CHECK("queued_mcu_output_still_runs_after_mode_ack",capture.packet[1]==0x31 && command_count==0);
    for (unsigned i=0;i<HOST_COMMAND_CAPACITY+1;i++)
        relay_btkey_handle_report(1,HID_REPORT_TYPE_OUTPUT,1,sizeof(light_request),light_request);
    CHECK("bounded_fifo_counts_overflow_without_replacing_head",
        command_count==HOST_COMMAND_CAPACITY && adapter_stats.command_overflows==1 &&
        adapter_stats.command_queue_peak==HOST_COMMAND_CAPACITY);
    relay_btkey_packet_handler(HCI_EVENT_PACKET,0,closed,sizeof(closed));
    relay_btkey_packet_handler(HCI_EVENT_PACKET,0,opened,sizeof(opened));
    adapter_test_pump();
    CHECK("reconnect_discards_old_queued_replies",!command_count && !command_loaded && capture.packet[1]==0x3f);
    CHECK("can_request_is_bounded_not_recursive_spin",capture.requests<(unsigned)(cases*3));
    printf("],\"cases\":%d,\"failed\":%d,\"reports\":{\"spi603d_hex\":\"",cases,failed);adapter_hex(spi603d,sizeof(spi603d));
    printf("\",\"spi6020_hex\":\"");adapter_hex(spi6020,sizeof(spi6020));
    printf("\",\"previous_input_hex\":\"");adapter_hex(previous,sizeof(previous));
    printf("\",\"latest_input_hex\":\"");adapter_hex(latest,sizeof(latest));
    printf("\"},\"expected_snapshot\":{\"lx\":2048,\"ly\":4095,\"rx\":0,\"ry\":2048,\"accel_x\":4096,\"accel_y\":8192,\"accel_z\":-4096,\"gyro_x\":15335,\"gyro_y\":-7667,\"gyro_z\":0}}\n");
#undef CHECK
    relay_btkey_shutdown();adapter_testing=0;return failed;
}
