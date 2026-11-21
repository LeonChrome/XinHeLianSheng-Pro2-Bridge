/* Compile the pinned upstream source unchanged. Intercept only its HID report
 * boundary so ordinary Pro1 pairing, input timing and security stay upstream. */
#include "relay_nfc.h"
#include "btstack.h"
#include <stdio.h>
#include <string.h>

typedef void (*report_callback_t)(uint16_t, hid_report_type_t, uint16_t, int, uint8_t *);
static void register_relay_report(report_callback_t callback);
static void relay_interrupt(uint16_t cid, const uint8_t *bytes, uint16_t length);
#define hid_device_register_report_data_callback register_relay_report
#define hid_device_send_interrupt_message relay_interrupt
#include "btkeyLib.c"
#undef hid_device_send_interrupt_message
#undef hid_device_register_report_data_callback

enum { TAG_BYTES=540, BLOCK_BYTES=20, BLOCKS=27, WIRE_SIZE=363, MCU_SIZE=313 };
static bool enabled, mode31, powered, configured, scanning, reading, completed, offline;
static uint16_t generation, staged_generation;
static unsigned next_block, signature_parts, observed_parts, pending_ack_count;
static uint8_t staged[TAG_BYTES], staged_signature[32], uid[7];
static uint8_t mcu[MCU_SIZE], pending_acks[8];
static uint8_t captured[WIRE_SIZE], wire[WIRE_SIZE];
static unsigned captured_size, wire_used, sent_events;
static uint16_t wire_generation;
static report_callback_t upstream_report;

static void event(const char *name, unsigned gen, const char *fields){
    if (!offline) relay_nfc_event(name,gen,fields);
}
static void set_status(unsigned state, unsigned detail){
    memset(mcu,0,sizeof(mcu));
    mcu[0]=0x2a; mcu[2]=5; mcu[5]=9; mcu[6]=0x31; mcu[7]=(uint8_t)state; mcu[8]=(uint8_t)detail;
    if (generation && (state==9 || state==4)){
        mcu[11]=1; mcu[12]=1; mcu[13]=2; mcu[15]=7;
        memcpy(mcu+16,uid,7);
    }
    mcu[312]=crc8ccitt(mcu,312);
}
static void set_state(void){
    memset(mcu,0,sizeof(mcu));
    mcu[0]=1; mcu[4]=3; mcu[6]=5; mcu[7]=powered&&configured ? 4 : 1;
    mcu[312]=crc8ccitt(mcu,312);
}
static void invalidate_read(void){
    reading=completed=false; observed_parts=0; wire_used=0;
}
static void reset_stage(void){
    staged_generation=0; next_block=signature_parts=0;
    SecureZeroMemory(staged,sizeof(staged));
    SecureZeroMemory(staged_signature,sizeof(staged_signature));
}
static void remove_tag(void){
    invalidate_read(); generation=0;
    SecureZeroMemory(uid,sizeof(uid));
    SecureZeroMemory(amiibo_block1,sizeof(amiibo_block1));
    SecureZeroMemory(amiibo_block2,sizeof(amiibo_block2));
    SecureZeroMemory(amiibo_block3,sizeof(amiibo_block3));
    SecureZeroMemory(reply110201+NFC_TAG_DETECTED_UID_OFFSET,7);
    crc_block1=crc_block2=crc_block3=crc_tag_detected=0;
    set_status(scanning?1:0,0);
}
void relay_nfc_start(void){
    enabled=true; powered=configured=scanning=mode31=false; pending_ack_count=0;
    reset_stage(); remove_tag(); set_state();
}
void relay_nfc_clear_session(void){
    reset_stage(); remove_tag(); enabled=false;
}
static bool valid_tag(const uint8_t *tag){
    static const uint8_t amiibo_cc[4]={0xf1,0x10,0xff,0xee};
    bool ndef=tag[12]==0xe1 && tag[13]==0x10 && tag[14]==0x3e && (tag[15]==0 || tag[15]==0x0f);
    return tag[0]==4 && tag[3]==(uint8_t)(0x88^tag[0]^tag[1]^tag[2]) &&
        tag[8]==(uint8_t)(tag[4]^tag[5]^tag[6]^tag[7]) && (ndef || !memcmp(tag+12,amiibo_cc,4));
}
static void commit_tag(void){
    invalidate_read();
    generation=staged_generation;
    amiibo_extract_uid(staged,uid);
    amiibo_build_blocks(staged,uid);
    /* Upstream nfc_packet2 contains an unrelated sample signature. Only emit
     * the 32 bytes actually provided by the physical reader or file. */
    memcpy(amiibo_block1+26,staged_signature,32);
    crc_block1=crc8ccitt(amiibo_block1,312);
    crc_block2=crc8ccitt(amiibo_block2,312);
    set_status(scanning?9:0,0);
}
static bool zeros(const uint8_t *bytes,unsigned length){
    for(unsigned i=0;i<length;i++) if(bytes[i]) return false;
    return true;
}
void relay_nfc_record(const uint8_t frame[28]){
    unsigned op=frame[4],index=frame[5]; uint16_t gen=(uint16_t)(frame[6]|(frame[7]<<8));
    if(!enabled || memcmp(frame,"P1NF",4) || !gen) return;
    const uint8_t *data=frame+8;
    bool ok=false;
    if(op==1){
        if(index==0){reset_stage(); staged_generation=gen;}
        if(gen==staged_generation && index==next_block && index<BLOCKS){
            memcpy(staged+index*BLOCK_BYTES,data,BLOCK_BYTES); ++next_block;
        }else reset_stage();
        return;
    }
    if(op==3){
        if(gen==staged_generation && next_block==BLOCKS && index==signature_parts && index<2 &&
            (index==0 || zeros(data+12,8))){
            memcpy(staged_signature+index*BLOCK_BYTES,data,index?12:20); ++signature_parts;
        }else reset_stage();
        return;
    }
    if(index==0 && zeros(data,BLOCK_BYTES)){
        if(op==0){remove_tag(); ok=true;}
        if(op==2 && gen==staged_generation && next_block==BLOCKS && signature_parts!=1 && valid_tag(staged)){
            commit_tag(); ok=true;
        }
    }
    reset_stage();
    char fields[40]; snprintf(fields,sizeof(fields),"present=%u ok=%u",(unsigned)(generation!=0),(unsigned)ok);
    event("NFC_TAG_ACK",gen,fields);
}

/* These read ranges and acknowledgement semantics match WirelessPro1Protocol.
 * The pinned library supplies the two data blocks and the live IMU prefix. */
static bool known_read(const uint8_t *request){
    static const uint8_t ranges[]={1,3,0,0x3b,0x3c,0x77,0x78,0x86,0,0};
    const uint8_t *data=request+5;
    return request[4]==20 && data[0]==0xd0 && data[1]==7 &&
        (zeros(data+2,8) || (!memcmp(data+2,uid,7) && !data[9])) && !memcmp(data+10,ranges,sizeof(ranges));
}
static void block(unsigned index){
    const uint8_t *data=index?amiibo_block2:amiibo_block1;
    memcpy(mcu,data,312); mcu[312]=index?crc_block2:crc_block1;
}
static void handle_mcu(const uint8_t *body, int length){
    if(!mode31 || !powered || length<=OUT_REPORT_SUBCMD) return;
    uint8_t command=body[OUT_REPORT_SUBCMD];
    if(command==1){set_state();return;}
    if(command!=2 || !configured || length<48) return;
    const uint8_t *req=body+10;
    if(req[4]>31 || req[36]!=crc8ccitt(req,36)){set_status(7,0x3e);return;}
    switch(req[0]){
    case 1:
        if(!scanning)event("NFC_HOST_SCAN",generation,"");
        invalidate_read();scanning=true;set_status(generation?9:1,0);break;
    case 2:
        scanning=false;invalidate_read();set_status(0,0);break;
    case 6:
        event("NFC_HOST_READ",generation,"");
        invalidate_read();
        if(!generation || !known_read(req)){set_status(7,0x3e);return;}
        reading=true; block(0);break;
    case 4:
        if(reading && req[2]<2)block(req[2]);
        else if(reading && req[2]>=2)set_status(4,0);
        else set_status(scanning?(generation?9:1):0,0);
        break;
    default:
        invalidate_read();set_status(7,0x3e);
        event("NFC_COMMAND_REJECTED",generation,"reason=read_only");break;
    }
}
static void handle_report(uint16_t cid,hid_report_type_t type,uint16_t id,int length,uint8_t *body){
    bool ours=false;
    if(enabled && type==HID_REPORT_TYPE_OUTPUT && body && length>OUT_REPORT_SUBCMD){
        uint8_t cmd=body[OUT_REPORT_SUBCMD],arg=report_byte(body,length,10);
        if(id==0x11){
            handle_mcu(body,length);
            return; /* never pass writes, raw RF or unsupported MCU commands through */
        }
        if(id==1 && cmd==2){
            /* Device Info (01/02) and MCU commands (11/02) have the same
             * command byte. Device Info padding must never create a tag. */
            report_counter=body[OUT_REPORT_TIMER];
            pairing_state=STATE_REPLY_02;
            if(!offline)hid_device_request_can_send_now_event(cid);
            return;
        }
        if(id==1 && cmd==3 && (arg==0x31 || (mode31 && arg==0x30))){
            mode31=arg==0x31; scanning=false; invalidate_read();set_state();ours=true;
        }else if(id==1 && cmd==0x22 && length>10 && arg<=1){
            powered=arg==1;configured=scanning=false;invalidate_read();set_state();ours=true;
        }else if(id==1 && cmd==0x21 && length>12 && arg==0x21 && body[11]==0 && (body[12]==4 || mode31)){
            configured=body[12]==4;set_state();ours=true;
        }
        if(ours){
            if(pending_ack_count<sizeof(pending_acks))pending_acks[pending_ack_count++]=cmd;
            pairing_state=STATE_INPUT_REPORT;
            return;
        }
    }
    if(upstream_report)upstream_report(cid,type,id,length,body);
}
static void register_relay_report(report_callback_t callback){
    upstream_report=callback; hid_device_register_report_data_callback(handle_report);
}
static void emit_interrupt(uint16_t cid,const uint8_t *bytes,uint16_t len){
    if(offline){captured_size=len;if(len<=sizeof(captured))memcpy(captured,bytes,len);return;}
    hid_device_send_interrupt_message(cid,bytes,len);
}
static void relay_interrupt(uint16_t cid,const uint8_t *bytes,uint16_t len){
    if(enabled && len==WIRE_SIZE && bytes[0]==0xa1 && bytes[1]==0x31){
        /* No fallback path may expose upstream's sample UID/data. All live
         * NFC responses, including "no tag", use the committed snapshot. */
        uint8_t safe[WIRE_SIZE];memcpy(safe,bytes,INPUT_REPORT_SIZE);
        memcpy(safe+INPUT_REPORT_SIZE,mcu,MCU_SIZE);
        emit_interrupt(cid,safe,sizeof(safe));return;
    }
    if(!enabled || len<INPUT_REPORT_SIZE || bytes[0]!=0xa1 || bytes[1]!=0x30){
        emit_interrupt(cid,bytes,len);return;
    }
    if(pending_ack_count){
        uint8_t ack[SUBCOMMAND_REPLY_SIZE]={0};memcpy(ack,bytes,14);
        uint8_t cmd=pending_acks[0];memmove(pending_acks,pending_acks+1,--pending_ack_count);
        ack[1]=0x21;ack[14]=0x80;ack[15]=cmd;
        if(cmd==0x21){
            const uint8_t version[]={1,0,0xff,0,3,0,5,1};
            ack[14]=0xa0;memcpy(ack+16,version,sizeof(version));ack[49]=crc8ccitt(ack+16,33);
        }
        emit_interrupt(cid,ack,sizeof(ack));return;
    }
    if(mode31){
        uint8_t output[WIRE_SIZE];memcpy(output,bytes,INPUT_REPORT_SIZE);
        output[1]=0x31;memcpy(output+INPUT_REPORT_SIZE,mcu,MCU_SIZE);
        emit_interrupt(cid,output,sizeof(output));return;
    }
    emit_interrupt(cid,bytes,len);
}
/* Trace outgoing HCI ACL, including controllers that fragment a full 0x31
 * across multiple USB transfers. Never equate sending with game acceptance. */
void relay_nfc_observe_acl(const uint8_t *p,unsigned size,uint16_t handle,uint16_t cid){
    if(!enabled || !reading || !generation || !cid || size<4 ||
       (little_endian_read_16(p,0)&0xfff)!=handle || little_endian_read_16(p,2)!=size-4)return;
    const uint8_t *part;unsigned length;
    if((p[1]&0x30)==0x10){
        if(!wire_used || wire_generation!=generation)return;
        part=p+4;length=size-4;
    }else{
        wire_used=0;
        if(size<10 || little_endian_read_16(p,4)!=WIRE_SIZE || little_endian_read_16(p,6)!=cid ||
           p[8]!=0xa1 || p[9]!=0x31)return;
        part=p+8;length=size-8;wire_generation=generation;
    }
    if(length>WIRE_SIZE-wire_used){wire_used=0;return;}
    memcpy(wire+wire_used,part,length);wire_used+=length;
    if(wire_used!=WIRE_SIZE)return;
    wire_used=0;
    const uint8_t *payload=wire+INPUT_REPORT_SIZE;
    if(payload[312]!=crc8ccitt(payload,312))return;
    if(!memcmp(payload,amiibo_block1,312)){observed_parts|=1;if(!completed && mcu[0]==0x3a && mcu[3]==1)block(1);}
    else if(!memcmp(payload,amiibo_block2,312)){observed_parts|=2;if(!completed && mcu[0]==0x3a && mcu[3]==2)set_status(4,0);}
    else if(payload[0]==0x2a && payload[7]==4 && !memcmp(payload+16,uid,7) && observed_parts==3 && !completed){
        completed=true;++sent_events;
        event("NFC_TAG_DATA_SENT",generation,"bytes=540 sections=2 scope=outgoing_acl");
        set_status(9,0);
    }
}

/* Offline transport fixture. Capture the report produced by the same upstream
 * CAN_SEND handler as live play, then pass first and continuation HCI packets
 * through the real transport observer. Only generated data is logged. */
static bool fixture_send(void){
    captured_size=0;handle_can_send_now();return captured_size!=0;
}
static bool fixture_ack(uint8_t command,uint8_t arg){
    uint8_t body[48]={0};body[9]=command;body[10]=arg;
    if(command==0x21){body[11]=0;body[12]=4;}
    handle_report(1,HID_REPORT_TYPE_OUTPUT,1,sizeof(body),body);
    return fixture_send() && captured_size==64 && captured[1]==0x21 && captured[15]==command &&
        (command!=0x21 || (captured[14]==0xa0 && captured[49]==crc8ccitt(captured+16,33)));
}
static void fixture_mcu(unsigned op,unsigned packet,bool corrupt){
    uint8_t body[48]={0},*req=body+10;
    body[9]=2;req[0]=(uint8_t)op;req[2]=(uint8_t)packet;req[3]=8;
    if(op==6){
        static const uint8_t ranges[]={1,3,0,0x3b,0x3c,0x77,0x78,0x86,0,0};
        req[4]=20;req[5]=0xd0;req[6]=7;memcpy(req+7,uid,7);memcpy(req+15,ranges,sizeof(ranges));
    }
    req[36]=crc8ccitt(req,36)^(corrupt?1:0);
    handle_report(1,HID_REPORT_TYPE_OUTPUT,0x11,sizeof(body),body);
}
static bool fixture_wire(void){
    if(captured_size!=WIRE_SIZE || captured[1]!=0x31 || captured[362]!=crc8ccitt(captured+50,312))return false;
    uint8_t first[8+100]={1,0x20,104,0,0x6b,1,0x56,0};
    uint8_t rest[4+WIRE_SIZE-100]={1,0x10,7,1};
    memcpy(first+8,captured,100);memcpy(rest+4,captured+100,WIRE_SIZE-100);
    unsigned before=sent_events;
    relay_nfc_observe_acl(first,sizeof(first),1,0x56);
    if(sent_events!=before)return false;
    relay_nfc_observe_acl(rest,sizeof(rest),1,0x56);return true;
}
static bool fixture_roundtrip(const uint8_t records[][28],unsigned count){
    if(count!=28 && count!=30)return false;
    uint8_t raw[TAG_BYTES],signature[32]={0},reconstructed[TAG_BYTES];
    uint16_t gen=(uint16_t)(records[0][6]|(records[0][7]<<8));if(!gen)return false;
    for(unsigned i=0;i<count;i++){
        const uint8_t *f=records[i];unsigned op=i<BLOCKS?1:i==count-1?2:3;
        unsigned index=op==1?i:op==3?i-BLOCKS:0;
        if(memcmp(f,"P1NF",4)||f[4]!=op||f[5]!=index||f[6]!=(gen&255)||f[7]!=(gen>>8))return false;
        if(op==1)memcpy(raw+index*20,f+8,20);
        if(op==3)memcpy(signature+index*20,f+8,index?12:20);
        relay_nfc_record(f);
        if(i+1<count && generation)return false;
    }
    if(generation!=gen || !fixture_ack(3,0x31) || !fixture_ack(0x22,1) || !fixture_ack(0x21,0x21))return false;
    uint8_t expected_uid[7]={raw[0],raw[1],raw[2],raw[4],raw[5],raw[6],raw[7]};
    fixture_mcu(1,0,false);
    if(!fixture_send() || captured[50]!=0x2a || captured[57]!=9 || memcmp(captured+66,expected_uid,7))return false;
    fixture_mcu(6,0,true);
    if(!fixture_send() || captured[57]!=7 || captured[58]!=0x3e)return false;
    fixture_mcu(6,0,false);
    if(!fixture_send() || captured[50]!=0x3a || captured[53]!=1 || memcmp(captured+65,expected_uid,7) ||
       memcmp(captured+76,signature,32))return false;
    memcpy(reconstructed,captured+117,245);
    if(!fixture_wire())return false;
    fixture_mcu(4,0,false); /* A repeated ACK asks for the same unmodified data. */
    if(!fixture_send() || captured[53]!=1 || memcmp(reconstructed,captured+117,245) || !fixture_wire())return false;
    if(!fixture_send() || captured[50]!=0x3a || captured[53]!=2)return false;
    memcpy(reconstructed+245,captured+57,295);
    if(memcmp(raw,reconstructed,540) || !fixture_wire())return false;
    unsigned before=sent_events;
    if(!fixture_send() || captured[50]!=0x2a || captured[57]!=4 || !fixture_wire() || sent_events!=before+1)return false;
    fixture_mcu(8,0,false); /* unsupported write command */
    if(!fixture_send() || captured[57]!=7 || captured[58]!=0x3e)return false;
    fixture_mcu(6,0,false);
    if(!fixture_send() || captured[53]!=1 || memcmp(captured+117,raw,245))return false;
    uint8_t clear[28]={'P','1','N','F',0,0,1};
    relay_nfc_record(clear);
    if(generation || reading || !fixture_send() || captured[50]!=0x2a || captured[57]!=1)return false;
    fixture_mcu(6,0,false);
    if(!fixture_send() || captured[57]!=7 || captured[58]!=0x3e)return false;
    /* Missing final data block, failed BCC and invalid capability container
     * never expose partially updated or zero-filled tags. */
    for(unsigned i=0;i<26;i++)relay_nfc_record(records[i]);
    relay_nfc_record(records[count-1]);
    if(generation)return false;
    for(unsigned i=0;i<BLOCKS;i++){
        uint8_t bad[28];memcpy(bad,records[i],28);if(!i)bad[11]^=1;relay_nfc_record(bad);
    }
    relay_nfc_record(records[count-1]);
    if(generation || !fixture_ack(3,0x30) || !fixture_send() || captured[1]!=0x30)return false;
    return true;
}
bool relay_nfc_fixture(const wchar_t *path){
    uint8_t records[30][28]={{0}};unsigned count=0;
    if(path){
        FILE *f=_wfopen(path,L"rb");if(!f)return false;
        size_t n=fread(records,1,sizeof(records),f);
        bool invalid=ferror(f)||n%28||fgetc(f)!=EOF;
        fclose(f);if(invalid)return false;count=(unsigned)(n/28);
    }else{
        uint8_t dump[540];for(unsigned i=0;i<540;i++)dump[i]=(uint8_t)(i*17+11);
        dump[0]=4;dump[3]=(uint8_t)(0x88^dump[0]^dump[1]^dump[2]);dump[8]=dump[4]^dump[5]^dump[6]^dump[7];
        memcpy(dump+12,"\xf1\x10\xff\xee",4);
        count=30;
        for(unsigned i=0;i<count;i++){
            uint8_t *f=records[i];memcpy(f,"P1NF",4);f[6]=7;
            if(i<27){f[4]=1;f[5]=(uint8_t)i;memcpy(f+8,dump+20*i,20);}
            else if(i<29){f[4]=3;f[5]=(uint8_t)(i-27);memset(f+8,(int)i,i==27?20:12);}
            else f[4]=2;
        }
    }
    offline=true;relay_nfc_start();pairing_state=STATE_INPUT_REPORT;
    bool ok=fixture_roundtrip(records,count);
    relay_nfc_clear_session();offline=false;SecureZeroMemory(records,sizeof(records));return ok;
}
