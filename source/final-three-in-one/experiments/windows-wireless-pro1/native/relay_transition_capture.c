/* Short H4 BTSnoop windows around a Switch Pro Controller's exit from Sniff. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include "relay_transition_capture.h"

enum { RING_SIZE=2048, PREFIX_SIZE=64, MAX_WINDOWS=4, MAX_PATH_CHARS=32768 };
typedef struct {
    uint64_t timestamp;
    uint32_t original;
    uint8_t flags, size, h4[PREFIX_SIZE];
} capture_record_t;
typedef struct {
    unsigned index, count, dropped;
    wchar_t *path;
    capture_record_t records[];
} capture_job_t;
typedef struct { uint16_t initiator, destination; uint8_t request_id; int from_host; } hid_channel_t;

static capture_record_t ring[RING_SIZE];
static hid_channel_t channels[2]; /* HID control and HID interrupt. */
static wchar_t *directory;
static HANDLE writers[MAX_WINDOWS];
static unsigned first, count, dropped, windows, errors;
static uint64_t utc_base, trigger;
static double mono_base;
static uint16_t link_handle;
static int link_live, sniff;

static uint16_t le16(const uint8_t *p){ return (uint16_t)(p[0]|p[1]<<8); }
static void be32(uint8_t *p,uint32_t n){p[0]=(uint8_t)(n>>24);p[1]=(uint8_t)(n>>16);p[2]=(uint8_t)(n>>8);p[3]=(uint8_t)n;}
static const uint64_t second=1000000;
static uint64_t capture_time(double stamp){
    return utc_base+(uint64_t)((stamp-mono_base)*1000000.0+0.5);
}
static int write_bytes(FILE *out,const void *p,size_t n){return fwrite(p,1,n,out)==n;}

static DWORD WINAPI write_window(void *arg){
    capture_job_t *job=arg;
    size_t path_size=wcslen(job->path)+10;
    wchar_t *partial=calloc(path_size,sizeof(*partial));
    FILE *out=NULL;
    int ok=0, created=0;
    if(partial){
        swprintf(partial,path_size,L"%ls.partial",job->path);
        if(GetFileAttributesW(job->path)==INVALID_FILE_ATTRIBUTES)out=_wfopen(partial,L"wbx");
    }
    if(out){
        created=1;
        const uint8_t header[]={'b','t','s','n','o','o','p',0,0,0,0,1,0,0,3,0xea};
        ok=write_bytes(out,header,sizeof(header));
        for(unsigned i=0;ok&&i<job->count;i++){
            const capture_record_t *r=&job->records[i];
            uint8_t row[24];
            be32(row,r->original);be32(row+4,r->size);be32(row+8,r->flags);
            be32(row+12,job->dropped);be32(row+16,(uint32_t)(r->timestamp>>32));be32(row+20,(uint32_t)r->timestamp);
            ok=write_bytes(out,row,sizeof(row))&&write_bytes(out,r->h4,r->size);
        }
        if(fclose(out)!=0)ok=0;
        if(ok)ok=MoveFileW(partial,job->path)!=0;
    }
    if(created&&!ok)DeleteFileW(partial);
    printf("{\"event\":\"PRO1_TRANSITION_CAPTURE\",\"index\":%u,\"records\":%u,\"ring_overflow\":%u,\"saved\":%s,\"filtered\":true}\n",job->index,job->count,job->dropped,ok?"true":"false");
    free(job->path);free(partial);free(job);
    return ok?0:1;
}
static void expire_before(uint64_t start){
    while(count&&ring[first].timestamp<start){first=(first+1)%RING_SIZE;count--;}
}
static void close_window(void){
    if(!trigger)return;
    unsigned index=windows++;
    size_t path_size=wcslen(directory)+32;
    capture_job_t *job=malloc(sizeof(*job)+(size_t)count*sizeof(*ring));
    wchar_t *path=calloc(path_size,sizeof(*path));
    if(job&&path){
        swprintf(path,path_size,L"%lsswitch-active-%02u.btsnoop",directory,index+1);
        job->index=index+1;job->count=count;job->dropped=dropped;job->path=path;
        for(unsigned i=0;i<count;i++)job->records[i]=ring[(first+i)%RING_SIZE];
        writers[index]=CreateThread(NULL,0,write_window,job,0,NULL);
    }
    if(!writers[index]){
        errors++;free(path);free(job);
        puts("{\"event\":\"PRO1_TRANSITION_CAPTURE\",\"saved\":false,\"reason\":\"writer_unavailable\"}");
    }
    count=first=dropped=0;trigger=0;
}
void relay_capture_init(const char *path,double stamp){
    if(!path||directory)return;
    int chars=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,path,-1,NULL,0);
    if(!chars||chars>MAX_PATH_CHARS)return;
    directory=calloc((size_t)chars,sizeof(*directory));
    if(!directory)return;
    if(!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,path,-1,directory,chars)){free(directory);directory=NULL;return;}
    size_t len=wcslen(directory);
    while(len&&directory[len-1]!=L'/'&&directory[len-1]!=L'\\')len--;
    directory[len]=0;
    FILETIME ft;GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER windows_time;windows_time.LowPart=ft.dwLowDateTime;windows_time.HighPart=ft.dwHighDateTime;
    utc_base=windows_time.QuadPart/10-11644473600000000ULL+0x00dcddb30f2f8000ULL;
    mono_base=stamp;trigger=0;windows=errors=count=first=dropped=0;link_live=sniff=0;
}
void relay_capture_new_link(uint16_t handle){
    if(!directory)return;
    if(trigger)close_window();
    first=count=dropped=0;memset(channels,0,sizeof(channels));
    link_handle=handle&0xfff;link_live=1;sniff=0;
}
void relay_capture_poll(double stamp){
    if(directory&&trigger&&capture_time(stamp)>=trigger+second)close_window();
}
void relay_capture_finish(void){
    if(!directory)return;
    close_window();
    for(unsigned i=0;i<windows;i++)if(writers[i]){
        WaitForSingleObject(writers[i],INFINITE);
        DWORD result=1;if(!GetExitCodeThread(writers[i],&result)||result)errors++;
        CloseHandle(writers[i]);writers[i]=NULL;
    }
    free(directory);directory=NULL;link_live=sniff=0;
}

/* Only explicit, public packet layouts reach the ring. Unknown ACL bodies are
 * represented by their headers. Sensitive HCI events and commands are omitted. */
static unsigned public_command_size(uint16_t opcode){
    switch(opcode){
        case 0x0406: case 0x0c2d:return 6; /* handle and disconnect reason / power type */
        case 0x0803:return 13; /* Sniff Mode: handle and four timing parameters */
        case 0x0804: case 0x0c36: case 0x1408:return 5;
        case 0x080d: case 0x0c37:return 7;
        case 0x0811:return 11;
        default:return 0;
    }
}
static unsigned event_prefix(const uint8_t *p,unsigned n){
    if(n<2||n!=2u+p[1])return 0;
    switch(p[0]){
        case 0x05: case 0x08: case 0x0c: case 0x0d: case 0x14:
        case 0x1c: case 0x1d: case 0x21: case 0x2e:{
            unsigned fixed=p[0]==0x0c?10:p[0]==0x0d?23:p[0]==0x14?8:p[0]==0x21?24:p[0]==0x2e?13:
                (p[0]==0x1c||p[0]==0x1d)?7:6;
            return n==fixed&&(le16(p+3)&0xfff)==link_handle?n:0;
        }
        case 0x11: case 0x1b: case 0x1e: case 0x38: case 0x39:{
            unsigned fixed=p[0]==0x1b?5:p[0]==0x38?6:4;
            return n==fixed&&(le16(p+2)&0xfff)==link_handle?n:0;
        }
        case 0x10: case 0x1a:return n==3?n:0;
        case 0x12: /* Role Change includes an address which is zeroed in the ring. */
            return n==10?n:0;
        case 0x13: /* Credits can describe several handles. Retain this link only. */
            return n==7&&p[2]==1&&(le16(p+3)&0xfff)==link_handle?n:0;
        case 0x0e:return n>=6&&public_command_size(le16(p+3))?6:0;
        case 0x0f:return n==6&&public_command_size(le16(p+4))?6:0;
        default:return 0;
    }
}
static unsigned signal_prefix(const uint8_t *p,unsigned n,int incoming){
    if(n<12)return 8;
    unsigned payload=le16(p+10);
    if(payload>n-12)return 12;
    uint8_t code=p[8],id=p[9];
    const uint8_t *s=p+12;
    if(code==2&&payload==4){
        uint16_t psm=le16(s);
        if(psm==0x11||psm==0x13){
            hid_channel_t *ch=&channels[(psm-0x11)/2];
            *ch=(hid_channel_t){le16(s+2),0,id,!incoming};
        }
        return 16;
    }
    if(code==3&&payload==8){
        for(unsigned i=0;i<2;i++){
            hid_channel_t *ch=&channels[i];
            if(ch->request_id==id&&ch->from_host==incoming&&ch->initiator==le16(s+2)&&le16(s+4)==0)
                ch->destination=le16(s);
        }
        return 20;
    }
    if(code==7&&payload==4)return 16;
    if(code==6&&payload==4){
        for(unsigned i=0;i<2;i++){
            hid_channel_t *ch=&channels[i];
            int targets_initiator=incoming==ch->from_host;
            uint16_t dcid=targets_initiator?ch->initiator:ch->destination;
            uint16_t scid=targets_initiator?ch->destination:ch->initiator;
            if(ch->destination&&le16(s)==dcid&&le16(s+2)==scid)memset(ch,0,sizeof(*ch));
        }
        return 16;
    }
    if(code==1&&payload>=2)return 14;
    if(code==10&&payload==2)return 14;
    if(code==11&&payload>=4)return 16;
    if(code==4||code==5){
        unsigned fixed=code==4?4:6;
        if(payload<fixed)return 12;
        /* MTU, flush timeout and retransmission options have fixed public formats. */
        unsigned pos=fixed;
        while(pos+2<=payload){
            uint8_t type=s[pos]&0x7f,len=s[pos+1];
            if(pos+2u+len>payload||!((type==1&&len==2)||(type==2&&len==2)||(type==4&&len==9)||(type==5&&len==1)))break;
            pos+=2+len;
        }
        return 12+pos;
    }
    return 12;
}
static unsigned acl_prefix(const uint8_t *p,unsigned n,int incoming){
    if(n<4||(le16(p)&0xfff)!=link_handle||le16(p+2)!=n-4)return 0;
    unsigned boundary=(p[1]>>4)&3;
    if(boundary==1||n<8)return 4;
    uint16_t cid=le16(p+6),l2_length=le16(p+4);
    if(l2_length<n-8)return 8;
    if(cid==1)return signal_prefix(p,n,incoming);
    if(cid<0x40)return 8;
    for(unsigned i=0;i<2;i++){
        const hid_channel_t *ch=&channels[i];
        if(!ch->destination||cid!=(incoming==ch->from_host?ch->initiator:ch->destination))continue;
        if(n<9)return 8;
        uint8_t hidp=p[8];
        if(i==0){ /* HID control handshake, GET/SET_REPORT, protocol or virtual cable. */
            unsigned transaction=hidp>>4;
            return (transaction==4||transaction==5||transaction==10)&&n>=10?10:9;
        }
        if(n<10)return 9;
        if(hidp==0xa1&&!incoming){
            uint8_t report=p[9];
            if((report==0x30&&l2_length==50)||(report==0x3f&&l2_length==13))return n;
            if(report==0x21&&l2_length==50&&n>=24)return 24; /* through ACK and subcommand, never reply data */
        }
        if(hidp==0xa2&&incoming){
            if(p[9]==0x10&&n>=19)return 19; /* timer and eight rumble bytes */
            if(p[9]==1&&n>=20){
                /* Keep the requested mode or lights. Pairing, flash and NFC command arguments stay private. */
                if((p[19]==3||p[19]==0x30||p[19]==0x40||p[19]==0x48)&&n>=21)return 21;
                return 20;
            }
        }
        return 10;
    }
    return 8;
}
void relay_capture_packet(uint8_t type,int incoming,const uint8_t *p,unsigned n,double stamp){
    if(!directory||!link_live||windows==MAX_WINDOWS||!p||!n)return;
    uint64_t when=capture_time(stamp);
    if(trigger&&when>trigger+second)return; /* the watchdog schedules disk work */
    if(!trigger)expire_before(when-second);
    unsigned keep=0;
    if(type==4&&incoming)keep=event_prefix(p,n);
    else if(type==2)keep=acl_prefix(p,n,incoming);
    else if(type==1&&!incoming&&n>=5&&n==3u+p[2]&&n==public_command_size(le16(p))&&(le16(p+3)&0xfff)==link_handle)keep=n;
    if(!keep)return;
    if(keep>=PREFIX_SIZE)keep=PREFIX_SIZE-1;
    if(count==RING_SIZE){first=(first+1)%RING_SIZE;count--;dropped++;}
    capture_record_t *r=&ring[(first+count++)%RING_SIZE];
    r->timestamp=when;r->original=n+1;r->flags=(uint8_t)((incoming?1:0)|(type==2?0:2));r->size=(uint8_t)(keep+1);
    r->h4[0]=type;memcpy(r->h4+1,p,keep);
    if(type==4&&p[0]==0x12)memset(r->h4+4,0,6);
    if(type==4&&p[0]==0x14&&n==8&&p[2]==0){
        if(sniff&&p[5]==0&&!trigger)trigger=when;
        sniff=p[5]==2;
    }
    if(type==4&&p[0]==5&&n==6&&p[2]==0){sniff=0;link_live=0;}
}

/* This fixture runs through the same transport boundary as the real controller.
 * The marker fills fields that must never appear in the file. */
int relay_capture_fixture(const char *path){
    relay_capture_init(path,0.0);
    if(!directory)return 1;
    relay_capture_new_link(0x42);
    uint8_t mode[]={0x14,6,0,0x42,0,2,0x20,0};
    uint8_t request[]={0x42,0x20,12,0,8,0,1,0,2,5,4,0,0x13,0,0x60,0};
    uint8_t response[]={0x42,0x20,16,0,12,0,1,0,3,5,8,0,0x61,0,0x60,0,0,0,0,0};
    uint8_t frame[58]={0x42,0x20,54,0,50,0,0x60,0,0xa1,0x30};
    uint8_t key[25]={0x18,23};memset(key+2,0xab,sizeof(key)-2);
    uint8_t pin[26]={0x0d,4,23};memset(pin+3,0xab,sizeof(pin)-3);
    uint8_t slots[]={0x1b,3,0x42,0,5};
    uint8_t role[]={0x12,8,0,0xab,0xab,0xab,0xab,0xab,0xab,1};
    uint8_t local_disconnect[]={0x06,0x04,3,0x42,0,0x13};
    uint8_t peer_close[]={0x42,0x20,12,0,8,0,1,0,6,7,4,0,0x61,0,0x60,0};
    relay_capture_packet(4,1,mode,sizeof(mode),.1);
    relay_capture_packet(2,1,request,sizeof(request),1.1);
    relay_capture_packet(2,0,response,sizeof(response),1.2);
    relay_capture_packet(2,0,frame,sizeof(frame),1.3);
    mode[5]=0;relay_capture_packet(4,1,mode,sizeof(mode),2.0);
    frame[9]=0x21;frame[22]=0x90;frame[23]=0x10;memset(frame+24,0xab,34);
    relay_capture_packet(2,0,frame,sizeof(frame),2.1);
    frame[9]=0x31;memset(frame+10,0xab,48);
    relay_capture_packet(2,0,frame,sizeof(frame),2.2);
    frame[6]=0x70;memset(frame+8,0xab,50);
    relay_capture_packet(2,1,frame,sizeof(frame),2.3);
    frame[1]=0x10;memset(frame+4,0xab,54);
    relay_capture_packet(2,0,frame,sizeof(frame),2.4);
    relay_capture_packet(4,1,key,sizeof(key),2.5);
    relay_capture_packet(1,0,pin,sizeof(pin),2.6);
    relay_capture_packet(4,1,slots,sizeof(slots),2.7);
    uint8_t command[58]={0x42,0x20,54,0,50,0,0x61,0,0xa2,1};
    command[19]=3;command[20]=0x30;memset(command+21,0xab,sizeof(command)-21);
    relay_capture_packet(2,1,command,sizeof(command),2.75);
    relay_capture_packet(4,1,role,sizeof(role),2.8);
    relay_capture_packet(2,1,peer_close,sizeof(peer_close),2.85);
    relay_capture_packet(1,0,local_disconnect,sizeof(local_disconnect),2.9);
    relay_capture_packet(4,1,slots,sizeof(slots),3.1);
    relay_capture_poll(3.2);
    relay_capture_finish();
    return windows==1&&!errors?0:1;
}
